using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// A callout over a rectangle of a plan or section (sheets 2): a new detail view at its own
    /// scale, or (--reference) a callout that points to an existing view. The rectangle is in
    /// the parent view's frame — the same convention as `crop --box`. The office pack's
    /// "callout" view defaults apply (else those of the view kind the callout becomes); the
    /// response names the view, its scale and its paper size, so `viewport` can follow.
    /// </summary>
    public class CalloutCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "callout";
        public string Description => "Callout in a plan/section over --box x1 y1 x2 y2 (plan: project mm; section: in-view x, elevation) → a new detail " +
                                     "view [--name] [--scale n] [--section: detail view from a plan]; --reference <view> points to an existing view";
        public string Usage => CalloutArgs.Usage;

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;
            var a = CalloutArgs.Parse(args);
            if (a.Error != null) return CommandResult.Error("BAD_ARGS", a.Error, "Usage: " + Usage);

            var parent = SheetHelper.FindView(doc, a.Parent);
            if (parent == null) return CommandResult.Error("ELEMENT_NOT_FOUND", "No view '" + a.Parent + "'.", "Run 'views'.");
            bool isPlan = parent is ViewPlan;
            bool isSection = parent.ViewType == ViewType.Section || parent.ViewType == ViewType.Elevation || parent.ViewType == ViewType.Detail;
            if (!isPlan && !isSection)
                return CommandResult.Error("BAD_ARGS", "'" + parent.Name + "' is a " + parent.ViewType + "; callouts go in plans, sections, elevations and detail views.", null);
            if (a.Name != null && new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Any(v => !v.IsTemplate && v.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Error("BAD_ARGS", "A view named '" + a.Name + "' already exists.", "Pick another --name.");

            // The two corners as model points on the parent's plane.
            var p1 = SheetHelper.ViewPoint(doc, parent, a.Box[0], a.Box[1], out var convention);
            var p2 = SheetHelper.ViewPoint(doc, parent, a.Box[2], a.Box[3], out _);
            double wMm = a.Box[2] - a.Box[0], hMm = a.Box[3] - a.Box[1];

            if (a.Reference != null) return Reference(doc, parent, a, p1, p2, convention);

            // Plan parent: a plan callout of the parent's own view type, or a detail view (--section).
            // Section/elevation parent: always a detail view.
            ViewFamilyType vft;
            if (isPlan && !a.Section)
                vft = doc.GetElement(parent.GetTypeId()) as ViewFamilyType;
            else
                vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .FirstOrDefault(t => t.ViewFamily == ViewFamily.Detail);
            if (vft == null)
                return CommandResult.Error("TYPE_NOT_LOADED", isPlan && !a.Section ? "The parent view has no view type." : "No detail view type in the document.", null);

            View view = null;
            string scaleNote = null;
            OfficeViewSetup setup = null;
            CommandResult setupError = null;
            var error = TransactionHelper.Execute(doc, "VibeModel: Create Callout", () =>
            {
                view = ViewSection.CreateCallout(doc, parent.Id, vft.Id, p1, p2);
                if (a.Name != null) view.Name = a.Name;
                setup = ResolveSetup(doc, view, a, out setupError);
                if (setupError != null) throw new OperationCanceledException();
                setup?.Apply(doc, view);
                if (a.Scale.HasValue && view.Scale != a.Scale.Value) scaleNote = SectionCommand.SetScale(view, a.Scale.Value);
            });
            if (setupError != null) return setupError;
            if (error != null)
                return CommandResult.FromTransactionError(error, "REVIT_ERROR", "Check that the --box lies inside the parent view's crop region.");

            int scale = Math.Max(1, view.Scale);
            var sb = new StringBuilder();
            sb.AppendLine("CALLOUT CREATED");
            sb.AppendLine("===============");
            sb.AppendLine("ID: " + view.Id.IntegerValue);
            sb.AppendLine("Name: " + view.Name + "  (" + view.ViewType + ", callout of '" + parent.Name + "')");
            sb.AppendLine("Scale: 1:" + view.Scale + (scaleNote != null ? "  (" + scaleNote + ")" : "") + (a.Scale.HasValue ? "" : "  (--scale n to change)"));
            sb.AppendLine("Box: (" + Mm.F(a.Box[0]) + ", " + Mm.F(a.Box[1]) + ") - (" + Mm.F(a.Box[2]) + ", " + Mm.F(a.Box[3]) + "), " + convention);
            sb.AppendLine("Model: " + Mm.F(wMm) + " × " + Mm.F(hMm) + " mm → on paper at 1:" + scale + ": " + Mm.F(wMm / scale) + " × " + Mm.F(hMm / scale) + " mm");
            setup?.AppendReport(sb);
            sb.AppendLine("Next: 'viewport <sheet> " + view.Id.IntegerValue + "' to put it on a sheet; 'screenshot --view " + view.Id.IntegerValue + "' to look at it.");

            var data = new Dictionary<string, object>
            {
                { "created", new List<object> { view.Id.IntegerValue } },
                { "name", view.Name },
                { "viewType", view.ViewType.ToString() },
                { "parent", parent.Id.IntegerValue },
                { "scale", view.Scale },
                { "modelMm", new List<object> { Math.Round(wMm), Math.Round(hMm) } },
                { "paperMm", new List<object> { Math.Round(wMm / scale), Math.Round(hMm / scale) } }
            };
            if (setup != null) data["office"] = setup.ToJson();
            return CommandResult.Ok(sb.ToString(), data);
        }

        /// <summary>The pack's "callout" view defaults when it has them, else those of the kind the callout became (plan / section).</summary>
        private static OfficeViewSetup ResolveSetup(Document doc, View view, CalloutArgs a, out CommandResult error)
        {
            var pack = StandardsContext.GetActivePack(doc);
            string kind = pack != null && pack.ViewDefaults.ContainsKey("callout") ? "callout" : view is ViewPlan ? "plan" : "section";
            return OfficeViewSetup.Resolve(doc, kind, a.Template, a.NoTemplate, out error);
        }

        private static CommandResult Reference(Document doc, View parent, CalloutArgs a, XYZ p1, XYZ p2, string convention)
        {
            var target = SheetHelper.FindView(doc, a.Reference);
            if (target == null || target is ViewSheet)
                return CommandResult.Error("ELEMENT_NOT_FOUND", "No view '" + a.Reference + "' to reference.", "Run 'views'.");
            var error = TransactionHelper.Execute(doc, "VibeModel: Reference Callout", () =>
                ViewSection.CreateReferenceCallout(doc, parent.Id, target.Id, p1, p2));
            if (error != null)
                return CommandResult.FromTransactionError(error, "REVIT_ERROR",
                    "A reference callout must point to a view of a matching kind (a plan or detail view from a plan; a detail/section view from a section).");
            return CommandResult.Ok("REFERENCE CALLOUT CREATED\n=========================\nIn '" + parent.Name + "' over (" + Mm.F(a.Box[0]) + ", " + Mm.F(a.Box[1]) + ") - (" +
                                    Mm.F(a.Box[2]) + ", " + Mm.F(a.Box[3]) + "), " + convention + "\nPoints to: '" + target.Name + "' (" + target.Id.IntegerValue + ") — no new view.\n",
                new Dictionary<string, object> { { "parent", parent.Id.IntegerValue }, { "references", target.Id.IntegerValue } });
        }
    }
}
