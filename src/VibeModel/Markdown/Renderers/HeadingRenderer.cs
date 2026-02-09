// Based on Markdig.Wpf (MIT License) - https://github.com/Kryptos-FR/markdig.wpf
// Copyright (c) Nicolas Musset. All rights reserved.

using System.Windows;
using System.Windows.Documents;
using Markdig.Syntax;

namespace VibeModel.Markdown.Renderers
{
    public class HeadingRenderer : WpfObjectRenderer<HeadingBlock>
    {
        protected override void Write(WpfRenderer renderer, HeadingBlock obj)
        {
            double fontSize;
            FontWeight weight;

            switch (obj.Level)
            {
                case 1:
                    fontSize = MarkdownStyles.FontSizeH1;
                    weight = FontWeights.Bold;
                    break;
                case 2:
                    fontSize = MarkdownStyles.FontSizeH2;
                    weight = FontWeights.Bold;
                    break;
                case 3:
                    fontSize = MarkdownStyles.FontSizeH3;
                    weight = FontWeights.SemiBold;
                    break;
                default:
                    fontSize = MarkdownStyles.FontSizeNormal;
                    weight = FontWeights.SemiBold;
                    break;
            }

            var paragraph = new Paragraph
            {
                FontSize = fontSize,
                FontWeight = weight,
                Foreground = MarkdownStyles.FgPrimary,
                Margin = new Thickness(0, 6, 0, 2)
            };

            renderer.Push(paragraph);
            renderer.WriteLeafInline(obj);
            renderer.Pop();
        }
    }
}
