// Based on Markdig.Wpf (MIT License) - https://github.com/Kryptos-FR/markdig.wpf
// Copyright (c) Nicolas Musset. All rights reserved.

using System.Windows;
using System.Windows.Documents;
using Markdig.Syntax;

namespace VibeModel.Markdown.Renderers
{
    public class ParagraphRenderer : WpfObjectRenderer<ParagraphBlock>
    {
        protected override void Write(WpfRenderer renderer, ParagraphBlock obj)
        {
            var paragraph = new Paragraph
            {
                Foreground = MarkdownStyles.FgPrimary,
                FontSize = MarkdownStyles.FontSizeNormal,
                Margin = new Thickness(0, 0, 0, 4)
            };

            renderer.Push(paragraph);
            renderer.WriteLeafInline(obj);
            renderer.Pop();
        }
    }
}
