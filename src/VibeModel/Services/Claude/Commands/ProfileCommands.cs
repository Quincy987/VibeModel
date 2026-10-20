using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Claude.FamilyGen;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// `profile`: a named cross-section stored as a Revit Profile family "&lt;prefix&gt;&lt;name&gt; profile"
    /// (detail lines, so Revit's own Wall Sweep / Slab Edge / Sweep tools can use it too) plus a
    /// type parameter VM_Profile with the points, which `extrude` / `sweep` read back.
    /// </summary>
    public class ProfileCommand : IClaudeCommand, IStructuredCommand, IModificationCommand, IJournaledCommand
    {
        public string Name => "profile";
        public string Description => "Named cross-section (mm; u = left of the run, v = up) as a Revit Profile family: 'profile Deksteen --points 0,0 500,0 500,250 470,280 0,280'; " +
                                     "'profile <name>' prints it, 'profile' lists them. Used by extrude/sweep";
        public string Usage => ProfileArgs.ProfileUsage;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public IList<string> JournalFiles(CommandResult result) => ProfileJournal.Files(result);

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;
            var a = ProfileArgs.ParseProfile(args);
            if (a.Error != null) return CommandResult.Error("BAD_ARGS", a.Error, "Usage: " + Usage);
            var pack = StandardsContext.GetActivePack(doc);
            var prefix = pack?.Profiles?.FamilyPrefix ?? FamilyNaming.DefaultPrefix;

            if (a.ProfileName == null) return List(doc, prefix);
            if (a.Points == null) return Show(doc, prefix, a.ProfileName);
            return Define(doc, pack, prefix, a);
        }

        private static CommandResult List(Document doc, string prefix)
        {
            var rows = ProfileStore.All(doc);
            var sb = new StringBuilder("PROFILES\n========\n");
            if (rows.Count == 0) sb.AppendLine("None yet. Define one: profile <name> --points u,v u,v u,v ...  (mm; u = left of the run direction, v = up)");
            foreach (var r in rows)
            {
                var p = ProfileGeometry.Normalize(r.Value, out _);
                sb.AppendLine("  " + r.Key + (p != null ? " — " + p.Describe() + ", " + p.Points.Count + " points" : " — (invalid outline)"));
            }
            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object> { { "profiles", rows.Select(r => (object)r.Key).ToList() } });
        }

        private static CommandResult Show(Document doc, string prefix, string name)
        {
            var pts = ProfileStore.Read(doc, prefix, name, out var err, out var shown);
            if (pts == null) return CommandResult.Error("NO_PROFILE", err, "Define it: profile " + name + " --points u,v u,v u,v ...");
            var p = ProfileGeometry.Normalize(pts, out var gerr);
            var sb = new StringBuilder("PROFILE " + shown + "\n" + new string('=', 8 + shown.Length) + "\n");
            sb.AppendLine("Points (mm, u = left of the run, v = up): " + ProfileGeometry.FormatPoints(pts));
            if (p == null) sb.AppendLine("WARN: " + gerr);
            else
            {
                sb.AppendLine("Area: " + ProfileGeometry.M2(p.AreaM2) + " m²  (per metre of run: " + ProfileGeometry.M3(p.AreaM2) + " m³)");
                sb.AppendLine("Box: u " + ProfileGeometry.F(p.MinU) + ".." + ProfileGeometry.F(p.MaxU) + ", v " + ProfileGeometry.F(p.MinV) + ".." + ProfileGeometry.F(p.MaxV) +
                              " mm (" + ProfileGeometry.F(p.Width) + " x " + ProfileGeometry.F(p.Height) + ")");
                sb.AppendLine("Centroid: u " + ProfileGeometry.F(p.CentroidU) + ", v " + ProfileGeometry.F(p.CentroidV) + " mm");
            }
            sb.AppendLine("Use: extrude " + shown + " --along <line id | x1 y1 x2 y2>  |  sweep " + shown + " --along <id,id,...>");
            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "name", shown }, { "points", ProfileGeometry.FormatPoints(pts) }, { "areaM2", p != null ? Math.Round(p.AreaM2, 4) : (object)null }
            });
        }

        private static CommandResult Define(Document doc, StandardsPack pack, string prefix, ProfileArgs a)
        {
            var prof = ProfileGeometry.Normalize(a.Points, out var gerr);
            if (prof == null) return CommandResult.Error("BAD_PROFILE", gerr, "List the points in order around the shape, mm, u = left of the run, v = up.");
            var name = FamilyNaming.Sanitize(a.ProfileName);
            var familyName = ProfileStore.FamilyName(prefix, name);
            var defaults = pack?.Profiles ?? new ProfileDefaults();
            var notes = new List<string>();

            string template;
            try { template = GeneratedFamilyStore.Template(doc.Application, "profile", pack, out var tn); if (tn != null) notes.Add(tn); }
            catch (Exception ex) { return CommandResult.Error("NO_TEMPLATE", ex.Message); }
            var text = ProfileGeometry.FormatPoints(prof.Points);
            var spec = FamilyNaming.Hash(GenKind.ProfileFamily.ToString(), System.IO.Path.GetFileName(template), text);

            var existing = GeneratedFamilyStore.FindFamily(doc, familyName);
            var stamp = GeneratedFamilyStore.ReadStamp(doc, existing);
            string structure = null;
            if (FamilyNaming.NeedsStructureCheck(existing != null, stamp, spec, a.Replace))
                structure = GeneratedFamilyStore.CurrentStructure(doc, existing, true, out _);
            var action = FamilyNaming.Decide(existing != null, stamp, spec, structure, a.Replace);
            if (action == FamilyNaming.Action.RefuseForeign)
                return CommandResult.Error("FAMILY_EXISTS", "A family '" + familyName + "' is already loaded and was not generated by VibeModel.", "Use another name, or --replace to overwrite it.");
            if (action == FamilyNaming.Action.RefuseEdited)
                return CommandResult.Error("FAMILY_EDITED", "The profile family '" + familyName + "' was edited in Revit since; redefining it would overwrite those edits.", "Add --replace to overwrite them.");
            if (action == FamilyNaming.Action.Reuse)
                return CommandResult.Ok("PROFILE " + name + "\nUnchanged — '" + familyName + "' already has these points (" + prof.Describe() + ").\n",
                    new Dictionary<string, object> { { "name", name }, { "family", familyName }, { "status", "unchanged" } });

            BuildResult built;
            try
            {
                built = GeneratedFamilyStore.WithShared(doc.Application, doc, defaults, notes, defs => FamilyBuilder.Build(doc.Application, new GenSpec
                {
                    Kind = GenKind.ProfileFamily,
                    FamilyName = familyName,
                    TypeName = name,
                    Profile = prof.Points,
                    ProfileText = text,
                    TemplatePath = template,
                    SpecHash = spec,
                    Shared = defs
                }, GeneratedFamilyStore.StagingPath(familyName)));
            }
            catch (Exception ex) { return CommandResult.Error("FAMILY_BUILD", ex.Message); }

            Family fam = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: profile " + name, () =>
            {
                fam = GeneratedFamilyStore.Load(doc, built.Path, familyName, FamilyNaming.Stamp(spec, built.StructureHash));
                // The reload keeps the project's VM_Profile too: without this every later extrude reads
                // the old points (live: "PROFILE UPDATED" 600 x 300, extrude still 500 x 250).
                ProfileStore.WritePoints(doc, fam, text);
            });
            if (error != null) return CommandResult.FromTransactionError(error, "LOAD_FAILED");

            var target = GeneratedFamilyStore.Resolve(doc, defaults);
            var file = GeneratedFamilyStore.CopyOut(built.Path, target, out var fileNote);
            var sb = new StringBuilder("PROFILE " + (action == FamilyNaming.Action.Create ? "SAVED" : "UPDATED") + "\n=============\n");
            sb.AppendLine("Name: " + name + " — " + prof.Describe() + ", " + prof.Points.Count + " points" + (prof.WasClockwise ? " (given clockwise; turned round)" : ""));
            sb.AppendLine("Points: " + text);
            sb.AppendLine("Family: " + fam.Name + " (Profiles" + (action == FamilyNaming.Action.Create ? ", new" : ", regenerated and reloaded") + ")" + (file != null ? " → " + file : ""));
            if (fileNote != null) sb.AppendLine("File: " + fileNote);
            foreach (var n in notes.Concat(built.Notes)) sb.AppendLine("Note: " + n);
            sb.AppendLine("Next: extrude " + name + " --along <line id | x1 y1 x2 y2>; Revit's Wall Sweep / Slab Edge tools list it as '" + fam.Name + "'.");
            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "name", name }, { "family", fam.Name }, { "status", action.ToString().ToLowerInvariant() }, { "points", text },
                { "areaM2", Math.Round(prof.AreaM2, 4) }, { "file", file }
            });
        }
    }

    /// <summary>`extrude`: a profile along a straight line (or standing up with --vertical) as a real family.</summary>
    public class ExtrudeCommand : IClaudeCommand, IStructuredCommand, IModificationCommand, IJournaledCommand
    {
        public string Name => "extrude";
        public string Description => "Profile along a line as a generated, schedulable family (Generic Models line-based; --category foundation|framing), " +
                                     "or --vertical <height> --at <x y>; re-running updates the family, one undo step";
        public string Usage => ProfileArgs.ExtrudeUsage;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public IList<string> JournalFiles(CommandResult result) => ProfileJournal.Files(result);

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;
            var phase = PhaseDefaults.Prepare(doc, args);
            if (phase.Error != null) return phase.ErrorResult;
            var a = ProfileArgs.ParseExtrude(phase.Remaining);
            if (a.Error != null) return CommandResult.Error("BAD_ARGS", a.Error, "Usage: " + Usage);
            return new ProfileRunner(uiApp, Name, a, phase).Run();
        }
    }

    /// <summary>`sweep`: a profile along a chain of lines/arcs (or a wall's curve), path baked into the family.</summary>
    public class SweepCommand : IClaudeCommand, IStructuredCommand, IModificationCommand, IJournaledCommand
    {
        public string Name => "sweep";
        public string Description => "Profile swept along lines/arcs (one chain) or a wall's curve as a generated Generic Model; the path is fixed in the family (run again after moving the lines)";
        public string Usage => ProfileArgs.SweepUsage;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public IList<string> JournalFiles(CommandResult result) => ProfileJournal.Files(result);

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;
            var phase = PhaseDefaults.Prepare(doc, args);
            if (phase.Error != null) return phase.ErrorResult;
            var a = ProfileArgs.ParseSweep(phase.Remaining);
            if (a.Error != null) return CommandResult.Error("BAD_ARGS", a.Error, "Usage: " + Usage);
            return new ProfileRunner(uiApp, Name, a, phase).Run();
        }
    }

    /// <summary>`lwall`: an L-shaped quay wall on one parametric family; each size combination is a type.</summary>
    public class LwallCommand : IClaudeCommand, IStructuredCommand, IModificationCommand, IJournaledCommand
    {
        public string Name => "lwall";
        public string Description => "L-shaped (quay) wall along a line: 'lwall x1 y1 x2 y2 --stem 600x4000 --foot 2500x800 [--toe 500]'; one parametric family, a type per size, " +
                                     "the line is the stem's front face, heel on --side (left)";
        public string Usage => ProfileArgs.LwallUsage;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public IList<string> JournalFiles(CommandResult result) => ProfileJournal.Files(result);

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;
            var phase = PhaseDefaults.Prepare(doc, args);
            if (phase.Error != null) return phase.ErrorResult;
            var a = ProfileArgs.ParseLwall(phase.Remaining);
            if (a.Error != null) return CommandResult.Error("BAD_ARGS", a.Error, "Usage: " + Usage);
            return new ProfileRunner(uiApp, Name, a, phase).Run();
        }
    }

    internal static class ProfileJournal
    {
        /// <summary>The .rfa a run wrote, for the journal line ("· wrote VM_L-wand.rfa").</summary>
        public static IList<string> Files(CommandResult result)
        {
            var data = result?.Data as Dictionary<string, object>;
            return data != null && data.TryGetValue("file", out var f) && f is string s && s.Length > 0 ? new List<string> { s } : new List<string>();
        }
    }

    /// <summary>Reads named profiles back from the model.</summary>
    internal static class ProfileStore
    {
        public const string Suffix = " profile";

        public static string FamilyName(string prefix, string name) => FamilyNaming.FamilyName(prefix, name) + Suffix;

        /// <summary>
        /// The points of profile <paramref name="name"/>: VM_Profile of the generated family, else the
        /// detail lines of any Profile family with that name (an office library profile).
        /// </summary>
        public static List<P2> Read(Document doc, string prefix, string name, out string error, out string shown)
        {
            error = null;
            shown = FamilyNaming.Sanitize(name);
            var profiles = ProfileFamilies(doc);
            var wanted = new[] { FamilyName(prefix, name), FamilyNaming.FamilyName(prefix, name), FamilyNaming.Sanitize(name) };
            var fam = wanted.Select(w => profiles.FirstOrDefault(f => string.Equals(f.Name, w, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(f => f != null);
            if (fam == null)
            {
                var hit = NameMatch.Find(profiles.Select(f => new NameMatch.Item(f.Id.IntegerValue, f.Name)), name, "profile family", out error);
                if (hit == null) return null;
                fam = profiles.First(f => f.Id.IntegerValue == hit.Id);
            }
            var text = PointsText(doc, fam);
            if (text != null)
            {
                var pts = ProfileGeometry.ParsePoints(text, out error);
                if (pts != null) shown = Display(fam.Name, prefix);
                return pts;
            }
            // A Profile family VibeModel did not make: its outline from the detail lines.
            Document fd = null;
            try
            {
                fd = doc.EditFamily(fam);
                var chains = new FilteredElementCollector(fd).OfClass(typeof(CurveElement)).Cast<CurveElement>()
                    .Select(c => c.GeometryCurve).Where(c => c is Line)
                    .Select(c => new List<(double X, double Y)>
                    {
                        (RevitUnitHelper.FeetToMm(c.GetEndPoint(0).X), RevitUnitHelper.FeetToMm(c.GetEndPoint(0).Y)),
                        (RevitUnitHelper.FeetToMm(c.GetEndPoint(1).X), RevitUnitHelper.FeetToMm(c.GetEndPoint(1).Y))
                    }).ToList();
                var loops = CurveChainer.ChainLoops(chains, 0.5).Loops;
                if (loops.Count != 1)
                {
                    error = "Profile family '" + fam.Name + "' has " + (loops.Count == 0 ? "no closed outline of straight lines" : loops.Count + " outlines") +
                            " (arcs in a profile come in phase 2). Define it with points: profile <name> --points ...";
                    return null;
                }
                shown = fam.Name;
                return loops[0].Select(p => new P2(p.X, p.Y)).ToList();
            }
            catch (Exception ex)
            {
                error = "Could not read profile family '" + fam.Name + "': " + ex.Message;
                return null;
            }
            finally { try { fd?.Close(false); } catch { } }
        }

        /// <summary>Generated profiles: name → points (for `profile` without arguments).</summary>
        public static List<KeyValuePair<string, List<P2>>> All(Document doc)
        {
            var list = new List<KeyValuePair<string, List<P2>>>();
            foreach (var f in ProfileFamilies(doc).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var text = PointsText(doc, f);
                if (text == null) continue;
                var pts = ProfileGeometry.ParsePoints(text, out _);
                if (pts != null) list.Add(new KeyValuePair<string, List<P2>>(Display(f.Name, null), pts));
            }
            return list;
        }

        private static string Display(string familyName, string prefix)
        {
            var n = familyName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? familyName.Substring(0, familyName.Length - Suffix.Length) : familyName;
            var p = prefix ?? FamilyNaming.DefaultPrefix;
            return p.Length > 0 && n.StartsWith(p, StringComparison.OrdinalIgnoreCase) ? n.Substring(p.Length) : n;
        }

        private static List<Family> ProfileFamilies(Document doc)
            => new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => f.FamilyCategory != null && f.FamilyCategory.Id.IntegerValue == (int)BuiltInCategory.OST_ProfileFamilies).ToList();

        private static string PointsText(Document doc, Family fam)
        {
            foreach (var id in fam.GetFamilySymbolIds())
            {
                var s = doc.GetElement(id)?.LookupParameter(FamilyBuilder.ParamProfile)?.AsString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            return null;
        }

        /// <summary>The points onto every project type of the profile family (read back by <see cref="PointsText"/>).</summary>
        public static void WritePoints(Document doc, Family fam, string text)
            => GeneratedFamilyStore.WriteTypeText(doc, fam, t => t.LookupParameter(FamilyBuilder.ParamProfile), text);
    }
}
