using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Join two elements' geometry — or cut one with the other — and explain any refusal.
    ///
    /// Revit's join/cut APIs fail with almost no information (a bare InvalidOperationException,
    /// or a silent no-op). This command reports what it tried, what Revit said, and which
    /// modelling workaround actually applies: the real cases that drove it are a sloped wing
    /// wall against a capstone (join needs real solid overlap, not a grazing face), a screw
    /// pile driven through a quay wall, and a steel H-beam inside a concrete pile (both need a
    /// solid-solid cut, and Revit's cut rules are category-sensitive).
    /// </summary>
    public class JoinCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "join";
        public string Description => "Join two elements' geometry (or cut one with the other) with failure diagnostics";
        public string Usage => "join <idA> <idB> [--cut|--switch]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var parsed = JoinArgs.ParseJoin(args);
            if (parsed.Error != null)
                return CommandResult.Error("BAD_ARGS", parsed.Error, Usage);

            var pair = JoinPair.Resolve(doc, parsed.IdA, parsed.IdB);
            if (pair.Error != null)
                return CommandResult.Error("ELEMENT_NOT_FOUND", pair.Error,
                    "Run 'selected' or 'list walls' to get valid element IDs.");

            switch (parsed.Mode)
            {
                case JoinMode.Cut: return DoCut(doc, pair);
                case JoinMode.Switch: return DoSwitch(doc, pair);
                default: return DoJoin(doc, pair);
            }
        }

        // ---- default: JoinGeometryUtils.JoinGeometry ----

        private CommandResult DoJoin(Document doc, JoinPair pair)
        {
            bool alreadyJoined;
            try
            {
                alreadyJoined = JoinGeometryUtils.AreElementsJoined(doc, pair.A, pair.B);
            }
            catch (Exception ex)
            {
                return pair.Failure("JOIN_FAILED",
                    "Join failed — Revit could not even evaluate this pair.",
                    "JoinGeometryUtils.AreElementsJoined", ex.Message,
                    new List<string>
                    {
                        "Both IDs must be model elements in THIS document — a linked-model element, " +
                        "a group member, or an annotation cannot be joined."
                    });
            }

            if (alreadyJoined)
            {
                var cuttingId = CuttingElementId(doc, pair);
                var model = pair.BaseReport("ALREADY JOINED");
                model.ApiTried = "JoinGeometryUtils.AreElementsJoined";
                model.Outcome = "These two are already joined" +
                                (cuttingId.HasValue ? " — " + cuttingId.Value + " currently cuts the other" : "") + ".";
                model.Hints.Add("Use 'join " + pair.IdA + " " + pair.IdB + " --switch' to flip which element wins at the join.");
                model.Hints.Add("Use 'unjoin " + pair.IdA + " " + pair.IdB + "' to undo the join.");
                return CommandResult.Ok(JoinReport.Build(model),
                    pair.BaseData("already-joined", "JoinGeometryUtils.AreElementsJoined", cuttingId));
            }

            var error = TransactionHelper.Execute(doc, "VibeModel: Join Geometry", () =>
            {
                JoinGeometryUtils.JoinGeometry(doc, pair.A, pair.B);
            });

            if (error != null)
                return pair.Failure("JOIN_FAILED",
                    "Join failed — Revit refused to join " + pair.IdA + " and " + pair.IdB + ".",
                    "JoinGeometryUtils.JoinGeometry", StripPrefix(error),
                    JoinDiagnostics.JoinHints(pair.BoxesOverlap, pair.CategoryA, pair.CategoryB));

            // Revit sometimes commits the call without actually creating the join (solids that
            // only graze each other). Verify rather than reporting a success that isn't one.
            if (!JoinGeometryUtils.AreElementsJoined(doc, pair.A, pair.B))
                return pair.Failure("JOIN_NOT_APPLIED",
                    "Join did not take — Revit accepted the call without error, but the two are still not joined.",
                    "JoinGeometryUtils.JoinGeometry",
                    "Their solids do not actually intersect (a shared face or edge is not enough).",
                    JoinDiagnostics.JoinHints(pair.BoxesOverlap, pair.CategoryA, pair.CategoryB));

            var cutter = CuttingElementId(doc, pair);
            var ok = pair.BaseReport("JOINED");
            ok.ApiTried = "JoinGeometryUtils.JoinGeometry";
            ok.Outcome = "Joined" + (cutter.HasValue ? " — " + cutter.Value + " cuts the other element" : "") + ".";
            ok.Hints.Add("Wrong element winning? Run 'join " + pair.IdA + " " + pair.IdB + " --switch'.");
            return CommandResult.Ok(JoinReport.Build(ok),
                pair.BaseData("joined", "JoinGeometryUtils.JoinGeometry", cutter));
        }

        // ---- --switch: JoinGeometryUtils.SwitchJoinOrder ----

        private CommandResult DoSwitch(Document doc, JoinPair pair)
        {
            if (!JoinGeometryUtils.AreElementsJoined(doc, pair.A, pair.B))
                return CommandResult.Error("NOT_JOINED",
                    "Elements " + pair.IdA + " and " + pair.IdB + " are not joined, so there is no join order to switch.",
                    "Run 'join " + pair.IdA + " " + pair.IdB + "' first.");

            var error = TransactionHelper.Execute(doc, "VibeModel: Switch Join Order", () =>
            {
                JoinGeometryUtils.SwitchJoinOrder(doc, pair.A, pair.B);
            });

            if (error != null)
                return pair.Failure("SWITCH_FAILED",
                    "Switching the join order failed.",
                    "JoinGeometryUtils.SwitchJoinOrder", StripPrefix(error),
                    new List<string>
                    {
                        "Not every join can be reversed (for instance against a wall that is already cut " +
                        "by an opening). Unjoin the pair and join it again in the order you want."
                    });

            var cutter = CuttingElementId(doc, pair);
            var m = pair.BaseReport("JOIN ORDER SWITCHED");
            m.ApiTried = "JoinGeometryUtils.SwitchJoinOrder";
            m.Outcome = cutter.HasValue ? cutter.Value + " now cuts the other element." : "Join order flipped.";
            return CommandResult.Ok(JoinReport.Build(m),
                pair.BaseData("switched", "JoinGeometryUtils.SwitchJoinOrder", cutter));
        }

        // ---- --cut: SolidSolidCutUtils.AddCutBetweenSolids ----

        private CommandResult DoCut(Document doc, JoinPair pair)
        {
            // Convention: A is the cutting solid, B is the element being cut.
            bool aAllowed = SolidSolidCutUtils.IsAllowedForSolidCut(pair.A);
            bool bAllowed = SolidSolidCutUtils.IsAllowedForSolidCut(pair.B);

            if (!aAllowed || !bAllowed)
            {
                var blocked = new List<string>();
                if (!aAllowed) blocked.Add(pair.IdA + " (" + pair.CategoryA + ")");
                if (!bAllowed) blocked.Add(pair.IdB + " (" + pair.CategoryB + ")");

                return pair.Failure("CUT_NOT_ALLOWED",
                    "Cut refused — Revit does not allow solid cuts on " + string.Join(" or ", blocked) + ".",
                    "SolidSolidCutUtils.IsAllowedForSolidCut",
                    "Only loadable-family instances, in-place families and generic forms can take part in a " +
                    "solid-solid cut. System families (walls, floors, roofs) cannot be cut this way, whatever " +
                    "their category.",
                    new List<string>
                    {
                        "Put a void extrusion in the cutting element's family and use 'Cut Geometry' — a void " +
                        "cuts hosts that solid-solid cuts refuse.",
                        "Or turn the element being cut into Parts (Modify > Create Parts); parts accept cuts.",
                        "If you only wanted a shared face rather than a hole, run 'join " + pair.IdA + " " +
                        pair.IdB + "' without --cut."
                    });
            }

            bool firstCutsSecond;
            if (SolidSolidCutUtils.CutExistsBetweenElements(pair.A, pair.B, out firstCutsSecond))
            {
                var m = pair.BaseReport("CUT ALREADY EXISTS");
                m.ApiTried = "SolidSolidCutUtils.CutExistsBetweenElements";
                m.Outcome = (firstCutsSecond ? pair.IdA + " already cuts " + pair.IdB
                                             : pair.IdB + " already cuts " + pair.IdA) + ".";
                m.Hints.Add("Use 'unjoin " + pair.IdA + " " + pair.IdB + "' to remove it" +
                            (firstCutsSecond ? "." : ", then re-run with the IDs swapped to cut the other way."));
                return CommandResult.Ok(JoinReport.Build(m),
                    pair.BaseData("cut-already-exists", "SolidSolidCutUtils.CutExistsBetweenElements",
                        firstCutsSecond ? pair.IdA : pair.IdB));
            }

            CutFailureReason reason;
            if (!SolidSolidCutUtils.CanElementCutElement(pair.A, pair.B, out reason))
            {
                var reasonName = reason.ToString();
                var hints = JoinDiagnostics.CutHints(reasonName, pair.CategoryA, pair.CategoryB)
                    .Select(h => h.Replace("<idA> <idB>", pair.IdA + " " + pair.IdB)).ToList();
                if (!pair.BoxesOverlap)
                    hints.Add("Their bounding boxes do not even intersect — move the cutting element into " +
                              "the other one before cutting.");

                return pair.Failure("CUT_REFUSED",
                    "Cut refused by Revit (" + reasonName + ").",
                    "SolidSolidCutUtils.CanElementCutElement",
                    JoinDiagnostics.ExplainCutFailure(reasonName, pair.CategoryA, pair.CategoryB),
                    hints);
            }

            var error = TransactionHelper.Execute(doc, "VibeModel: Cut Geometry", () =>
            {
                SolidSolidCutUtils.AddCutBetweenSolids(doc, pair.B, pair.A);
            });

            if (error != null)
                return pair.Failure("CUT_FAILED",
                    "Cut failed — Revit accepted the pair but the operation threw.",
                    "SolidSolidCutUtils.AddCutBetweenSolids", StripPrefix(error),
                    JoinDiagnostics.CutHints("CutNotAppropriateForElements", pair.CategoryA, pair.CategoryB)
                        .Select(h => h.Replace("<idA> <idB>", pair.IdA + " " + pair.IdB)).ToList());

            var okm = pair.BaseReport("CUT CREATED");
            okm.ApiTried = "SolidSolidCutUtils.AddCutBetweenSolids";
            okm.Outcome = pair.IdA + " now cuts " + pair.IdB + " (a void where the two solids overlap).";
            okm.Hints.Add("Use 'unjoin " + pair.IdA + " " + pair.IdB + "' to remove the cut.");
            return CommandResult.Ok(JoinReport.Build(okm),
                pair.BaseData("cut", "SolidSolidCutUtils.AddCutBetweenSolids", pair.IdA));
        }

        // ---- helpers ----

        /// <summary>Which of the two wins the join, or null if they are not joined.</summary>
        internal static int? CuttingElementId(Document doc, JoinPair pair)
        {
            try
            {
                if (!JoinGeometryUtils.AreElementsJoined(doc, pair.A, pair.B)) return null;
                return JoinGeometryUtils.IsCuttingElementInJoin(doc, pair.A, pair.B) ? pair.IdA : pair.IdB;
            }
            catch { return null; }
        }

        internal static string StripPrefix(string transactionError)
        {
            if (transactionError == null) return null;
            return transactionError.StartsWith("ERROR:", StringComparison.Ordinal)
                ? transactionError.Substring(6).Trim()
                : transactionError;
        }
    }

    /// <summary>
    /// The two elements plus the context every join/cut report needs: categories, whether their
    /// bounding boxes intersect, and how far apart they are when they don't.
    /// </summary>
    internal sealed class JoinPair
    {
        public Element A, B;
        public int IdA, IdB;
        public string CategoryA, CategoryB;
        public bool BoxesOverlap;
        public string OverlapText;
        public string Error;

        public static JoinPair Resolve(Document doc, int idA, int idB)
        {
            var p = new JoinPair { IdA = idA, IdB = idB };
            p.A = doc.GetElement(new ElementId(idA));
            p.B = doc.GetElement(new ElementId(idB));

            var missing = new List<int>();
            if (p.A == null) missing.Add(idA);
            if (p.B == null) missing.Add(idB);
            if (missing.Count > 0)
            {
                p.Error = "Element" + (missing.Count > 1 ? "s " : " ") +
                          string.Join(" and ", missing.Select(i => i.ToString())) +
                          " not found in this document.";
                return p;
            }

            p.CategoryA = p.A.Category?.Name ?? "no category";
            p.CategoryB = p.B.Category?.Name ?? "no category";
            p.DescribeOverlap();
            return p;
        }

        private void DescribeOverlap()
        {
            var ba = ModelBox(A);
            var bb = ModelBox(B);
            if (ba == null || bb == null)
            {
                BoxesOverlap = false;
                OverlapText = "unknown — at least one element has no bounding box (it may be a sketch, " +
                              "an annotation, or carry no model geometry)";
                return;
            }

            double ox = Math.Min(ba.Item2.X, bb.Item2.X) - Math.Max(ba.Item1.X, bb.Item1.X);
            double oy = Math.Min(ba.Item2.Y, bb.Item2.Y) - Math.Max(ba.Item1.Y, bb.Item1.Y);
            double oz = Math.Min(ba.Item2.Z, bb.Item2.Z) - Math.Max(ba.Item1.Z, bb.Item1.Z);

            if (ox > 0 && oy > 0 && oz > 0)
            {
                BoxesOverlap = true;
                OverlapText = "bounding boxes intersect over " + Mm(ox) + " x " + Mm(oy) + " x " + Mm(oz) + " mm";
            }
            else
            {
                BoxesOverlap = false;
                // The axis with the largest negative overlap is the real separation.
                double gap = -Math.Min(Math.Min(ox, oy), oz);
                var axes = new List<string>();
                if (ox <= 0) axes.Add("X");
                if (oy <= 0) axes.Add("Y");
                if (oz <= 0) axes.Add("Z");
                OverlapText = "bounding boxes do NOT intersect — separated by " + Mm(gap) + " mm in " +
                              string.Join("/", axes) + ". Revit only joins or cuts solids that really overlap";
            }
        }

        /// <summary>
        /// Bounding box in model coordinates as (min, max). The box transform is not always
        /// identity, so all eight corners are transformed and re-bounded.
        /// </summary>
        private static Tuple<XYZ, XYZ> ModelBox(Element e)
        {
            BoundingBoxXYZ box;
            try { box = e.get_BoundingBox(null); }
            catch { return null; }
            if (box == null) return null;

            var t = box.Transform ?? Transform.Identity;
            var corners = new List<XYZ>();
            foreach (var x in new[] { box.Min.X, box.Max.X })
                foreach (var y in new[] { box.Min.Y, box.Max.Y })
                    foreach (var z in new[] { box.Min.Z, box.Max.Z })
                        corners.Add(t.OfPoint(new XYZ(x, y, z)));

            var min = new XYZ(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z));
            var max = new XYZ(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z));
            return Tuple.Create(min, max);
        }

        private static string Mm(double feet) =>
            Math.Round(RevitUnitHelper.FeetToMm(feet)).ToString(System.Globalization.CultureInfo.InvariantCulture);

        public string Describe(Element e, int id, string category)
        {
            var name = SafeName(e);
            return id + " - " + category + (string.IsNullOrEmpty(name) ? "" : " - \"" + name + "\"") +
                   " (" + e.GetType().Name + ")";
        }

        internal static string SafeName(Element e)
        {
            try { return e.Name; } catch { return null; }
        }

        public JoinReportModel BaseReport(string title)
        {
            return new JoinReportModel
            {
                Title = title,
                ElementA = Describe(A, IdA, CategoryA),
                ElementB = Describe(B, IdB, CategoryB),
                Overlap = OverlapText
            };
        }

        /// <summary>
        /// A failed attempt: a one-line headline (what text mode prefixes with "ERROR: ") followed
        /// by the same element/overlap/API block a success prints, plus the workaround hints.
        /// </summary>
        public CommandResult Failure(string code, string headline, string api, string reason, List<string> hints)
        {
            var m = BaseReport(null);   // no title — the headline carries it
            m.ApiTried = api;
            m.Reason = reason;

            // Hints ride in Suggestion rather than the body: text mode renders them as the
            // trailing "Hint:" lines (one per hint), JSON puts them in error.suggestion —
            // and neither form repeats them.
            var body = JoinReport.Build(m).TrimEnd();
            var suggestion = hints == null || hints.Count == 0 ? null : string.Join("\nHint: ", hints);
            return CommandResult.Error(code, headline + "\n\n" + body, suggestion);
        }

        public Dictionary<string, object> BaseData(string state, string api, int? cuttingElement)
        {
            return new Dictionary<string, object>
            {
                { "state", state },
                { "api", api },
                { "a", new Dictionary<string, object> { { "id", IdA }, { "category", CategoryA }, { "name", SafeName(A) } } },
                { "b", new Dictionary<string, object> { { "id", IdB }, { "category", CategoryB }, { "name", SafeName(B) } } },
                { "boxesOverlap", BoxesOverlap },
                { "overlap", OverlapText },
                { "cuttingElement", cuttingElement }
            };
        }
    }
}
