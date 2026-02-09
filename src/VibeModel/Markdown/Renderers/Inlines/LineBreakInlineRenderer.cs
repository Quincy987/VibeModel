// Based on Markdig.Wpf (MIT License) - https://github.com/Kryptos-FR/markdig.wpf
// Copyright (c) Nicolas Musset. All rights reserved.

using System.Windows.Documents;
using Markdig.Syntax.Inlines;

namespace VibeModel.Markdown.Renderers.Inlines
{
    public class LineBreakInlineRenderer : WpfObjectRenderer<LineBreakInline>
    {
        protected override void Write(WpfRenderer renderer, LineBreakInline obj)
        {
            if (obj.IsHard)
            {
                renderer.WriteInline(new LineBreak());
            }
            else
            {
                renderer.WriteText(" ");
            }
        }
    }
}
