using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Auto-discovers and routes commands implementing IClaudeCommand.
    /// Adding a new command = one new file. Zero changes to infrastructure.
    /// </summary>
    public class ClaudeCommandRegistry
    {
        private static readonly HashSet<string> NoDocRequired =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "help", "health", "chatlog" };

        private readonly Dictionary<string, IClaudeCommand> _commands =
            new Dictionary<string, IClaudeCommand>(StringComparer.OrdinalIgnoreCase);

        private readonly JavaScriptSerializer _json =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public ClaudeCommandRegistry()
        {
            DiscoverCommands();
        }

        /// <summary>
        /// The single place a command runs and its errors are caught. Returns the structured
        /// result UN-rendered so callers can both inspect it (batch: Assimilate vs RollBack on
        /// .Success) and render text or JSON as needed.
        /// </summary>
        public CommandResult ExecuteCore(string command, string args, UIApplication uiApp)
        {
            if (!_commands.TryGetValue(command, out var cmd))
                return CommandResult.Error("UNKNOWN_COMMAND",
                    "Unknown command '" + command + "'", "Run 'help' to list available commands.");

            // Guard: most commands need an open document
            if (!NoDocRequired.Contains(command) && uiApp.ActiveUIDocument?.Document == null)
                return CommandResult.Error("NO_DOCUMENT",
                    "No document open", "Open a Revit project before running commands.");

            try
            {
                if (cmd is IStructuredCommand sc)
                    return sc.ExecuteStructured(args, uiApp);

                // Legacy string command — wrap (text preserved verbatim).
                return CommandResult.Legacy(cmd.Execute(args, uiApp));
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                // Revit-specific: document closing, view switching, etc.
                Logger.Error("Revit operation failed for '" + command + "'", ex);
                return CommandResult.Error("REVIT_ERROR", "Revit operation failed: " + ex.Message,
                    "Revit may be mid-operation (modal dialog/view switch); retry in a moment.");
            }
            catch (Exception ex)
            {
                Logger.Error("Command '" + command + "' failed", ex);
                return CommandResult.Error("INTERNAL", "Command '" + command + "' failed: " + ex.Message, null);
            }
        }

        public string Execute(string command, string args, UIApplication uiApp, ResponseFormat fmt)
        {
            return ExecuteCore(command, args, uiApp).Render(fmt, _json);
        }

        // Back-compat: existing callers default to text.
        public string Execute(string command, string args, UIApplication uiApp)
        {
            return Execute(command, args, uiApp, ResponseFormat.Text);
        }

        public IReadOnlyDictionary<string, IClaudeCommand> GetCommands()
        {
            return _commands;
        }

        /// <summary>
        /// True if the command modifies the document (marked with IModificationCommand).
        /// Used by batch grouping to decide whether the group has anything worth assimilating.
        /// </summary>
        public bool IsModification(string command)
        {
            return _commands.TryGetValue(command, out var c) && c is IModificationCommand;
        }

        private void DiscoverCommands()
        {
            Type[] allTypes;
            try
            {
                allTypes = Assembly.GetExecutingAssembly().GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Some types may fail to load (e.g. missing dependency DLLs).
                // Use the types that did load successfully.
                allTypes = ex.Types.Where(t => t != null).ToArray();
                foreach (var loaderEx in ex.LoaderExceptions?.Distinct() ?? Enumerable.Empty<Exception>())
                    Logger.Warn("Type load warning: " + loaderEx.Message);
            }

            var commandTypes = allTypes
                .Where(t => typeof(IClaudeCommand).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

            foreach (var type in commandTypes)
            {
                try
                {
                    var instance = (IClaudeCommand)Activator.CreateInstance(type);

                    if (instance is Commands.HelpCommand helpCmd)
                    {
                        helpCmd.SetRegistry(this);
                    }

                    _commands[instance.Name] = instance;
                    Logger.Info("Registered command: " + instance.Name);
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to register command type: " + type.Name, ex);
                }
            }

            Logger.Info("Command registry initialized with " + _commands.Count + " commands");
        }
    }
}
