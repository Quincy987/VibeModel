// Based on Markdig.Wpf (MIT License) - https://github.com/Kryptos-FR/markdig.wpf
// Copyright (c) Nicolas Musset. All rights reserved.

using System.Windows;
using System.Windows.Documents;
using Markdig.Syntax.Inlines;

namespace VibeModel.Markdown.Renderers.Inlines
{
    public class EmphasisInlineRenderer : WpfObjectRenderer<EmphasisInline>
    {
        protected override void Write(WpfRenderer renderer, EmphasisInline obj)
        {
            Span span = null;

            switch (obj.DelimiterChar)
            {
                case '*':
                case '_':
                    span = obj.DelimiterCount == 2 ? (Span)new Bold() : new Italic();
                    break;
                case '~':
                    span = new Span();
                    if (obj.DelimiterCount == 2)
                        span.TextDecorations = TextDecorations.Strikethrough;
                    break;
            }

            if (span != null)
            {
                renderer.Push(span);
                renderer.WriteChildren(obj);
                renderer.Pop();
            }
            else
            {
                renderer.WriteChildren(obj);
            }
        }
    }
}
