using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace VibeModel.Services.Helpers
{
    /// <summary>
    /// One polyline chain extracted from a CAD import, in model space, mm.
    /// Lines contribute 2 points; polylines their vertices; arcs/splines a tessellation.
    /// </summary>
    public sealed class DwgChain
    {
        public string Layer { get; set; }
        public List<XYZ> PointsMm { get; set; }
    }

    /// <summary>
    /// Walks an ImportInstance's geometry tree and flattens every curve to a point chain in
    /// model coordinates (mm). DWG layers survive import as graphics styles: each geometry
    /// object's GraphicsStyleId resolves to a GraphicsStyle whose category name is the layer.
    /// </summary>
    public static class DwgGeometryHelper
    {
        public static List<DwgChain> GetChains(Element importInstance, Document doc)
        {
            var chains = new List<DwgChain>();
            var options = new Options { DetailLevel = ViewDetailLevel.Fine };
            var geom = importInstance.get_Geometry(options);
            if (geom != null)
                Walk(geom, doc, chains);
            return chains;
        }

        private static void Walk(GeometryElement geom, Document doc, List<DwgChain> chains)
        {
            foreach (var obj in geom)
            {
                if (obj is GeometryInstance gi)
                {
                    // GetInstanceGeometry applies the placement transform -> model coordinates.
                    var inner = gi.GetInstanceGeometry();
                    if (inner != null)
                        Walk(inner, doc, chains);
                }
                else if (obj is PolyLine pl)
                {
                    AddChain(chains, doc, obj, pl.GetCoordinates());
                }
                else if (obj is Line line)
                {
                    AddChain(chains, doc, obj, new List<XYZ> { line.GetEndPoint(0), line.GetEndPoint(1) });
                }
                else if (obj is Curve curve)
                {
                    // Arcs, ellipses, splines: tessellate to a point chain.
                    if (curve.IsBound)
                        AddChain(chains, doc, obj, curve.Tessellate());
                }
                // Solids/meshes/points in a DWG are not useful as footprint linework; skip.
            }
        }

        private static void AddChain(List<DwgChain> chains, Document doc, GeometryObject obj, IList<XYZ> pointsFeet)
        {
            if (pointsFeet == null || pointsFeet.Count < 2) return;

            var mm = new List<XYZ>(pointsFeet.Count);
            foreach (var p in pointsFeet)
            {
                mm.Add(new XYZ(
                    RevitUnitHelper.FeetToMm(p.X),
                    RevitUnitHelper.FeetToMm(p.Y),
                    RevitUnitHelper.FeetToMm(p.Z)));
            }

            chains.Add(new DwgChain { Layer = ResolveLayer(doc, obj), PointsMm = mm });
        }

        private static string ResolveLayer(Document doc, GeometryObject obj)
        {
            if (obj.GraphicsStyleId != null && obj.GraphicsStyleId != ElementId.InvalidElementId)
            {
                var style = doc.GetElement(obj.GraphicsStyleId) as GraphicsStyle;
                var name = style?.GraphicsStyleCategory?.Name;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            return "(no layer)";
        }
    }
}
