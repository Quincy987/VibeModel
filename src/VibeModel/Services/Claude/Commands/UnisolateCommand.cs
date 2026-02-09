using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class UnisolateCommand : IClaudeCommand, IModificationCommand
    {
        public string Name => "unisolate";
        public string Description => "Clear temporary isolation in the active view";
        public string Usage => "unisolate";

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var view = FormattingHelper.GetActiveGraphicalView(uiDoc);
            if (view == null)
                return "ERROR: No graphical view active. Switch to a plan, section, or 3D view.";

            var error = TransactionHelper.Execute(doc, "VibeModel: Clear Isolation", () =>
            {
                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            });

            if (error != null) return error;

            return "Cleared temporary isolation in view '" + view.Name + "'.";
        }
    }
}
