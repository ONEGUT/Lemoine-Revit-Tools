using System;
using System.Collections.Generic;

namespace LemoineTools.Tools.Ceilings.CeilingTags.TagCore
{
    /// <summary>Tunables for a planning run. Only <see cref="MaxTagSpacingFt"/> is user-facing.</summary>
    public sealed class TagPlanConfig
    {
        /// <summary>A corridor stretch longer than this gets evenly spaced extra tags, in feet.
        /// Spacing is measured WITHIN one corner-to-corner stretch — see
        /// <see cref="TagPointPlanner"/> rule 3.</summary>
        public double MaxTagSpacingFt { get; set; } = 30.0;

        /// <summary>Subtract lower regions on the same layer from each region's footprint.</summary>
        public bool AccountForCovered { get; set; } = true;

        /// <summary>Raster resolution. 6 in bounds any tag's centring error at ~3 in.</summary>
        public double CellSizeFt { get; set; } = 0.5;

        /// <summary>Upper bound on cells per region; the cell size grows for a huge footprint
        /// rather than allocating an unbounded grid inside a Revit run.</summary>
        public int MaxCells { get; set; } = 400_000;

        /// <summary>A visible island smaller than this is not worth a tag.</summary>
        public double MinIslandAreaFt2 { get; set; } = 10.0;

        /// <summary>
        /// An opening in a ceiling smaller than this (square feet) is treated as solid.
        ///
        /// Recessed light fixtures, diffusers and sprinklers cut real holes in a ceiling's
        /// bottom face, and they are devastating to shape analysis if taken literally: a 40×30
        /// office with a 5×4 grid of 2×4 lights reports a 7.3 ft width instead of 30 ft (the
        /// biggest inscribed circle now fits only BETWEEN fixtures), which turns its elongation
        /// from 1.3 into 19.3 — every fixture-laden room reads as a corridor, and every corridor
        /// reads as a loop and collapses to one tag. 25 ft² clears any fixture (a 2×4 is 8 ft²)
        /// while keeping genuine architectural openings such as an atrium or shaft.
        /// </summary>
        public double MinHoleAreaFt2 { get; set; } = 25.0;

        /// <summary>
        /// area / width² above which a region is treated as a corridor rather than a room.
        /// Measured on the ceiling's OWN footprint (see <see cref="TagPointPlanner"/>), with
        /// fixture openings ignored — without that filter a light grid collapses the measured
        /// width and every office reads as a corridor.
        /// With honest widths: 14×12 office 1.2, 40×30 office 1.3, 60×25 room 2.4,
        /// 60×12 wide corridor 5.0, 90×6 corridor 15, L corridor 11.5. 4 separates cleanly.
        /// </summary>
        public double CorridorElongation { get; set; } = 4.0;

        /// <summary>Two tags on the same ceiling closer than this collapse to one, in feet.
        /// Deliberately an absolute distance, NOT a fraction of <see cref="MaxTagSpacingFt"/>:
        /// this exists to stop tags overlapping on the sheet, and scaling it with the spacing
        /// setting made a large spacing swallow tags that were correctly placed.</summary>
        public double MinTagSeparationFt { get; set; } = 6.0;
    }

    /// <summary>
    /// Turns <see cref="TaggableRegion"/>s into tag points. Pure geometry — no Revit types, so
    /// the whole placement policy is testable on plain fixtures and, at run time, executes
    /// without touching the model once the read phase has finished.
    ///
    /// Three rules, in order:
    ///
    /// 1. <b>Room</b> (own footprint not elongated) → exactly ONE tag, on its largest visible
    ///    island, at the centroid.
    /// 2. <b>Encloses a hole</b> (a thin band wrapping other ceilings, or any loop) → ONE tag
    ///    at the TOP CENTRE of the band. A ring is not tagged once per side.
    /// 3. <b>Corridor</b> → ONE tag per corner-to-corner STRETCH, subdivided by
    ///    <see cref="TagPlanConfig.MaxTagSpacingFt"/>. A corner starts a new count, so each
    ///    arm of an L or U carries its own tag instead of being swallowed by a long
    ///    neighbouring arm.
    ///
    /// The room/corridor split is decided on the ceiling's OWN footprint, before occluders are
    /// subtracted, because a room with a soffit under it leaves a thin frame that is
    /// geometrically indistinguishable from a ring corridor.
    /// </summary>
    public static class TagPointPlanner
    {
        /// <summary>Skip reason for a region wholly covered by lower regions. A const because
        /// the run log tests for it to decide whether to name the region individually — a bare
        /// literal on both sides would drift apart silently.</summary>
        public const string SkipFullyHidden = "fully hidden by lower ceilings";

