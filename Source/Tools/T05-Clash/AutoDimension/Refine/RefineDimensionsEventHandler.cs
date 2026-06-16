using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Clash.AutoDimension.Refine
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

        public string GetName() => "LemoineTools.Tools.Clash.AutoDimension.Refine.RefineDimensionsEventHandler";

        public void Execute(UIApplication app)
        {
            int placed = 0, failures = 0, replaced = 0;
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    Log("No active document.", "fail");
                    Progress(100, 0, 1, 0);
                    Complete(0, 1, 0);
                    return;
                }
                if (ViewIds == null || ViewIds.Count == 0)
                {
                    Log("No views selected.", "fail");
                    Progress(100, 0, 1, 0);
                    Complete(0, 1, 0);
                    return;
                }
                if (LemoineRun.CancelRequested)
                {
                    Log("Stopped by user before the refine pass began — nothing changed.", "warn");
                    Complete(0, 0, ViewIds.Count);
                    return;
                }

                Progress(5, 0, 0, 0);

                // Destination is the only override; chaining, grouping, and spacing always come from
                // the saved Settings → Dimensions values. Restore the snapshot in finally.
                var dimCfg = AutoDimensionConfig.Instance;
                string snapTarget = dimCfg.TargetType;
                try
                {
                    dimCfg.TargetType = string.Equals(DimTargetType, "Grid", StringComparison.OrdinalIgnoreCase)
                        ? "Grid" : "SlabEdge";

                    Log($"Refine: re-dimensioning {ViewIds.Count} view(s) to the nearest "
                      + $"{(dimCfg.TargetType == "Grid" ? "grid" : "slab edge")} visible in each view — "
                      + "no clash detection, no callouts, scale unchanged.", "info");

                    // Every selected view is crop-bounded: the resolvers reject any target whose
                    // dimension landing point falls outside what the view shows, so each clash
                    // dimensions to the NEAREST grid / slab edge visible in that view.
                    var cropBounded = new HashSet<ElementId>(ViewIds);

                    var result = AutoDimensionRunner.Run(
                        doc, ViewIds, dimCfg,
                        (t, s) => Log(t, s),
                        p => Progress(Math.Min(95, 5 + (int)(p * 1.8)), 0, 0, 0),
                        datums: null, slabScopes: null, excludeSourceKeys: null,
                        cropBoundedViewIds: cropBounded);

                    placed   = result.Placed;
                    failures = result.Failures;
                    replaced = result.DeletedPrior + result.StaleDeleted;
                }
                finally
                {
                    dimCfg.TargetType = snapTarget;
                }

                Log($"Done — {placed} dimension(s) placed across {ViewIds.Count} view(s); "
                  + $"{replaced} prior dimension(s) replaced; {failures} placement failure(s).",
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
                LemoineLog.Error("RefineDimensionsEventHandler: execute", ex);
                Log($"Fatal: {ex.Message}", "fail");
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
