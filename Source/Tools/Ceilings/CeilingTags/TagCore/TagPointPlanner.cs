using System;
using System.Collections.Generic;

namespace LemoineTools.Tools.Ceilings.CeilingTags.TagCore
{
    /// <summary>Tunables for a planning run. Only <see cref="MaxTagSpacingFt"/> is user-facing.</summary>
    public sealed class TagPlanConfig
    {
        /// <summary>A corridor stretch longer than this gets evenly spaced extra tags.</summary>
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

        /// <summary>area / width² above which a region is treated as a corridor rather than a
        /// room. Measured on the ceiling's OWN footprint (see <see cref="TagPointPlanner"/>).
        /// A square scores 1, a 60×20 room scores 3, a 90×6 corridor scores 15 and a ring
        /// corridor ~22 — so 4 sits in a wide gap and errs toward "room", which is the safer
        /// mistake: one tag too few is a nudge, one ring of tags too many is a mess.</summary>
        public double CorridorElongation { get; set; } = 4.0;

        /// <summary>Two tags on the same ceiling closer than this collapse to one, in feet.
        /// Deliberately an absolute distance, NOT a fraction of <see cref="MaxTagSpacingFt"/>:
        /// this exists to stop tags overlapping on the sheet, and scaling it with the spacing
        /// setting made a large spacing swallow a ring corridor's legitimate side tags.</summary>
        public double MinTagSeparationFt { get; set; } = 6.0;
    }

    /// <summary>
    /// Turns <see cref="TaggableRegion"/>s into tag points. Pure geometry — no Revit types, so
    /// the whole placement policy is testable on plain fixtures and, at run time, executes
    /// without touching the model once the read phase has finished.
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
                var allLoops = new List<Loop2>();
                allLoops.AddRange(region.Outers);
                allLoops.AddRange(region.Holes);
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
                // with four edge tags. Its own footprint scores 1.2: it is a room, and it gets
                // one tag. A corridor sketched AS a ring still scores ~22 here, so the genuine
                // case is untouched.
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

                        var occl = new List<Loop2>();
                        occl.AddRange(other.Outers);
                        occl.AddRange(other.Holes);
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
                diag.IslandCount = islandCount;

                // Plan into a per-region list so near-duplicates can be collapsed before the
                // placements are published — two legs meeting at a corner can otherwise put
                // two tags a couple of feet apart, which reads as a smudge on the sheet.
                int before = plan.Placements.Count;
                for (int label = 1; label <= islandCount; label++)
                {
                    PolyRaster island = raster.IslandSubset(labels, label);
                    if (island.AreaFt2 < cfg.MinIslandAreaFt2) continue;

                    PlanIsland(island, region, cfg, plan, diag, isCorridorShape);
                }
                int emitted = CollapseNearDuplicates(plan.Placements, before, cfg.MinTagSeparationFt);

                diag.TagCount = emitted;
                if (emitted == 0 && diag.SkipReason == null)
                    diag.SkipReason = "visible area too small to tag";
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

        /// <summary>Plans one connected visible island. Returns how many tags it produced.
        /// <paramref name="isCorridorShape"/> is the verdict from the region's OWN footprint —
        /// this method must not re-derive it from the island, which may be a post-occlusion
        /// sliver of a perfectly ordinary room.</summary>
        private static int PlanIsland(PolyRaster island, TaggableRegion region,
                                      TagPlanConfig cfg, TagPlan plan, RegionDiagnostic diag,
                                      bool isCorridorShape)
        {
            double[] clearance = island.ClearanceField();

            double maxClear = 0;
            for (int k = 0; k < clearance.Length; k++) if (clearance[k] > maxClear) maxClear = clearance[k];

            double widthFt = Math.Max(island.CellSize, 2.0 * maxClear * island.CellSize);

            // Keep tags off the very edge where the shape allows it, but never demand more
            // clearance than the shape actually has.
            double minClear = Math.Min(maxClear * 0.5, 1.0 / island.CellSize);

            // ── Room: one tag at the centroid ────────────────────────────────
            if (!isCorridorShape)
            {
                Pt2? c = island.Centroid();
                if (c == null) return 0;

                // The centroid of an L or a ring can land in the hole — snapping to a real
                // interior cell is what guarantees the tag sits ON the ceiling.
                Pt2? snapped = island.NearestInterior(c.Value, clearance, minClear);
                if (snapped == null) return 0;

                plan.Placements.Add(new TagPlacement
                {
                    RegionId = region.Id, Point = snapped.Value, StretchIndex = 0,
                });
                return 1;
            }

            // ── Corridor: one tag per stretch, subdivided by the spacing setting ──
            diag.WasCorridor = true;

            bool[] skel = RegionSkeleton.Thin(island);
            var branches = RegionSkeleton.TraceBranches(island, skel);
            var legs = RegionSkeleton.ExtractLegs(branches, widthFt);

            if (legs.Count == 0)
            {
                // Thinning produced nothing usable — fall back to a centroid tag rather than
                // leaving a real ceiling untagged.
                Pt2? c = island.Centroid();
                if (c == null) return 0;
                Pt2? snapped = island.NearestInterior(c.Value, clearance, minClear);
                if (snapped == null) return 0;

                plan.Placements.Add(new TagPlacement { RegionId = region.Id, Point = snapped.Value });
                return 1;
            }

            double spacing = cfg.MaxTagSpacingFt > 1.0 ? cfg.MaxTagSpacingFt : 1.0;
            int count = 0;

            for (int li = 0; li < legs.Count; li++)
            {
                List<Pt2> leg = legs[li];
                double len = RegionSkeleton.PolylineLength(leg);
                if (len < 1e-6) continue;

                // n evenly spaced tags, each at the centre of its own share of the run. One tag
                // for a short leg; a 90 ft leg at 30 ft spacing gets 3, at 1/6, 1/2, 5/6.
                int n = (int)Math.Ceiling(len / spacing);
                if (n < 1) n = 1;

                for (int k = 0; k < n; k++)
                {
                    double at = len * (k + 0.5) / n;
                    Pt2 raw = RegionSkeleton.PointAtLength(leg, at);

                    Pt2? snapped = island.NearestInterior(raw, clearance, minClear);
                    if (snapped == null) continue;

                    plan.Placements.Add(new TagPlacement
                    {
                        RegionId = region.Id, Point = snapped.Value, StretchIndex = li,
                    });
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Greedily drops tags added since <paramref name="from"/> that sit within
        /// <paramref name="minSepFt"/> of one already kept. Applied per region, so two
        /// different ceilings may still carry tags close together — they show different
        /// values and both belong on the drawing.
        /// </summary>
        private static int CollapseNearDuplicates(List<TagPlacement> placements, int from, double minSepFt)
        {
            if (minSepFt <= 0 || placements.Count - from <= 1) return placements.Count - from;

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
