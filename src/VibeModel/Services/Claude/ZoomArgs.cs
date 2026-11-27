using System;
using System.Collections.Generic;
using System.Linq;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// `screenshot [pixels] [--view v] [--at ids|selected [--margin mm] | --box x0 y0 x1 y1]` (plan 23).
    /// --at / --box zoom in on part of a view on a temporary copy that is always rolled back, so
    /// the view itself — also one on a sheet — never changes. Pure, tested.
    /// </summary>
    internal sealed class ZoomArgs
    {
        public const int DefaultPixels = 1536, MinPixels = 512, MaxPixels = 3000;
        public const double DefaultMarginMm = 500;

        public int Pixels = DefaultPixels;
        public string View;
        public List<int> AtIds = new List<int>();
        public bool AtSelected;
        public double[] Box;                  // x0 y0 x1 y1, normalised: x0 < x1, y0 < y1
        public double Margin = DefaultMarginMm;
        public string Error;

        public bool Zoom => AtSelected || AtIds.Count > 0 || Box != null;

        public const string Usage = "screenshot [pixels] [--view <viewId|name|sheetNumber>] [--at <ids|selected> [--margin <mm>] | --box <x0> <y0> <x1> <y1>]";

        public static ZoomArgs Parse(string args)
        {
            var r = new ZoomArgs();
            var f = FlagArgs.Parse(args);
            r.Error = f.Unknown("view", "at", "box", "margin");
            if (r.Error != null) return r;

            // The pixel count stays optional and lenient, as before: the first positional number.
            var px = f.Positional.FirstOrDefault();
            if (px != null && int.TryParse(px, out var p)) r.Pixels = Math.Max(MinPixels, Math.Min(MaxPixels, p));

            if (f.Has("view"))
            {
                r.View = f.Text("view");
                if (string.IsNullOrWhiteSpace(r.View)) { r.Error = "--view needs a view id, name or sheet number."; return r; }
            }

            if (f.Has("at") && f.Has("box")) { r.Error = "Pick one: --at <ids|selected> (the elements' extent) or --box x0 y0 x1 y1."; return r; }

            if (f.Has("at"))
            {
                var tokens = string.Join(" ", f.Values("at")).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) { r.Error = "--at needs element ids (comma or space separated) or 'selected'."; return r; }
                if (tokens.Length == 1 && tokens[0].Equals("selected", StringComparison.OrdinalIgnoreCase)) r.AtSelected = true;
                else
                    foreach (var t in tokens)
                    {
                        if (!int.TryParse(t, out int id)) { r.Error = "--at: '" + t + "' is not an element id (or give 'selected')."; return r; }
                        if (!r.AtIds.Contains(id)) r.AtIds.Add(id);
                    }
            }

            if (f.Has("box"))
            {
                if (!f.TryNums("box", 4, out var b, out r.Error)) { r.Error = r.Error ?? "--box needs 4 numbers: x0 y0 x1 y1."; return r; }
                r.Box = new[] { Math.Min(b[0], b[2]), Math.Min(b[1], b[3]), Math.Max(b[0], b[2]), Math.Max(b[1], b[3]) };
                if (r.Box[2] - r.Box[0] < 1 || r.Box[3] - r.Box[1] < 1) { r.Error = "--box has no area: x0 y0 x1 y1 must differ in both directions."; return r; }
            }

            if (f.Has("margin"))
            {
                if (!f.Has("at")) { r.Error = "--margin goes with --at (a --box is taken as given)."; return r; }
                if (!f.TryNum("margin", out double m, out r.Error)) return r;
                if (m < 0) { r.Error = "--margin cannot be negative."; return r; }
                r.Margin = m;
            }
            return r;
        }

        /// <summary>
        /// The viewport a paper box (mm) on a sheet belongs to: the one that contains it whole, the
        /// smallest when they are stacked. -1 + a reason when it lies in no single viewport.
        /// </summary>
        public static int PickViewport(IList<MmRect> viewports, double[] box, out string reason)
        {
            reason = null;
            int best = -1;
            double bestArea = double.MaxValue;
            const double tol = 0.5;
            for (int i = 0; i < (viewports?.Count ?? 0); i++)
            {
                var v = viewports[i];
                bool inside = box[0] >= v.X0 - tol && box[1] >= v.Y0 - tol && box[2] <= v.X1 + tol && box[3] <= v.Y1 + tol;
                if (!inside) continue;
                double area = (v.X1 - v.X0) * (v.Y1 - v.Y0);
                if (area < bestArea) { bestArea = area; best = i; }
            }
            if (best >= 0) return best;
            int touched = (viewports ?? new List<MmRect>()).Count(v => box[0] < v.X1 && box[2] > v.X0 && box[1] < v.Y1 && box[3] > v.Y0);
            reason = touched == 0 ? "the box lies on no viewport of this sheet"
                                  : "the box spans " + touched + " viewport" + (touched == 1 ? " but sticks out of it" : "s");
            return -1;
        }
    }
}
