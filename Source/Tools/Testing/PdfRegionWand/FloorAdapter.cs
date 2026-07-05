using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.PdfGeometry.Plans;
using LemoineTools.PdfGeometry.Primitives;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.PdfRegionWand
{
    /// <summary>
    /// Creates one Revit floor from a region plan: outer loop plus hole loops
    /// as sketch profile curves, arcs preserved via Arc.Create(start, end,
    /// pointOnArc). Runs inside the caller's transaction.
    /// </summary>
    public sealed class FloorAdapter : IRegionOutputAdapter
    {
        public string DisplayName => LemoineStrings.T("pdfRegionWand.adapter.floorName");

        public IList<ElementId> Create(
            Document doc, RegionPlan plan, PdfToModelTransform transform,
            RegionOutputOptions options, Action<string, string> log)
        {
            if (options.TypeId == ElementId.InvalidElementId)
                throw new InvalidOperationException(LemoineStrings.T("pdfRegionWand.adapter.noFloorType"));
            if (options.LevelId == ElementId.InvalidElementId)
                throw new InvalidOperationException(LemoineStrings.T("pdfRegionWand.adapter.noLevel"));

            double z = (doc.GetElement(options.LevelId) as Level)?.Elevation ?? transform.PlanZ;

            var profile = new List<CurveLoop> { BuildLoop(plan.OuterCurves, transform, z) };

            for (int i = 0; i < plan.HoleCurves.Count; i++)
            {
                double holeFt2 = transform.AreaPtsToFt2(PolylineOps.Area(plan.Holes[i]));
                if (holeFt2 < options.MinHoleAreaFt2)
                {
                    log(LemoineStrings.T("pdfRegionWand.adapter.holeSkipped", plan.FaceId, holeFt2), "info");
                    continue;
                }
                profile.Add(BuildLoop(plan.HoleCurves[i], transform, z));
            }

            var floor = Floor.Create(doc, profile, options.TypeId, options.LevelId,
                                     options.Structural, null, 0.0);
            return new List<ElementId> { floor.Id };
        }

        private static CurveLoop BuildLoop(IReadOnlyList<PlanCurve> pieces, PdfToModelTransform t, double z)
        {
            // Revit rejects curves shorter than ~1/256 ft, and CurveLoop demands
            // exact end-to-start contiguity. Walk the pieces welding each curve's
            // start to the previous end; micro segments are absorbed by letting
            // the next curve start where the last *emitted* curve ended.
            const double minLen = 0.005;

            var curves = new List<Curve>();
            XYZ first = WithZ(t.PdfToModel(pieces[0].Start), z);
            XYZ current = first;

            for (int i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i];
                XYZ end = i == pieces.Count - 1 ? first : WithZ(t.PdfToModel(piece.End), z);
                if (current.DistanceTo(end) < minLen) continue;

                if (piece.Kind == PlanCurveKind.Arc)
                {
                    var m = WithZ(t.PdfToModel(piece.Mid), z);
                    curves.Add(Arc.Create(current, end, m));
                }
                else
                {
                    curves.Add(Line.CreateBound(current, end));
                }
                current = end;
            }

            // Close the ring exactly: a skipped trailing micro segment leaves a
            // sub-tolerance gap, which is absorbed by re-ending the last curve
            // on the start point.
            double gap = current.DistanceTo(first);
            if (gap > 1e-9 && curves.Count > 0)
            {
                if (gap >= minLen)
                {
                    curves.Add(Line.CreateBound(current, first));
                }
                else
                {
                    var last = curves[curves.Count - 1];
                    curves[curves.Count - 1] = last is Arc lastArc
                        ? (Curve)Arc.Create(last.GetEndPoint(0), first, lastArc.Evaluate(0.5, true))
                        : Line.CreateBound(last.GetEndPoint(0), first);
                }
            }

            if (curves.Count < 2)
                throw new InvalidOperationException(LemoineStrings.T("pdfRegionWand.adapter.openLoopFailed"));

            var loop = new CurveLoop();
            foreach (var c in curves) loop.Append(c);
            return loop;
        }

        private static XYZ WithZ(XYZ p, double z) => new XYZ(p.X, p.Y, z);
    }
}
