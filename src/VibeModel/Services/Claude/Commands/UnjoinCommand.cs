using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Remove whatever geometric relation exists between two elements: a geometry join, a
    /// solid-solid cut, or both. Mirrors <see cref="JoinCommand"/> so the same pair of IDs
    /// undoes what join created, whichever API made it.
    /// </summary>
    public class UnjoinCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "unjoin";
        public string Description => "Remove a geometry join and/or solid cut between two elements";
        public string Usage => "unjoin <idA> <idB>";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var parsed = JoinArgs.ParseUnjoin(args);
            if (parsed.Error != null)
                return CommandResult.Error("BAD_ARGS", parsed.Error, Usage);

            var pair = JoinPair.Resolve(doc, parsed.IdA, parsed.IdB);
            if (pair.Error != null)
                return CommandResult.Error("ELEMENT_NOT_FOUND", pair.Error,
                    "Run 'selected' or 'list walls' to get valid element IDs.");

            bool joined = JoinGeometryUtils.AreElementsJoined(doc, pair.A, pair.B);
            bool cutFirstCutsSecond;
            bool cut = SolidSolidCutUtils.CutExistsBetweenElements(pair.A, pair.B, out cutFirstCutsSecond);

            if (!joined && !cut)
                return CommandResult.Error("NOT_JOINED",
                    "Elements " + pair.IdA + " and " + pair.IdB + " are neither joined nor cutting each other — " +
                    "there is nothing to remove.",
                    "Run 'join " + pair.IdA + " " + pair.IdB + "' to create a join, or add --cut for a solid cut.");

            var removed = new List<string>();

            if (joined)
            {
                var error = TransactionHelper.Execute(doc, "VibeModel: Unjoin Geometry", () =>
                {
                    JoinGeometryUtils.UnjoinGeometry(doc, pair.A, pair.B);
                });
                if (error != null)
                    return pair.Failure("UNJOIN_FAILED",
                        "Unjoin failed — Revit refused to break the join between " + pair.IdA + " and " + pair.IdB + ".",
                        "JoinGeometryUtils.UnjoinGeometry", JoinCommand.StripPrefix(error),
                        new List<string>
                        {
                            "The join may be held by another element in a chain of joins; unjoin that one first.",
                            "If either element sits in a group or a linked model, edit it there instead."
                        });
                removed.Add("geometry join");
            }

            if (cut)
            {
                var error = TransactionHelper.Execute(doc, "VibeModel: Remove Solid Cut", () =>
                {
                    SolidSolidCutUtils.RemoveCutBetweenSolids(doc, pair.A, pair.B);
                });
                if (error != null)
                    return pair.Failure("UNCUT_FAILED",
                        "Removing the solid cut between " + pair.IdA + " and " + pair.IdB + " failed.",
                        "SolidSolidCutUtils.RemoveCutBetweenSolids", JoinCommand.StripPrefix(error),
                        new List<string>
                        {
                            "Check whether the cut was made by a void family instead — those are removed with " +
                            "Revit's 'Uncut Geometry' tool, not through this API."
                        });
                removed.Add(cutFirstCutsSecond
                    ? "solid cut (" + pair.IdA + " was cutting " + pair.IdB + ")"
                    : "solid cut (" + pair.IdB + " was cutting " + pair.IdA + ")");
            }

            var m = pair.BaseReport("UNJOINED");
            m.ApiTried = joined && cut
                ? "JoinGeometryUtils.UnjoinGeometry + SolidSolidCutUtils.RemoveCutBetweenSolids"
                : joined ? "JoinGeometryUtils.UnjoinGeometry" : "SolidSolidCutUtils.RemoveCutBetweenSolids";
            m.Outcome = "Removed: " + string.Join(" and ", removed) + ".";

            var data = pair.BaseData("unjoined", m.ApiTried, null);
            data["removedJoin"] = joined;
            data["removedCut"] = cut;
            return CommandResult.Ok(JoinReport.Build(m), data);
        }
    }
}
