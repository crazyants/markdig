﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Textamina.Markdig.Helpers;
using Textamina.Markdig.Syntax;

namespace Textamina.Markdig.Parsing
{
    public class MarkdownParser
    {
        public static TextWriter Log;

        private StringLine line;

        private readonly List<BlockParser> blockParsers;
        private readonly List<InlineParser> inlineParsers;
        private readonly List<InlineParser> regularInlineParsers;
        private readonly InlineParser[] inlineWithFirstCharParsers;
        private readonly Document document;
        private readonly BlockParserState blockParserState;
        private readonly StringBuilderCache stringBuilderCache;

        public MarkdownParser(TextReader reader)
        {
            document = new Document();
            Reader = reader;
            blockParsers = new List<BlockParser>();
            inlineParsers = new List<InlineParser>();
            inlineWithFirstCharParsers = new InlineParser[128];
            regularInlineParsers = new List<InlineParser>();
            stringBuilderCache = new StringBuilderCache();
            blockParserState = new BlockParserState(stringBuilderCache, document);
            blockParsers = new List<BlockParser>()
            {
                BreakBlock.Parser,
                HeadingBlock.Parser,
                QuoteBlock.Parser,
                ListBlock.Parser,

                HtmlBlock.Parser,
                CodeBlock.Parser, 
                FencedCodeBlock.Parser,
                ParagraphBlock.Parser,
            };

            inlineParsers = new List<InlineParser>()
            {
                LinkInline.Parser,
                EmphasisInline.Parser,
                EscapeInline.Parser,
                CodeInline.Parser,
                AutolinkInline.Parser,
                HardlineBreakInline.Parser,
                LiteralInline.Parser,
            };
            InitializeInlineParsers();
        }

        private void InitializeInlineParsers()
        {
            foreach (var inlineParser in inlineParsers)
            {
                if (inlineParser.FirstChars != null && inlineParser.FirstChars.Length > 0)
                {
                    foreach (var firstChar in inlineParser.FirstChars)
                    {
                        if (firstChar >= 128)
                        {
                            throw new InvalidOperationException($"Invalid character '{firstChar}'. Support only ASCII < 128 chars");
                        }
                        inlineWithFirstCharParsers[firstChar] = inlineParser;
                    }
                }
                else
                {
                    regularInlineParsers.Add(inlineParser);
                }
            }
        }

        public TextReader Reader { get; }

        private Block LastBlock
        {
            get
            {
                var count = blockParserState.Count;
                return count > 0 ? blockParserState[count - 1] : null;
            }
        }

        private ContainerBlock LastContainer
        {
            get
            {
                for (int i = blockParserState.Count - 1; i >= 0; i--)
                {
                    var container = blockParserState[i] as ContainerBlock;
                    if (container != null)
                    {
                        return container;
                    }
                }
                return null;
            }
        }

        public Document Parse()
        {
            ParseLines();
            ProcessInlines(document);
            return document;
        }

        private void ParseLines()
        {
            int lineIndex = 0;
            while (true)
            {
                var lineText = Reader.ReadLine();

                // If this is the end of file and the last line is empty
                if (lineText == null)
                {
                    break;
                }
                line = new StringLine(lineText, lineIndex++);

                bool continueProcessLiner = ProcessPendingBlocks();

                // If we have already reached eol and the last block was a paragraph
                // we close it
                if (line.IsEol)
                {
                    int index = blockParserState.Count - 1;
                    if (blockParserState[index] is ParagraphBlock)
                    {
                        blockParserState.Close(index);
                        continue;
                    }
                }

                // If the line was not entirely processed by pending blocks, try to process it with any new block
                while (continueProcessLiner)
                {
                    ParseNewBlocks(ref continueProcessLiner);
                }

                // Close blocks that are no longer opened
                blockParserState.CloseAll(false);
            }

            blockParserState.CloseAll(true);
            // Close opened blocks
            //ProcessPendingBlocks(true);
        }

        private void ProcessInlines(ContainerBlock container)
        {
            var list = new Stack<ContainerBlock>();
            list.Push(container);
            var leafs = new List<Task>();

            while (list.Count > 0)
            {
                container = list.Pop();
                foreach (var block in container.Children)
                {
                    var leafBlock = block as LeafBlock;
                    if (leafBlock != null)
                    {
                        if (!leafBlock.NoInline)
                        {
                            var task = new Task(() => ProcessInlineLeaf(leafBlock));
                            task.Start();
                            leafs.Add(task);
                            //ProcessInlineLeaf(leafBlock);
                        }
                    }
                    else 
                    {
                        list.Push((ContainerBlock)block);
                    }
                }
            }

            Task.WaitAll(leafs.ToArray());
        }

