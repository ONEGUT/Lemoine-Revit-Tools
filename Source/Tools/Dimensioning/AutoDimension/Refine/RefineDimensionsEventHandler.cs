using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Dimensioning.AutoDimension.Refine
{
    /// <summary>
    /// Re-dimensions views that ALREADY carry clash markers, without detecting or marking clashes.
    /// Runs the shared <see cref="AutoDimensionRunner"/> directly over the selected views, with each
    /// view passed as crop-bounded so every clash dimensions to the nearest grid / slab edge VISIBLE
    /// in that view (the same constraint dense-area callouts use). The runner never creates callouts
    /// and never changes a view's scale, so a refine pass leaves the view's scale and any callouts
    /// alone and simply replaces the tool's own prior dimensions (AutoDimOwnerSchema delete-and-replace).
    /// Window ownership, run-log/failure capture, and the cancel flag are all wired by StepFlowWindow.
    /// </summary>
    public class RefineDimensionsEventHandler : IExternalEventHandler
    {
        // ── Inputs (set by the ViewModel before Raise()) ──────────────────────
        public List<ElementId> ViewIds       { get; set; } = new List<ElementId>();
        public string          DimTargetType { get; set; } = "SlabEdge";   // "SlabEdge" | "Grid"

        public Action<string, string>?           PushLog       { get; set; }
        public Action<int, int, int, int>?        OnProgress    { get; set; }
        public Action<int, int, int>?             OnComplete    { get; set; }
        public Action<IReadOnlyList<ResultChip>>? OnResultChips { get; set; }

        public string GetName() => "LemoineTools.Tools.Dimensioning.AutoDimension.Refine.RefineDimensionsEventHandler";

        public void Execute(UIApplication app)
        {
            int placed = 0, failures = 0, replaced = 0;
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    Log(AppStrings.T("clash.refineDimensions.log.noDoc"), "fail");
                    Progress(100, 0, 1, 0);
                    Complete(0, 1, 0);
                    return;
                }
                if (ViewIds == null || ViewIds.Count == 0)
                {
                    Log(AppStrings.T("clash.refineDimensions.log.noViews"), "fail");
                    Progress(100, 0, 1, 0);
                    Complete(0, 1, 0);
                    return;
                }
                if (RunState.CancelRequested)
                {
                    Log(AppStrings.T("clash.refineDimensions.log.stoppedEarly"), "warn");
                    Complete(0, 0, ViewIds.Count);
                    return;
                }

                Progress(5, 0, 0, 0);

                // Destination is the only override; chaining, grouping, and spacing always come
                // from the saved Settings → Dimensions values. The override goes on a per-run
                // CLONE so a concurrent settings save can never persist it.
                var dimCfg = AutoDimensionConfig.Instance.CloneForRun();
                dimCfg.TargetType = string.Equals(DimTargetType, "Grid", StringComparison.OrdinalIgnoreCase)
                    ? "Grid" : "SlabEdge";

                Log(AppStrings.T("clash.refineDimensions.log.refining", ViewIds.Count, dimCfg.TargetType == "Grid" ? AppStrings.T("clash.refineDimensions.log.wordGrid") : AppStrings.T("clash.refineDimensions.log.wordSlabEdge")), "info");

                // Every selected view is crop-bounded: the resolvers reject any target whose
                // dimension landing point falls outside what the view shows, so each clash
                // dimensions to the NEAREST grid / slab edge visible in that view.
                var cropBounded = new HashSet<ElementId>(ViewIds);

                // Runner progress now spans planning (0–50) AND committing (50–100); the
                // runner also honors the cancel flag at its per-view boundaries, so the red
                // Cancel button works mid-run (finished views are still committed).
                var result = AutoDimensionRunner.Run(
                    doc, ViewIds, dimCfg,
                    (t, s) => Log(t, s),
                    p => Progress(Math.Min(95, 5 + (int)(p * 0.9)), 0, 0, 0),
                    datums: null, slabScopes: null, excludeSourceKeys: null,
                    cropBoundedViewIds: cropBounded);

                placed   = result.Placed;
                failures = result.Failures;
                // DeletedPrior already includes the stale subset — adding StaleDeleted on top
                // double-counted and made the log overstate the replacements.
                replaced = result.DeletedPrior;

                Log(AppStrings.T("clash.refineDimensions.log.done", placed, ViewIds.Count, replaced, failures),
                    placed > 0 ? "pass" : failures > 0 ? "fail" : "info");

                OnResultChips?.Invoke(new List<ResultChip>
                {
                    new ResultChip("dims",     placed,   "LemoineGreen"),
                    new ResultChip("replaced", replaced, "LemoineTextDim"),
                    new ResultChip("failed",   failures, "LemoineRed"),
                });

                Progress(100, placed, failures, 0);
                Complete(placed, failures, 0);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("RefineDimensionsEventHandler: execute", ex);
                Log(AppStrings.T("clash.refineDimensions.log.fatal", ex.Message), "fail");
                Complete(placed, failures + 1, 0);
            }
            finally
            {
                // Session-long static handler — drop the run's payload (memory discipline).
                ViewIds = new List<ElementId>();
            }
        }

        private void Log(string text, string status) => PushLog?.Invoke(text, status);
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
