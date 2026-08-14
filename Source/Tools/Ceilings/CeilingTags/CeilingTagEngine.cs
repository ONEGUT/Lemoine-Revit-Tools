using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Framework;
using LemoineTools.Tools.Ceilings.CeilingTags.TagCore;

namespace LemoineTools.Tools.Ceilings.CeilingTags
{
    /// <summary>One view's planned tags plus the Revit data the commit needs.</summary>
    public sealed class ViewTagPlan
    {
        public ElementId ViewId   { get; set; } = ElementId.InvalidElementId;
        public string    ViewName { get; set; } = "";
        public TagPlan   Plan     { get; set; } = new TagPlan();
        public Dictionary<string, CeilingSourceRef> Refs { get; }
            = new Dictionary<string, CeilingSourceRef>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The read side of ceiling tagging: reads each selected view's ceilings once, hands them
    /// to the Revit-free planner, and returns a finished plan per view.
    ///
    /// Nothing here mutates the document. That separation is the whole point — the previous
    /// implementation interleaved <c>IndependentTag.Create</c> with per-ceiling geometry reads,
    /// which forced Revit to regenerate the model before every single read (~2 s per tag).
    /// With all reads finished before the first write, the run costs one regeneration total.
    /// </summary>
    public sealed class CeilingTagEngine
    {
        /// <summary>How close a ceiling's level elevation must be to the view's to count as the
        /// same level when the names differ, in feet (1/8 in). Names are the primary match; this
        /// only covers a link whose level naming differs from the host's.</summary>
        private const double LevelMatchTolFt = 0.01;

        private readonly Action<string, string> _log;

        public CeilingTagEngine(Action<string, string> log)
        {
            _log = log ?? ((a, b) => { });
        }

        public List<ViewTagPlan> BuildPlans(
            Document doc, IReadOnlyList<ElementId> viewIds, TagPlanConfig cfg,
            ref int failed, ref int skipped)
        {
            var result = new List<ViewTagPlan>();

            // Tessellated geometry is shared across views: a ceiling visible in three RCPs is
            // read from the model once, not three times.
            var geometryCache = new Dictionary<(long link, long el), CeilingRegionSource.CachedGeometry>();

            for (int vi = 0; vi < viewIds.Count; vi++)
            {
                if (RunState.CancelRequested)
                {
                    _log(AppStrings.T("common.log.stoppedByUser", vi, viewIds.Count), "warn");
                    break;
                }

                ElementId viewId = viewIds[vi];
                if (!(doc.GetElement(viewId) is ViewPlan vp))
                {
                    _log(AppStrings.T("ceilings.tags.log.viewSkipped", viewId), "warn");
                    skipped++;
                    continue;
                }

                var vtp = new ViewTagPlan { ViewId = viewId, ViewName = vp.Name };
                var regions = new List<TaggableRegion>();
                int noGeometry = 0;

                CeilingRegionSource.Collect(doc, vp, regions, vtp.Refs, geometryCache, _log,
                                            ref failed, ref noGeometry);

                // A ceiling with no usable bottom face never reaches the planner, so it would
                // otherwise vanish without a trace.
                if (noGeometry > 0)
                {
                    _log(AppStrings.T("ceilings.tags.log.noGeometry", noGeometry, vp.Name), "warn");
                    skipped += noGeometry;
                }

                // A zero-result collector is indistinguishable from a broken one unless it says
                // so out loud.
                if (regions.Count == 0)
                {
                    _log(AppStrings.T("ceilings.tags.log.noCeilingsInView", vp.Name), "warn");
                    skipped++;
                    continue;
                }

                int hostCount = 0, linkedCount = 0;
                foreach (var kv in vtp.Refs)
                {
                    if (kv.Value.Linked) linkedCount++; else hostCount++;
                }
                _log(AppStrings.T("ceilings.tags.log.viewScanned", vp.Name, hostCount, linkedCount), "info");

                AuditLevels(vp, vtp.Refs);

                vtp.Plan = TagPointPlanner.Plan(regions, cfg);
                result.Add(vtp);

                ReportPlan(vtp);
            }

            return result;
        }

