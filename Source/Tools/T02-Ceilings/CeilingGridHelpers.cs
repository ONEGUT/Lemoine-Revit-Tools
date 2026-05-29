using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Ceilings
{
    internal readonly struct XyBounds
    {
        public readonly double MinX, MinY, MaxX, MaxY;
        public XyBounds(double minX, double minY, double maxX, double maxY)
        { MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY; }

        public bool Overlaps(XyBounds o)
            => MinX <= o.MaxX && MaxX >= o.MinX && MinY <= o.MaxY && MaxY >= o.MinY;

        public static XyBounds FromPoints(XYZ a, XYZ b) => new XyBounds(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    internal sealed class FaceRecord
    {
        public readonly PlanarFace Face;
        public readonly XyBounds   Bounds;
        public readonly double     Elevation;
        public FaceRecord(PlanarFace face, XyBounds bounds, double elevation)
        { Face = face; Bounds = bounds; Elevation = elevation; }
    }

    internal static class CeilingGridHelpers
    {
        public static IList<Element> GetCeilingsInView(Document doc, View view)
            => new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Ceilings)
                .WhereElementIsNotElementType()
                .ToElements();

        public static IList<ModelCurve> GetModelCurvesInView(Document doc, View view)
        {
            var results = new List<ModelCurve>();
            foreach (Element e in new FilteredElementCollector(doc, view.Id)
                                      .OfClass(typeof(CurveElement))
                                      .WhereElementIsNotElementType())
                if (e is ModelCurve mc) results.Add(mc);
            return results;
        }

        public static IList<FaceRecord> GetCeilingBottomFaces(IList<Element> ceilings)
        {
            var records     = new List<FaceRecord>();
            var geomOptions = new Options { ComputeReferences = true, IncludeNonVisibleObjects = false };

            foreach (Element ceiling in ceilings)
            {
                GeometryElement geom = ceiling.get_Geometry(geomOptions);
                if (geom == null) continue;

                PlanarFace bestFace = null;
                double     lowestZ  = double.MaxValue;

                foreach (GeometryObject obj in geom)
                {
                    Solid solid = obj as Solid;
                    if (solid == null || solid.Faces.IsEmpty) continue;

                    PlanarFace singleCandidate = null;
                    int        downwardCount   = 0;

                    foreach (Face face in solid.Faces)
                    {
                        if (face is PlanarFace pf && pf.FaceNormal.Z < -0.9)
                        {
                            downwardCount++;
                            singleCandidate = pf;
                            if (downwardCount > 1) break;
                        }
                    }

                    if (downwardCount == 0) continue;

                    if (downwardCount == 1)
                    {
                        double z = singleCandidate.Origin.Z;
                        if (z < lowestZ) { lowestZ = z; bestFace = singleCandidate; }
                    }
                    else
                    {
                        foreach (Face face in solid.Faces)
                        {
                            if (!(face is PlanarFace pf) || pf.FaceNormal.Z >= -0.9) continue;
                            BoundingBoxUV bb  = pf.GetBoundingBox();
                            XYZ           mid = pf.Evaluate((bb.Min + bb.Max) * 0.5);
                            if (mid.Z < lowestZ) { lowestZ = mid.Z; bestFace = pf; }
                        }
                    }
                }

                if (bestFace == null) continue;
                records.Add(new FaceRecord(bestFace, ComputeXyBounds(bestFace), lowestZ));
            }

            return records;
        }

        public static XyBounds ComputeXyBounds(PlanarFace face)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (EdgeArray loop in face.EdgeLoops)
                foreach (Edge edge in loop)
                    foreach (XYZ pt in edge.Tessellate())
                    {
                        if (pt.X < minX) minX = pt.X; if (pt.Y < minY) minY = pt.Y;
                        if (pt.X > maxX) maxX = pt.X; if (pt.Y > maxY) maxY = pt.Y;
                    }
            return new XyBounds(minX, minY, maxX, maxY);
        }

        public static XYZ? ProjectPointOntoFace(XYZ point, PlanarFace face)
        {
            double nDotZ = face.FaceNormal.Z;
            if (Math.Abs(nDotZ) < 1e-9) return null;
            double t = face.FaceNormal.DotProduct(face.Origin - point) / nDotZ;
            return new XYZ(point.X, point.Y, point.Z + t);
        }

        public static IList<Curve> ProjectCurveOntoCeilings(Curve src, IList<FaceRecord> records)
        {
            var result = new List<Curve>();
            XYZ      p0    = src.GetEndPoint(0);
            XYZ      p1    = src.GetEndPoint(1);
            XyBounds cBBox = XyBounds.FromPoints(p0, p1);

            foreach (FaceRecord rec in records)
            {
                if (!rec.Bounds.Overlaps(cBBox)) continue;
                XYZ? ps = ProjectPointOntoFace(p0, rec.Face);
                XYZ? pe = ProjectPointOntoFace(p1, rec.Face);
                if (ps == null || pe == null || ps.IsAlmostEqualTo(pe)) continue;
                try
                {
                    if (rec.Face.Project(ps) == null || rec.Face.Project(pe) == null) continue;
                    result.Add(Line.CreateBound(ps, pe));
                }
                catch { /* skip degenerate */ }
            }
            return result;
        }

        public static void TryCreateModelCurve(Document doc, View view, Curve curve,
            Dictionary<double, SketchPlane> cache)
        {
            try
            {
                double key = Math.Round(curve.GetEndPoint(0).Z, 4);
                if (!cache.TryGetValue(key, out SketchPlane sp))
                {
                    sp = SketchPlane.Create(doc,
                        Plane.CreateByNormalAndOrigin(XYZ.BasisZ, curve.GetEndPoint(0)));
                    cache[key] = sp;
                }
                doc.Create.NewModelCurve(curve, sp);
            }
            catch
            {
                try { doc.Create.NewDetailCurve(view, curve); } catch (Exception __lex) { LemoineLog.Swallowed("CeilingGrids: create detail curve", __lex); }
            }
        }
    }
}
