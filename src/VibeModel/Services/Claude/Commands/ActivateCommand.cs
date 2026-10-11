using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Make a view the active view (by id or exact name). The one thing the AI could not do
    /// through exec (a view switch is refused inside an open transaction): after creating new
    /// work on the office's new-work phase, switch to a view that can show it.
    /// </summary>
    public class ActivateCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "activate";
        public string Description => "Activate a view by id or name, or a sheet by number (e.g. the work view just created with 'view')";
        public string Usage => "activate <viewId|view name|sheet number>";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc.Document;
            var q = (args ?? "").Trim();
            if (q.Length == 0)
                return CommandResult.Error("BAD_ARGS", "Usage: " + Usage, "Run 'views' to list views.");

            View view = null;
            if (int.TryParse(q, out var id))
                view = doc.GetElement(new ElementId(id)) as View;
            if (view == null)
                view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
            if (view == null)
                view = Helpers.SheetHelper.FindSheet(doc, q);
            if (view == null)
                return CommandResult.Error("ELEMENT_NOT_FOUND", "No view '" + q + "'.", "Run 'views' to list view ids and names.");
            if (view.IsTemplate)
                return CommandResult.Error("BAD_ARGS", "'" + view.Name + "' is a view template; it cannot be opened.", null);

            return Activate(uidoc, view);
        }

        internal static CommandResult Activate(UIDocument uidoc, View view)
        {
            try
            {
                uidoc.RequestViewChange(view);
            }
            catch (Exception ex)
            {
                return CommandResult.Error("REVIT_ERROR", "Revit refused to activate '" + view.Name + "': " + ex.Message, null);
            }
            var phase = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
            var phaseName = phase != null && phase.AsElementId() != ElementId.InvalidElementId
                ? view.Document.GetElement(phase.AsElementId())?.Name : null;
            return CommandResult.Ok("VIEW ACTIVATED\n==============\n" + view.Name + " (id " + view.Id.IntegerValue + ", " + view.ViewType + ")" +
                                    (phaseName != null ? "\nPhase: " + phaseName : "") + "\n",
                new Dictionary<string, object>
                {
                    { "id", view.Id.IntegerValue }, { "name", view.Name }, { "type", view.ViewType.ToString() }, { "phase", phaseName }
                });
        }
    }
}
