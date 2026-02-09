// Based on Markdig.Wpf (MIT License) - https://github.com/Kryptos-FR/markdig.wpf
// Copyright (c) Nicolas Musset. All rights reserved.

using Markdig.Syntax.Inlines;

namespace VibeModel.Markdown.Renderers.Inlines
{
    public class LiteralInlineRenderer : WpfObjectRenderer<LiteralInline>
    {
        protected override void Write(WpfRenderer renderer, LiteralInline obj)
        {
            if (obj.Content.IsEmpty)
                return;

            renderer.WriteText(ref obj.Content);
        }
    }
}
