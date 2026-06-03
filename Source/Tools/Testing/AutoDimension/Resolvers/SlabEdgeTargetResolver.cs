using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.AutoDimension.Resolvers
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
            public double Area;
            public string Key = "";
        }

        private sealed class Candidate
        {
            public double Score;
            public double ProjDist;
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
                if (projDist > ctx.Config.MaxDistanceFt) continue;
                if (projDist < 1e-4) continue; // coincident — not a meaningful offset

                double axisDevDeg = Math.Acos(Math.Min(1.0, axisDot)) * 180.0 / Math.PI;
                double score = projDist
                             + ctx.Config.SlabAxisWeight * axisDevDeg
                             - ctx.Config.SlabLengthWeight * f.Area;

                candidates.Add(new Candidate
                {
                    Score       = score,
                    ProjDist    = projDist,
                    Delta       = delta,
                    Ref         = f.Ref,
                    TargetPoint = source.Anchor2d + axis * delta,
                    Key         = f.Key,
                });
            }

            if (candidates.Count == 0)
                return ResolvedTarget.Fail(source.SourceKey, Core.TargetType.SlabEdge,
                    "no slab/opening termination face within distance cap on the measurement axis");

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
                if (Math.Abs(best.ProjDist - second.ProjDist) < ctx.Config.AmbiguityThresholdFt)
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

                            list.Add(new FaceCand
                            {
                                Ref      = r,
                                Normal2d = ctx.Projection.Dir2D(wn).Normalized(),
                                Origin2d = ctx.Projection.To2D(sd.Transform.OfPoint(pf.Origin)),
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
    }
}
