using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class DeleteCommand : IClaudeCommand, IModificationCommand
    {
        public string Name => "delete";
        public string Description => "Delete elements by ID";
        public string Usage => "delete <id> [id2] [id3] ...";

        public string Execute(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

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
                    if (doc.GetElement(elemId) != null)
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

            var error = TransactionHelper.Execute(doc, "VibeModel: Delete Elements", () =>
            {
                foreach (var id in idsToDelete)
                    doc.Delete(id);
            });

            if (error != null) return error;

            var sb = new StringBuilder();
            sb.AppendLine("DELETED " + idsToDelete.Count + " ELEMENT(S)");
            sb.AppendLine("IDs: " + string.Join(", ", idsToDelete.Select(id => id.IntegerValue)));

            if (notFound.Count > 0)
                sb.AppendLine("\nNot found: " + string.Join(", ", notFound));

            return sb.ToString();
        }
    }
}
