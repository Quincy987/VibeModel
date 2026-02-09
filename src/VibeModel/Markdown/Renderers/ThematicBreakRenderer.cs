// Based on Markdig.Wpf (MIT License) - https://github.com/Kryptos-FR/markdig.wpf
// Copyright (c) Nicolas Musset. All rights reserved.

using System.Windows;
using System.Windows.Documents;
using System.Windows.Shapes;
using Markdig.Syntax;

namespace VibeModel.Markdown.Renderers
{
    public class ThematicBreakRenderer : WpfObjectRenderer<ThematicBreakBlock>
    {
        protected override void Write(WpfRenderer renderer, ThematicBreakBlock obj)
        {
            var line = new Line
            {
                X2 = 1,
                Stretch = System.Windows.Media.Stretch.Fill,
                Stroke = MarkdownStyles.BorderColor,
                StrokeThickness = 1
            };

            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 4, 0, 4),
                Inlines = { new InlineUIContainer(line) }
            };

            renderer.WriteBlock(paragraph);
        }
    }
}