        public static TagPlan Plan(IReadOnlyList<TaggableRegion> regions, TagPlanConfig cfg)
        {
            var plan = new TagPlan();
            if (regions == null || regions.Count == 0) return plan;

            // Group by layer so ceilings can only ever be occluded by ceilings.
            var byLayer = new Dictionary<int, List<TaggableRegion>>();
            foreach (var r in regions)
            {
                if (!byLayer.TryGetValue(r.OcclusionLayer, out var list))
                    byLayer[r.OcclusionLayer] = list = new List<TaggableRegion>();
                list.Add(r);
            }

            foreach (var layer in byLayer.Values)
                PlanLayer(layer, cfg, plan);

            return plan;
        }

        private static void PlanLayer(List<TaggableRegion> layer, TagPlanConfig cfg, TagPlan plan)
        {
            // Precompute each region's world bounds once — the occluder search below is the
            // only quadratic step, and a cheap box reject keeps it to near-neighbours.
            var bounds = new Bounds[layer.Count];
            for (int i = 0; i < layer.Count; i++) bounds[i] = Bounds.Of(layer[i]);

            for (int i = 0; i < layer.Count; i++)
            {
                TaggableRegion region = layer[i];
                var diag = new RegionDiagnostic { RegionId = region.Id, DisplayName = region.DisplayName };
                plan.Diagnostics.Add(diag);

                Bounds bb = bounds[i];
                if (!bb.Valid)
                {
                    diag.SkipReason = "no boundary geometry";
                    continue;
                }

                double cell = ChooseCellSize(bb, cfg);
                var raster = new PolyRaster(bb.MinX, bb.MinY, bb.MaxX, bb.MaxY, cell);

                // Fixture openings are filled in: a recessed light is not a change in the
                // ceiling's shape, but taken literally it shrinks the measured width to the gap
                // between fixtures and makes every office look like a corridor. Only openings
                // big enough to be architectural survive to shape the region.
                var allLoops = new List<Loop2>();
                allLoops.AddRange(region.Outers);
                int ignoredOpenings = 0;
                foreach (Loop2 hole in region.Holes)
                {
                    if (hole.AbsArea >= cfg.MinHoleAreaFt2) allLoops.Add(hole);
                    else ignoredOpenings++;
                }
                diag.IgnoredOpenings = ignoredOpenings;

                raster.FillLoops(allLoops, true);

                int ownCells = raster.Count;
                if (ownCells == 0)
                {
                    diag.SkipReason = "footprint too small to resolve";
                    continue;
                }

                // ── Classify corridor-vs-room on the ceiling's OWN footprint ──
                //
                // This MUST happen before occluders are subtracted. A room with a soffit or
                // bulkhead below it leaves a thin visible frame, and a frame is geometrically
                // indistinguishable from a ring corridor — measured after subtraction, a plain
                // 30×25 room with a 20×15 soffit scores an elongation of 12.5 and gets ringed
                // with edge tags. Its own footprint scores 1.2: it is a room, and it gets one.
                bool isCorridorShape = IsCorridorShaped(raster, cfg);

                // ── Occlusion: erase every LOWER region on this layer that overlaps ──
                if (cfg.AccountForCovered)
                {
                    for (int j = 0; j < layer.Count; j++)
                    {
                        if (j == i) continue;
                        TaggableRegion other = layer[j];
                        if (other.SortDepth >= region.SortDepth) continue;   // not below this one
                        if (!bb.Intersects(bounds[j])) continue;

                        // The occluder's own fixture openings are filled too — a light in the
                        // ceiling below does not let the ceiling above show through.
                        var occl = new List<Loop2>();
                        occl.AddRange(other.Outers);
                        foreach (Loop2 h in other.Holes)
                            if (h.AbsArea >= cfg.MinHoleAreaFt2) occl.Add(h);
                        raster.FillLoops(occl, false);
                    }
                }

                int visibleCells = raster.Count;
                diag.VisibleFraction = ownCells > 0 ? visibleCells / (double)ownCells : 0.0;

                if (visibleCells == 0)
                {
                    diag.SkipReason = SkipFullyHidden;
                    plan.FullyHiddenCount++;
                    continue;
                }

                // ── Islands: a region split by an occluder is several places, not one ──
                int[] labels = raster.LabelIslands(out int islandCount);

                var islands = new List<PolyRaster>();
                for (int label = 1; label <= islandCount; label++)
                {
                    PolyRaster island = raster.IslandSubset(labels, label);
                    if (island.AreaFt2 >= cfg.MinIslandAreaFt2) islands.Add(island);
                }
                diag.IslandCount = islands.Count;

                if (islands.Count == 0)
                {
                    diag.SkipReason = "visible area too small to tag";
                    continue;
                }

                int before = plan.Placements.Count;

                if (!isCorridorShape)
                {
                    // ── Rule 1: a room gets exactly ONE tag ──────────────────
                    // Not one per island: a room chopped in two by a bulkhead is still one
                    // ceiling carrying one height, and two tags on it read as a mistake.
                    PolyRaster biggest = islands[0];
                    for (int k = 1; k < islands.Count; k++)
                        if (islands[k].AreaFt2 > biggest.AreaFt2) biggest = islands[k];

                    PlaceSingle(biggest, region, plan);
                }
                else
                {
                    foreach (PolyRaster island in islands)
                    {
                        if (island.HasEnclosedHole())
                        {
                            // ── Rule 2: a loop gets ONE tag at the top centre ──
                            PlaceSingle(island, region, plan, forceTopCentre: true);
                        }
                        else
                        {
                            diag.WasCorridor = true;
                            PlaceAlongRun(island, region, cfg, plan);
                        }
                    }
                }

                diag.TagCount = CollapseNearDuplicates(plan.Placements, before, cfg.MinTagSeparationFt);
                if (diag.TagCount == 0 && diag.SkipReason == null)
                    diag.SkipReason = "no usable tag point found";
            }
        }

