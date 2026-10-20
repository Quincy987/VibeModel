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
                { "remember", "Remembering for this project…" },
                { "memory", "Reading project memory…" },
                { "forget", "Forgetting a project fact…" },
                { "journal", "Reading the project journal…" },
                { "chatlog", "Reading the chat history…" },
                { "diagnose", "Checking the setup…" },
                { "approvals", "Reading decisions…" },
                { "apilookup", "Looking up the Revit API…" },

                // Modify
                { "wall", "Creating wall…" },
                { "floor", "Creating floor…" },
                { "place", "Placing family…" },
                { "placeid", "Placing family…" },
                { "profile", "Saving profile…" },
                { "extrude", "Generating family along the line…" },
                { "sweep", "Sweeping profile along the path…" },
                { "lwall", "Generating L-wall…" },
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
                { "spot", "Adding spot elevation…" },
                { "whatdepends", "Checking dependents…" },
                { "text", "Adding text…" },
                { "sheetinfo", "Checking the sheet…" },
                { "crop", "Cropping view…" },
                { "look", "Aiming the 3D view…" },
                { "schedule", "Building schedule…" },
                { "callout", "Creating callout…" },
                { "region", "Drawing filled region…" },
                { "export", "Exporting…" },

                // Diagnosis, source data, office standards (plans 07/08/11)
                { "health", "Checking the connection…" },
                { "help", "Looking up commands…" },
                { "whyhidden", "Diagnosing visibility…" },
                { "imports", "Listing imports…" },
                { "layers", "Reading DWG layers…" },
                { "curves", "Reading DWG lines…" },
                { "pointclouds", "Listing point clouds…" },
                { "pcprobe", "Probing the point cloud…" },
                { "pcline", "Reading point cloud profile…" },
                { "template", "Looking at the template…" },
                { "standards", "Reading office standards…" },
                { "check", "Checking the model…" },
                { "coords", "Reading coordinates…" },
                { "library", "Searching the library…" },
                { "loadfamily", "Loading family…" },
                { "linkcad", "Linking CAD…" },
                { "linkrvt", "Linking model…" },
                { "phase", "Setting phase…" },
                { "activate", "Opening view…" },

                // The Claude CLI's own tools (activity block step rows; see ToolCallDescriber)
                { "@read", "Reading file…" },
                { "@image", "Looking at the image…" },
                { "@shell", "Running a command…" },
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
                { "remember", "Onthouden voor dit project…" },
                { "memory", "Projectgeheugen lezen…" },
                { "forget", "Projectfeit vergeten…" },
                { "journal", "Projectjournaal lezen…" },
                { "chatlog", "Chatgeschiedenis lezen…" },
                { "diagnose", "Installatie controleren…" },
                { "approvals", "Beslissingen lezen…" },
                { "apilookup", "Revit-API opzoeken…" },

                // Wijzigen
                { "wall", "Wand maken…" },
                { "floor", "Vloer maken…" },
                { "place", "Familie plaatsen…" },
                { "placeid", "Familie plaatsen…" },
                { "profile", "Profiel opslaan…" },
                { "extrude", "Familie langs de lijn maken…" },
                { "sweep", "Profiel langs het pad sweepen…" },
                { "lwall", "L-wand maken…" },
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
                { "spot", "Peilmaat plaatsen…" },
                { "whatdepends", "Afhankelijkheden controleren…" },
                { "text", "Tekst plaatsen…" },
                { "sheetinfo", "Sheet controleren…" },
                { "crop", "View bijsnijden…" },
                { "look", "3D-camera richten…" },
                { "schedule", "Staat maken…" },
                { "callout", "Detail-uitsnede maken…" },
                { "region", "Vlak tekenen…" },
                { "export", "Exporteren…" },

                // Diagnose, brondata, kantoorstandaard
                { "health", "Verbinding controleren…" },
                { "help", "Commando's opzoeken…" },
                { "whyhidden", "Zichtbaarheid onderzoeken…" },
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
                { "linkrvt", "Model koppelen…" },
                { "phase", "Fase instellen…" },
                { "activate", "View openen…" },

                // De eigen tools van de Claude CLI (stappen in het activiteitenblok)
                { "@read", "Bestand lezen…" },
                { "@image", "Afbeelding bekijken…" },
                { "@shell", "Opdracht uitvoeren…" },
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

        /// <summary>
        /// The label of one step row in the chat pane's activity block, in the Settings language.
        /// A step is a VibeModel command ("wall") or one of the Claude CLI's own tools ("@read",
        /// "@image", "@shell", "@&lt;ToolName&gt;" — see <see cref="ToolCallDescriber"/>). Same verbs
        /// as the status lines; an unmapped step reads "name…" (the row has no room for "Bezig… (name)").
        /// </summary>
        public static string StepLabel(string command) => StepLabel(command, Strings.Current);

        public static string StepLabel(string command, string language)
        {
            command = command ?? "";
            string verb;
            if ((Strings.IsEnglish(language) ? En : Nl).TryGetValue(command, out verb))
                return verb;
            return (command.StartsWith("@") ? command.Substring(1) : command) + "…";
        }

        /// <summary>Every command with a verb, for the test that keeps both maps in step.</summary>
        internal static IEnumerable<string> EnglishCommands => En.Keys;

        internal static IEnumerable<string> DutchCommands => Nl.Keys;
    }
}
