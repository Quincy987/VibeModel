using System;
using System.Collections.Generic;
using System.Linq;

namespace VibeModel.Services.Chat
{
    /// <summary>What the pane knows about the open model when it builds the starter chips.</summary>
    public sealed class StarterContext
    {
        public int SelectedCount { get; set; }
        public bool ActiveViewIsSheet { get; set; }
        /// <summary>An office standards pack matches the open document.</summary>
        public bool PackActive { get; set; }
        /// <summary>Documents in the project folder (0 when there is no folder or it is not listed yet).</summary>
        public int ProjectDocCount { get; set; }
    }

    /// <summary>
    /// The chips of an empty chat — no model call: rules on the live context first (what is
    /// selected, a sheet that is open, an office pack, project documents), then the fixed
    /// examples, without duplicates, at most <see cref="MaxChips"/>. WPF-free.
    /// </summary>
    public static class Starters
    {
        public const int MaxChips = 4;

        public static IReadOnlyList<string> Pick(StarterContext ctx, string language)
        {
            ctx = ctx ?? new StarterContext();
            var picks = new List<string>();

            if (ctx.SelectedCount > 0) picks.Add(Strings.For(language, Strings.StarterSelection));
            if (ctx.ActiveViewIsSheet) picks.Add(Strings.For(language, Strings.StarterSheet));
            if (ctx.PackActive) picks.Add(Strings.For(language, Strings.StarterCheck));
            if (ctx.ProjectDocCount > 0) picks.Add(Strings.For(language, Strings.StarterDocs));

            var defaults = new[]
            {
                Strings.WelcomeExample1, Strings.WelcomeExample2, Strings.WelcomeExample3,
                // The sheet starter already is "fit these views" — the A0 example would repeat it.
                ctx.ActiveViewIsSheet ? null : Strings.WelcomeExample4
            };
            foreach (var key in defaults)
            {
                if (picks.Count >= MaxChips) break;
                if (key == null) continue;
                var text = Strings.For(language, key);
                if (!picks.Contains(text, StringComparer.OrdinalIgnoreCase)) picks.Add(text);
            }
            return picks.Take(MaxChips).ToList();
        }
    }
}