        /// <summary>True when a region's own shape is long-and-thin enough to be a corridor.
        /// Width is twice the largest clearance (the radius of the biggest inscribed circle),
        /// so elongation is area / width² — scale-free, and ~1 for any squarish shape.</summary>
        private static bool IsCorridorShaped(PolyRaster raster, TagPlanConfig cfg)
        {
            double[] clearance = raster.ClearanceField();
            double maxClear = 0;
            for (int k = 0; k < clearance.Length; k++) if (clearance[k] > maxClear) maxClear = clearance[k];

            double widthFt = Math.Max(raster.CellSize, 2.0 * maxClear * raster.CellSize);
            return raster.AreaFt2 / (widthFt * widthFt) >= cfg.CorridorElongation;
        }

        /// <summary>
        /// One tag for this island: at the centroid when the centroid actually lands on the
        /// ceiling, otherwise at the top centre. A ring, frame or L has its centroid in open
        /// air, and "nearest solid cell to the centroid" put the tag on whichever side happened
        /// to be closest — top centre is predictable and reads as deliberate.
        /// </summary>
        private static void PlaceSingle(PolyRaster island, TaggableRegion region, TagPlan plan,
                                        bool forceTopCentre = false)
        {
            double[] clearance = island.ClearanceField();
            double maxClear = 0;
            for (int k = 0; k < clearance.Length; k++) if (clearance[k] > maxClear) maxClear = clearance[k];
            double minClear = Math.Min(maxClear * 0.5, 1.0 / island.CellSize);

            Pt2? target = null;
            if (!forceTopCentre)
            {
                Pt2? c = island.Centroid();
                if (c != null && island.Contains(c.Value)) target = c;
            }
            if (target == null) target = island.TopCentre();
            if (target == null) return;

            // Snap to a real interior cell so the tag can never sit off the ceiling.
            Pt2? snapped = island.NearestInterior(target.Value, clearance, minClear);
            if (snapped == null) return;

            plan.Placements.Add(new TagPlacement { RegionId = region.Id, Point = snapped.Value });
        }

