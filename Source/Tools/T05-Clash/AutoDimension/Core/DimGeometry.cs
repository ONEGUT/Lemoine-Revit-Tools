namespace LemoineTools.Tools.Clash.AutoDimension.Core
{
    /// <summary>
    /// Pure geometry helpers shared by the scorer and layout engine. Recomputes a planned
    /// dimension's paper-space bounds from its current side/offset. Revit-free.
    /// </summary>
    public static class DimGeometry
    {
        /// <summary>
        /// Recomputes the dimension's derived geometry at its current side/offset/text states:
        /// plans the moved-tag column slots, rebuilds the full <see cref="DimAnatomy"/>
        /// (dimension line, witnesses, text boxes, leader chords), and sets
        /// <see cref="PlannedDimension.PaperBounds"/> to the anatomy's union box (broad-phase
        /// pruning only — all penalties score against the anatomy primitives).
        /// </summary>
        public static void RecomputeBounds(PlannedDimension d, LayoutConfig cfg)
        {
            TagColumnPlanner.PlanColumns(d, cfg);
            d.Anatomy = DimAnatomy.Build(d, cfg);
            d.PaperBounds = d.Anatomy.Bounds;
        }

        /// <summary>
        /// Axial coordinates of the segment boundaries (refs) along <paramref name="axis"/>,
        /// length = segments + 1. Prefers the chainer-supplied <see cref="PlannedDimension.RefAnchors"/>;
        /// falls back to accumulating segment lengths from the span's minimum axial coordinate
        /// (exact for contiguous chains, which is all the chainer emits).
        /// </summary>
        internal static double[] SegmentBoundaries(PlannedDimension d, Vec2 axis)
        {
            int n = d.Segments.Count;
            var bounds = new double[n + 1];
            if (d.RefAnchors != null && d.RefAnchors.Count == n + 1)
            {
                for (int i = 0; i <= n; i++) bounds[i] = d.RefAnchors[i].Dot(axis);
                System.Array.Sort(bounds);
                return bounds;
            }

            bounds[0] = System.Math.Min(d.SourcePoint.Dot(axis), d.TargetPoint.Dot(axis));
            for (int k = 0; k < n; k++) bounds[k + 1] = bounds[k] + d.Segments[k].LengthFt;
            return bounds;
        }

        /// <summary>Projected length of the source→target span along the measurement axis (ft).</summary>
        public static double AxialLength(PlannedDimension d)
        {
            Vec2 axis = d.AxisDir.Normalized();
            return System.Math.Abs((d.TargetPoint - d.SourcePoint).Dot(axis));
        }

        /// <summary>Axial coordinate of the source anchor — the deterministic sort key.</summary>
        public static double AxialStart(PlannedDimension d)
        {
            Vec2 axis = d.AxisDir.Normalized();
            return d.SourcePoint.Dot(axis);
        }
    }
}
