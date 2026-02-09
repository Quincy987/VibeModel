// Based on Markdig.Wpf (MIT License) - https://github.com/Kryptos-FR/markdig.wpf
// Copyright (c) Nicolas Musset. All rights reserved.

using System.Windows;
using System.Windows.Documents;
using Markdig.Syntax;

namespace VibeModel.Markdown.Renderers
{
    public class CodeBlockRenderer : WpfObjectRenderer<CodeBlock>
    {
        protected override void Write(WpfRenderer renderer, CodeBlock obj)
        {
            var paragraph = new Paragraph
            {
                FontFamily = MarkdownStyles.MonoFont,
                FontSize = MarkdownStyles.FontSizeCode,
                Foreground = MarkdownStyles.FgPrimary,
                Background = MarkdownStyles.BgCode,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 4, 0, 4),
                BorderBrush = MarkdownStyles.BorderColor,
                BorderThickness = new Thickness(1),
            };

            renderer.Push(paragraph);
            renderer.WriteLeafRawLines(obj);
            renderer.Pop();
        }
    }
}
