using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Testing.PlaceDependentViews
{
    /// <summary>
    /// Creates one sheet per selected parent view and places that view's dependents on
    /// it — trimmed (annotation crop) and packed without overlap, centered with even
    /// edge spacing. Runs on the Revit API thread; set all inputs before Raise().
    /// </summary>
    public sealed class PlaceDependentViewsEventHandler : IExternalEventHandler
    {
        // ── Inputs set by the view model before Raise() ───────────────────────
        public List<ElementId> ParentViewIds  { get; set; } = new List<ElementId>();
        public ElementId       TitleBlockTypeId { get; set; } = ElementId.InvalidElementId;
        public int             StartingNumber { get; set; } = 1;
        public string          NamingPattern  { get; set; } = "{ParentViewName}";
        public string          NumberPrefix   { get; set; } = "";
        public string          NumberSuffix   { get; set; } = "";
        public string          SheetSeries    { get; set; } = "";
        public string          SeriesParamName { get; set; } = "Sheet Series";

        public bool   TrimBubbles { get; set; } = true;
        public double TrimInches  { get; set; } = 0.125;   // paper inches past the model crop

        public double MarginTopIn    { get; set; } = 0.5;  // all paper inches
        public double MarginBottomIn { get; set; } = 0.5;
        public double MarginLeftIn   { get; set; } = 0.5;
        public double MarginRightIn  { get; set; } = 0.5;
        public double GapIn          { get; set; } = 0.25;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Testing.PlaceDependentViews";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        public void Execute(UIApplication app)
        {
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            int pass = 0, fail = 0, skip = 0;
            long issues0 = LemoineLog.IssueCount;

            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    Log("No active project — open a document and try again.", "fail");
                    onComplete(0, 1, 0);
                    return;
                }
                if (TitleBlockTypeId == ElementId.InvalidElementId)
                {
                    Log("No title block selected.", "fail");
                    onComplete(0, 1, 0);
                    return;
                }
                if (ParentViewIds.Count == 0)
                {
                    Log("No views selected.", "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                double marginTop    = MarginTopIn    / 12.0;
                double marginBottom = MarginBottomIn / 12.0;
                double marginLeft   = MarginLeftIn   / 12.0;
                double marginRight  = MarginRightIn  / 12.0;
                double gap          = Math.Max(0, GapIn) / 12.0;
                double trimPaperFt  = Math.Max(0, TrimInches) / 12.0;

                int total = ParentViewIds.Count;

                using (var tx = new Transaction(doc, "Lemoine — Place Dependent Views"))
                {
                    ConfigureFailures(tx);
                    tx.Start();

                    int number = StartingNumber;
                    bool seriesWarned = false;

                    // Existing sheet numbers, built once; NextFreeNumber adds to it as we go so the
                    // O(N²) "rescan every sheet" cost is gone.
                    var usedNumbers = new HashSet<string>(
                        new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
                            .Cast<ViewSheet>().Select(vs => vs.SheetNumber),
                        StringComparer.OrdinalIgnoreCase);

                    // ── Phase 1: create sheets, trim, place viewports — NO regen ──────────
                    // doc.Regenerate() recomputes the whole model, so calling it per sheet is the
                    // dominant cost. We do all the mutations first, then regenerate ONCE.
                    var jobs = new List<SheetJob>();
                    for (int i = 0; i < ParentViewIds.Count; i++)
                    {
                        var parent = doc.GetElement(ParentViewIds[i]) as View;
                        if (parent == null)
                        {
                            Log("[SKIP] A selected view no longer exists.", "warn");
                            LemoineLog.Warn("PlaceDependentViews", $"Parent view {ParentViewIds[i]} not found.");
                            skip++;
                            onProgress(Pct(i + 1, total) / 2, pass, fail, skip);
                            continue;
                        }

                        var depIds = parent.GetDependentViewIds()?.ToList() ?? new List<ElementId>();
                        if (depIds.Count == 0)
                        {
                            Log($"[SKIP] '{parent.Name}' has no dependent views.", "warn");
                            skip++;
                            onProgress(Pct(i + 1, total) / 2, pass, fail, skip);
                            continue;
                        }

                        // ── Sheet number / name ──────────────────────────────
                        string? sheetNumber = NextFreeNumber(ref number, NumberPrefix, NumberSuffix, usedNumbers);
                        if (sheetNumber == null)
                        {
                            Log($"[FAIL] Could not find a free sheet number for '{parent.Name}'.", "fail");
                            fail++;
                            onProgress(Pct(i + 1, total) / 2, pass, fail, skip);
                            continue;
                        }
                        string sheetName = LemoineTokenInput.Resolve(
                            NamingPattern, BuildTokens(parent, doc, sheetNumber));

                        ViewSheet sheet;
                        try
                        {
                            sheet = ViewSheet.Create(doc, TitleBlockTypeId);
                            sheet.get_Parameter(BuiltInParameter.SHEET_NUMBER)?.Set(sheetNumber);
                            sheet.get_Parameter(BuiltInParameter.SHEET_NAME)?.Set(sheetName);
                        }
                        catch (Exception ex)
                        {
                            Log($"[FAIL] Could not create sheet for '{parent.Name}': {ex.Message}", "fail");
                            LemoineLog.Error("PlaceDependentViews: create sheet", ex);
                            fail++;
                            onProgress(Pct(i + 1, total) / 2, pass, fail, skip);
                            continue;
                        }

                        // ── Sheet Series parameter ───────────────────────────
                        if (!string.IsNullOrWhiteSpace(SheetSeries))
                            WriteSeries(sheet, ref seriesWarned);

                        // ── Trim each dependent's annotation crop ────────────
                        if (TrimBubbles)
                            foreach (var depId in depIds)
                                TrimAnnotationCrop(doc.GetElement(depId) as View, trimPaperFt);

                        // ── Place viewports at a provisional point (repositioned in phase 2) ──
                        var placed = new List<Viewport>();
                        foreach (var depId in depIds)
                        {
                            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, depId))
                            {
                                var dv = doc.GetElement(depId) as View;
                                Log($"[SKIP] '{dv?.Name ?? depId.ToString()}' is already placed on a sheet.", "warn");
                                skip++;
                                continue;
                            }
                            try
                            {
                                placed.Add(Viewport.Create(doc, sheet.Id, depId, XYZ.Zero));
                            }
                            catch (Exception ex)
                            {
                                var dv = doc.GetElement(depId) as View;
                                Log($"[FAIL] Could not place '{dv?.Name ?? depId.ToString()}': {ex.Message}", "fail");
                                LemoineLog.Swallowed("PlaceDependentViews: Viewport.Create", ex);
                                fail++;
                            }
                        }

                        if (placed.Count == 0)
                        {
                            Log($"[WARN] Nothing placed on sheet {sheetNumber} — {sheetName}.", "warn");
                            onProgress(Pct(i + 1, total) / 2, pass, fail, skip);
                            continue;
                        }

                        jobs.Add(new SheetJob(sheet, sheetNumber, sheetName, placed));
                        onProgress(Pct(i + 1, total) / 2, pass, fail, skip);
                    }

                    // ── Single regen: trims + sheets + title blocks + viewports all settle here ──
                    doc.Regenerate();

                    // ── Phase 2: measure real footprints, pack, position ─────────────────
                    for (int j = 0; j < jobs.Count; j++)
                    {
                        var job = jobs[j];

                        if (!TryGetDrawingArea(doc, job.Sheet, marginLeft, marginRight, marginTop, marginBottom,
                                               out double areaMinX, out double areaMinY, out double areaW, out double areaH))
                        {
                            Log($"[WARN] Could not read title-block size for sheet {job.Number}; " +
                                "dependents left at the sheet origin.", "warn");
                            LemoineLog.Warn("PlaceDependentViews", $"No title-block bbox on sheet {job.Number}.");
                            pass++;
                            onProgress(50 + Pct(j + 1, jobs.Count) / 2, pass, fail, skip);
                            continue;
                        }

                        var rects = new List<SheetLayoutPacker.Rect>(job.Viewports.Count);
                        var keep  = new List<Viewport>(job.Viewports.Count);
                        foreach (var vp in job.Viewports)
                        {
                            if (TryGetOutlineSize(vp, out double w, out double h))
                            {
                                rects.Add(new SheetLayoutPacker.Rect(w, h));
                                keep.Add(vp);
                            }
                            else
                            {
                                Log("[WARN] A viewport reported no size and was left at the sheet origin.", "warn");
                            }
                        }

                        if (keep.Count > 0)
                        {
                            var result = SheetLayoutPacker.Pack(rects, areaW, areaH, gap);
                            for (int k = 0; k < keep.Count; k++)
                            {
                                var p = result.Placements[k];
                                keep[k].SetBoxCenter(new XYZ(areaMinX + p.CenterX, areaMinY + p.CenterY, 0));
                            }

                            if (result.Overflow)
                                Log($"[WARN] Sheet {job.Number}: {keep.Count} view(s) don't fit the drawing " +
                                    "area at this scale — they extend past the margins.", "warn");
                        }

                        pass++;
                        Log($"✓ Sheet {job.Number} — {job.Name}  ({keep.Count} view(s) placed)", "pass");
                        onProgress(50 + Pct(j + 1, jobs.Count) / 2, pass, fail, skip);
                    }
                    // SetBoxCenter needs no regen here — the commit below recomputes everything once.

                    tx.Commit();
                }

                long issues = LemoineLog.IssuesSince(issues0);
                if (issues > 0)
                    Log($"{issues} non-fatal issue(s) recorded — see diagnostics log.", "warn");

                onProgress(100, pass, fail, skip);
                onComplete(pass, fail, skip);
            }
            catch (Exception ex)
            {
                Log($"Place Dependent Views error: {ex.Message}", "fail");
                LemoineLog.Error("PlaceDependentViews.Execute", ex);
                onComplete(pass, fail + 1, skip);
            }
        }

        // ── Annotation crop trimming ──────────────────────────────────────────
        private static void TrimAnnotationCrop(View view, double trimPaperFt)
        {
            if (view == null) return;
            try
            {
                if (view.Scale <= 0) return;                 // perspective / no defined scale — can't convert
                view.CropBoxActive = true;

                var annoParam = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (annoParam != null && !annoParam.IsReadOnly) annoParam.Set(1);

                var mgr = view.GetCropRegionShapeManager();
                if (mgr == null) return;

                // Offsets are model feet; convert the paper gap with the view scale so the
                // on-sheet trim is constant. Floor to a tiny positive value (Revit rejects 0).
                double offset = Math.Max(trimPaperFt * view.Scale, 1.0 / 64.0 / 12.0 * view.Scale);
                if (offset <= 0) offset = 1e-3;

                mgr.TopAnnotationCropOffset    = offset;
                mgr.BottomAnnotationCropOffset = offset;
                mgr.LeftAnnotationCropOffset   = offset;
                mgr.RightAnnotationCropOffset  = offset;
            }
            catch (Exception ex)
            {
                // Non-fatal — place the view untrimmed rather than aborting the whole run.
                LemoineLog.Swallowed($"PlaceDependentViews: trim annotation crop on view {view.Id.Value}", ex);
            }
        }

        // ── Drawing area from the placed title block ──────────────────────────
        private static bool TryGetDrawingArea(
            Document doc, ViewSheet sheet,
            double mLeft, double mRight, double mTop, double mBottom,
            out double minX, out double minY, out double w, out double h)
        {
            minX = minY = w = h = 0;

            var tb = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstElement();
            if (tb == null) return false;

            BoundingBoxXYZ bb = tb.get_BoundingBox(sheet);
            if (bb == null) return false;

            minX = bb.Min.X + mLeft;
            minY = bb.Min.Y + mBottom;
            w    = (bb.Max.X - bb.Min.X) - mLeft - mRight;
            h    = (bb.Max.Y - bb.Min.Y) - mTop  - mBottom;
            return w > 0 && h > 0;
        }

        private static bool TryGetOutlineSize(Viewport vp, out double w, out double h)
        {
            w = h = 0;
            try
            {
                var o = vp.GetBoxOutline();
                if (o == null) return false;
                w = o.MaximumPoint.X - o.MinimumPoint.X;
                h = o.MaximumPoint.Y - o.MinimumPoint.Y;
                return w > 1e-9 && h > 1e-9;
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed($"PlaceDependentViews: read viewport outline {vp.Id.Value}", ex);
                return false;
            }
        }

        // ── Naming ────────────────────────────────────────────────────────────
        private static Dictionary<string, string> BuildTokens(View parent, Document doc, string sheetNumber)
        {
            string level = "";
            try { level = parent.GenLevel?.Name ?? ""; }
            catch (Exception ex) { LemoineLog.Swallowed("PlaceDependentViews: read GenLevel", ex); }

            return new Dictionary<string, string>
            {
                ["ParentViewName"] = parent.Name ?? "",
                ["ViewType"]       = parent.ViewType.ToString(),
                ["Level"]          = level,
                ["SheetNumber"]    = sheetNumber,
            };
        }

        /// <summary>
        /// Advances <paramref name="number"/> past any used sheet numbers and returns the first free
        /// fully-formatted number (<c>prefix + running-number + suffix</c>), reserving it in
        /// <paramref name="used"/> so later sheets in the same run don't collide.
        /// </summary>
        private static string? NextFreeNumber(ref int number, string prefix, string suffix, HashSet<string> used)
        {
            for (int guard = 0; guard < 100000; guard++)
            {
                string candidate = (prefix ?? "") + number.ToString() + (suffix ?? "");
                number++;
                if (used.Add(candidate)) return candidate;   // Add is false when already present
            }
            return null;
        }

        /// <summary>One created sheet plus the viewports placed on it, carried from phase 1 to phase 2.</summary>
        private sealed class SheetJob
        {
            public ViewSheet      Sheet     { get; }
            public string         Number    { get; }
            public string         Name      { get; }
            public List<Viewport> Viewports { get; }
            public SheetJob(ViewSheet sheet, string number, string name, List<Viewport> viewports)
            {
                Sheet = sheet; Number = number; Name = name; Viewports = viewports;
            }
        }

        /// <summary>Writes the Sheet Series value to the named sheet parameter; warns once if it's missing or read-only.</summary>
        private void WriteSeries(ViewSheet sheet, ref bool warned)
        {
            var p = sheet.LookupParameter(SeriesParamName);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.String)
            {
                if (!warned)
                {
                    warned = true;
                    Log($"[WARN] Sheet parameter '{SeriesParamName}' not found or not writable — " +
                        "series value skipped.", "warn");
                    LemoineLog.Warn("PlaceDependentViews", $"Series param '{SeriesParamName}' missing/not writable.");
                }
                return;
            }
            p.Set(SheetSeries);
        }

        private static int Pct(int done, int total) => total > 0 ? (int)(done * 100.0 / total) : 100;

        // ── Failure handling (report distinct warnings, then resolve) ─────────
        private void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            opts.SetFailuresPreprocessor(new SuppressWarningsPreprocessor(PushLog));
            tx.SetFailureHandlingOptions(opts);
        }

        private sealed class SuppressWarningsPreprocessor : IFailuresPreprocessor
        {
            private readonly Action<string, string>? _log;
            private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
            public SuppressWarningsPreprocessor(Action<string, string>? log) { _log = log; }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                foreach (var msg in fa.GetFailureMessages()
                                      .Where(m => m.GetSeverity() == FailureSeverity.Warning))
                {
                    string desc;
                    try { desc = msg.GetDescriptionText(); }
                    catch (Exception ex)
                    {
                        LemoineLog.Swallowed("PlaceDependentViews: read warning text", ex);
                        desc = "(unreadable warning)";
                    }
                    if (_seen.Add(desc))
                    {
                        _log?.Invoke($"[warning] {desc}", "warn");
                        LemoineLog.Warn("PlaceDependentViews", desc);
                    }
                    fa.DeleteWarning(msg);
                }
                return FailureProcessingResult.Continue;
            }
        }
    }
}
