using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// The Revit side of <see cref="ForbiddenActions"/>: the refusal before a command runs and the
    /// reading of what is protected, compared after it ran. A failure in here never fails the
    /// command it guards.
    /// </summary>
    internal static class ForbiddenGuard
    {
        private const long SlowCaptureMs = 50;

        /// <summary>The refusal for a command that names something protected, or null.</summary>
        public static CommandResult PreCheck(Document doc, string command, string args)
        {
            try
            {
                if (doc == null || doc.IsFamilyDocument) return null;
                if (string.Equals(command, "exec", StringComparison.OrdinalIgnoreCase))
                    return Refuse(ForbiddenActions.ScanExec(args));
                if (!AnnotationScope.Applies(command)) return null;

                var ids = AnnotationScope.TargetIds(command, args);
                if (ids == null) return null;
                bool isSet = string.Equals(command, "set", StringComparison.OrdinalIgnoreCase);
                foreach (var id in ids)
                {
                    var e = doc.GetElement(new ElementId(id));
                    if (e is BasePoint) return Refuse(ForbiddenActions.BasePointWhat);
                    if (e is View v && v.IsTemplate) return Refuse(ForbiddenActions.TemplateWhat);
                    if (isSet && IsDatumOrLink(e) && e.Pinned && ForbiddenActions.SetsPinned(args))
                        return Refuse(ForbiddenActions.PinWhat);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Forbidden check skipped: " + ex.Message);
            }
            return null;
        }

        private static CommandResult Refuse(string what)
            => what == null ? null : CommandResult.Error(ForbiddenActions.Code, ForbiddenActions.RefusedMessage(what), ForbiddenActions.Hint);

        private static bool IsDatumOrLink(Element e) => e is Grid || e is Level || e is RevitLinkInstance || e is ImportInstance;

        /// <summary>Base points, shared coordinates, pins and view templates as they are now; null when unreadable.</summary>
        public static Dictionary<string, ProtectedState> Capture(Document doc)
        {
            if (doc == null || doc.IsFamilyDocument) return null;
            var clock = Stopwatch.StartNew();
            try
            {
                var map = new Dictionary<string, ProtectedState>();
                AddPoint(map, "project", BasePoint.GetProjectBasePoint(doc));
                AddPoint(map, "survey", BasePoint.GetSurveyPoint(doc));

                var location = doc.ActiveProjectLocation;
                var position = location?.GetProjectPosition(XYZ.Zero);
                if (position != null)
                    map["location"] = new ProtectedState(ProtectedKind.Position, ForbiddenActions.CoordinatesWhat,
                        location.Id.IntegerValue.ToString(CultureInfo.InvariantCulture) + " " +
                        F(position.EastWest) + " " + F(position.NorthSouth) + " " + F(position.Elevation) + " " + F(position.Angle));

                var pinnable = new ElementMulticlassFilter(new List<Type> { typeof(Grid), typeof(Level), typeof(RevitLinkInstance), typeof(ImportInstance) });
                foreach (var e in new FilteredElementCollector(doc).WherePasses(pinnable))
                    map["pin:" + e.Id.IntegerValue] = new ProtectedState(ProtectedKind.Pin, ForbiddenActions.PinWhat,
                        e.Pinned ? ForbiddenActions.PinnedState : ForbiddenActions.UnpinnedState);

                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(x => x.IsTemplate))
                    map["template:" + v.Id.IntegerValue] = new ProtectedState(ProtectedKind.Template, ForbiddenActions.TemplateWhat, TemplateState(v));

                if (clock.ElapsedMilliseconds > SlowCaptureMs)
                    Logger.Info("Forbidden check: reading the protected items took " + clock.ElapsedMilliseconds + " ms (" + map.Count + " items)");
                return map;
            }
            catch (Exception ex)
            {
                Logger.Warn("Forbidden check skipped: " + ex.Message);
                return null;
            }
        }

        private static void AddPoint(Dictionary<string, ProtectedState> map, string key, BasePoint point)
        {
            if (point == null) return;
            var p = point.Position;
            var s = point.SharedPosition;
            map[key] = new ProtectedState(ProtectedKind.Position, ForbiddenActions.BasePointWhat,
                F(p.X) + " " + F(p.Y) + " " + F(p.Z) + " " + F(s.X) + " " + F(s.Y) + " " + F(s.Z));
        }

        private static string F(double v) => v.ToString("F6", CultureInfo.InvariantCulture);

        private static string TemplateState(View v)
        {
            var sb = new StringBuilder(v.Name);
            foreach (Parameter p in v.Parameters)
            {
                sb.Append('|').Append(p.Id.IntegerValue).Append('=');
                switch (p.StorageType)
                {
                    case StorageType.Double: sb.Append(F(p.AsDouble())); break;
                    case StorageType.Integer: sb.Append(p.AsInteger()); break;
                    case StorageType.String: sb.Append(p.AsString()); break;
                    case StorageType.ElementId: sb.Append(p.AsElementId().IntegerValue); break;
                }
            }
            foreach (var f in v.GetFilters()) sb.Append("|f").Append(f.IntegerValue);
            return sb.ToString().GetHashCode().ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>What a command changed that it may not, or null.</summary>
        public static string Compare(Document doc, Dictionary<string, ProtectedState> before)
        {
            if (before == null) return null;
            var what = ForbiddenActions.Difference(before, Capture(doc));
            if (what != null) Logger.Warn("Forbidden change: " + what);
            return what;
        }
    }
}