        /// <summary>
        /// Checks that every ceiling collected for a view actually belongs to that view's level,
        /// and warns about the ones that do not.
        ///
        /// Identity is the level NAME first, because that is the only comparison that survives a
        /// document boundary: a linked ceiling's level is an element in the LINK, so its
        /// ElementId means nothing in the host. World elevation is the fallback, for a link whose
        /// level names differ from the host's.
        ///
        /// Strays are still tagged. This is a REPORT, not a filter — isolating a plan to its own
        /// level is the level filters' job (<c>CeilingLevelFilters</c>, applied by Ceiling
        /// Heatmap and Make Ceiling Grids), and this check is how a user finds out those filters
        /// did not fully take. Silently dropping the tags instead would hide exactly that.
        /// </summary>
        private void AuditLevels(ViewPlan vp, Dictionary<string, CeilingSourceRef> refs)
        {
            Level? viewLevel = vp.GenLevel;
            if (viewLevel == null)
            {
                // A view with no associated level has nothing to check against — say so, rather
                // than reporting a clean pass that never happened.
                _log(AppStrings.T("ceilings.tags.log.levelCheckSkipped", vp.Name), "info");
                return;
            }

            string wantName = (viewLevel.Name ?? "").Trim();
            double wantZ    = viewLevel.Elevation;

            int matched = 0, noLevel = 0;
            var strays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (CeilingSourceRef src in refs.Values)
            {
                if (!src.HasLevel) { noLevel++; continue; }

                bool onLevel =
                    string.Equals((src.LevelName ?? "").Trim(), wantName, StringComparison.OrdinalIgnoreCase)
                    || (!double.IsNaN(src.LevelWorldZ) && Math.Abs(src.LevelWorldZ - wantZ) <= LevelMatchTolFt);

                if (onLevel) { matched++; continue; }

                string key = string.IsNullOrWhiteSpace(src.LevelName)
                    ? AppStrings.T("ceilings.tags.log.levelUnnamed")
                    : src.LevelName.Trim();
                strays[key] = strays.TryGetValue(key, out int c) ? c + 1 : 1;
            }

            int strayCount = strays.Values.Sum();

            if (strayCount == 0 && noLevel == 0)
            {
                _log(AppStrings.T("ceilings.tags.log.levelCheckOk", vp.Name, wantName, matched), "info");
                return;
            }

            if (strayCount > 0)
            {
                _log(AppStrings.T("ceilings.tags.log.levelMismatch", strayCount, vp.Name, wantName), "warn");
                // One line per offending level rather than per ceiling — bounded by the level
                // count, so a badly-scoped view cannot bury the rest of the log.
                foreach (var kv in strays.OrderByDescending(k => k.Value))
                    _log(AppStrings.T("ceilings.tags.log.levelMismatchDetail", kv.Value, kv.Key), "warn");
            }

            if (noLevel > 0)
                _log(AppStrings.T("ceilings.tags.log.levelMissing", noLevel, vp.Name), "warn");
        }

        /// <summary>
        /// Rolls the plan up to one line per view, with detail only for the things a user would
        /// otherwise have to discover by hand: ceilings that produced no tag, and ceilings that
        /// were clipped by a lower ceiling.
        /// </summary>
        private void ReportPlan(ViewTagPlan vtp)
        {
            int corridors = 0, clipped = 0, openings = 0, withOpenings = 0, ringCorridors = 0, belowGrid = 0;
            foreach (RegionDiagnostic d in vtp.Plan.Diagnostics)
            {
                if (d.WasCorridor) corridors++;
                if (d.WasRingCorridor) ringCorridors++;
                if (d.VisibleFraction < 0.999 && d.SkipReason == null) clipped++;
                if (d.IgnoredOpenings > 0) { openings += d.IgnoredOpenings; withOpenings++; }
                if (d.BelowGridResolution) belowGrid++;
            }

            _log(AppStrings.T("ceilings.tags.log.viewPlanned",
                    vtp.ViewName, vtp.Plan.Placements.Count, vtp.Plan.Diagnostics.Count, corridors),
                "info");

            // A ring that carries ten tags instead of one is a big visible change, so say why:
            // its enclosed hole was full of other ceilings, meaning it loops rooms.
            if (ringCorridors > 0)
                _log(AppStrings.T("ceilings.tags.log.ringCorridors", ringCorridors), "info");

            if (vtp.Plan.FullyHiddenCount > 0)
                _log(AppStrings.T("ceilings.tags.log.fullyHidden", vtp.Plan.FullyHiddenCount), "warn");

            if (clipped > 0)
                _log(AppStrings.T("ceilings.tags.log.partlyHidden", clipped), "info");

            // Light fixtures and diffusers cut real holes in a ceiling face. They are filled
            // back in before the shape is measured, and saying so out loud is what would have
            // made this visible the first time round instead of presenting as odd placement.
            if (openings > 0)
                _log(AppStrings.T("ceilings.tags.log.ignoredOpenings", openings, withOpenings), "info");

            // Tagged, but placed from the raw footprint rather than a measured point — the only
            // ceilings whose tag position was not shape-analysed. Size never skips a ceiling, so
            // this is a note about HOW it was placed, not a warning that it was dropped.
            if (belowGrid > 0)
                _log(AppStrings.T("ceilings.tags.log.belowGrid", belowGrid), "info");

            // Any ceiling that produced no tag for a reason other than being fully hidden is a
            // surprise worth naming — it is the difference between "correctly excluded" and
            // "silently missing".
            foreach (RegionDiagnostic d in vtp.Plan.Diagnostics)
            {
                if (d.SkipReason == null || d.SkipReason == TagPointPlanner.SkipFullyHidden) continue;
                _log(AppStrings.T("ceilings.tags.log.regionSkipped",
                        string.IsNullOrEmpty(d.DisplayName) ? d.RegionId : d.DisplayName, d.SkipReason),
                    "warn");
            }
        }
    }
}
