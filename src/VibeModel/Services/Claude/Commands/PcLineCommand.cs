using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.UI;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Ground-following height profile along a line through a point cloud. Probes stations at
    /// a fixed step, estimates ground per station (lowest dense band), then a continuity pass
    /// rejects clutter/occlusion spikes and interpolates the gaps — so a retaining wall can
    /// follow the street surface even where cars or trees sit on it.
    /// </summary>
    public class PcLineCommand : IClaudeCommand, IStructuredCommand
    {
        private const int MaxStations = 100;
        private const int MaxPointsPerStation = 4000;

        public string Name => "pcline";
        public string Description => "Ground profile along a line through a point cloud (mm)";
        public string Usage => "pcline <cloudId> <x1> <y1> <x2> <y2> [step]";

        public string Execute(string args, UIApplication uiApp)
        {
            return ExecuteStructured(args, uiApp).RenderText();
        }

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var doc = uiApp.ActiveUIDocument.Document;

            var parsed = SourceDataArgs.ParsePcLine(args);
            if (parsed.Error != null)
                return CommandResult.Error("BAD_ARGS", parsed.Error, Usage);

            var cloud = PcProbeCommand.ResolveCloud(doc, parsed.CloudId, out CommandResult error);
            if (cloud == null) return error;

            var stations = PointCloudAnalysis.BuildStations(parsed.X1Mm, parsed.Y1Mm,
                parsed.X2Mm, parsed.Y2Mm, parsed.StepMm, MaxStations, out string stationError);
            if (stations == null)
                return CommandResult.Error("BAD_ARGS", stationError, Usage);

            double radius = Math.Max(parsed.StepMm / 2.0, 100);

            var grounds = new List<double?>();
            foreach (var st in stations)
            {
                var zs = PointCloudHelper.GetColumnZsMm(cloud, st.X, st.Y, radius,
                    MaxPointsPerStation, out _);
                var est = zs.Count > 0 ? PointCloudAnalysis.EstimateGround(zs) : null;
                grounds.Add(est?.GroundZMm);
            }

            var profile = PointCloudAnalysis.SmoothProfile(grounds);

            if (profile.All(p => p.Flag == "nodata"))
                return CommandResult.Error("NO_POINTS",
                    "No usable ground points along this line.",
                    "Check the line lies inside the cloud bbox ('pointclouds'), or try a larger step.");

            double length = stations[stations.Count - 1].Dist;
            var okOrInterp = profile.Where(p => p.ZMm.HasValue).Select(p => p.ZMm.Value).ToList();
            double zStart = profile.First(p => p.ZMm.HasValue).ZMm.Value;
            double zEnd = profile.Last(p => p.ZMm.HasValue).ZMm.Value;

            var sb = new StringBuilder();
            sb.AppendLine("GROUND PROFILE (cloud " + parsed.CloudId + ", " + stations.Count +
                          " stations, step " + PcProbeCommand.Fmt(parsed.StepMm) + " mm)");
            sb.AppendLine("=".PadRight(50, '='));
            sb.AppendLine();
            sb.AppendLine("  dist_mm      ground_z_mm  flag");

            var stationData = new List<object>();
            for (int i = 0; i < stations.Count; i++)
            {
                var p = profile[i];
                sb.AppendLine("  " + PcProbeCommand.Fmt(stations[i].Dist).PadLeft(9) + "  " +
                              (p.ZMm.HasValue ? PcProbeCommand.Fmt(p.ZMm.Value).PadLeft(13) : "-".PadLeft(13)) +
                              "  " + p.Flag);
                stationData.Add(new Dictionary<string, object>
                {
                    { "distMm", Math.Round(stations[i].Dist, 1) },
                    { "xMm", Math.Round(stations[i].X, 1) },
                    { "yMm", Math.Round(stations[i].Y, 1) },
                    { "groundZMm", p.ZMm.HasValue ? (object)Math.Round(p.ZMm.Value, 1) : null },
                    { "flag", p.Flag }
                });
            }

            int interpCount = profile.Count(p => p.Flag == "interp");
            double slopePct = length > 0 ? (zEnd - zStart) / length * 100 : 0;
            sb.AppendLine();
            sb.AppendLine("Summary: start " + PcProbeCommand.Fmt(zStart) + ", end " + PcProbeCommand.Fmt(zEnd) +
                          ", min " + PcProbeCommand.Fmt(okOrInterp.Min()) + ", max " + PcProbeCommand.Fmt(okOrInterp.Max()) +
                          " mm, slope " + slopePct.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                          "% over " + PcProbeCommand.Fmt(length) + " mm" +
                          (interpCount > 0 ? " (" + interpCount + " stations interpolated past clutter/occlusion)" : ""));

            var data = new Dictionary<string, object>
            {
                { "cloudId", parsed.CloudId },
                { "stepMm", parsed.StepMm },
                { "lengthMm", Math.Round(length, 1) },
                { "startZMm", Math.Round(zStart, 1) },
                { "endZMm", Math.Round(zEnd, 1) },
                { "stations", stationData }
            };

            return CommandResult.Ok(sb.ToString(), data);
        }
    }
}
