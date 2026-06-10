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
        /// <summary>Linear (curve-driven) categories this tool can copy off one backbone.</summary>
        public static readonly IReadOnlyList<(BuiltInCategory Cat, string Label)> Categories = new[]
        {
            (BuiltInCategory.OST_PipeCurves,       "Pipes"),
            (BuiltInCategory.OST_DuctCurves,       "Ducts"),
            (BuiltInCategory.OST_Conduit,          "Conduit"),
            (BuiltInCategory.OST_CableTray,        "Cable Trays"),
            (BuiltInCategory.OST_FlexPipeCurves,   "Flex Pipes"),
            (BuiltInCategory.OST_FlexDuctCurves,   "Flex Ducts"),
            (BuiltInCategory.OST_StructuralFraming, "Structural Framing"),
        };

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
