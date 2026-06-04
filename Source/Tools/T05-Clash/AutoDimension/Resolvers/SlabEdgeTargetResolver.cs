using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Clash.AutoDimension.Resolvers
{
    /// <summary>
    /// SLAB_EDGE target resolver — the real complexity, quarantined here so the GRID path never
    /// depends on it. Measures an offset from the source run out to where the slab plate
    /// terminates: slab perimeter faces AND floor-opening / shaft boundary faces (the interior
    /// vertical side faces of the floor solid). Candidate faces are the VERTICAL side faces (normal
    /// lies in the view plane); top/bottom slab faces (normal along the view direction) are
    /// rejected. Host and linked floors are scanned.
    ///
    /// The expensive, axis- and clash-independent work — collecting floors, extracting geometry,
    /// projecting face normals/points, resolving link references — is done ONCE per view and cached
    /// on the resolver instance (the engine reuses one instance for every clash and both axes). Each
    /// <see cref="Resolve"/> call then only runs the cheap per-axis match + distance + score.
    /// </summary>
    public sealed class SlabEdgeTargetResolver : ITargetResolver
    {
        /// <summary>One vertical slab face, with everything axis-independent precomputed.</summary>
        private sealed class FaceCand
        {
            public Reference Ref = null!;
            public Core.Vec2 Normal2d;   // projected, normalized world face normal (in view plane)
            public Core.Vec2 Origin2d;   // projected world point on the face
            public Core.Vec2 SegA;       // 2D extent of the edge in plan (the face reads as a line segment)
            public Core.Vec2 SegB;
            public double Area;
            public string Key = "";
        }

        private sealed class Candidate
        {
            public double Score;
            public double ProjDist;       // |offset| along the measurement axis (the dimension length)
            public double RadialDist;     // straight-line distance from the source to the edge segment
            public double Delta;          // signed offset along the axis (sign distinguishes sides)
            public Reference Ref = null!;
            public Core.Vec2 TargetPoint;
            public string Key = "";
        }

        // Faces whose along-axis offset matches this closely resolve to the same edge location
        // (e.g. identical stacked-level slabs) and are collapsed, not treated as an ambiguity.
        private const double SameEdgeTolFt = 0.005;

        private List<FaceCand>? _cache;
        private ResolveContext? _cacheCtx;

        public ResolvedTarget Resolve(SourceLine source, ResolveContext ctx)
        {
            var faces = EnsureCache(ctx);

            Core.Vec2 axis = ctx.Axis;
            double axisCosTol = Math.Cos(ctx.Config.AxisToleranceDeg * Math.PI / 180.0);

            var candidates = new List<Candidate>();
            foreach (var f in faces)
            {
                // AXIS MATCH: face normal parallel to the measurement axis (per-axis, so per call).
                double axisDot = Math.Abs(f.Normal2d.Dot(axis));
                if (axisDot < axisCosTol) continue;

                double delta = (f.Origin2d - source.Anchor2d).Dot(axis);
                double projDist = Math.Abs(delta);
                if (projDist < 1e-4) continue; // coincident — not a meaningful offset

                // Radial (straight-line) distance to the edge segment, not just the along-axis
                // offset: an edge near on this axis but far off to the side reads as far away, so a
                // genuinely closer edge wins even when its along-axis offset is larger. Equals
                // projDist when the source sits within the edge's perpendicular span.
                double radialDist = DistanceToSegment(source.Anchor2d, f.SegA, f.SegB);

                // The cap bounds how FAR the edge is, not how long the dimension is — an edge that
                // is genuinely near is dimensioned however long that turns out to be.
                if (radialDist > ctx.Config.MaxDistanceFt) continue;

                double axisDevDeg = Math.Acos(Math.Min(1.0, axisDot)) * 180.0 / Math.PI;
                double score = radialDist
                             + ctx.Config.SlabAxisWeight * axisDevDeg
                             - ctx.Config.SlabLengthWeight * f.Area;

                candidates.Add(new Candidate
                {
                    Score       = score,
                    ProjDist    = projDist,
                    RadialDist  = radialDist,
                    Delta       = delta,
                    Ref         = f.Ref,
                    TargetPoint = source.Anchor2d + axis * delta,
                    Key         = f.Key,
                });
            }

            if (candidates.Count == 0)
                return ResolvedTarget.Fail(source.SourceKey, Core.TargetType.SlabEdge,
                    "no slab/opening termination face on the measurement axis within the distance cap");

            // Deterministic ordering: score, then stable key.
            candidates.Sort((a, b) =>
            {
                int c = a.Score.CompareTo(b.Score);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });

            // Collapse faces that resolve to the same edge location (identical stacked-level slabs,
            // coincident perimeters) — they would draw the same dimension, so they are not an
            // ambiguity. Distinct edges (different along-axis offset) survive for the guard below.
            var distinct = new List<Candidate>();
            foreach (var c in candidates)
                if (!distinct.Any(d => Math.Abs(d.Delta - c.Delta) < SameEdgeTolFt))
                    distinct.Add(c);

            var best = distinct[0];

            // AMBIGUITY: two genuinely different edges within threshold — don't guess which side.
            if (distinct.Count >= 2)
            {
                var second = distinct[1];
                if (Math.Abs(best.RadialDist - second.RadialDist) < ctx.Config.AmbiguityThresholdFt)
                {
                    return new ResolvedTarget
                    {
                        Success   = false,
                        Ambiguity = new Core.AmbiguousTarget
                        {
                            SourceKey  = source.SourceKey,
                            CandidateA = best.Key,
                            CandidateB = second.Key,
                            ScoreA     = best.Score,
                            ScoreB     = second.Score,
                        },
                    };
                }
            }

            return ResolvedTarget.Ok(best.Ref, best.TargetPoint, best.Key);
        }

        /// <summary>Builds the axis-independent candidate-face list once per view (per context).</summary>
        private List<FaceCand> EnsureCache(ResolveContext ctx)
        {
            if (_cache != null && ReferenceEquals(_cacheCtx, ctx)) return _cache;

            var list = new List<FaceCand>();
            var geomOpt = new Options
            {
                ComputeReferences        = true,
                IncludeNonVisibleObjects = true,
                DetailLevel              = ViewDetailLevel.Fine,
            };

            foreach (var sd in ctx.Sources)
            {
                if (sd.Link != null && !ctx.Config.IncludeLinks) continue;

                // When the user picked floor(s), only scan those — and only in the doc they belong to.
                HashSet<int>? allowedFloors = null;
                if (ctx.SlabScopes.Count > 0)
                {
                    ElementId sdLink = sd.Link?.Id ?? ElementId.InvalidElementId;
                    allowedFloors = new HashSet<int>(ctx.SlabScopes
                        .Where(s => s.LinkInstanceId == sdLink)
                        .Select(s => s.FloorId.IntegerValue));
                    if (allowedFloors.Count == 0) continue; // no picked floor lives in this source
                }

                List<Floor> floors;
                try
                {
                    var q = new FilteredElementCollector(sd.Doc)
                        .OfClass(typeof(Floor)).Cast<Floor>();
                    if (allowedFloors != null) q = q.Where(f => allowedFloors.Contains(f.Id.IntegerValue));
                    floors = q.OrderBy(f => f.Id.IntegerValue).ToList();
                }
                catch (Exception ex) { LemoineLog.Swallowed("SlabEdgeTargetResolver: collect floors", ex); continue; }

                foreach (var floor in floors)
                {
                    GeometryElement? ge = null;
                    try { ge = floor.get_Geometry(geomOpt); }
                    catch (Exception ex) { LemoineLog.Swallowed("SlabEdgeTargetResolver: floor geometry", ex); }
                    if (ge == null) continue;

                    int faceIndex = 0;
                    foreach (GeometryObject go in ge)
                    {
                        if (!(go is Solid solid) || solid.Faces.Size == 0) continue;
                        foreach (Face face in solid.Faces)
                        {
                            int thisIndex = faceIndex++;
                            if (!(face is PlanarFace pf)) continue;        // slab terminations are planar
                            if (pf.Area < 0.01) continue;                  // VALIDITY: drop slivers/stubs

                            XYZ wn = sd.Transform.OfVector(pf.FaceNormal).Normalize();
                            // VERTICAL face only: normal lies in the view plane (axis-independent).
                            if (!ctx.Projection.NormalInPlane(wn, ctx.Config.AxisToleranceDeg)) continue;

                            Reference? r = pf.Reference;
                            if (r == null) continue;
                            if (sd.Link != null)
                            {
                                r = LinkRefHelper.ToHostReference(sd.Link, r, ctx.ReportMissingLink);
                                if (r == null) continue;
                            }

                            Core.Vec2 origin2d = ctx.Projection.To2D(sd.Transform.OfPoint(pf.Origin));
                            ProjectSegment(pf, sd.Transform, ctx.Projection, origin2d, out Core.Vec2 segA, out Core.Vec2 segB);

                            list.Add(new FaceCand
                            {
                                Ref      = r,
                                Normal2d = ctx.Projection.Dir2D(wn).Normalized(),
                                Origin2d = origin2d,
                                SegA     = segA,
                                SegB     = segB,
                                Area     = pf.Area,
                                Key      = $"slab:{(sd.Link != null ? sd.Link.Id.IntegerValue + ":" : "")}{floor.Id.IntegerValue}:{thisIndex}",
                            });
                        }
                    }
                }
            }

            ctx.Log($"Target cache: {list.Count} slab side-face(s) scanned across {ctx.Sources.Count} document(s).", "info");
            _cache = list;
            _cacheCtx = ctx;
            return list;
        }

        /// <summary>
        /// A vertical face reads as a line segment in plan. Projects the face's vertices into 2D and
        /// returns its extreme endpoints along the in-plane tangent, so the matcher can measure the
        /// real distance to the edge rather than to a single arbitrary origin point. Falls back to a
        /// zero-length segment at <paramref name="origin2d"/> when the face can't be triangulated.
        /// </summary>
        private static void ProjectSegment(
            PlanarFace pf, Transform transform, ViewProjection projection, Core.Vec2 origin2d,
            out Core.Vec2 a, out Core.Vec2 b)
        {
            a = origin2d;
            b = origin2d;
            try
            {
                Mesh? mesh = pf.Triangulate();
                if (mesh == null || mesh.Vertices.Count == 0) return;

                // The face projects to a line; its direction is the in-plane normal turned 90°.
                Core.Vec2 tangent = projection.Dir2D(transform.OfVector(pf.FaceNormal)).Normalized().Perp();
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                foreach (XYZ v in mesh.Vertices)
                {
                    Core.Vec2 p = projection.To2D(transform.OfPoint(v));
                    double t = p.Dot(tangent);
                    if (t < lo) { lo = t; a = p; }
                    if (t > hi) { hi = t; b = p; }
                }
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("SlabEdgeTargetResolver: project face segment", ex);
            }
        }

        /// <summary>Shortest distance from a point to a finite segment (point when degenerate).</summary>
        private static double DistanceToSegment(Core.Vec2 p, Core.Vec2 a, Core.Vec2 b)
        {
            Core.Vec2 ab = b - a;
            double len2 = ab.Dot(ab);
            if (len2 < 1e-12) return (p - a).Length;
            double t = (p - a).Dot(ab) / len2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            return (p - (a + ab * t)).Length;
        }
    }
}
