using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace LemoineTools.Tools.CopyLinear
{
    /// <summary>
    /// Revit-light geometry + identity helpers for the Copy Linear Elements tool.
    ///
    /// Everything here is pure maths on <see cref="XYZ"/>/doubles so the station and hash
    /// logic stays trivially reviewable and independent of transactions. The run handler owns
    /// all element mutation; this class only decides <em>where</em> things go and <em>what</em>
    /// a source element's identity hash is.
    /// </summary>
    public static class CopyLinearEngine
    {
        private const double Tol = 1e-4;

        // ── Stations ──────────────────────────────────────────────────────────

        /// <summary>
        /// Splits a run of length <paramref name="totalLen"/> (feet) into cells of
        /// <paramref name="segLen"/> feet, leaving a physical <paramref name="gapFeet"/> gap
        /// between consecutive pieces (the gap is taken off the interior cut faces only, so the
        /// run's two outer ends are preserved). Returns each piece as a [start,end] station pair
        /// measured along the curve.
        /// </summary>
        /// <param name="keepRemainder">
        /// When false a trailing cell shorter than <paramref name="segLen"/> is dropped instead of
        /// emitted as a short piece.
        /// </param>
        public static List<(double Start, double End)> SplitStations(
            double totalLen, double segLen, double gapFeet, bool keepRemainder)
        {
            var cells = new List<(double, double)>();
            if (totalLen <= Tol || segLen <= Tol) return cells;

            double half = Math.Max(0.0, gapFeet) * 0.5;
            int    n    = (int)Math.Floor((totalLen + Tol) / segLen);
            double covered = n * segLen;
            bool   hasRemainder = totalLen - covered > Tol;
            int    cellCount = n + (hasRemainder && keepRemainder ? 1 : 0);
            if (cellCount == 0) cellCount = keepRemainder ? 1 : 0; // run shorter than one segment
            if (cellCount == 0) return cells;

            for (int k = 0; k < cellCount; k++)
            {
                double cellStart = k * segLen;
                double cellEnd   = Math.Min((k + 1) * segLen, totalLen);
                if (k == cellCount - 1) cellEnd = totalLen; // final cell always reaches the end

                double s = cellStart + (k == 0 ? 0.0 : half);
                double e = cellEnd   - (k == cellCount - 1 ? 0.0 : half);
                if (e - s > Tol) cells.Add((s, e));
            }
            return cells;
        }

        /// <summary>
        /// Stations (feet from the start) at which to drop a family instance: one every
        /// <paramref name="interval"/> + <paramref name="extraSpacingFeet"/> feet, starting at the
        /// run's start point and never past its end.
        /// </summary>
        public static List<double> PlacementStations(double totalLen, double interval, double extraSpacingFeet)
        {
            var pts  = new List<double>();
            double step = interval + Math.Max(0.0, extraSpacingFeet);
            if (totalLen <= Tol || step <= Tol) return pts;
            for (double d = 0.0; d <= totalLen + Tol; d += step)
                pts.Add(Math.Min(d, totalLen));
            return pts;
        }

        /// <summary>Point on the straight line A→B at <paramref name="station"/> feet from A.</summary>
        public static XYZ PointAlong(XYZ a, XYZ b, double station)
        {
            double len = a.DistanceTo(b);
            if (len <= Tol) return a;
            double t = Math.Max(0.0, Math.Min(1.0, station / len));
            return a + t * (b - a);
        }

        /// <summary>
        /// Plan rotation (radians, about +Z) that aligns a family's +X axis with the run
        /// direction. Returns 0 for a vertical run.
        /// </summary>
        public static double PlanRotation(XYZ dir)
        {
            var flat = new XYZ(dir.X, dir.Y, 0.0);
            if (flat.GetLength() < Tol) return 0.0;
            return Math.Atan2(flat.Y, flat.X);
        }

        // ── Alignment calibration (Replace mode) ─────────────────────────────

        /// <summary>
        /// Correction that makes a placed instance line up with its source run: an extra plan
        /// rotation about +Z at the station point, then a translation stored as run-frame
        /// components. Computed per source element from its own box, so every placement is
        /// corrected relative to the element it replaces.
        /// </summary>
        public sealed class AlignmentCorrection
        {
            public double ExtraRotation;  // radians about +Z, applied at the station point
            public double OffsetAlong;    // feet along the run direction
            public double OffsetSide;     // feet along the horizontal perpendicular
            public double OffsetUp;       // feet along the frame's up axis
        }

        /// <summary>
        /// The placed family's footprint relative to its station, in run-frame components —
        /// measured once from the first placed instance (the only part of alignment that costs
        /// a regen) and then reused for every placement, because a family's box relative to its
        /// own rotated-to-run placement point is constant. X = along, Y = side, Z = up.
        /// </summary>
        public sealed class InstanceProfile
        {
            public XYZ Min0  = XYZ.Zero, Max0  = XYZ.Zero;  // as placed (rotated to the run)
            public XYZ Min90 = XYZ.Zero, Max90 = XYZ.Zero;  // after an extra 90° turn about Z

            /// <summary>
            /// Void-axis verdict from the family's solid faces: true = the open through-axis
            /// points sideways and the instance needs a 90° turn; false = it already faces the
            /// run; null = no decisive void (near-symmetric solid), fall back to box extents.
            /// Box extents alone cannot orient a square-plan section — its 0° and 90° boxes
            /// are identical — which is why the void carries the decision when available.
            /// </summary>
            public bool? VoidTurn;
        }

        /// <summary>
        /// Orthonormal run-local frame: Along = a→b, Side = horizontal perpendicular,
        /// Up completes the set (world +Z for a horizontal run). A vertical run falls
        /// back to BasisY for Side so the frame is always well-defined.
        /// </summary>
        public static (XYZ Along, XYZ Side, XYZ Up) RunFrame(XYZ a, XYZ b)
        {
            XYZ along = b - a;
            along = along.GetLength() < Tol ? XYZ.BasisX : along.Normalize();
            XYZ side = XYZ.BasisZ.CrossProduct(along);
            side = side.GetLength() < Tol ? XYZ.BasisY : side.Normalize();
            XYZ up = along.CrossProduct(side).Normalize();
            return (along, side, up);
        }

        /// <summary>
        /// Min/max extents of a point set measured along each frame axis, relative to
        /// <paramref name="origin"/>. X = along, Y = side, Z = up components.
        /// </summary>
        public static (XYZ Min, XYZ Max) ExtentsInFrame(
            IReadOnlyList<XYZ> points, XYZ origin, XYZ along, XYZ side, XYZ up)
        {
            double minA = double.MaxValue, minS = double.MaxValue, minU = double.MaxValue;
            double maxA = double.MinValue, maxS = double.MinValue, maxU = double.MinValue;
            foreach (var pt in points)
            {
                var d = pt - origin;
                double va = d.DotProduct(along), vs = d.DotProduct(side), vu = d.DotProduct(up);
                if (va < minA) minA = va; if (va > maxA) maxA = va;
                if (vs < minS) minS = vs; if (vs > maxS) maxS = vs;
                if (vu < minU) minU = vu; if (vu > maxU) maxU = vu;
            }
            return (new XYZ(minA, minS, minU), new XYZ(maxA, maxS, maxU));
        }

        /// <summary>
        /// Builds the placed family's station-relative footprint from its world-space box
        /// corners: extents in the run frame as placed, plus the extents after an analytic
        /// 90° turn about Z at the station. Null on degenerate input.
        /// </summary>
        public static InstanceProfile? MeasureInstance(
            IReadOnlyList<XYZ> instanceCorners, XYZ a, XYZ b, XYZ station)
        {
            if (instanceCorners == null || instanceCorners.Count == 0) return null;
            var (along, side, up) = RunFrame(a, b);
            var e0     = ExtentsInFrame(instanceCorners, station, along, side, up);
            var turned = instanceCorners.Select(c => RotateAboutZ(c, station, Math.PI / 2)).ToList();
            var e90    = ExtentsInFrame(turned, station, along, side, up);
            return new InstanceProfile { Min0 = e0.Min, Max0 = e0.Max, Min90 = e90.Min, Max90 = e90.Max };
        }

        /// <summary>
        /// Correction that aligns one placement's outer faces with <em>its own</em> source
        /// element: the source's box is measured in this run's frame relative to this station,
        /// then the instance footprint is centred on it (side + up), slid only enough to keep
        /// the body inside the source's along-run span, with an extra 90° about Z when that
        /// makes the cross-section extents match this source better (a family modelled along
        /// its local Y axis). Pure math — call it for every placement. Null on degenerate input.
        /// </summary>
        public static AlignmentCorrection? ComputeAlignment(
            IReadOnlyList<XYZ> sourceCorners, InstanceProfile profile, XYZ a, XYZ b, XYZ station)
        {
            if (sourceCorners == null || sourceCorners.Count == 0 || profile == null) return null;

            var (along, side, up) = RunFrame(a, b);
            var src = ExtentsInFrame(sourceCorners, station, along, side, up);

            double srcW = src.Max.Y - src.Min.Y, srcH = src.Max.Z - src.Min.Z;
            double Score(XYZ min, XYZ max) =>
                Math.Abs((max.Y - min.Y) - srcW) + Math.Abs((max.Z - min.Z) - srcH);

            // The void-axis verdict wins when available — box extents can't orient a
            // square-plan section (its 0° and 90° boxes score identically).
            bool turn = profile.VoidTurn
                ?? (Score(profile.Min90, profile.Max90) + Tol < Score(profile.Min0, profile.Max0));
            XYZ extMin = turn ? profile.Min90 : profile.Min0;
            XYZ extMax = turn ? profile.Max90 : profile.Max0;

            var corr = new AlignmentCorrection { ExtraRotation = turn ? Math.PI / 2 : 0.0 };
            corr.OffsetSide = ((src.Min.Y + src.Max.Y) - (extMin.Y + extMax.Y)) * 0.5;
            corr.OffsetUp   = ((src.Min.Z + src.Max.Z) - (extMin.Z + extMax.Z)) * 0.5;

            // Along the run the stations govern position — only slide enough to stop the body
            // overhanging an end of the source span (centre on the span if it can't fit).
            double instLen = extMax.X - extMin.X, srcLen = src.Max.X - src.Min.X;
            if (instLen >= srcLen)
                corr.OffsetAlong = ((src.Min.X + src.Max.X) - (extMin.X + extMax.X)) * 0.5;
            else if (extMin.X < src.Min.X)
                corr.OffsetAlong = src.Min.X - extMin.X;
            else if (extMax.X > src.Max.X)
                corr.OffsetAlong = src.Max.X - extMax.X;

            return corr;
        }

        private static XYZ RotateAboutZ(XYZ p, XYZ pivot, double angle)
        {
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            double dx = p.X - pivot.X, dy = p.Y - pivot.Y;
            return new XYZ(pivot.X + dx * cos - dy * sin, pivot.Y + dx * sin + dy * cos, p.Z);
        }

        // ── Identity hash ─────────────────────────────────────────────────────

        /// <summary>
        /// Stable identity descriptor for a source run. Two reads of the same unchanged element
        /// produce the same string; any move, re-size, re-system, re-type, or phase change flips
        /// it — which is exactly what the "only changed" re-run compares.
        /// </summary>
        public static string GeoHash(XYZ a, XYZ b, string size, string system, string typeName, string phase)
        {
            string R(XYZ p) => string.Format(CultureInfo.InvariantCulture,
                "{0:F4},{1:F4},{2:F4}", p.X, p.Y, p.Z);
            // Endpoint order-independent so a flipped curve isn't a false "change".
            string ra = R(a), rb = R(b);
            if (string.CompareOrdinal(ra, rb) > 0) { var t = ra; ra = rb; rb = t; }
            return string.Join("|", ra, rb, size ?? "", system ?? "", typeName ?? "", phase ?? "");
        }

        /// <summary>Composite source key: which document instance + which element, both UniqueId-stable.</summary>
        public static string SourceKey(string linkInstanceUid, string sourceElementUid)
            => (linkInstanceUid ?? "host") + "::" + (sourceElementUid ?? "");
    }
}
