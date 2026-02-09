using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class ZoomToCommand : IClaudeCommand
    {
        public string Name => "zoomto";
        public string Description => "Zoom to element(s) in active view";
        public string Usage => "zoomto <id> [id2...] | zoomto selected";

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var input = (args ?? "").Trim();
            if (string.IsNullOrEmpty(input))
                return "ERROR: Usage: zoomto <id> [id2...] | zoomto selected";

            var ids = new List<ElementId>();

            if (input.Equals("selected", StringComparison.OrdinalIgnoreCase))
            {
                ids = uiDoc.Selection.GetElementIds().ToList();
                if (ids.Count == 0)
                    return "ERROR: No elements selected.";
            }
            else
            {
                var parts = input.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                ids = FormattingHelper.ParseElementIds(doc, parts);
                if (ids.Count == 0)
                    return "ERROR: No valid element IDs found.";
            }

            // Compute combined bounding box
            BoundingBoxXYZ combined = null;
            foreach (var id in ids)
            {
                var elem = doc.GetElement(id);
                var bbox = elem?.get_BoundingBox(null);
                if (bbox == null) continue;

                if (combined == null)
                {
                    combined = new BoundingBoxXYZ
                    {
                        Min = bbox.Min,
                        Max = bbox.Max
                    };
                }
                else
                {
                    combined.Min = new XYZ(
                        Math.Min(combined.Min.X, bbox.Min.X),
                        Math.Min(combined.Min.Y, bbox.Min.Y),
                        Math.Min(combined.Min.Z, bbox.Min.Z));
                    combined.Max = new XYZ(
                        Math.Max(combined.Max.X, bbox.Max.X),
                        Math.Max(combined.Max.Y, bbox.Max.Y),
                        Math.Max(combined.Max.Z, bbox.Max.Z));
                }
            }

            // Select the elements
            try { uiDoc.Selection.SetElementIds(ids); }
            catch { /* selection may fail in some views */ }

            var activeViewName = uiDoc.ActiveView?.Name ?? "?";

            // Try UIView.ZoomAndCenterRectangle (stays in current view)
            if (combined != null)
            {
                double padding = 1000.0 / 304.8; // 1m padding in feet
                var min = new XYZ(combined.Min.X - padding, combined.Min.Y - padding, combined.Min.Z - padding);
                var max = new XYZ(combined.Max.X + padding, combined.Max.Y + padding, combined.Max.Z + padding);

                try
                {
                    var uiViews = uiDoc.GetOpenUIViews();
                    var activeViewId = uiDoc.ActiveView.Id;
                    var uiView = uiViews.FirstOrDefault(v => v.ViewId == activeViewId);

                    if (uiView != null)
                    {
                        uiView.ZoomAndCenterRectangle(min, max);

                        var sb = new StringBuilder();
                        sb.AppendLine("ZOOMED TO " + ids.Count + " ELEMENT(S)");
                        sb.AppendLine("View: " + activeViewName);
                        sb.AppendLine("IDs: " + string.Join(", ", ids.Select(id => id.IntegerValue)));
                        return sb.ToString();
                    }
                }
                catch { /* fall through to ShowElements */ }
            }

            // Fallback: ShowElements (may switch views)
            try
            {
                uiDoc.ShowElements(ids);
                var newViewName = uiDoc.ActiveView?.Name ?? "?";

                var sb = new StringBuilder();
                sb.AppendLine("ZOOMED TO " + ids.Count + " ELEMENT(S)");
                if (newViewName != activeViewName)
                    sb.AppendLine("Note: View switched from '" + activeViewName + "' to '" + newViewName + "'");
                else
                    sb.AppendLine("View: " + newViewName);
                sb.AppendLine("IDs: " + string.Join(", ", ids.Select(id => id.IntegerValue)));
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "ERROR: Could not zoom to elements: " + ex.Message;
            }
        }
    }
}
