using System;
using System.Collections.Generic;
using LemoineTools.PdfGeometry.Plans;
using LemoineTools.PdfGeometry.Primitives;

namespace LemoineTools.PdfGeometry.Arcs
{
    /// <summary>
    /// Reconstructs circular arcs from a polyline. Runs per shared edge and is
    /// a pure deterministic function of the input vertices, so the two faces
    /// referencing the same edge always reconstruct identical pieces — no
    /// sliver mismatch along shared boundaries.
    /// </summary>
    public static class ArcFitter
    {
        private const int MinArcPoints = 5;
        private const double MinArcSweepDeg = 8.0;
        private const double MaxRadius = 1e5;          // beyond this a "circle" is just a line, pts
        private const double CollinearMergeRad = 2e-3; // ~0.1° — merge angle for adjacent line pieces

        /// <summary>
        /// Splits an open polyline into Line/Arc pieces. <paramref name="tol"/> is
        /// the max radial deviation of covered vertices from the fitted circle.
        /// </summary>
        public static List<PlanCurve> Fit(IReadOnlyList<Pt2> polyline, double tol)
        {
            var pieces = new List<PlanCurve>();
            int n = polyline.Count;
            if (n < 2) return pieces;

            int i = 0;
            while (i < n - 1)
            {
                int j = TryExtendArc(polyline, i, tol, out Pt2 center, out double radius);
                if (j > i)
                {
                    pieces.Add(MakeArc(polyline, i, j, center, radius));
                    i = j;
                }
                else
                {
                    pieces.Add(PlanCurve.Line(polyline[i], polyline[i + 1]));
                    i++;
                }
            }

            MergeCollinearLines(pieces);
            return pieces;
        }

        /// <summary>
        /// Longest window start..j that validates as a single arc; returns the
        /// end index, or <paramref name="start"/> when no arc fits here.
        /// </summary>
        private static int TryExtendArc(IReadOnlyList<Pt2> pts, int start, double tol, out Pt2 center, out double radius)
        {
            center = default; radius = 0;
            int n = pts.Count;
            if (start + MinArcPoints - 1 > n - 1) return start;

            int best = start;
            Pt2 bestC = default; double bestR = 0;
            for (int j = start + MinArcPoints - 1; j < n; j++)
            {
                if (!ValidateWindow(pts, start, j, tol, out Pt2 c, out double r))
                {
                    // Windows only get harder to satisfy as they grow — stop at the first failure.
                    break;
                }
                best = j; bestC = c; bestR = r;
            }
            if (best == start) return start;
            center = bestC; radius = bestR;
            return best;
        }

        private static bool ValidateWindow(IReadOnlyList<Pt2> pts, int i0, int i1, double tol, out Pt2 center, out double radius)
        {
            center = default; radius = 0;
            if (!FitCircleKasa(pts, i0, i1, out center, out radius)) return false;
            if (radius < tol * 4 || radius > MaxRadius) return false;

            // Every covered vertex within radial tolerance.
            for (int k = i0; k <= i1; k++)
                if (Math.Abs(pts[k].DistanceTo(center) - radius) > tol)
                    return false;

            // Monotone turning direction, no step spanning ≥ 90° of the circle.
            int sign = 0;
            for (int k = i0 + 1; k < i1; k++)
            {
                double cross = (pts[k] - pts[k - 1]).Cross(pts[k + 1] - pts[k]);
                int s = cross > 1e-12 ? 1 : cross < -1e-12 ? -1 : 0;
                if (s != 0)
                {
                    if (sign != 0 && s != sign) return false;
                    sign = s;
                }
            }
            if (sign == 0) return false; // straight — not an arc
            for (int k = i0 + 1; k <= i1; k++)
                if (pts[k - 1].DistanceTo(pts[k]) > radius * Math.Sqrt(2.0))
                    return false;

            // Require a real sweep so a barely-bowed line never becomes a giant arc.
            double sweep = SweepAngle(pts, i0, i1, center, sign);
            return Math.Abs(sweep) * 180.0 / Math.PI >= MinArcSweepDeg;
        }

