using System;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class ActiveViewCommand : IClaudeCommand
    {
        public string Name => "activeview";
        public string Description => "Info about the active view";
        public string Usage => "activeview";

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            View view;
            try { view = uiDoc.ActiveGraphicalView; }
            catch { view = null; }
            if (view == null)
                view = uiDoc.ActiveView;
            if (view == null)
                return "ERROR: No active view";

            var sb = new StringBuilder();
            sb.AppendLine("ACTIVE VIEW");
            sb.AppendLine("===========");
            sb.AppendLine();
            sb.AppendLine("Name: " + view.Name);
            sb.AppendLine("ID: " + view.Id.IntegerValue);
            sb.AppendLine("Type: " + view.ViewType);
            sb.AppendLine("Scale: 1:" + view.Scale);
            sb.AppendLine("Detail Level: " + view.DetailLevel);

            if (view.GenLevel != null)
                sb.AppendLine("Associated Level: " + view.GenLevel.Name);

            if (view is ViewPlan vp)
            {
                sb.AppendLine();
                sb.AppendLine("VIEW RANGE:");
                try
                {
                    var viewRange = vp.GetViewRange();
                    var topClip = viewRange.GetLevelId(PlanViewPlane.TopClipPlane);
                    var cutPlane = viewRange.GetLevelId(PlanViewPlane.CutPlane);
                    var bottomClip = viewRange.GetLevelId(PlanViewPlane.BottomClipPlane);

                    sb.AppendLine("  Top Clip: " + FormattingHelper.GetLevelName(doc, topClip) + " +" + FormattingHelper.FormatLength(viewRange.GetOffset(PlanViewPlane.TopClipPlane)));
                    sb.AppendLine("  Cut Plane: " + FormattingHelper.GetLevelName(doc, cutPlane) + " +" + FormattingHelper.FormatLength(viewRange.GetOffset(PlanViewPlane.CutPlane)));
                    sb.AppendLine("  Bottom Clip: " + FormattingHelper.GetLevelName(doc, bottomClip) + " +" + FormattingHelper.FormatLength(viewRange.GetOffset(PlanViewPlane.BottomClipPlane)));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  (Could not get view range: " + ex.Message + ")");
                }
            }

            return sb.ToString();
        }
    }
}