        /// <summary>
        /// One tag per corner-to-corner STRETCH, subdivided by the spacing setting: a leg of
        /// length L gets ceil(L / spacing) tags, each centred on its own share of the leg. A
        /// short leg therefore gets a single tag at its midpoint, and a 90 ft leg at 30 ft
        /// spacing gets three, at 1/6, 1/2 and 5/6 along it.
        ///
        /// A corner STARTS A NEW COUNT — that is the point of splitting the centreline at
        /// corners in the first place. Without it, one long arm of an L absorbs the whole
        /// spacing budget and the short arm can come out with no tag at all.
        ///
        /// This rule was briefly replaced by a continuous run that carried the accumulated
        /// distance across corners, because fixture openings were collapsing the measured
        /// width and manufacturing legs where there was no real corner (a 40×30 office scored
        /// an elongation of 19.3). <see cref="TagPlanConfig.MinHoleAreaFt2"/> fixed that cause,
        /// so the corner split now runs on honest geometry and is back.
        ///
        /// <see cref="CollapseNearDuplicates"/> is what stops the two legs that meet at a
        /// corner from putting two tags a few feet apart.
        /// </summary>
        private static void PlaceAlongRun(PolyRaster island, TaggableRegion region,
                                          TagPlanConfig cfg, TagPlan plan)
        {
            double[] clearance = island.ClearanceField();
            double maxClear = 0;
            for (int k = 0; k < clearance.Length; k++) if (clearance[k] > maxClear) maxClear = clearance[k];
            double widthFt  = Math.Max(island.CellSize, 2.0 * maxClear * island.CellSize);
            double minClear = Math.Min(maxClear * 0.5, 1.0 / island.CellSize);

            bool[] skel = RegionSkeleton.Thin(island);
            var legs = RegionSkeleton.ExtractLegs(RegionSkeleton.TraceBranches(island, skel), widthFt);

            double spacing = cfg.MaxTagSpacingFt > 1.0 ? cfg.MaxTagSpacingFt : 1.0;
            int placed = 0;

            foreach (List<Pt2> leg in legs)
            {
                double len = RegionSkeleton.PolylineLength(leg);
                if (len < 1e-6) continue;

                int n = (int)Math.Ceiling(len / spacing);
                if (n < 1) n = 1;

                for (int k = 0; k < n; k++)
                {
                    Pt2 raw = RegionSkeleton.PointAtLength(leg, len * (k + 0.5) / n);
                    Pt2? snapped = island.NearestInterior(raw, clearance, minClear);
                    if (snapped == null) continue;

                    plan.Placements.Add(new TagPlacement { RegionId = region.Id, Point = snapped.Value });
                    placed++;
                }
            }

            // Thinning yielded no usable leg, or every sample failed to snap onto a real cell.
            // A corridor that reaches here is still a real ceiling and still needs a tag.
            if (placed == 0) PlaceSingle(island, region, plan);
        }

        /// <summary>
        /// Greedily drops tags added since <paramref name="from"/> that sit within
        /// <paramref name="minSepFt"/> of one already kept, and returns how many survive.
        /// Applied per region, so two different ceilings may still carry tags close together —
        /// they show different values and both belong on the drawing.
        /// </summary>
        private static int CollapseNearDuplicates(List<TagPlacement> placements, int from, double minSepFt)
        {
            int added = placements.Count - from;
            if (minSepFt <= 0 || added <= 1) return added;

            double min2 = minSepFt * minSepFt;
            var kept = new List<TagPlacement>();

            for (int i = from; i < placements.Count; i++)
            {
                TagPlacement p = placements[i];
                bool clash = false;
                foreach (TagPlacement k in kept)
                {
                    double dx = p.Point.X - k.Point.X, dy = p.Point.Y - k.Point.Y;
                    if (dx * dx + dy * dy < min2) { clash = true; break; }
                }
                if (!clash) kept.Add(p);
            }

            placements.RemoveRange(from, placements.Count - from);
            placements.AddRange(kept);
            return kept.Count;
        }

        /// <summary>Grows the cell size when a footprint would otherwise blow past
        /// <see cref="TagPlanConfig.MaxCells"/>.</summary>
        private static double ChooseCellSize(Bounds bb, TagPlanConfig cfg)
        {
            double cell = cfg.CellSizeFt > 0 ? cfg.CellSizeFt : 0.5;
            double w = Math.Max(1e-6, bb.MaxX - bb.MinX);
            double h = Math.Max(1e-6, bb.MaxY - bb.MinY);

            // +3 per axis matches PolyRaster's padding, so the cap reflects the real allocation.
            while ((w / cell + 3) * (h / cell + 3) > cfg.MaxCells) cell *= 1.5;
            return cell;
        }

        private struct Bounds
        {
            public double MinX, MinY, MaxX, MaxY;
            public bool   Valid;

            public static Bounds Of(TaggableRegion r)
            {
                var b = new Bounds
                {
                    MinX = double.MaxValue, MinY = double.MaxValue,
                    MaxX = double.MinValue, MaxY = double.MinValue,
                    Valid = false,
                };
                foreach (Loop2 loop in r.Outers)
                foreach (Pt2 p in loop.Points)
                {
                    if (p.X < b.MinX) b.MinX = p.X;
                    if (p.Y < b.MinY) b.MinY = p.Y;
                    if (p.X > b.MaxX) b.MaxX = p.X;
                    if (p.Y > b.MaxY) b.MaxY = p.Y;
                    b.Valid = true;
                }
                return b;
            }

            public bool Intersects(Bounds o)
                => Valid && o.Valid && MinX <= o.MaxX && MaxX >= o.MinX && MinY <= o.MaxY && MaxY >= o.MinY;
        }
    }
}
