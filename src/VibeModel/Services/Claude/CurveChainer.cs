using System;
using System.Collections.Generic;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Pure 2D curve-chaining: assembles closed loops (building footprints) from an unordered
    /// pile of polyline chains by snapping endpoints together. Revit-free so the headless test
    /// suite can cover it (same pattern as <see cref="BatchParser"/>).
    /// </summary>
    internal static class CurveChainer
    {
        /// <summary>
        /// Chain point-lists into closed loops by endpoint proximity.
        /// Input chains need >= 2 points each (shorter ones are dropped).
        /// Closed loops are returned WITHOUT the duplicated closing point,
        /// ready to paste into the 'floor' command. Chains that cannot be
        /// closed are returned as open chains.
        /// </summary>
        public static ChainResult ChainLoops(List<List<(double X, double Y)>> chains, double tolMm)
        {
            var loops = new List<List<(double X, double Y)>>();
            var open = new List<List<(double X, double Y)>>();

            // Normalize: drop degenerate chains, collapse consecutive duplicate points.
            var work = new List<List<(double X, double Y)>>();
            foreach (var chain in chains ?? new List<List<(double X, double Y)>>())
            {
                if (chain == null || chain.Count < 2) continue;
                var cleaned = new List<(double X, double Y)> { chain[0] };
                for (int i = 1; i < chain.Count; i++)
                {
                    if (!Near(chain[i], cleaned[cleaned.Count - 1], tolMm))
                        cleaned.Add(chain[i]);
                }
                if (cleaned.Count < 2) continue;

                // Already closed on arrival (e.g. a closed DWG polyline).
                if (cleaned.Count >= 4 && Near(cleaned[0], cleaned[cleaned.Count - 1], tolMm))
                {
                    cleaned.RemoveAt(cleaned.Count - 1);
                    loops.Add(cleaned);
                    continue;
                }
                work.Add(cleaned);
            }

            // Greedy chaining: grow a chain by appending any chain whose endpoint meets ours.
            while (work.Count > 0)
            {
                var current = work[0];
                work.RemoveAt(0);

                bool extended = true;
                while (extended)
                {
                    extended = false;
                    var tail = current[current.Count - 1];
                    var head = current[0];

                    for (int i = 0; i < work.Count; i++)
                    {
                        var cand = current;
                        var other = work[i];
                        var oStart = other[0];
                        var oEnd = other[other.Count - 1];

                        if (Near(tail, oStart, tolMm))
                        {
                            cand.AddRange(other.GetRange(1, other.Count - 1));
                        }
                        else if (Near(tail, oEnd, tolMm))
                        {
                            for (int j = other.Count - 2; j >= 0; j--) cand.Add(other[j]);
                        }
                        else if (Near(head, oEnd, tolMm))
                        {
                            cand.InsertRange(0, other.GetRange(0, other.Count - 1));
                        }
                        else if (Near(head, oStart, tolMm))
                        {
                            var reversed = new List<(double X, double Y)>();
                            for (int j = other.Count - 1; j >= 1; j--) reversed.Add(other[j]);
                            cand.InsertRange(0, reversed);
                        }
                        else
                        {
                            continue;
                        }

                        work.RemoveAt(i);
                        extended = true;
                        break;
                    }

                    // Closed?
                    if (current.Count >= 4 && Near(current[0], current[current.Count - 1], tolMm))
                    {
                        current.RemoveAt(current.Count - 1);
                        loops.Add(current);
                        current = null;
                        break;
                    }
                }

                if (current != null) open.Add(current);
            }

            return new ChainResult { Loops = loops, OpenChains = open };
        }

        private static bool Near((double X, double Y) a, (double X, double Y) b, double tol)
        {
            return Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol;
        }
    }

    /// <summary>Outcome of <see cref="CurveChainer.ChainLoops"/>.</summary>
    internal sealed class ChainResult
    {
        public List<List<(double X, double Y)>> Loops { get; set; }
        public List<List<(double X, double Y)>> OpenChains { get; set; }
    }
}
