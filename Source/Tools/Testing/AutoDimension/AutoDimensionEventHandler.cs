using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.AutoDimension
{
    /// <summary>
    /// Runs the auto-dimension engine: builds an abstract <see cref="Core.DimensionPlan"/> per
    /// view (read-only), then commits every view's plan inside ONE transaction — clearing prior
    /// engine-owned dimensions, placing the new set, and stamping ownership. Reports unresolved
    /// targets, ambiguous slab faces, missing link refs, stale deletions, and layout notes.
    /// </summary>
    public class AutoDimensionEventHandler : IExternalEventHandler
    {
        public List<ElementId> ViewIds { get; set; } = new List<ElementId>();
        public AutoDimensionConfig Config { get; set; } = new AutoDimensionConfig();

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Testing.AutoDimension.AutoDimensionEventHandler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            int placed = 0, fail = 0, skip = 0;

            try
            {
                if (ViewIds == null || ViewIds.Count == 0)
                {
                    Log("No views selected.", "fail");
                    fail++;
                }
                else
                {
                    // ── Read side: build a plan per view (no element changes) ──
                    var built = new List<(View view, EngineOutput output)>();
                    var engine = new AutoDimensionEngine(Log);
                    int n = 0;
                    foreach (var viewId in ViewIds)
                    {
                        Progress(5 + (int)(n * 50.0 / ViewIds.Count), placed, fail, skip);
                        var view = doc.GetElement(viewId) as View;
                        if (view == null) { skip++; n++; continue; }

                        Log($"— View '{view.Name}' —", "info");
                        var output = engine.BuildPlan(doc, view, Config);
                        ReportPlan(view, output.Plan);
                        built.Add((view, output));
                        n++;
                    }

                    // ── Commit side: one transaction for every view's plan ─────
                    using (var tx = new Transaction(doc, "Lemoine - Auto Dimension"))
                    {
                        tx.Start();
                        ConfigureFailures(tx);

                        foreach (var (view, output) in built)
                        {
                            var r = AutoDimensionCommit.Commit(doc, view, output, Log);
                            placed += r.Placed;
                            fail   += r.Failures;
                        }

                        tx.Commit();
                    }

                    Log($"Auto-dimension done — {placed} placed, {fail} failure(s).", placed > 0 ? "pass" : "fail");
                }
            }
            catch (Exception ex)
            {
                LemoineLog.Error("AutoDimensionEventHandler: run", ex);
                Log($"Fatal: {ex.Message}", "fail");
                fail++;
            }

            Progress(100, placed, fail, skip);
            Complete(placed, fail, skip);
        }

        private void ReportPlan(View view, Core.DimensionPlan plan)
        {
            foreach (var u in plan.Unresolved)
                Log($"Unresolved [{view.Name}] source {u.SourceKey}: {u.Reason}", "fail");
            foreach (var a in plan.Ambiguities)
                Log($"Ambiguous [{view.Name}] source {a.SourceKey}: '{a.CandidateA}' vs '{a.CandidateB}' (Δscore {Math.Abs(a.ScoreA - a.ScoreB):0.###}) — resolve manually.", "fail");
            foreach (var m in plan.MissingLinkRefs)
                Log($"Missing link ref [{view.Name}]: {m}", "fail");
            foreach (var note in plan.Notes)
                Log($"[{view.Name}] {note}", "info");
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private void Log(string text, string status) => PushLog?.Invoke(text, status);
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
