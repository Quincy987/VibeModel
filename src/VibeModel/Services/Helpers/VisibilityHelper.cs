using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VibeModel.Services.Helpers
{
    public static class VisibilityHelper
    {
        public static string SetCategoryVisibility(Document doc, UIDocument uiDoc, BuiltInCategory cat, bool hide)
        {
            var view = FormattingHelper.GetActiveGraphicalView(uiDoc);
            if (view == null)
                return "ERROR: No graphical view active. Switch to a plan, section, or 3D view.";

            var catId = new ElementId(cat);
            if (!view.CanCategoryBeHidden(catId))
                return "ERROR: Category visibility cannot be changed in this view.";

            var action = hide ? "Hide" : "Show";
            var error = TransactionHelper.Execute(doc, "VibeModel: " + action + " Category", () =>
            {
                view.SetCategoryHidden(catId, hide);
            });

            return error;
        }

        public static string SetElementVisibility(Document doc, UIDocument uiDoc, ICollection<ElementId> ids, bool hide)
        {
            var view = FormattingHelper.GetActiveGraphicalView(uiDoc);
            if (view == null)
                return "ERROR: No graphical view active. Switch to a plan, section, or 3D view.";

            // Filter to elements that can actually be hidden in this view
            var validIds = ids.Where(id => doc.GetElement(id) != null).ToList();
            if (validIds.Count == 0)
                return "ERROR: None of the specified elements can be " + (hide ? "hidden" : "shown") + " in this view.";

            var action = hide ? "Hide" : "Show";
            var error = TransactionHelper.Execute(doc, "VibeModel: " + action + " Elements", () =>
            {
                if (hide)
                    view.HideElements(validIds);
                else
                    view.UnhideElements(validIds);
            });

            return error;
        }
    }
}
