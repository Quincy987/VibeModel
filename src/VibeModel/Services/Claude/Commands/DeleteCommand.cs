using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VibeModel.Services.Claude.Commands
{
    public class DeleteCommand : IClaudeCommand
    {
        public string Name => "delete";
        public string Description => "Delete elements by ID";
        public string Usage => "delete <id> [id2] [id3] ...";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return "ERROR: No document open";

            var parts = (args ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return "ERROR: Usage: delete <id> [id2] [id3] ...";

            var idsToDelete = new List<ElementId>();
            var notFound = new List<string>();

            foreach (var part in parts)
            {
                if (int.TryParse(part.Trim(), out int idValue))
                {
                    var elemId = new ElementId(idValue);
                    var elem = doc.GetElement(elemId);
                    if (elem != null)
                        idsToDelete.Add(elemId);
                    else
                        notFound.Add(part);
                }
                else
                {
                    notFound.Add(part);
                }
            }

            if (idsToDelete.Count == 0)
                return "ERROR: No valid element IDs found";

            int deletedCount = 0;
            using (var trans = new Transaction(doc, "VibeModel: Delete Elements"))
            {
                trans.Start();
                try
                {
                    foreach (var id in idsToDelete)
                    {
                        doc.Delete(id);
                        deletedCount++;
                    }
                    trans.Commit();
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    return "ERROR: Failed to delete elements: " + ex.Message;
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("DELETED " + deletedCount + " ELEMENT(S)");
            sb.AppendLine("IDs: " + string.Join(", ", idsToDelete.Select(id => id.IntegerValue)));

            if (notFound.Count > 0)
                sb.AppendLine("\nNot found: " + string.Join(", ", notFound));

            return sb.ToString();
        }
    }
}
