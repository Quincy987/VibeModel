using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        public ClaudeCommandRegistry()
        {
            DiscoverCommands();
        }

        public string Execute(string command, string args, UIApplication uiApp)
        {
            if (_commands.TryGetValue(command, out var cmd))
            {
                // Guard: most commands need an open document
                if (!NoDocRequired.Contains(command) && uiApp.ActiveUIDocument?.Document == null)
                    return "ERROR: No document open";

                try
                {
                    return cmd.Execute(args, uiApp);
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                {
                    // Revit-specific: document closing, view switching, etc.
                    Logger.Error("Revit operation failed for '" + command + "'", ex);
                    return "ERROR: Revit operation failed: " + ex.Message;
                }
                catch (Exception ex)
                {
                    Logger.Error("Command '" + command + "' failed", ex);
                    return "ERROR: Command '" + command + "' failed: " + ex.Message;
                }
            }

            return "ERROR: Unknown command '" + command + "'\n\nType 'help' for available commands.";
        }

        public IReadOnlyDictionary<string, IClaudeCommand> GetCommands()
        {
            return _commands;
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