        private bool ProcessPendingBlocks()
        {
            bool processLiner = true;

            // Set all blocks non opened. 
            // They will be marked as open in the following loop
            for (int i = 1; i < blockParserState.Count; i++)
            {
                blockParserState[i].IsOpen = false;
            }

            // Create the line state that will be used by all parser
            blockParserState.Reset(line);

            // Process any current block potentially opened
            for (int i = 1; i < blockParserState.Count; i++)
            {
                var block = blockParserState[i];

                // Else tries to match the Parser with the current line
                var parser = block.Parser;
                blockParserState.Pending = block;

                // If we have a paragraph block, we want to try to match over blocks before trying the Paragraph
                if (blockParserState.Pending is ParagraphBlock)
                {
                    break;
                }

                var saveLiner = line.Save();

                // If we have a discard, we can remove it from the current state
                blockParserState.CurrentContainer = LastContainer;
                blockParserState.PendingIndex = i;
                blockParserState.LastBlock = LastBlock;
                var result = parser.Match(blockParserState);
                if (result == MatchLineResult.Skip)
                {
                    continue;
                }

                if (result == MatchLineResult.None)
                {
                    // Restore the Line where it was
                    line.Restore(ref saveLiner);
                    break;
                }

                // In case the BlockParser has modified the blockParserState we are iterating on
                if (i >= blockParserState.Count)
                {
                    i = blockParserState.Count - 1;
                }

                // If a parser is adding a block, it must be the last of the list
                if ((i + 1) < blockParserState.Count && blockParserState.NewBlocks.Count > 0)
                {
                    throw new InvalidOperationException("A pending parser cannot add a new block when it is not the last pending block");
                }

                // If we have a leaf block
                var leaf = blockParserState.Pending as LeafBlock;
                if (leaf != null && blockParserState.NewBlocks.Count == 0)
                {
                    processLiner = false;
                    if (result != MatchLineResult.LastDiscard && result != MatchLineResult.ContinueDiscard)
                    {
                        leaf.Append(line);
                    }

                    if (blockParserState.NewBlocks.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "The NewBlocks is not empty. This is happening if a LeafBlock is not the last to be pushed");
                    }
                }

                // A block is open only if it has a Continue state.
                // otherwise it is a Last state, and we don't keep it opened
                block.IsOpen = result == MatchLineResult.Continue || result == MatchLineResult.ContinueDiscard;

                if (result == MatchLineResult.LastDiscard)
                {
                    processLiner = false;
                    break;
                }

                bool isLast = i == blockParserState.Count - 1;
                if (processLiner)
                {
                    processLiner = ProcessNewBlocks(result, false);
                }
                if (isLast || !processLiner)
                {
                    break;
                }
            }

            return processLiner;
        }

        private void ParseNewBlocks(ref bool continueProcessLiner)
        {
            blockParserState.Reset(line);

            for (int j = 0; j < blockParsers.Count; j++)
            {
                var blockParser = blockParsers[j];
                if (line.IsEol)
                {
                    continueProcessLiner = false;
                    break;
                }

                // If a block parser cannot interrupt a paragraph, and the last block is a paragraph
                // we can skip this parser
                var lastBlock = LastBlock;
                var paragraph = lastBlock as ParagraphBlock;
                if (paragraph != null && !blockParser.CanInterruptParagraph)
                {
                    continue;
                }

                bool isParsingParagraph = blockParser == ParagraphBlock.Parser;
                blockParserState.Pending = isParsingParagraph ? paragraph : null;
                blockParserState.CurrentContainer = LastContainer;
                blockParserState.LastBlock = lastBlock;

                var saveLiner = line.Save();
                var result = blockParser.Match(blockParserState);
                if (result == MatchLineResult.None)
                {
                    // If we have reached a blank line after trying to parse a paragraph
                    // we can ignore it
                    if (isParsingParagraph && line.IsBlankLine())
                    {
                        continueProcessLiner = false;
                        break;
                    }

                    line.Restore(ref saveLiner);
                    continue;
                }

                // Special case for paragraph
                paragraph = LastBlock as ParagraphBlock;
                if (isParsingParagraph && paragraph != null)
                {
                    Debug.Assert(blockParserState.NewBlocks.Count == 0);

                    continueProcessLiner = false;
                    paragraph.Append(line);

                    // We have just found a lazy continuation for a paragraph, early exit
                    // Mark all block opened after a lazy continuation
                    for (int i = 0; i < blockParserState.Count; i++)
                    {
                        blockParserState[i].IsOpen = true;
                    }
                    break;
                }

                // Nothing found but the BlockParser may instruct to break, so early exit
                if (blockParserState.NewBlocks.Count == 0 && result == MatchLineResult.LastDiscard)
                {
                    continueProcessLiner = false;
                    break;
                }

                continueProcessLiner = ProcessNewBlocks(result, true);

                // If we have a container, we can retry to match against all types of block.
                if (continueProcessLiner)
                {
                    // rewind to the first parser
                    j = -1;
                }
                else
                {
                    // We have a leaf node, we can stop
                    break;
                }
            }
        }

