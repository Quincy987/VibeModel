using System;
using System.Windows.Documents;
using Markdig;

namespace VibeModel.Markdown
{
    /// <summary>
    /// Static entry point for converting markdown to a WPF FlowDocument.
    /// </summary>
    public static class MarkdownHelper
    {
        // Pipe and grid tables: the model answers "list the 5 longest walls" with a table.
        private static readonly MarkdownPipeline Pipeline =
            new MarkdownPipelineBuilder().UsePipeTables().UseGridTables().Build();

        /// <summary>
        /// Parses markdown text and returns a styled FlowDocument for dark-theme display.
        /// </summary>
        public static FlowDocument ToFlowDocument(string markdown) => ToFlowDocument(markdown, null);

        /// <summary>
        /// As <see cref="ToFlowDocument(string)"/>; <c>[bron: …]</c> source markers become
        /// superscript numbers with a "Bronnen" footer, wired to <paramref name="options"/>.
        /// </summary>
        public static FlowDocument ToFlowDocument(string markdown, MarkdownRenderOptions options)
        {
            if (string.IsNullOrEmpty(markdown))
            {
                var empty = new FlowDocument();
                empty.Foreground = MarkdownStyles.FgPrimary;
                empty.FontSize = MarkdownStyles.FontSizeNormal;
                return empty;
            }

            var citations = CitationParser.Parse(markdown);
            var document = new FlowDocument();
            var renderer = new WpfRenderer(document)
            {
                Citations = citations,
                Options = options ?? new MarkdownRenderOptions()
            };

            Pipeline.Setup(renderer);

            var parsed = Markdig.Markdown.Parse(citations.Text, Pipeline);
            renderer.Render(parsed);

            if (citations.Citations.Count > 0)
                document.Blocks.Add(CitationInlines.Footer(citations, renderer.Options));

            return document;
        }
    }
}
