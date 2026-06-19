using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    public class TagCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "tag";
        public string Description => "Tag an element by ID in the active view";
        public string Usage => "tag <elementId>";

        // Explicit element-category -> tag-category map (correct, not a name heuristic).
        private static readonly Dictionary<BuiltInCategory, BuiltInCategory> TagMap =
            new Dictionary<BuiltInCategory, BuiltInCategory>
            {
                { BuiltInCategory.OST_Walls, BuiltInCategory.OST_WallTags },
                { BuiltInCategory.OST_Doors, BuiltInCategory.OST_DoorTags },
                { BuiltInCategory.OST_Windows, BuiltInCategory.OST_WindowTags },
                { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_RoomTags },
                { BuiltInCategory.OST_Floors, BuiltInCategory.OST_FloorTags },
                { BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_CeilingTags },
                { BuiltInCategory.OST_Roofs, BuiltInCategory.OST_RoofTags },
                { BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralColumnTags },
                { BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFramingTags },
                { BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_StructuralFoundationTags },
                { BuiltInCategory.OST_Furniture, BuiltInCategory.OST_FurnitureTags },
                { BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_GenericModelTags },
                { BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_MechanicalEquipmentTags },
                { BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_PlumbingFixtureTags },
                { BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingFixtureTags },
            };

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            if (!int.TryParse((args ?? "").Trim(), out int eid))
                return CommandResult.Error("BAD_ARGS", "Usage: tag <elementId>", Usage);

            var element = doc.GetElement(new ElementId(eid));
            if (element == null)
                return CommandResult.Error("ELEMENT_NOT_FOUND", "Element " + eid + " not found.",
                    "Run 'list <category>' to find valid element IDs.");

            var view = FormattingHelper.GetActiveGraphicalView(uiDoc);
            if (view == null)
                return CommandResult.Error("NO_ACTIVE_VIEW",
                    "No active graphical view to place the tag in.",
                    "Switch to a plan, section, elevation, or 3D view.");

            if (element.Category == null)
                return CommandResult.Error("BAD_ARGS", "Element has no category to tag.", null);

            var elemBic = (BuiltInCategory)element.Category.Id.IntegerValue;
            if (!TagMap.TryGetValue(elemBic, out var tagBic))
                return CommandResult.Error("TYPE_NOT_LOADED",
                    "Tagging isn't supported for category '" + element.Category.Name + "' yet.", null);

            var tagSymbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(tagBic)
                .Cast<FamilySymbol>()
                .FirstOrDefault();
            if (tagSymbol == null)
                return CommandResult.Error("TYPE_NOT_LOADED",
                    "No tag family loaded for " + element.Category.Name + ".",
                    "Load a " + element.Category.Name + " tag family first.");

            var bbox = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            XYZ mid = bbox != null ? (bbox.Min + bbox.Max) * 0.5 : XYZ.Zero;

            IndependentTag tag = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create Tag", () =>
            {
                if (!tagSymbol.IsActive)
                {
                    tagSymbol.Activate();
                    doc.Regenerate();
                }
                tag = IndependentTag.Create(doc, tagSymbol.Id, view.Id,
                    new Reference(element), false, TagOrientation.Horizontal, mid);
            });
            if (error != null)
                return CommandResult.FromTransactionError(error);

            var sb = new StringBuilder();
            sb.AppendLine("TAG CREATED");
            sb.AppendLine("===========");
            sb.AppendLine();
            sb.AppendLine("ID: " + tag.Id.IntegerValue);
            sb.AppendLine("Tags element: " + eid);
            sb.AppendLine("Type: " + tagSymbol.Family.Name + " : " + tagSymbol.Name);
            sb.AppendLine("View: " + view.Name);

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { tag.Id.IntegerValue } },
                { "taggedElement", eid },
                { "type", tagSymbol.Family.Name + " : " + tagSymbol.Name },
                { "view", view.Name }
            };
            return CommandResult.Ok(sb.ToString(), data);
        }
    }
}
