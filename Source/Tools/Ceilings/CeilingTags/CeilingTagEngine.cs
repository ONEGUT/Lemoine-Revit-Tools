using System;
using System.Collections.Generic;
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

                vtp.Plan = TagPointPlanner.Plan(regions, cfg);
                result.Add(vtp);

                ReportPlan(vtp);
            }

            return result;
        }

        /// <summary>
        /// Rolls the plan up to one line per view, with detail only for the things a user would
        /// otherwise have to discover by hand: ceilings that produced no tag, and ceilings that
        /// were clipped by a lower ceiling.
        /// </summary>
        private void ReportPlan(ViewTagPlan vtp)
        {
            int corridors = 0, clipped = 0, openings = 0, withOpenings = 0;
            foreach (RegionDiagnostic d in vtp.Plan.Diagnostics)
            {
                if (d.WasCorridor) corridors++;
                if (d.VisibleFraction < 0.999 && d.SkipReason == null) clipped++;
                if (d.IgnoredOpenings > 0) { openings += d.IgnoredOpenings; withOpenings++; }
            }

            _log(AppStrings.T("ceilings.tags.log.viewPlanned",
                    vtp.ViewName, vtp.Plan.Placements.Count, vtp.Plan.Diagnostics.Count, corridors),
                "info");

            if (vtp.Plan.FullyHiddenCount > 0)
                _log(AppStrings.T("ceilings.tags.log.fullyHidden", vtp.Plan.FullyHiddenCount), "warn");

            if (clipped > 0)
                _log(AppStrings.T("ceilings.tags.log.partlyHidden", clipped), "info");

            // Light fixtures and diffusers cut real holes in a ceiling face. They are filled
            // back in before the shape is measured, and saying so out loud is what would have
            // made this visible the first time round instead of presenting as odd placement.
            if (openings > 0)
                _log(AppStrings.T("ceilings.tags.log.ignoredOpenings", openings, withOpenings), "info");

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
