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
                try
                {
                    return cmd.Execute(args, uiApp);
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
            var commandTypes = Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(t => typeof(IClaudeCommand).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

            foreach (var type in commandTypes)
            {
                try
                {
                    var instance = (IClaudeCommand)Activator.CreateInstance(type);

                    // Special case: HelpCommand needs the registry reference
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
