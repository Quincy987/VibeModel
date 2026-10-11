using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VibeModel.Services.Claude;
using VibeModel.Services.Helpers;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// Reads <see cref="TemplateFacts"/> from a live document. Two depths:
    /// fingerprint-only (phases, scope boxes, bound parameters — a few cheap collectors, run
    /// whenever the edit counter moved) and full (views, templates, families, coordinates,
    /// links, sheets — for the `template` command and the prompt summary).
    /// Must run on the Revit main thread.
    /// </summary>
    public static class TemplateFactsCollector
    {
        public static TemplateFacts Collect(Document doc, bool full)
        {
            var key = ModelChangeTracker.DocKey(doc.PathName, doc.Title);
            var f = new TemplateFacts
            {
                DocKey = key,
                Title = doc.Title,
                PathName = doc.PathName,
                Edits = ModelChangeTracker.GetTotalEdits(key),
                IsFamilyDocument = doc.IsFamilyDocument,
                IsFull = full
            };
            try { f.IsWorkshared = doc.IsWorkshared; } catch { }

            Safe(() =>
            {
                foreach (Phase p in doc.Phases) f.Phases.Add(p.Name);
            });
            Safe(() => f.ScopeBoxes = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()
                .Select(e => e.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList());
            Safe(() =>
            {
                var it = doc.ParameterBindings.ForwardIterator();
                while (it.MoveNext())
                    if (it.Key?.Name != null) f.ProjectParameters.Add(it.Key.Name);
            });

            if (!full) return f;

            Safe(() => f.PhaseFilters = new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter))
                .Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());

            Safe(() =>
            {
                foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)))
                {
                    if (v.IsTemplate) f.ViewTemplates.Add(v.Name);
                    else if (!(v is ViewSheet)) f.ViewNames.Add(v.Name);
                }
                f.ViewTemplates.Sort(StringComparer.OrdinalIgnoreCase);
            });

            Safe(() =>
            {
                foreach (Family fam in new FilteredElementCollector(doc).OfClass(typeof(Family)))
                {
                    Category cat = null;
                    try { cat = fam.FamilyCategory; } catch { }
                    f.Families.Add(new FamilyFact
                    {
                        Name = fam.Name,
                        Category = cat?.Name ?? "",
                        IsModelCategory = cat != null && cat.CategoryType == CategoryType.Model
                    });
                }
            });

            Safe(() => f.MaterialsCount = new FilteredElementCollector(doc).OfClass(typeof(Material)).GetElementCount());

            Safe(() => f.Titleblocks = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks).Cast<FamilySymbol>()
                .Select(s => s.FamilyName).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());

            Safe(() => f.Sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .Select(s => s.SheetNumber + " " + s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());

            Safe(() => f.Coordinates = ReadCoordinates(doc));

            Safe(() =>
            {
                foreach (ImportInstance ii in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)))
                {
                    if (ii.IsLinked) { f.CadLinks++; if (ii.Pinned) f.CadLinksPinned++; }
                    else f.CadImports++;
                }
            });
            Safe(() =>
            {
                foreach (var pc in new FilteredElementCollector(doc).OfClass(typeof(PointCloudInstance)))
                {
                    f.PointClouds++;
                    if (pc.Pinned) f.PointCloudsPinned++;
                }
            });
            Safe(() =>
            {
                foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)))
                {
                    f.Grids++;
                    if (g.Pinned) f.GridsPinned++;
                }
            });

            return f;
        }

        public static CoordinateFacts ReadCoordinates(Document doc)
        {
            var c = new CoordinateFacts();
            var pbp = BasePoint.GetProjectBasePoint(doc);
            var sp = BasePoint.GetSurveyPoint(doc);
            if (pbp == null || sp == null) return c;

            c.Available = true;
            c.BaseNorthMm = Mm(pbp.get_Parameter(BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM));
            c.BaseEastMm = Mm(pbp.get_Parameter(BuiltInParameter.BASEPOINT_EASTWEST_PARAM));
            c.BaseElevationMm = Mm(pbp.get_Parameter(BuiltInParameter.BASEPOINT_ELEVATION_PARAM));
            var ang = pbp.get_Parameter(BuiltInParameter.BASEPOINT_ANGLETON_PARAM);
            c.BaseAngleDeg = ang != null ? ang.AsDouble() * 180.0 / Math.PI : 0;
            c.BasePinned = pbp.Pinned;

            c.SurveyPinned = sp.Pinned;
            c.SurveyNorthMm = Mm(sp.get_Parameter(BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM));
            c.SurveyEastMm = Mm(sp.get_Parameter(BuiltInParameter.BASEPOINT_EASTWEST_PARAM));
            c.SurveyElevationMm = Mm(sp.get_Parameter(BuiltInParameter.BASEPOINT_ELEVATION_PARAM));
            c.SurveyOffsetFromOriginMm = RevitUnitHelper.FeetToMm(sp.Position.GetLength());

            try
            {
                var loc = doc.ActiveProjectLocation;
                c.SiteName = loc?.Name;
                var pos = loc?.GetProjectPosition(XYZ.Zero);
                c.SharedCoordinates = pos != null &&
                    (Math.Abs(pos.EastWest) > 1e-6 || Math.Abs(pos.NorthSouth) > 1e-6 ||
                     Math.Abs(pos.Elevation) > 1e-6 || Math.Abs(pos.Angle) > 1e-9);
            }
            catch { }
            return c;
        }

        public static ActiveViewFacts ReadActiveView(View v)
        {
            if (v == null) return null;
            var f = new ActiveViewFacts { Name = v.Name, ViewType = v.ViewType.ToString() };
            Safe(() => f.Phase = ElementName(v.Document, v.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId()));
            Safe(() => f.PhaseFilter = ElementName(v.Document, v.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId()));
            Safe(() =>
            {
                if (v.ViewTemplateId != null && v.ViewTemplateId != ElementId.InvalidElementId)
                    f.Template = v.Document.GetElement(v.ViewTemplateId)?.Name;
            });
            return f;
        }

        public static string ElementName(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return null;
            return doc.GetElement(id)?.Name;
        }

        private static double Mm(Parameter p) => p == null ? 0 : RevitUnitHelper.FeetToMm(p.AsDouble());

        private static void Safe(Action a)
        {
            try { a(); }
            catch (Exception ex) { VibeModel.Infrastructure.Logger.Warn("Template facts: " + ex.Message); }
        }
    }
}
