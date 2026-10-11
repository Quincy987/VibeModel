using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// A text note in the active view (plan 12), optionally with a leader to a point. The
    /// building block for annotation — including annotating from a specification: the chat
    /// reads the document and calls `text` per remark.
    /// </summary>
    public class TextCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "text";
        public string Description => "Place a text note in the active view (plan: project x,y mm; section/elevation: x = position along the view as " +
                                     "printed by 'section', y = elevation mm; sheet: paper mm). '\\n' = new line. --leader x y points at something";
        public string Usage => TextArgs.Usage;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc.Document;
            var a = TextArgs.Parse(args);
            if (a.Error != null) return CommandResult.Error("BAD_ARGS", a.Error, "Usage: " + Usage);

            var view = FormattingHelper.GetActiveGraphicalView(uidoc);
            if (view == null || view is View3D)
                return CommandResult.Error("BAD_ARGS", "Text notes need a plan, section, elevation, drafting view or sheet as the active view" +
                                           (view != null ? " (active: 3D view '" + view.Name + "')" : "") + ".",
                                           "'activate <view or sheet number>' first, in a separate call.");

            var types = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
            if (types.Count == 0) return CommandResult.Error("TYPE_NOT_LOADED", "No text note types in the document.", null);
            TextNoteType type;
            string typeNote = null;
            bool needType = false;   // --size with no type of that height: copy the nearest one inside the transaction
            if (a.Type != null)
            {
                type = types.FirstOrDefault(t => t.Name.Equals(a.Type, StringComparison.OrdinalIgnoreCase));
                if (type == null)
                    return CommandResult.Error("ELEMENT_NOT_FOUND", "No text type '" + a.Type + "'. Available: " + string.Join(", ", types.Select(t => t.Name).Take(20)), null);
            }
            else if (a.Size.HasValue)
            {
                type = types.OrderBy(t => Math.Abs(SizeMm(t) - a.Size.Value)).First();
                needType = Math.Abs(SizeMm(type) - a.Size.Value) > 0.05;
            }
            else type = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) as TextNoteType ?? types[0];

            var pos = SheetHelper.ViewPoint(doc, view, a.X, a.Y, out var convention);
            XYZ leaderEnd = a.Leader != null ? SheetHelper.ViewPoint(doc, view, a.Leader[0], a.Leader[1], out _) : null;

            TextNote note = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create Text", () =>
            {
                if (needType)
                {
                    // No type at that size: copy the nearest and set its height (paper mm).
                    var name = "VibeModel " + Mm.F1(a.Size.Value) + " mm";
                    var existing = types.FirstOrDefault(t => t.Name == name);
                    if (existing != null) { type = existing; typeNote = "type '" + name + "'"; }
                    else
                    {
                        type = (TextNoteType)type.Duplicate(name);
                        type.get_Parameter(BuiltInParameter.TEXT_SIZE).Set(RevitUnitHelper.MmToFeet(a.Size.Value));
                        typeNote = "created text type '" + name + "' (no existing type at " + Mm.F1(a.Size.Value) + " mm)";
                    }
                }
                note = TextNote.Create(doc, view.Id, pos, a.Text, type.Id);
                if (leaderEnd != null)
                {
                    var leader = note.AddLeader(TextNoteLeaderTypes.TNLT_STRAIGHT_L);
                    leader.End = leaderEnd;
                }
            });
            if (error != null)
                return CommandResult.FromTransactionError(error, "REVIT_ERROR", "A text note must lie in the view's plane; check the coordinates (" + convention + ").");

            var sb = new StringBuilder();
            sb.AppendLine("TEXT CREATED");
            sb.AppendLine("============");
            sb.AppendLine();
            sb.AppendLine("ID: " + note.Id.IntegerValue);
            sb.AppendLine("View: " + view.Name + " (" + view.ViewType + ")");
            sb.AppendLine("At: " + Mm.F(a.X) + ", " + Mm.F(a.Y) + " (" + convention + ")" + (a.Leader != null ? ", leader to " + Mm.F(a.Leader[0]) + ", " + Mm.F(a.Leader[1]) : ""));
            sb.AppendLine("Type: " + type.Name + " (" + Mm.F1(SizeMm(type)) + " mm on paper)" + (typeNote != null ? " — " + typeNote : ""));
            sb.AppendLine("Text: " + a.Text.Replace("\n", " ⏎ "));

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { note.Id.IntegerValue } },
                { "view", view.Id.IntegerValue },
                { "type", type.Name },
                { "text", a.Text }
            };
            return CommandResult.Ok(sb.ToString(), data);
        }

        private static double SizeMm(TextNoteType t)
        {
            var p = t.get_Parameter(BuiltInParameter.TEXT_SIZE);
            return p != null ? RevitUnitHelper.FeetToMm(p.AsDouble()) : 0;
        }
    }
}
