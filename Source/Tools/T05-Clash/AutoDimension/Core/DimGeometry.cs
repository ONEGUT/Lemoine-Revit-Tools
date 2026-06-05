namespace LemoineTools.Tools.Clash.AutoDimension.Core
{
    /// <summary>
    /// Pure geometry helpers shared by the scorer and layout engine. Recomputes a planned
    /// dimension's paper-space bounds from its current side/offset. Revit-free.
    /// </summary>
    public static class DimGeometry
    {
        /// <summary>
        /// Recomputes <see cref="PlannedDimension.PaperBounds"/> from the source/target
        /// anchors, the current side and offset, and the text band thickness. The dimension
        /// line runs along the axis at the offset level; the band is widened by the text
        /// height (plus a small pad) so collision tests cover the value text.
        /// </summary>
        public static void RecomputeBounds(PlannedDimension d, LayoutConfig cfg)
        {
            Vec2 axis = d.AxisDir.Normalized();
            Vec2 perp = axis.Perp();
            double sign = d.Side == DimSide.Positive ? 1.0 : -1.0;
            Vec2 offVec = perp * (sign * d.OffsetFt);

            Vec2 a = d.SourcePoint + offVec;
            Vec2 b = d.TargetPoint + offVec;

            // Text band thickness: text height plus a small pad. Moved text (staggered above or
            // flipped below) reaches one extra band outward, so widen on that case.
            double band = cfg.TextHeightFt * 1.5;
            bool moved = false;
            foreach (var seg in d.Segments)
                if (seg.TextState == SegmentTextState.Staggered || seg.TextState == SegmentTextState.Flipped)
                { moved = true; break; }
            if (moved) band += cfg.TextHeightFt;

            var box = Box2.FromPoints(a, b);
            d.PaperBounds = new Box2(box.MinX - band, box.MinY - band,
                                     box.MaxX + band, box.MaxY + band);
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
