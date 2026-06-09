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
                        string? sheetNumber = NextFreeNumber(doc, ref number);
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

                        // ── Trim each dependent's annotation crop ────────────
                        if (TrimBubbles)
                            foreach (var depId in depIds)
                                TrimAnnotationCrop(doc.GetElement(depId) as View, trimPaperFt);

                        doc.Regenerate();

                        // ── Usable drawing area from the title block bbox ────
                        if (!TryGetDrawingArea(doc, sheet, marginLeft, marginRight, marginTop, marginBottom,
                                               out double areaMinX, out double areaMinY, out double areaW, out double areaH))
                        {
                            Log($"[WARN] Could not read title-block size for sheet {sheetNumber}; " +
                                "dependents left unplaced.", "warn");
                            LemoineLog.Warn("PlaceDependentViews", $"No title-block bbox on sheet {sheetNumber}.");
                            // Sheet still created; count it as a partial pass with a warning.
                            pass++;
                            onProgress(Pct(i + 1, total), pass, fail, skip);
                            continue;
                        }

                        // ── Place viewports (provisional center) ─────────────
                        var areaCenter = new XYZ(areaMinX + areaW / 2.0, areaMinY + areaH / 2.0, 0);
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
                                placed.Add(Viewport.Create(doc, sheet.Id, depId, areaCenter));
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
                            onProgress(Pct(i + 1, total), pass, fail, skip);
                            continue;
                        }

                        doc.Regenerate(); // outlines are only valid after a regen

                        // ── Measure real footprints and pack ─────────────────
                        var rects   = new List<SheetLayoutPacker.Rect>(placed.Count);
                        var keep    = new List<Viewport>(placed.Count);
                        foreach (var vp in placed)
                        {
                            if (TryGetOutlineSize(vp, out double w, out double h))
                            {
                                rects.Add(new SheetLayoutPacker.Rect(w, h));
                                keep.Add(vp);
                            }
                            else
                            {
                                Log("[WARN] A viewport reported no size and was left at the sheet center.", "warn");
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
                            doc.Regenerate();

                            if (result.Overflow)
                                Log($"[WARN] Sheet {sheetNumber}: {keep.Count} view(s) don't fit the drawing " +
                                    "area at this scale — they extend past the margins.", "warn");
                        }

                        pass++;
                        Log($"✓ Sheet {sheetNumber} — {sheetName}  ({keep.Count} view(s) placed)", "pass");
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

        /// <summary>Advances <paramref name="number"/> past any existing sheet numbers and returns the first free one.</summary>
        private static string? NextFreeNumber(Document doc, ref int number)
        {
            var existing = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>().Select(vs => vs.SheetNumber),
                StringComparer.OrdinalIgnoreCase);

            for (int guard = 0; guard < 100000; guard++)
            {
                string candidate = number.ToString();
                number++;
                if (!existing.Contains(candidate)) return candidate;
            }
            return null;
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
