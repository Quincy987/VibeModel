using System;
using System.Collections.Generic;
using System.Linq;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// What the open document says about how it was set up — read live from the model
    /// (TemplateFactsCollector), never transcribed from a PDF. Pure data so fingerprint
    /// matching and summary rendering are unit-testable without Revit.
    /// </summary>
    public sealed class TemplateFacts
    {
        public string DocKey { get; set; }
        public string Title { get; set; }
        public string PathName { get; set; }
        public long Edits { get; set; }
        public bool IsWorkshared { get; set; }
        public bool IsFamilyDocument { get; set; }

        /// <summary>Phases in document order.</summary>
        public List<string> Phases { get; set; } = new List<string>();
        public List<string> PhaseFilters { get; set; } = new List<string>();
        public List<string> ViewTemplates { get; set; } = new List<string>();
        public List<string> ScopeBoxes { get; set; } = new List<string>();
        public List<string> ViewNames { get; set; } = new List<string>();
        /// <summary>Names of parameters bound to the project (project + shared parameters).</summary>
        public List<string> ProjectParameters { get; set; } = new List<string>();
        public List<FamilyFact> Families { get; set; } = new List<FamilyFact>();
        public int MaterialsCount { get; set; }
        public List<string> Titleblocks { get; set; } = new List<string>();
        /// <summary>"number name" per sheet.</summary>
        public List<string> Sheets { get; set; } = new List<string>();

        public CoordinateFacts Coordinates { get; set; } = new CoordinateFacts();
        public int CadLinks { get; set; }
        public int CadLinksPinned { get; set; }
        public int CadImports { get; set; }
        public int PointClouds { get; set; }
        public int PointCloudsPinned { get; set; }
        public int Grids { get; set; }
        public int GridsPinned { get; set; }

        /// <summary>True once the full collection ran (vs the cheap fingerprint-only pass).</summary>
        public bool IsFull { get; set; }
    }

    public sealed class FamilyFact
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public bool IsModelCategory { get; set; }
    }

    public sealed class CadCounts
    {
        public int Links { get; set; }
        public int LinksPinned { get; set; }
        public int Imports { get; set; }
    }

    public sealed class CoordinateFacts
    {
        public bool Available { get; set; }
        public double SurveyNorthMm { get; set; }
        public double SurveyEastMm { get; set; }
        public double SurveyElevationMm { get; set; }
        public bool SurveyPinned { get; set; }
        /// <summary>
        /// The survey point's own shared coordinates are 0,0,0 — it IS the shared origin. Its
        /// distance from the internal origin is legitimately large on a georeferenced project
        /// (that is what acquiring coordinates does), so that distance is NOT a fault.
        /// </summary>
        public bool SurveyAtSharedOrigin =>
            Math.Abs(SurveyNorthMm) < 1 && Math.Abs(SurveyEastMm) < 1 && Math.Abs(SurveyElevationMm) < 1;
        public double BaseNorthMm { get; set; }
        public double BaseEastMm { get; set; }
        public double BaseElevationMm { get; set; }
        public double BaseAngleDeg { get; set; }
        public bool BasePinned { get; set; }
        /// <summary>Survey point distance from the internal origin (informational).</summary>
        public double SurveyOffsetFromOriginMm { get; set; }
        public string SiteName { get; set; }
        /// <summary>True when the document has acquired/published shared coordinates.</summary>
        public bool SharedCoordinates { get; set; }
    }

    /// <summary>Active view state at the moment of the snapshot (changes without edits).</summary>
    public sealed class ActiveViewFacts
    {
        public string Name { get; set; }
        public string ViewType { get; set; }
        public string Phase { get; set; }
        public string PhaseFilter { get; set; }
        public string Template { get; set; }
    }

    /// <summary>Result of testing a document against a pack fingerprint.</summary>
    public sealed class FingerprintResult
    {
        public bool Matched { get; set; }
        public int Score { get; set; }
        public int Max { get; set; }
        public List<string> Reasons { get; } = new List<string>();
        public List<string> Missing { get; } = new List<string>();

        public string Summary => Max == 0
            ? "no fingerprint declared (always active)"
            : Score + "/" + Max + (Reasons.Count > 0 ? ": " + string.Join(", ", Reasons) : "");
    }

    public static class FingerprintMatcher
    {
        /// <summary>
        /// Activation rule (plan 11 §3): a pack applies when the document has ALL the pack's
        /// phases and AT LEAST ONE of its shared parameters. Each criterion only counts when the
        /// pack declares it, so a pack that declares nothing is always active. Scope boxes, views
        /// and view templates are reported as supporting evidence but never block activation:
        /// real projects delete the template's scope boxes and rename views freely (the pilot's
        /// demo project had no scope box left), while phases and bound shared parameters survive.
        /// </summary>
        public static FingerprintResult Match(PackFingerprint fp, TemplateFacts facts)
        {
            var r = new FingerprintResult();
            if (fp == null || facts == null || fp.IsEmpty)
            {
                r.Matched = fp == null || fp.IsEmpty;
                if (r.Matched) r.Reasons.Add("no fingerprint declared");
                return r;
            }

            bool ok = true;

            if (fp.Phases.Count > 0)
            {
                r.Max++;
                var missing = fp.Phases.Where(p => !ContainsAny(facts.Phases, fp.Spellings(p))).ToList();
                if (missing.Count == 0) { r.Score++; r.Reasons.Add("phases"); }
                else { ok = false; r.Missing.Add("phase(s) " + string.Join(", ", missing.Select(Q))); }
            }

            if (fp.SharedParameters.Count > 0)
            {
                r.Max++;
                var hit = fp.SharedParameters.FirstOrDefault(s => ContainsAny(facts.ProjectParameters, fp.Spellings(s)));
                if (hit != null) { r.Score++; r.Reasons.Add("shared parameters"); }
                else { ok = false; r.Missing.Add("shared parameter " + string.Join(" / ", fp.SharedParameters.Select(Q))); }
            }

            // Supporting evidence only.
            if (fp.ScopeBoxes.Count > 0 && fp.ScopeBoxes.Any(s => ContainsAny(facts.ScopeBoxes, fp.Spellings(s))))
                r.Reasons.Add("scope box");
            if (facts.IsFull && fp.ViewTemplates.Count > 0)
            {
                int n = fp.ViewTemplates.Count(v => ContainsAny(facts.ViewTemplates, fp.Spellings(v)));
                if (n > 0) r.Reasons.Add(n + "/" + fp.ViewTemplates.Count + " view templates");
            }
            if (facts.IsFull && fp.Views.Count > 0)
            {
                int n = fp.Views.Count(v => ContainsAny(facts.ViewNames, fp.Spellings(v)));
                if (n > 0) r.Reasons.Add(n + "/" + fp.Views.Count + " views");
            }

            r.Matched = ok; // IsEmpty returned early, so at least one required criterion was tested
            return r;
        }

        private static bool ContainsAny(IEnumerable<string> haystack, IEnumerable<string> needles)
        {
            if (haystack == null) return false;
            var set = new HashSet<string>(haystack.Where(h => h != null).Select(h => h.Trim()),
                StringComparer.OrdinalIgnoreCase);
            return needles.Any(n => n != null && set.Contains(n.Trim()));
        }

        private static string Q(string s) => "'" + s + "'";
    }
}
