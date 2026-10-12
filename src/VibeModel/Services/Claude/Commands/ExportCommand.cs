using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Chat;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Export sheets/views to PDF (Revit's own PDF exporter, 2022+), DWG (the office's saved
    /// export setup when the pack names one) or IFC (default setup) in one go. Files go to the
    /// project folder's \export\ unless --to says otherwise; the response lists every file
    /// written with its size, and names any target file that is locked (an open PDF) instead
    /// of failing with Revit's generic error. Changes nothing in the model.
    /// </summary>
    public class ExportCommand : IClaudeCommand, IStructuredCommand, IJournaledCommand
    {
        public string Name => "export";
        public string Description => "Export sheets/views: 'export pdf 0001,0002' | 'export dwg --all-sheets' | 'export ifc [view]'. Default folder: " +
                                     "<project folder>\\export\\ (--to to change), names \"{number} - {name}\" (--name pattern; {project} {date}); lists files + sizes";
        public string Usage => ExportArgs.Usage;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        /// <summary>The "files" paths of a successful export, for the project journal.</summary>
        public IList<string> JournalFiles(CommandResult result)
        {
            var data = result?.Data as Dictionary<string, object>;
            if (data == null || !data.TryGetValue("files", out var files) || !(files is List<object> list)) return new List<string>();
            return list.OfType<Dictionary<string, object>>()
                .Select(f => f.TryGetValue("path", out var path) ? path as string : null)
                .Where(path => !string.IsNullOrEmpty(path))
                .ToList();
        }

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;
            var a = ExportArgs.Parse(args);
            if (a.Error != null) return CommandResult.Error("BAD_ARGS", a.Error, "Usage: " + Usage);
            if (doc.IsModifiable)
                return CommandResult.Error("BAD_STATE", "Revit cannot export while a transaction is open (e.g. inside an atomic batch).", "Run 'export' on its own.");

            // ---- what to export ----
            var views = new List<View>();
            if (a.AllSheets)
            {
                views.AddRange(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => !s.IsPlaceholder && !s.IsTemplate).OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase));
                if (views.Count == 0) return CommandResult.Error("ELEMENT_NOT_FOUND", "The model has no sheets.", "Create one with 'sheet --number <nr> --name <name>'.");
            }
            else
            {
                var missing = new List<string>();
                foreach (var t in a.Targets)
                {
                    // Sheet numbers first: "0001" would otherwise be read as an element id.
                    View v = SheetHelper.FindSheet(doc, t) ?? SheetHelper.FindView(doc, t);
                    if (v == null) { missing.Add(t); continue; }
                    if (v is ViewSchedule && a.Format != ExportFormat.Pdf) { missing.Add(t + " (a schedule: export it via the sheet it is on)"); continue; }
                    if (!views.Any(x => x.Id == v.Id)) views.Add(v);
                }
                if (missing.Count > 0)
                    return CommandResult.Error("ELEMENT_NOT_FOUND", "Not found: " + string.Join(", ", missing) + ".", "Sheet numbers from 'list sheets', view ids/names from 'views'.");
            }

            // ---- where to ----
            var folderError = ResolveFolder(doc, a.To, out var folder, out var folderNote);
            if (folderError != null) return folderError;
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex) { return CommandResult.Error("FOLDER_UNAVAILABLE", "Cannot create '" + folder + "': " + ex.Message, "Give another folder with --to."); }

            // ---- names ----
            string project = Path.GetFileNameWithoutExtension(doc.Title ?? "model");
            var now = DateTime.Now;
            string ext = a.Format == ExportFormat.Pdf ? ".pdf" : a.Format == ExportFormat.Dwg ? ".dwg" : ".ifc";
            var jobs = new List<KeyValuePair<string, List<View>>>();   // file name (no extension) → views in it
            if (a.Format == ExportFormat.Ifc)
            {
                var v = views.FirstOrDefault();
                jobs.Add(new KeyValuePair<string, List<View>>(ExportNaming.Expand(a.NamePattern ?? (v == null ? "{project}" : "{project} - {name}"), "", v?.Name, project, now, "ifc"),
                    views));
            }
            else if (a.Combine)
            {
                jobs.Add(new KeyValuePair<string, List<View>>(ExportNaming.Expand(a.NamePattern ?? "{project}", "", "", project, now, "pdf"), views));
            }
            else
            {
                var names = ExportNaming.MakeUnique(views.Select(v => v is ViewSheet s
                    ? ExportNaming.Expand(a.NamePattern ?? ExportNaming.SheetPattern, s.SheetNumber, s.Name, project, now, a.Format.ToString().ToLowerInvariant())
                    : ExportNaming.Expand(a.NamePattern ?? ExportNaming.ViewPattern, "", v.Name, project, now, a.Format.ToString().ToLowerInvariant())).ToList());
                for (int i = 0; i < views.Count; i++) jobs.Add(new KeyValuePair<string, List<View>>(names[i], new List<View> { views[i] }));
            }

            // ---- DWG setup ----
            DWGExportOptions dwg = null;
            string setupNote = null;
            if (a.Format == ExportFormat.Dwg)
            {
                var setupName = a.Setup ?? StandardsContext.GetActivePack(doc)?.Export?.DwgSetup;
                if (setupName != null)
                {
                    var saved = SafeList(() => ExportDWGSettings.ListNames(doc));
                    var match = saved.FirstOrDefault(n => n.Equals(setupName, StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                    {
                        if (a.Setup != null)
                            return CommandResult.Error("ELEMENT_NOT_FOUND", "No DWG export setup '" + a.Setup + "'. Saved setups: " +
                                (saved.Count > 0 ? string.Join(", ", saved.Select(n => "'" + n + "'")) : "none") + ".", "Drop --setup to use Revit's defaults.");
                        setupNote = "office setup '" + setupName + "' is not in this model — Revit defaults used (saved: " + (saved.Count > 0 ? string.Join(", ", saved) : "none") + ")";
                    }
                    else
                    {
                        dwg = DWGExportOptions.GetPredefinedOptions(doc, match);
                        setupNote = "setup '" + match + "'" + (a.Setup != null ? " (--setup)" : " (office default)");
                    }
                }
                if (dwg == null)
                {
                    // One self-contained DWG per sheet instead of a sheet + one xref per viewport.
                    dwg = new DWGExportOptions { MergedViews = true };
                    setupNote = setupNote ?? "Revit defaults, views merged into the sheet (no xrefs); --setup <name> or the pack's export.dwgSetup to change";
                }
            }

            // ---- export ----
            var before = Snapshot(folder);
            var lines = new List<string>();
            var results = new List<object>();
            int ok = 0;
            foreach (var job in jobs)
            {
                string file = Path.Combine(folder, job.Key + ext);
                string label = job.Value.Count == 1 ? Describe(job.Value[0]) : job.Value.Count == 0 ? "the whole model" : job.Value.Count + " sheets/views";
                if (IsLocked(file))
                {
                    lines.Add("LOCKED " + Path.GetFileName(file) + " — open in another program (a PDF viewer?); close it and export again (" + label + " skipped)");
                    results.Add(new Dictionary<string, object> { { "file", file }, { "locked", true }, { "ok", false } });
                    continue;
                }
                string err = null;
                try
                {
                    bool done = Run(doc, a, folder, job.Key, job.Value, dwg);
                    if (!done) err = "Revit reported the export as failed";
                }
                catch (Exception ex) { err = ex.Message; }
                if (err == null && !File.Exists(file)) err = "Revit wrote no " + Path.GetFileName(file);
                if (err != null)
                {
                    if (IsLocked(file)) err = Path.GetFileName(file) + " is locked by another program (an open PDF?) — close it and export again";
                    lines.Add("ERROR " + label + ": " + err);
                    results.Add(new Dictionary<string, object> { { "file", file }, { "ok", false }, { "error", err } });
                    continue;
                }
                ok++;
                lines.Add("OK    " + label + " → " + Path.GetFileName(file));
                var result = new Dictionary<string, object> { { "file", file }, { "ok", true }, { "views", job.Value.Select(v => (object)v.Id.IntegerValue).ToList() } };
                if (a.Format == ExportFormat.Ifc) lines.AddRange(DescribeIfc(doc, file, job.Value.FirstOrDefault(), result));
                results.Add(result);
            }

            // Every file the export wrote or rewrote (DWG xrefs and IFC side files included).
            var written = Changed(before, Snapshot(folder));

            var sb = new StringBuilder();
            sb.AppendLine("EXPORT " + a.Format.ToString().ToUpperInvariant() + " (" + ok + "/" + jobs.Count + " file" + (jobs.Count == 1 ? "" : "s") + ")");
            sb.AppendLine("=".PadRight(40, '='));
            sb.AppendLine("Folder: " + folder + (folderNote != null ? "  " + folderNote : ""));
            if (setupNote != null) sb.AppendLine("DWG: " + setupNote);
            if (a.Format == ExportFormat.Ifc) sb.AppendLine("IFC: default setup (" + (views.Count > 0 ? "elements visible in '" + views[0].Name + "'" : "whole model") + ")");
            foreach (var l in lines) sb.AppendLine(l);
            sb.AppendLine("Files written:");
            if (written.Count == 0) sb.AppendLine("  (none)");
            foreach (var w in written) sb.AppendLine("  " + Path.GetFileName(w.Key) + "  " + ExportNaming.Size(w.Value));

            var data = new Dictionary<string, object>
            {
                { "format", a.Format.ToString().ToLowerInvariant() },
                { "folder", folder },
                { "results", results },
                { "files", written.Select(w => (object)new Dictionary<string, object> { { "path", w.Key }, { "sizeBytes", w.Value } }).ToList() }
            };
            if (ok == 0)
                return CommandResult.Error("EXPORT_FAILED", sb.ToString().TrimEnd(), lines.Any(l => l.StartsWith("LOCKED")) ? "Close the open file(s) and run the export again." : null);
            return CommandResult.Ok(sb.ToString(), data);
        }

        private static bool Run(Document doc, ExportArgs a, string folder, string name, List<View> views, DWGExportOptions dwg)
        {
            var ids = views.Select(v => v.Id).ToList();
            switch (a.Format)
            {
                case ExportFormat.Pdf:
                    var pdf = new PDFExportOptions
                    {
                        FileName = name,
                        Combine = true,                 // one file per job; per-sheet jobs hold one sheet
                        PaperFormat = ExportPaperFormat.Default,   // the sheet's own size
                        ZoomType = ZoomType.Zoom,
                        ZoomPercentage = 100,
                        HideCropBoundaries = true,
                        HideScopeBoxes = true,
                        HideReferencePlane = true,
                        HideUnreferencedViewTags = true,
                        ColorDepth = ColorDepthType.Color
                    };
                    return doc.Export(folder, ids, pdf);
                case ExportFormat.Dwg:
                    return doc.Export(folder, name, ids, dwg);
                default:
                    var ifc = new IFCExportOptions();
                    if (ids.Count > 0) ifc.FilterViewId = ids[0];
                    // The IFC exporter needs an open transaction; nothing it does is kept.
                    using (var t = new Transaction(doc, ModelChangeTracker.TransactionPrefix + "IFC export (rolled back)"))
                    {
                        t.Start();
                        try { return doc.Export(folder, name, ifc); }
                        finally { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); }
                    }
            }
        }

        // Model categories the IFC exporter never writes as elements of their own (or that are
        // not building parts at all); leaving them out of the scope keeps "Not in the file" honest.
        // Built on first use: a static initializer touching RevitAPI types breaks the type for the
        // Revit-less unit tests (and command discovery) that only construct the command.
        private static HashSet<int> _notIfcElements;
        private static HashSet<int> NotIfcElements => _notIfcElements ?? (_notIfcElements = new HashSet<int>(new[]
        {
            BuiltInCategory.OST_Lines, BuiltInCategory.OST_Cameras, BuiltInCategory.OST_RvtLinks, BuiltInCategory.OST_PointClouds,
            BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Areas, BuiltInCategory.OST_MEPSpaces, BuiltInCategory.OST_Topography,
            BuiltInCategory.OST_SectionBox, BuiltInCategory.OST_IOSModelGroups, BuiltInCategory.OST_Assemblies,
            BuiltInCategory.OST_Viewers, BuiltInCategory.OST_DetailComponents, BuiltInCategory.OST_ProjectBasePoint,
            BuiltInCategory.OST_SharedBasePoint, BuiltInCategory.OST_VolumeOfInterest, BuiltInCategory.OST_Levels,
            BuiltInCategory.OST_Grids, BuiltInCategory.OST_CLines, BuiltInCategory.OST_Matchline,
            BuiltInCategory.OST_SketchLines, BuiltInCategory.OST_Sheets, BuiltInCategory.OST_Reveals,
            // openings are cut into their host, never exported on their own
            BuiltInCategory.OST_ShaftOpening, BuiltInCategory.OST_SWallRectOpening, BuiltInCategory.OST_ArcWallRectOpening,
            BuiltInCategory.OST_FloorOpening, BuiltInCategory.OST_RoofOpening, BuiltInCategory.OST_ColumnOpening,
            BuiltInCategory.OST_StructuralFramingOpening
        }.Select(c => (int)c)));

        /// <summary>
        /// Reads the IFC back: per category how many elements went in and as which IFC class, and
        /// which categories in scope (the view's visible model elements, or every non-demolished
        /// model element) are missing — so "the piles are not in it" can be told apart from
        /// "the piles are in it as IfcSlab".
        /// </summary>
        private static List<string> DescribeIfc(Document doc, string file, View view, Dictionary<string, object> result)
        {
            try
            {
                var contents = IfcContents.Read(file);
                var collector = view != null ? new FilteredElementCollector(doc, view.Id) : new FilteredElementCollector(doc);
                var scope = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in collector.WhereElementIsNotElementType())
                {
                    var cat = e.Category;
                    if (cat == null || cat.CategoryType != CategoryType.Model || e.ViewSpecific) continue;
                    if (e is ImportInstance || e is RevitLinkInstance || e is View || e is BasePoint || e is InternalOrigin
                        || NotIfcElements.Contains(cat.Id.IntegerValue)) continue;
                    if (e.get_BoundingBox(null) == null) continue;
                    if (view == null)
                    {
                        var demolished = e.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.AsElementId();
                        if (demolished != null && demolished != ElementId.InvalidElementId) continue;
                    }
                    if (!scope.TryGetValue(cat.Name, out var ids)) scope[cat.Name] = ids = new List<int>();
                    ids.Add(e.Id.IntegerValue);
                }
                Func<int, string> categoryOf = id => doc.GetElement(new ElementId(id))?.Category?.Name;
                var exported = new HashSet<int>(contents.Items.Select(x => x.RevitId));
                result["ifc"] = new Dictionary<string, object>
                {
                    { "elements", contents.Items.Count },
                    { "byCategory", contents.Items.GroupBy(x => categoryOf(x.RevitId) ?? "(unknown)").OrderByDescending(g => g.Count())
                        .Select(g => (object)new Dictionary<string, object>
                        {
                            { "category", g.Key },
                            { "count", g.Count() },
                            { "classes", g.GroupBy(x => x.IfcClass + (x.Predefined != null ? "." + x.Predefined : ""))
                                .ToDictionary(h => h.Key, h => (object)h.Count()) }
                        }).ToList() },
                    { "inScope", scope.Sum(kv => kv.Value.Count) },
                    { "missing", scope.Where(kv => kv.Value.Any(id => !exported.Contains(id)))
                        .ToDictionary(kv => kv.Key, kv => (object)kv.Value.Count(id => !exported.Contains(id))) }
                };
                var lines = contents.Describe(categoryOf, scope);
                // Revit's default class table (exportlayers-ifc-IAI.txt) maps Structural Foundations
                // to IfcSlab BASESLAB, so pile families arrive as slabs unless their type says otherwise.
                int foundationSlabs = contents.Items.GroupBy(x => x.RevitId).Select(g => g.First()).Count(x => x.IfcClass == "IfcSlab" &&
                    doc.GetElement(new ElementId(x.RevitId)) is FamilyInstance fi &&
                    fi.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_StructuralFoundation);
                if (foundationSlabs > 0)
                    lines.Add("  Note: " + foundationSlabs + " Structural Foundation family instance" + (foundationSlabs == 1 ? " was" : "s were") +
                              " exported as IfcSlab (Revit's default IFC class for that category) — piles are in the file, but not as IfcPile/IfcFooting. " +
                              "To change that, set the type parameter 'Export Type to IFC As' (e.g. IfcPile, predefined DRIVEN) or remap the category in the IFC export classes.");
                return lines;
            }
            catch (Exception ex)
            {
                return new List<string> { "IFC contents: could not read the file back (" + ex.Message + ")" };
            }
        }

        private static string Describe(View v) => v is ViewSheet s ? "sheet " + SheetHelper.Label(s) : "view '" + v.Name + "' (" + v.Id.IntegerValue + ")";

        /// <summary>--to (absolute, or relative to the project folder), else &lt;project folder&gt;\export.</summary>
        private static CommandResult ResolveFolder(Document doc, string to, out string folder, out string note)
        {
            folder = null; note = null;
            var pf = ProjectDocs.ResolveForDocument(doc.Title, doc.PathName);
            if (to != null && Path.IsPathRooted(to)) { folder = Path.GetFullPath(to); return null; }
            if (!pf.IsSet)
                return CommandResult.Error("NO_PROJECT_FOLDER", "No project folder to export into: the model is not saved (or a cloud model) and none is configured.",
                    "Give a folder with --to \"C:\\path\\export\", or pick a project folder in the chat pane → Settings.");
            folder = Path.GetFullPath(Path.Combine(pf.Path, to ?? "export"));
            note = "(project folder " + pf.SourceLabel + (to == null ? " + \\export" : "") + ")";
            return null;
        }

        private static List<string> SafeList(Func<IList<string>> f)
        {
            try { return (f() ?? new List<string>()).ToList(); }
            catch { return new List<string>(); }
        }

        /// <summary>An existing file another program holds open (Acrobat keeps its PDFs locked).</summary>
        internal static bool IsLocked(string path)
        {
            if (!File.Exists(path)) return false;
            try { using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        private static Dictionary<string, Tuple<long, DateTime>> Snapshot(string folder)
        {
            var d = new Dictionary<string, Tuple<long, DateTime>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in new DirectoryInfo(folder).GetFiles())
                    d[f.FullName] = Tuple.Create(f.Length, f.LastWriteTimeUtc);
            }
            catch { /* unreadable folder: nothing to compare */ }
            return d;
        }

        private static List<KeyValuePair<string, long>> Changed(Dictionary<string, Tuple<long, DateTime>> before, Dictionary<string, Tuple<long, DateTime>> after)
            => after.Where(kv => !before.TryGetValue(kv.Key, out var b) || b.Item1 != kv.Value.Item1 || b.Item2 != kv.Value.Item2)
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new KeyValuePair<string, long>(kv.Key, kv.Value.Item1)).ToList();
    }
}
