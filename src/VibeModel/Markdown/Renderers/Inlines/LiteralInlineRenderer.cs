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

            // A source marker became a placeholder before parsing (CitationParser); it renders
            // as a small superscript number.
            var citations = renderer.Citations;
            if (citations != null && citations.Citations.Count > 0
                && obj.Content.IndexOf(CitationParser.PlaceholderStart) >= 0)
            {
                // Markers side by side get a small comma, so ¹ ² reads as two sources, not "12".
                var afterMarker = false;
                CitationParser.Split(obj.Content.ToString(),
                    text =>
                    {
                        if (text.Length == 0) return;
                        renderer.WriteText(text);
                        afterMarker = false;
                    },
                    number =>
                    {
                        var citation = citations.ByNumber(number);
                        if (citation == null) return;
                        if (afterMarker) renderer.WriteInline(CitationInlines.Separator());
                        renderer.WriteInline(CitationInlines.Marker(citation, renderer.Options));
                        afterMarker = true;
                    });
                return;
            }

            renderer.WriteText(ref obj.Content);
        }
    }
}
