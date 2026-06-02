using System;

namespace LemoineTools.Tools.Testing.AutoDimension.Resolvers
{
    /// <summary>
    /// MANUAL_DATUM resolver — the user picked one or more datum edges per view; every clash
    /// dimensions out to the picked datum. For a given axis pass it selects the datum whose edge
    /// runs ACROSS that axis (so measuring along the axis to it is meaningful — e.g. a horizontal
    /// slab edge serves the vertical pass), then measures the along-axis offset from the source to
    /// the datum point. The datum reference is used verbatim (already host-usable, even for links).
    /// </summary>
    public sealed class ManualDatumResolver : ITargetResolver
    {
        public ResolvedTarget Resolve(SourceLine source, ResolveContext ctx)
        {
            Core.Vec2 axis = ctx.Axis;
            double srcAxial = source.Anchor2d.Dot(axis);
            double axisCosTol = Math.Cos(ctx.Config.AxisToleranceDeg * Math.PI / 180.0);

            if (ctx.Datums == null || ctx.Datums.Count == 0)
                return ResolvedTarget.Fail(source.SourceKey, Core.TargetType.SlabEdge,
                    "manual-datum mode but no datum was picked for this view");

            // Pick the datum best aligned to this axis: its edge must be roughly perpendicular to
            // the axis (so the axis-normal of the edge ≈ the axis). Datums with an unknown direction
            // are eligible only when they are the sole datum (user intent is unambiguous).
            ManualDatum? best = null;
            double bestAlign = axisCosTol;
            foreach (var dm in ctx.Datums)
            {
                if (dm.WorldDir == null)
                {
                    if (ctx.Datums.Count == 1) best = dm;   // single datum → serve every axis pass
                    continue;
                }
                Core.Vec2 edge2d = ctx.Projection.Dir2D(dm.WorldDir).Normalized();
                // Edge perpendicular to axis ⇔ its in-plane normal is parallel to axis.
                Core.Vec2 edgeNormal = edge2d.Perp();
                double align = Math.Abs(edgeNormal.Dot(axis));
                if (align >= bestAlign) { bestAlign = align; best = dm; }
            }

            if (best == null)
                return ResolvedTarget.Fail(source.SourceKey, Core.TargetType.SlabEdge,
                    "no picked datum runs across this axis");

            double datumAxial = ctx.Projection.To2D(best.WorldPoint).Dot(axis);
            double delta = datumAxial - srcAxial;
            if (Math.Abs(delta) < 1e-4)
                return ResolvedTarget.Fail(source.SourceKey, Core.TargetType.SlabEdge,
                    "clash sits on the datum — no measurable offset on this axis");

            Core.Vec2 targetPt = source.Anchor2d + axis * delta;
            return ResolvedTarget.Ok(best.Ref, targetPt, best.Key);
        }
    }
}
