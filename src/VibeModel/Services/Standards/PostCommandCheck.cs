using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// After a modifying command or batch: run the element-level office rules over exactly the
    /// elements it created. Returns null when no pack/rules apply or nothing was created, so a
    /// project without office standards sees byte-identical output.
    /// </summary>
    public static class PostCommandCheck
    {
        public static List<CheckFinding> Run(UIApplication app, IEnumerable<ElementId> created)
        {
            try
            {
                var uidoc = app?.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null || created == null) return null;
                var ids = created.Where(id => id != null && id != ElementId.InvalidElementId)
                    .Distinct().Where(id => doc.GetElement(id) != null).ToList();
                if (ids.Count == 0) return null;

                var pack = StandardsContext.GetActivePack(doc);
                if (pack == null) return null;
                var rules = ModelChecks.LoadRules(pack, out _);
                if (rules.Count == 0) return null;

                var findings = ModelChecks.Run(doc, uidoc.ActiveView, pack, rules, ids);
                CheckLog.Append(ModelChecks.LogPath(pack), pack.Id, "post-command", findings);
                return findings;
            }
            catch (Exception ex)
            {
                Logger.Warn("Post-command check failed: " + ex.Message);
                return null;
            }
        }
    }
}
