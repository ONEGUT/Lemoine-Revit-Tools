using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.LinkViews.BulkRename
{
    /// <summary>
    /// <see cref="IExternalEventHandler"/> that applies a bulk rename to the selected
    /// sheets or views. The ViewModel sets the public inputs before calling <c>Raise()</c>.
    /// Values are recomputed from the live document through the same
    /// <see cref="BulkRenameEngine.Plan"/> used by the preview, so the result matches.
    /// Unchanged, empty, and colliding values are skipped and logged — never written blindly.
    /// </summary>
    public sealed class BulkRenameRunHandler : IExternalEventHandler
    {
        // ── Inputs set before Raise() ─────────────────────────────────
        public RenameTarget    Target     { get; set; } = RenameTarget.Sheets;
        public RenameField     Field      { get; set; } = RenameField.Name;
        public List<ElementId> OrderedIds { get; set; } = new List<ElementId>();
        public RenameConfig    Config     { get; set; } = new RenameConfig();

        // ── Callbacks ─────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.LinkViews.BulkRename.BulkRenameRunHandler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            long issues0 = DiagnosticsLog.IssueCount;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                if (doc == null)
                {
                    Log(AppStrings.T("linkviews.bulkRename.log.noDoc"), "fail");
                    Complete(0, 1, 0);
                    return;
                }

                try { RunRename(doc, ref pass, ref fail, ref skip); }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("Bulk rename: run aborted", ex);
                    Log(AppStrings.T("linkviews.bulkRename.log.error", ex.Message), "fail");
                    fail++;
                }

                Progress(100, pass, fail, skip);
                long issues = DiagnosticsLog.IssuesSince(issues0);
                if (issues > 0) Log(AppStrings.T("linkviews.bulkRename.log.nonFatal", issues), "warn");
                Complete(pass, fail, skip);
            }
            finally
            {
                // Session-long static handler (App.BulkRenameRunHandler) — drop the run's payload.
                OrderedIds = new List<ElementId>();
                Config     = new RenameConfig();
            }
        }

        // ── Main logic ─────────────────────────────────────────────────
        private void RunRename(Document doc, ref int pass, ref int fail, ref int skip)
        {
            bool enforceUnique = Target == RenameTarget.Views
                              || (Target == RenameTarget.Sheets && Field == RenameField.Number);

            // Build ordered entries from the live document (authoritative values).
            var entries = new List<(string oldValue, Dictionary<string, string> tokens, object? tag)>();
            foreach (var id in OrderedIds)
            {
                var elem = doc.GetElement(id);
                if (elem == null)
                {
                    Log(AppStrings.T("linkviews.bulkRename.log.skipGone", id.Value), "info");
                    skip++;
                    continue;
                }

                if (Target == RenameTarget.Sheets && elem is ViewSheet vs)
                {
                    string number = vs.SheetNumber ?? "";
                    string name   = vs.Name ?? "";
                    string oldVal = Field == RenameField.Number ? number : name;
                    entries.Add((oldVal,
                        new Dictionary<string, string> { ["SheetNumber"] = number, ["SheetName"] = name }, id));
                }
                else if (Target == RenameTarget.Views && elem is View v && !(elem is ViewSheet))
                {
                    entries.Add((v.Name ?? "",
                        new Dictionary<string, string>
                        {
                            ["ViewName"] = v.Name ?? "",
                            ["ViewType"] = ViewsByTemplateRunHandler.ViewTypeLabel(v.ViewType),
                        }, id));
                }
                else
                {
                    Log(AppStrings.T("linkviews.bulkRename.log.skipWrongType", id.Value, Target == RenameTarget.Sheets ? AppStrings.T("linkviews.bulkRename.log.wordSheet") : AppStrings.T("linkviews.bulkRename.log.wordView")), "info");
                    skip++;
                }
            }

            if (entries.Count == 0)
            {
                Log(AppStrings.T("linkviews.bulkRename.log.nothingToDo"), "info");
                return;
            }

            var existing = enforceUnique ? ExistingValuesNotSelected(doc) : Enumerable.Empty<string>();
            var plan = BulkRenameEngine.Plan(Config, entries, existing, enforceUnique);

            // Log & count the skipped outcomes; collect the actual renames to apply.
            var changes = new List<RenamePlanItem>();
            foreach (var item in plan)
            {
                switch (item.Status)
                {
                    case RenameStatus.Unchanged:
                        Log(AppStrings.T("linkviews.bulkRename.log.skipNoChange", item.OldValue), "info");
                        skip++;
                        break;
                    case RenameStatus.Empty:
                        Log(AppStrings.T("linkviews.bulkRename.log.skipEmpty", item.OldValue), "info");
                        skip++;
                        break;
                    case RenameStatus.Collision:
                        Log(AppStrings.T("linkviews.bulkRename.log.skipInUse", item.OldValue, item.NewValue), "info");
                        skip++;
                        break;
                    default:
                        changes.Add(item);
                        break;
                }
            }

            using (var tx = new Transaction(doc, "Lemoine — Bulk Rename"))
            {
                ConfigureFailures(tx);
                tx.Start();

                if (enforceUnique)
                    ApplyTwoPhase(doc, changes, skip, ref pass, ref fail);
                else
                    ApplySinglePhase(doc, changes, skip, ref pass, ref fail);

                tx.Commit();
            }

            Log(AppStrings.T("linkviews.bulkRename.log.complete", pass, skip, fail), "pass");
        }

        // Sheet-name renames (no uniqueness): each new value is final, so apply directly and
        // allow per-item cancellation — committed renames stay consistent.
        private void ApplySinglePhase(Document doc, List<RenamePlanItem> changes, int skip, ref int pass, ref int fail)
        {
            int total = changes.Count, done = 0;
            foreach (var item in changes)
            {
                if (RunState.CancelRequested)
                {
                    Log(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                    break;
                }
                done++;
                Progress((int)(done * 95.0 / Math.Max(total, 1)), pass, fail, skip);

                if (!(item.Tag is ElementId id)) { fail++; continue; }
                try
                {
                    ApplyRename(doc, id, item.NewValue);
                    Log(AppStrings.T("linkviews.bulkRename.log.renamed", item.OldValue, item.NewValue), "pass");
                    pass++;
                }
                catch (Exception e)
                {
                    DiagnosticsLog.Swallowed($"Bulk rename: set value on {id.Value}", e);
                    Log(AppStrings.T("linkviews.bulkRename.log.failed", item.OldValue, item.NewValue, e.Message), "fail");
                    fail++;
                }
            }
        }

        // Unique fields (sheet numbers, view names): park every item on a unique temporary value
        // first, then assign finals. Without this, a shift (101→102, 102→103) or a swap (A↔B)
        // trips Revit's mid-batch duplicate check and only the last item renames. Because the
        // Set calls are cheap and only the single commit is costly, the whole apply is treated
        // as one unit — cancellation is honoured up front so a cancel can never leave an element
        // stranded on a temporary name.
        private void ApplyTwoPhase(Document doc, List<RenamePlanItem> changes, int skip, ref int pass, ref int fail)
        {
            if (changes.Count == 0) return;

            if (RunState.CancelRequested)
            {
                Log(AppStrings.T("common.log.stoppedByUser", 0, changes.Count), "warn");
                return;
            }

            // Phase 1 — temporary unique values.
            var staged = new List<RenamePlanItem>();
            foreach (var item in changes)
            {
                if (!(item.Tag is ElementId id)) { fail++; continue; }
                string temp = "TMP_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_" + id.Value;
                try
                {
                    ApplyRename(doc, id, temp);
                    staged.Add(item);
                }
                catch (Exception e)
                {
                    DiagnosticsLog.Swallowed($"Bulk rename: stage temp value on {id.Value}", e);
                    Log(AppStrings.T("linkviews.bulkRename.log.failed", item.OldValue, item.NewValue, e.Message), "fail");
                    fail++;
                }
            }

            // Phase 2 — final values (targets were pre-checked unique by the planner).
            int total = staged.Count, done = 0;
            foreach (var item in staged)
            {
                done++;
                Progress((int)(done * 95.0 / Math.Max(total, 1)), pass, fail, skip);

                var id = (ElementId)item.Tag!;
                try
                {
                    ApplyRename(doc, id, item.NewValue);
                    Log(AppStrings.T("linkviews.bulkRename.log.renamed", item.OldValue, item.NewValue), "pass");
                    pass++;
                }
                catch (Exception e)
                {
                    DiagnosticsLog.Swallowed($"Bulk rename: set final value on {id.Value}", e);
                    Log(AppStrings.T("linkviews.bulkRename.log.failed", item.OldValue, item.NewValue, e.Message), "fail");
                    fail++;
                }
            }
        }

        private void ApplyRename(Document doc, ElementId id, string newValue)
        {
            var elem = doc.GetElement(id)
                ?? throw new InvalidOperationException("Element disappeared mid-run.");

            if (Target == RenameTarget.Sheets)
            {
                var bip = Field == RenameField.Number ? BuiltInParameter.SHEET_NUMBER : BuiltInParameter.SHEET_NAME;
                var p = elem.get_Parameter(bip)
                    ?? throw new InvalidOperationException($"Parameter {bip} not found.");
                if (!p.Set(newValue))
                    throw new InvalidOperationException("Revit rejected the value.");
            }
            else if (elem is View v)
            {
                v.Name = newValue; // throws on duplicate — caught by caller
            }
        }

        private IEnumerable<string> ExistingValuesNotSelected(Document doc)
        {
            var selected = new HashSet<long>(OrderedIds.Select(i => i.Value));

            if (Target == RenameTarget.Sheets) // Number uniqueness
            {
                return new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => !s.IsTemplate && !selected.Contains(s.Id.Value))
                    .Select(s => s.SheetNumber ?? "")
                    .ToList();
            }

            // View Name uniqueness — exclude sheets (their names are not in the view namespace concern here).
            return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && !(v is ViewSheet) && !selected.Contains(v.Id.Value))
                .Select(v => v.Name ?? "")
                .ToList();
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private void Log(string t, string s)               => PushLog?.Invoke(t, s);
        private void Progress(int p, int pa, int f, int s)  => OnProgress?.Invoke(p, pa, f, s);
        private void Complete(int p, int f, int s)          => OnComplete?.Invoke(p, f, s);
    }
}
