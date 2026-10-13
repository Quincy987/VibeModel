using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using VibeModel.Infrastructure;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Keeps annotations alive when a VibeModel command replaces or changes the element they sit
    /// on. Measured in Revit 2023: most host changes made through the API raise no failure —
    /// deleting a floor silently deletes its spot elevation, a wall+floor dimension survives with
    /// only the wall references (0 segments, reads wrong on the sheet), a pile's parameter change
    /// can leave its spot with a stale value, and shape-editing a floor invalidates its spot.
    /// <para>
    /// Around delete / exec / set / join / unjoin / phase (and any batch holding one of them):
    /// <see cref="Capture"/> records the spot elevations, dimensions and tags on the elements the
    /// command names (every one in the document for exec); after the command <see cref="Reconcile"/>
    /// looks only at annotations whose referenced element the command deleted or changed (an
    /// annotation the command removed itself stays removed). Each one that vanished or went stale
    /// is refreshed on the same element when it survived, else moved to a replacement (what the
    /// command added, or an element VibeModel created earlier this session) — spots at the same
    /// height ±1 mm within 50 mm, dimension faces on the old plane ±1 mm — and the rest is deleted,
    /// never left invalid. Revit's own failure handling (<see cref="FailureCollector"/>) runs first;
    /// annotations it deleted are listed as removed "(Revit)" unless they could be re-hosted.
    /// </para>
    /// Main thread only. Never throws into a command: callers wrap every step.
    /// </summary>
    public static class AnnotationGuard
    {
        /// <summary>Above this many annotations in the document the snapshot is skipped (logged once).</summary>
        public const int Budget = 5000;

        private static readonly double SearchFt = RevitUnitHelper.MmToFeet(AnnotationMatch.SearchRadiusMm);
        private static readonly double TolFt = RevitUnitHelper.MmToFeet(AnnotationMatch.RehostToleranceMm);
        private static bool _budgetLogged;

        public static bool Applies(string command) => AnnotationScope.Applies(command);

        /// <summary>Every spot elevation, dimension and tag in the document (one collector, three classes).</summary>
        internal static FilteredElementCollector Annotations(Document doc)
            => new FilteredElementCollector(doc).WherePasses(new ElementMulticlassFilter(
                new List<Type> { typeof(Dimension), typeof(SpotDimension), typeof(IndependentTag) }));

        // ------------------------------------------------------------------ capture

        /// <summary>
        /// Records the annotations that reference <paramref name="targets"/> (null = every
        /// annotation in the document; a type id stands for all its instances). Null when there
        /// is nothing to guard.
        /// </summary>
        public static AnnotationSnapshot Capture(Document doc, ICollection<int> targets)
        {
            if (doc == null || doc.IsFamilyDocument) return null;
            if (targets != null && targets.Count == 0) return null;
            var clock = Stopwatch.StartNew();

            var collector = Annotations(doc);
            int count = collector.GetElementCount();
            if (count == 0) return null;
            if (count > Budget)
            {
                if (!_budgetLogged) Logger.Warn("Annotation guard skipped: " + count + " annotations in '" + doc.Title + "' (budget " + Budget + ")");
                _budgetLogged = true;
                return null;
            }

            var named = targets != null ? new HashSet<int>(targets) : new HashSet<int>();
            var targetSet = targets != null ? WithInstancesOfTypes(doc, targets) : null;
            var sheets = SheetsByView(doc);
            var records = new List<AnnotationRecord>();
            foreach (var e in collector)
            {
                try
                {
                    var rec = Record(doc, e, targetSet);
                    if (rec == null) continue;
                    rec.ViewName = (doc.GetElement(rec.OwnerViewId) as View)?.Name;
                    sheets.TryGetValue(rec.OwnerViewId.IntegerValue, out rec.Sheet);
                    records.Add(rec);
                }
                catch (Exception ex)
                {
                    Logger.Warn("Annotation guard: could not record " + e.Id.IntegerValue + ": " + ex.Message);
                }
            }
            if (clock.ElapsedMilliseconds > 200)
                Logger.Info("Annotation guard: captured " + records.Count + " of " + count + " annotations in " + clock.ElapsedMilliseconds + " ms");
            return records.Count == 0 ? null : new AnnotationSnapshot(doc, records, named);
        }

        /// <summary>The targets plus, for every target that is a type, the instances of that type (a type edit changes them all).</summary>
        private static HashSet<int> WithInstancesOfTypes(Document doc, ICollection<int> targets)
        {
            var set = new HashSet<int>(targets);
            var types = new HashSet<int>(targets.Where(t => doc.GetElement(new ElementId(t)) is ElementType));
            if (types.Count == 0) return set;
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var tid = e.GetTypeId();
                if (tid != null && types.Contains(tid.IntegerValue)) set.Add(e.Id.IntegerValue);
            }
            return set;
        }

        private static AnnotationRecord Record(Document doc, Element e, HashSet<int> targets)
        {
            if (e.OwnerViewId == null || e.OwnerViewId == ElementId.InvalidElementId) return null;   // a floor's sketch dimensions etc.

            if (e is IndependentTag tag)
            {
                var tagged = tag.GetTaggedLocalElementIds().Where(i => i != ElementId.InvalidElementId).ToList();
                if (tagged.Count == 0) return null;
                if (targets != null && !tagged.Any(i => targets.Contains(i.IntegerValue))) return null;
                var rec = new AnnotationRecord
                {
                    Id = e.Id.IntegerValue, Kind = AnnotationKind.Tag, OwnerViewId = e.OwnerViewId, TypeId = e.GetTypeId(),
                    Head = tag.TagHeadPosition, TagLeader = tag.HasLeader, Orientation = tag.TagOrientation
                };
                rec.Refs.Add(RefOf(doc, doc.GetElement(tagged[0]), null));
                return rec;
            }

            if (e is SpotDimension sd)
            {
                var r = sd.References;
                if (r == null || r.Size == 0) return null;
                var reference = r.get_Item(0);
                var host = doc.GetElement(reference.ElementId);
                if (host == null) return null;
                if (targets != null && !targets.Contains(host.Id.IntegerValue)) return null;
                var rec = new AnnotationRecord
                {
                    Id = e.Id.IntegerValue, Kind = AnnotationKind.SpotElevation, OwnerViewId = e.OwnerViewId, TypeId = e.GetTypeId()
                };
                rec.Refs.Add(RefOf(doc, host, reference));
                var hit = AnnotationGeometry.Resolve(doc, sd, doc.GetElement(e.OwnerViewId) as View);
                rec.Point = hit?.Point ?? SafeOrigin(sd);
                rec.FaceNormal = hit?.Normal;
                rec.EdgeRef = hit != null ? hit.Curve != null : host is FamilyInstance;
                rec.ValueMm = SpotValueMm(sd);
                rec.OffsetMm = rec.Point != null ? rec.ValueMm - RevitUnitHelper.FeetToMm(rec.Point.Z) : 0;
                try { rec.HasLeader = sd.HasLeader; } catch { }
                try { if (rec.HasLeader) rec.LeaderEnd = sd.LeaderEndPosition; } catch { }
                try { rec.HasShoulder = rec.HasLeader && sd.LeaderHasShoulder; } catch { }
                try { if (rec.HasShoulder) rec.Shoulder = sd.LeaderShoulderPosition; } catch { }
                return rec;
            }

            if (e is Dimension dim)
            {
                var refs = dim.References;
                if (refs == null || refs.Size < 2) return null;
                if (!(dim.Curve is Line line)) return null;   // radial/angular/arc dimensions: not guarded
                var list = new List<Reference>();
                foreach (Reference x in refs) list.Add(x);
                if (targets != null && !list.Any(x => targets.Contains(x.ElementId.IntegerValue))) return null;

                var rec = new AnnotationRecord
                {
                    Id = e.Id.IntegerValue, Kind = AnnotationKind.Dimension, OwnerViewId = e.OwnerViewId, TypeId = e.GetTypeId(),
                    LineOrigin = line.Origin, LineDir = line.Direction.Normalize()
                };
                var ts = SegmentPositions(dim, rec.LineOrigin, rec.LineDir);
                rec.TMin = ts.Count > 0 ? ts.Min() : 0;
                rec.TMax = ts.Count > 0 ? ts.Max() : 0;
                var hosts = list.Select(x => doc.GetElement(x.ElementId)).ToList();
                var positions = AnnotationMatch.AssignPositions(ts, list.Select((x, i) => PlanePosition(hosts[i], x, rec.LineOrigin, rec.LineDir, ts)).ToList());
                for (int i = 0; i < list.Count; i++)
                {
                    var rr = RefOf(doc, hosts[i], list[i]);
                    rr.T = positions[i];
                    rec.Refs.Add(rr);
                }
                return rec;
            }
            return null;
        }

        private static RefRecord RefOf(Document doc, Element host, Reference reference)
        {
            var rr = new RefRecord { ElementId = host?.Id.IntegerValue ?? -1 };
            if (reference != null)
                try { rr.Stable = reference.ConvertToStableRepresentation(doc); } catch { }
            if (host != null)
            {
                rr.CategoryId = host.Category?.Id.IntegerValue ?? 0;
                rr.IsDatum = IsDatum(host);
                var bb = host.get_BoundingBox(null);
                if (bb != null) { rr.BoxMin = bb.Min; rr.BoxMax = bb.Max; }
            }
            return rr;
        }

        /// <summary>
        /// The position of every reference along a linear dimension's line, in reference order:
        /// from the segments (start/end = origin ∓ value/2), or the one value of a 2-reference dimension.
        /// </summary>
        private static List<double> SegmentPositions(Dimension dim, XYZ lo, XYZ dir)
        {
            var ts = new List<double>();
            try
            {
                if (dim.NumberOfSegments > 1)
                {
                    bool first = true;
                    foreach (DimensionSegment s in dim.Segments)
                    {
                        double c = (s.Origin - lo).DotProduct(dir), v = s.Value ?? 0;
                        if (first) { ts.Add(c - v / 2); first = false; }
                        ts.Add(c + v / 2);
                    }
                }
                else
                {
                    double c = (dim.Origin - lo).DotProduct(dir), v = dim.Value ?? 0;
                    ts.Add(c - v / 2);
                    ts.Add(c + v / 2);
                }
            }
            catch { /* no segment data — re-creation uses the old line only */ }
            return ts;
        }

        /// <summary>
        /// Where a dimension reference lies along the dimension line (feet from <paramref name="lo"/>):
        /// a level's elevation, a grid/reference plane's position, a face's plane. A family
        /// instance's face may come back in the instance's own frame — of the two readings the one
        /// closer to a segment end wins. Null when unknown (a family's centre plane).
        /// </summary>
        private static double? PlanePosition(Element host, Reference r, XYZ lo, XYZ dir, List<double> ts)
        {
            try
            {
                if (host is Level l) return Math.Abs(dir.Z) > 0.99 ? (l.ProjectElevation - lo.Z) * dir.Z : (double?)null;
                if (host is Grid g && g.Curve is Line gl) return (gl.Origin - lo).DotProduct(dir);
                if (host is ReferencePlane rp) return (rp.BubbleEnd - lo).DotProduct(dir);
                XYZ p = null;
                var go = host?.GetGeometryObjectFromReference(r);
                if (go is PlanarFace pf) p = pf.Origin;
                else if (go is Edge ed) p = ed.AsCurve().Evaluate(0.5, true);
                if (p == null) return null;
                double t = (p - lo).DotProduct(dir);
                if (host is FamilyInstance fi && ts.Count > 0)
                {
                    double tt = (fi.GetTransform().OfPoint(p) - lo).DotProduct(dir);
                    if (ts.Min(x => Math.Abs(x - tt)) < ts.Min(x => Math.Abs(x - t))) t = tt;
                }
                return t;
            }
            catch { return null; }
        }

        internal static double SpotValueMm(SpotDimension sd)
        {
            try
            {
                var p = sd.get_Parameter(BuiltInParameter.SPOT_ELEV_SINGLE_OR_UPPER_VALUE);
                return p != null && p.HasValue ? RevitUnitHelper.FeetToMm(p.AsDouble()) : double.NaN;
            }
            catch { return double.NaN; }
        }

        private static XYZ SafeOrigin(SpotDimension sd)
        {
            try { return sd.Origin; } catch { return null; }
        }

        internal static Dictionary<int, string> SheetsByView(Document doc)
        {
            var map = new Dictionary<int, string>();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                if (doc.GetElement(vp.SheetId) is ViewSheet s) map[vp.ViewId.IntegerValue] = s.SheetNumber;
            return map;
        }

        // ------------------------------------------------------------------ reconcile

        /// <summary>
        /// After the command: re-host or remove what went missing or stale. Runs one transaction
        /// "VibeModel: Re-host annotations" only when there is something to do.
        /// </summary>
        public static AnnotationOutcome Reconcile(AnnotationSnapshot snapshot, RecordedChanges changes, FailureReport revit)
        {
            var outcome = new AnnotationOutcome();
            if (snapshot == null) return outcome;
            var doc = snapshot.Doc;
            if (doc == null || !doc.IsValidObject || doc.IsReadOnly) return outcome;

            var deleted = new HashSet<int>((changes?.Deleted ?? new List<ElementId>()).Select(i => i.IntegerValue));
            var modified = new HashSet<int>((changes?.Modified ?? new List<ElementId>()).Select(i => i.IntegerValue));
            var revitDeleted = new HashSet<long>(revit?.DeletedIds ?? Enumerable.Empty<long>());
            if (deleted.Count == 0 && modified.Count == 0 && revitDeleted.Count == 0) return outcome;

            var lost = new List<AnnotationRecord>();
            var damaged = new List<AnnotationRecord>();
            foreach (var rec in snapshot.Records)
            {
                // Only what this command did to the referenced elements counts: an annotation the
                // command deleted itself (named, or an exec snippet deleting it with its host
                // untouched) stays deleted.
                if (snapshot.Named.Contains(rec.Id)) continue;
                bool touched = rec.Refs.Any(r => deleted.Contains(r.ElementId) || modified.Contains(r.ElementId));
                var e = doc.GetElement(new ElementId(rec.Id));
                if (e == null)
                {
                    if (touched || revitDeleted.Contains(rec.Id)) lost.Add(rec);
                    continue;
                }
                if (!touched) continue;
                try
                {
                    if (rec.Kind == AnnotationKind.Dimension && e is Dimension d && !(e is SpotDimension))
                    {
                        // Measured: a wall+floor dimension whose floor was deleted survives with fewer
                        // references and 0 segments. (AreReferencesAvailable reads False on healthy
                        // dimensions too — not a signal.)
                        int n = d.References.Size;
                        if (n < rec.Refs.Count || (n >= 3 && d.NumberOfSegments != n - 1) || (n == 2 && d.Value == null)) damaged.Add(rec);
                    }
                    else if (rec.Kind == AnnotationKind.SpotElevation && e is SpotDimension sd)
                    {
                        if (!SpotStillValid(doc, sd, rec)) damaged.Add(rec);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("Annotation guard: check of " + rec.Id + " failed (" + ex.Message + ") — treated as stale");
                    damaged.Add(rec);
                }
            }
            if (lost.Count == 0 && damaged.Count == 0) return outcome;

            var added = (changes?.Added ?? new List<ElementId>()).Select(i => doc.GetElement(i)).Where(x => x != null).ToList();
            var results = new Dictionary<int, RehostResult>();
            var err = TransactionHelper.Execute(doc, ModelChangeTracker.TransactionPrefix + "Re-host annotations", () =>
            {
                foreach (var rec in damaged)
                {
                    try { doc.Delete(new ElementId(rec.Id)); }
                    catch (Exception ex) { Logger.Warn("Annotation guard: could not delete stale " + rec.Id + ": " + ex.Message); }
                }
                foreach (var rec in lost.Concat(damaged))
                {
                    RehostResult r = null;
                    try { r = Rehost(doc, rec, added, deleted); }
                    catch (Exception ex) { Logger.Warn("Annotation guard: re-host of " + rec.Id + " failed: " + ex.Message); }
                    results[rec.Id] = r;
                }
            });
            if (err != null)
            {
                Logger.Warn("Annotation guard: re-host transaction failed (" + err + ") — removing stale annotations only");
                results.Clear();
                if (damaged.Count > 0)
                    TransactionHelper.Execute(doc, ModelChangeTracker.TransactionPrefix + "Remove stale annotations", () =>
                    {
                        foreach (var rec in damaged)
                            try { if (doc.GetElement(new ElementId(rec.Id)) != null) doc.Delete(new ElementId(rec.Id)); } catch { }
                    });
            }

            foreach (var rec in lost.Concat(damaged))
            {
                results.TryGetValue(rec.Id, out var r);
                // Revit's failure handling may still have deleted a re-created annotation at commit.
                if (r != null && doc.GetElement(new ElementId(r.NewId)) != null)
                    outcome.Rehosted.Add(new RehostedAnnotation
                    {
                        From = rec.Id, To = r.NewId, Kind = rec.Kind, View = rec.ViewName,
                        Hosts = r.Hosts.Values.Select(h => new KeyValuePair<long, string>(h.Id.IntegerValue, h.Category?.Name)).ToList()
                    });
                else
                    outcome.Removed.Add(new RemovedAnnotation
                    {
                        Id = rec.Id, Kind = rec.Kind, View = rec.ViewName, Sheet = rec.Sheet,
                        Referenced = Culprits(rec, deleted, modified),
                        ByRevit = revitDeleted.Contains(rec.Id)
                    });
            }
            return outcome;
        }

        /// <summary>The referenced elements this command deleted or changed; else every non-datum reference.</summary>
        private static List<long> Culprits(AnnotationRecord rec, HashSet<int> deleted, HashSet<int> modified)
        {
            var hit = rec.Refs.Where(x => x.ElementId > 0 && (deleted.Contains(x.ElementId) || modified.Contains(x.ElementId)))
                .Select(x => (long)x.ElementId).Distinct().ToList();
            return hit.Count > 0 ? hit : rec.Refs.Where(x => !x.IsDatum && x.ElementId > 0).Select(x => (long)x.ElementId).Distinct().ToList();
        }

        private sealed class RehostResult
        {
            public int NewId;
            public readonly Dictionary<int, Element> Hosts = new Dictionary<int, Element>();   // by id, in order of use

            public RehostResult Host(Element e)
            {
                if (e != null && !Hosts.ContainsKey(e.Id.IntegerValue)) Hosts[e.Id.IntegerValue] = e;
                return this;
            }
        }

        private static RehostResult Rehost(Document doc, AnnotationRecord rec, List<Element> added, HashSet<int> deleted)
        {
            var view = doc.GetElement(rec.OwnerViewId) as View;
            if (view == null) return null;   // the view is gone: nothing to put it back in
            switch (rec.Kind)
            {
                case AnnotationKind.SpotElevation: return RehostSpot(doc, view, rec, added);
                case AnnotationKind.Dimension: return RehostDimension(doc, view, rec, added, deleted);
                default: return RehostTag(doc, view, rec, added);
            }
        }

        private static RehostResult RehostSpot(Document doc, View view, AnnotationRecord rec, List<Element> added)
        {
            if (rec.Point == null) return null;
            var old = rec.Refs[0];
            AnnotationGeometry.Hit hit = null;
            Element host = null;

            // 1. The same element when it survived (moved / lengthened): its nearest face or edge of
            //    the same kind, at any distance — the spot follows the element and its value is fresh.
            var same = old.ElementId > 0 ? doc.GetElement(new ElementId(old.ElementId)) : null;
            if (same != null)
            {
                hit = AnnotationGeometry.Nearest(same, view, rec.Point, rec.EdgeRef, rec.FaceNormal, double.MaxValue);
                if (hit != null) host = same;
            }
            // 2. A replacement with a face/edge at the same height (±1 mm) within the search radius —
            //    a spot measures height, so a 350 pile replacing a 320 one (its tip edge 15 mm
            //    further out) keeps its tip spot.
            if (hit == null)
            {
                var oldMm = AnnotationGeometry.V(rec.Point);
                foreach (var cand in Candidates(doc, added, old, rec.Point, false))
                {
                    var h = AnnotationGeometry.Nearest(cand, view, rec.Point, rec.EdgeRef, rec.FaceNormal, SearchFt,
                        p => AnnotationMatch.SpotMatch(oldMm, AnnotationGeometry.V(p)));
                    if (h != null && (hit == null || h.Distance < hit.Distance)) { hit = h; host = cand; }
                }
            }
            if (hit == null) return null;

            var delta = hit.Point - rec.Point;
            XYZ bend, end;
            if (rec.HasLeader && rec.LeaderEnd != null)
            {
                end = rec.LeaderEnd + delta;
                bend = rec.HasShoulder && rec.Shoulder != null ? rec.Shoulder + delta : (hit.Point + end) * 0.5;
            }
            else AnnotationGeometry.DefaultLeader(view, hit.Point, true, out bend, out end);

            var spot = doc.Create.NewSpotElevation(view, hit.Reference, hit.Point, bend, end, hit.Point, rec.HasLeader);
            if (spot == null) return null;
            KeepType(spot, rec.TypeId);
            return new RehostResult { NewId = spot.Id.IntegerValue }.Host(host);
        }

        private static RehostResult RehostDimension(Document doc, View view, AnnotationRecord rec, List<Element> added, HashSet<int> deleted)
        {
            if (rec.LineOrigin == null || rec.LineDir == null) return null;
            var vdir = view.ViewDirection.Normalize();
            var dirMm = AnnotationGeometry.V(rec.LineDir);
            var ra = new ReferenceArray();
            var result = new RehostResult();
            foreach (var rr in rec.Refs)
            {
                var existing = rr.ElementId > 0 && !deleted.Contains(rr.ElementId) ? doc.GetElement(new ElementId(rr.ElementId)) : null;
                var keep = StillValid(doc, existing, rr);
                if (keep != null)
                {
                    ra.Append(keep);
                    if (!rr.IsDatum) result.Host(existing);
                    continue;
                }
                if (rr.IsDatum || !rr.T.HasValue) return null;   // a datum that is gone, or no position recorded

                // A planar face of a replacement (or of the same, changed element) on the old reference plane (±1 mm).
                var planeMm = AnnotationGeometry.V(rec.LineOrigin + rec.LineDir * rr.T.Value);
                var probe = rr.BoxMin != null ? (rr.BoxMin + rr.BoxMax) * 0.5 : rec.LineOrigin + rec.LineDir * rr.T.Value;
                Reference best = null; Element bestHost = null; double bestD = double.MaxValue;
                foreach (var cand in Candidates(doc, added, rr, probe, true))
                {
                    foreach (var g in AnnotationGeometry.Enumerate(cand, view, faces: true, edges: false))
                    {
                        if (!(g.Face is PlanarFace pf)) continue;
                        if (Math.Abs(pf.FaceNormal.DotProduct(vdir)) > 0.01) continue;   // must be seen edge-on
                        var originMm = AnnotationGeometry.V(pf.Origin);
                        if (!AnnotationMatch.SamePlane(dirMm, planeMm, AnnotationGeometry.V(pf.FaceNormal), originMm)) continue;
                        double dd = Math.Abs((originMm - planeMm).Dot(dirMm) / dirMm.Length);
                        if (dd < bestD) { best = g.Reference; bestHost = cand; bestD = dd; }
                    }
                }
                if (best == null) return null;
                ra.Append(best);
                result.Host(bestHost);
            }

            double t0 = rec.TMin, t1 = rec.TMax;
            if (t1 - t0 < 1e-6) { t0 -= 1; t1 += 1; }
            var line = Line.CreateBound(rec.LineOrigin + rec.LineDir * (t0 - 1), rec.LineOrigin + rec.LineDir * (t1 + 1));
            var dim = doc.Create.NewDimension(view, line, ra);
            if (dim == null) return null;
            KeepType(dim, rec.TypeId);
            result.NewId = dim.Id.IntegerValue;
            return result;
        }

        /// <summary>The recorded reference when its element and face still exist; null otherwise.</summary>
        private static Reference StillValid(Document doc, Element existing, RefRecord rr)
        {
            if (existing == null) return null;
            // A datum's reference is rebuilt from the element: measured, a level reference parsed back
            // from a dimension's "<level>:0:SURFACE" makes NewDimension fail ("Invalid number of references").
            var datum = DatumReference(existing);
            if (datum != null) return datum;
            if (rr.Stable == null) return null;
            try
            {
                var parsed = Reference.ParseFromStableRepresentation(doc, rr.Stable);
                if (parsed == null) return null;
                // A family-instance plane has no geometry object to check; a face does.
                return existing is FamilyInstance || existing.GetGeometryObjectFromReference(parsed) != null ? parsed : null;
            }
            catch { return null; }
        }

        /// <summary>Levels, grids, reference planes and detail/model lines: never replaced by a command.</summary>
        private static bool IsDatum(Element e) => e is Level || e is Grid || e is ReferencePlane || e is CurveElement;

        private static Reference DatumReference(Element e)
        {
            switch (e)
            {
                case Level l: return l.GetPlaneReference();
                case Grid g: return new Reference(g);
                case ReferencePlane rp: return rp.GetReference();
                case CurveElement ce: return ce.GeometryCurve?.Reference;
                default: return null;
            }
        }

        private static RehostResult RehostTag(Document doc, View view, AnnotationRecord rec, List<Element> added)
        {
            var old = rec.Refs[0];
            if (old.BoxMin == null) return null;
            var centre = (old.BoxMin + old.BoxMax) * 0.5;
            var host = Candidates(doc, added, old, centre, false).FirstOrDefault(c => Contains(c, centre));
            if (host == null) return null;
            var tag = IndependentTag.Create(doc, rec.TypeId, view.Id, new Reference(host), rec.TagLeader, rec.Orientation, rec.Head);
            return tag == null ? null : new RehostResult { NewId = tag.Id.IntegerValue }.Host(host);
        }

        private static void KeepType(Element annotation, ElementId typeId)
        {
            if (typeId == null || typeId == ElementId.InvalidElementId || annotation.GetTypeId() == typeId) return;
            try { annotation.ChangeTypeId(typeId); } catch { /* keep the default type */ }
        }

        /// <summary>
        /// Replacement candidates of the old reference's category, near the old element (its box
        /// ± 50 mm): what the command added first, then elements VibeModel created earlier this
        /// session (a replacement placed in a previous call) — never an unrelated neighbour, so
        /// deleting one floor does not move its spot onto the next. <paramref name="includeSame"/>:
        /// the old element itself too (a dimension whose face died while the element survived).
        /// </summary>
        private static IEnumerable<Element> Candidates(Document doc, List<Element> added, RefRecord old, XYZ probe, bool includeSame)
        {
            var seen = new HashSet<int>();
            if (includeSame && old.ElementId > 0 && doc.GetElement(new ElementId(old.ElementId)) is Element same && Fits(same, old))
            {
                seen.Add(same.Id.IntegerValue);
                yield return same;
            }
            seen.Add(old.ElementId);
            var min = old.BoxMin ?? probe; var max = old.BoxMax ?? probe;
            if (min == null || max == null) yield break;
            var outline = new Outline(new XYZ(Math.Min(min.X, max.X) - SearchFt, Math.Min(min.Y, max.Y) - SearchFt, Math.Min(min.Z, max.Z) - SearchFt),
                                      new XYZ(Math.Max(min.X, max.X) + SearchFt, Math.Max(min.Y, max.Y) + SearchFt, Math.Max(min.Z, max.Z) + SearchFt));

            foreach (var e in added)
                if (Fits(e, old) && Overlaps(e, outline) && seen.Add(e.Id.IntegerValue)) yield return e;

            List<Element> near;
            try { near = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new BoundingBoxIntersectsFilter(outline)).ToList(); }
            catch { yield break; }
            foreach (var e in near)
                if (Fits(e, old) && VibeModelCreated.Contains(doc, e.Id) && seen.Add(e.Id.IntegerValue)) yield return e;
        }

        private static bool Fits(Element e, RefRecord old)
            => e != null && SheetHelper.IsModelContent(e) &&
               (old.CategoryId == 0 || (e.Category?.Id.IntegerValue ?? 0) == old.CategoryId);

        private static bool Overlaps(Element e, Outline outline)
        {
            var bb = e.get_BoundingBox(null);
            return bb != null && outline.Intersects(new Outline(bb.Min, bb.Max), 0);
        }

        private static bool Contains(Element e, XYZ p)
        {
            var bb = e.get_BoundingBox(null);
            return bb != null &&
                   p.X >= bb.Min.X - SearchFt && p.X <= bb.Max.X + SearchFt &&
                   p.Y >= bb.Min.Y - SearchFt && p.Y <= bb.Max.Y + SearchFt &&
                   p.Z >= bb.Min.Z - SearchFt && p.Z <= bb.Max.Z + SearchFt;
        }

        /// <summary>
        /// A surviving spot on a changed element is fine when its point still lies on its
        /// reference (±1 mm) and its value matches that point's height (±1 mm).
        /// </summary>
        private static bool SpotStillValid(Document doc, SpotDimension sd, AnnotationRecord rec)
        {
            var hit = AnnotationGeometry.Resolve(doc, sd, doc.GetElement(rec.OwnerViewId) as View);
            if (hit == null || hit.Distance > TolFt) return false;
            double value = SpotValueMm(sd);
            if (double.IsNaN(value) || double.IsNaN(rec.ValueMm)) return true;
            double expected = RevitUnitHelper.FeetToMm(hit.Point.Z) + rec.OffsetMm;
            return Math.Abs(value - expected) <= AnnotationMatch.RehostToleranceMm;
        }

        // ------------------------------------------------------------------ queries (whatdepends)

        /// <summary>The spot elevations, dimensions and tags (in a view) that reference any of <paramref name="ids"/>.</summary>
        public static List<Element> ReferencingAnnotations(Document doc, ICollection<int> ids)
        {
            var set = new HashSet<int>(ids);
            var result = new List<Element>();
            foreach (var e in Annotations(doc))
            {
                try
                {
                    if (e.OwnerViewId == null || e.OwnerViewId == ElementId.InvalidElementId) continue;   // a floor's sketch dimensions etc.
                    if (ReferencedIds(e).Any(set.Contains)) result.Add(e);
                }
                catch { /* unreadable annotation — skip */ }
            }
            return result;
        }

        /// <summary>The element ids an annotation references (a tag: what it tags).</summary>
        internal static IEnumerable<int> ReferencedIds(Element annotation)
        {
            if (annotation is IndependentTag t) return t.GetTaggedLocalElementIds().Select(i => i.IntegerValue);
            var list = new List<int>();
            if (annotation is Dimension d)
                foreach (Reference r in d.References) list.Add(r.ElementId.IntegerValue);
            return list;
        }

        public static AnnotationKind KindOf(Element e)
            => e is SpotDimension ? AnnotationKind.SpotElevation : e is IndependentTag ? AnnotationKind.Tag : AnnotationKind.Dimension;
    }

    /// <summary>What the guard recorded before the command.</summary>
    public sealed class AnnotationSnapshot
    {
        internal Document Doc { get; }
        internal List<AnnotationRecord> Records { get; }
        /// <summary>The ids the command named itself — an annotation among them was deleted on purpose.</summary>
        internal HashSet<int> Named { get; }

        internal AnnotationSnapshot(Document doc, List<AnnotationRecord> records, HashSet<int> named)
        {
            Doc = doc;
            Records = records;
            Named = named ?? new HashSet<int>();
        }
    }

    internal sealed class RefRecord
    {
        public int ElementId;
        public string Stable;
        public int CategoryId;
        public bool IsDatum;              // level, grid, reference plane, detail/model line: never replaced
        public XYZ BoxMin, BoxMax;        // the element's model box when recorded
        public double? T;                 // dimension references: position along the dimension line (feet)
    }

    internal sealed class AnnotationRecord
    {
        public int Id;
        public AnnotationKind Kind;
        public ElementId OwnerViewId;
        public ElementId TypeId;
        public string ViewName;
        public string Sheet;
        public readonly List<RefRecord> Refs = new List<RefRecord>();

        // spot elevation
        public XYZ Point;                 // model point on the reference
        public XYZ FaceNormal;            // null for edge references
        public bool EdgeRef;
        public XYZ LeaderEnd, Shoulder;
        public bool HasLeader, HasShoulder;
        public double ValueMm = double.NaN, OffsetMm;

        // dimension
        public XYZ LineOrigin, LineDir;
        public double TMin, TMax;

        // tag
        public XYZ Head;
        public bool TagLeader;
        public TagOrientation Orientation;
    }

    /// <summary>
    /// Faces and edges with references, in model coordinates, for annotations. Family
    /// instances: instance geometry, Fine (edges — Revit refuses a spot on an instance FACE in a
    /// section: "Spot Dimension does not lie on its reference"); system families: the view's
    /// geometry. Never combine DetailLevel with View in one Options.
    /// </summary>
    internal static class AnnotationGeometry
    {
        internal sealed class Geo
        {
            public Reference Reference;
            public Face Face;
            public Curve Curve;
        }

        internal sealed class Hit
        {
            public Reference Reference;
            public XYZ Point;        // on the reference, model coordinates
            public XYZ Normal;       // faces only
            public Curve Curve;      // edges only
            public double Distance;  // feet
        }

        public static IEnumerable<Geo> Enumerate(Element e, View view, bool faces, bool edges)
        {
            Options opt = e is FamilyInstance || view == null
                ? new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine }
                : new Options { ComputeReferences = true, View = view };
            GeometryElement ge;
            try { ge = e.get_Geometry(opt); } catch { ge = null; }
            if (ge == null && !(e is FamilyInstance) && view != null)
            {
                try { ge = e.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine }); } catch { ge = null; }
            }
            if (ge == null) yield break;
            var fi = e as FamilyInstance;
            foreach (var g in Walk(ge, faces, edges))
            {
                if (fi != null)
                {
                    var r = AsInstanceReference(fi, g.Reference);
                    if (r == null) continue;
                    g.Reference = r;
                }
                yield return g;
            }
        }

        /// <summary>
        /// Instance geometry carries references to the (internal) family symbol — measured: a spot
        /// or dimension on such a reference hangs on the symbol, outlives the instance and is
        /// invisible to a scan for it. "&lt;instance&gt;:0:INSTANCE:&lt;symbol ref&gt;" is the
        /// instance's own reference to the same face/edge. A reference to the instance itself or to
        /// a nested shared component (a real element) is kept as it is. Null when it cannot be made.
        /// </summary>
        internal static Reference AsInstanceReference(FamilyInstance fi, Reference r)
        {
            if (r == null) return null;
            if (r.ElementId == fi.Id) return r;
            var doc = fi.Document;
            if (!(doc.GetElement(r.ElementId) is ElementType)) return r;
            try
            {
                var parsed = Reference.ParseFromStableRepresentation(doc, fi.UniqueId + ":0:INSTANCE:" + r.ConvertToStableRepresentation(doc));
                return parsed != null && parsed.ElementId == fi.Id ? parsed : null;
            }
            catch { return null; }
        }

        private static IEnumerable<Geo> Walk(GeometryElement ge, bool faces, bool edges)
        {
            foreach (var go in ge)
            {
                if (go is Solid s && s.Faces.Size > 0)
                {
                    if (faces)
                        foreach (Face f in s.Faces)
                            if (f.Reference != null) yield return new Geo { Reference = f.Reference, Face = f };
                    if (edges)
                        foreach (Edge ed in s.Edges)
                        {
                            if (ed.Reference == null) continue;
                            Curve c;
                            try { c = ed.AsCurve(); } catch { continue; }
                            yield return new Geo { Reference = ed.Reference, Curve = c };
                        }
                }
                else if (go is GeometryInstance gi)
                {
                    GeometryElement inner;
                    try { inner = gi.GetInstanceGeometry(); } catch { inner = null; }
                    if (inner != null) foreach (var g in Walk(inner, faces, edges)) yield return g;
                }
            }
        }

        /// <summary>
        /// The closest face (facing like <paramref name="facing"/> when given) or edge (family
        /// instances, or when <paramref name="preferEdge"/>) of <paramref name="e"/> to
        /// <paramref name="p"/>, within <paramref name="maxFt"/> and passing <paramref name="accept"/>
        /// (tested on the point on the reference). Ties within 1 mm prefer a level edge (a pile's
        /// tip edge over its vertical corner).
        /// </summary>
        public static Hit Nearest(Element e, View view, XYZ p, bool preferEdge, XYZ facing, double maxFt, Func<XYZ, bool> accept = null)
        {
            bool edges = e is FamilyInstance || preferEdge;
            Hit best = null;
            double tol = RevitUnitHelper.MmToFeet(1);
            foreach (var g in Enumerate(e, view, faces: !edges, edges: edges))
            {
                var h = Project(g, p);
                if (h == null || h.Distance > maxFt) continue;
                if (h.Normal != null && facing != null && !AnnotationMatch.SameFacing(V(facing), V(h.Normal))) continue;
                if (accept != null && !accept(h.Point)) continue;
                if (best == null || h.Distance < best.Distance - tol ||
                    (Math.Abs(h.Distance - best.Distance) <= tol && IsLevel(h.Curve) && !IsLevel(best.Curve)))
                    best = h;
            }
            return best;
        }

        /// <summary>p projected onto a face or edge (model coordinates).</summary>
        private static Hit Project(Geo g, XYZ p)
        {
            IntersectionResult r;
            if (g.Face != null)
            {
                try { r = g.Face.Project(p); } catch { r = null; }
                if (r == null) return null;
                XYZ n = null;
                try { n = g.Face.ComputeNormal(r.UVPoint); } catch { }
                return new Hit { Reference = g.Reference, Point = r.XYZPoint, Normal = n, Distance = r.Distance };
            }
            if (g.Curve == null) return null;
            try { r = g.Curve.Project(p); } catch { r = null; }
            return r == null ? null : new Hit { Reference = g.Reference, Point = r.XYZPoint, Curve = g.Curve, Distance = r.Distance };
        }

        public static bool IsLevel(Curve c) => c is Line l && Math.Abs(l.Direction.Z) < 0.01;

        /// <summary>
        /// Where an existing spot elevation sits: its point on the face/edge it references, in
        /// model coordinates. Cheap first (the referenced geometry object directly, trying the
        /// family instance's own frame too — Revit may report a spot on an instance in that frame),
        /// the model-frame geometry matched by stable representation as a fallback.
        /// </summary>
        public static Hit Resolve(Document doc, SpotDimension sd, View view)
        {
            Reference reference;
            try { reference = sd.References.get_Item(0); } catch { return null; }
            var host = doc.GetElement(reference.ElementId);
            if (host == null) return null;
            XYZ raw;
            try { raw = sd.Origin; } catch { return null; }

            var frames = new List<Transform> { Transform.Identity };
            var points = new List<XYZ> { raw };
            if (host is FamilyInstance fi)
            {
                try { var t = fi.GetTransform(); frames.Add(t); points.Add(t.OfPoint(raw)); } catch { }
            }

            GeometryObject go = null;
            try { go = host.GetGeometryObjectFromReference(reference); } catch { }
            Hit best = null;
            if (go is Face || go is Edge)
            {
                var g = go is Face face ? new Geo { Reference = reference, Face = face } : new Geo { Reference = reference, Curve = ((Edge)go).AsCurve() };
                foreach (var frame in frames)
                    foreach (var p in points)
                    {
                        var h = Project(g, frame.Inverse.OfPoint(p));
                        if (h == null) continue;
                        h.Point = frame.OfPoint(h.Point);              // the frames are rigid: distances stay
                        if (h.Normal != null) h.Normal = frame.OfVector(h.Normal);
                        if (h.Curve != null && !frame.IsIdentity) h.Curve = h.Curve.CreateTransformed(frame);
                        if (best == null || h.Distance < best.Distance) best = h;
                    }
            }
            if (best != null && best.Distance <= RevitUnitHelper.MmToFeet(AnnotationMatch.RehostToleranceMm)) return best;

            // Fallback: the face/edge in the model-frame geometry with the same stable representation.
            string stable;
            try { stable = reference.ConvertToStableRepresentation(doc); } catch { return best; }
            foreach (var g in Enumerate(host, view, faces: true, edges: true))
            {
                string s;
                try { s = g.Reference.ConvertToStableRepresentation(doc); } catch { continue; }
                if (s != stable) continue;
                foreach (var p in points)
                {
                    var h = Project(g, p);
                    if (h != null && (best == null || h.Distance < best.Distance)) { h.Reference = reference; best = h; }
                }
                break;
            }
            return best;
        }

        /// <summary>
        /// The default leader of a spot elevation: the shoulder 12 paper mm above (top) or below
        /// (bottom) the point, the text 12 paper mm further to the view's right.
        /// </summary>
        public static void DefaultLeader(View view, XYZ p, bool top, out XYZ bend, out XYZ end)
        {
            double step = RevitUnitHelper.MmToFeet(12 * Math.Max(1, view.Scale));
            var up = view.UpDirection.Normalize();
            var right = view.RightDirection.Normalize();
            bend = p + up * (top ? step : -step);
            end = bend + right * step;
        }

        public static V3 V(XYZ p) => new V3(RevitUnitHelper.FeetToMm(p.X), RevitUnitHelper.FeetToMm(p.Y), RevitUnitHelper.FeetToMm(p.Z));
    }
}
