using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VibeModel.Services.Claude.Commands
{
    public class SelectCommand : IClaudeCommand
    {
        public string Name => "select";
        public string Description => "Select element(s) by ID";
        public string Usage => "select <id> [id2,id3,...]";

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            var idString = args?.Trim() ?? "";
            var idStrings = idString.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var ids = new List<ElementId>();

            foreach (var s in idStrings)
            {
                if (int.TryParse(s.Trim(), out int idValue))
                {
                    var elem = doc.GetElement(new ElementId(idValue));
                    if (elem != null)
                        ids.Add(new ElementId(idValue));
                }
            }

            if (ids.Count == 0)
                return "ERROR: No valid element IDs provided";

            uiDoc.Selection.SetElementIds(ids);
            return "Selected " + ids.Count + " element(s): " + string.Join(", ", ids.Select(id => id.IntegerValue));
        }
    }
}
