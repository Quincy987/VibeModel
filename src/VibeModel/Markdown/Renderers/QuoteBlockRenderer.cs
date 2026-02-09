// Based on Markdig.Wpf (MIT License) - https://github.com/Kryptos-FR/markdig.wpf
// Copyright (c) Nicolas Musset. All rights reserved.

using System.Windows;
using System.Windows.Documents;
using Markdig.Syntax;

namespace VibeModel.Markdown.Renderers
{
    public class QuoteBlockRenderer : WpfObjectRenderer<QuoteBlock>
    {
        protected override void Write(WpfRenderer renderer, QuoteBlock obj)
        {
            var section = new Section
            {
                Foreground = MarkdownStyles.FgSecondary,
                BorderBrush = MarkdownStyles.QuoteBorder,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(10, 2, 0, 2),
                Margin = new Thickness(4, 4, 0, 4)
            };

            renderer.Push(section);
            renderer.WriteChildren(obj);
            renderer.Pop();
        }
    }
}
