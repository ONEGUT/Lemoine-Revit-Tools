using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // ZonePolygonOps — 2D polygon boolean work, done with Revit's own solids.
    //
    // There is no 2D polygon boolean library here and writing one (clipping,
    // winding, self-intersection, robustness) is a long tail of failures. Revit
    // already ships a robust 3D boolean engine, so every 2D operation is done by
    // extruding the polygons into 1 ft prisms, running the boolean, and reading
    // the outer loop of each upward-facing planar face back out.
    //
    // This is the shared machinery behind BOTH:
    //   • the key plan's building outline (union of the slab footprints), and
    //   • the highlight trimming (area rectangle ∩ building outline, then the
    //     matchline half-plane cuts).
    //
    // API surface, confirmed against libs/RevitAPI.dll (2024) by reading the
    // metadata tables scoped to each declaring type, never a string search:
    //
    //   Solid  GeometryCreationUtilities.CreateExtrusionGeometry(
    //              profileLoops, extrusionDir, extrusionDist)
    //   Solid  BooleanOperationsUtils.ExecuteBooleanOperation(
    //              solid0, solid1, booleanType)
    //   BooleanOperationsType.Union / Intersect / Difference
    //   FaceArray  Solid.Faces
    //   XYZ        PlanarFace.FaceNormal
    //   IList<CurveLoop>  Face.GetEdgesAsCurveLoops()
    //
    // Every polygon here is a list of world XY points at z = 0, implicitly
    // closed (the last point is NOT a repeat of the first).
    //
    // UNVERIFIED, needs a Windows/Revit run: how Revit's booleans behave on real
    // slab and scope-box profiles — slivers where two shapes touch at a hair of
    // overlap, near-tangent edges. Every operation is guarded and reports what
    // it could not do rather than losing the input silently.
    // =========================================================================
    public static class ZonePolygonOps
    {
        /// <summary>
        /// How far apart two points must be to count as distinct, in feet. Revit rejects a
        /// zero-length curve, so a raw tessellation cannot be rebuilt into a CurveLoop
        /// without this weld.
        /// </summary>
        public const double WeldToleranceFt = 1e-4;

        /// <summary>
        /// Depth of the throwaway prism a polygon is extruded into, in feet. Its only job is
        /// to give the boolean engine something with volume; nothing ever reads it.
        /// </summary>
        public const double ExtrusionDepthFt = 1.0;

        /// <summary>
        /// Polygons smaller than this are dropped from a boolean result, in square feet.
        /// A boolean between two shapes that merely touch produces slivers of near-zero area;
        /// drawn on a key plan they read as stray marks.
        /// </summary>
        public const double MinResultAreaFt2 = 1.0;

        // ── Point-list plumbing ───────────────────────────────────────────────

        /// <summary>
        /// Drops points closer together than <see cref="WeldToleranceFt"/>, including a first
        /// point coincident with the last, so the list can become a valid CurveLoop.
        /// </summary>
        public static List<XYZ> Dedupe(IEnumerable<XYZ>? pts)
        {
            var outPts = new List<XYZ>();
            if (pts == null) return outPts;

            foreach (var p in pts)
            {
                if (p == null) continue;
                if (outPts.Count > 0 && outPts[outPts.Count - 1].DistanceTo(p) < WeldToleranceFt) continue;
                outPts.Add(new XYZ(p.X, p.Y, 0));
            }
            while (outPts.Count >= 2 &&
                   outPts[0].DistanceTo(outPts[outPts.Count - 1]) < WeldToleranceFt)
                outPts.RemoveAt(outPts.Count - 1);
            return outPts;
        }

        /// <summary>A curve loop as deduped world XY points at z = 0, straight segments only.</summary>
        public static List<XYZ> FlattenLoop(CurveLoop? loop, Transform? tf = null)
        {
            var pts = new List<XYZ>();
            if (loop == null) return pts;
            Transform t = tf ?? Transform.Identity;

            try
            {
                foreach (var c in loop)
                {
                    var tess = c.Tessellate();
                    if (tess == null) continue;
                    // Skip each curve's last point — it repeats the next curve's first.
                    for (int i = 0; i < tess.Count - 1; i++)
                    {
                        XYZ w = t.OfPoint(tess[i]);
                        pts.Add(new XYZ(w.X, w.Y, 0));
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ZonePolygonOps: tessellate loop", ex);
                return new List<XYZ>();
            }
            return Dedupe(pts);
        }

        /// <summary>Planar area by the shoelace formula. Used to rank loops and drop slivers.</summary>
        public static double AreaFt2(IReadOnlyList<XYZ>? poly)
        {
            if (poly == null || poly.Count < 3) return 0;
            double sum = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                sum += (a.X * b.Y) - (b.X * a.Y);
            }
            return Math.Abs(sum) / 2.0;
        }

        /// <summary>Axis-aligned rectangle as a polygon, counter-clockwise.</summary>
        public static List<XYZ> Rect(double minX, double minY, double maxX, double maxY)
            => new List<XYZ>
            {
                new XYZ(minX, minY, 0),
                new XYZ(maxX, minY, 0),
                new XYZ(maxX, maxY, 0),
                new XYZ(minX, maxY, 0),
            };

        // ── Solid plumbing ────────────────────────────────────────────────────

        /// <summary>
        /// Extrudes one polygon into a prism. Returns null (logged) when the profile is
        /// degenerate or self-intersecting — a real condition for a hand-modelled slab, and
        /// never a reason to lose the other polygons in the batch.
        /// </summary>
        public static Solid? Extrude(IReadOnlyList<XYZ>? poly)
        {
            var pts = Dedupe(poly);
            if (pts.Count < 3) return null;

            try
            {
                var loop = new CurveLoop();
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[i];
                    var b = pts[(i + 1) % pts.Count];
                    if (a.DistanceTo(b) < WeldToleranceFt) continue;
                    loop.Append(Line.CreateBound(a, b));
                }
                return GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop }, XYZ.BasisZ, ExtrusionDepthFt);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ZonePolygonOps: extrude polygon", ex);
                return null;
            }
        }

        /// <summary>
        /// The outer loop of every upward-facing planar face of a solid — one polygon per
        /// connected mass. Only the top matters: the bottom repeats it and the sides are the
        /// extrusion walls. Inner loops are dropped, so a result is always a filled outline.
        /// </summary>
        public static List<List<XYZ>> TopOutlines(Solid? solid)
        {
            var polys = new List<List<XYZ>>();
            if (solid == null) return polys;

            try
            {
                foreach (Face f in solid.Faces)
                {
                    if (!(f is PlanarFace pf)) continue;
                    if (pf.FaceNormal == null || pf.FaceNormal.Z < 0.9) continue;

                    var loops = f.GetEdgesAsCurveLoops();
                    if (loops == null || loops.Count == 0) continue;

                    CurveLoop? outer = null;
                    double best = -1;
                    foreach (var l in loops)
                    {
                        double a = AreaFt2(FlattenLoop(l));
                        if (a > best) { best = a; outer = l; }
                    }
                    if (outer == null) continue;

                    var pts = FlattenLoop(outer);
                    if (pts.Count >= 3 && AreaFt2(pts) >= MinResultAreaFt2) polys.Add(pts);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZonePolygonOps: read solid top faces", ex);
                return new List<List<XYZ>>();
            }
            return polys;
        }

        /// <summary>Folds a boolean across many solids, skipping (and counting) any that refuse.</summary>
        private static Solid? Fold(IEnumerable<Solid> solids, BooleanOperationsType op, out int failed)
        {
            failed = 0;
            Solid? acc = null;
            foreach (var s in solids)
            {
                if (s == null) continue;
                if (acc == null) { acc = s; continue; }
                try
                {
                    acc = BooleanOperationsUtils.ExecuteBooleanOperation(acc, s, op);
                }
                catch (Exception ex)
                {
                    // Keep what has merged so far — one refusing solid must not discard the rest.
                    failed++;
                    DiagnosticsLog.Swallowed($"ZonePolygonOps: {op} fold", ex);
                }
            }
            return acc;
        }

        // ── The operations callers actually want ──────────────────────────────

        /// <summary>
        /// Union of many polygons — one continuous boundary per connected mass, holes filled
        /// (inner loops never enter, and TopOutlines drops any that survive).
        /// Returns an empty list when nothing could be built; the caller reports that.
        /// </summary>
        public static List<List<XYZ>> Union(IEnumerable<IReadOnlyList<XYZ>> polys,
                                            Action<string, string>? log = null)
        {
            var solids = new List<Solid>();
            int badProfile = 0, total = 0;
            foreach (var p in polys)
            {
                total++;
                var s = Extrude(p);
                if (s == null) { badProfile++; continue; }
                solids.Add(s);
            }

            var merged = Fold(solids, BooleanOperationsType.Union, out int badUnion);

            if ((badProfile > 0 || badUnion > 0) && log != null)
                Say(log, $"Outline union: {solids.Count - badUnion} of {total} profile(s) merged " +
                         $"({badProfile} could not be extruded, {badUnion} could not be unioned).", "warn");

            return TopOutlines(merged);
        }

        /// <summary>
        /// Intersection of one subject polygon with a set of clip polygons (the clips are
        /// unioned first, so the subject is kept only where ANY clip covers it).
        ///
        /// This is what trims a scope box back to the building: the part of the box hanging
        /// off the edge simply is not in the union of the slab footprints. It can legitimately
        /// return SEVERAL polygons — a box spanning a courtyard comes back as two pieces — and
        /// that is the expected answer, not a failure.
        /// </summary>
        public static List<List<XYZ>> IntersectWith(IReadOnlyList<XYZ> subject,
                                                    IEnumerable<IReadOnlyList<XYZ>> clips,
                                                    Action<string, string>? log = null)
        {
            var subjectSolid = Extrude(subject);
            if (subjectSolid == null)
            {
                Say(log, "The area rectangle could not be turned into a shape, so it was left untrimmed.", "warn");
                return new List<List<XYZ>> { Dedupe(subject) };
            }

            var clipSolids = new List<Solid>();
            foreach (var c in clips)
            {
                var s = Extrude(c);
                if (s != null) clipSolids.Add(s);
            }
            if (clipSolids.Count == 0)
            {
                Say(log, "No building outline was available to trim against, so the area is untrimmed.", "warn");
                return new List<List<XYZ>> { Dedupe(subject) };
            }

            var clipSolid = Fold(clipSolids, BooleanOperationsType.Union, out _);
            if (clipSolid == null) return new List<List<XYZ>> { Dedupe(subject) };

            try
            {
                var cut = BooleanOperationsUtils.ExecuteBooleanOperation(
                    subjectSolid, clipSolid, BooleanOperationsType.Intersect);
                var result = TopOutlines(cut);

                // An empty intersection is a real answer — the area sits entirely off the
                // building — but it is indistinguishable from a failed boolean, so say so.
                if (result.Count == 0)
                    Say(log, "The area does not overlap the building outline at all, so nothing was highlighted.", "warn");
                return result;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZonePolygonOps: intersect area with outline", ex);
                Say(log, $"The area could not be trimmed to the building outline ({ex.Message}); " +
                         "it is drawn untrimmed.", "warn");
                return new List<List<XYZ>> { Dedupe(subject) };
            }
        }

        /// <summary>
        /// Cuts polygons back to the side of each matchline that contains <paramref name="keepPoint"/>.
        ///
        /// A matchline is an OPEN curve, so it cannot be intersected against directly. Each of
        /// its segments is instead treated as a dividing line: a large half-plane prism is built
        /// on the side AWAY from the keep point and subtracted. For a straight matchline that is
        /// exactly right, and for an L-shaped one whose corner encloses the keep point it is
        /// also exact (the region is the intersection of the two half-planes).
        ///
        /// KNOWN LIMIT: an L-shaped matchline whose keep point lies OUTSIDE the corner describes
        /// a union of half-planes, which no intersection of cuts can express — that case is
        /// over-cut. A segment whose infinite line does not actually cross the polygon's bounds
        /// is skipped entirely, which is what keeps a distant matchline from removing everything.
        /// </summary>
        public static List<List<XYZ>> TrimToMatchlineSide(IReadOnlyList<IReadOnlyList<XYZ>> polys,
                                                          IEnumerable<(XYZ A, XYZ B)> segments,
                                                          XYZ keepPoint,
                                                          Action<string, string>? log = null)
        {
            var current = polys.Select(p => (IReadOnlyList<XYZ>)Dedupe(p)).Where(p => p.Count >= 3).ToList();
            if (current.Count == 0) return new List<List<XYZ>>();

            int cuts = 0;
            foreach (var seg in segments)
            {
                if (seg.A == null || seg.B == null) continue;

                var dir = new XYZ(seg.B.X - seg.A.X, seg.B.Y - seg.A.Y, 0);
                double len = dir.GetLength();
                if (len < WeldToleranceFt) continue;
                dir = dir.Normalize();
                var nrm = new XYZ(-dir.Y, dir.X, 0);

                // Which side is the area on? Anything within the weld tolerance of the line is
                // ambiguous, and cutting on a coin-flip would be worse than not cutting.
                double side = (keepPoint.X - seg.A.X) * nrm.X + (keepPoint.Y - seg.A.Y) * nrm.Y;
                if (Math.Abs(side) < WeldToleranceFt) continue;
                double sign = side > 0 ? 1.0 : -1.0;

                if (!CrossesAny(current, seg.A, nrm)) continue;

                double reach = Reach(current);
                var half = new List<XYZ>
                {
                    Offset(seg.A, dir, -reach),
                    Offset(seg.A, dir,  reach),
                    Offset(Offset(seg.A, dir,  reach), nrm, -sign * reach),
                    Offset(Offset(seg.A, dir, -reach), nrm, -sign * reach),
                };

                var next = new List<IReadOnlyList<XYZ>>();
                foreach (var poly in current)
                {
                    var kept = Subtract(poly, half);
                    // A cut that removes a polygon entirely is legitimate (that piece was wholly
                    // on the far side) — the empty result is simply not carried forward.
                    foreach (var k in kept) next.Add(k);
                }
                current = next;
                cuts++;

                if (current.Count == 0) break;
            }

            if (cuts > 0)
                Say(log, $"Trimmed the highlight at {cuts} matchline segment(s) — {current.Count} piece(s) remain.", "info");

            return current.Select(p => p.ToList()).ToList();
        }

        /// <summary>One polygon minus another, as polygons.</summary>
        private static List<List<XYZ>> Subtract(IReadOnlyList<XYZ> subject, IReadOnlyList<XYZ> cutter)
        {
            var a = Extrude(subject);
            var b = Extrude(cutter);
            if (a == null) return new List<List<XYZ>>();
            if (b == null) return new List<List<XYZ>> { Dedupe(subject) };

            try
            {
                var cut = BooleanOperationsUtils.ExecuteBooleanOperation(
                    a, b, BooleanOperationsType.Difference);
                return TopOutlines(cut);
            }
            catch (Exception ex)
            {
                // A failed cut leaves the polygon whole rather than dropping it — an over-large
                // highlight is recoverable by hand, a missing one is not obvious at all.
                DiagnosticsLog.Swallowed("ZonePolygonOps: subtract half-plane", ex);
                return new List<List<XYZ>> { Dedupe(subject) };
            }
        }

        /// <summary>True when the infinite line through <paramref name="origin"/> with normal
        /// <paramref name="nrm"/> has points of any polygon on both sides.</summary>
        private static bool CrossesAny(IEnumerable<IReadOnlyList<XYZ>> polys, XYZ origin, XYZ nrm)
        {
            bool pos = false, neg = false;
            foreach (var poly in polys)
                foreach (var p in poly)
                {
                    double d = (p.X - origin.X) * nrm.X + (p.Y - origin.Y) * nrm.Y;
                    if (d >  WeldToleranceFt) pos = true;
                    if (d < -WeldToleranceFt) neg = true;
                    if (pos && neg) return true;
                }
            return false;
        }

        /// <summary>A length comfortably larger than the polygons, so a half-plane covers them.</summary>
        private static double Reach(IEnumerable<IReadOnlyList<XYZ>> polys)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var poly in polys)
                foreach (var p in poly)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                }
            if (minX > maxX) return 1000.0;
            double diag = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
            return Math.Max(diag * 4.0, 100.0);
        }

        private static XYZ Offset(XYZ p, XYZ dir, double d)
            => new XYZ(p.X + dir.X * d, p.Y + dir.Y * d, 0);

        private static void Say(Action<string, string>? log, string msg, string tone)
        {
            try { log?.Invoke(msg, tone); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ZonePolygonOps: log", ex); }
        }
    }
}
