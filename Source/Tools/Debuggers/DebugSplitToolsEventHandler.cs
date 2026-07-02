using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.ModifyElements;

namespace LemoineTools.Tools.Debuggers
{
    internal enum DebugSplitMode { Level, Grid, ReferencePlane, Cell }

    /// <summary>
    /// Debug harness for the four split tools (CLAUDE.md "Crashes &amp; Large Ambiguous
    /// Issues" — a dedicated harness that exercises the real engine, rather than reading code
    /// and guessing). Each mode auto-collects every supported element in the active view and
    /// every available cutter (levels / grids / reference planes, or a default cell size), logs
    /// exactly what was found BEFORE running, then calls the same production engine
    /// (<see cref="SplitElementsShared"/> / <see cref="SplitByCellHelpers"/>) the real tools use
    /// and logs the per-element outcome. Runs are committed (not rolled back) — this is a real
    /// test of the real commit path, not a rehearsal, so use a scratch/test file.
    /// Debug-only tool: log text is deliberately hardcoded (not LemoineStrings-routed).
    /// </summary>
    internal sealed class DebugSplitToolsEventHandler : IExternalEventHandler
    {
        internal DebugSplitMode Mode { get; set; }

        public Action<string, string>? OnLog      { get; set; }
        public Action?                 OnComplete { get; set; }

        public string GetName() => "DebugSplitTools";

        public void Execute(UIApplication app)
        {
            var pushLog    = OnLog      ?? ((m, s) => { });
            var onComplete = OnComplete ?? (() => { });

            // LemoineRun.CancelRequested is a process-wide flag shared by every tool. This
            // debug harness bypasses StepFlowWindow.StartRun() (which normally resets it via
            // Begin()), so force a clean state here — otherwise a stuck-true flag from an
            // unrelated prior run would make SplitElementsShared break out of its loop on the
            // very first element with no clear explanation, exactly the kind of misleading
            // "detected a reason that isn't true" symptom this harness exists to catch.
            LemoineRun.Begin();

            try
            {
                Document doc  = app.ActiveUIDocument.Document;
                View?    view = app.ActiveUIDocument.ActiveView;

                if (view == null || view.IsTemplate)
                {
                    pushLog("No active (non-template) view — open a view with your test elements and try again.", "fail");
                    return;
                }

                switch (Mode)
                {
                    case DebugSplitMode.Level:         RunLevelTest(doc, view, pushLog); break;
                    case DebugSplitMode.Grid:           RunGridTest(doc, view, pushLog); break;
                    case DebugSplitMode.ReferencePlane: RunReferencePlaneTest(doc, view, pushLog); break;
                    case DebugSplitMode.Cell:           RunCellTest(doc, view, pushLog); break;
                }
            }
            catch (Exception ex)
            {
                pushLog($"Unhandled error: {ex.Message}", "fail");
                LemoineLog.Error("DebugSplitTools: unhandled error", ex);
            }
            finally
            {
                LemoineRun.End();
                OnLog      = null;
                OnComplete = null;
                onComplete();
            }
        }

        // ── Level ────────────────────────────────────────────────────────────
        private static void RunLevelTest(Document doc, View view, Action<string, string> pushLog)
        {
            var targets  = SplitTargets.For(SplitCutterKind.Level);
            var (_, total, elements) = SplitTargets.CollectForPicker(doc, view, targets);
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();

            pushLog($"Detected {total} candidate element(s) in view '{view.Name}': {DescribeElements(elements)}", "info");
            pushLog($"Cutting with {levels.Count} level(s): {string.Join(", ", levels.Select(l => l.Name))}", "info");

            if (total == 0)
            { pushLog($"No supported elements found — place one of: {string.Join(", ", targets.Select(t => t.Label))}.", "fail"); return; }
            if (levels.Count == 0)
            { pushLog("No levels found in the document.", "fail"); return; }

            SplitStats stats;
            using (var tx = new Transaction(doc, "DEBUG Split by Level"))
            {
                var fho = tx.GetFailureHandlingOptions();
                fho.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(fho);
                tx.Start();
                stats = SplitElementsShared.SplitByLevel(doc, elements, levels);
                tx.Commit();
            }
            LogStats(stats, pushLog);
        }

        // ── Grid ─────────────────────────────────────────────────────────────
        private static void RunGridTest(Document doc, View view, Action<string, string> pushLog)
        {
            var targets  = SplitTargets.For(SplitCutterKind.Grid);
            var (_, total, elements) = SplitTargets.CollectForPicker(doc, view, targets);
            var grids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid)).Cast<Grid>().Where(g => g.Curve is Line).OrderBy(g => g.Name).ToList();

            pushLog($"Detected {total} candidate element(s) in view '{view.Name}': {DescribeElements(elements)}", "info");
            pushLog($"Cutting with {grids.Count} straight grid line(s): {string.Join(", ", grids.Select(g => g.Name))}", "info");

            if (total == 0)
            { pushLog($"No supported elements found — place one of: {string.Join(", ", targets.Select(t => t.Label))}.", "fail"); return; }
            if (grids.Count == 0)
            { pushLog("No straight grid lines found in the document.", "fail"); return; }

