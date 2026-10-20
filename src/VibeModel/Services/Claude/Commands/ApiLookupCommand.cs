using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public string Description => "Revit API signatures from THIS Revit (use before exec when unsure): a type's namespace, how to get one, properties, methods, enum values, [Obsolete]; or one member's overloads; also what this API cannot do (labels in annotation families, legends, design options, phases) and the workaround";
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

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

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

        /// <summary>The hint exec appends to its compile errors — see <see cref="ApiLookup.CompileErrorHint"/>.</summary>
        internal static string CompileErrorHint(IEnumerable<ApiLookup.CompileError> errors)
        {
            try
            {
                return Engine.CompileErrorHint(errors);
            }
            catch (Exception ex)
            {
                Logger.Warn("apilookup hint failed: " + ex.Message);
                return null;
            }
        }
    }
}
