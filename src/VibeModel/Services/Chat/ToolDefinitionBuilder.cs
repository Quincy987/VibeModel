using System;
using System.Collections.Generic;
using System.Linq;
using VibeModel.Services.Claude;

namespace VibeModel.Services.Chat
{
    public enum ToolFormat
    {
        Anthropic,
        OpenAI
    }

    public static class ToolDefinitionBuilder
    {
        private static readonly HashSet<string> SkipCommands =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "help", "health", "chatlog" };

        public static object[] Build(IReadOnlyDictionary<string, IClaudeCommand> commands, ToolFormat format)
        {
            var tools = new List<object>();

            foreach (var cmd in commands.Values.OrderBy(c => c.Name))
            {
                if (SkipCommands.Contains(cmd.Name))
                    continue;

                if (format == ToolFormat.Anthropic)
                    tools.Add(BuildAnthropicTool(cmd));
                else
                    tools.Add(BuildOpenAITool(cmd));
            }

            return tools.ToArray();
        }

        private static Dictionary<string, object> BuildAnthropicTool(IClaudeCommand cmd)
        {
            var toolName = "revit_" + cmd.Name;
            var description = cmd.Description;
            if (!string.IsNullOrEmpty(cmd.Usage))
                description += "\nUsage: " + cmd.Usage;

            var inputSchema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", new Dictionary<string, object>
                    {
                        { "args", new Dictionary<string, object>
                            {
                                { "type", "string" },
                                { "description", "Arguments to pass to the command. " +
                                    (!string.IsNullOrEmpty(cmd.Usage) ? "Format: " + cmd.Usage : "Leave empty if no arguments needed.") }
                            }
                        }
                    }
                },
                { "required", new string[0] }
            };

            return new Dictionary<string, object>
            {
                { "name", toolName },
                { "description", description },
                { "input_schema", inputSchema }
            };
        }

        private static Dictionary<string, object> BuildOpenAITool(IClaudeCommand cmd)
        {
            var toolName = "revit_" + cmd.Name;
            var description = cmd.Description;
            if (!string.IsNullOrEmpty(cmd.Usage))
                description += "\nUsage: " + cmd.Usage;

            var parameters = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", new Dictionary<string, object>
                    {
                        { "args", new Dictionary<string, object>
                            {
                                { "type", "string" },
                                { "description", "Arguments to pass to the command. " +
                                    (!string.IsNullOrEmpty(cmd.Usage) ? "Format: " + cmd.Usage : "Leave empty if no arguments needed.") }
                            }
                        }
                    }
                },
                { "required", new string[0] }
            };

            return new Dictionary<string, object>
            {
                { "type", "function" },
                { "function", new Dictionary<string, object>
                    {
                        { "name", toolName },
                        { "description", description },
                        { "parameters", parameters }
                    }
                }
            };
        }
    }
}
