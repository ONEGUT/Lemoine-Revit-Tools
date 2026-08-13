using System;
using System.Collections.Generic;

namespace LemoineTools.Tools.Ceilings.CeilingTags.TagCore
{
    /// <summary>
    /// Reduces a corridor-shaped raster to its centreline and splits that centreline into
    /// straight "stretches" (legs) at corners.
    ///
    /// This is what makes a corridor that wraps a room produce one tag per side: the
    /// centreline of a ring corridor is a closed loop, and splitting it at its four direction
    /// changes yields four legs, each of which gets its own centred tag. A straight corridor
    /// yields one leg, which the planner then subdivides by the spacing setting.
    /// </summary>
    public static class RegionSkeleton
    {
        /// <summary>Direction change at a vertex beyond which the centreline is considered to
        /// turn a corner and a new stretch begins.</summary>
        public const double CornerAngleDeg = 40.0;

        /// <summary>Douglas-Peucker tolerance in feet — removes rasterization stair-stepping
        /// without rounding off real corners.</summary>
        private const double SimplifyTolFt = 1.0;

        // ─────────────────────────────────────────────────────────────────────
        // Thinning
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Zhang-Suen thinning to a 1-cell-wide skeleton. Returns the surviving cell indices.
        /// The two sub-iterations alternate which corner conditions are tested, which is what
        /// keeps the result centred rather than eroding from one side.
        /// </summary>
        public static bool[] Thin(PolyRaster r)
        {
            int w = r.Width, h = r.Height;
            var cur = new bool[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                cur[y * w + x] = r[x, y];

            var doomed = new List<int>();
            bool changed = true;
            // Bound the loop: thinning converges in ~half the shape's width in cells, but a
            // pathological input must never spin forever inside a Revit run.
            int guard = Math.Max(w, h) + 8;

            while (changed && guard-- > 0)
            {
                changed = false;
                for (int step = 0; step < 2; step++)
                {
                    doomed.Clear();

                    for (int y = 1; y < h - 1; y++)
                    for (int x = 1; x < w - 1; x++)
                    {
                        int i = y * w + x;
                        if (!cur[i]) continue;

                        // Neighbours P2..P9, clockwise from north.
                        bool p2 = cur[i + w],     p3 = cur[i + w + 1];
                        bool p4 = cur[i + 1],     p5 = cur[i - w + 1];
                        bool p6 = cur[i - w],     p7 = cur[i - w - 1];
                        bool p8 = cur[i - 1],     p9 = cur[i + w - 1];

                        int b = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0)
                              + (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
                        if (b < 2 || b > 6) continue;

                        int a = Trans(p2, p3) + Trans(p3, p4) + Trans(p4, p5) + Trans(p5, p6)
                              + Trans(p6, p7) + Trans(p7, p8) + Trans(p8, p9) + Trans(p9, p2);
                        if (a != 1) continue;

                        if (step == 0)
                        {
                            if (p2 && p4 && p6) continue;
                            if (p4 && p6 && p8) continue;
                        }
                        else
                        {
                            if (p2 && p4 && p8) continue;
                            if (p2 && p6 && p8) continue;
                        }

                        doomed.Add(i);
                    }

                    if (doomed.Count > 0)
                    {
                        foreach (int i in doomed) cur[i] = false;
                        changed = true;
                    }
                }
            }
            return cur;
        }

        private static int Trans(bool a, bool b) => (!a && b) ? 1 : 0;

        // ─────────────────────────────────────────────────────────────────────
        // Graph tracing
        // ─────────────────────────────────────────────────────────────────────

        private static readonly int[] DX = { 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly int[] DY = { 1, 1, 0, -1, -1, -1, 0, 1 };

        /// <summary>
        /// Traces the thinned skeleton into polylines running between endpoints and junctions.
        /// A skeleton with neither (a pure loop, e.g. a corridor around a room) is traced once
        /// around as a closed polyline.
        /// </summary>
        public static List<List<Pt2>> TraceBranches(PolyRaster r, bool[] skel)
        {
            int w = r.Width, h = r.Height;
            var result = new List<List<Pt2>>();

            int Degree(int x, int y)
            {
                int n = 0;
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + DX[k], ny = y + DY[k];
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h && skel[ny * w + nx]) n++;
                }
                return n;
            }

            var nodes = new List<int>();     // endpoints (deg 1) and junctions (deg >= 3)
            int anySkel = -1;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!skel[i]) continue;
                if (anySkel < 0) anySkel = i;
                int d = Degree(x, y);
                if (d == 1 || d >= 3) nodes.Add(i);
            }

