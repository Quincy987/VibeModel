// Based on Markdig.Wpf (MIT License) - https://github.com/Kryptos-FR/markdig.wpf
// Copyright (c) Nicolas Musset. All rights reserved.

using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using Markdig.Syntax;

namespace VibeModel.Markdown.Renderers
{
    public class ListRenderer : WpfObjectRenderer<ListBlock>
    {
        protected override void Write(WpfRenderer renderer, ListBlock listBlock)
        {
            var list = new List
            {
                Foreground = MarkdownStyles.FgPrimary,
                Margin = new Thickness(0, 2, 0, 2),
                Padding = new Thickness(16, 0, 0, 0)
            };

            if (listBlock.IsOrdered)
            {
                list.MarkerStyle = TextMarkerStyle.Decimal;

                if (listBlock.OrderedStart != null && listBlock.DefaultOrderedStart != listBlock.OrderedStart)
                {
                    list.StartIndex = int.Parse(listBlock.OrderedStart, NumberFormatInfo.InvariantInfo);
                }
            }
            else
            {
                list.MarkerStyle = TextMarkerStyle.Disc;
            }

            renderer.Push(list);

            foreach (var item in listBlock)
            {
                var listItemBlock = (ListItemBlock)item;
                var listItem = new ListItem { Margin = new Thickness(0, 1, 0, 1) };
                renderer.Push(listItem);
                renderer.WriteChildren(listItemBlock);
                renderer.Pop();
            }

            renderer.Pop();
        }
    }
}
