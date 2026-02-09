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
            var run = new Run(obj.Content)
            {
                FontFamily = MarkdownStyles.MonoFont,
                FontSize = MarkdownStyles.FontSizeCode,
                Background = MarkdownStyles.BgCode
            };
            renderer.WriteInline(run);
        }
    }
}
