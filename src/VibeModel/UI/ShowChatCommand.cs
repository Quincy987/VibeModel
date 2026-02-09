using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;

namespace VibeModel.UI
{
    [Transaction(TransactionMode.Manual)]
    public class ShowChatCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var pane = commandData.Application.GetDockablePane(ChatPane.PaneId);
                if (pane == null)
                {
                    message = "Chat pane not registered";
                    return Result.Failed;
                }

                if (pane.IsShown())
                    pane.Hide();
                else
                    pane.Show();

                return Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                Logger.Error("ShowChatCommand failed", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
