using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.AutoDimension
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
            Action<string, string> log, Action<int>? progress = null)
        {
            log = log ?? ((a, b) => { });
            var total = new CommitResult();
            if (doc == null || viewIds == null || viewIds.Count == 0) return total;

            // ── Read side: build a plan per view (no element changes) ─────────
            var engine = new AutoDimensionEngine(log);
            var built  = new List<(View view, EngineOutput output)>();
            int n = 0;
            foreach (var viewId in viewIds)
            {
                progress?.Invoke((int)(n * 50.0 / viewIds.Count));
                var view = doc.GetElement(viewId) as View;
                if (view == null) { n++; continue; }

                log($"— Dimensioning '{view.Name}' —", "info");
                var output = engine.BuildPlan(doc, view, cfg);
                ReportPlan(view, output.Plan, log);
                built.Add((view, output));
                n++;
            }

            // ── Commit side: one transaction for every view's plan ────────────
            using (var tx = new Transaction(doc, "Lemoine - Auto Dimension"))
            {
                tx.Start();
                ConfigureFailures(tx);

                foreach (var (view, output) in built)
                {
                    var r = AutoDimensionCommit.Commit(doc, view, output, log);
                    total.Placed       += r.Placed;
                    total.Failures     += r.Failures;
                    total.DeletedPrior += r.DeletedPrior;
                    total.StaleDeleted += r.StaleDeleted;
                }

                tx.Commit();
            }

            return total;
        }

        private static void ReportPlan(View view, Core.DimensionPlan plan, Action<string, string> log)
        {
            foreach (var u in plan.Unresolved)
                log($"Unresolved [{view.Name}] source {u.SourceKey}: {u.Reason}", "fail");
            foreach (var a in plan.Ambiguities)
                log($"Ambiguous [{view.Name}] source {a.SourceKey}: '{a.CandidateA}' vs '{a.CandidateB}' (Δscore {Math.Abs(a.ScoreA - a.ScoreB):0.###}) — resolve manually.", "fail");
            foreach (var m in plan.MissingLinkRefs)
                log($"Missing link ref [{view.Name}]: {m}", "fail");
            foreach (var note in plan.Notes)
                log($"[{view.Name}] {note}", "info");
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
