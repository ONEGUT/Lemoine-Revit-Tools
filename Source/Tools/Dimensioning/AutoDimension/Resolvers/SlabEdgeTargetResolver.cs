using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Dimensioning.AutoDimension.Resolvers
{
    /// <summary>
    /// SLAB_EDGE target resolver — the real complexity, quarantined here so the GRID path never
    /// depends on it. Measures an offset from the source run out to where the slab plate
    /// terminates: slab perimeter faces AND floor-opening / shaft boundary faces (the interior
    /// vertical side faces of the floor solid). Candidate faces are the VERTICAL side faces (normal
    /// lies in the view plane); top/bottom slab faces (normal along the view direction) are
    /// rejected. Host and linked floors are scanned.
    ///
    /// When a source marker carries the exact Group 2 element it clashed (stamped at marking), the
    /// resolver takes a TARGETED path first: it dimensions to that one element's nearest edge — exact,
    /// no scan, and generalises to walls/beams (anything in Group 2). The whole-view scan below is the
    /// fallback for markers without a stamped target (legacy runs) or when the user forces a floor.
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
            public string Src = "";      // diagnostic label: "host" or "link <id>"
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
            public string Src = "";       // diagnostic label, copied from the winning face
            public string Key = "";
        }

        // Faces whose along-axis offset matches this closely resolve to the same edge location
        // (e.g. identical stacked-level slabs) and are collapsed, not treated as an ambiguity.
        private const double SameEdgeTolFt = 0.005;

        private List<FaceCand>? _cache;
        private ResolveContext? _cacheCtx;

        // Per-element face cache for the targeted path, keyed "<linkId>:<elemId>" — many penetrations
        // through one slab share the same Group 2 element, so its faces are extracted only once.
        // A null value means the target was UNRESOLVABLE (link unloaded / element gone) → scan fallback
        // is allowed; a (possibly empty) list means it resolved and the targeted result is trusted.
        private readonly Dictionary<string, List<FaceCand>?> _elemCache = new Dictionary<string, List<FaceCand>?>();

        public ResolvedTarget Resolve(SourceLine source, ResolveContext ctx)
        {
            Core.Vec2 axis = ctx.Axis;

            // ── Per-clash targeted path ──────────────────────────────────────────
            // When the marker carries the exact Group 2 element it clashed (and the user hasn't forced
            // a specific floor), dimension straight to THAT element's nearest edge. Exact, no scan, and
            // it generalises beyond slabs (walls, beams — anything in Group 2) for free.
            if (ctx.SlabScopes.Count == 0 && source.TargetElementId != ElementId.InvalidElementId)
            {
                var elemFaces = EnsureElementCache(source, ctx);
                // Resolved (non-null) → trust the targeted result exactly, never substitute an unrelated
                // floor. Null → the element is gone/unloaded (already reported) → fall through to scan.
                if (elemFaces != null)
                {
                    var targetedResult = ResolveAgainstFaces(source, ctx, axis, elemFaces, targeted: true);
                    // Crop-bounded views (dense callouts): the stamped element's edge can sit OUTSIDE
                    // the visible crop — fall back to scanning for the nearest edge actually shown.
                    if (targetedResult.Success || !ctx.TargetBounds.HasValue)
                        return targetedResult;
                }
            }

            return ResolveAgainstFaces(source, ctx, axis, EnsureCache(ctx), targeted: false);
        }

        /// <summary>Runs the per-axis match + distance + score + ambiguity guard over a candidate-face
        /// list (the per-element set for the targeted path, or the whole-view cache for the scan).</summary>
        private ResolvedTarget ResolveAgainstFaces(
            SourceLine source, ResolveContext ctx, Core.Vec2 axis, List<FaceCand> faces, bool targeted)
        {
            double axisCosTol = Math.Cos(ctx.Config.AxisToleranceDeg * Math.PI / 180.0);
            string mode = targeted ? "target" : "scan";

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

                // Scan mode caps distance to reject unrelated floors; the targeted path trusts the
                // explicitly-clashed element, so its edge is measured however far away it is (a large
                // slab plate can have its nearest edge well beyond the scan cap).
                if (!targeted && radialDist > ctx.Config.MaxDistanceFt) continue;

                // Crop-bounded views (dense callouts): only edges whose dimension landing point is
                // visible in the crop are eligible — the dimension must stay inside the callout.
                if (ctx.TargetBounds.HasValue
                    && !ctx.TargetBounds.Value.Contains(source.Anchor2d + axis * delta)) continue;

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
                    Src         = f.Src,
                    Key         = f.Key,
                });
            }

            if (candidates.Count == 0)
            {
                if (ctx.Config.DiagnoseSlabEdge)
                    ctx.Log($"  slab-diag resolve {source.SourceKey} axis {AxisName(axis)} [{mode}]: "
                          + $"0 candidate(s) from {faces.Count} face(s) (none axis-aligned within tol AND within {ctx.Config.MaxDistanceFt:0.#} ft).", "info");
                return ResolvedTarget.Fail(source.SourceKey, Core.TargetType.SlabEdge,
                    "no slab/opening termination face on the measurement axis within the distance cap");
            }

            // Order by straight-line distance to the edge directly (nearest wins), then a stable key.
            // The composite score's axis-deviation/area terms could otherwise rank a slightly farther
            // edge ahead of the nearest, and the ambiguity guard below already compares RadialDist.
            candidates.Sort((a, b) =>
            {
                int c = a.RadialDist.CompareTo(b.RadialDist);
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

            if (ctx.Config.DiagnoseSlabEdge)
            {
                string top = string.Join("  |  ", distinct.Take(5).Select(c =>
                    $"[{c.Src}] radial {c.RadialDist:0.##} ft, delta {c.Delta:+0.##;-0.##} ft, score {c.Score:0.###} ({c.Key})"));
                ctx.Log($"  slab-diag resolve {source.SourceKey} axis {AxisName(axis)} [{mode}]: "
                      + $"{candidates.Count} candidate(s), {distinct.Count} distinct edge(s). "
                      + $"Winner [{best.Src}] {best.Key}. Top: {top}", "info");
            }

            // AMBIGUITY: two genuinely different edges within threshold — don't guess which side.
            // Scan mode only: across many elements, equidistant edges are genuinely unclear. In the
            // targeted path it's ONE known element, so equidistant faces give the SAME dimension length
            // — picking the nearest (deterministic tie-break above) is correct, not a guess.
            if (!targeted && distinct.Count >= 2)
            {
                var second = distinct[1];
                if (Math.Abs(best.RadialDist - second.RadialDist) < ctx.Config.AmbiguityThresholdFt)
                {
                    if (ctx.Config.DiagnoseSlabEdge)
                        ctx.Log($"  slab-diag resolve {source.SourceKey} axis {AxisName(axis)} [{mode}]: AMBIGUOUS — "
                              + $"[{best.Src}] {best.Key} (radial {best.RadialDist:0.##}) vs [{second.Src}] {second.Key} "
                              + $"(radial {second.RadialDist:0.##}), within {ctx.Config.AmbiguityThresholdFt:0.###} ft — not placed.", "info");
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

            int hostKept = 0, linkKept = 0;   // diagnostic: where the surviving faces came from
            foreach (var sd in ctx.Sources)
            {
                if (sd.Link != null && !ctx.Config.IncludeLinks) continue;
                string srcLabel = sd.Link != null ? $"link {sd.Link.Id.Value}" : "host";

                // When the user picked floor(s), only scan those — and only in the doc they belong to.
                HashSet<long>? allowedFloors = null;
                if (ctx.SlabScopes.Count > 0)
                {
                    ElementId sdLink = sd.Link?.Id ?? ElementId.InvalidElementId;
                    allowedFloors = new HashSet<long>(ctx.SlabScopes
                        .Where(s => s.LinkInstanceId == sdLink)
                        .Select(s => s.FloorId.Value));
                    if (allowedFloors.Count == 0) continue; // no picked floor lives in this source
                }

                List<Floor> floors;
                try
                {
                    var q = new FilteredElementCollector(sd.Doc)
                        .OfClass(typeof(Floor)).Cast<Floor>();
                    if (allowedFloors != null) q = q.Where(f => allowedFloors.Contains(f.Id.Value));
                    floors = q.OrderBy(f => f.Id.Value).ToList();
                }
                catch (Exception ex) { LemoineLog.Swallowed("SlabEdgeTargetResolver: collect floors", ex); continue; }

                // Per-source diagnostic tallies — isolate WHERE linked faces are lost (collection,
                // verticality, null reference, or link-reference conversion).
                var stats = new FaceStats();
                foreach (var floor in floors)
                {
                    GeometryElement? ge = null;
                    try { ge = floor.get_Geometry(geomOpt); }
                    catch (Exception ex) { LemoineLog.Swallowed("SlabEdgeTargetResolver: floor geometry", ex); }
                    if (ge == null) continue;
                    CollectVerticalFaces(ge, sd.Transform, sd.Link, ctx, floor.Id.Value, srcLabel, "slab:", list, stats);
                }

                if (sd.Link != null) linkKept += stats.Kept; else hostKept += stats.Kept;
                if (ctx.Config.DiagnoseSlabEdge)
                    ctx.Log($"  slab-diag [{srcLabel}]: {floors.Count} floor(s) → {stats.Planar} planar face(s), "
                          + $"{stats.Vertical} vertical, kept {stats.Kept} (dropped {stats.NullRef} null-ref, {stats.ConvFail} link-conv-fail).", "info");
            }

            ctx.Log($"Target cache: {list.Count} slab side-face(s) scanned across {ctx.Sources.Count} document(s) "
                  + $"— host {hostKept}, linked {linkKept}.", "info");
            _cache = list;
            _cacheCtx = ctx;
            return list;
        }

        /// <summary>
        /// Builds the candidate-face list for ONE clashed Group 2 element (host or linked), cached so
        /// repeated penetrations of the same slab extract its geometry only once. Resolves the link by
        /// id even when "include links" is off — the user explicitly clashed it.
        /// </summary>
        private List<FaceCand>? EnsureElementCache(SourceLine source, ResolveContext ctx)
        {
            string key = $"{source.TargetLinkInstanceId.Value}:{source.TargetElementId.Value}";
            if (_elemCache.TryGetValue(key, out var cached)) return cached;

            var list = new List<FaceCand>();

            Document doc;
            Transform xform;
            RevitLinkInstance? link = null;
            if (source.TargetLinkInstanceId != ElementId.InvalidElementId)
            {
                link = ctx.HostDoc.GetElement(source.TargetLinkInstanceId) as RevitLinkInstance;
                Document? ld = link?.GetLinkDocument();
                if (link == null || ld == null)
                {
                    // Surfaced, not silent: the clashed element's link is unloaded since marking, so we
                    // can't target it — null lets the scan fallback run, and the user sees this report.
                    ctx.ReportMissingLink($"clash target link {source.TargetLinkInstanceId.Value} "
                        + $"not loaded for source {source.SourceKey} — fell back to nearest-edge scan.");
                    _elemCache[key] = null;
                    return null;
                }
                doc = ld;
                xform = link.GetTotalTransform();
            }
            else
            {
                doc = ctx.HostDoc;
                xform = Transform.Identity;
            }

            Element? elem = null;
            try { elem = doc.GetElement(source.TargetElementId); }
            catch (Exception ex) { LemoineLog.Swallowed("SlabEdgeTargetResolver: get target element", ex); }
            if (elem == null)
            {
                // Clashed element deleted since marking — surface the fallback rather than swap silently.
                ctx.ReportMissingLink($"clash target element {source.TargetElementId.Value} not found "
                    + $"for source {source.SourceKey} — fell back to nearest-edge scan.");
                _elemCache[key] = null;
                return null;
            }

            string srcLabel = link != null ? $"target link {link.Id.Value}" : "target host";
            var geomOpt = new Options
            {
                ComputeReferences        = true,
                IncludeNonVisibleObjects = true,
                DetailLevel              = ViewDetailLevel.Fine,
            };
            GeometryElement? ge = null;
            try { ge = elem.get_Geometry(geomOpt); }
            catch (Exception ex) { LemoineLog.Swallowed("SlabEdgeTargetResolver: target element geometry", ex); }

            var stats = new FaceStats();
            if (ge != null)
                CollectVerticalFaces(ge, xform, link, ctx, source.TargetElementId.Value, srcLabel, "el:", list, stats);

            if (ctx.Config.DiagnoseSlabEdge)
                ctx.Log($"  slab-diag [{srcLabel}]: element {source.TargetElementId.Value} → "
                      + $"{stats.Planar} planar face(s), {stats.Vertical} vertical, kept {stats.Kept} "
                      + $"(dropped {stats.NullRef} null-ref, {stats.ConvFail} link-conv-fail).", "info");

            // No usable edge on the clashed element at all (e.g. a round column or face-less family in
            // Group 2) → null lets the nearest-edge scan fall back rather than hard-failing the clash.
            // A non-empty list is trusted exactly (never substituted with an unrelated floor).
            var resolved = list.Count > 0 ? list : null;
            _elemCache[key] = resolved;
            return resolved;
        }

        /// <summary>
        /// Extracts the VERTICAL planar side faces (normal in the view plane) from a geometry element,
        /// recursing into <see cref="GeometryInstance"/> so family-based targets (beams, etc.) work,
        /// not just direct-solid floors/walls. Transforms each face by <paramref name="xform"/>,
        /// promotes linked references to host references, and appends a <see cref="FaceCand"/> per kept
        /// face. <paramref name="stats"/> tallies why faces were dropped (for the diagnostic log).
        /// </summary>
        private void CollectVerticalFaces(
            GeometryElement ge, Transform xform, RevitLinkInstance? link, ResolveContext ctx,
            long elemIdInt, string srcLabel, string keyPrefix, List<FaceCand> list, FaceStats stats)
        {
            foreach (GeometryObject go in ge)
            {
                if (go is GeometryInstance gi)
                {
                    GeometryElement? inner = null;
                    try { inner = gi.GetInstanceGeometry(); }
                    catch (Exception ex) { LemoineLog.Swallowed("SlabEdgeTargetResolver: instance geometry", ex); }
                    if (inner != null)
                        CollectVerticalFaces(inner, xform, link, ctx, elemIdInt, srcLabel, keyPrefix, list, stats);
                    continue;
                }
                if (!(go is Solid solid) || solid.Faces.Size == 0) continue;
                foreach (Face face in solid.Faces)
                {
                    if (!(face is PlanarFace pf)) continue;        // edge terminations are planar
                    if (pf.Area < 0.01) continue;                  // VALIDITY: drop slivers/stubs
                    stats.Planar++;

                    XYZ wn = xform.OfVector(pf.FaceNormal).Normalize();
                    // VERTICAL face only: normal lies in the view plane (axis-independent).
                    if (!ctx.Projection.NormalInPlane(wn, ctx.Config.AxisToleranceDeg)) continue;
                    stats.Vertical++;

                    Reference? r = pf.Reference;
                    if (r == null) { stats.NullRef++; continue; }
                    if (link != null)
                    {
                        r = LinkRefHelper.ToHostReference(link, r, ctx.ReportMissingLink);
                        if (r == null) { stats.ConvFail++; continue; }
                    }

                    Core.Vec2 origin2d = ctx.Projection.To2D(xform.OfPoint(pf.Origin));
                    ProjectSegment(pf, xform, ctx.Projection, origin2d, out Core.Vec2 segA, out Core.Vec2 segB);

                    list.Add(new FaceCand
                    {
                        Ref      = r,
                        Normal2d = ctx.Projection.Dir2D(wn).Normalized(),
                        Origin2d = origin2d,
                        SegA     = segA,
                        SegB     = segB,
                        Area     = pf.Area,
                        Src      = srcLabel,
                        Key      = $"{keyPrefix}{(link != null ? link.Id.Value + ":" : "")}{elemIdInt}:{list.Count}",
                    });
                    stats.Kept++;
                }
            }
        }

        /// <summary>Drop tallies for one face-extraction pass — drives the diagnostic log only.</summary>
        private sealed class FaceStats
        {
            public int Planar, Vertical, NullRef, ConvFail, Kept;
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

        /// <summary>Diagnostic label for the measurement axis (view-2D x or y).</summary>
        private static string AxisName(Core.Vec2 axis) => Math.Abs(axis.X) >= Math.Abs(axis.Y) ? "x" : "y";

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
