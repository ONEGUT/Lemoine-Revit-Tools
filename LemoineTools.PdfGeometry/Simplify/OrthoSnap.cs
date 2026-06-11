using System;
using System.Collections.Generic;
using LemoineTools.PdfGeometry.Primitives;

namespace LemoineTools.PdfGeometry.Simplify
{
    /// <summary>
    /// Snaps nearly-horizontal / nearly-vertical segments of a closed ring to
    /// true ortho and merges near-collinear runs. Diagonals outside the snap
    /// angle pass through untouched. Corner vertices are recomputed as exact
    /// line intersections so snapped walls stay connected.
    /// </summary>
    public static class OrthoSnap
    {
        private enum Kind { H, V, Free }

        private sealed class Run
        {
            public Kind Kind;
            public double Coord;      // y for H runs, x for V runs (length-weighted)
            public double Weight;     // total length, for merging
            public Pt2 First, Last;   // original run endpoints (free runs: the segment)
        }

        /// <summary>
        /// <paramref name="angleDeg"/>: max deviation from ortho that still snaps.
        /// <paramref name="coordMergeTol"/>: adjacent same-axis runs within this
        /// offset merge into one (use the simplify tolerance).
        /// </summary>
        public static List<Pt2> Apply(IReadOnlyList<Pt2> ring, double angleDeg, double coordMergeTol)
        {
            int n = ring.Count;
            if (n < 3) return new List<Pt2>(ring);

            var kinds = new Kind[n];
            for (int i = 0; i < n; i++)
                kinds[i] = Classify(ring[i], ring[(i + 1) % n], angleDeg);

            // Rotate the start index to a run boundary so circular grouping is linear.
            int start = -1;
            for (int i = 0; i < n; i++)
            {
                int prev = (i + n - 1) % n;
                if (kinds[i] != kinds[prev] || kinds[i] == Kind.Free) { start = i; break; }
            }
            if (start < 0) return new List<Pt2>(ring); // every segment same axis — degenerate ring, leave as-is

            var runs = BuildRuns(ring, kinds, start);
            MergeAdjacentRuns(runs, coordMergeTol);
            if (runs.Count < 3) return new List<Pt2>(ring);

            var result = Reassemble(runs);
            result = PolylineOps.Dedupe(result, 1e-6, isRing: true);

            // Snap sanity: a collapsed or self-intersecting result means the snap
            // destroyed the shape — fall back to the unsnapped ring.
            if (result.Count < 3 ||
                PolylineOps.Area(result) < PolylineOps.Area(ring) * 0.5 ||
                !PolylineOps.IsSimpleRing(result, 1e-9))
                return new List<Pt2>(ring);

            return result;
        }

        private static Kind Classify(Pt2 a, Pt2 b, double angleDeg)
        {
            double dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y);
            double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI; // 0 = horizontal, 90 = vertical
            if (angle <= angleDeg) return Kind.H;
            if (angle >= 90 - angleDeg) return Kind.V;
            return Kind.Free;
        }

        private static List<Run> BuildRuns(IReadOnlyList<Pt2> ring, Kind[] kinds, int start)
        {
            int n = ring.Count;
            var runs = new List<Run>();
            Run? cur = null;
            for (int k = 0; k < n; k++)
            {
                int i = (start + k) % n;
                Pt2 a = ring[i], b = ring[(i + 1) % n];
                double len = a.DistanceTo(b);
                if (len <= double.Epsilon) continue;
                Kind kind = kinds[i];

                bool extend = cur != null && cur.Kind == kind && kind != Kind.Free;
                if (extend)
                {
                    double c = kind == Kind.H ? (a.Y + b.Y) * 0.5 : (a.X + b.X) * 0.5;
                    cur!.Coord = (cur.Coord * cur.Weight + c * len) / (cur.Weight + len);
                    cur.Weight += len;
                    cur.Last = b;
                }
                else
                {
                    cur = new Run
                    {
                        Kind = kind,
                        Coord = kind == Kind.H ? (a.Y + b.Y) * 0.5 : (a.X + b.X) * 0.5,
                        Weight = len,
                        First = a,
                        Last = b,
                    };
                    runs.Add(cur);
                }
            }
            return runs;
        }

        private static void MergeAdjacentRuns(List<Run> runs, double coordMergeTol)
        {
            // Drop tiny connector runs sandwiched between two same-axis runs at
            // (near) the same coordinate — a jogged wall reads as H / tiny-V / H
            // and should collapse to one straight run.
            for (int i = runs.Count - 1; i >= 0 && runs.Count > 3; i--)
            {
                var run = runs[i % runs.Count];
                if (run.Weight > coordMergeTol * 1.5) continue;
                var prev = runs[(i - 1 + runs.Count) % runs.Count];
                var next = runs[(i + 1) % runs.Count];
                if (prev == run || next == run || prev == next) continue;
                if (prev.Kind == Kind.Free || prev.Kind != next.Kind) continue;
                if (Math.Abs(prev.Coord - next.Coord) > coordMergeTol) continue;
                runs.RemoveAt(i % runs.Count);
            }

            // Circularly merge same-axis neighbors whose snapped coordinates agree —
            // a wall traced as H / tiny-jog-free / H would otherwise produce a jog.
            bool merged = true;
            while (merged && runs.Count > 3)
            {
                merged = false;
                for (int i = 0; i < runs.Count; i++)
                {
                    var a = runs[i];
                    var b = runs[(i + 1) % runs.Count];
                    if (a == b) break;
                    if (a.Kind == Kind.Free || a.Kind != b.Kind) continue;
                    if (Math.Abs(a.Coord - b.Coord) > coordMergeTol) continue;

                    a.Coord = (a.Coord * a.Weight + b.Coord * b.Weight) / (a.Weight + b.Weight);
                    a.Weight += b.Weight;
                    a.Last = b.Last;
                    runs.RemoveAt((i + 1) % runs.Count);
                    merged = true;
                    break;
                }
            }
        }

        private static List<Pt2> Reassemble(List<Run> runs)
        {
            var verts = new List<Pt2>();
            int m = runs.Count;
            for (int i = 0; i < m; i++)
            {
                Run cur = runs[i], next = runs[(i + 1) % m];
                GetLine(cur, out Pt2 p1, out Pt2 d1);
                GetLine(next, out Pt2 p2, out Pt2 d2);

                double denom = d1.Cross(d2);
                if (Math.Abs(denom) < 1e-9)
                {
                    // Parallel neighbors (e.g. two H walls at different y): keep a jog.
                    verts.Add(ProjectOnto(cur.Last, p1, d1));
                    verts.Add(ProjectOnto(next.First, p2, d2));
                }
                else
                {
                    double t = (p2 - p1).Cross(d2) / denom;
                    verts.Add(p1 + d1 * t);
                }
            }
            return verts;
        }

        private static void GetLine(Run r, out Pt2 point, out Pt2 dir)
        {
            switch (r.Kind)
            {
                case Kind.H: point = new Pt2(r.First.X, r.Coord); dir = new Pt2(1, 0); break;
                case Kind.V: point = new Pt2(r.Coord, r.First.Y); dir = new Pt2(0, 1); break;
                default:     point = r.First; dir = (r.Last - r.First).Normalized(); break;
            }
        }

        private static Pt2 ProjectOnto(Pt2 p, Pt2 linePoint, Pt2 lineDir) =>
            linePoint + lineDir * (p - linePoint).Dot(lineDir);
    }
}