            if (anySkel < 0) return result;   // nothing survived thinning

            var usedEdge = new HashSet<long>();
            long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

            // Walk outward from every node along each of its unvisited neighbours.
            foreach (int startIdx in nodes)
            {
                int sx = startIdx % w, sy = startIdx / w;
                for (int k = 0; k < 8; k++)
                {
                    int nx = sx + DX[k], ny = sy + DY[k];
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int nIdx = ny * w + nx;
                    if (!skel[nIdx]) continue;
                    if (!usedEdge.Add(EdgeKey(startIdx, nIdx))) continue;

                    var poly = new List<Pt2> { r.CellCentre(sx, sy) };
                    int px = sx, py = sy, cx = nx, cy = ny;

                    while (true)
                    {
                        poly.Add(r.CellCentre(cx, cy));
                        if (Degree(cx, cy) != 2) break;   // reached another node

                        int fx = -1, fy = -1;
                        for (int m = 0; m < 8; m++)
                        {
                            int ax = cx + DX[m], ay = cy + DY[m];
                            if (ax < 0 || ay < 0 || ax >= w || ay >= h) continue;
                            if (!skel[ay * w + ax]) continue;
                            if (ax == px && ay == py) continue;
                            fx = ax; fy = ay; break;
                        }
                        if (fx < 0) break;               // dead end
                        if (!usedEdge.Add(EdgeKey(cy * w + cx, fy * w + fx))) break;

                        px = cx; py = cy; cx = fx; cy = fy;
                    }

                    if (poly.Count >= 2) result.Add(poly);
                }
            }

            // No nodes at all → a closed loop. Walk it once from an arbitrary cell.
            if (result.Count == 0)
            {
                var poly  = new List<Pt2>();
                var seen  = new HashSet<int>();
                int cx = anySkel % w, cy = anySkel / w;

                while (true)
                {
                    int idx = cy * w + cx;
                    if (!seen.Add(idx)) break;
                    poly.Add(r.CellCentre(cx, cy));

                    int fx = -1, fy = -1;
                    for (int m = 0; m < 8; m++)
                    {
                        int ax = cx + DX[m], ay = cy + DY[m];
                        if (ax < 0 || ay < 0 || ax >= w || ay >= h) continue;
                        if (!skel[ay * w + ax]) continue;
                        if (seen.Contains(ay * w + ax)) continue;
                        fx = ax; fy = ay; break;
                    }
                    if (fx < 0) break;
                    cx = fx; cy = fy;
                }

                if (poly.Count >= 2)
                {
                    poly.Add(poly[0]);   // close it so the corner split sees the final turn
                    result.Add(poly);
                }
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Legs
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Turns traced branches into straight stretches: drops spurs, simplifies away
        /// rasterization noise, then splits at every corner. A closed branch is rotated to
        /// start ON a corner first, so a side is never split into two half-legs by an
        /// arbitrary trace start point.
        /// </summary>
        public static List<List<Pt2>> ExtractLegs(List<List<Pt2>> branches, double minBranchFt)
        {
            var legs = new List<List<Pt2>>();

            foreach (var raw in branches)
            {
                if (raw.Count < 2) continue;

                bool closed = Dist(raw[0], raw[raw.Count - 1]) < 1e-6;
                if (PolylineLength(raw) < minBranchFt && !closed)
                    continue;   // spur shorter than the corridor is wide — not a real stretch

                var simp = Simplify(raw, SimplifyTolFt);
                if (simp.Count < 2) continue;

                if (closed && simp.Count > 3)
                {
                    simp.RemoveAt(simp.Count - 1);            // drop the duplicated closing point
                    int firstCorner = FindFirstCorner(simp, true);
                    if (firstCorner > 0)
                    {
                        var rotated = new List<Pt2>(simp.Count);
                        for (int i = 0; i < simp.Count; i++) rotated.Add(simp[(firstCorner + i) % simp.Count]);
                        simp = rotated;
                    }
                    simp.Add(simp[0]);                        // re-close after rotating
                }

                // Split at corners.
                var cur = new List<Pt2> { simp[0] };
                for (int i = 1; i < simp.Count; i++)
                {
                    cur.Add(simp[i]);
                    if (i < simp.Count - 1 && TurnDeg(simp[i - 1], simp[i], simp[i + 1]) > CornerAngleDeg)
                    {
                        legs.Add(cur);
                        cur = new List<Pt2> { simp[i] };
                    }
                }
                if (cur.Count >= 2) legs.Add(cur);
            }

            return legs;
        }

        private static int FindFirstCorner(List<Pt2> pts, bool wrap)
        {
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                Pt2 prev = pts[(i - 1 + n) % n];
                Pt2 next = pts[(i + 1) % n];
                if (!wrap && (i == 0 || i == n - 1)) continue;
                if (TurnDeg(prev, pts[i], next) > CornerAngleDeg) return i;
            }
            return -1;
        }

