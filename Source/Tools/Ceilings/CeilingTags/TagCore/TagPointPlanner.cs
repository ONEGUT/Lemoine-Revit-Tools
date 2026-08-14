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

        /// <summary>
        /// Cells the grid must span across a footprint's NARROW axis, which shrinks
        /// <see cref="CellSizeFt"/> for a small ceiling rather than letting it fall through the
        /// grid. The rasterizer samples cell CENTRES, so a footprint narrower than one cell can
        /// miss every sample and rasterize to nothing at all — which is how a small ceiling
        /// came out with no tag. Every ceiling is tagged regardless of size, so the grid follows
        /// the ceiling down instead of the ceiling being dropped.
        /// </summary>
        public int MinCellsAcross { get; set; } = 4;

        /// <summary>Floor on the shrunk cell size, in feet (1/8 in). A stop against a degenerate
        /// footprint demanding an absurd grid — not a size policy; anything that still fails to
        /// resolve is tagged at its footprint centre rather than skipped.</summary>
        public double MinCellSizeFt { get; set; } = 1.0 / 96.0;

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

        /// <summary>
        /// How much of a ring's enclosed hole must be covered by OTHER ceilings before the ring
        /// counts as looping rooms rather than a void.
        ///
        /// This is what separates a corridor that loops a block of rooms — which is tagged all
        /// the way around — from a ceiling with an atrium or shaft punched through it, which
        /// takes a single tag. A corridor rings ROOMS, and rooms have ceilings; an atrium rings
        /// a VOID, which has none at that level. Measured: corridor rings score 100% (and 71%
        /// when the inner rooms are only partly ceilinged), an atrium 0%, an atrium with a small
        /// canopy in it 5% — so anything from ~0.1 to ~0.6 separates them and 0.5 sits in the
        /// middle of that gap.
        ///
        /// Deliberately NOT a corridor-width cap: elongation alone cannot do this job (an atrium
        /// ring scores 6.0 and a shaft ring 5.4, both above the corridor threshold), and a fixed
        /// maximum width would misjudge a genuinely wide circulation spine. Deliberately NOT
        /// wall- or Room-based either: a corridor looping ONE large room has no walls inside its
        /// hole at all, while an atrium usually has walls right at its edge.
        /// </summary>
        public double RingFillFraction { get; set; } = 0.5;

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
    /// 2. <b>Rings a VOID</b> (an atrium or shaft punched through the ceiling — an enclosed hole
    ///    with no other ceiling inside it) → ONE tag at the TOP CENTRE of the band.
    ///    A ring that loops ROOMS is not this case: it is a corridor, and falls through to
    ///    rule 3 to be tagged all the way around. See
    ///    <see cref="TagPlanConfig.RingFillFraction"/>.
    /// 3. <b>Corridor</b> → ONE tag per corner-to-corner STRETCH, subdivided by
    ///    <see cref="TagPlanConfig.MaxTagSpacingFt"/>. A corner starts a new count, so each
    ///    arm of an L or U carries its own tag instead of being swallowed by a long
    ///    neighbouring arm.
    ///
    /// The room/corridor split is decided on the ceiling's OWN footprint, before occluders are
    /// subtracted, because a room with a soffit under it leaves a thin frame that is
    /// geometrically indistinguishable from a ring corridor.
    ///
    /// <b>Size never decides whether a ceiling is tagged.</b> There is no minimum area and no
    /// minimum island: every visible piece of every ceiling gets at least one tag, and a
    /// footprint finer than the grid is tagged at its own centre rather than skipped. Only two
    /// things stop a tag — no boundary geometry at all, and being wholly covered by a lower
    /// ceiling (which is what <see cref="TagPlanConfig.AccountForCovered"/> switches off).
    /// Crowding is handled where it belongs, by <see cref="TagPlanConfig.MinTagSeparationFt"/>
    /// collapsing tags that land on top of each other — never by dropping the ceiling.
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
                    // Finer than the grid this run could afford — there is nothing to measure,
                    // but it is still a real ceiling and still gets its tag. Placed at the
                    // footprint's own centre, which at a size below one cell is within a
                    // fraction of an inch of any point the raster could have chosen.
                    diag.BelowGridResolution = true;
                    plan.Placements.Add(new TagPlacement
                    {
                        RegionId = region.Id,
                        Point    = FootprintCentre(region, bb),
                    });
                    diag.TagCount = 1;
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
                //
                // Every island counts, however small. There is no minimum area: a ceiling that
                // survives occlusion as a narrow strip is still a ceiling carrying a height, and
                // dropping it left the user with silently untagged ceilings. Tags that end up
                // close together are collapsed by MinTagSeparationFt below, which is the right
                // control for crowding — an area threshold removed the whole ceiling instead.
                int[] labels = raster.LabelIslands(out int islandCount);

                var islands = new List<PolyRaster>();
                for (int label = 1; label <= islandCount; label++)
                    islands.Add(raster.IslandSubset(labels, label));
                diag.IslandCount = islands.Count;

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
                        // ── Rule 2 vs 3: does this ring loop ROOMS, or a VOID? ──
                        //
                        // Elongation cannot answer this — a hole thins any shape, so an atrium
                        // ring scores 6.0 and a shaft ring 5.4, both "corridor". What separates
                        // them is what is INSIDE the ring: a corridor loops rooms, and rooms
                        // have ceilings; an atrium or shaft loops nothing. Every ceiling in the
                        // view is already in `layer`, so this costs no extra model reads.
                        if (island.HasEnclosedHole()
                            && !EnclosesOtherRegions(island, layer, i, bounds, cfg))
                        {
                            // Rings a void → ONE tag at the top centre.
                            PlaceSingle(island, region, plan, forceTopCentre: true);
                        }
                        else
                        {
                            // Rings rooms (or is an open run) → corridor, tagged per stretch all
                            // the way around.
                            diag.WasCorridor = true;
                            if (island.HasEnclosedHole()) diag.WasRingCorridor = true;
                            PlaceAlongRun(island, region, cfg, plan);
                        }
                    }
                }

                diag.TagCount = CollapseNearDuplicates(plan.Placements, before, cfg.MinTagSeparationFt);
                if (diag.TagCount == 0 && diag.SkipReason == null)
                    diag.SkipReason = "no usable tag point found";
            }
        }

        /// <summary>
        /// True when this island's enclosed hole is substantially covered by OTHER regions on
        /// the layer — i.e. the ring loops something that has a ceiling, which makes it a
        /// corridor rather than a ceiling with a void punched through it.
        ///
        /// Depth is deliberately ignored (unlike occlusion, which only subtracts LOWER regions):
        /// the question is merely "is there a room in there", and a room's ceiling counts
        /// whether it sits above or below the ring.
        /// </summary>
        private static bool EnclosesOtherRegions(
            PolyRaster island, List<TaggableRegion> layer, int selfIndex,
            Bounds[] bounds, TagPlanConfig cfg)
        {
            List<int> hole = island.EnclosedCells();
            if (hole.Count == 0) return false;

            // Painted onto a clone of the island's own grid, so cell indices line up exactly.
            PolyRaster others = island.CloneEmpty();
            bool any = false;

            for (int j = 0; j < layer.Count; j++)
            {
                if (j == selfIndex) continue;
                if (!bounds[selfIndex].Intersects(bounds[j])) continue;   // cheap box reject

                var loops = new List<Loop2>();
                loops.AddRange(layer[j].Outers);
                // The other ceiling's fixture openings are filled in for the same reason they are
                // everywhere else: a recessed light does not mean there is no room there.
                foreach (Loop2 h in layer[j].Holes)
                    if (h.AbsArea >= cfg.MinHoleAreaFt2) loops.Add(h);

                others.FillLoops(loops, true);
                any = true;
            }

            if (!any) return false;

            int covered = 0;
            foreach (int idx in hole) if (others.IsFilled(idx)) covered++;

            return covered >= hole.Count * cfg.RingFillFraction;
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

        /// <summary>
        /// Centre of a region's footprint, straight from its boundary loops — the tag point for
        /// a ceiling too fine for the raster to hold. The area-weighted (shoelace) centroid of
        /// the largest outer loop, falling back to the bounding-box centre for a loop with no
        /// area at all. No raster is involved, so this works at any size.
        /// </summary>
        private static Pt2 FootprintCentre(TaggableRegion region, Bounds bb)
        {
            Loop2? biggest = null;
            double bestArea = -1.0;
            foreach (Loop2 loop in region.Outers)
            {
                double a = loop.AbsArea;
                if (a > bestArea) { bestArea = a; biggest = loop; }
            }

            if (biggest != null && biggest.Points.Count >= 3)
            {
                double cross2 = 0, cx = 0, cy = 0;
                var pts = biggest.Points;
                for (int i = 0; i < pts.Count; i++)
                {
                    Pt2 p = pts[i], q = pts[(i + 1) % pts.Count];
                    double cross = p.X * q.Y - q.X * p.Y;
                    cross2 += cross;
                    cx     += (p.X + q.X) * cross;
                    cy     += (p.Y + q.Y) * cross;
                }
                // A sliver whose signed area rounds to zero has no meaningful centroid — the
                // bounding-box centre below is the honest answer for it.
                if (Math.Abs(cross2) > 1e-12)
                    return new Pt2(cx / (3.0 * cross2), cy / (3.0 * cross2));
            }

            return new Pt2((bb.MinX + bb.MaxX) * 0.5, (bb.MinY + bb.MaxY) * 0.5);
        }

        /// <summary>
        /// Picks the grid resolution for one footprint: shrinks the cell so a small ceiling
        /// still lands on the grid (<see cref="TagPlanConfig.MinCellsAcross"/>), then grows it
        /// back when the footprint would otherwise blow past
        /// <see cref="TagPlanConfig.MaxCells"/>.
        /// </summary>
        private static double ChooseCellSize(Bounds bb, TagPlanConfig cfg)
        {
            double cell = cfg.CellSizeFt > 0 ? cfg.CellSizeFt : 0.5;
            double w = Math.Max(1e-6, bb.MaxX - bb.MinX);
            double h = Math.Max(1e-6, bb.MaxY - bb.MinY);

            // A small ceiling gets a small cell rather than being lost between samples. The
            // grid is tiny either way, so this costs nothing: a 2 ft × 8 in ceiling comes out
            // 12 × 39 cells at a 2 in cell.
            int across = cfg.MinCellsAcross > 0 ? cfg.MinCellsAcross : 1;
            double narrow = Math.Min(w, h);
            if (narrow / cell < across) cell = narrow / across;

            double floor = cfg.MinCellSizeFt > 0 ? cfg.MinCellSizeFt : 1.0 / 96.0;
            if (cell < floor) cell = floor;

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
