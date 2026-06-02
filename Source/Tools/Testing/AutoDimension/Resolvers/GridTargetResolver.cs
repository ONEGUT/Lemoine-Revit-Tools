using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.AutoDimension.Resolvers
{
    /// <summary>
    /// GRID target resolver (~95% of cases). Picks the nearest grid whose line runs across the
    /// measurement axis (so the horizontal distance to it is meaningful), within the distance
    /// cap, and returns the grid's reference. Deliberately simple and self-contained — it never
    /// touches slab-edge logic.
    /// </summary>
    public sealed class GridTargetResolver : ITargetResolver
    {
        public ResolvedTarget Resolve(SourceLine source, ResolveContext ctx)
        {
            Core.Vec2 axis = ctx.Projection.HorizontalAxis;
            double srcAxial = source.Anchor2d.Dot(axis);

            double bestDist = double.MaxValue;
            Reference? bestRef = null;
            Core.Vec2 bestPt = source.Anchor2d;
            string bestKey = "";

            foreach (var sd in ctx.Sources)
            {
                if (sd.Link != null && !ctx.Config.IncludeLinks) continue;

                List<Grid> grids;
                try
                {
                    grids = new FilteredElementCollector(sd.Doc)
                        .OfClass(typeof(Grid)).Cast<Grid>()
                        .OrderBy(g => g.Id.IntegerValue)
                        .ToList();
                }
                catch (Exception ex) { LemoineLog.Swallowed("GridTargetResolver: collect grids", ex); continue; }

                foreach (var grid in grids)
                {
                    Curve? gc = grid.Curve;
                    if (gc == null) continue;

                    // Grid direction projected into the view; we want grids running ACROSS the
                    // axis (perpendicular), so |dir·axis| should be near zero.
                    XYZ dir3d = sd.Transform.OfVector((gc.GetEndPoint(1) - gc.GetEndPoint(0)));
                    Core.Vec2 dir2d = ctx.Projection.Dir2D(dir3d).Normalized();
                    if (Math.Abs(dir2d.Dot(axis)) > Math.Cos((90.0 - ctx.Config.AxisToleranceDeg) * Math.PI / 180.0))
                        continue; // not sufficiently perpendicular to the axis

                    // Axial coordinate of the grid line (use its midpoint).
                    XYZ mid3d = sd.Transform.OfPoint(gc.Evaluate(0.5, true));
                    Core.Vec2 mid2d = ctx.Projection.To2D(mid3d);
                    double gridAxial = mid2d.Dot(axis);
                    double dist = Math.Abs(gridAxial - srcAxial);

                    if (dist > ctx.Config.MaxDistanceFt) continue;
                    if (dist >= bestDist) continue;

                    Reference? gref = new Reference(grid);
                    if (sd.Link != null)
                    {
                        gref = LinkRefHelper.ToHostReference(sd.Link, gref!, ctx.ReportMissingLink);
                        if (gref == null) continue;
                    }

                    bestDist = dist;
                    bestRef  = gref;
                    bestPt   = source.Anchor2d + axis * (gridAxial - srcAxial);
                    bestKey  = "grid:" + (sd.Link != null ? sd.Link.Id.IntegerValue + ":" : "") + grid.Id.IntegerValue;
                }
            }

            if (bestRef == null)
                return ResolvedTarget.Fail(source.SourceKey, Core.TargetType.Grid,
                    "no grid within distance cap runs across the measurement axis");

            return ResolvedTarget.Ok(bestRef, bestPt, bestKey);
        }
    }
}
