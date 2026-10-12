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
            // Inside a list item, paragraphs sit tight; in a table cell they have no gap and the
            // table's smaller type; elsewhere they get a full gap.
            var inList = obj.Parent is ListItemBlock;
            var inTable = obj.Parent is Markdig.Extensions.Tables.TableCell;
            var paragraph = new Paragraph
            {
                Foreground = MarkdownStyles.FgPrimary,
                FontSize = inTable ? MarkdownStyles.FontSizeTable : MarkdownStyles.FontSizeNormal,
                LineHeight = inTable ? MarkdownStyles.LineHeightTable : MarkdownStyles.LineHeight,
                Margin = new Thickness(0, 0, 0, inTable ? 0 : inList ? 2 : MarkdownStyles.ParagraphSpacing)
            };

            renderer.Push(paragraph);
            renderer.WriteLeafInline(obj);
            renderer.Pop();
        }
    }
}
