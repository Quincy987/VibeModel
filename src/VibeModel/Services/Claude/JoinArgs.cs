using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VibeModel.Services.Claude
{
    /// <summary>What the caller asked <c>join</c> to do.</summary>
    internal enum JoinMode
    {
        /// <summary>JoinGeometryUtils.JoinGeometry — the normal "make these two share a face" case.</summary>
        Join,
        /// <summary>SolidSolidCutUtils.AddCutBetweenSolids — one solid punches a void in the other.</summary>
        Cut,
        /// <summary>JoinGeometryUtils.SwitchJoinOrder — flip which element wins at the join.</summary>
        Switch
    }

    /// <summary>Parsed <c>join</c> / <c>unjoin</c> arguments. Error is non-null when invalid.</summary>
    internal sealed class JoinArgsResult
    {
        public int IdA;
        public int IdB;
        public JoinMode Mode = JoinMode.Join;
        public string Error;
    }

    /// <summary>
    /// Pure argument parsing + report rendering for the join/cut diagnostics commands.
    /// Deliberately Revit-free so the headless test suite can cover it: the command classes
    /// do the Revit calls and hand plain strings/bools back here for formatting.
    /// </summary>
    internal static class JoinArgs
    {
        private static readonly char[] Separators = { ' ', ',' };

        /// <summary>join &lt;idA&gt; &lt;idB&gt; [--cut|--switch]</summary>
        public static JoinArgsResult ParseJoin(string args)
        {
            return Parse(args, allowFlags: true,
                usage: "Usage: join <idA> <idB> [--cut|--switch]");
        }

        /// <summary>unjoin &lt;idA&gt; &lt;idB&gt; — removes a join and/or a solid cut between the pair.</summary>
        public static JoinArgsResult ParseUnjoin(string args)
        {
            return Parse(args, allowFlags: false, usage: "Usage: unjoin <idA> <idB>");
        }

        private static JoinArgsResult Parse(string args, bool allowFlags, string usage)
        {
            var r = new JoinArgsResult();
            var tokens = (args ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries).ToList();

            // Flags first, anywhere in the string; what remains must be exactly two IDs.
            bool cut = false, sw = false;
            for (int i = 0; i < tokens.Count;)
            {
                var t = tokens[i];
                if (!t.StartsWith("--", StringComparison.Ordinal)) { i++; continue; }

                if (!allowFlags)
                {
                    r.Error = "unjoin takes no flags. " + usage;
                    return r;
                }
                if (t.Equals("--cut", StringComparison.OrdinalIgnoreCase)) cut = true;
                else if (t.Equals("--switch", StringComparison.OrdinalIgnoreCase)) sw = true;
                else
                {
                    r.Error = "Unknown flag '" + t + "'. " + usage;
                    return r;
                }
                tokens.RemoveAt(i);
            }

            if (cut && sw)
            {
                r.Error = "--cut and --switch are mutually exclusive. " + usage;
                return r;
            }

            if (tokens.Count < 2)
            {
                r.Error = "Need two element IDs. " + usage;
                return r;
            }
            if (tokens.Count > 2)
            {
                r.Error = "Expected exactly two element IDs, got " + tokens.Count + ". " + usage;
                return r;
            }

            if (!int.TryParse(tokens[0], out int a) || !int.TryParse(tokens[1], out int b))
            {
                r.Error = "Element IDs must be whole numbers. " + usage;
                return r;
            }
            if (a == b)
            {
                r.Error = "An element cannot be joined to itself (both IDs are " + a + ").";
                return r;
            }

            r.IdA = a;
            r.IdB = b;
            r.Mode = cut ? JoinMode.Cut : sw ? JoinMode.Switch : JoinMode.Join;
            return r;
        }
    }

    /// <summary>
    /// Plain-data view of one join/cut attempt. The command fills this in from Revit, then
    /// <see cref="JoinReport.Build"/> renders it — so the whole output format is unit-testable.
    /// </summary>
    internal sealed class JoinReportModel
    {
        public string Title;            // "JOINED", "JOIN FAILED", ...
        public string ElementA;         // "12345 - Walls - Basic Wall: Concrete 300"
        public string ElementB;
        public string Overlap;          // one human line about bbox intersection
        public string ApiTried;         // "JoinGeometryUtils.JoinGeometry"
        public string Outcome;          // what happened (success line)
        public string Reason;           // why it failed (null on success)
        public List<string> Hints = new List<string>();
    }

    /// <summary>Renders a <see cref="JoinReportModel"/> as the command's plain-text output.</summary>
    internal static class JoinReport
    {
        public static string Build(JoinReportModel m)
        {
            var sb = new StringBuilder();
            // Title is omitted for failures: those render through CommandResult.Error, which
            // already prefixes a headline ("ERROR: Join failed — ...").
            if (!string.IsNullOrEmpty(m.Title))
            {
                sb.AppendLine(m.Title);
                sb.AppendLine(new string('=', m.Title.Length));
                sb.AppendLine();
            }

            if (m.ElementA != null) sb.AppendLine("A: " + m.ElementA);
            if (m.ElementB != null) sb.AppendLine("B: " + m.ElementB);
            if (m.Overlap != null) sb.AppendLine("Overlap: " + m.Overlap);
            if (m.ElementA != null || m.Overlap != null) sb.AppendLine();

            if (m.ApiTried != null) sb.AppendLine("API tried: " + m.ApiTried);
            if (m.Outcome != null) sb.AppendLine("Result: " + m.Outcome);
            if (m.Reason != null) sb.AppendLine("Reason: " + m.Reason);

            if (m.Hints != null && m.Hints.Count > 0)
            {
                sb.AppendLine();
                foreach (var h in m.Hints)
                    sb.AppendLine("Hint: " + h);
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Turns Revit's terse refusals into plain language plus a workaround. Keyed on strings
    /// (the enum's name, a category pair) so this stays Revit-free and testable.
    /// </summary>
    internal static class JoinDiagnostics
    {
        /// <summary>
        /// Plain-language meaning of a <c>CutFailureReason</c> value, keyed by its enum name.
        /// Unknown values fall through to a generic sentence so a new Revit release can't
        /// produce an empty explanation.
        /// </summary>
        public static string ExplainCutFailure(string reasonName, string categoryA, string categoryB)
        {
            var a = categoryA ?? "element A";
            var b = categoryB ?? "element B";

            switch (reasonName)
            {
                case "CutAllowed":
                    return "Revit reports the cut is allowed.";
                case "CutAlreadyExists":
                    return "These two already cut each other in this direction — nothing to add.";
                case "OppositeCutExists":
                    return "A cut already exists the other way round (B currently cuts A). " +
                           "Revit allows only one direction at a time.";
                case "CutNotAppropriateForElements":
                    return "Revit refuses a solid-solid cut between a " + a + " element and a " + b +
                           " element. Solid cuts only work between elements Revit considers cuttable " +
                           "(loadable-family instances, in-place families, generic forms) and, in " +
                           "practice, not between two elements of the same kind of host geometry.";
                default:
                    return "Revit refused the cut (" + (reasonName ?? "unknown reason") + ").";
            }
        }

        /// <summary>Workarounds to print after a failed cut, tailored to the refusal.</summary>
        public static List<string> CutHints(string reasonName, string categoryA, string categoryB)
        {
            var hints = new List<string>();
            switch (reasonName)
            {
                case "OppositeCutExists":
                    hints.Add("Run 'unjoin " + "<idA> <idB>" + "' to drop the existing cut, then re-run with the IDs swapped.");
                    break;
                case "CutAlreadyExists":
                    hints.Add("Nothing to do — use 'unjoin <idA> <idB>' if you wanted to remove it.");
                    break;
                case "CutNotAppropriateForElements":
                    hints.Add("Model the penetration with a void: put a void extrusion in one element's " +
                              "family and use 'Cut Geometry' — a void cuts hosts that refuse solid-solid cuts.");
                    hints.Add("Or convert the element being cut into Parts (Modify > Create Parts); " +
                              "parts accept cuts that the original host refuses.");
                    hints.Add("Or make the cutting element an in-place family in a different category " +
                              "than the element being cut — Revit's cut rules are category-sensitive.");
                    break;
                default:
                    hints.Add("Try 'join <idA> <idB>' without --cut, or model the penetration with a void family.");
                    break;
            }
            return hints;
        }

        /// <summary>Workarounds to print after a failed JoinGeometry call.</summary>
        public static List<string> JoinHints(bool boxesOverlap, string categoryA, string categoryB)
        {
            var hints = new List<string>();
            if (!boxesOverlap)
            {
                hints.Add("The two bounding boxes do not intersect, so the solids cannot be touching. " +
                          "Revit only joins elements whose solids actually overlap — extend or move one " +
                          "of them into the other first.");
                hints.Add("Sloped elements (a wing wall against a capstone) usually fail here: the sloped " +
                          "face only grazes the other solid. Extend the sloped element past the face it " +
                          "meets so there is real overlap, then join and let the join trim it back.");
            }
            else
            {
                hints.Add("Bounding boxes overlap but Revit still refused — the actual solids may only " +
                          "touch at a face or edge. Overlap them by a few mm and try again.");
                hints.Add("A " + (categoryA ?? "?") + " element and a " + (categoryB ?? "?") +
                          " element may simply not be joinable in Revit; try '--cut' for a solid-solid cut, " +
                          "or model the connection with a void family or Parts.");
            }
            return hints;
        }
    }
}
