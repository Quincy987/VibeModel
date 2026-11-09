using System.Globalization;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// One wording for output that was cut off: the real total and the fact that it is not
    /// everything, so a cut list is never read as the whole. Pure.
    /// </summary>
    public static class Truncation
    {
        /// <summary>"showing 100 of 312 — incomplete; narrow with filter or --view".</summary>
        public static string Line(int shown, int total, string hint = null)
            => Line(shown, total.ToString(CultureInfo.InvariantCulture), hint);

        /// <summary>As above, for a total that is itself a lower bound ("5000+").</summary>
        public static string Line(int shown, string total, string hint = null)
            => "showing " + shown.ToString(CultureInfo.InvariantCulture) + " of " + total + " — incomplete" + (string.IsNullOrWhiteSpace(hint) ? "" : "; " + hint);
    }
}
