using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Tools.Ceilings.CeilingTags.TagCore;

namespace LemoineTools.Tools.Ceilings.CeilingTags
{
    /// <summary>
    /// Runs the Tag Ceilings pipeline on Revit's main thread: read → plan → commit.
    ///
    /// Parked on an <c>App</c> static for the whole Revit session, so every per-run input is
    /// cleared in a <c>finally</c> — otherwise a run's view list, plans and cached Revit
    /// references would outlive it for the rest of the session.
    /// </summary>
    public class CeilingTagEventHandler : IExternalEventHandler
    {
        // ── Inputs (set by the ViewModel before Raise()) ──────────────────────
        public List<ElementId> SelectedViewIds   { get; set; } = new List<ElementId>();
        public ElementId       TagTypeId         { get; set; } = ElementId.InvalidElementId;
        public double          MaxTagSpacingFt   { get; set; } = 30.0;
        public bool            ReplaceExisting   { get; set; } = true;
        public bool            AccountForCovered { get; set; } = true;

        // ── Callbacks (BeginInvoke-wrapped by StepFlowWindow) ─────────────────
        public Action<string, string>?     PushLog       { get; set; }
        public Action<int, int, int, int>? OnProgress    { get; set; }
        public Action<int, int, int>?      OnComplete    { get; set; }
        public Action<IReadOnlyList<ResultChip>>? OnResultChips { get; set; }

        public string GetName() => "LemoineTools.Tools.Ceilings.CeilingTagEventHandler";

        public void Execute(UIApplication app)
        {
            Document doc = app.ActiveUIDocument.Document;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                Run(doc, ref pass, ref fail, ref skip);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("CeilingTags: run aborted", ex);
                Log(AppStrings.T("ceilings.tags.log.error", ex.Message), "fail");
                fail++;
            }
            finally
            {
                // Session-long static handler — drop this run's payload so nothing is retained.
                SelectedViewIds = new List<ElementId>();
                TagTypeId       = ElementId.InvalidElementId;
            }

            Progress(100, pass, fail, skip);
            OnResultChips?.Invoke(new List<ResultChip>
            {
                new ResultChip("tags",    pass, "LemoineGreen"),
                new ResultChip("failed",  fail, "LemoineRed"),
                new ResultChip("skipped", skip, "LemoineTextDim"),
            });
            Complete(pass, fail, skip);
        }

        private void Run(Document doc, ref int pass, ref int fail, ref int skip)
        {
            if (SelectedViewIds == null || SelectedViewIds.Count == 0)
            {
                Log(AppStrings.T("ceilings.tags.log.noViews"), "fail");
                fail++; return;
            }

            // ── Validate the tag type against THIS document ──────────────────
            if (!(doc.GetElement(TagTypeId) is FamilySymbol tagSymbol))
            {
                Log(AppStrings.T("ceilings.tags.log.noTagType"), "fail");
                fail++; return;
            }

            if (!tagSymbol.IsActive)
            {
                using (var txActivate = new Transaction(doc, "Activate Ceiling Tag Type"))
                {
                    ConfigureFailures(txActivate);
                    txActivate.Start();
                    tagSymbol.Activate();
                    txActivate.Commit();
                }
            }

            // ── Phase 1: read + plan (0–60%), no mutation ────────────────────
            Log(AppStrings.T("ceilings.tags.log.reading", SelectedViewIds.Count), "info");

            var cfg = new TagPlanConfig
            {
                MaxTagSpacingFt   = MaxTagSpacingFt,
                AccountForCovered = AccountForCovered,
            };

            var engine = new CeilingTagEngine(Log);
            List<ViewTagPlan> plans = engine.BuildPlans(doc, SelectedViewIds, cfg, ref fail, ref skip);

            Progress(60, pass, fail, skip);

            if (RunState.CancelRequested)
            {
                // Cancelled before anything was written — leave the model untouched rather
                // than committing a partial plan built from a partial scan.
                Log(AppStrings.T("ceilings.tags.log.cancelledBeforeWrite"), "warn");
                return;
            }

            int totalPlanned = 0;
            foreach (ViewTagPlan p in plans) totalPlanned += p.Plan.Placements.Count;

            if (totalPlanned == 0)
            {
                Log(AppStrings.T("ceilings.tags.log.nothingToPlace"), "warn");
                return;
            }

            Log(AppStrings.T("ceilings.tags.log.planned", totalPlanned, plans.Count), "info");

            // ── Phase 2: commit (60–100%) ────────────────────────────────────
            CommitResult commit = CeilingTagCommit.Place(
                doc, plans, TagTypeId, ReplaceExisting, Log);

            pass += commit.Placed;
            fail += commit.Failed;

            if (commit.Deleted > 0)
                Log(AppStrings.T("ceilings.tags.log.placedReplaced", commit.Placed, commit.Deleted), "pass");
            else
                Log(AppStrings.T("ceilings.tags.log.placed", commit.Placed), "pass");
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private void Log(string t, string s) => PushLog?.Invoke(t, s);
        private void Progress(int p, int pa, int f, int s) => OnProgress?.Invoke(p, pa, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
