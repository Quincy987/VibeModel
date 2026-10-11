using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VibeModel.Services.Claude;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// A creating command's phase decision, prepared BEFORE its transaction and applied to each
    /// new element INSIDE it. When no office pack is active and no --phase was given the request
    /// is inert: the args come back unchanged, nothing is applied, no note is added — so every
    /// command behaves exactly as before.
    /// </summary>
    public sealed class PhaseRequest
    {
        public string Remaining { get; internal set; }
        public string Error { get; internal set; }
        public string Hint { get; internal set; }
        public StandardsPack Pack { get; internal set; }
        public PhaseMapping Mapping { get; internal set; }
        public bool Explicit { get; internal set; }
        internal Phase Created { get; set; }
        internal Phase Demolished { get; set; }
        /// <summary>Why a default could not be applied (pack phase missing in this document).</summary>
        internal string SkipReason { get; set; }

        public int Applied { get; private set; }
        public int NotPhased { get; private set; }
        private readonly List<string> _failures = new List<string>();

        public bool IsActive => Mapping != null && Created != null;

        /// <summary>Legacy (string) command error form: "ERROR: msg" + "Hint: ..." line.</summary>
        public string ErrorText => "ERROR: " + Error + (string.IsNullOrEmpty(Hint) ? "" : "\nHint: " + Hint);

        public CommandResult ErrorResult => CommandResult.Error("BAD_ARGS", Error, Hint);

        /// <summary>Append <see cref="Note"/> as a line when there is one.</summary>
        public void AppendNote(System.Text.StringBuilder sb, Document doc, View activeView)
        {
            var note = Note(doc, activeView);
            if (note != null) sb.AppendLine(note);
        }

        /// <summary>Set Phase Created/Demolished. Call inside the command's open transaction.</summary>
        public void Apply(Element e)
        {
            if (!IsActive || e == null) return;
            try
            {
                bool phased;
                try { phased = e.HasPhases() && e.ArePhasesModifiable(); }
                catch { phased = false; }
                if (!phased) { NotPhased++; return; }

                // Clear demolished first so a later created phase never conflicts with it.
                e.DemolishedPhaseId = ElementId.InvalidElementId;
                e.CreatedPhaseId = Created.Id;
                if (Demolished != null) e.DemolishedPhaseId = Demolished.Id;
                Applied++;
            }
            catch (Exception ex)
            {
                _failures.Add(e.Id.IntegerValue + ": " + ex.Message);
            }
        }

        /// <summary>
        /// One line for the command output, or null when nothing is worth saying. Warns when the
        /// active view cannot show the element (view phase earlier than the created phase).
        /// </summary>
        public string Note(Document doc, View activeView)
        {
            if (Mapping == null)
                return SkipReason != null ? "phase: default not applied — " + SkipReason : null;
            if (Applied == 0 && _failures.Count == 0)
                return Explicit && NotPhased > 0 ? "phase: not applicable (this element type has no phases)" : null;

            var line = "phase: " + Created.Name +
                       (Demolished != null ? " → demolished in " + Demolished.Name : "") +
                       (Explicit
                           ? " (--phase " + Mapping.Key + ")"
                           : " (office default '" + Mapping.Key + "'; override with --phase " +
                             PhaseArgs.KeyList(Pack.PhaseTable.Keys.Where(k => !string.Equals(k, Mapping.Key, StringComparison.OrdinalIgnoreCase))) + ")");
            if (_failures.Count > 0)
                line += "; FAILED on " + string.Join(", ", _failures.Take(3));

            var hidden = VisibilityWarning(doc, activeView);
            if (hidden != null) line += Environment.NewLine + hidden;
            return line;
        }

        private string VisibilityWarning(Document doc, View v)
        {
            try
            {
                if (v == null || v.IsTemplate) return null;
                var viewPhaseId = v.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
                if (viewPhaseId == null || viewPhaseId == ElementId.InvalidElementId) return null;
                int viewIdx = PhaseDefaults.IndexOf(doc, viewPhaseId);
                int createdIdx = PhaseDefaults.IndexOf(doc, Created.Id);
                if (viewIdx < 0 || createdIdx < 0 || createdIdx <= viewIdx) return null;
                return "  ! not visible in the active view '" + v.Name + "' (view phase '" +
                       doc.GetElement(viewPhaseId)?.Name + "' is before '" + Created.Name +
                       "'). Open or create a view on that phase (e.g. `view plan`) to see it.";
            }
            catch { return null; }
        }

        public Dictionary<string, object> ToJson()
        {
            if (Mapping == null) return null;
            return new Dictionary<string, object>
            {
                { "key", Mapping.Key },
                { "created", Created?.Name },
                { "demolished", Demolished?.Name },
                { "explicit", Explicit },
                { "applied", Applied }
            };
        }
    }

    public static class PhaseDefaults
    {
        /// <summary>
        /// Parse --phase out of the args and resolve it against the document's active pack.
        /// Errors only for an explicit, unusable --phase; a default that cannot be applied
        /// degrades to a note so the command still runs.
        /// </summary>
        public static PhaseRequest Prepare(Document doc, string args)
        {
            var pa = PhaseArgs.Parse(args);
            var req = new PhaseRequest { Remaining = pa.Remaining, Explicit = pa.Explicit };
            if (pa.Error != null)
            {
                req.Error = pa.Error;
                req.Hint = "Use --phase existing|new|temp|demolish|future.";
                return req;
            }

            var pack = StandardsContext.GetActivePack(doc);
            if (pack?.PhaseTable == null)
            {
                if (pa.Explicit)
                {
                    req.Error = "--phase needs an active office standard with a phase table; none applies to this document.";
                    req.Hint = "Run 'standards' to see which packs were found and why none matched, or set the phase afterwards with 'set <id> Phase Created <name>'.";
                }
                return req;
            }

            req.Pack = pack;
            var mapping = pa.Explicit ? pack.PhaseTable.Resolve(pa.Key) : pack.PhaseTable.Default;
            if (mapping == null)
            {
                if (pa.Explicit)
                {
                    req.Error = "Unknown phase kind '" + pa.Key + "'.";
                    req.Hint = "Use one of: " + PhaseArgs.KeyList(pack.PhaseTable.Keys) +
                               (pack.PhaseTable.Aliases.Count > 0 ? " (aliases: " + string.Join(", ", pack.PhaseTable.Aliases.Keys) + ")" : "") + ".";
                }
                return req;
            }

            var created = FindPhase(doc, mapping.Created);
            var demolished = mapping.Demolished != null ? FindPhase(doc, mapping.Demolished) : null;
            if (created == null || (mapping.Demolished != null && demolished == null))
            {
                var missing = created == null ? mapping.Created : mapping.Demolished;
                if (pa.Explicit)
                {
                    req.Error = "Phase '" + missing + "' (pack " + pack.Id + ") does not exist in this document.";
                    req.Hint = "Phases here: " + string.Join(", ", AllPhases(doc).Select(p => p.Name)) + ".";
                }
                else
                {
                    req.SkipReason = "phase '" + missing + "' from pack " + pack.Id + " is not in this document";
                }
                return req;
            }

            req.Mapping = mapping;
            req.Created = created;
            req.Demolished = demolished;
            return req;
        }

        public static Phase FindPhase(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return AllPhases(doc).FirstOrDefault(p => string.Equals(p.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public static List<Phase> AllPhases(Document doc)
        {
            var list = new List<Phase>();
            foreach (Phase p in doc.Phases) list.Add(p);
            return list;
        }

        public static int IndexOf(Document doc, ElementId phaseId)
        {
            int i = 0;
            foreach (Phase p in doc.Phases)
            {
                if (p.Id == phaseId) return i;
                i++;
            }
            return -1;
        }
    }
}