        /// <summary>Kåsa algebraic least-squares circle fit over pts[i0..i1].</summary>
        private static bool FitCircleKasa(IReadOnlyList<Pt2> pts, int i0, int i1, out Pt2 center, out double radius)
        {
            center = default; radius = 0;
            int n = i1 - i0 + 1;
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0, sz = 0;
            for (int k = i0; k <= i1; k++)
            {
                double x = pts[k].X, y = pts[k].Y, z = x * x + y * y;
                sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
                sxz += x * z; syz += y * z; sz += z;
            }

            // Solve [sxx sxy sx; sxy syy sy; sx sy n] · [D E F]ᵀ = -[sxz; syz; sz]
            double a11 = sxx, a12 = sxy, a13 = sx;
            double a21 = sxy, a22 = syy, a23 = sy;
            double a31 = sx, a32 = sy, a33 = n;
            double b1 = -sxz, b2 = -syz, b3 = -sz;

            double det = a11 * (a22 * a33 - a23 * a32)
                       - a12 * (a21 * a33 - a23 * a31)
                       + a13 * (a21 * a32 - a22 * a31);
            if (Math.Abs(det) < 1e-12) return false;

            double d = (b1 * (a22 * a33 - a23 * a32)
                      - a12 * (b2 * a33 - a23 * b3)
                      + a13 * (b2 * a32 - a22 * b3)) / det;
            double e = (a11 * (b2 * a33 - a23 * b3)
                      - b1 * (a21 * a33 - a23 * a31)
                      + a13 * (a21 * b3 - b2 * a31)) / det;
            double f = (a11 * (a22 * b3 - b2 * a32)
                      - a12 * (a21 * b3 - b2 * a31)
                      + b1 * (a21 * a32 - a22 * a31)) / det;

            double cx = -d / 2, cy = -e / 2;
            double r2 = cx * cx + cy * cy - f;
            if (r2 <= 0) return false;
            center = new Pt2(cx, cy);
            radius = Math.Sqrt(r2);
            return true;
        }

        private static double SweepAngle(IReadOnlyList<Pt2> pts, int i0, int i1, Pt2 center, int sign)
        {
            // Accumulate signed step angles so sweeps over 180° are handled.
            double sweep = 0;
            double prev = (pts[i0] - center).Angle;
            for (int k = i0 + 1; k <= i1; k++)
            {
                double cur = (pts[k] - center).Angle;
                double d = cur - prev;
                while (d > Math.PI) d -= 2 * Math.PI;
                while (d < -Math.PI) d += 2 * Math.PI;
                sweep += d;
                prev = cur;
            }
            return sweep;
        }

        private static PlanCurve MakeArc(IReadOnlyList<Pt2> pts, int i0, int i1, Pt2 center, double radius)
        {
            int sign = 0;
            for (int k = i0 + 1; k < i1 && sign == 0; k++)
            {
                double cross = (pts[k] - pts[k - 1]).Cross(pts[k + 1] - pts[k]);
                sign = cross > 0 ? 1 : cross < 0 ? -1 : 0;
            }
            double sweep = SweepAngle(pts, i0, i1, center, sign);
            double a0 = (pts[i0] - center).Angle;
            double aMid = a0 + sweep / 2;
            var mid = center + new Pt2(Math.Cos(aMid), Math.Sin(aMid)) * radius;
            return PlanCurve.Arc(pts[i0], pts[i1], mid);
        }

        private static void MergeCollinearLines(List<PlanCurve> pieces)
        {
            for (int i = pieces.Count - 2; i >= 0; i--)
            {
                var a = pieces[i];
                var b = pieces[i + 1];
                if (a.Kind != PlanCurveKind.Line || b.Kind != PlanCurveKind.Line) continue;
                var d1 = (a.End - a.Start).Normalized();
                var d2 = (b.End - b.Start).Normalized();
                if (Math.Abs(d1.Cross(d2)) < CollinearMergeRad && d1.Dot(d2) > 0)
                {
                    pieces[i] = PlanCurve.Line(a.Start, b.End);
                    pieces.RemoveAt(i + 1);
                }
            }
        }
    }
}