        private bool ProcessNewBlocks(MatchLineResult result, bool allowClosing)
        {
            var newBlocks = blockParserState.NewBlocks;
            while (newBlocks.Count > 0)
            {
                var block = newBlocks.Pop();

                block.Line = line.LineIndex;

                // If we have a leaf block
                var leaf = block as LeafBlock;
                if (leaf != null)
                {
                    if (result != MatchLineResult.LastDiscard && result != MatchLineResult.ContinueDiscard)
                    {
                        leaf.Append(line);
                    }

                    if (newBlocks.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "The NewBlocks is not empty. This is happening if a LeafBlock is not the last to be pushed");
                    }
                }

                if (allowClosing)
                {
                    // Close any previous blocks not opened
                    blockParserState.CloseAll(false);
                }

                // If previous block is a container, add the new block as a children of the previous block
                if (block.Parent == null)
                {
                    var container = LastContainer;
                    LastContainer.Children.Add(block);
                    block.Parent = container;
                }

                block.IsOpen = result == MatchLineResult.Continue || result == MatchLineResult.ContinueDiscard;

                // Add a block blockParserState to the stack (and leave it opened)
                blockParserState.Add(block);

                if (leaf != null)
                {
                    return false;
                }
            }
            return true;
        }

        private void ProcessInlineLeaf(LeafBlock leafBlock)
        {
            var lines = leafBlock.Lines;

            leafBlock.Inline = new ContainerInline() {IsClosed = false};
            var inlineState = new InlineParserState(stringBuilderCache, document);

            inlineState.Lines = lines;
            inlineState.Inline = leafBlock.Inline;
            inlineState.Block = leafBlock;

            var saveLines = new StringLineGroup.State();

            while (!lines.IsEndOfLines)
            {
                lines.Save(ref saveLines);

                var c = lines.CurrentChar;
                var inlineParser = c < 128 ? inlineWithFirstCharParsers[c] : null;
                if (inlineParser == null || !inlineParser.Match(inlineState))
                {
                    for (int i = 0; i < regularInlineParsers.Count; i++)
                    {
                        lines.Restore(ref saveLines);

                        inlineParser = regularInlineParsers[i];

                        if (inlineParser.Match(inlineState))
                        {
                            break;
                        }

                        inlineParser = null;
                    }

                    if (inlineParser == null)
                    {
                        lines.Restore(ref saveLines);
                    }
                }

                var nextInline = inlineState.Inline;

                if (nextInline != null)
                {
                    if (nextInline.Parent == null)
                    {
                        // Get deepest container
                        var container = (ContainerInline)leafBlock.Inline;
                        while (true)
                        {
                            var nextContainer = container.LastChild as ContainerInline;
                            if (nextContainer != null && !nextContainer.IsClosed)
                            {
                                container = nextContainer;
                            }
                            else
                            {
                                break;
                            }
                        }

                        container.AppendChild(nextInline);
                    }

                    if (nextInline.IsClosable && !nextInline.IsClosed)
                    {
                        var inlinesToClose = inlineState.InlinesToClose;
                        var last = inlinesToClose.Count > 0
                            ? inlineState.InlinesToClose[inlinesToClose.Count - 1]
                            : null;
                        if (last != nextInline)
                        {
                            inlineState.InlinesToClose.Add(nextInline);
                        }
                    }
                }
                else
                {
                    // Get deepest container
                    var container = (ContainerInline)leafBlock.Inline;
                    while (true)
                    {
                        var nextContainer = container.LastChild as ContainerInline;
                        if (nextContainer != null && !nextContainer.IsClosed)
                        {
                            container = nextContainer;
                        }
                        else
                        {
                            break;
                        }
                    }

                    inlineState.Inline = container.LastChild is LeafInline ? container.LastChild : container;
                }

                if (Log != null)
                {
                    Log.WriteLine($"** Dump: char '{c}");
                    leafBlock.Inline.DumpTo(Log);
                }
            }

            // Close all inlines not closed
            inlineState.Inline = null;
            foreach (var inline in inlineState.InlinesToClose)
            {
                inline.CloseInternal(inlineState);
            }
            inlineState.InlinesToClose.Clear();

            if (Log != null)
            {
                Log.WriteLine("** Dump before Emphasis:");
                leafBlock.Inline.DumpTo(Log);
                EmphasisInline.ProcessEmphasis(leafBlock.Inline);

                Log.WriteLine();
                Log.WriteLine("** Dump after Emphasis:");
                leafBlock.Inline.DumpTo(Log);
            }
            // TODO: Close opened inlines

            // Close last inline
            //while (inlineStack.Count > 0)
            //{
            //    var inlineState = inlineStack.Pop();
            //    inlineState.Parser.Close(state, inlineState.Inline);
            //}
        }
    }
}