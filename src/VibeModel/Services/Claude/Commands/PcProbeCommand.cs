using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Vertical probe into a point cloud at (x, y): height histogram + robust ground estimate.
    /// Ground = lowest dense band of points, so parked cars / trees / bins above the surface
    /// and stray scan noise below it are both ignored.
    /// </summary>
    public class PcProbeCommand : IClaudeCommand, IStructuredCommand
    {
        private const int MaxPoints = 20000;
        private const int MaxHistogramLines = 30;

        public string Name => "pcprobe";
        public string Description => "Probe point cloud at (x,y): ground level + height histogram (mm)";
        public string Usage => "pcprobe <cloudId> <x> <y> [radius]";

        public string Execute(string args, UIApplication uiApp)
        {
            return ExecuteStructured(args, uiApp).RenderText();
        }

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var parsed = SourceDataArgs.ParsePcProbe(args);
            if (parsed.Error != null)
                return CommandResult.Error("BAD_ARGS", parsed.Error, Usage);

            var cloud = ResolveCloud(doc, parsed.CloudId, out CommandResult error);
            if (cloud == null) return error;

            var bbox = cloud.get_BoundingBox(null);
            if (bbox != null && !InsideXy(parsed.XMm, parsed.YMm, bbox))
                return CommandResult.Error("OUT_OF_BOUNDS",
                    "(" + Fmt(parsed.XMm) + ", " + Fmt(parsed.YMm) + ") is outside the cloud's XY extent " +
                    BboxXyText(bbox) + ".",
                    "Pick coordinates inside the bounding box (see 'pointclouds').");

            var zs = PointCloudHelper.GetColumnZsMm(cloud, parsed.XMm, parsed.YMm,
                parsed.RadiusMm, MaxPoints, out string diag);

            if (zs.Count == 0)
                return CommandResult.Error("NO_POINTS",
                    "No points found within " + Fmt(parsed.RadiusMm) + " mm of (" +
                    Fmt(parsed.XMm) + ", " + Fmt(parsed.YMm) + ")." +
                    (diag != null ? " [" + diag + "]" : ""),
                    "Try a larger radius, or check the cloud bbox with 'pointclouds'.");

            var est = PointCloudAnalysis.EstimateGround(zs);

            var sb = new StringBuilder();
            sb.AppendLine("POINT CLOUD PROBE (cloud " + parsed.CloudId + ")");
            sb.AppendLine("=".PadRight(50, '='));
            sb.AppendLine();
            sb.AppendLine("Location: (" + Fmt(parsed.XMm) + ", " + Fmt(parsed.YMm) + ") mm, radius " +
                          Fmt(parsed.RadiusMm) + " mm");
            sb.AppendLine("Points: " + est.Count);
            sb.AppendLine("Z range: " + Fmt(est.MinZMm) + " .. " + Fmt(est.MaxZMm) + " mm");
            sb.AppendLine(est.GroundZMm.HasValue
                ? "GROUND: " + Fmt(est.GroundZMm.Value) + " mm  (" + est.GroundBandPoints +
                  " pts in the lowest dense band; points above it are clutter/objects)"
                : "GROUND: indeterminate (no dense low band — too few points here)");
            sb.AppendLine();
            sb.AppendLine("Height histogram (50 mm bins, occupied only):");

            int shown = 0;
            foreach (var bin in est.Bins)
            {
                if (shown++ >= MaxHistogramLines)
                {
                    sb.AppendLine("  ... " + (est.Bins.Count - MaxHistogramLines) + " more bins");
                    break;
                }
                sb.AppendLine("  " + Fmt(bin.ZStartMm).PadLeft(10) + " .. " +
                              Fmt(bin.ZStartMm + bin.WidthMm).PadLeft(10) + " : " + bin.Count);
            }

            var data = new Dictionary<string, object>
            {
                { "cloudId", parsed.CloudId },
                { "xMm", parsed.XMm },
                { "yMm", parsed.YMm },
                { "radiusMm", parsed.RadiusMm },
                { "points", est.Count },
                { "minZMm", est.MinZMm },
                { "maxZMm", est.MaxZMm },
                { "groundZMm", est.GroundZMm },
                { "histogram", est.Bins.Select(b => (object)new Dictionary<string, object>
                    {
                        { "zStartMm", b.ZStartMm },
                        { "count", b.Count }
                    }).ToList() }
            };

            return CommandResult.Ok(sb.ToString(), data);
        }

        internal static PointCloudInstance ResolveCloud(Document doc, int id, out CommandResult error)
        {
            error = null;
            var element = doc.GetElement(new ElementId(id));
            if (element == null)
            {
                error = CommandResult.Error("NOT_FOUND",
                    "No element with ID " + id + ".",
                    "Run 'pointclouds' to list loaded point clouds.");
                return null;
            }
            if (!(element is PointCloudInstance cloud))
            {
                error = CommandResult.Error("NOT_A_POINTCLOUD",
                    "Element " + id + " is not a point cloud (" + element.GetType().Name + ").",
                    "Run 'pointclouds' to list loaded point clouds.");
                return null;
            }
            return cloud;
        }

        internal static bool InsideXy(double xMm, double yMm, BoundingBoxXYZ bbox)
        {
            const double padMm = 1000;
            return xMm >= RevitUnitHelper.FeetToMm(bbox.Min.X) - padMm &&
                   xMm <= RevitUnitHelper.FeetToMm(bbox.Max.X) + padMm &&
                   yMm >= RevitUnitHelper.FeetToMm(bbox.Min.Y) - padMm &&
                   yMm <= RevitUnitHelper.FeetToMm(bbox.Max.Y) + padMm;
        }

        internal static string BboxXyText(BoundingBoxXYZ bbox)
        {
            return "(" + Fmt(RevitUnitHelper.FeetToMm(bbox.Min.X)) + ", " +
                   Fmt(RevitUnitHelper.FeetToMm(bbox.Min.Y)) + ") .. (" +
                   Fmt(RevitUnitHelper.FeetToMm(bbox.Max.X)) + ", " +
                   Fmt(RevitUnitHelper.FeetToMm(bbox.Max.Y)) + ") mm";
        }

        internal static string Fmt(double mm)
        {
            return mm.ToString("F1", CultureInfo.InvariantCulture);
        }
    }
}
