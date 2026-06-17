using System.Collections.Generic;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// Maps a Revit command (or "revit_"-prefixed tool name) to a short, human-friendly
    /// status verb shown in the chat while the tool runs ("Creating wall…").
    /// These status lines are UI-only — they are streamed to the user via onToken but are
    /// never added to the model-bound conversation history.
    /// New commands (plans 01/04) should add an entry here so they get a status line for free.
    /// </summary>
    public static class StatusVerbs
    {
        private static readonly Dictionary<string, string> Map =
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
            };

        /// <summary>
        /// Returns a friendly status verb for a tool/command name. Accepts the "revit_" tool
        /// prefix. Falls back to "Working… (name)" for unmapped commands.
        /// </summary>
        public static string Describe(string toolName)
        {
            var command = (toolName ?? "").StartsWith("revit_")
                ? toolName.Substring(6)
                : (toolName ?? "");

            string verb;
            if (Map.TryGetValue(command, out verb))
                return verb;

            return "Working… (" + command + ")";
        }
    }
}
