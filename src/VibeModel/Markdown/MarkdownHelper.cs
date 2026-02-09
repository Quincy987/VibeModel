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
        private static readonly MarkdownPipeline Pipeline =
            new MarkdownPipelineBuilder().Build();

        /// <summary>
        /// Parses markdown text and returns a styled FlowDocument for dark-theme display.
        /// </summary>
        public static FlowDocument ToFlowDocument(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
            {
                var empty = new FlowDocument();
                empty.Foreground = MarkdownStyles.FgPrimary;
                empty.FontSize = MarkdownStyles.FontSizeNormal;
                return empty;
            }

            var document = new FlowDocument();
            var renderer = new WpfRenderer(document);

            Pipeline.Setup(renderer);

            var parsed = Markdig.Markdown.Parse(markdown, Pipeline);
            renderer.Render(parsed);

            return document;
        }
    }
}
