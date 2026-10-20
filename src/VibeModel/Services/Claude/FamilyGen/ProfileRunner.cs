using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using VibeModel.Services.Claude.Commands;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.FamilyGen
{
    /// <summary>
    /// The shared body of `extrude`, `sweep` and `lwall` (plan 12): resolve the profile, decide
    /// whether the family must be generated (new / reused / updated / refused), build it, then load
    /// + type + place + phase + joins as ONE undo step, copy the .rfa to the project folder and
    /// report what was used.
    /// </summary>
    internal sealed class ProfileRunner
    {
        private readonly UIApplication _uiApp;
        private readonly Document _doc;
        private readonly string _command;
        private readonly ProfileArgs _a;
        private readonly PhaseRequest _phase;
        private readonly StandardsPack _pack;
        private readonly ProfileDefaults _defaults;
        private readonly List<string> _notes = new List<string>();

        public ProfileRunner(UIApplication uiApp, string command, ProfileArgs a, PhaseRequest phase)
        {
            _uiApp = uiApp;
            _doc = uiApp.ActiveUIDocument.Document;
            _command = command;
            _a = a;
            _phase = phase;
            _pack = StandardsContext.GetActivePack(_doc);
            _defaults = _pack?.Profiles ?? new ProfileDefaults();
        }

        private string Prefix => _defaults.FamilyPrefix ?? FamilyNaming.DefaultPrefix;
        private static double Ft(double mm) => RevitUnitHelper.MmToFeet(mm);
        private static double Mm(double ft) => RevitUnitHelper.FeetToMm(ft);

        // ================================================================== plan

        private sealed class Plan
        {
            public GenKind Kind;
            public string KindLabel;          // "Generic Models, line-based"
            public string FamilyName;
            public string TypeName;
            public string ProfileLabel;       // the profile's name for responses
            public Profile2D Profile;         // as placed (flip applied); null for the parametric L
            public LwallSizes Sizes;
            public string TemplateKey;
            public string TemplatePath;
            public Curve Run;                 // the placement line (feet, z = 0), or the sweep chain's first curve
            public List<Curve> Chain;         // sweep: the curves in travel order (feet, z = 0)
            public double ChainLengthMm;
            public string RunNote;
            public XYZ At;                    // vertical: insertion point (feet, z = 0)
            public string MaterialName;       // requested (flag or pack)
            public string MaterialSource;
        }

        public CommandResult Run()
        {
            var plan = new Plan();
            var err = _command == "lwall" ? PlanLwall(plan) : PlanProfile(plan);
            if (err != null) return err;

            // ---- level ----
            Level level;
            if (_a.Level != null)
            {
                level = WallCommand.FindLevel(new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().ToList(), _a.Level, out var levelErr);
                if (level == null) return CommandResult.Error("BAD_ARGS", levelErr);
            }
            else level = FormattingHelper.GetPreferredLevel(_doc, _uiApp.ActiveUIDocument);
            if (level == null) return CommandResult.Error("NO_LEVEL", "No level found in the document.");

            // ---- the family: new / reuse / regenerate / refuse ----
            var existing = GeneratedFamilyStore.FindFamily(_doc, plan.FamilyName);
            string templatePath;
            try { templatePath = GeneratedFamilyStore.Template(_doc.Application, plan.TemplateKey, _pack, out var tnote); if (tnote != null) _notes.Add(tnote); }
            catch (Exception ex) { return CommandResult.Error("NO_TEMPLATE", ex.Message); }
            plan.TemplatePath = templatePath;
            var specHash = SpecHash(plan, templatePath);
            var stamp = GeneratedFamilyStore.ReadStamp(_doc, existing);
            string structure = null;
            if (FamilyNaming.NeedsStructureCheck(existing != null, stamp, specHash, _a.Replace))
            {
                structure = GeneratedFamilyStore.CurrentStructure(_doc, existing, plan.Kind != GenKind.LwallLineBased && plan.Kind != GenKind.LwallFoundation, out var openErr);
                if (structure == null) _notes.Add("could not open the loaded family to check for edits (" + openErr + ")");
            }
            var action = FamilyNaming.Decide(existing != null, stamp, specHash, structure, _a.Replace);
            if (action == FamilyNaming.Action.RefuseForeign)
                return CommandResult.Error("FAMILY_EXISTS",
                    "A family '" + plan.FamilyName + "' is already loaded and was not generated by VibeModel.",
                    "Pick another name (--name / another profile name), or add --replace to overwrite it with the generated family.");
            if (action == FamilyNaming.Action.RefuseEdited)
                return CommandResult.Error("FAMILY_EDITED",
                    "The family '" + plan.FamilyName + "' was edited in Revit since VibeModel generated it; regenerating would overwrite those edits.",
                    "Add --replace to overwrite the edits, or use another profile name for the new shape.");

            // ---- generate (outside the project transaction; the family document has its own) ----
            BuildResult built = null;
            bool generate = action == FamilyNaming.Action.Create || action == FamilyNaming.Action.Regenerate;
            if (generate)
            {
                try
                {
                    var spec = new GenSpec
                    {
                        Kind = plan.Kind,
                        FamilyName = plan.FamilyName,
                        TypeName = plan.TypeName,
                        Profile = plan.Profile?.Points,
                        Sizes = plan.Sizes,
                        VerticalHeight = _a.VerticalHeight,
                        TemplatePath = templatePath,
                        SpecHash = specHash,
                        Path = plan.Chain == null ? null : RelativeChain(plan.Chain)
                    };
                    built = GeneratedFamilyStore.WithShared(_doc.Application, _doc, _defaults, _notes, defs =>
                    {
                        spec.Shared = defs;
                        return FamilyBuilder.Build(_doc.Application, spec, GeneratedFamilyStore.StagingPath(plan.FamilyName));
                    });
                    _notes.AddRange(built.Notes);
                }
                catch (Exception ex)
                {
                    return CommandResult.Error("FAMILY_BUILD", ex.Message,
                        plan.Kind == GenKind.Sweep ? "Check that the path is one connected chain without sharp reversals and that the profile fits around the tightest bend."
                                                   : "Check the profile (no tiny edges); 'profile <name>' prints its points.");
                }
            }

            // ---- material ----
            Material material = null;
            if (plan.MaterialName != null)
            {
                var mats = new FilteredElementCollector(_doc).OfClass(typeof(Material)).Cast<Material>().ToList();
                var hit = NameMatch.Find(mats.Select(m => new NameMatch.Item(m.Id.IntegerValue, m.Name)), plan.MaterialName, "material", out var merr);
                if (hit == null)
                {
                    if (plan.MaterialSource == "--material") return CommandResult.Error("BAD_ARGS", merr);
                    _notes.Add("office material '" + plan.MaterialName + "' not in this model — material left as it was");
                }
                else material = mats.First(m => m.Id.IntegerValue == hit.Id);
            }

            // ---- load + type + place (one undo step with the joins) ----
            Family fam = existing;
            FamilySymbol symbol = null;
            FamilyInstance inst = null;
            bool movedExisting = false;
            string typeStatus = null, zNote = null;
            var setNotes = new List<string>();
            var joined = new List<string>();
            var joinRefused = new List<string>();
            string joinRollback = null;

            using (var tg = new TransactionGroup(_doc, ModelChangeTracker.TransactionPrefix + _command + " " + plan.FamilyName))
            {
                tg.Start();
                var error = TransactionHelper.Execute(_doc, "VibeModel: " + _command, () =>
                {
                    if (generate)
                    {
                        fam = GeneratedFamilyStore.Load(_doc, built.Path, plan.FamilyName, FamilyNaming.Stamp(specHash, built.StructureHash));
                    }
                    symbol = TypeFor(fam, plan, out typeStatus);
                    if (material != null) SetMaterial(symbol, material, plan);
                    if (!symbol.IsActive) { symbol.Activate(); _doc.Regenerate(); }

                    if (plan.Kind == GenKind.Sweep && action == FamilyNaming.Action.Regenerate)
                    {
                        // The path is baked into the family: the existing instance follows the new chain start.
                        var old = new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                            .FirstOrDefault(i => i.Symbol.Family.Id == fam.Id);
                        if (old != null && old.Location is LocationPoint lp)
                        {
                            lp.Point = new XYZ(plan.Run.GetEndPoint(0).X, plan.Run.GetEndPoint(0).Y, lp.Point.Z);
                            inst = old;
                            movedExisting = true;
                        }
                    }
                    if (inst == null)
                    {
                        inst = Place(plan, symbol, level);
                        _phase?.Apply(inst);
                    }
                    _doc.Regenerate();
                    zNote = FixZ(inst, ExpectedMinZ(plan, level));
                    if (plan.Kind == GenKind.Sweep) SetLength(inst, plan.ChainLengthMm);

                    foreach (var kv in _a.Sets)
                    {
                        var p = ParamWriter.Lookup(inst, kv.Key);
                        if (p == null)
                        {
                            var names = ParamWriter.WritableNames(inst);
                            throw new InvalidOperationException("No instance parameter '" + kv.Key + "' on " + plan.FamilyName + ". Available: " +
                                                                string.Join(", ", names.Take(60)) + ". Nothing was placed.");
                        }
                        ParamWriter.Set(_doc, p, p.Definition.Name, kv.Value);
                        setNotes.Add(p.Definition.Name + " = " + FormattingHelper.GetParameterValue(p));
                    }
                });
                if (error != null)
                {
                    tg.RollBack();
                    return CommandResult.FromTransactionError(error, "PLACE_FAILED", "Nothing was changed. 'profile <name>' shows the profile; check the line and the level.");
                }

                var candidates = !_a.NoJoin && !movedExisting ? JoinCandidates(inst) : new List<Element>();
                if (candidates.Count > 0)
                {
                    // Its own transaction: a join Revit refuses at commit must not undo the element.
                    var jerr = TransactionHelper.Execute(_doc, "VibeModel: " + _command + " join", () => Join(inst, candidates, joined, joinRefused));
                    if (jerr != null)
                    {
                        joinRollback = jerr.StartsWith("ERROR: ", StringComparison.Ordinal) ? jerr.Substring(7) : jerr;
                        joined.Clear();
                    }
                }
                tg.Assimilate();
            }

            // ---- the file next to the project's other data ----
            string filePath = null, fileNote = null;
            var target = GeneratedFamilyStore.Resolve(_doc, _defaults);
            if (built != null) filePath = GeneratedFamilyStore.CopyOut(built.Path, target, out fileNote);
            else
            {
                var expected = System.IO.Path.Combine(target.Directory, plan.FamilyName + ".rfa");
                if (Standards.PathProbe.FileExists(expected) == true) filePath = expected;
            }

            return Report(plan, action, built, fam, symbol, inst, movedExisting, typeStatus, material, level, zNote, setNotes,
                          joined, joinRefused, joinRollback, filePath, fileNote, target);
        }

        // ================================================================== planning

        private CommandResult PlanProfile(Plan plan)
        {
            // ---- the profile ----
            List<P2> points;
            string profileName;
            if (_a.Points != null)
            {
                points = _a.Points;
                profileName = _a.Name ?? ("Profile " + FamilyNaming.Hash(ProfileGeometry.FormatPoints(points)).Substring(0, 6));
            }
            else
            {
                points = ProfileStore.Read(_doc, Prefix, _a.ProfileName, out var perr, out profileName);
                if (points == null) return CommandResult.Error("NO_PROFILE", perr, "Define it first: profile " + _a.ProfileName + " --points u,v u,v u,v ...  ('profile' lists the profiles).");
            }
            var prof = ProfileGeometry.Normalize(points, out var gerr);
            if (prof == null) return CommandResult.Error("BAD_PROFILE", "Profile '" + profileName + "': " + gerr);
            if (_a.Flip) prof = ProfileGeometry.Normalize(ProfileGeometry.Mirror(prof.Points), out gerr);
            plan.Profile = prof;
            plan.ProfileLabel = profileName;
            plan.TypeName = FamilyNaming.Sanitize(_a.TypeName ?? profileName);

            // ---- material ----
            plan.MaterialName = _a.Material ?? _defaults.MaterialFor(profileName, _command);
            plan.MaterialSource = _a.Material != null ? "--material" : "office pack";

            if (_command == "sweep") return PlanSweep(plan, profileName);

            if (_a.Vertical)
            {
                plan.Kind = GenKind.Vertical;
                plan.KindLabel = "Generic Models, standing on its insertion point";
                plan.TemplateKey = "generic";
                plan.FamilyName = FamilyNaming.FamilyName(Prefix, profileName) + " vertical";
                if (_a.TypeName == null) plan.TypeName = FamilyNaming.Sanitize(profileName + " " + ProfileGeometry.F(_a.VerticalHeight));
                plan.At = new XYZ(Ft(_a.At[0]), Ft(_a.At[1]), 0);
                return null;
            }

            var category = _a.Category ?? ProfileArgs.ParseCategoryName(_defaults.CategoryFor(profileName, "extrude")) ?? ProfileCategory.Generic;
            ApplyCategory(plan, category, FamilyNaming.FamilyName(Prefix, profileName));
            var run = RunLine(out var runErr);
            if (run == null) return CommandResult.Error("BAD_ARGS", runErr);
            plan.Run = run;
            return null;
        }

        private void ApplyCategory(Plan plan, ProfileCategory category, string baseName)
        {
            switch (category)
            {
                case ProfileCategory.Foundation:
                    plan.Kind = GenKind.Foundation;
                    plan.KindLabel = "Structural Foundations, point-based with Wall length";
                    plan.TemplateKey = "foundation";
                    plan.FamilyName = baseName + " SF";
                    break;
                case ProfileCategory.Framing:
                    plan.Kind = GenKind.Framing;
                    plan.KindLabel = "Structural Framing (a beam: analytical member, the section hangs from the top at the line)";
                    plan.TemplateKey = "framing";
                    plan.FamilyName = baseName + " beam";
                    break;
                default:
                    plan.Kind = GenKind.LineBased;
                    plan.KindLabel = "Generic Models, line-based";
                    plan.TemplateKey = "lineBased";
                    plan.FamilyName = baseName;
                    break;
            }
        }

        private CommandResult PlanSweep(Plan plan, string profileName)
        {
            plan.Kind = GenKind.Sweep;
            plan.KindLabel = "Generic Models, swept along a fixed path";
            plan.TemplateKey = "generic";
            plan.FamilyName = _a.Name != null && _a.Points == null
                ? FamilyNaming.FamilyName(Prefix, _a.Name)
                : FamilyNaming.FamilyName(Prefix, profileName) + " sweep " + _a.AlongIds[0];

            var curves = new List<Curve>();
            var pieces = new List<PathChain.Piece>();
            foreach (var id in _a.AlongIds)
            {
                var e = _doc.GetElement(new ElementId(id));
                if (e == null) return CommandResult.Error("BAD_ARGS", "--along: element " + id + " not found.");
                Curve c = e is CurveElement ce ? ce.GeometryCurve : (e.Location as LocationCurve)?.Curve;
                if (c == null || !c.IsBound) return CommandResult.Error("BAD_ARGS", "--along: element " + id + " (" + (e.Category?.Name ?? e.GetType().Name) + ") is not a bounded line or arc.");
                if (!(c is Line) && !(c is Arc)) return CommandResult.Error("BAD_ARGS", "--along: element " + id + " is a " + c.GetType().Name + "; sweep follows lines and arcs.");
                c = Flatten(c);
                curves.Add(c);
                var a = c.GetEndPoint(0); var b = c.GetEndPoint(1);
                pieces.Add(new PathChain.Piece(Mm(a.X), Mm(a.Y), Mm(b.X), Mm(b.Y)));
            }
            var order = PathChain.Order(pieces, 1.0, out var cerr);
            if (order == null) return CommandResult.Error("BAD_ARGS", cerr, "Give the ids in any order, but they must touch end to end.");
            plan.Chain = order.Select(o => o.Value ? curves[o.Key].CreateReversed() : curves[o.Key]).ToList();
            plan.Run = plan.Chain[0];
            plan.ChainLengthMm = plan.Chain.Sum(c => Mm(c.Length));
            plan.RunNote = string.Join(" + ", order.Select(o => (curves[o.Key] is Arc ? "arc " : "line ") + _a.AlongIds[o.Key] + (o.Value ? " (reversed)" : "")));
            return null;
        }

        private CommandResult PlanLwall(Plan plan)
        {
            var s = _a.Sizes;
            plan.Sizes = s;
            plan.ProfileLabel = "L";
            plan.TypeName = FamilyNaming.Sanitize(_a.TypeName ?? FamilyNaming.LwallTypeName(_defaults.TypeName, s));
            plan.MaterialName = _a.Material ?? _defaults.MaterialFor("lwall");
            plan.MaterialSource = _a.Material != null ? "--material" : "office pack";
            var category = _a.Category ?? ProfileArgs.ParseCategoryName(_defaults.CategoryFor("lwall")) ?? ProfileCategory.Generic;
            if (category == ProfileCategory.Framing) category = ProfileCategory.Generic;

            var run = RunLine(out var runErr);
            if (run == null) return CommandResult.Error("BAD_ARGS", runErr);
            // The heel lies left of the line; --side right walks the line the other way round.
            if (_a.RightSide) run = run.CreateReversed();
            plan.Run = run;

            if (s.Slope.HasValue)
            {
                // A sloped foot is not parametric yet (plan 12 §2.2): a fixed profile per size.
                var prof = ProfileGeometry.Normalize(ProfileGeometry.LwallPolygon(s), out var gerr);
                if (prof == null) return CommandResult.Error("BAD_PROFILE", gerr);
                plan.Profile = prof;
                ApplyCategory(plan, category, FamilyNaming.FamilyName(Prefix, "L-wand " + plan.TypeName));
                _notes.Add("sloped foot: built as a fixed profile (its sizes are not type parameters) — a new size makes a new family");
                return null;
            }
            plan.Profile = ProfileGeometry.Normalize(ProfileGeometry.LwallPolygon(s), out _);
            if (category == ProfileCategory.Foundation)
            {
                plan.Kind = GenKind.LwallFoundation;
                plan.KindLabel = "Structural Foundations, point-based with Wall length, sizes as type parameters";
                plan.TemplateKey = "foundation";
                plan.FamilyName = FamilyNaming.FamilyName(Prefix, "L-wand") + " SF";
            }
            else
            {
                plan.Kind = GenKind.LwallLineBased;
                plan.KindLabel = "Generic Models, line-based, sizes as type parameters";
                plan.TemplateKey = "lineBased";
                plan.FamilyName = FamilyNaming.FamilyName(Prefix, "L-wand");
            }
            return null;
        }

        /// <summary>The straight run from x1 y1 x2 y2 or a line/wall/beam id (feet, flattened to z = 0).</summary>
        private Curve RunLine(out string error)
        {
            error = null;
            if (_a.Line != null)
            {
                var l = _a.Line;
                _runNote = "(" + ProfileGeometry.F(l[0]) + ", " + ProfileGeometry.F(l[1]) + ") → (" + ProfileGeometry.F(l[2]) + ", " + ProfileGeometry.F(l[3]) + ")";
                return Line.CreateBound(new XYZ(Ft(l[0]), Ft(l[1]), 0), new XYZ(Ft(l[2]), Ft(l[3]), 0));
            }
            int id = _a.AlongIds[0];
            var e = _doc.GetElement(new ElementId(id));
            if (e == null) { error = "--along: element " + id + " not found."; return null; }
            Curve c = e is CurveElement ce ? ce.GeometryCurve : (e.Location as LocationCurve)?.Curve;
            if (c == null) { error = "--along: element " + id + " (" + (e.Category?.Name ?? e.GetType().Name) + ") has no line. Give a detail/model line, wall or beam id — or x1 y1 x2 y2."; return null; }
            if (!(c is Line)) { error = "--along: element " + id + " is " + (c is Arc ? "an arc" : "a " + c.GetType().Name) + "; " + _command + " runs along a straight line. Use 'sweep <profile> --along " + id + "' for curves."; return null; }
            var what = e is DetailCurve ? "detail line" : e is ModelCurve ? "model line" : (e.Category?.Name ?? e.GetType().Name);
            _runNote = what + " " + id;
            return Flatten(c);
        }

        private string _runNote;

        private static Curve Flatten(Curve c)
        {
            var a = c.GetEndPoint(0); var b = c.GetEndPoint(1);
            if (c is Line) return Line.CreateBound(new XYZ(a.X, a.Y, 0), new XYZ(b.X, b.Y, 0));
            var m = c.Evaluate(0.5, true);
            return Arc.Create(new XYZ(a.X, a.Y, 0), new XYZ(b.X, b.Y, 0), new XYZ(m.X, m.Y, 0));
        }

        /// <summary>The chain moved so its start is the family origin.</summary>
        private static List<Curve> RelativeChain(List<Curve> chain)
        {
            var start = chain[0].GetEndPoint(0);
            var move = Transform.CreateTranslation(new XYZ(-start.X, -start.Y, 0));
            return chain.Select(c => c.CreateTransformed(move)).ToList();
        }

        private string SpecHash(Plan plan, string templatePath)
        {
            var template = System.IO.Path.GetFileName(templatePath ?? "");
            switch (plan.Kind)
            {
                case GenKind.LwallLineBased:
                case GenKind.LwallFoundation:
                    return FamilyNaming.Hash(plan.Kind.ToString(), template);
                case GenKind.Sweep:
                    var path = string.Join(";", plan.Chain.Select(c => P(c.GetEndPoint(0)) + "-" + P(c.Evaluate(0.5, true)) + "-" + P(c.GetEndPoint(1))));
                    return FamilyNaming.Hash(plan.Kind.ToString(), template, ProfileGeometry.FormatPoints(plan.Profile.Points), path);
                default:
                    return FamilyNaming.Hash(plan.Kind.ToString(), template, ProfileGeometry.FormatPoints(plan.Profile.Points));
            }
        }

        private static string P(XYZ p) => Math.Round(Mm(p.X)) + "," + Math.Round(Mm(p.Y));

        // ================================================================== inside the transaction

        /// <summary>The project type for this run: an existing one by name (the L's sizes must match), else a duplicate.</summary>
        private FamilySymbol TypeFor(Family fam, Plan plan, out string status)
        {
            var symbols = fam.GetFamilySymbolIds().Select(id => _doc.GetElement(id) as FamilySymbol).Where(s => s != null).ToList();
            if (symbols.Count == 0) throw new InvalidOperationException("The family '" + fam.Name + "' has no type.");
            var hit = symbols.FirstOrDefault(s => string.Equals(s.Name, plan.TypeName, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                if (plan.Sizes != null && plan.Kind != GenKind.LineBased && plan.Kind != GenKind.Foundation && plan.Kind != GenKind.Framing)
                {
                    var off = SizeMismatch(hit, plan.Sizes);
                    if (off != null)
                        throw new InvalidOperationException("Type '" + hit.Name + "' already exists with other sizes (" + off + ") — it was changed in Revit. " +
                                                            "Give another --type name, or change that type's sizes back. Nothing was placed.");
                }
                status = "existing type";
                return hit;
            }
            var dup = symbols[0].Duplicate(plan.TypeName) as FamilySymbol;
            if (plan.Sizes != null && (plan.Kind == GenKind.LwallLineBased || plan.Kind == GenKind.LwallFoundation)) SetSizes(dup, plan.Sizes);
            if (plan.Kind == GenKind.Vertical) Length(dup, FamilyBuilder.ParamHeight)?.Set(Ft(_a.VerticalHeight));
            status = "new type";
            return dup;
        }

        private static Parameter Length(Element e, string name)
        {
            var p = e.LookupParameter(name);
            return p != null && p.StorageType == StorageType.Double && !p.IsReadOnly ? p : null;
        }

        private static void SetSizes(FamilySymbol s, LwallSizes z)
        {
            void Set(string n, double mm)
            {
                var p = Length(s, n) ?? throw new InvalidOperationException("Type '" + s.Name + "' has no parameter '" + n + "'.");
                p.Set(Ft(mm));
            }
            Set(FamilyBuilder.ParamStem, z.Stem);
            Set(FamilyBuilder.ParamHeight, z.Height);
            Set(FamilyBuilder.ParamFoot, z.Foot);
            Set(FamilyBuilder.ParamFootThickness, z.FootThickness);
            Set(FamilyBuilder.ParamToe, z.Toe);
        }

        /// <summary>"Foot width 2400 ≠ 2500" for every size that differs by more than 0.5 mm; null when equal.</summary>
        private static string SizeMismatch(FamilySymbol s, LwallSizes z)
        {
            var off = new List<string>();
            void Check(string n, double mm)
            {
                var p = s.LookupParameter(n);
                if (p == null) { off.Add(n + " missing"); return; }
                var have = Mm(p.AsDouble());
                if (Math.Abs(have - mm) > 0.5) off.Add(n + " " + ProfileGeometry.F(have) + " ≠ " + ProfileGeometry.F(mm));
            }
            Check(FamilyBuilder.ParamStem, z.Stem);
            Check(FamilyBuilder.ParamHeight, z.Height);
            Check(FamilyBuilder.ParamFoot, z.Foot);
            Check(FamilyBuilder.ParamFootThickness, z.FootThickness);
            Check(FamilyBuilder.ParamToe, z.Toe);
            return off.Count == 0 ? null : string.Join(", ", off);
        }

        private string _materialNote;

        private void SetMaterial(FamilySymbol s, Material m, Plan plan)
        {
            var p = s.LookupParameter(plan.Kind == GenKind.Framing ? "Structural Material" : FamilyBuilder.ParamMaterial)
                    ?? s.LookupParameter(FamilyBuilder.ParamMaterial);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.ElementId) { _materialNote = "the type has no Material parameter"; return; }
            if (p.AsElementId() == m.Id) return;
            bool hadOther = p.AsElementId() != ElementId.InvalidElementId;
            p.Set(m.Id);
            if (hadOther) _materialNote = "type material changed to " + m.Name + " (a type parameter: every instance of '" + s.Name + "' follows)";
        }

        private FamilyInstance Place(Plan plan, FamilySymbol symbol, Level level)
        {
            double z = level.Elevation + Ft(_a.Z);
            switch (plan.Kind)
            {
                case GenKind.LineBased:
                case GenKind.LwallLineBased:
                case GenKind.Framing:
                {
                    double lift = plan.Kind == GenKind.Framing ? Ft(plan.Profile.MaxV) : 0;
                    var a = plan.Run.GetEndPoint(0); var b = plan.Run.GetEndPoint(1);
                    var line = Line.CreateBound(new XYZ(a.X, a.Y, z + lift), new XYZ(b.X, b.Y, z + lift));
                    return _doc.Create.NewFamilyInstance(line, symbol, level, plan.Kind == GenKind.Framing ? StructuralType.Beam : StructuralType.NonStructural);
                }
                case GenKind.Foundation:
                case GenKind.LwallFoundation:
                {
                    var a = plan.Run.GetEndPoint(0); var b = plan.Run.GetEndPoint(1);
                    var p = new XYZ(a.X, a.Y, z);
                    var fi = _doc.Create.NewFamilyInstance(p, symbol, level, StructuralType.Footing);
                    var wl = fi.LookupParameter(FamilyBuilder.ParamWallLength) ?? throw new InvalidOperationException("The foundation family has no 'Wall length'.");
                    wl.Set(plan.Run.Length);
                    double angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
                    if (Math.Abs(angle) > 1e-9) ElementTransformUtils.RotateElement(_doc, fi.Id, Line.CreateBound(p, p + XYZ.BasisZ), angle);
                    return fi;
                }
                case GenKind.Vertical:
                {
                    var p = new XYZ(plan.At.X, plan.At.Y, z);
                    var fi = _doc.Create.NewFamilyInstance(p, symbol, level, StructuralType.NonStructural);
                    if (Math.Abs(_a.Rotate) > 1e-9) ElementTransformUtils.RotateElement(_doc, fi.Id, Line.CreateBound(p, p + XYZ.BasisZ), _a.Rotate * Math.PI / 180);
                    return fi;
                }
                case GenKind.Sweep:
                {
                    var s = plan.Run.GetEndPoint(0);
                    return _doc.Create.NewFamilyInstance(new XYZ(s.X, s.Y, z), symbol, level, StructuralType.NonStructural);
                }
            }
            throw new InvalidOperationException("Unknown kind " + plan.Kind);
        }

        private double ExpectedMinZ(Plan plan, Level level)
        {
            double minV = plan.Kind == GenKind.Vertical ? 0 : plan.Profile?.MinV ?? 0;
            return level.Elevation + Ft(_a.Z + minV);
        }

        /// <summary>
        /// Bring the instance's underside to where the profile says (level + --z + its lowest v)
        /// through whichever offset parameter moves it; null when it already is.
        /// </summary>
        private string FixZ(FamilyInstance inst, double wantMinZ)
        {
            var bb = inst.get_BoundingBox(null);
            if (bb == null) return null;
            double diff = wantMinZ - bb.Min.Z;
            if (Math.Abs(diff) < Ft(1)) return null;
            var candidates = new List<Parameter>();
            foreach (var bip in new[] { BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM,
                                        BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION })
            {
                var p = inst.get_Parameter(bip);
                if (p != null) candidates.Add(p);
            }
            foreach (var p in candidates)
            {
                if (p.IsReadOnly || p.StorageType != StorageType.Double) continue;
                double old = p.AsDouble();
                try
                {
                    if (!p.Set(old + diff)) continue;
                    _doc.Regenerate();
                    var now = inst.get_BoundingBox(null);
                    if (now != null && Math.Abs(now.Min.Z - wantMinZ) < Ft(1))
                        return "z: underside at " + ProfileGeometry.F(Mm(wantMinZ)) + " mm via '" + p.Definition.Name + "'";
                    p.Set(old);
                    _doc.Regenerate();
                }
                catch { try { p.Set(old); } catch { } }
            }
            return "WARN z: the underside stays at " + ProfileGeometry.F(Mm(bb.Min.Z)) + " mm, not " + ProfileGeometry.F(Mm(wantMinZ)) + " mm.";
        }

        private static void SetLength(FamilyInstance inst, double mm)
        {
            var p = inst.get_Parameter(GeneratedFamilyStore.LengthGuid);
            if (p != null && !p.IsReadOnly) p.Set(Ft(mm));
        }

        /// <summary>
        /// Join with what the new element really overlaps: walls, floors and other generated
        /// families (never library families such as piles — that would cut their heads).
        /// </summary>
        private List<Element> JoinCandidates(FamilyInstance inst)
        {
            var cats = new ElementMulticategoryFilter(new List<BuiltInCategory>
            {
                BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_StructuralFoundation
            });
            return new FilteredElementCollector(_doc).WherePasses(cats).WherePasses(new ElementIntersectsElementFilter(inst))
                .Where(e => e.Id != inst.Id)
                .Where(e => !(e is FamilyInstance fi) || GeneratedFamilyStore.ReadStamp(_doc, fi.Symbol.Family) != null)
                .Take(20).ToList();
        }

        private void Join(FamilyInstance inst, List<Element> candidates, List<string> joined, List<string> refused)
        {
            foreach (var e in candidates)
            {
                var label = (e.Category?.Name ?? "element") + " " + e.Id.IntegerValue;
                try
                {
                    if (!JoinGeometryUtils.AreElementsJoined(_doc, inst, e)) JoinGeometryUtils.JoinGeometry(_doc, inst, e);
                    bool weCut = JoinGeometryUtils.IsCuttingElementInJoin(_doc, inst, e);
                    joined.Add(label + (weCut ? " (the new element cuts it)" : " (it cuts the new element)"));
                }
                catch (Exception ex) { refused.Add(label + ": " + ex.Message); }
            }
        }

        // ================================================================== response

        private CommandResult Report(Plan plan, FamilyNaming.Action action, BuildResult built, Family fam, FamilySymbol symbol, FamilyInstance inst,
            bool movedExisting, string typeStatus, Material material, Level level, string zNote, List<string> setNotes,
            List<string> joined, List<string> joinRefused, string joinRollback, string filePath, string fileNote, GeneratedFamilyStore.Target target)
        {
            // No Regenerate here: the transactions are committed (a commit regenerates) and a
            // Regenerate outside a transaction throws "Modification of the document is forbidden".
            string familyStatus;
            switch (action)
            {
                case FamilyNaming.Action.Create: familyStatus = "new"; break;
                case FamilyNaming.Action.Reuse: familyStatus = "reused, not regenerated"; break;
                default:
                    familyStatus = _a.Replace ? "replaced (--replace)" : "updated: regenerated and reloaded, project types and their values kept, existing instances follow";
                    break;
            }
            var volP = inst.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
            double? volume = volP != null && volP.HasValue ? RevitUnitHelper.CuFeetToM3(volP.AsDouble()) : (double?)null;
            double lengthMm = plan.Kind == GenKind.Sweep ? plan.ChainLengthMm : plan.Run != null ? Mm(plan.Run.Length) : 0;
            var bb = inst.get_BoundingBox(null);

            // A reused family is not opened (speed), so a hand edit since the generation is invisible
            // to the stamp (live: an extra extrusion was placed as "reused"). The volume gives it away:
            // a straight run must measure profile area × length (a joined element may have lost some).
            string reuseNote = null;
            if (action == FamilyNaming.Action.Reuse && volume.HasValue && plan.Profile != null && plan.Kind != GenKind.Sweep && joined.Count == 0)
            {
                double runM = (plan.Kind == GenKind.Vertical ? _a.VerticalHeight : lengthMm) / 1000.0;
                double expected = plan.Profile.AreaM2 * runM;
                if (expected > 0 && Math.Abs(volume.Value - expected) > 0.01 * expected + 0.0005)
                    reuseNote = "WARN: volume " + ProfileGeometry.M3(volume.Value) + " m³, but the profile gives " + ProfileGeometry.M3(expected) +
                                " m³ — the loaded family differs from what VibeModel generated (edited in Revit?). Add --replace to regenerate it.";
            }

            string title = _command == "lwall" ? "L-WALL PLACED" : _command == "sweep" ? "SWEPT" : "EXTRUDED";
            var sb = new StringBuilder();
            sb.AppendLine(title);
            sb.AppendLine(new string('=', title.Length));
            sb.AppendLine("Family: " + fam.Name + " (" + familyStatus + ", " + plan.KindLabel + ")" + (filePath != null ? " → " + filePath : ""));
            if (built != null) sb.AppendLine("  generated in " + built.Milliseconds + " ms from " + System.IO.Path.GetFileName(plan.TemplatePath));

            var typeLine = "Type: " + symbol.Name + " (" + typeStatus + ")";
            if (plan.Profile != null)
                typeLine += " — profile " + plan.Profile.Describe() + (plan.ProfileLabel != null && plan.ProfileLabel != "L" ? " ('" + plan.ProfileLabel + "')" : "");
            sb.AppendLine(typeLine);
            if (plan.Sizes != null)
            {
                var s = plan.Sizes;
                sb.AppendLine("  stem " + ProfileGeometry.F(s.Stem) + " x " + ProfileGeometry.F(s.Height) + " mm (total height), foot " + ProfileGeometry.F(s.Foot) + " x " + ProfileGeometry.F(s.FootThickness) + " mm" +
                              (s.Toe > 0 ? ", toe " + ProfileGeometry.F(s.Toe) + " mm in front of the stem" : "") +
                              (s.Slope.HasValue ? ", foot top sloping to " + ProfileGeometry.F(s.Slope.Value) + " mm at the back" : ""));
            }
            if (plan.Kind != GenKind.Vertical && plan.Run != null)
            {
                var a = plan.Run.GetEndPoint(0); var b = plan.Run.GetEndPoint(1);
                var side = Compass(-(b.Y - a.Y), b.X - a.X);
                sb.AppendLine("  " + (_command == "lwall" ? "heel" : "profile +u") + " on the " + (_command == "lwall" && _a.RightSide ? "right" : "left") + " (" + side + ") side of the line" +
                              (_command == "lwall" ? "; the line is the stem's front face" : ""));
            }

            string where;
            if (plan.Kind == GenKind.Vertical)
                where = "at (" + ProfileGeometry.F(_a.At[0]) + ", " + ProfileGeometry.F(_a.At[1]) + ")" + (Math.Abs(_a.Rotate) > 1e-9 ? ", rotated " + ProfileGeometry.F(_a.Rotate) + "°" : "") +
                        ", height " + ProfileGeometry.F(_a.VerticalHeight) + " mm";
            else if (plan.Kind == GenKind.Sweep)
                where = "along " + plan.RunNote + ", path " + ProfileGeometry.F(lengthMm) + " mm" + (movedExisting ? " (existing instance moved to the new path start)" : "");
            else
                where = "along " + (_runNote ?? "") + (_runNote != null && _runNote.StartsWith("(", StringComparison.Ordinal) ? "" : " " + LinePts(plan.Run)) + ", length " + ProfileGeometry.F(lengthMm) + " mm";
            sb.AppendLine("Instance: " + inst.Id.IntegerValue + " " + where + (volume.HasValue ? ", volume " + ProfileGeometry.M3(volume.Value) + " m³" : ""));
            if (bb != null) sb.AppendLine("  underside " + ProfileGeometry.F(Mm(bb.Min.Z)) + " mm, top " + ProfileGeometry.F(Mm(bb.Max.Z)) + " mm (level " + level.Name + (Math.Abs(_a.Z) > 1e-9 ? " + " + ProfileGeometry.F(_a.Z) : "") + ")");
            if (zNote != null) sb.AppendLine("  " + zNote);
            if (reuseNote != null) sb.AppendLine("  " + reuseNote);

            var matP = symbol.LookupParameter(plan.Kind == GenKind.Framing ? "Structural Material" : FamilyBuilder.ParamMaterial);
            var matName = matP != null && matP.AsElementId() != ElementId.InvalidElementId ? _doc.GetElement(matP.AsElementId())?.Name : "<By Category>";
            sb.AppendLine("Material: " + matName + (material != null && plan.MaterialSource == "office pack" ? " (office pack)" : "") +
                          (material == null && plan.MaterialName == null && matName == "<By Category>" ? " (--material <name> to set it)" : ""));
            if (_materialNote != null) sb.AppendLine("  " + _materialNote);
            foreach (var s in setNotes) sb.AppendLine("Set: " + s);
            if (!movedExisting) _phase?.AppendNote(sb, _doc, _uiApp.ActiveUIDocument.ActiveView);

            if (_a.NoJoin) sb.AppendLine("Joined: — (--no-join)");
            else if (movedExisting) sb.AppendLine("Joined: unchanged (existing instance)");
            else if (joinRollback != null) sb.AppendLine("Joined: none — Revit refused the joins: " + joinRollback);
            else sb.AppendLine("Joined: " + (joined.Count == 0 ? "nothing (no wall, floor or generated element overlaps it)" : string.Join("; ", joined)));
            foreach (var r in joinRefused) sb.AppendLine("  not joined: " + r);

            if (fileNote != null) sb.AppendLine("File: " + fileNote);
            else if (filePath == null && built == null) sb.AppendLine("File: not regenerated (the family is unchanged; it lives in the model)");
            foreach (var n in _notes) sb.AppendLine("Note: " + n);
            if (plan.Kind == GenKind.Sweep)
                sb.AppendLine("Note: the path is fixed in the family — move the line(s) and run 'sweep' again to update it.");

            var scheduleCat = plan.Kind == GenKind.Foundation || plan.Kind == GenKind.LwallFoundation ? "structural foundations"
                            : plan.Kind == GenKind.Framing ? "structural framing" : "generic models";
            // 'section <id>' takes wall ids only (live): give a line across the element instead.
            sb.AppendLine("Next: 'section x1 y1 x2 y2 --depth 1500' across " + inst.Id.IntegerValue + " to check the profile; 'schedule " + scheduleCat +
                          " --fields \"Family and Type,Count," + GeneratedFamilyStore.LengthName + ",Volume\" --group Family and Type --total'.");
            sb.AppendLine("One undo step (Ctrl+Z removes the element" + (built != null ? " and the family load" : "") + ").");

            var data = new Dictionary<string, object>
            {
                { "family", fam.Name },
                { "familyStatus", action.ToString().ToLowerInvariant() },
                { "category", plan.KindLabel },
                { "type", symbol.Name },
                { "typeStatus", typeStatus },
                { "instance", inst.Id.IntegerValue },
                { "lengthMm", Math.Round(lengthMm, 1) },
                { "volumeM3", volume.HasValue ? Math.Round(volume.Value, 3) : (object)null },
                { "material", matName },
                { "file", filePath },
                { "joined", joined.Cast<object>().ToList() },
                { "phase", _phase?.ToJson() }
            };
            if (plan.Profile != null) data["profile"] = new Dictionary<string, object> { { "points", ProfileGeometry.FormatPoints(plan.Profile.Points) }, { "areaM2", Math.Round(plan.Profile.AreaM2, 4) } };
            return CommandResult.Ok(sb.ToString(), data);
        }

        private static string LinePts(Curve c)
            => "(" + ProfileGeometry.F(Mm(c.GetEndPoint(0).X)) + ", " + ProfileGeometry.F(Mm(c.GetEndPoint(0).Y)) + ") → (" +
               ProfileGeometry.F(Mm(c.GetEndPoint(1).X)) + ", " + ProfileGeometry.F(Mm(c.GetEndPoint(1).Y)) + ")";

        /// <summary>"north-west" for a plan direction (x east, y north).</summary>
        internal static string Compass(double dx, double dy)
        {
            var names = new[] { "east", "north-east", "north", "north-west", "west", "south-west", "south", "south-east" };
            double deg = Math.Atan2(dy, dx) * 180 / Math.PI;
            int i = (int)Math.Round(AlongLayout.Normalize(deg) / 45) % 8;
            return names[i];
        }
    }
}
