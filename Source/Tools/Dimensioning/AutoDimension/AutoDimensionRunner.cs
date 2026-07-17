using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Dimensioning.AutoDimension
{
    /// <summary>
    /// Orchestrates a full auto-dimension run: build a <see cref="Core.DimensionPlan"/> per view
    /// (read-only), report each plan's unresolved / ambiguous / missing-link / note entries, then
    /// commit every view's plan inside ONE transaction via <see cref="AutoDimensionCommit"/>.
    /// Shared so the standalone Auto Dimension tool and the Clash Finder's dimension pass place
    /// dimensions through exactly the same path. The caller owns no transaction — this opens its
    /// own, so it must run after any prior transaction (e.g. clash marking) has committed.
    /// </summary>
    public static class AutoDimensionRunner
    {
        public static CommitResult Run(
            Document doc, IList<ElementId> viewIds, AutoDimensionConfig cfg,
            Action<string, string> log, Action<int>? progress = null,
            IDictionary<ElementId, List<Resolvers.ManualDatum>>? datums = null,
            IDictionary<ElementId, List<Resolvers.SlabScope>>? slabScopes = null,
            IDictionary<ElementId, List<string>>? excludeSourceKeys = null,
            ISet<ElementId>? cropBoundedViewIds = null)
        {
            log = log ?? ((a, b) => { });
            var total = new CommitResult();
            if (doc == null || viewIds == null || viewIds.Count == 0) return total;

            // ── Read side: build a plan per view (no element changes) ─────────
            log(AppStrings.T("clash.autoDim.log.buildingPlans", viewIds.Count), "info");
            var engine = new AutoDimensionEngine(log);
            var built  = new List<(View view, EngineOutput output)>();
            int n = 0;
            foreach (var viewId in viewIds)
            {
                // Cooperative cancel at the per-view boundary: plans already built below
                // still commit, so a stopped run preserves the views finished so far.
                if (RunState.CancelRequested)
                {
                    log(AppStrings.T("common.log.stoppedByUser", n, viewIds.Count), "warn");
                    break;
                }

                progress?.Invoke((int)(n * 50.0 / viewIds.Count));
                var view = doc.GetElement(viewId) as View;
                if (view == null) { n++; continue; }

                List<Resolvers.ManualDatum>? viewDatums = null;
                datums?.TryGetValue(viewId, out viewDatums);
                List<Resolvers.SlabScope>? viewScopes = null;
                slabScopes?.TryGetValue(viewId, out viewScopes);
                List<string>? viewExcludes = null;
                excludeSourceKeys?.TryGetValue(viewId, out viewExcludes);

                log(AppStrings.T("clash.autoDim.log.dimensioningView", view.Name), "info");
                // Dense-area callouts dimension only to references visible in their crop.
                bool boundToCrop = cropBoundedViewIds != null && cropBoundedViewIds.Contains(viewId);
                try
                {
                    // Per-view guard: one throwing view used to abort the entire multi-view
                    // run (Refine passes its whole selection in a single call).
                    var output = engine.BuildPlan(doc, view, cfg, viewDatums, viewScopes, viewExcludes, boundToCrop);
                    ReportPlan(view, output.Plan, log);
                    built.Add((view, output));
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error($"AutoDimensionRunner: build plan for '{view.Name}'", ex);
                    log(AppStrings.T("clash.autoDim.log.planFailed", view.Name, ex.Message), "fail");
                    total.Failures++;
                }
                n++;
            }

            // ── Commit side: one transaction for every view's plan ────────────
            log(AppStrings.T("clash.autoDim.log.committing", built.Count), "info");
            using (var tx = new Transaction(doc, "Lemoine - Auto Dimension"))
            {
                tx.Start();
                ConfigureFailures(tx);

                int c = 0;
                foreach (var (view, output) in built)
                {
                    try
                    {
                        var r = AutoDimensionCommit.Commit(doc, view, output, log);
                        total.Placed       += r.Placed;
                        total.Failures     += r.Failures;
                        total.DeletedPrior += r.DeletedPrior;
                        total.StaleDeleted += r.StaleDeleted;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Error($"AutoDimensionRunner: commit '{view.Name}'", ex);
                        log(AppStrings.T("clash.autoDim.log.commitFailed", view.Name, ex.Message), "fail");
                        total.Failures++;
                    }
                    progress?.Invoke(50 + (int)(++c * 50.0 / built.Count));
                }

                tx.Commit();
            }

            return total;
        }

        private static void ReportPlan(View view, Core.DimensionPlan plan, Action<string, string> log)
        {
            foreach (var u in plan.Unresolved)
                log(AppStrings.T("clash.autoDim.log.unresolved", view.Name, u.SourceKey, u.Reason), "fail");
            foreach (var a in plan.Ambiguities)
                log(AppStrings.T("clash.autoDim.log.ambiguous", view.Name, a.SourceKey, a.CandidateA, a.CandidateB, Math.Abs(a.ScoreA - a.ScoreB)), "fail");
            foreach (var m in plan.MissingLinkRefs)
                log(AppStrings.T("clash.autoDim.log.missingLinkRef", view.Name, m), "fail");
            foreach (var note in plan.Notes)
                log(AppStrings.T("clash.autoDim.log.viewNote", view.Name, note), "info");
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }
    }
}
