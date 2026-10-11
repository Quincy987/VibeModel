using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>Load an .rfa (typically a 'library' hit) and list the types it brings in.</summary>
    public class LoadFamilyCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "loadfamily";
        public string Description => "Load a family file (.rfa) into the project — e.g. a hit from 'library'";
        public string Usage => "loadfamily \"<path to .rfa>\"";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;
            var path = (args ?? "").Trim().Trim('"');
            if (path.Length == 0)
                return CommandResult.Error("BAD_ARGS", "Usage: " + Usage, "Find a family with 'library <terms>'.");
            if (!path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                return CommandResult.Error("BAD_ARGS", "Not a family file (.rfa): " + path, null);
            var exists = Standards.PathProbe.FileExists(path);
            if (exists == null)
                return CommandResult.Error("SHARE_UNREACHABLE", "The location did not answer within 3 s: " + path,
                    "The network share is probably offline (VPN?). Try again when it is reachable.");
            if (exists == false)
                return CommandResult.Error("FILE_NOT_FOUND", "File not found: " + path,
                    "Check the network drive; 'library' works from a cached index even when the share is offline, loading does not.");
            if (doc.IsFamilyDocument)
                return CommandResult.Error("BAD_ARGS", "Open a project to load families into.", null);

            var name = Path.GetFileNameWithoutExtension(path);
            var existing = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            Family family = null;
            bool loaded = false;
            var error = TransactionHelper.Execute(doc, "VibeModel: Load Family", () =>
            {
                loaded = doc.LoadFamily(path, new KeepExistingLoadOptions(), out family);
            });
            if (error != null) return CommandResult.FromTransactionError(error);
            if (family == null) family = existing;
            if (family == null)
                return CommandResult.Error("LOAD_FAILED", "Revit did not load " + path + ".", null);

            var types = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>()
                .Select(s => new { s.Id, s.Name }).OrderBy(s => s.Name).ToList();

            var sb = new StringBuilder();
            sb.AppendLine(loaded ? "FAMILY LOADED" : "FAMILY ALREADY LOADED (kept the project's version)");
            sb.AppendLine("=============");
            sb.AppendLine("Family: " + family.Name + " (id " + family.Id.IntegerValue + ")");
            sb.AppendLine("Category: " + (family.FamilyCategory?.Name ?? "?"));
            sb.AppendLine("Types (" + types.Count + "):");
            foreach (var t in types.Take(40)) sb.AppendLine("  " + t.Id.IntegerValue + "  " + t.Name);
            if (types.Count > 40) sb.AppendLine("  … +" + (types.Count - 40) + " more");
            sb.AppendLine("Place one with: placeid <typeId> <x> <y>");

            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "familyId", family.Id.IntegerValue }, { "family", family.Name }, { "loaded", loaded },
                { "types", types.Select(t => (object)new Dictionary<string, object> { { "id", t.Id.IntegerValue }, { "name", t.Name } }).ToList() }
            });
        }

        /// <summary>Never overwrite a family the project already has (its parameters may be edited).</summary>
        private sealed class KeepExistingLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = false;
                return false;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Project;
                overwriteParameterValues = false;
                return false;
            }
        }
    }
}
