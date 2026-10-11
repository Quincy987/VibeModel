using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>Survey point, project base point, shared-coordinate status and pins in one call.</summary>
    public class CoordsCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "coords";
        public string Description => "Coordinate setup: survey point, project base point (N/S, E/W, elevation, angle), shared coordinates, pins";
        public string Usage => "coords";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;
            var c = TemplateFactsCollector.ReadCoordinates(doc);
            if (!c.Available)
                return CommandResult.Error("NOT_AVAILABLE", "This document has no project base point / survey point.", null);

            var pack = StandardsContext.GetActivePack(doc);
            double round = pack?.CoordinateRoundingMm ?? 1000;
            bool rounded = StandardsRender.IsRound(c.BaseNorthMm, round) && StandardsRender.IsRound(c.BaseEastMm, round);

            int links = 0, linksPinned = 0;
            foreach (ImportInstance ii in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)))
                if (ii.IsLinked) { links++; if (ii.Pinned) linksPinned++; }

            var sb = new StringBuilder();
            sb.AppendLine("COORDINATES");
            sb.AppendLine("===========");
            sb.AppendLine("Survey point: " + (c.SurveyAtSharedOrigin ? "at shared 0,0,0" : "N/S " + F(c.SurveyNorthMm) + ", E/W " + F(c.SurveyEastMm) + ", elev " + F(c.SurveyElevationMm) + " mm (not 0,0,0)") +
                          "; " + F(c.SurveyOffsetFromOriginMm) + " mm from the internal origin" +
                          ", " + (c.SurveyPinned ? "pinned" : "NOT pinned"));
            sb.AppendLine("Project base point: N/S " + F(c.BaseNorthMm) + " mm, E/W " + F(c.BaseEastMm) + " mm, elev " +
                          F(c.BaseElevationMm) + " mm, angle to true north " + c.BaseAngleDeg.ToString("0.####", CultureInfo.InvariantCulture) + "°, " +
                          (c.BasePinned ? "pinned" : "NOT pinned"));
            sb.AppendLine("  rounded to " + F(round) + " mm: " + (rounded ? "yes" : "no"));
            sb.AppendLine("Shared coordinates: " + (c.SharedCoordinates ? "acquired/published (site '" + c.SiteName + "')" : "not set up (site '" + c.SiteName + "')"));
            sb.AppendLine("CAD links: " + links + " (" + linksPinned + " pinned)");
            if (pack != null)
                sb.AppendLine("Office rules: 'standards coordinates' (pack " + pack.Id + ")");

            return CommandResult.Ok(sb.ToString(), new Dictionary<string, object>
            {
                { "surveyAtSharedOrigin", c.SurveyAtSharedOrigin }, { "surveyOffsetMm", c.SurveyOffsetFromOriginMm }, { "surveyPinned", c.SurveyPinned },
                { "baseNorthMm", c.BaseNorthMm }, { "baseEastMm", c.BaseEastMm }, { "baseElevationMm", c.BaseElevationMm },
                { "baseAngleDeg", c.BaseAngleDeg }, { "basePinned", c.BasePinned }, { "baseRounded", rounded },
                { "roundToMm", round }, { "sharedCoordinates", c.SharedCoordinates }, { "site", c.SiteName },
                { "cadLinks", links }, { "cadLinksPinned", linksPinned }
            });
        }

        private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
