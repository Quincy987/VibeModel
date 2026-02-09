// Based on Markdig.Wpf (MIT License) - https://github.com/Kryptos-FR/markdig.wpf
// Copyright (c) Nicolas Musset. All rights reserved.

using System;
using System.Diagnostics;
using System.Windows.Documents;
using Markdig.Syntax.Inlines;

namespace VibeModel.Markdown.Renderers.Inlines
{
    public class AutolinkInlineRenderer : WpfObjectRenderer<AutolinkInline>
    {
        protected override void Write(WpfRenderer renderer, AutolinkInline link)
        {
            var url = link.Url;
            if (link.IsEmail)
                url = "mailto:" + url;

            if (!Uri.IsWellFormedUriString(url, UriKind.RelativeOrAbsolute))
                url = "#";

            var hyperlink = new Hyperlink
            {
                Foreground = MarkdownStyles.AccentBlue,
                ToolTip = link.Url,
            };

            var capturedUrl = url;
            hyperlink.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(capturedUrl) { UseShellExecute = true }); }
                catch { }
            };

            renderer.Push(hyperlink);
            renderer.WriteText(link.Url);
            renderer.Pop();
        }
    }
}
