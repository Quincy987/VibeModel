// Based on Markdig.Wpf (MIT License) - https://github.com/Kryptos-FR/markdig.wpf
// Copyright (c) Nicolas Musset. All rights reserved.

using System;
using System.Diagnostics;
using System.Windows.Documents;
using Markdig.Syntax.Inlines;

namespace VibeModel.Markdown.Renderers.Inlines
{
    public class LinkInlineRenderer : WpfObjectRenderer<LinkInline>
    {
        protected override void Write(WpfRenderer renderer, LinkInline link)
        {
            var url = link.GetDynamicUrl != null ? link.GetDynamicUrl() ?? link.Url : link.Url;

            if (!Uri.IsWellFormedUriString(url, UriKind.RelativeOrAbsolute))
                url = "#";

            if (link.IsImage)
            {
                // Images: just render the alt text as inline code
                renderer.WriteChildren(link);
            }
            else
            {
                var hyperlink = new Hyperlink
                {
                    Foreground = MarkdownStyles.AccentBlue,
                    ToolTip = !string.IsNullOrEmpty(link.Title) ? link.Title : url,
                };

                hyperlink.Click += (s, e) =>
                {
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch { }
                };

                renderer.Push(hyperlink);
                renderer.WriteChildren(link);
                renderer.Pop();
            }
        }
    }
}
