using System.Collections.Generic;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// Maps a Revit command (or "revit_"-prefixed tool name) to a short, human-friendly
    /// status verb shown in the chat while the tool runs ("Creating wall…").
    /// These status lines are UI-only — they are streamed to the user via onToken but are
    /// never added to the model-bound conversation history.
    /// New commands (plans 01/04) should add an entry here so they get a status line for free —
    /// in BOTH maps (English and Dutch; a test checks they cover the same commands).
    /// </summary>
    public static class StatusVerbs
    {
        private static readonly Dictionary<string, string> En =
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                // Read / inspect
                { "info", "Looking at the model…" },
                { "context", "Looking at the model…" },
                { "activeview", "Looking at the active view…" },
                { "selected", "Checking the selection…" },
                { "params", "Reading parameters…" },
                { "get", "Reading element…" },
                { "list", "Listing elements…" },
                { "categories", "Reading categories…" },
                { "subcats", "Reading subcategories…" },
                { "views", "Listing views…" },
                { "levels", "Listing levels…" },
                { "family", "Reading family info…" },
                { "familytypes", "Listing family types…" },
                { "bbox", "Measuring bounds…" },
                { "geometry", "Reading geometry…" },
                { "measure", "Measuring…" },
                { "whereused", "Finding usages…" },
                { "filter", "Filtering elements…" },
                { "docs", "Listing project documents…" },
                { "readdoc", "Reading project document…" },

                // Modify
                { "wall", "Creating wall…" },
                { "floor", "Creating floor…" },
                { "place", "Placing family…" },
                { "placeid", "Placing family…" },
                { "delete", "Deleting elements…" },
                { "set", "Setting parameter…" },
                { "color", "Applying color…" },
                { "colorsplash", "Applying colors…" },
                { "select", "Selecting elements…" },
                { "isolate", "Isolating elements…" },
                { "unisolate", "Restoring view…" },
                { "join", "Joining geometry…" },
                { "unjoin", "Removing join…" },
                { "hide", "Hiding elements…" },
                { "show", "Showing elements…" },
                { "zoomto", "Zooming to elements…" },
                { "exec", "Running code…" },
                { "batch", "Running batch…" },

                // Reserved for upcoming plans (01 vision, 04 modeling breadth)
                { "screenshot", "Taking screenshot…" },
                { "grid", "Adding grid…" },
                { "level", "Adding level…" },
                { "view", "Creating view…" },
                { "room", "Adding room…" },
                { "sheet", "Creating sheet…" },
                { "tag", "Tagging element…" },

                // Plan 12 — sheet assembly
                { "section", "Creating section…" },
                { "viewport", "Placing views on sheet…" },
                { "dimension", "Adding dimension…" },
                { "text", "Adding text…" },
                { "sheetinfo", "Checking the sheet…" },
                { "crop", "Cropping view…" },
            };

        private static readonly Dictionary<string, string> Nl =
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                // Lezen / bekijken
                { "info", "Model bekijken…" },
                { "context", "Model bekijken…" },
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
                { "docs", "Projectmap bekijken…" },
                { "readdoc", "Projectdocument lezen…" },

                // Wijzigen
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

                // Plan 12 — sheets samenstellen
                { "section", "Doorsnede maken…" },
                { "viewport", "Views op sheet plaatsen…" },
                { "dimension", "Maatvoering toevoegen…" },
                { "text", "Tekst plaatsen…" },
                { "sheetinfo", "Sheet controleren…" },
                { "crop", "View bijsnijden…" },
            };

        /// <summary>
        /// Returns a friendly status verb for a tool/command name in the Settings language
        /// (Taal / Language). Accepts the "revit_" tool prefix. Falls back to "Working… (name)" /
        /// "Bezig… (name)" for unmapped commands.
        /// </summary>
        public static string Describe(string toolName) => Describe(toolName, Strings.Current);

        /// <summary>
        /// The status verb in <paramref name="language"/> ("nl" / "en", see <see cref="Strings"/>).
        /// </summary>
        public static string Describe(string toolName, string language)
        {
            var command = (toolName ?? "").StartsWith("revit_")
                ? toolName.Substring(6)
                : (toolName ?? "");

            string verb;
            if ((Strings.IsEnglish(language) ? En : Nl).TryGetValue(command, out verb))
                return verb;

            return Strings.For(language, Strings.Working, command);
        }

        /// <summary>Every command with a verb, for the test that keeps both maps in step.</summary>
        internal static IEnumerable<string> EnglishCommands => En.Keys;

        internal static IEnumerable<string> DutchCommands => Nl.Keys;
    }
}