            SplitStats stats;
            using (var tx = new Transaction(doc, "DEBUG Split by Grid"))
            {
                var fho = tx.GetFailureHandlingOptions();
                fho.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(fho);
                tx.Start();
                var gridList = new List<Grid?>();
                foreach (var g in grids) gridList.Add(g);
                stats = SplitElementsShared.SplitByGrid(doc, elements, gridList);
                tx.Commit();
            }
            LogStats(stats, pushLog);
        }

        // ── Reference Plane ──────────────────────────────────────────────────
        private static void RunReferencePlaneTest(Document doc, View view, Action<string, string> pushLog)
        {
            var targets  = SplitTargets.For(SplitCutterKind.ReferencePlane);
            var (_, total, elements) = SplitTargets.CollectForPicker(doc, view, targets);
            var refPlanes = new FilteredElementCollector(doc)
                .OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();

            pushLog($"Detected {total} candidate element(s) in view '{view.Name}': {DescribeElements(elements)}", "info");
            pushLog($"Cutting with {refPlanes.Count} reference plane(s).", "info");

            if (total == 0)
            { pushLog($"No supported elements found — place one of: {string.Join(", ", targets.Select(t => t.Label))}.", "fail"); return; }
            if (refPlanes.Count == 0)
            { pushLog("No reference planes found in the document.", "fail"); return; }

            SplitStats stats;
            using (var tx = new Transaction(doc, "DEBUG Split by Reference Plane"))
            {
                var fho = tx.GetFailureHandlingOptions();
                fho.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(fho);
                tx.Start();
                var planeList = new List<ReferencePlane?>();
                foreach (var r in refPlanes) planeList.Add(r);
                stats = SplitElementsShared.SplitByReferencePlane(doc, elements, planeList);
                tx.Commit();
            }
            LogStats(stats, pushLog);
        }

        // ── Cell ─────────────────────────────────────────────────────────────
        private static void RunCellTest(Document doc, View view, Action<string, string> pushLog)
        {
            var categoryMap = SplitByCellHelpers.BuildCategoryMap(doc, view);
            var targetIds   = categoryMap.Values.SelectMany(v => v).ToList();

            pushLog($"Detected {targetIds.Count} candidate element(s) in view '{view.Name}': {DescribeCategoryMap(doc, categoryMap)}", "info");
            pushLog("Cutting with a 10' x 10' cell grid, from each element's own bounding-box origin.", "info");

            if (targetIds.Count == 0)
            {
                var targets = SplitTargets.For(SplitCutterKind.Cell);
                pushLog($"No supported elements found — place one of: {string.Join(", ", targets.Select(t => t.Label))}.", "fail");
                return;
            }

            int created = 0, skipped = 0, failed = 0;
            using (var tg = new TransactionGroup(doc, "DEBUG Split by Cell"))
            {
                tg.Start();
                foreach (var id in targetIds)
                {
                    Element? el = doc.GetElement(id);
                    if (el == null) { skipped++; continue; }

                    using (var tx = new Transaction(doc, $"DEBUG Split {el.Name}"))
                    {
                        var fho = tx.GetFailureHandlingOptions();
                        fho.SetClearAfterRollback(true);
                        tx.SetFailureHandlingOptions(fho);
                        tx.Start();

                        try
                        {
                            var (n, status) = SplitByCellHelpers.SplitElement(doc, el, 10.0, 10.0, null);
                            if (status == CellSplitStatus.Split)
                            { created += n; pushLog($"✓ {el.Category?.Name} {el.Id} → {n} cell(s)", "pass"); tx.Commit(); }
                            else if (status == CellSplitStatus.FitsInOneCell)
                            { tx.RollBack(); skipped++; pushLog($"— {el.Category?.Name} {el.Id}: fits in one cell", "info"); }
                            else if (status == CellSplitStatus.NoGeometry)
                            { tx.RollBack(); skipped++; pushLog($"— {el.Category?.Name} {el.Id}: no solid geometry", "info"); }
                            else
                            { tx.RollBack(); failed++; pushLog($"✗ {el.Category?.Name} {el.Id}: boolean intersection produced no cells", "fail"); }
                        }
                        catch (Exception ex)
                        {
                            tx.RollBack();
                            failed++;
                            pushLog($"✗ {el.Category?.Name} {el.Id}: {ex.Message}", "fail");
                        }
                    }
                }
                tg.Assimilate();
            }
            pushLog($"Done. {created} cell(s) created, {skipped} skipped, {failed} failed.", failed > 0 ? "fail" : "pass");
        }

        // ── Shared logging ───────────────────────────────────────────────────
        private static void LogStats(SplitStats stats, Action<string, string> pushLog)
        {
            foreach (var entry in stats.Log)
            {
                string status = entry.StartsWith("✓") ? "pass" : entry.StartsWith("✗") ? "fail" : "info";
                pushLog(entry, status);
            }
            pushLog(
                $"Done. {stats.SegmentsCreated} segment(s) created from {stats.SplitCount} element(s), " +
                $"{stats.SkipCount} skipped, {stats.FailCount} failed.",
                stats.FailCount > 0 ? "fail" : "pass");
        }

        private static string DescribeElements(List<Element> elements)
        {
            if (elements.Count == 0) return "(none)";
            var byCat = elements
                .GroupBy(e => e.Category?.Name ?? "?")
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Key} ({g.Count()})");
            return string.Join(", ", byCat);
        }

        private static string DescribeCategoryMap(Document doc, Dictionary<BuiltInCategory, List<ElementId>> map)
        {
            if (map.Count == 0) return "(none)";
            var parts = map.Select(kvp =>
            {
                string label = Category.GetCategory(doc, kvp.Key)?.Name ?? kvp.Key.ToString();
                return $"{label} ({kvp.Value.Count})";
            }).OrderBy(s => s);
            return string.Join(", ", parts);
        }
    }
}
