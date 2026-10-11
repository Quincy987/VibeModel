using System.Text;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// Thread-safe holder for the latest TEMPLATE + STANDARDS prompt blocks. Written on the
    /// Revit main thread (StandardsContext), read by the chat backends on worker threads when
    /// they build a system prompt. Pure — no Revit types, so backends and tests can use it.
    /// </summary>
    public static class StandardsPrompt
    {
        private static volatile string _template = "";
        private static volatile string _standards = "";

        public static string TemplateBlock => _template;
        public static string StandardsBlock => _standards;

        public static void Publish(string template, string standards)
        {
            _template = template ?? "";
            _standards = standards ?? "";
        }

        /// <summary>Both blocks joined (empty string when there is nothing to inject).</summary>
        public static string Combined
        {
            get
            {
                var t = _template;
                var s = _standards;
                if (string.IsNullOrWhiteSpace(t) && string.IsNullOrWhiteSpace(s)) return "";
                var sb = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(t)) sb.AppendLine(t.TrimEnd());
                if (!string.IsNullOrWhiteSpace(s))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine(s.TrimEnd());
                }
                return sb.ToString();
            }
        }
    }
}