        /// <summary>Direction change at vertex b, in degrees (0 = dead straight).</summary>
        public static double TurnDeg(Pt2 a, Pt2 b, Pt2 c)
        {
            Pt2 v1 = (b - a).Normalized();
            Pt2 v2 = (c - b).Normalized();
            double dot = v1.X * v2.X + v1.Y * v2.Y;
            if (dot > 1.0) dot = 1.0;
            if (dot < -1.0) dot = -1.0;
            return Math.Acos(dot) * 180.0 / Math.PI;
        }

        public static double PolylineLength(List<Pt2> pts)
        {
            double len = 0;
            for (int i = 1; i < pts.Count; i++) len += Dist(pts[i - 1], pts[i]);
            return len;
        }

        private static double Dist(Pt2 a, Pt2 b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Point at a given arc-length distance along a polyline.</summary>
        public static Pt2 PointAtLength(List<Pt2> pts, double target)
        {
            if (pts.Count == 0) return new Pt2(0, 0);
            if (pts.Count == 1) return pts[0];

            double run = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                double seg = Dist(pts[i - 1], pts[i]);
                if (run + seg >= target || i == pts.Count - 1)
                {
                    double t = seg < 1e-9 ? 0 : (target - run) / seg;
                    if (t < 0) t = 0;
                    if (t > 1) t = 1;
                    return new Pt2(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * t,
                                   pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * t);
                }
                run += seg;
            }
            return pts[pts.Count - 1];
        }

        // ── Douglas-Peucker ──────────────────────────────────────────────────
        public static List<Pt2> Simplify(List<Pt2> pts, double tol)
        {
            if (pts.Count < 3) return new List<Pt2>(pts);

            var keep = new bool[pts.Count];
            keep[0] = keep[pts.Count - 1] = true;
            SimplifySegment(pts, 0, pts.Count - 1, tol, keep);

            var outPts = new List<Pt2>();
            for (int i = 0; i < pts.Count; i++) if (keep[i]) outPts.Add(pts[i]);
            return outPts;
        }

        private static void SimplifySegment(List<Pt2> pts, int first, int last, double tol, bool[] keep)
        {
            if (last <= first + 1) return;

            double maxDist = -1; int maxIdx = -1;
            Pt2 a = pts[first], b = pts[last];
            double abx = b.X - a.X, aby = b.Y - a.Y;
            double abLen = Math.Sqrt(abx * abx + aby * aby);

            for (int i = first + 1; i < last; i++)
            {
                double d;
                if (abLen < 1e-9)
                {
                    d = Dist(pts[i], a);
                }
                else
                {
                    // Perpendicular distance from pts[i] to the chord a→b.
                    d = Math.Abs((pts[i].X - a.X) * aby - (pts[i].Y - a.Y) * abx) / abLen;
                }
                if (d > maxDist) { maxDist = d; maxIdx = i; }
            }

            if (maxDist > tol && maxIdx > 0)
            {
                keep[maxIdx] = true;
                SimplifySegment(pts, first, maxIdx, tol, keep);
                SimplifySegment(pts, maxIdx, last, tol, keep);
            }
        }
    }
}
