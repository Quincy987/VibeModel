// Based on Markdig.Wpf (MIT License) - https://github.com/Kryptos-FR/markdig.wpf
// Copyright (c) Nicolas Musset. All rights reserved.

using Markdig.Syntax.Inlines;

namespace VibeModel.Markdown.Renderers.Inlines
{
    public class DelimiterInlineRenderer : WpfObjectRenderer<DelimiterInline>
    {
        protected override void Write(WpfRenderer renderer, DelimiterInline obj)
        {
            renderer.WriteText(obj.ToLiteral());
            renderer.WriteChildren(obj);
        }
    }
}
