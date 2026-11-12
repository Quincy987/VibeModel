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
    /// Revit side of <see cref="StaticQuantityReport"/>: after a run that KEPT its change, the project
    /// parameters it created (a ParameterElement / SharedParameterElement among the added ids that is
    /// bound in <c>doc.ParameterBindings</c>) of a Length, Area, Volume or Number spec, and how many of
    /// the added or modified elements now carry a non-zero value on them.
    /// <para>
    /// Deliberately limited to parameters that did not exist before the run: then every value on them
    /// was written by the run, so no value pre-read is needed and an element Revit only regenerated
    /// cannot raise the line. A parameter that already existed (made by the user, or by VibeModel in an
    /// earlier call) and is filled now is not caught — that would need a before/after read of values.
    /// A family's own shared parameter (loaded with the family, not bound in the project) is skipped:
    /// its value comes from the family's formula. A value tied to a global parameter follows that one.
    /// </para>
    /// </summary>
    internal static class StaticQuantityCheck
    {
        private const int MaxReads = 5000;

        public static List<StaticQuantityReport.Entry> Find(Document doc, RecordedChanges changes)
        {
            if (doc == null || changes == null || changes.Added.Count == 0) return null;
            try
            {
                var sw = Stopwatch.StartNew();
                var parameters = new List<(Definition Definition, QuantityKind Spec)>();
                foreach (var id in changes.Added)
                {
                    if (!(doc.GetElement(id) is ParameterElement pe) || pe is GlobalParameter) continue;
                    var def = pe.GetDefinition();
                    if (def == null || doc.ParameterBindings.get_Item(def) == null) continue;
                    var spec = QuantitySpec(def);
                    if (spec != null) parameters.Add((def, spec.Value));
                }
                if (parameters.Count == 0) return null;

                var counts = new int[parameters.Count];
                int reads = 0;
                bool capped = false;
                foreach (var id in changes.Added.Concat(changes.Modified))
                {
                    if (reads >= MaxReads) { capped = true; break; }
                    var e = doc.GetElement(id);
                    if (e == null || e is ParameterElement) continue;
                    reads++;
                    for (int i = 0; i < parameters.Count; i++)
                        if (HasFixedNumber(e.get_Parameter(parameters[i].Definition))) counts[i]++;
                }
                if (sw.ElapsedMilliseconds > 200)
                    Logger.Info("Quantities check read " + reads + " elements in " + sw.ElapsedMilliseconds + " ms");

                var entries = new List<StaticQuantityReport.Entry>();
                for (int i = 0; i < parameters.Count; i++)
                    if (counts[i] > 0)
                        entries.Add(new StaticQuantityReport.Entry
                            { Parameter = parameters[i].Definition.Name, Elements = counts[i], StoppedAfter = capped ? MaxReads : 0, Spec = parameters[i].Spec });
                return entries.Count > 0 ? entries : null;
            }
            catch (Exception ex)
            {
                Logger.Warn("Quantities check skipped: " + ex.Message);
                return null;
            }
        }

        /// <summary>Length / Area / Volume / Number (counts included), else null: not a quantity.</summary>
        private static QuantityKind? QuantitySpec(Definition def)
        {
            ForgeTypeId spec;
            try { spec = def.GetDataType(); } catch { return null; }
            var kind = FormattingHelper.QuantityKindOf(spec, integerAsNumber: true);
            return CalculatedFieldHint.TypeLabel(kind) != null ? kind : (QuantityKind?)null;
        }

        private static bool HasFixedNumber(Parameter p)
        {
            if (p == null || !p.HasValue) return false;
            try
            {
                var global = p.GetAssociatedGlobalParameter();
                if (global != null && global != ElementId.InvalidElementId) return false;
            }
            catch { /* not associable: an ordinary value */ }
            switch (p.StorageType)
            {
                case StorageType.Double: return Math.Abs(p.AsDouble()) > 1e-12;
                case StorageType.Integer: return p.AsInteger() != 0;
                default: return false;
            }
        }
    }
}
