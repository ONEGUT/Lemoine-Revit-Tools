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

        /// <summary>Set when the scope is "active view only" (the default). Null otherwise.</summary>
        public ElementId? ActiveViewId { get; set; }

        /// <summary>
        /// Views the user picked when the active-view scope was switched off. Elements visible in
        /// ANY of them are split, deduplicated by id so a run showing in two views is cut once.
        /// </summary>
        public List<ElementId>? SelectedViewIds { get; set; }

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
                    var views = ResolveScopeViews(doc, pushLog);
                    if (views.Count == 0)
                    {
                        // Every requested view failed to resolve — refuse rather than silently
                        // widening the run to the whole document.
                        pushLog(AppStrings.T("modify.splitByLength.log.noViews"), "fail");
                        onComplete(0, 1, 0);
                        return;
                    }

                    elements = CollectInViews(doc, views, SelectedCategoryNames);
                    pushLog(AppStrings.T("modify.splitByLength.log.scopeViews", views.Count), "info");

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

                // Bulk "nothing to do" skips are rolled up into one counted line each rather than
                // one line per element — hundreds of identical notices would bury the warnings and
                // failures that actually need reading. The reason is still stated, so no element
                // is ever silently passed over.
                if (stats.QuietSkips.TryGetValue(SplitElementsShared.SkipTooShort, out int tooShort) && tooShort > 0)
                    pushLog(AppStrings.T("modify.splitByLength.log.skipTooShort", tooShort, SegmentLengthFeet), "info");

                if (stats.QuietSkips.TryGetValue(SplitElementsShared.SkipNotStraight, out int notStraight) && notStraight > 0)
                    pushLog(AppStrings.T("modify.splitByLength.log.skipNotStraight", notStraight), "info");

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
                SelectedViewIds       = null;
            }
        }

        /// <summary>
        /// Resolves the views the run is scoped to: the active view when that scope is on,
        /// otherwise the user's picked views. A view that no longer resolves (deleted between
        /// launch and run) is reported rather than dropped, so the scope can never shrink
        /// silently.
        /// </summary>
        private List<View> ResolveScopeViews(Document doc, Action<string, string> pushLog)
        {
            var wanted = ActiveViewId != null
                ? new List<ElementId> { ActiveViewId! }
                : (SelectedViewIds ?? new List<ElementId>());

            var views = new List<View>();
            int dropped = 0;
            foreach (var id in wanted)
            {
                var v = doc.GetElement(id) as View;
                if (v == null || v.IsTemplate) { dropped++; continue; }
                views.Add(v);
            }

            if (dropped > 0)
            {
                pushLog(AppStrings.T("modify.splitByLength.log.viewsGone", dropped), "warn");
                DiagnosticsLog.Warn("SplitByLength: scope views",
                    $"{dropped} of {wanted.Count} scope views no longer resolve.");
            }
            return views;
        }

        /// <summary>
        /// Collects the elements of the selected categories visible in ANY of
        /// <paramref name="views"/>, deduplicated by id — a run showing in three views is still
        /// cut once. Category matching is by name because the picker is built by scanning the
        /// document, so it can list any line-based family category the project happens to load.
        /// </summary>
        private static List<Element> CollectInViews(Document doc, List<View> views, List<string> labels)
        {
            var selected = new HashSet<string>(labels ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            if (selected.Count == 0) return new List<Element>();

            var seen  = new HashSet<long>();
            var found = new List<Element>();

            foreach (var view in views)
            {
                foreach (var el in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
                {
                    if (el?.Category?.Name == null || !selected.Contains(el.Category.Name)) continue;
                    if (!seen.Add(el.Id.Value)) continue;
                    found.Add(el);
                }
            }
            return found;
        }
    }
}
