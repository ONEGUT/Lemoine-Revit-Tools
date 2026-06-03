using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.AutoDimension.Resolvers
{
    /// <summary>
    /// GRID target resolver (~95% of cases). Picks the nearest grid whose line runs across the
    /// measurement axis (so the distance to it is meaningful), within the distance cap, and returns
    /// the grid's reference. Deliberately simple and self-contained — it never touches slab-edge
    /// logic.
    ///
    /// Collecting grids, projecting their direction/midpoint and resolving link references is
    /// axis- and clash-independent, so it is done ONCE per view and cached on the resolver instance
    /// (the engine reuses one instance for every clash and both axes). Each <see cref="Resolve"/>
    /// call then only runs the cheap per-axis perpendicularity + distance test.
    /// </summary>
    public sealed class GridTargetResolver : ITargetResolver
    {
        /// <summary>One grid line, with everything axis-independent precomputed.</summary>
        private sealed class GridCand
        {
            public Reference Ref = null!;
            public Core.Vec2 Dir2d;   // projected, normalized grid direction
            public Core.Vec2 Mid2d;   // projected grid midpoint
            public string Key = "";
        }

        private List<GridCand>? _cache;
        private ResolveContext? _cacheCtx;

        public ResolvedTarget Resolve(SourceLine source, ResolveContext ctx)
        {
            var grids = EnsureCache(ctx);

            Core.Vec2 axis = ctx.Axis;
            double srcAxial = source.Anchor2d.Dot(axis);
            double perpCos = Math.Cos((90.0 - ctx.Config.AxisToleranceDeg) * Math.PI / 180.0);

            double bestDist = double.MaxValue;
            Reference? bestRef = null;
            Core.Vec2 bestPt = source.Anchor2d;
            string bestKey = "";

            foreach (var g in grids)
            {
                // Want grids running ACROSS the axis (perpendicular), so |dir·axis| ≈ 0.
                if (Math.Abs(g.Dir2d.Dot(axis)) > perpCos) continue;

                double gridAxial = g.Mid2d.Dot(axis);
                double dist = Math.Abs(gridAxial - srcAxial);
                if (dist > ctx.Config.MaxDistanceFt) continue;
                if (dist >= bestDist) continue;

                bestDist = dist;
                bestRef  = g.Ref;
                bestPt   = source.Anchor2d + axis * (gridAxial - srcAxial);
                bestKey  = g.Key;
            }

            if (bestRef == null)
                return ResolvedTarget.Fail(source.SourceKey, Core.TargetType.Grid,
                    "no grid within distance cap runs across the measurement axis");

            return ResolvedTarget.Ok(bestRef, bestPt, bestKey);
        }

        /// <summary>Builds the axis-independent grid-candidate list once per view (per context).</summary>
        private List<GridCand> EnsureCache(ResolveContext ctx)
        {
            if (_cache != null && ReferenceEquals(_cacheCtx, ctx)) return _cache;

            var list = new List<GridCand>();
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

                    Reference? gref = new Reference(grid);
                    if (sd.Link != null)
                    {
                        gref = LinkRefHelper.ToHostReference(sd.Link, gref!, ctx.ReportMissingLink);
                        if (gref == null) continue;
                    }

                    XYZ dir3d = sd.Transform.OfVector(gc.GetEndPoint(1) - gc.GetEndPoint(0));
                    XYZ mid3d = sd.Transform.OfPoint(gc.Evaluate(0.5, true));

                    list.Add(new GridCand
                    {
                        Ref   = gref!,
                        Dir2d = ctx.Projection.Dir2D(dir3d).Normalized(),
                        Mid2d = ctx.Projection.To2D(mid3d),
                        Key   = "grid:" + (sd.Link != null ? sd.Link.Id.IntegerValue + ":" : "") + grid.Id.IntegerValue,
                    });
                }
            }

            ctx.Log($"Target cache: {list.Count} grid(s) available across {ctx.Sources.Count} document(s).", "info");
            _cache = list;
            _cacheCtx = ctx;
            return list;
        }
    }
}
