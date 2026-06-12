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

        /// <summary>Fast, approximate layout: size viewports from the crop box instead of measuring placed outlines.</summary>
        public bool            EstimateMode   { get; set; } = false;

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

                    // The drawing area is identical for every sheet (same title block), so resolve
                    // it once and reuse — in estimate mode this is the ONLY explicit regen.
                    bool   areaKnown = false;
                    double areaMinX = 0, areaMinY = 0, areaW = 0, areaH = 0;

                    Log(EstimateMode
                        ? "Quick estimate — sizing views from their crop boxes."
                        : "Accurate — measuring each placed view.", "info");

                    // One sheet at a time: progress updates per sheet and (accurate mode) each regen
                    // only touches that sheet's changes, so the commit at the end is cheap.
                    for (int i = 0; i < ParentViewIds.Count; i++)
                    {
                        var parent = doc.GetElement(ParentViewIds[i]) as View;
                        if (parent == null)
                        {
                            Log("[SKIP] A selected view no longer exists.", "warn");
                            LemoineLog.Warn("PlaceDependentViews", $"Parent view {ParentViewIds[i]} not found.");
                            skip++;
                            onProgress(Pct(i + 1, total), pass, fail, skip);
                            continue;
                        }

                        var depIds = parent.GetDependentViewIds()?.ToList() ?? new List<ElementId>();
                        if (depIds.Count == 0)
                        {
                            Log($"[SKIP] '{parent.Name}' has no dependent views.", "warn");
                            skip++;
                            onProgress(Pct(i + 1, total), pass, fail, skip);
                            continue;
                        }

                        // ── Sheet number / name ──────────────────────────────
                        string? sheetNumber = NextFreeNumber(ref number, NumberPrefix, NumberSuffix, usedNumbers);
                        if (sheetNumber == null)
                        {
                            Log($"[FAIL] Could not find a free sheet number for '{parent.Name}'.", "fail");
                            fail++;
                            onProgress(Pct(i + 1, total), pass, fail, skip);
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
                            onProgress(Pct(i + 1, total), pass, fail, skip);
                            continue;
                        }

                        // ── Sheet Series parameter ───────────────────────────
                        if (!string.IsNullOrWhiteSpace(SheetSeries))
                            WriteSeries(sheet, ref seriesWarned);

                        // ── Trim each dependent's annotation crop ────────────
                        if (TrimBubbles)
                            foreach (var depId in depIds)
                                TrimAnnotationCrop(doc.GetElement(depId) as View, trimPaperFt);

                        // ── Which dependents can actually be placed ──────────
                        var toPlace = new List<View>(depIds.Count);
                        foreach (var depId in depIds)
                        {
                            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, depId))
                            {
                                var dv0 = doc.GetElement(depId) as View;
                                Log($"[SKIP] '{dv0?.Name ?? depId.ToString()}' is already placed on a sheet.", "warn");
                                skip++;
                                continue;
                            }
                            if (doc.GetElement(depId) is View dv) toPlace.Add(dv);
                        }
                        if (toPlace.Count == 0)
                        {
                            Log($"[WARN] Nothing to place on sheet {sheetNumber} — {sheetName}.", "warn");
                            onProgress(Pct(i + 1, total), pass, fail, skip);
                            continue;
                        }

                        int placedCount = 0;

                        if (EstimateMode)
                        {
                            // Resolve the area once (one regen so the title-block bbox is valid).
                            if (!areaKnown)
                            {
                                doc.Regenerate();
                                areaKnown = TryGetDrawingArea(doc, sheet, marginLeft, marginRight, marginTop, marginBottom,
                                                             out areaMinX, out areaMinY, out areaW, out areaH);
                            }
                            if (!areaKnown)
                            {
                                Log($"[WARN] Could not read title-block size; sheet {sheetNumber} left empty.", "warn");
                                LemoineLog.Warn("PlaceDependentViews", $"No title-block bbox on sheet {sheetNumber}.");
                                pass++;
                                onProgress(Pct(i + 1, total), pass, fail, skip);
                                continue;
                            }

                            // Estimate footprints from the crop box, pack, place directly — no regen.
                            var rects = toPlace.Select(v => EstimateRect(v, TrimBubbles, trimPaperFt)).ToList();
                            var result = SheetLayoutPacker.Pack(rects, areaW, areaH, gap);
                            for (int k = 0; k < toPlace.Count; k++)
                            {
                                var p = result.Placements[k];
                                try
                                {
                                    Viewport.Create(doc, sheet.Id, toPlace[k].Id,
                                                    new XYZ(areaMinX + p.CenterX, areaMinY + p.CenterY, 0));
                                    placedCount++;
                                }
                                catch (Exception ex)
                                {
                                    Log($"[FAIL] Could not place '{toPlace[k].Name}': {ex.Message}", "fail");
                                    LemoineLog.Swallowed("PlaceDependentViews: Viewport.Create (estimate)", ex);
                                    fail++;
                                }
                            }
                            if (result.Overflow)
                                Log($"[WARN] Sheet {sheetNumber}: estimated views don't fit the drawing area — " +
                                    "they may extend past the margins.", "warn");
                        }
                        else
                        {
                            // Accurate: place provisionally, regen this sheet, measure, pack, position.
                            var placed = new List<Viewport>(toPlace.Count);
                            foreach (var dv in toPlace)
                            {
                                try { placed.Add(Viewport.Create(doc, sheet.Id, dv.Id, XYZ.Zero)); }
                                catch (Exception ex)
                                {
                                    Log($"[FAIL] Could not place '{dv.Name}': {ex.Message}", "fail");
                                    LemoineLog.Swallowed("PlaceDependentViews: Viewport.Create", ex);
                                    fail++;
                                }
                            }
                            if (placed.Count == 0)
                            {
                                Log($"[WARN] Nothing placed on sheet {sheetNumber} — {sheetName}.", "warn");
                                onProgress(Pct(i + 1, total), pass, fail, skip);
                                continue;
                            }

                            doc.Regenerate(); // outlines are only valid after a regen

                            if (!areaKnown)
                                areaKnown = TryGetDrawingArea(doc, sheet, marginLeft, marginRight, marginTop, marginBottom,
                                                             out areaMinX, out areaMinY, out areaW, out areaH);
                            if (!areaKnown)
                            {
                                Log($"[WARN] Could not read title-block size for sheet {sheetNumber}; " +
                                    "views left at the sheet origin.", "warn");
                                LemoineLog.Warn("PlaceDependentViews", $"No title-block bbox on sheet {sheetNumber}.");
                                pass++;
                                onProgress(Pct(i + 1, total), pass, fail, skip);
                                continue;
                            }

                            var rects = new List<SheetLayoutPacker.Rect>(placed.Count);
                            var keep  = new List<Viewport>(placed.Count);
                            foreach (var vp in placed)
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
                                    Log($"[WARN] Sheet {sheetNumber}: {keep.Count} view(s) don't fit the drawing " +
                                        "area at this scale — they extend past the margins.", "warn");
                            }
                            placedCount = keep.Count;
                        }

                        pass++;
                        Log($"✓ Sheet {sheetNumber} — {sheetName}  ({placedCount} view(s) placed)", "pass");
                        onProgress(Pct(i + 1, total), pass, fail, skip);
                    }

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
            finally
            {
                // Session-long static handler — drop the run's payload.
                ParentViewIds = new List<ElementId>();
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

        /// <summary>Per-side paper pad assumed for un-trimmed views (rough bubble allowance) in estimate mode.</summary>
        private const double DefaultAnnotationPadIn = 0.5;

        /// <summary>
        /// Approximates a viewport's on-sheet footprint from the view's crop box and scale, without
        /// placing or regenerating. When trimming we know the annotation gap exactly; otherwise a
        /// default pad stands in for bubbles/section heads so the estimate isn't grossly undersized.
        /// </summary>
        private static SheetLayoutPacker.Rect EstimateRect(View v, bool trim, double trimPaperFt)
        {
            double scale = v.Scale > 0 ? v.Scale : 1.0;
            BoundingBoxXYZ cb = v.CropBox;
            double wPaper = Math.Abs(cb.Max.X - cb.Min.X) / scale;
            double hPaper = Math.Abs(cb.Max.Y - cb.Min.Y) / scale;
            double pad = trim ? trimPaperFt : (DefaultAnnotationPadIn / 12.0);
            return new SheetLayoutPacker.Rect(wPaper + 2 * pad, hPaper + 2 * pad);
        }

        /// <summary>Writes the Sheet Series value to the named sheet parameter; warns once if it's missing or read-only.</summary>
        private void WriteSeries(ViewSheet sheet, ref bool warned)
        {
            // LookupParameter returns only the FIRST name match and silently picks the wrong one
            // when duplicates exist (a shared "Sheet Series" is rarely the first). GetParameters
            // returns all matches, so prefer the shared, writable, text one — else any writable text.
            var matches = sheet.GetParameters(SeriesParamName);
            Parameter? p =
                matches.FirstOrDefault(x => x != null && !x.IsReadOnly && x.StorageType == StorageType.String && x.IsShared)
                ?? matches.FirstOrDefault(x => x != null && !x.IsReadOnly && x.StorageType == StorageType.String);

            if (p == null)
            {
                if (!warned)
                {
                    warned = true;
                    Log($"[WARN] No writable text sheet parameter named '{SeriesParamName}' — series value skipped.", "warn");
                    LemoineLog.Warn("PlaceDependentViews",
                        $"Series param '{SeriesParamName}' not found as a writable text parameter on sheets.");
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
