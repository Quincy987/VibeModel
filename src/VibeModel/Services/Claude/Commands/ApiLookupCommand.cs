using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Read-only (plan 16): Revit API signatures from the running Revit — the year the model is
    /// open in — for writing <c>exec</c> code without guessing. See <see cref="ApiLookup"/>.
    /// </summary>
    public class ApiLookupCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "apilookup";
        public string Description => "Revit API signatures from THIS Revit (use before exec when unsure): a type's namespace, how to get one, properties, methods, enum values, [Obsolete]; or one member's overloads";
        public string Usage => "apilookup <Type>[.<Member>] [--filter <text>] [--all]";

        private static readonly Lazy<ApiLookup> Shared = new Lazy<ApiLookup>(() =>
        {
            var sw = Stopwatch.StartNew();
            var engine = new ApiLookup(
                new[] { typeof(Document).Assembly, typeof(UIApplication).Assembly, typeof(ApiLookupCommand).Assembly },
                "Autodesk.Revit.DB", "Autodesk.Revit.UI", "Autodesk.Revit.DB.Structure", "Autodesk.Revit.DB.Architecture",
                "Autodesk.Revit.DB.Mechanical", "Autodesk.Revit.DB.Plumbing", "Autodesk.Revit.DB.Electrical",
                "Autodesk.Revit.ApplicationServices", "Autodesk.Revit.UI.Selection", "VibeModel.Services.Helpers");
            Logger.Info("apilookup: type index built in " + sw.ElapsedMilliseconds + " ms");
            return engine;
        });

        /// <summary>The shared engine (also used by exec's compile-error hint).</summary>
        internal static ApiLookup Engine => Shared.Value;

        public string Execute(string args, UIApplication uiApp)
        {
            var r = ExecuteStructured(args, uiApp);
            return r.Success ? r.Text : "ERROR: " + r.Message;
        }

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var q = ApiLookup.ParseArgs(args);
            if (q.Error != null)
                return CommandResult.Error("BAD_ARGS", q.Error, "Usage: " + Usage);
            var sw = Stopwatch.StartNew();
            var result = Engine.Lookup(q.Name, q.Filter, q.All);
            if (sw.ElapsedMilliseconds > 200) Logger.Info("apilookup " + args + ": " + sw.ElapsedMilliseconds + " ms");
            // "not found" is an answer (with the closest names), not a failure.
            return CommandResult.Ok(result.Text, result.Data);
        }

        /// <summary>
        /// The hint exec appends to its compile errors (plan 16): for each missing member or type
        /// (at most two), "Hint: apilookup Wall.Foo" and the first lines of that lookup — the
        /// closest member/type names or the real signatures — so the next attempt is right.
        /// </summary>
        internal static string CompileErrorHint(IEnumerable<string> errors)
        {
            try
            {
                var lookups = ApiLookup.LookupsForCompileErrors(errors);
                if (lookups.Count == 0) return null;
                var sb = new StringBuilder();
                foreach (var name in lookups.Take(2))
                {
                    var r = Engine.Lookup(name, null, false);
                    sb.AppendLine("Hint: apilookup " + name);
                    foreach (var line in r.Text.Replace("\r\n", "\n").Split('\n').Take(20))
                        sb.AppendLine("  " + line);
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                Logger.Warn("apilookup hint failed: " + ex.Message);
                return null;
            }
        }
    }
}
