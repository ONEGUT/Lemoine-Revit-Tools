using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.FiltersLegends.LegendCreator
{
    // =========================================================================
    // SmartLegendPreviewEventHandler — the Legend Creator window's "what would
    // smart filtering actually draw?" probe.
    //
    // The settings window runs on its own STA thread and cannot touch the Revit
    // document, so it cannot answer this itself. The AUTHORITATIVE pass is the one
    // LegendCreatorEventHandler runs at Create/Update time; this handler exists only
    // so the user can see the answer before committing to a run.
    //
    // Read-only: no transaction, nothing mutated.
    // =========================================================================

    /// <summary>What the window shows after a preview refresh.</summary>
    public sealed class SmartLegendPreviewResult
    {
        /// <summary>One-line description of the resolved scope, already localized.</summary>
        public string ScopeLabel { get; set; } = "";

        /// <summary>False when no scope could be resolved — the legend would draw in full.</summary>
        public bool HasScope { get; set; }

        /// <summary>Legend rows that would be drawn, and the number visible today.</summary>
        public int DrawnRows { get; set; }
        public int VisibleRows { get; set; }

        /// <summary>Filter names proven live, and those proven absent.</summary>
        public int LiveFilters { get; set; }
        public int HiddenFilters { get; set; }

        /// <summary>Filter names that could not be tested and are therefore kept.</summary>
        public int UnprovableFilters { get; set; }

        /// <summary>Non-empty when the probe itself failed — shown instead of counts.</summary>
        public string? Error { get; set; }
    }

    public sealed class SmartLegendPreviewEventHandler : IExternalEventHandler
    {
        // Payload — set by the window before each Raise().
        public List<LegendRowConfig>? Rows            { get; set; }
        public List<string>?          TargetViewNames { get; set; }
        public ElementId?             LegendViewId    { get; set; }

        /// <summary>Called with the result. The window marshals back to its own dispatcher.</summary>
        public Action<SmartLegendPreviewResult>? OnResult { get; set; }

        public string GetName() =>
            "LemoineTools.Tools.FiltersLegends.LegendCreator.SmartLegendPreviewEventHandler";

        public void Execute(UIApplication app)
        {
            var result   = new SmartLegendPreviewResult();
            var rows     = Rows;
            var names    = TargetViewNames;
            var legendId = LegendViewId;
            var callback = OnResult;

            // This is a session-long static handler: drop every reference to the window's
            // data (and to the window itself, via the callback) before doing the work, so a
            // window closed mid-probe cannot be kept alive by it for the rest of the session.
            Rows            = null;
            TargetViewNames = null;
            LegendViewId    = null;
            OnResult        = null;

            try
            {
                Document? doc = app?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    result.Error = AppStrings.T("testing.legendCreator.log.noActiveDoc");
                }
                else
                {
                    Compute(doc, rows, names, legendId, result);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("SmartLegendPreview: probe failed", ex);
                result.Error = ex.Message;
            }

            // Always report — a silent probe would leave the card showing stale counts as
            // though they were current.
            try { callback?.Invoke(result); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("SmartLegendPreview: deliver result", ex); }
        }

        private static void Compute(
            Document doc,
            List<LegendRowConfig>? rows,
            List<string>? targetViewNames,
            ElementId? legendViewId,
            SmartLegendPreviewResult result)
        {
            var resolver   = SmartLegendScope.BuildFilterNameResolver();
            var candidates = SmartLegendScope.CollectCandidates(rows, resolver);

            result.VisibleRows = CountVisible(rows);

            var scope = SmartLegendScope.ResolveScope(doc, legendViewId, targetViewNames);
            result.ScopeLabel = SmartLegendScope.DescribeScope(scope);
            result.HasScope   = scope.HasScope;

            if (!scope.HasScope || candidates.Count == 0)
            {
                // No scope, or nothing testable — every visible row would be drawn.
                result.DrawnRows = result.VisibleRows;
                return;
            }

            // No cancel probe: this is the window's advisory preview, and a live run's
            // cancel flag must not be able to truncate it.
            var usage = SmartLegendScope.ComputeUsage(doc, scope.ViewIds, candidates, null);

            result.LiveFilters       = usage.Live.Count;
            result.HiddenFilters     = usage.Hidden.Count;
            result.UnprovableFilters = usage.Unprovable.Count;

            int drawn = 0;
            foreach (var row in rows ?? new List<LegendRowConfig>())
                foreach (var grp in row?.Groups ?? new List<LegendGroupConfig>())
                    foreach (var blk in grp?.Blocks ?? new List<LegendBlockConfig>())
                    {
                        if (blk == null || !blk.Visible) continue;
                        string? fn = resolver(blk);
                        // A custom swatch has no filter to test, so it always survives.
                        if (fn == null || usage.Live.Contains(fn)) drawn++;
                    }
            result.DrawnRows = drawn;
        }

        private static int CountVisible(List<LegendRowConfig>? rows)
        {
            int n = 0;
            foreach (var row in rows ?? new List<LegendRowConfig>())
                foreach (var grp in row?.Groups ?? new List<LegendGroupConfig>())
                    foreach (var blk in grp?.Blocks ?? new List<LegendBlockConfig>())
                        if (blk != null && blk.Visible) n++;
            return n;
        }
    }
}
