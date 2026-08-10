using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.ModifyElements
{
    /// <summary>
    /// Run handler for the Split by Length tool. Collects the chosen scope, then cuts every
    /// straight duct/pipe run into fixed-length pieces inside a single transaction.
    ///
    /// The geometry work lives in <see cref="SplitElementsShared.SplitByLength"/>; this class only
    /// resolves the scope, opens the transaction, streams outcomes to the run log, and clears its
    /// payload afterwards (the handler is parked on an App static for the whole Revit session, so
    /// anything left on it outlives the run — CLAUDE.md "Memory &amp; Lifetime Discipline").
    /// </summary>
    public class SplitByLengthEventHandler : IExternalEventHandler
    {
        // ── Inputs (set before Raise) ─────────────────────────────────────────
        public List<string>     SelectedCategoryNames { get; set; } = new List<string>();
        public List<ElementId>? PreSelectedIds        { get; set; }
        public ElementId?       ActiveViewId          { get; set; }

        public double SegmentLengthFeet { get; set; } = 10.0;
        public double GapFeet           { get; set; } = 0.0;
        public bool   EvenLengths       { get; set; } = false;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     OnLog      { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "SplitByLength";

        public void Execute(UIApplication app)
        {
            var pushLog    = OnLog      ?? ((m, s) => { });
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            try
            {
                Document doc = app.ActiveUIDocument.Document;

                var opts = new LengthSplitOptions
                {
                    SegmentLengthFeet = SegmentLengthFeet,
                    GapFeet           = GapFeet,
                    EvenLengths       = EvenLengths,
                };

                List<Element> elements;
                if (PreSelectedIds != null && PreSelectedIds.Count > 0)
                {
                    elements = PreSelectedIds
                        .Select(id => doc.GetElement(id))
                        .Where(e => e != null)
                        .ToList()!;
                    pushLog(AppStrings.T("modify.splitByLength.log.preSelected", elements.Count), "info");

                    // Ids captured at launch can stop resolving if the element was deleted or
                    // undone in between. Say so — a selection that quietly shrinks looks like the
                    // tool skipped elements for no reason.
                    int vanished = PreSelectedIds.Count - elements.Count;
                    if (vanished > 0)
                    {
                        pushLog(AppStrings.T("modify.splitByLength.log.preSelectedGone", vanished), "warn");
                        DiagnosticsLog.Warn("SplitByLength: pre-selection",
                            $"{vanished} of {PreSelectedIds.Count} pre-selected ids no longer resolve.");
                    }
                }
                else
                {
                    View? view = ActiveViewId != null ? doc.GetElement(ActiveViewId) as View : null;
                    elements = CollectByName(doc, view, SelectedCategoryNames);
                    // A zero result is reported explicitly — a silent empty run is
                    // indistinguishable from a broken collector (CLAUDE.md).
                    pushLog(
                        elements.Count == 0
                            ? AppStrings.T("modify.splitByLength.log.foundNone", SelectedCategoryNames.Count)
                            : AppStrings.T("modify.splitByLength.log.foundCats", elements.Count, SelectedCategoryNames.Count),
                        elements.Count == 0 ? "warn" : "info");
                }

                if (elements.Count == 0)
                {
                    onProgress(100, 0, 0, 0);
                    onComplete(0, 0, 0);
                    return;
                }

                pushLog(AppStrings.T("modify.splitByLength.log.splitting",
                            elements.Count,
                            SegmentLengthFeet,
                            EvenLengths
                                ? AppStrings.T("modify.splitByLength.log.modeEven")
                                : AppStrings.T("modify.splitByLength.log.modeOffcut")),
                        "info");

                pushLog(opts.KeepConnected
                            ? AppStrings.T("modify.splitByLength.log.pathConnected")
                            : AppStrings.T("modify.splitByLength.log.pathDetached", GapFeet * 12.0),
                        "info");

                var progress = new RunProgressReporter(pushLog, elements.Count, "elements");

                SplitStats stats;
                using (var tx = new Transaction(doc, "Split Elements by Length"))
                {
                    var fho = tx.GetFailureHandlingOptions();
                    fho.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(fho);
                    tx.Start();

                    // pushLog streams each element's outcome to the Output log the moment it
                    // happens, rather than dumping the whole list after the commit.
                    stats = SplitElementsShared.SplitByLength(doc, elements, opts, progress, pushLog);

                    // A cancelled run still commits, so every piece cut so far is preserved.
                    tx.Commit();
                }

                if (RunState.CancelRequested)
                {
                    int processed = stats.SplitCount + stats.SkipCount + stats.FailCount;
                    pushLog(AppStrings.T("common.log.stoppedByUser", processed, elements.Count), "warn");
                }

                pushLog(AppStrings.T("modify.splitByLength.log.done",
                            stats.SegmentsCreated, stats.SplitCount, stats.SkipCount, stats.FailCount),
                        stats.FailCount > 0 ? "fail" : "pass");
                onProgress(100, stats.SegmentsCreated, stats.FailCount, stats.SkipCount);
                onComplete(stats.SegmentsCreated, stats.FailCount, stats.SkipCount);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("SplitByLengthEventHandler.Execute", ex);
                pushLog(AppStrings.T("modify.splitByLength.log.error", ex.Message), "fail");
                onComplete(0, 1, 0);
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                SelectedCategoryNames = new List<string>();
                PreSelectedIds        = null;
                ActiveViewId          = null;
            }
        }

        /// <summary>
        /// Collects candidate elements for the selected category labels. Uses an
        /// <see cref="ElementMulticategoryFilter"/> built from
        /// <see cref="SplitElementsShared.LengthSplitCategories"/> rather than walking every
        /// element and comparing category names — the tool supports exactly two categories, so
        /// the filter is both faster on a large model and immune to localised category names.
        /// </summary>
        private static List<Element> CollectByName(Document doc, View? view, List<string> labels)
        {
            var selected = new HashSet<string>(labels ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            var cats = SplitElementsShared.LengthSplitCategories
                .Where(c => selected.Contains(c.Label))
                .Select(c => c.Cat)
                .ToList();

            if (cats.Count == 0) return new List<Element>();

            var coll = (view != null && !view.IsTemplate)
                ? new FilteredElementCollector(doc, view.Id)
                : new FilteredElementCollector(doc);

            return coll
                .WherePasses(new ElementMulticategoryFilter(cats))
                .WhereElementIsNotElementType()
                .ToList();
        }
    }
}
