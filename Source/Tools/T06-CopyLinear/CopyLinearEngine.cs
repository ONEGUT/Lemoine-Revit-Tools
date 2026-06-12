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
        /// components so the same fix can be re-applied to runs pointing any direction.
        /// </summary>
        public sealed class AlignmentCorrection
        {
            public double ExtraRotation;  // radians about +Z, applied at the station point
            public double OffsetAlong;    // feet along the run direction
            public double OffsetSide;     // feet along the horizontal perpendicular
            public double OffsetUp;       // feet along the frame's up axis
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
        /// Compares the source run's box with the first placed instance's box (both as
        /// world-space corner sets) and returns the correction that best aligns their outer
        /// faces: the instance's cross-section is centred on the source's (side + up) and
        /// slid the minimum distance that keeps its body inside the source's along-run span;
        /// an extra 90° about Z is chosen when that makes the instance's cross-section extents
        /// match the source better (a family modelled along its local Y axis). Null on
        /// degenerate input.
        /// </summary>
        public static AlignmentCorrection? ComputeAlignment(
            IReadOnlyList<XYZ> sourceCorners, IReadOnlyList<XYZ> instanceCorners,
            XYZ a, XYZ b, XYZ station)
        {
            if (sourceCorners == null   || sourceCorners.Count == 0)   return null;
            if (instanceCorners == null || instanceCorners.Count == 0) return null;

            var (along, side, up) = RunFrame(a, b);
            var src = ExtentsInFrame(sourceCorners, station, along, side, up);

            var inst0  = ExtentsInFrame(instanceCorners, station, along, side, up);
            var turned = instanceCorners.Select(c => RotateAboutZ(c, station, Math.PI / 2)).ToList();
            var inst90 = ExtentsInFrame(turned, station, along, side, up);

            double srcW = src.Max.Y - src.Min.Y, srcH = src.Max.Z - src.Min.Z;
            double Score((XYZ Min, XYZ Max) e) =>
                Math.Abs((e.Max.Y - e.Min.Y) - srcW) + Math.Abs((e.Max.Z - e.Min.Z) - srcH);

            bool turn = Score(inst90) + Tol < Score(inst0);   // prefer no rotation on a tie
            var ext = turn ? inst90 : inst0;

            var corr = new AlignmentCorrection { ExtraRotation = turn ? Math.PI / 2 : 0.0 };
            corr.OffsetSide = ((src.Min.Y + src.Max.Y) - (ext.Min.Y + ext.Max.Y)) * 0.5;
            corr.OffsetUp   = ((src.Min.Z + src.Max.Z) - (ext.Min.Z + ext.Max.Z)) * 0.5;

            // Along the run the stations govern position — only slide enough to stop the body
            // overhanging an end of the source span (centre on the span if it can't fit).
            double instLen = ext.Max.X - ext.Min.X, srcLen = src.Max.X - src.Min.X;
            if (instLen >= srcLen)
                corr.OffsetAlong = ((src.Min.X + src.Max.X) - (ext.Min.X + ext.Max.X)) * 0.5;
            else if (ext.Min.X < src.Min.X)
                corr.OffsetAlong = src.Min.X - ext.Min.X;
            else if (ext.Max.X > src.Max.X)
                corr.OffsetAlong = src.Max.X - ext.Max.X;

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
