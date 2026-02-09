// Based on Markdig.Wpf (MIT License) - https://github.com/Kryptos-FR/markdig.wpf
// Copyright (c) Nicolas Musset. All rights reserved.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows.Documents;
using System.Windows.Markup;
using Markdig.Helpers;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using VibeModel.Markdown.Renderers;
using VibeModel.Markdown.Renderers.Inlines;
using Block = System.Windows.Documents.Block;
using Inline = System.Windows.Documents.Inline;

namespace VibeModel.Markdown
{
    /// <summary>
    /// WPF renderer that walks the Markdig AST and builds a FlowDocument.
    /// </summary>
    public class WpfRenderer : RendererBase
    {
        private readonly Stack<IAddChild> _stack = new Stack<IAddChild>();
        private char[] _buffer;

        public FlowDocument Document { get; private set; }

        public WpfRenderer(FlowDocument document)
        {
            _buffer = new char[1024];
            Document = document ?? throw new ArgumentNullException(nameof(document));

            // Apply document-level styles
            Document.Foreground = MarkdownStyles.FgPrimary;
            Document.FontSize = MarkdownStyles.FontSizeNormal;
            Document.PagePadding = new System.Windows.Thickness(0);

            _stack.Push(document);
            LoadRenderers();
        }

        public override object Render(MarkdownObject markdownObject)
        {
            Write(markdownObject);
            return Document;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteLeafInline(LeafBlock leafBlock)
        {
            if (leafBlock == null) throw new ArgumentNullException(nameof(leafBlock));
            var inline = (Markdig.Syntax.Inlines.Inline)leafBlock.Inline;
            while (inline != null)
            {
                Write(inline);
                inline = inline.NextSibling;
            }
        }

        public void WriteLeafRawLines(LeafBlock leafBlock)
        {
            if (leafBlock == null) throw new ArgumentNullException(nameof(leafBlock));
            if (leafBlock.Lines.Lines != null)
            {
                var lines = leafBlock.Lines;
                var slices = lines.Lines;
                for (var i = 0; i < lines.Count; i++)
                {
                    if (i != 0)
                        WriteInline(new LineBreak());

                    WriteText(ref slices[i].Slice);
                }
            }
        }

        public void Push(IAddChild o)
        {
            _stack.Push(o);
        }

        public void Pop()
        {
            var popped = _stack.Pop();
            _stack.Peek().AddChild(popped);
        }

        public void WriteBlock(Block block)
        {
            _stack.Peek().AddChild(block);
        }

        public void WriteInline(Inline inline)
        {
            _stack.Peek().AddChild(inline);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteText(ref StringSlice slice)
        {
            if (slice.Start > slice.End)
                return;

            WriteText(slice.Text, slice.Start, slice.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteText(string text)
        {
            WriteInline(new Run(text));
        }

        public void WriteText(string text, int offset, int length)
        {
            if (text == null)
                return;

            if (offset == 0 && text.Length == length)
            {
                WriteText(text);
            }
            else
            {
                if (length > _buffer.Length)
                {
                    _buffer = text.ToCharArray();
                    WriteText(new string(_buffer, offset, length));
                }
                else
                {
                    text.CopyTo(offset, _buffer, 0, length);
                    WriteText(new string(_buffer, 0, length));
                }
            }
        }

        protected virtual void LoadRenderers()
        {
            // Block renderers
            ObjectRenderers.Add(new CodeBlockRenderer());
            ObjectRenderers.Add(new ListRenderer());
            ObjectRenderers.Add(new HeadingRenderer());
            ObjectRenderers.Add(new ParagraphRenderer());
            ObjectRenderers.Add(new QuoteBlockRenderer());
            ObjectRenderers.Add(new ThematicBreakRenderer());

            // Inline renderers
            ObjectRenderers.Add(new AutolinkInlineRenderer());
            ObjectRenderers.Add(new CodeInlineRenderer());
            ObjectRenderers.Add(new DelimiterInlineRenderer());
            ObjectRenderers.Add(new EmphasisInlineRenderer());
            ObjectRenderers.Add(new HtmlEntityInlineRenderer());
            ObjectRenderers.Add(new LineBreakInlineRenderer());
            ObjectRenderers.Add(new LinkInlineRenderer());
            ObjectRenderers.Add(new LiteralInlineRenderer());
        }
    }
}
