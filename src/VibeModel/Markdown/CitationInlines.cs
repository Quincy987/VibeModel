using System;
using System.Windows;
using System.Windows.Documents;
using VibeModel.Markdown.Renderers;
using VibeModel.UI;

namespace VibeModel.Markdown
{
    /// <summary>Click handlers and language for the interactive parts of a rendered message.</summary>
    public sealed class MarkdownRenderOptions
    {
        /// <summary>A source marker (or its footer line) was clicked. Null: markers are tooltip-only.</summary>
        public Action<Citation> OnCitationClick { get; set; }

        /// <summary>"nl"/"en" for the fixed texts (Kopiëren, Bronnen …); null = the Settings language.</summary>
        public string Language { get; set; }
    }

    /// <summary>
    /// The WPF side of source markers: the superscript number in the text and the per-message
    /// "Bronnen (N)" footer that unfolds into the numbered list.
    /// </summary>
    public static class CitationInlines
    {
        internal const string FooterTag = "vm-sources-footer";
        internal const string ListTag = "vm-sources-list";

        /// <summary>"¹" in the accent colour; tooltip = the source; clickable when it can be opened.</summary>
        public static Inline Marker(Citation citation, MarkdownRenderOptions options)
        {
            var run = new Run(citation.Superscript)
            {
                Foreground = MarkdownStyles.AccentBlue,
                FontSize = MarkdownStyles.FontSizeCitation,
                FontWeight = FontWeights.SemiBold
            };
            var canOpen = options?.OnCitationClick != null && citation.TryGetLookup(out _, out _);
            var tip = UiText.CitationTooltip(citation.Source, canOpen, options?.Language);
            if (!canOpen)
            {
                run.ToolTip = tip;
                return run;
            }

            var link = new Hyperlink(run)
            {
                TextDecorations = null,
                Foreground = MarkdownStyles.AccentBlue,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = tip
            };
            link.Click += (s, e) => options.OnCitationClick(citation);
            return link;
        }

        /// <summary>The small raised comma between adjacent markers.</summary>
        public static Inline Separator() => new Run(",")
        {
            Foreground = MarkdownStyles.AccentBlue,
            FontSize = MarkdownStyles.FontSizeCitation,
            BaselineAlignment = BaselineAlignment.Superscript
        };

        /// <summary>
        /// "Bronnen (3) ▸" — collapsed by default; a click unfolds one line per source
        /// ("¹ leidraad 4.2"), each clickable like its marker.
        /// </summary>
        public static Paragraph Footer(CitationParseResult citations, MarkdownRenderOptions options)
        {
            var language = options?.Language;
            var header = new Paragraph
            {
                FontSize = MarkdownStyles.FontSizeSmall,
                Foreground = MarkdownStyles.FgSecondary,
                Margin = new Thickness(0, 2, 0, 4),
                Tag = FooterTag
            };
            var label = new Run(UiText.SourcesFooter(citations.Citations.Count, false, language));
            var toggle = TableRenderer.MutedLink(null);
            toggle.Foreground = MarkdownStyles.FgSecondary;
            toggle.Inlines.Add(label);
            header.Inlines.Add(toggle);

            var list = new Section { Tag = ListTag, Margin = new Thickness(0, 0, 0, 4) };
            foreach (var c in citations.Citations)
            {
                var line = new Paragraph
                {
                    FontSize = MarkdownStyles.FontSizeSmall,
                    Foreground = MarkdownStyles.FgSecondary,
                    Margin = new Thickness(8, 0, 0, 1)
                };
                line.Inlines.Add(Marker(c, options));
                line.Inlines.Add(new Run(" " + c.Source));
                list.Blocks.Add(line);
            }

            toggle.Click += (s, e) =>
            {
                var doc = header.Parent as FlowDocument;
                var open = list.Parent != null;
                if (open) ((FlowDocument)list.Parent).Blocks.Remove(list);
                else doc?.Blocks.InsertAfter(header, list);
                label.Text = UiText.SourcesFooter(citations.Citations.Count, !open, language);
            };
            return header;
        }
    }
}
