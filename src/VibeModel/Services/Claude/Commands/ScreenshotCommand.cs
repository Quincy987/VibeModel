using System;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Exports the active view to a PNG and returns a plain-text payload with the absolute
    /// path + metadata. Vision-capable backends read the file and inline the image.
    /// Path-over-bytes keeps the text/plain HTTP contract intact — no server changes.
    /// </summary>
    public class ScreenshotCommand : IClaudeCommand
    {
        public string Name => "screenshot";
        public string Description => "Export the active view to a PNG so it can be viewed";
        public string Usage => "screenshot [pixels]";   // optional long-edge px, default 1536

        // Vision sweet spot: big enough to read geometry, small enough to keep tokens sane.
        // Anthropic downscales images > ~1568px long edge anyway, so 1536 sits just under.
        private const int DefaultPixels = 1536;
        private const int MinPixels = 512;
        private const int MaxPixels = 3000;

        // Keep only the most recent N screenshot temp dirs to bound disk growth.
        private const int KeepRecent = 20;

        public string Execute(string args, UIApplication uiApp)
        {
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            View view;
            try { view = uiDoc.ActiveGraphicalView; } catch { view = null; }
            if (view == null) view = uiDoc.ActiveView;
            if (view == null) return "ERROR: No active view to capture.";

            // Guard view types Revit can't raster-export as a single useful image.
            if (view is ViewSchedule || view.ViewType == ViewType.Legend ||
                view.ViewType == ViewType.DrawingSheet)
                return "ERROR: View type '" + view.ViewType + "' cannot be exported as an image. " +
                       "Switch to a plan, section, elevation, or 3D view.";

            int pixels = DefaultPixels;
            if (!string.IsNullOrWhiteSpace(args) && int.TryParse(args.Trim(), out var p))
                pixels = Math.Max(MinPixels, Math.Min(MaxPixels, p));

            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VibeModel", "screenshots");
            PruneOldScreenshots(root);

            // Export into a fresh, empty temp dir so we can unambiguously find the PNG.
            // (Revit mangles the filename — appends " - <ViewType> - <ViewName>.png".)
            var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var basePath = Path.Combine(dir, "view");

            var opts = new ImageExportOptions
            {
                FilePath = basePath,
                ExportRange = ExportRange.CurrentView,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = pixels,                 // long-edge pixels when FitToPage
                FitDirection = FitDirectionType.Horizontal,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150
            };

            try
            {
                doc.ExportImage(opts);   // read-only — no Transaction required
            }
            catch (Exception ex)
            {
                return "ERROR: ExportImage failed: " + ex.Message;
            }

            var png = Directory.GetFiles(dir, "*.png").FirstOrDefault();
            if (png == null)
                return "ERROR: ExportImage produced no PNG (view may be empty or hidden).";

            var info = new FileInfo(png);
            var sb = new StringBuilder();
            sb.AppendLine("SCREENSHOT");
            sb.AppendLine("==========");
            sb.AppendLine("View: " + view.Name + " (" + view.ViewType + ")");
            sb.AppendLine("Pixels (long edge): " + pixels);
            sb.AppendLine("Size: " + (info.Length / 1024) + " KB");
            // NOTE: backends locate the file by parsing this "Path:" line. Keep the prefix
            // stable — this is the text contract until Plan 03 (structured I/O) replaces it.
            sb.AppendLine("Path: " + png);
            sb.AppendLine();
            sb.AppendLine("The PNG at the path above shows the active view. " +
                          "Open/read it to see the current state of the model.");
            return sb.ToString();
        }

        // Best-effort: keep only the newest KeepRecent screenshot temp dirs.
        private static void PruneOldScreenshots(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                var stale = new DirectoryInfo(root).GetDirectories()
                    .OrderByDescending(d => d.LastWriteTimeUtc)
                    .Skip(KeepRecent);
                foreach (var d in stale)
                {
                    try { d.Delete(true); } catch { /* best effort */ }
                }
            }
            catch { /* best effort — never fail a screenshot over cleanup */ }
        }
    }
}
