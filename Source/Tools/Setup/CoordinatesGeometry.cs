using System;
using Autodesk.Revit.DB;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// Pure plan-geometry helpers shared by Align Coordinates and Compare Grids: read a grid's
    /// line in the XY plane, intersect two grid lines, and measure the lateral offset / angle
    /// between two (effectively infinite) grid lines. All inputs/outputs are in whatever frame
    /// the caller supplies — callers transform link geometry to host world coordinates first.
    /// </summary>
    internal static class CoordinatesGeometry
    {
        private const double Eps = 1e-9;

        /// <summary>
        /// A grid as a flat XY line: a point on it and a normalized XY direction.
        /// Returns false for a grid with no straight curve or a degenerate (vertical-in-XY) line.
        /// </summary>
        public static bool TryGridLine(Grid g, out XYZ point, out XYZ dir)
        {
            point = XYZ.Zero;
            dir   = XYZ.BasisX;
            Curve? c = null;
            try { c = g?.Curve; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("CoordinatesGeometry.TryGridLine: read grid curve", ex); c = null; }
            if (c == null) return false;

            XYZ a, b;
            try { a = c.GetEndPoint(0); b = c.GetEndPoint(1); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("CoordinatesGeometry.TryGridLine: read grid endpoints", ex); return false; }

            var d = new XYZ(b.X - a.X, b.Y - a.Y, 0);
            if (d.GetLength() < Eps) return false;   // grid is vertical in plan — no usable XY direction

            point = new XYZ(a.X, a.Y, 0);
            dir   = d.Normalize();
            return true;
        }

        /// <summary>
        /// XY intersection of line (p1,d1) with line (p2,d2), or null when the directions are
        /// parallel (no unique intersection). Z is returned as 0 — the caller applies the level.
        /// </summary>
        public static XYZ? Intersect(XYZ p1, XYZ d1, XYZ p2, XYZ d2)
        {
            double denom = d1.X * d2.Y - d1.Y * d2.X;
            if (Math.Abs(denom) < Eps) return null;
            double t = ((p2.X - p1.X) * d2.Y - (p2.Y - p1.Y) * d2.X) / denom;
            return new XYZ(p1.X + t * d1.X, p1.Y + t * d1.Y, 0);
        }

        /// <summary>Signed planar bearing of an XY direction, in radians.</summary>
        public static double Bearing(XYZ dir) => Math.Atan2(dir.Y, dir.X);

        /// <summary>
        /// Smallest rotation (radians, in (−π/2, π/2]) that turns <paramref name="from"/> onto
        /// <paramref name="to"/>, treating both as undirected lines (180° ambiguity collapsed) so
        /// a grid is never flipped end-for-end.
        /// </summary>
        public static double UndirectedDelta(double fromBearing, double toBearing)
        {
            double theta = toBearing - fromBearing;
            while (theta >   Math.PI / 2) theta -= Math.PI;
            while (theta <= -Math.PI / 2) theta += Math.PI;
            return theta;
        }

        /// <summary>Perpendicular (lateral) distance from point <paramref name="q"/> to line (p,dir), in feet.</summary>
        public static double LateralOffset(XYZ p, XYZ dir, XYZ q)
        {
            var w = new XYZ(q.X - p.X, q.Y - p.Y, 0);
            double along = w.X * dir.X + w.Y * dir.Y;
            var perp = new XYZ(w.X - along * dir.X, w.Y - along * dir.Y, 0);
            return perp.GetLength();
        }

        /// <summary>Unsigned angle between two XY directions, in radians (0..π/2, 180° ambiguity collapsed).</summary>
        public static double AngleBetween(XYZ d1, XYZ d2)
        {
            double dot = Math.Abs(d1.X * d2.X + d1.Y * d2.Y);
            dot = Math.Max(0.0, Math.Min(1.0, dot));
            return Math.Acos(dot);
        }
    }
}
