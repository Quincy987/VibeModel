// Based on Markdig.Wpf (MIT License) - https://github.com/Kryptos-FR/markdig.wpf
// Copyright (c) Nicolas Musset. All rights reserved.

using System.Windows.Documents;
using Markdig.Syntax.Inlines;

namespace VibeModel.Markdown.Renderers.Inlines
{
    public class CodeInlineRenderer : WpfObjectRenderer<CodeInline>
    {
        protected override void Write(WpfRenderer renderer, CodeInline obj)
        {
            // A monospace "pill": lighter background, hair spaces as horizontal padding. A Run
            // (not a UI container) so long paths still wrap and stay selectable.
            var run = new Run("\u200A" + obj.Content + "\u200A")
            {
                FontFamily = MarkdownStyles.MonoFont,
                FontSize = MarkdownStyles.FontSizeCode,
                Foreground = MarkdownStyles.FgCodeInline,
                Background = MarkdownStyles.BgCodeInline
            };
            renderer.WriteInline(run);
        }
    }
}
