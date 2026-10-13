using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VibeModel.Infrastructure;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Keeps annotations alive when a VibeModel command replaces or changes the element they sit
    /// on. Measured in Revit 2023: host changes made through the API raise no failure at all —
    /// deleting a floor silently deletes its spot elevation, a wall+floor dimension survives with
    /// only the wall references (0 segments, reads wrong on the sheet), moving a pile deletes its
    /// spot, and changing a pile's length leaves its spot with a stale value.
    /// <para>
    /// Around delete / exec / set / join / unjoin / phase (and any batch holding one of them):
    /// <see cref="Capture"/> records the spot elevations, dimensions and tags on the elements the
    /// command names (every one in the document for exec); after the command <see cref="Reconcile"/>
    /// puts each one that vanished or went stale back on the new (or the same, refreshed) element
    /// when that element's face/edge lies within <see cref="AnnotationMatch.RehostToleranceMm"/>
    /// of where the old reference was, and deletes the rest — never an invalid annotation left
    /// behind. Revit's own failure handling (<see cref="FailureCollector"/>) runs first; annotations
    /// it deleted are listed as removed "(Revit)" unless they could be re-hosted.
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

        // ------------------------------------------------------------------ capture

        /// <summary>
        /// Records the annotations that reference <paramref name="targets"/> (null = every
        /// annotation in the document). Null when there is nothing to guard.
        /// </summary>
        public static AnnotationSnapshot Capture(Document doc, ICollection<int> targets)
        {
            if (doc == null || doc.IsFamilyDocument) return null;
            if (targets != null && targets.Count == 0) return null;

            var collector = new FilteredElementCollector(doc).WherePasses(new ElementMulticlassFilter(
                new List<Type> { typeof(Dimension), typeof(SpotDimension), typeof(IndependentTag) }));
            int count = collector.GetElementCount();
            if (count == 0) return null;
            if (count > Budget)
            {
                if (!_budgetLogged) Logger.Warn("Annotation guard skipped: " + count + " annotations in '" + doc.Title + "' (budget " + Budget + ")");
                _budgetLogged = true;
                return null;
            }

            var targetSet = targets != null ? new HashSet<int>(targets) : null;
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
            return records.Count == 0 ? null : new AnnotationSnapshot(doc, records);
        }

        private static AnnotationRecord Record(Document doc, Element e, HashSet<int> targets)
        {
            if (e.OwnerViewId == null || e.OwnerViewId == ElementId.InvalidElementId) return null;

            if (e is IndependentTag tag)
            {
                var tagged = tag.GetTaggedLocalElementIds().Where(i => i != ElementId.InvalidElementId).ToList();
                if (tagged.Count == 0) return null;
                if (targets != null && !tagged.Any(i => targets.Contains(i.IntegerValue))) return null;
                var host = doc.GetElement(tagged[0]);
                var rec = new AnnotationRecord
                {
                    Id = e.Id.IntegerValue, Kind = AnnotationKind.Tag, OwnerViewId = e.OwnerViewId, TypeId = e.GetTypeId(),
                    Head = tag.TagHeadPosition, TagLeader = tag.HasLeader, Orientation = tag.TagOrientation
                };
                rec.Refs.Add(RefOf(doc, host, null));
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
                var view = doc.GetElement(e.OwnerViewId) as View;
                var rec = new AnnotationRecord
                {
                    Id = e.Id.IntegerValue, Kind = AnnotationKind.SpotElevation, OwnerViewId = e.OwnerViewId, TypeId = e.GetTypeId()
                };
                var rr = RefOf(doc, host, reference);
                rec.Refs.Add(rr);
                var hit = AnnotationGeometry.Resolve(doc, sd, view);
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
                for (int i = 0; i < list.Count; i++)
                {
                    var host = doc.GetElement(list[i].ElementId);
                    var rr = RefOf(doc, host, list[i]);
                    rr.T = i < ts.Count ? ts[i] : (double?)null;
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
            {
                try { rr.Stable = reference.ConvertToStableRepresentation(doc); } catch { }
                rr.RefType = reference.ElementReferenceType;
            }
            if (host != null)
            {
                rr.Category = host.Category?.Name;
                rr.CategoryId = host.Category?.Id.IntegerValue ?? 0;
                rr.IsInstance = host is FamilyInstance;
                rr.IsDatum = host is Level || host is Grid || host is ReferencePlane || host is DetailCurve || host is ModelCurve;
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

        private static Dictionary<int, string> SheetsByView(Document doc)
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
                var e = doc.GetElement(new ElementId(rec.Id));
                if (e == null) { lost.Add(rec); continue; }
                bool touched = rec.Refs.Any(r => deleted.Contains(r.ElementId) || modified.Contains(r.ElementId));
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
                {
                    outcome.Rehosted.Add(new RehostedAnnotation
                    {
                        From = rec.Id, To = r.NewId, Kind = rec.Kind, View = rec.ViewName,
                        Hosts = r.Hosts.GroupBy(h => h.Id.IntegerValue).Select(g => new KeyValuePair<long, string>(g.Key, g.First().Category?.Name)).ToList()
                    });
                }
                else
                {
                    outcome.Removed.Add(new RemovedAnnotation
                    {
                        Id = rec.Id, Kind = rec.Kind, View = rec.ViewName, Sheet = rec.Sheet,
                        Referenced = Culprits(rec, deleted, modified),
                        ByRevit = revitDeleted.Contains(rec.Id)
                    });
                }
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
            public List<Element> Hosts = new List<Element>();
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
                hit = AnnotationGeometry.Nearest(doc, same, view, rec.Point, rec.EdgeRef, rec.FaceNormal, double.MaxValue);
                if (hit != null) host = same;
            }
            // 2. A new element of the same category with a face/edge at the same height (±1 mm)
            //    within the 50 mm search radius — a spot measures height, so a 350 pile replacing
            //    a 320 one (its tip edge 15 mm further out) keeps its tip spot.
            if (hit == null)
            {
                foreach (var cand in Candidates(doc, added, old, rec.Point, rec.Point))
                {
                    var h = AnnotationGeometry.Nearest(doc, cand, view, rec.Point, rec.EdgeRef, rec.FaceNormal, SearchFt, rec.Point.Z);
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
            if (rec.TypeId != null && rec.TypeId != ElementId.InvalidElementId && spot.GetTypeId() != rec.TypeId)
                try { spot.ChangeTypeId(rec.TypeId); } catch { /* keep the default type */ }
            return new RehostResult { NewId = spot.Id.IntegerValue, Hosts = { host } };
        }

        private static RehostResult RehostDimension(Document doc, View view, AnnotationRecord rec, List<Element> added, HashSet<int> deleted)
        {
            if (rec.LineOrigin == null || rec.LineDir == null) return null;
            var vdir = view.ViewDirection.Normalize();
            var ra = new ReferenceArray();
            var hosts = new List<Element>();
            foreach (var rr in rec.Refs)
            {
                var existing = rr.ElementId > 0 && !deleted.Contains(rr.ElementId) ? doc.GetElement(new ElementId(rr.ElementId)) : null;
                Reference keep = null;
                if (existing != null && rr.Stable != null)
                {
                    try
                    {
                        var parsed = Reference.ParseFromStableRepresentation(doc, rr.Stable);
                        if (parsed != null && (rr.IsDatum || existing is FamilyInstance || existing.GetGeometryObjectFromReference(parsed) != null))
                            keep = parsed;
                    }
                    catch { keep = null; }
                }
                if (keep != null)
                {
                    ra.Append(keep);
                    if (!rr.IsDatum && !hosts.Contains(existing)) hosts.Add(existing);
                    continue;
                }
                if (rr.IsDatum || !rr.T.HasValue) return null;   // a datum that is gone, or no position recorded

                // A planar face of a replacement element on the old reference plane (±1 mm).
                var planeOrigin = rec.LineOrigin + rec.LineDir * rr.T.Value;
                var probe = rr.BoxMin != null ? (rr.BoxMin + rr.BoxMax) * 0.5 : planeOrigin;
                Reference best = null; Element bestHost = null; double bestD = double.MaxValue;
                foreach (var cand in Candidates(doc, added, rr, probe, null))
                {
                    foreach (var g in AnnotationGeometry.Enumerate(cand, view, faces: true, edges: false))
                    {
                        if (!(g.Face is PlanarFace pf)) continue;
                        if (Math.Abs(pf.FaceNormal.DotProduct(rec.LineDir)) < AnnotationMatch.ParallelDot) continue;
                        if (Math.Abs(pf.FaceNormal.DotProduct(vdir)) > 0.01) continue;   // must be seen edge-on
                        double dd = Math.Abs((pf.Origin - planeOrigin).DotProduct(rec.LineDir));
                        if (dd <= TolFt + 1e-9 && dd < bestD) { best = g.Reference; bestHost = cand; bestD = dd; }
                    }
                }
                if (best == null) return null;
                ra.Append(best);
                if (!hosts.Contains(bestHost)) hosts.Add(bestHost);
            }

            double t0 = rec.TMin, t1 = rec.TMax;
            if (t1 - t0 < 1e-6) { t0 -= 1; t1 += 1; }
            var line = Line.CreateBound(rec.LineOrigin + rec.LineDir * (t0 - 1), rec.LineOrigin + rec.LineDir * (t1 + 1));
            var dim = doc.Create.NewDimension(view, line, ra);
            if (dim == null) return null;
            if (rec.TypeId != null && rec.TypeId != ElementId.InvalidElementId && dim.GetTypeId() != rec.TypeId)
                try { dim.ChangeTypeId(rec.TypeId); } catch { }
            return new RehostResult { NewId = dim.Id.IntegerValue, Hosts = hosts };
        }

        private static RehostResult RehostTag(Document doc, View view, AnnotationRecord rec, List<Element> added)
        {
            var old = rec.Refs[0];
            if (old.BoxMin == null) return null;
            var centre = (old.BoxMin + old.BoxMax) * 0.5;
            Element host = null;
            foreach (var cand in Candidates(doc, added, old, centre, centre))
            {
                host = cand;
                break;
            }
            if (host == null) return null;
            var tag = IndependentTag.Create(doc, rec.TypeId, view.Id, new Reference(host), rec.TagLeader, rec.Orientation, rec.Head);
            return tag == null ? null : new RehostResult { NewId = tag.Id.IntegerValue, Hosts = { host } };
        }

        /// <summary>
        /// Replacement candidates of the old reference's category: what the command added first,
        /// then existing model elements near the old element (its box ± 50 mm). When
        /// <paramref name="mustContain"/> is given the candidate's box must contain that point ± 50 mm.
        /// </summary>
        private static IEnumerable<Element> Candidates(Document doc, List<Element> added, RefRecord old, XYZ probe, XYZ mustContain)
        {
            var seen = new HashSet<int> { old.ElementId };
            foreach (var e in added)
                if (Fits(e, old, mustContain) && seen.Add(e.Id.IntegerValue)) yield return e;

            var min = old.BoxMin ?? probe; var max = old.BoxMax ?? probe;
            if (min == null || max == null) yield break;
            var outline = new Outline(new XYZ(Math.Min(min.X, max.X) - SearchFt, Math.Min(min.Y, max.Y) - SearchFt, Math.Min(min.Z, max.Z) - SearchFt),
                                      new XYZ(Math.Max(min.X, max.X) + SearchFt, Math.Max(min.Y, max.Y) + SearchFt, Math.Max(min.Z, max.Z) + SearchFt));
            IEnumerable<Element> near;
            try { near = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new BoundingBoxIntersectsFilter(outline)).ToList(); }
            catch { yield break; }
            foreach (var e in near)
                if (Fits(e, old, mustContain) && seen.Add(e.Id.IntegerValue)) yield return e;
        }

        private static bool Fits(Element e, RefRecord old, XYZ mustContain)
        {
            if (e == null || !SheetHelper.IsModelContent(e)) return false;
            if (old.CategoryId != 0 && (e.Category?.Id.IntegerValue ?? 0) != old.CategoryId) return false;
            if (mustContain == null) return true;
            var bb = e.get_BoundingBox(null);
            if (bb == null) return false;
            return mustContain.X >= bb.Min.X - SearchFt && mustContain.X <= bb.Max.X + SearchFt &&
                   mustContain.Y >= bb.Min.Y - SearchFt && mustContain.Y <= bb.Max.Y + SearchFt &&
                   mustContain.Z >= bb.Min.Z - SearchFt && mustContain.Z <= bb.Max.Z + SearchFt;
        }

        /// <summary>
        /// A surviving spot on a changed element is fine when its point still lies on its
        /// reference (±1 mm) and its value matches that point's height (±1 mm).
        /// </summary>
        private static bool SpotStillValid(Document doc, SpotDimension sd, AnnotationRecord rec)
        {
            var refs = sd.References;
            if (refs == null || refs.Size == 0) return false;
            var hit = AnnotationGeometry.Resolve(doc, sd, doc.GetElement(rec.OwnerViewId) as View);
            if (hit == null || hit.Distance > TolFt) return false;
            double value = SpotValueMm(sd);
            if (double.IsNaN(value) || double.IsNaN(rec.ValueMm)) return true;
            double expected = RevitUnitHelper.FeetToMm(hit.Point.Z) + rec.OffsetMm;
            return Math.Abs(value - expected) <= AnnotationMatch.RehostToleranceMm;
        }

        // ------------------------------------------------------------------ queries (whatdepends)

        /// <summary>The spot elevations, dimensions and tags that reference any of <paramref name="ids"/>.</summary>
        public static List<Element> ReferencingAnnotations(Document doc, ICollection<int> ids)
        {
            var set = new HashSet<int>(ids);
            var result = new List<Element>();
            var collector = new FilteredElementCollector(doc).WherePasses(new ElementMulticlassFilter(
                new List<Type> { typeof(Dimension), typeof(SpotDimension), typeof(IndependentTag) }));
            foreach (var e in collector)
            {
                try
                {
                    IEnumerable<int> referenced;
                    if (e.OwnerViewId == null || e.OwnerViewId == ElementId.InvalidElementId) continue;   // sketch dimensions of a floor's boundary etc.
                    if (e is IndependentTag t) referenced = t.GetTaggedLocalElementIds().Select(i => i.IntegerValue);
                    else if (e is Dimension d)
                    {
                        var list = new List<int>();
                        foreach (Reference r in d.References) list.Add(r.ElementId.IntegerValue);
                        referenced = list;
                    }
                    else continue;
                    if (referenced.Any(set.Contains)) result.Add(e);
                }
                catch { /* unreadable annotation — skip */ }
            }
            return result;
        }

        public static AnnotationKind KindOf(Element e)
            => e is SpotDimension ? AnnotationKind.SpotElevation : e is IndependentTag ? AnnotationKind.Tag : AnnotationKind.Dimension;
    }

    /// <summary>What the guard recorded before the command.</summary>
    public sealed class AnnotationSnapshot
    {
        internal Document Doc { get; }
        internal List<AnnotationRecord> Records { get; }
        public int Count => Records.Count;

        internal AnnotationSnapshot(Document doc, List<AnnotationRecord> records)
        {
            Doc = doc;
            Records = records;
        }
    }

    internal sealed class RefRecord
    {
        public int ElementId;
        public string Stable;
        public ElementReferenceType RefType;
        public string Category;
        public int CategoryId;
        public bool IsInstance;
        public bool IsDatum;
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
    /// geometry (faces). Never combine DetailLevel with View in one Options.
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
                // Instance geometry carries references to the (internal) symbol — measured: a spot on
                // such an edge reports the symbol as its host, so it outlives the pile and is
                // invisible to a scan for the pile. "<instance>:0:INSTANCE:<symbol ref>" is the
                // instance's own reference to the same edge/face.
                if (fi != null && g.Reference.ElementId != fi.Id)
                {
                    var r = ToInstanceReference(fi, g.Reference);
                    if (r == null) continue;
                    g.Reference = r;
                }
                yield return g;
            }
        }

        internal static Reference ToInstanceReference(FamilyInstance fi, Reference symbolRef)
        {
            try
            {
                var doc = fi.Document;
                var stable = symbolRef.ConvertToStableRepresentation(doc);
                var r = Reference.ParseFromStableRepresentation(doc, fi.UniqueId + ":0:INSTANCE:" + stable);
                return r != null && r.ElementId == fi.Id ? r : null;
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
        /// The closest face (system families; facing like <paramref name="facing"/> when given) or
        /// edge (family instances, or when <paramref name="preferEdge"/>) of <paramref name="e"/>
        /// to <paramref name="p"/>, within <paramref name="maxFt"/>. Ties within 1 mm prefer a
        /// horizontal edge (a pile's tip edge over its vertical corner).
        /// </summary>
        public static Hit Nearest(Document doc, Element e, View view, XYZ p, bool preferEdge, XYZ facing, double maxFt, double? sameZ = null)
        {
            bool edges = e is FamilyInstance || preferEdge;
            Hit best = null;
            double tol = RevitUnitHelper.MmToFeet(1);
            foreach (var g in Enumerate(e, view, faces: !edges, edges: edges))
            {
                Hit h = null;
                if (g.Face != null)
                {
                    IntersectionResult r;
                    try { r = g.Face.Project(p); } catch { r = null; }
                    if (r == null) continue;
                    XYZ n;
                    try { n = g.Face.ComputeNormal(r.UVPoint); } catch { n = null; }
                    if (facing != null && n != null && !AnnotationMatch.SameFacing(V(facing), V(n))) continue;
                    h = new Hit { Reference = g.Reference, Point = r.XYZPoint, Normal = n, Distance = r.Distance };
                }
                else if (g.Curve != null)
                {
                    IntersectionResult r;
                    try { r = g.Curve.Project(p); } catch { r = null; }
                    if (r == null) continue;
                    h = new Hit { Reference = g.Reference, Point = r.XYZPoint, Curve = g.Curve, Distance = r.Distance };
                }
                if (h == null || h.Distance > maxFt) continue;
                if (sameZ.HasValue && !AnnotationMatch.SameHeight(RevitUnitHelper.FeetToMm(sameZ.Value), RevitUnitHelper.FeetToMm(h.Point.Z))) continue;
                if (best == null || h.Distance < best.Distance - tol ||
                    (Math.Abs(h.Distance - best.Distance) <= tol && Horizontal(h.Curve) && !Horizontal(best.Curve)))
                    best = h;
            }
            return best;
        }

        private static bool Horizontal(Curve c)
        {
            if (!(c is Line l)) return false;
            return Math.Abs(l.Direction.Z) < 0.01;
        }

        /// <summary>
        /// Where an existing spot elevation sits: the face/edge it references and its point in
        /// model coordinates. A spot on a family instance reports its origin in the instance's
        /// own frame; both readings are tried and the one on the geometry wins.
        /// </summary>
        public static Hit Resolve(Document doc, SpotDimension sd, View view)
        {
            Reference reference;
            try { reference = sd.References.get_Item(0); } catch { return null; }
            var host = doc.GetElement(reference.ElementId);
            if (host == null) return null;
            XYZ raw;
            try { raw = sd.Origin; } catch { return null; }

            var points = new List<XYZ> { raw };
            if (host is FamilyInstance fi)
            {
                try { var t = fi.GetTransform(); points.Add(t.OfPoint(raw)); points.Add(t.Inverse.OfPoint(raw)); } catch { }
            }

            string stable = null;
            try { stable = reference.ConvertToStableRepresentation(doc); } catch { }

            // The referenced face/edge itself: by its stable representation in the model-frame geometry.
            Geo target = null;
            if (stable != null)
            {
                bool inst = host is FamilyInstance;
                foreach (var g in Enumerate(host, view, faces: true, edges: true))
                {
                    string s;
                    try { s = g.Reference.ConvertToStableRepresentation(doc); } catch { continue; }
                    if (s == stable) { target = g; break; }
                }
                if (target == null && !inst)
                {
                    try
                    {
                        var go = host.GetGeometryObjectFromReference(reference);
                        if (go is Face f) target = new Geo { Reference = reference, Face = f };
                        else if (go is Edge ed) target = new Geo { Reference = reference, Curve = ed.AsCurve() };
                    }
                    catch { }
                }
            }
            if (target == null) return null;

            Hit best = null;
            foreach (var p in points)
            {
                Hit h = null;
                if (target.Face != null)
                {
                    IntersectionResult r;
                    try { r = target.Face.Project(p); } catch { r = null; }
                    if (r != null)
                    {
                        XYZ n = null;
                        try { n = target.Face.ComputeNormal(r.UVPoint); } catch { }
                        h = new Hit { Reference = reference, Point = r.XYZPoint, Normal = n, Distance = r.Distance };
                    }
                }
                else
                {
                    IntersectionResult r;
                    try { r = target.Curve.Project(p); } catch { r = null; }
                    if (r != null) h = new Hit { Reference = reference, Point = r.XYZPoint, Curve = target.Curve, Distance = r.Distance };
                }
                if (h != null && (best == null || h.Distance < best.Distance)) best = h;
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
