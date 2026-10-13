using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.FamilyGen
{
    /// <summary>What a generated family is (plan 12 §1–§2).</summary>
    internal enum GenKind
    {
        LineBased,        // fixed profile, line-based Generic Model: the length follows the placement line
        Foundation,       // fixed profile, point-based Structural Foundation with an instance "Wall length"
        Framing,          // fixed profile as the section of a Structural Framing (beam) family
        Vertical,         // plan profile extruded upward, point-based Generic Model, type parameter Height
        Sweep,            // profile swept along a baked path, point-based Generic Model
        ProfileFamily,    // a Revit Profile family (detail lines) + VM_Profile with the points
        LwallLineBased,   // the parametric L, line-based Generic Model, sizes as type parameters
        LwallFoundation   // the parametric L, point-based Structural Foundation
    }

    /// <summary>Everything the builder needs; units mm unless stated.</summary>
    internal sealed class GenSpec
    {
        public GenKind Kind;
        public string FamilyName;
        public string TypeName;                 // the family's own type: named as the project type will be
        public List<P2> Profile;                // (u, v), counter-clockwise; Vertical: plan (x, y) around the insertion point
        public List<Curve> Path;                // Sweep: feet, family coordinates (chain start at the origin), z = 0
        public LwallSizes Sizes;                // Lwall*: the values of the family's own type
        public double VerticalHeight;           // Vertical: the family's own type's Height
        public string TemplatePath;
        public string SpecHash;
        public SharedDefinitions Shared;        // VM_Length / VM_Source / office object code; null → none added
        public string ProfileText;              // ProfileFamily: the VM_Profile value

        public bool IsParametric => Kind == GenKind.LwallLineBased || Kind == GenKind.LwallFoundation;
    }

    /// <summary>The definitions taken from the shared-parameter file for one build.</summary>
    internal sealed class SharedDefinitions
    {
        public ExternalDefinition Length;       // instance, = the run's length
        public ExternalDefinition Source;       // type, hidden: the VM_Source stamp
        public ExternalDefinition ObjectCode;   // instance, the office's object code (only when the project does not bind it)
    }

    /// <summary>What a build produced.</summary>
    internal sealed class BuildResult
    {
        public string Path;                     // the saved .rfa (staging)
        public string StructureHash;
        public List<string> Notes = new List<string>();
        public long Milliseconds;
    }

    /// <summary>
    /// Generates a family document from a <see cref="GenSpec"/> and saves it. Revit recipe from
    /// the plan 12 probes (§1.3): face references from geometry computed without a view,
    /// regenerate after new reference planes, labelled dimensions + locked alignments, the family
    /// document is always closed. Runs before (or inside) the project transaction — the family
    /// document's own transactions never reach the project's undo stack.
    /// </summary>
    internal static class FamilyBuilder
    {
        public const string ParamMaterial = "Material";
        public const string ParamStem = "Stem width";
        public const string ParamHeight = "Height";
        public const string ParamFoot = "Foot width";
        public const string ParamFootThickness = "Foot thickness";
        public const string ParamToe = "Toe";
        public const string ParamHeel = "Heel";
        public const string ParamWallLength = "Wall length";
        public const string ParamProfile = "VM_Profile";

        private static double Ft(double mm) => RevitUnitHelper.MmToFeet(mm);

        public static BuildResult Build(Application app, GenSpec spec, string savePath)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new BuildResult { Path = savePath };
            Document fd = app.NewFamilyDocument(spec.TemplatePath);
            if (fd == null) throw new InvalidOperationException("Revit could not create a family from the template " + spec.TemplatePath + ".");
            string step = "start";
            try
            {
                using (var t = new Transaction(fd, "VibeModel: generate " + spec.FamilyName))
                {
                    TransactionHelper.Attach(t);
                    t.Start();
                    var forms = new List<GenericForm>();
                    step = "geometry";
                    switch (spec.Kind)
                    {
                        case GenKind.LineBased: forms.Add(LineBased(fd, spec, result)); break;
                        case GenKind.Foundation: forms.Add(Foundation(fd, spec, result)); break;
                        case GenKind.Framing: forms.Add(Framing(fd, spec, result)); break;
                        case GenKind.Vertical: forms.Add(Vertical(fd, spec)); break;
                        case GenKind.Sweep: forms.Add(SweepAlong(app, fd, spec)); break;
                        case GenKind.ProfileFamily: ProfileFamily(fd, spec); break;
                        case GenKind.LwallLineBased:
                        case GenKind.LwallFoundation: forms.AddRange(ParametricL(fd, spec, result)); break;
                    }

                    step = "parameters";
                    var fm = fd.FamilyManager;
                    if (fm.CurrentType == null) fm.NewType(spec.TypeName);
                    else if (fm.CurrentType.Name != spec.TypeName) fm.RenameCurrentType(spec.TypeName);

                    if (forms.Count > 0 && spec.Kind != GenKind.Framing)
                    {
                        var mat = fm.get_Parameter(ParamMaterial) ?? fm.AddParameter(ParamMaterial, GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
                        foreach (var f in forms)
                        {
                            var p = f.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                            if (p != null) fm.AssociateElementParameterToFamilyParameter(p, mat);
                        }
                    }
                    AddShared(fd, spec, result);
                    fd.Regenerate();

                    step = "stamp";
                    result.StructureHash = StructureHash(fd, !spec.IsParametric);
                    if (spec.Shared?.Source != null)
                    {
                        var src = fm.get_Parameter(spec.Shared.Source.GUID);
                        if (src != null) fm.Set(src, FamilyNaming.Stamp(spec.SpecHash, result.StructureHash));
                    }
                    step = "commit";
                    var err = TransactionHelper.CommitOrReport(t, null);
                    if (err != null) throw new InvalidOperationException(err);
                }

                step = "save";
                fd.SaveAs(savePath, new SaveAsOptions { OverwriteExistingFile = true });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Generating family '" + spec.FamilyName + "' failed at " + step + ": " + ex.Message, ex);
            }
            finally
            {
                try { fd.Close(false); } catch { /* already closed */ }
            }
            result.Milliseconds = sw.ElapsedMilliseconds;
            return result;
        }

        // ------------------------------------------------------------------ fixed profiles

        /// <summary>Line-based Generic Model: profile in the plane x = 0, both ends locked to Left/Right.</summary>
        private static GenericForm LineBased(Document fd, GenSpec spec, BuildResult result)
        {
            var planes = Planes(fd);
            var left = Named(planes, "Left") ?? EndPlane(planes, true);
            var right = Named(planes, "Right") ?? EndPlane(planes, false);
            if (left == null || right == null) throw new InvalidOperationException("the template has no Left/Right reference planes (is it line-based?)");
            double x0 = Origin(left).X, x1 = Origin(right).X;
            var ext = ExtrudeYZ(fd, spec.Profile, x0, x1 - x0, 0);
            fd.Regenerate();
            var plan = PlanView(fd);
            int locked = LockEnds(fd, ext, plan, left, right);
            if (locked < 2) result.Notes.Add("only " + locked + " of the 2 ends locked to the line");
            return ext;
        }

        /// <summary>Point-based Structural Foundation: an "End" plane at "Wall length" from Center (Left/Right).</summary>
        private static GenericForm Foundation(Document fd, GenSpec spec, BuildResult result)
        {
            var plan = PlanView(fd);
            var planes = Planes(fd);
            var start = Named(planes, "Center (Left/Right)") ?? Through(planes, XYZ.BasisX, 0);
            if (start == null) throw new InvalidOperationException("the template has no Center (Left/Right) reference plane");
            var end = AddWallLength(fd, plan, start);
            var ext = ExtrudeYZ(fd, spec.Profile, 0, Ft(1200), 0);
            fd.Regenerate();
            int locked = LockEnds(fd, ext, plan, start, end);
            if (locked < 2) result.Notes.Add("only " + locked + " of the 2 ends locked to Wall length");
            return ext;
        }

        private static ReferencePlane AddWallLength(Document fd, View plan, ReferencePlane start)
        {
            var fc = fd.FamilyCreate;
            var end = fc.NewReferencePlane(new XYZ(Ft(1200), 0, 0), new XYZ(Ft(1200), Ft(1000), 0), XYZ.BasisZ, plan);
            end.Name = "End";
            fd.Regenerate();
            var fm = fd.FamilyManager;
            var wl = fm.get_Parameter(ParamWallLength) ?? fm.AddParameter(ParamWallLength, GroupTypeId.Geometry, SpecTypeId.Length, true);
            var ra = new ReferenceArray();
            ra.Append(start.GetReference());
            ra.Append(end.GetReference());
            var d = fc.NewDimension(plan, Line.CreateBound(new XYZ(0, Ft(-3000), 0), new XYZ(Ft(1200), Ft(-3000), 0)), ra);
            d.FamilyLabel = wl;
            fd.Regenerate();
            return end;
        }

        /// <summary>Structural Framing: the template's rectangle replaced by the profile, top of the section at the curve.</summary>
        private static GenericForm Framing(Document fd, GenSpec spec, BuildResult result)
        {
            var old = new FilteredElementCollector(fd).OfClass(typeof(Extrusion)).Cast<Extrusion>().FirstOrDefault();
            if (old == null) throw new InvalidOperationException("the framing template has no extrusion to replace");
            var ob = old.get_BoundingBox(null);
            double x0 = ob.Min.X, x1 = ob.Max.X;
            var planes = Planes(fd).Where(r => Math.Abs(r.Normal.X) > 0.99).ToList();
            ReferencePlane pl = null, pr = null;
            foreach (var r in planes)
            {
                double x = Origin(r).X;
                if (Math.Abs(x - x0) < 1e-6 && (pl == null || r.Name.StartsWith("Member", StringComparison.Ordinal))) pl = r;
                if (Math.Abs(x - x1) < 1e-6 && (pr == null || r.Name.StartsWith("Member", StringComparison.Ordinal))) pr = r;
            }
            if (pl == null || pr == null) throw new InvalidOperationException("the framing template's end planes were not found");
            var assoc = fd.FamilyManager.GetAssociatedFamilyParameter(old.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM));
            fd.Delete(old.Id);
            double top = spec.Profile.Max(p => p.V);
            var ext = ExtrudeYZ(fd, spec.Profile, x0, x1 - x0, -top);
            if (assoc != null) fd.FamilyManager.AssociateElementParameterToFamilyParameter(ext.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), assoc);
            fd.Regenerate();
            int locked = LockEnds(fd, ext, PlanView(fd), pl, pr);
            if (locked < 2) result.Notes.Add("only " + locked + " of the 2 ends locked to the member ends");
            return ext;
        }

        /// <summary>Plan profile extruded upward from the level; its height is the type parameter Height.</summary>
        private static GenericForm Vertical(Document fd, GenSpec spec)
        {
            var loop = new CurveArray();
            var pts = spec.Profile;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                loop.Append(Line.CreateBound(new XYZ(Ft(a.U), Ft(a.V), 0), new XYZ(Ft(b.U), Ft(b.V), 0)));
            }
            var arr = new CurveArrArray();
            arr.Append(loop);
            var ext = fd.FamilyCreate.NewExtrusion(true, arr, SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero)), Ft(spec.VerticalHeight));
            var fm = fd.FamilyManager;
            var h = fm.get_Parameter(ParamHeight) ?? fm.AddParameter(ParamHeight, GroupTypeId.Geometry, SpecTypeId.Length, false);
            fm.Set(h, Ft(spec.VerticalHeight));
            fm.AssociateElementParameterToFamilyParameter(ext.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), h);
            return ext;
        }

        /// <summary>The profile swept along the baked path (family origin = the path start).</summary>
        private static GenericForm SweepAlong(Application app, Document fd, GenSpec spec)
        {
            var path = new CurveArray();
            foreach (var c in spec.Path) path.Append(c);
            var pathPlane = SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
            var loop = new CurveArray();
            var pts = spec.Profile;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                loop.Append(Line.CreateBound(new XYZ(Ft(a.U), Ft(a.V), 0), new XYZ(Ft(b.U), Ft(b.V), 0)));
            }
            var arr = new CurveArrArray();
            arr.Append(loop);
            var profile = app.Create.NewCurveLoopsProfile(arr);
            return fd.FamilyCreate.NewSweep(true, path, pathPlane, profile, 0, ProfilePlaneLocation.Start);
        }

        /// <summary>A Profile family: the outline as detail lines (x = u, y = v) + VM_Profile with the points.</summary>
        private static void ProfileFamily(Document fd, GenSpec spec)
        {
            var plan = PlanView(fd);
            var pts = spec.Profile;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                fd.FamilyCreate.NewDetailCurve(plan, Line.CreateBound(new XYZ(Ft(a.U), Ft(a.V), 0), new XYZ(Ft(b.U), Ft(b.V), 0)));
            }
            var fm = fd.FamilyManager;
            var p = fm.get_Parameter(ParamProfile) ?? fm.AddParameter(ParamProfile, GroupTypeId.Data, SpecTypeId.String.Text, false);
            fm.Set(p, spec.ProfileText ?? "");
        }

        // ------------------------------------------------------------------ the parametric L

        /// <summary>
        /// Foot and stem as two rectangles whose faces are locked to reference planes driven by the
        /// type parameters (Stem width, Height, Foot width, Foot thickness, Toe; Heel = Foot width −
        /// Toe), both ends to the run's ends. Two rectangles instead of one L outline so Toe = 0 stays
        /// a valid shape (no zero-length edge).
        /// </summary>
        private static List<GenericForm> ParametricL(Document fd, GenSpec spec, BuildResult result)
        {
            var s = spec.Sizes;
            var fc = fd.FamilyCreate;
            var fm = fd.FamilyManager;
            var planes = Planes(fd);
            var plan = PlanView(fd);
            var cross = CrossSectionView(fd);
            if (cross == null) throw new InvalidOperationException("the template has no elevation looking along the run (Left/Right)");

            ReferencePlane startPlane, endPlane;
            double x0, length;
            if (spec.Kind == GenKind.LwallLineBased)
            {
                startPlane = Named(planes, "Left") ?? EndPlane(planes, true);
                endPlane = Named(planes, "Right") ?? EndPlane(planes, false);
                if (startPlane == null || endPlane == null) throw new InvalidOperationException("the template has no Left/Right reference planes (is it line-based?)");
                x0 = Origin(startPlane).X;
                length = Origin(endPlane).X - x0;
            }
            else
            {
                startPlane = Named(planes, "Center (Left/Right)") ?? Through(planes, XYZ.BasisX, 0);
                if (startPlane == null) throw new InvalidOperationException("the template has no Center (Left/Right) reference plane");
                endPlane = AddWallLength(fd, plan, startPlane);
                x0 = 0;
                length = Ft(1200);
            }

            var center = Named(planes, "Center (Front/Back)") ?? Through(planes, XYZ.BasisY, 0);
            if (center == null)
            {
                center = fc.NewReferencePlane(new XYZ(x0, 0, 0), new XYZ(x0, 0, Ft(1000)), XYZ.BasisX, cross);
                center.Name = "Stem front";
                center.Pinned = true;
            }
            var bottom = Named(planes, "Reference Plane") is ReferencePlane rp && Math.Abs(rp.Normal.Z) > 0.99 && Math.Abs(Origin(rp).Z) < 1e-6
                ? rp : Through(planes, XYZ.BasisZ, 0);
            if (bottom == null)
            {
                bottom = fc.NewReferencePlane(new XYZ(x0, 0, 0), new XYZ(x0, Ft(1000), 0), XYZ.BasisX, cross);
                bottom.Name = "Underside";
                bottom.Pinned = true;
            }

            ReferencePlane VPlane(double y, string name)
            {
                var r = fc.NewReferencePlane(new XYZ(x0, Ft(y), 0), new XYZ(x0, Ft(y), Ft(1000)), XYZ.BasisX, cross);
                r.Name = name;
                return r;
            }
            ReferencePlane HPlane(double z, string name)
            {
                var r = fc.NewReferencePlane(new XYZ(x0, 0, Ft(z)), new XYZ(x0, Ft(1000), Ft(z)), XYZ.BasisX, cross);
                r.Name = name;
                return r;
            }
            var pStemBack = VPlane(s.Stem, "Stem back");
            var pFootBack = VPlane(s.Heel, "Foot back");
            var pFootFront = VPlane(-s.Toe, "Foot front");
            var pFootTop = HPlane(s.FootThickness, "Foot top");
            var pTop = HPlane(s.Height, "Top");
            fd.Regenerate();

            FamilyParameter P(string name) => fm.get_Parameter(name) ?? fm.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, false);
            var gStem = P(ParamStem);
            var gHeight = P(ParamHeight);
            var gFoot = P(ParamFoot);
            var gFootT = P(ParamFootThickness);
            var gToe = P(ParamToe);
            var gHeel = P(ParamHeel);
            if (fm.CurrentType == null) fm.NewType(spec.TypeName);
            fm.Set(gStem, Ft(s.Stem));
            fm.Set(gHeight, Ft(s.Height));
            fm.Set(gFoot, Ft(s.Foot));
            fm.Set(gFootT, Ft(s.FootThickness));
            fm.Set(gToe, Ft(s.Toe));
            fm.SetFormula(gHeel, ParamFoot + " - " + ParamToe);

            void Dim(ReferencePlane a, ReferencePlane b, Line where, FamilyParameter label)
            {
                var ra = new ReferenceArray();
                ra.Append(a.GetReference());
                ra.Append(b.GetReference());
                var d = fc.NewDimension(cross, where, ra);
                d.FamilyLabel = label;
            }
            double top = s.Height, wide = Math.Max(s.Heel, s.Stem);
            Dim(center, pStemBack, Line.CreateBound(new XYZ(x0, 0, Ft(top + 500)), new XYZ(x0, Ft(s.Stem), Ft(top + 500))), gStem);
            Dim(center, pFootBack, Line.CreateBound(new XYZ(x0, 0, Ft(-500)), new XYZ(x0, Ft(s.Heel), Ft(-500))), gHeel);
            Dim(pFootFront, pFootBack, Line.CreateBound(new XYZ(x0, Ft(-s.Toe), Ft(-1000)), new XYZ(x0, Ft(s.Heel), Ft(-1000))), gFoot);
            Dim(bottom, pFootTop, Line.CreateBound(new XYZ(x0, Ft(wide + 500), 0), new XYZ(x0, Ft(wide + 500), Ft(s.FootThickness))), gFootT);
            Dim(bottom, pTop, Line.CreateBound(new XYZ(x0, Ft(wide + 1000), 0), new XYZ(x0, Ft(wide + 1000), Ft(top))), gHeight);
            fd.Regenerate();

            var foot = ExtrudeYZ(fd, new List<P2> { new P2(-s.Toe, 0), new P2(s.Heel, 0), new P2(s.Heel, s.FootThickness), new P2(-s.Toe, s.FootThickness) }, x0, length, 0);
            var stem = ExtrudeYZ(fd, new List<P2> { new P2(0, s.FootThickness), new P2(s.Stem, s.FootThickness), new P2(s.Stem, s.Height), new P2(0, s.Height) }, x0, length, 0);
            fd.Regenerate();

            int locked = 0, wanted = 0;
            var missed = new List<string>();
            void Lock(GenericForm form, Dictionary<XYZ, ReferencePlane> byNormal)
            {
                foreach (var pf in Faces(form))
                {
                    ReferencePlane target = null;
                    View v = cross;
                    if (pf.FaceNormal.IsAlmostEqualTo(-XYZ.BasisX)) { target = startPlane; v = plan; }
                    else if (pf.FaceNormal.IsAlmostEqualTo(XYZ.BasisX)) { target = endPlane; v = plan; }
                    else foreach (var kv in byNormal) if (pf.FaceNormal.IsAlmostEqualTo(kv.Key)) target = kv.Value;
                    if (target == null) continue;
                    wanted++;
                    try { fc.NewAlignment(v, target.GetReference(), pf.Reference).IsLocked = true; locked++; }
                    catch (Exception ex) { missed.Add(target.Name + ": " + ex.Message); }
                }
            }
            Lock(foot, new Dictionary<XYZ, ReferencePlane> { { -XYZ.BasisY, pFootFront }, { XYZ.BasisY, pFootBack }, { -XYZ.BasisZ, bottom }, { XYZ.BasisZ, pFootTop } });
            Lock(stem, new Dictionary<XYZ, ReferencePlane> { { -XYZ.BasisY, center }, { XYZ.BasisY, pStemBack }, { -XYZ.BasisZ, pFootTop }, { XYZ.BasisZ, pTop } });
            if (missed.Count > 0) throw new InvalidOperationException("locking the faces failed (" + locked + "/" + wanted + "): " + string.Join("; ", missed));

            try { JoinGeometryUtils.JoinGeometry(fd, foot, stem); }
            catch (Exception ex) { result.Notes.Add("foot and stem not joined in the family (" + ex.Message + ")"); }
            return new List<GenericForm> { foot, stem };
        }

        // ------------------------------------------------------------------ shared parameters

        private static void AddShared(Document fd, GenSpec spec, BuildResult result)
        {
            var sh = spec.Shared;
            if (sh == null) return;
            var fm = fd.FamilyManager;
            if (sh.Source != null && fm.get_Parameter(sh.Source.GUID) == null)
                fm.AddParameter(sh.Source, GroupTypeId.IdentityData, false);
            if (spec.Kind == GenKind.ProfileFamily) return;

            if (sh.Length != null && spec.Kind != GenKind.Framing && spec.Kind != GenKind.Vertical && fm.get_Parameter(sh.Length.GUID) == null)
            {
                var len = fm.AddParameter(sh.Length, GroupTypeId.Geometry, true);
                string formula = spec.Kind == GenKind.LineBased || spec.Kind == GenKind.LwallLineBased ? "Length"
                               : spec.Kind == GenKind.Foundation || spec.Kind == GenKind.LwallFoundation ? ParamWallLength : null;
                if (formula != null)
                {
                    try { fm.SetFormula(len, formula); }
                    catch (Exception ex) { result.Notes.Add(sh.Length.Name + " = " + formula + " refused: " + ex.Message); }
                }
            }
            if (sh.ObjectCode != null && fm.get_Parameter(sh.ObjectCode.GUID) == null)
            {
                try { fm.AddParameter(sh.ObjectCode, GroupTypeId.IdentityData, true); }
                catch (Exception ex) { result.Notes.Add(sh.ObjectCode.Name + " not added: " + ex.Message); }
            }
        }

        // ------------------------------------------------------------------ structure fingerprint

        /// <summary>
        /// A hash of what the family is made of — parameter names, element counts per kind and, for
        /// fixed profiles, the sketch points. Stored in VM_Source at generation; a different value
        /// in the loaded family means it was edited in Revit since (Edit Family → Load into Project).
        /// </summary>
        public static string StructureHash(Document fd, bool withSketchPoints)
        {
            var sb = new StringBuilder();
            foreach (var n in fd.FamilyManager.Parameters.Cast<FamilyParameter>().Select(p => p.Definition.Name).OrderBy(n => n, StringComparer.Ordinal))
                sb.Append(n).Append('|');
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in new FilteredElementCollector(fd).WhereElementIsNotElementType())
            {
                string k = e is GenericForm ? e.GetType().Name
                         : e is ReferencePlane ? "ReferencePlane"
                         : e is Dimension ? "Dimension"
                         : e is CurveElement ? "CurveElement"
                         : e is FamilyInstance ? "FamilyInstance" : null;
                if (k == null) continue;
                counts.TryGetValue(k, out int c);
                counts[k] = c + 1;
            }
            foreach (var kv in counts) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
            if (withSketchPoints)
            {
                var pts = new List<string>();
                foreach (var ce in new FilteredElementCollector(fd).OfClass(typeof(CurveElement)).Cast<CurveElement>())
                {
                    try
                    {
                        var c = ce.GeometryCurve;
                        pts.Add(P(c.GetEndPoint(0)) + "-" + P(c.GetEndPoint(1)));
                    }
                    catch { /* unbound curve */ }
                }
                pts.Sort(StringComparer.Ordinal);
                sb.Append(string.Join(",", pts));
            }
            return FamilyNaming.Hash(sb.ToString());
        }

        private static string P(XYZ p)
            => Math.Round(RevitUnitHelper.FeetToMm(p.X)) + " " + Math.Round(RevitUnitHelper.FeetToMm(p.Y)) + " " + Math.Round(RevitUnitHelper.FeetToMm(p.Z));

        // ------------------------------------------------------------------ helpers

        /// <summary>Profile (u, v) in the plane x = x0 (family +Y = u, +Z = v + dz), extruded along +X.</summary>
        private static Extrusion ExtrudeYZ(Document fd, List<P2> pts, double x0, double depth, double dzMm)
        {
            var loop = new CurveArray();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                loop.Append(Line.CreateBound(new XYZ(x0, Ft(a.U), Ft(a.V + dzMm)), new XYZ(x0, Ft(b.U), Ft(b.V + dzMm))));
            }
            var arr = new CurveArrArray();
            arr.Append(loop);
            var plane = SketchPlane.Create(fd, Plane.CreateByNormalAndOrigin(XYZ.BasisX, new XYZ(x0, 0, 0)));
            return fd.FamilyCreate.NewExtrusion(true, arr, plane, depth);
        }

        private static int LockEnds(Document fd, GenericForm form, View plan, ReferencePlane start, ReferencePlane end)
        {
            int n = 0;
            foreach (var pf in Faces(form))
            {
                ReferencePlane target = pf.FaceNormal.IsAlmostEqualTo(-XYZ.BasisX) ? start : pf.FaceNormal.IsAlmostEqualTo(XYZ.BasisX) ? end : null;
                if (target == null) continue;
                fd.FamilyCreate.NewAlignment(plan, target.GetReference(), pf.Reference).IsLocked = true;
                n++;
            }
            return n;
        }

        // Face references come from geometry computed WITHOUT a view: with a plan view the first
        // NewAlignment crashed Revit's native code in the probes (plan 12 §1.3).
        private static IEnumerable<PlanarFace> Faces(GenericForm form)
        {
            var solid = form.get_Geometry(new Options { ComputeReferences = true }).OfType<Solid>().FirstOrDefault(s => s.Volume > 0);
            if (solid == null) yield break;
            foreach (Face f in solid.Faces)
                if (f is PlanarFace pf) yield return pf;
        }

        private static List<ReferencePlane> Planes(Document fd)
            => new FilteredElementCollector(fd).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();

        private static ReferencePlane Named(List<ReferencePlane> planes, string name)
            => planes.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

        private static XYZ Origin(ReferencePlane r) => r.GetPlane().Origin;

        /// <summary>A plane with this normal through the origin's coordinate along it (office templates may rename planes).</summary>
        private static ReferencePlane Through(List<ReferencePlane> planes, XYZ normal, double at)
            => planes.FirstOrDefault(r => Math.Abs(Math.Abs(r.Normal.DotProduct(normal)) - 1) < 1e-6 && Math.Abs(Origin(r).DotProduct(normal) - at) < 1e-6);

        /// <summary>The run's start (min x) or end (max x) plane among the planes normal to X.</summary>
        private static ReferencePlane EndPlane(List<ReferencePlane> planes, bool start)
        {
            var xs = planes.Where(r => Math.Abs(Math.Abs(r.Normal.X) - 1) < 1e-6).ToList();
            if (xs.Count < 2) return null;
            return start ? xs.OrderBy(r => Origin(r).X).First() : xs.OrderByDescending(r => Origin(r).X).First();
        }

        private static View PlanView(Document fd)
        {
            var views = new FilteredElementCollector(fd).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate).ToList();
            var plan = views.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan) ?? views.FirstOrDefault();
            if (plan == null) throw new InvalidOperationException("the template has no plan view");
            return plan;
        }

        /// <summary>An elevation looking along the run (±X): where the cross-section planes are drawn.</summary>
        private static View CrossSectionView(Document fd)
        {
            var views = new FilteredElementCollector(fd).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Elevation && Math.Abs(Math.Abs(v.ViewDirection.X) - 1) < 1e-6).ToList();
            return views.FirstOrDefault(v => v.Name == "Left") ?? views.FirstOrDefault();
        }
    }
}
