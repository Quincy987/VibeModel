using System;
using System.Collections.Generic;

namespace VibeModel.UI
{
    /// <summary>
    /// Localized strings for the chat pane's own chrome (activity block, step labels, summaries).
    /// Dutch by default; English when <see cref="Language"/> is "en". WPF-free so it is testable.
    /// The model-facing texts (system prompts, <see cref="Services.Chat.StatusVerbs"/>) are NOT
    /// here — those stay where they are; this only covers what the pane itself draws.
    /// </summary>
    public static class UiText
    {
        /// <summary>"nl" (default) or "en". Anything else falls back to Dutch.</summary>
        public static string Language { get; set; } = "nl";

        private static bool En => string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);

        public static string Thinking => En ? "Thinking…" : "Aan het denken…";
        public static string Working => En ? "Working…" : "Bezig…";
        public static string WritingAnswer => En ? "Writing the answer…" : "Antwoord schrijven…";
        public static string StallHint => En ? "Taking a while — Revit may be busy" : "Duurt lang — Revit is misschien bezig";
        public static string Cancelled => En ? "Cancelled" : "Geannuleerd";

        public static string EarlierSteps(int count)
        {
            if (En) return "+" + count + (count == 1 ? " earlier step" : " earlier steps");
            return "+" + count + (count == 1 ? " eerdere stap" : " eerdere stappen");
        }

        /// <summary>"7 stappen · 1 mislukt · 48 s" — the collapsed one-line summary of a turn.</summary>
        public static string StepSummary(int steps, int failed, TimeSpan elapsed)
        {
            var parts = new List<string>();
            if (En) parts.Add(steps + (steps == 1 ? " step" : " steps"));
            else parts.Add(steps + (steps == 1 ? " stap" : " stappen"));
            if (failed > 0) parts.Add(failed + (En ? " failed" : " mislukt"));
            parts.Add(Duration(elapsed));
            return string.Join(" · ", parts);
        }

        /// <summary>"48 s", "1 min 12 s".</summary>
        public static string Duration(TimeSpan elapsed)
        {
            var total = (int)Math.Round(Math.Max(0, elapsed.TotalSeconds));
            if (total < 60) return total + " s";
            var sec = total % 60;
            return (total / 60) + " min" + (sec > 0 ? " " + sec + " s" : "");
        }

        /// <summary>The live counter: "00:12", "01:05", "61:00".</summary>
        public static string Clock(TimeSpan elapsed)
        {
            var total = (int)Math.Max(0, elapsed.TotalSeconds);
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }

        public static string MoreCodeLines(int count) =>
            En ? "… " + count + " more lines (Copy shows everything)"
               : "… nog " + count + " regels (Kopiëren geeft alles)";

        // --- Step labels ------------------------------------------------------------------
        // A step is a VibeModel command ("wall") or, for the Claude CLI, one of its own tools
        // ("@read", "@image", "@shell", "@<ToolName>") — see ToolCallDescriber.

        private static readonly Dictionary<string, string> StepNl =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "info", "Model bekijken…" },
                { "context", "Model bekijken…" },
                { "health", "Verbinding controleren…" },
                { "help", "Commando's opzoeken…" },
                { "activeview", "Actieve view bekijken…" },
                { "selected", "Selectie bekijken…" },
                { "params", "Parameters lezen…" },
                { "get", "Element lezen…" },
                { "list", "Elementen opsommen…" },
                { "categories", "Categorieën lezen…" },
                { "subcats", "Subcategorieën lezen…" },
                { "views", "Views opsommen…" },
                { "levels", "Levels opsommen…" },
                { "family", "Familie bekijken…" },
                { "familytypes", "Familietypes opsommen…" },
                { "bbox", "Afmetingen meten…" },
                { "geometry", "Geometrie lezen…" },
                { "measure", "Meten…" },
                { "whereused", "Gebruik zoeken…" },
                { "filter", "Elementen filteren…" },
                { "whyhidden", "Zichtbaarheid onderzoeken…" },
                { "docs", "Projectmap bekijken…" },
                { "readdoc", "Projectdocument lezen…" },
                { "imports", "Imports opsommen…" },
                { "layers", "DWG-lagen lezen…" },
                { "curves", "DWG-lijnen lezen…" },
                { "pointclouds", "Puntenwolken opsommen…" },
                { "pcprobe", "Puntenwolk peilen…" },
                { "pcline", "Puntenwolkprofiel lezen…" },
                { "template", "Template bekijken…" },
                { "standards", "Kantoorstandaard lezen…" },
                { "check", "Model controleren…" },
                { "coords", "Coördinaten lezen…" },
                { "library", "Bibliotheek doorzoeken…" },
                { "loadfamily", "Familie laden…" },
                { "linkcad", "CAD koppelen…" },
                { "phase", "Fase instellen…" },
                { "activate", "View openen…" },

                { "wall", "Wand maken…" },
                { "floor", "Vloer maken…" },
                { "place", "Familie plaatsen…" },
                { "placeid", "Familie plaatsen…" },
                { "delete", "Elementen verwijderen…" },
                { "set", "Parameter instellen…" },
                { "color", "Kleur toepassen…" },
                { "colorsplash", "Kleuren toepassen…" },
                { "select", "Elementen selecteren…" },
                { "isolate", "Elementen isoleren…" },
                { "unisolate", "View herstellen…" },
                { "join", "Geometrie verbinden…" },
                { "unjoin", "Verbinding verwijderen…" },
                { "hide", "Elementen verbergen…" },
                { "show", "Elementen tonen…" },
                { "zoomto", "Inzoomen…" },
                { "exec", "Code uitvoeren…" },
                { "batch", "Reeks opdrachten uitvoeren…" },
                { "screenshot", "Screenshot maken…" },
                { "grid", "Stramien maken…" },
                { "level", "Level maken…" },
                { "view", "View maken…" },
                { "room", "Ruimte maken…" },
                { "sheet", "Sheet maken…" },
                { "tag", "Label plaatsen…" },
                { "section", "Doorsnede maken…" },
                { "viewport", "Views op sheet plaatsen…" },
                { "dimension", "Maatvoering toevoegen…" },
                { "text", "Tekst plaatsen…" },
                { "sheetinfo", "Sheet controleren…" },
                { "crop", "View bijsnijden…" },

                { "@read", "Bestand lezen…" },
                { "@image", "Afbeelding bekijken…" },
                { "@shell", "Opdracht uitvoeren…" },
            };

        private static readonly Dictionary<string, string> StepEnTools =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "health", "Checking the connection…" },
                { "help", "Looking up commands…" },
                { "@read", "Reading file…" },
                { "@image", "Looking at the image…" },
                { "@shell", "Running a command…" },
            };

        /// <summary>
        /// The label of one step. English reuses <see cref="Services.Chat.StatusVerbs"/> (the single
        /// source of the English verbs); Dutch has its own table here.
        /// </summary>
        public static string StepLabel(string command)
        {
            command = command ?? "";
            string label;
            if (En)
            {
                if (StepEnTools.TryGetValue(command, out label)) return label;
                if (command.StartsWith("@")) return command.Substring(1) + "…";
                return Services.Chat.StatusVerbs.Describe(command);
            }

            if (StepNl.TryGetValue(command, out label)) return label;
            if (command.StartsWith("@")) return command.Substring(1) + "…";
            return command + "…";
        }
    }
}
