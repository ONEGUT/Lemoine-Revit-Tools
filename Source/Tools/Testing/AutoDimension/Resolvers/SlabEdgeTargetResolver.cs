using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.AutoDimension.Resolvers
{
    /// <summary>
    /// SLAB_EDGE target resolver — the real complexity, quarantined here so the GRID path never
    /// depends on it. Measures a horizontal offset from the source run out to where the slab
    /// plate terminates: slab perimeter faces AND floor-opening / shaft boundary faces (these are
    /// the interior vertical side faces of the floor solid). Candidate faces are the VERTICAL
    /// side faces (normal lies in the view plane, parallel to the measurement axis); top/bottom
    /// slab faces (normal along the view direction) are rejected. Host and linked floors are
    /// scanned. The measurement axis is a parameter (default horizontal) so a vertical-clearance
    /// variant is a config change, not a rewrite.
    /// </summary>
    public sealed class SlabEdgeTargetResolver : ITargetResolver
    {
        private sealed class Candidate
        {
            public double Score;
            public double ProjDist;
            public Reference Ref = null!;
            public Core.Vec2 TargetPoint;
            public string Key = "";       // stable identity (doc:elem:faceindex) for determinism + report
        }

        public ResolvedTarget Resolve(SourceLine source, ResolveContext ctx)
        {
            Core.Vec2 axis = ctx.Axis;
            double srcAxial = source.Anchor2d.Dot(axis);
            double axisCosTol = Math.Cos(ctx.Config.AxisToleranceDeg * Math.PI / 180.0);

            var candidates = new List<Candidate>();

            var geomOpt = new Options
            {
                ComputeReferences        = true,
                IncludeNonVisibleObjects = true,
                DetailLevel              = ViewDetailLevel.Fine,
            };

            foreach (var sd in ctx.Sources)
            {
                if (sd.Link != null && !ctx.Config.IncludeLinks) continue;

                List<Floor> floors;
                try
                {
                    floors = new FilteredElementCollector(sd.Doc)
                        .OfClass(typeof(Floor)).Cast<Floor>()
                        .OrderBy(f => f.Id.IntegerValue)
                        .ToList();
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

                            // World normal + a point on the face.
                            XYZ wn = sd.Transform.OfVector(pf.FaceNormal).Normalize();
                            XYZ wo = sd.Transform.OfPoint(pf.Origin);

                            // AXIS MATCH: normal must lie in the view plane (vertical face) and be
                            // parallel to the measurement axis. Rejects top/bottom slab faces.
                            if (!ctx.Projection.NormalInPlane(wn, ctx.Config.AxisToleranceDeg)) continue;
                            Core.Vec2 n2d = ctx.Projection.Dir2D(wn).Normalized();
                            double axisDot = Math.Abs(n2d.Dot(axis));
                            if (axisDot < axisCosTol) continue;

                            Core.Vec2 o2d = ctx.Projection.To2D(wo);
                            double delta = (o2d - source.Anchor2d).Dot(axis);
                            double projDist = Math.Abs(delta);

                            // DISTANCE CAP.
                            if (projDist > ctx.Config.MaxDistanceFt) continue;
                            if (projDist < 1e-4) continue; // coincident — not a meaningful offset

                            Reference? r = pf.Reference;
                            if (r == null) continue;
                            if (sd.Link != null)
                            {
                                r = LinkRefHelper.ToHostReference(sd.Link, r, ctx.ReportMissingLink);
                                if (r == null) continue;
                            }

                            double axisDevDeg = Math.Acos(Math.Min(1.0, axisDot)) * 180.0 / Math.PI;

                            // SCORE: nearest projected distance wins; small axis-deviation penalty;
                            // credit larger / more primary boundary faces (tiebreak).
                            double score = projDist
                                         + ctx.Config.SlabAxisWeight * axisDevDeg
                                         - ctx.Config.SlabLengthWeight * pf.Area;

                            candidates.Add(new Candidate
                            {
                                Score       = score,
                                ProjDist    = projDist,
                                Ref         = r,
                                TargetPoint = source.Anchor2d + axis * delta,
                                Key         = $"slab:{(sd.Link != null ? sd.Link.Id.IntegerValue + ":" : "")}{floor.Id.IntegerValue}:{thisIndex}",
                            });
                        }
                    }
                }
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

            var best = candidates[0];

            // AMBIGUITY: if the top two are within threshold, do not guess.
            if (candidates.Count >= 2)
            {
                var second = candidates[1];
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
    }
}
