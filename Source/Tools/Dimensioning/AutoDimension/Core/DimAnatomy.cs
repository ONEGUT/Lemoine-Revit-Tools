using System;
using System.Collections.Generic;

namespace LemoineTools.Tools.Dimensioning.AutoDimension.Core
{
    /// <summary>
    /// The full drawn anatomy of a planned dimension at its current side/offset/text states,
    /// modelled as real line/box primitives in view 2D (model ft): the dimension line, one
    /// witness line per reference, every value-text box (inline or relocated tag), and a chord
    /// approximation of each moved tag's arc leader. This is what lets the scorer enforce the
    /// drafting rules a fat bounding band cannot represent — dimension lines never crossing
    /// witness lines or each other, leaders not crossing leaders, text never covered by lines.
    /// Rebuilt by <see cref="DimGeometry.RecomputeBounds"/> whenever side/offset/states change.
    /// Revit-free.
    /// </summary>
    public sealed class DimAnatomy
    {
        /// <summary>The dimension line itself (source→target at the current offset).</summary>
        public Seg2 DimLine { get; private set; }

        /// <summary>The dimension line fattened by ~¾ text height each side — the exclusive
        /// band a string needs to stay readable. Used for line-vs-line spacing and
        /// line-through-obstacle penalties (area-based, like the old whole-string band).</summary>
        public Box2 LineBand { get; private set; }

        /// <summary>One witness (extension) line per reference anchor, from just off the
        /// anchor to just past the dimension line. Witnesses may legitimately cross object
        /// lines and each other — only dimension lines must never cross them.</summary>
        public List<Seg2> Witnesses { get; } = new List<Seg2>();

        /// <summary>Every segment's value-text box at its actual planned spot: inline boxes on
        /// the reading side of the line, moved boxes at their planned column slot.</summary>
        public List<Box2> TextBoxes { get; } = new List<Box2>();

        /// <summary>Chord approximation of each moved tag's arc leader (anchor on the
        /// dimension line → tag front edge). Good enough to count crossings.</summary>
        public List<Seg2> Leaders { get; } = new List<Seg2>();

        /// <summary>Union of everything above — broad-phase box for collision pruning.</summary>
        public Box2 Bounds { get; private set; }

        public static DimAnatomy Build(PlannedDimension d, LayoutConfig cfg)
        {
            var an = new DimAnatomy();
            Vec2 axis = d.AxisDir.Normalized();
            Vec2 perp = axis.Perp();
            double sign = d.Side == DimSide.Positive ? 1.0 : -1.0;
            double th = Math.Max(cfg.TextHeightFt, 1e-6);
            Vec2 offVec = perp * (sign * d.OffsetFt);
            double lineLevel = d.SourcePoint.Dot(perp) + sign * d.OffsetFt;

            // ── Dimension line + its readable band ────────────────────────────
            Vec2 a = d.SourcePoint + offVec;
            Vec2 b = d.TargetPoint + offVec;
            an.DimLine = new Seg2(a, b);
            // Pad the band perpendicular to the line only (plus a hair along it): two strings
            // deliberately aligned end-to-end on one lane must NOT read as overlapping.
            double band = th * 0.75;
            double tiny = th * 0.1;
            double padX = Math.Abs(perp.X) * band + Math.Abs(axis.X) * tiny;
            double padY = Math.Abs(perp.Y) * band + Math.Abs(axis.Y) * tiny;
            var lineBox = Box2.FromPoints(a, b);
            an.LineBand = new Box2(lineBox.MinX - padX, lineBox.MinY - padY,
                                   lineBox.MaxX + padX, lineBox.MaxY + padY);
            Box2 bounds = an.LineBand;

            // ── Witness lines: anchor (+gap) → just past the dimension LINE LEVEL ──
            // Each anchor keeps its true position (a dense-cluster chain has members scattered
            // off the run line), so the witness runs from the anchor toward the line — whichever
            // side of it the anchor sits on — and overshoots 1/8" beyond it.
            if (d.RefAnchors != null)
            {
                foreach (var r in d.RefAnchors)
                {
                    double anchorPerp = r.Dot(perp);
                    double dir = lineLevel >= anchorPerp ? 1.0 : -1.0;
                    var w = new Seg2(
                        r + perp * (dir * cfg.WitnessGapFt),
                        axis * r.Dot(axis) + perp * (lineLevel + dir * cfg.WitnessOvershootFt));
                    an.Witnesses.Add(w);
                    bounds = bounds.Union(w.Bounds);
                }
            }

            // ── Per-segment value text (+ leader chords for moved tags) ───────
            double[] segBounds = DimGeometry.SegmentBoundaries(d, axis);
            for (int k = 0; k < d.Segments.Count; k++)
            {
                var seg = d.Segments[k];
                double centreA = (segBounds[k] + segBounds[k + 1]) * 0.5;
                double halfAlong = Math.Max(seg.TextWidthFt, th) * 0.5;
                double halfPerp  = th * 0.55;

                if (TagColumnPlanner.IsMoved(seg.TextState) && seg.TagPos.HasValue)
                {
                    Vec2 tag = seg.TagPos.Value;
                    Box2 box = AxisBox(tag, axis, perp, halfAlong, halfPerp);
                    an.TextBoxes.Add(box);
                    bounds = bounds.Union(box);

                    // Leader: from the segment's anchor point on the dimension line to the
                    // tag's front (arc-side) edge — the edge facing back toward the group,
                    // so it mirrors when the column hangs off the other end. The real leader
                    // is an arc; its chord is what we test for crossings.
                    double colDir = d.TagColumnDir < 0 ? -1.0 : 1.0;
                    Vec2 anchor = axis * centreA + perp * lineLevel;
                    Vec2 front  = tag - axis * (halfAlong * colDir);
                    var leader  = new Seg2(anchor, front);
                    an.Leaders.Add(leader);
                    bounds = bounds.Union(leader.Bounds);
                }
                else
                {
                    // Inline text sits on the reading side of the line (+perp in the reading
                    // orientation — above a horizontal line, left of a vertical one).
                    Vec2 centre = axis * centreA + perp * (lineLevel + th * 0.7);
                    Box2 box = AxisBox(centre, axis, perp, halfAlong, halfPerp);
                    an.TextBoxes.Add(box);
                    bounds = bounds.Union(box);
                }
            }

            an.Bounds = bounds;
            return an;
        }

        /// <summary>AABB of a text box whose along/perp half-extents follow the measurement
        /// axis (the axes are view-aligned in practice, so the AABB is exact).</summary>
        private static Box2 AxisBox(Vec2 centre, Vec2 axis, Vec2 perp, double halfAlong, double halfPerp)
        {
            double halfX = Math.Abs(axis.X) * halfAlong + Math.Abs(perp.X) * halfPerp;
            double halfY = Math.Abs(axis.Y) * halfAlong + Math.Abs(perp.Y) * halfPerp;
            return Box2.FromCenter(centre, halfX, halfY);
        }
    }
}
