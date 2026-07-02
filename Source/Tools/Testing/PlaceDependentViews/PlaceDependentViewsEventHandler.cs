using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Testing.PlaceDependentViews
{
    /// <summary>How the selected views are turned into sheets.</summary>
    public enum PlaceViewsMode
    {
        /// <summary>One sheet per parent view, holding that view's dependent views.</summary>
        DependentsPerParent,
        /// <summary>One sheet per source view, holding the view itself (anchored top- or
        /// left-center) plus every callout/section/elevation visible in it.</summary>
        CompositeOneSheet,
    }

    /// <summary>How placed viewports are sized for layout.</summary>
    public enum LayoutMode
    {
        /// <summary>Place every view, regen per sheet, and measure each viewport's real outline.</summary>
        Measured,
        /// <summary>Measure the first view of each identical size (crop+scale) group, then reuse that
        /// footprint for the rest — far fewer regens when many views match. Requires trim on.</summary>
        Grouped,
        /// <summary>Size every view from its crop box and scale; no measure regen. Fast, approximate.</summary>
        Estimate,
    }

    /// <summary>
    /// Creates one sheet per selected parent view and places either that view's
    /// dependents (packed without overlap, centered) or — in composite mode — the view
    /// itself plus its visible callouts/sections/elevations (source anchored top- or
    /// left-center, sub views in aligned rows/columns). Sub views are trimmed
    /// (annotation crop) when enabled; the composite source view never is.
    /// Runs on the Revit API thread; set all inputs before Raise().
    /// </summary>
    public sealed class PlaceDependentViewsEventHandler : IExternalEventHandler
    {
        // ── Inputs set by the view model before Raise() ───────────────────────
        public PlaceViewsMode  Mode           { get; set; } = PlaceViewsMode.DependentsPerParent;
        public List<ElementId> ParentViewIds  { get; set; } = new List<ElementId>();
        public ElementId       TitleBlockTypeId { get; set; } = ElementId.InvalidElementId;
        public int             StartingNumber { get; set; } = 1;
        public string          NamingPattern  { get; set; } = "{ParentViewName}";
        public string          NumberPrefix   { get; set; } = "";
        public string          NumberSuffix   { get; set; } = "";
        public string          SheetSeries    { get; set; } = "";
        public string          SeriesParamName { get; set; } = "Sheet Series";

        /// <summary>How placed viewports are sized for layout (measured / grouped / estimate).</summary>
        public LayoutMode      Layout         { get; set; } = LayoutMode.Measured;

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
                    Log(LemoineStrings.T("testing.placeDependentViews.log.noDoc"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }
                if (TitleBlockTypeId == ElementId.InvalidElementId)
                {
                    Log(LemoineStrings.T("testing.placeDependentViews.log.noTitleBlock"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }
                if (ParentViewIds.Count == 0)
                {
                    Log(LemoineStrings.T("testing.placeDependentViews.log.noViews"), "fail");
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

                    // Grouped layout only reliably predicts a view's real outline when the annotation
                    // crop is uniform across a group — which only trimming guarantees. Without trim the
                    // key is approximate, so fall back to a full measured run rather than mis-size silently.
                    LayoutMode layout = Layout;
                    if (layout == LayoutMode.Grouped && !TrimBubbles)
                    {
                        Log(LemoineStrings.T("testing.placeDependentViews.log.groupedNeedsTrim"), "warn");
                        LemoineLog.Warn("PlaceDependentViews", "Grouped layout requested with trim off — fell back to measured.");
                        layout = LayoutMode.Measured;
                    }

                    // Run-level cache of measured footprints, keyed by (cropWidth, cropHeight, scale).
                    // The first view of each group is measured for real; the rest reuse its size.
                    var sizeCache = new Dictionary<(long, long, int), (double w, double h)>();

                    Log(layout == LayoutMode.Estimate
                        ? LemoineStrings.T("testing.placeDependentViews.log.announceEstimate")
                        : layout == LayoutMode.Grouped
                            ? LemoineStrings.T("testing.placeDependentViews.log.announceGrouped")
                            : LemoineStrings.T("testing.placeDependentViews.log.announceAccurate"), "info");

                    bool composite = Mode == PlaceViewsMode.CompositeOneSheet;

                    // Composite mode lookups, built once: section/callout markers resolve
                    // to views by their (unique) view name, and views already on a sheet
                    // are pre-read so a placed source fails before its sheet is created.
                    Dictionary<string, View>? viewsByName  = null;
                    HashSet<ElementId>?       placedViewIds = null;
                    if (composite)
                    {
                        viewsByName = new Dictionary<string, View>(StringComparer.Ordinal);
                        foreach (var v in new FilteredElementCollector(doc)
                                     .OfClass(typeof(View)).Cast<View>())
                        {
                            if (!v.IsTemplate && !viewsByName.ContainsKey(v.Name))
                                viewsByName[v.Name] = v;
                        }
                        placedViewIds = new HashSet<ElementId>(
                            new FilteredElementCollector(doc).OfClass(typeof(Viewport))
                                .Cast<Viewport>().Select(vp => vp.ViewId));
                    }

                    // One sheet at a time: progress updates per sheet and (accurate mode) each regen
                    // only touches that sheet's changes, so the commit at the end is cheap.
                    for (int i = 0; i < ParentViewIds.Count; i++)
                    {
                        if (LemoineRun.CancelRequested)
                        {
                            Log(LemoineStrings.T("testing.placeDependentViews.log.stoppedByUser", i, total), "warn");
                            break;   // falls through to tx.Commit() below
                        }
                        var parent = doc.GetElement(ParentViewIds[i]) as View;
                        if (parent == null)
                        {
                            Log(LemoineStrings.T("testing.placeDependentViews.log.selectedGone"), "warn");
                            LemoineLog.Warn("PlaceDependentViews", $"Parent view {ParentViewIds[i]} not found.");
                            skip++;
                            onProgress(Pct(i + 1, total), pass, fail, skip);
                            continue;
                        }

                        // Views going onto this sheet, in placement order. Composite mode
                        // puts the source view first — the layout anchors it.
                        View? sourceView = null;
                        List<ElementId> candidateIds;

                        if (composite)
                        {
                            if (placedViewIds!.Contains(parent.Id))
                            {
                                Log(LemoineStrings.T("testing.placeDependentViews.log.alreadyPlaced", parent.Name), "fail");
                                fail++;
                                onProgress(Pct(i + 1, total), pass, fail, skip);
                                continue;
                            }

                            var subs = DiscoverSubViews(doc, parent, viewsByName!);
                            if (subs.Count == 0)
                            {
                                Log(LemoineStrings.T("testing.placeDependentViews.log.noVisibleMarkers", parent.Name), "warn");
                                skip++;
                                onProgress(Pct(i + 1, total), pass, fail, skip);
                                continue;
                            }

                            sourceView   = parent;
                            candidateIds = new List<ElementId>(subs.Count + 1) { parent.Id };
                            candidateIds.AddRange(subs.Select(s => s.Id));
                        }
                        else
                        {
                            candidateIds = parent.GetDependentViewIds()?.ToList() ?? new List<ElementId>();
                            if (candidateIds.Count == 0)
                            {
                                Log(LemoineStrings.T("testing.placeDependentViews.log.noDependents", parent.Name), "warn");
                                skip++;
                                onProgress(Pct(i + 1, total), pass, fail, skip);
                                continue;
                            }
                        }

                        // ── Sheet number / name ──────────────────────────────
                        string? sheetNumber = NextFreeNumber(ref number, NumberPrefix, NumberSuffix, usedNumbers);
                        if (sheetNumber == null)
                        {
                            Log(LemoineStrings.T("testing.placeDependentViews.log.noFreeNumber", parent.Name), "fail");
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
                            Log(LemoineStrings.T("testing.placeDependentViews.log.createSheetFail", parent.Name, ex.Message), "fail");
                            LemoineLog.Error("PlaceDependentViews: create sheet", ex);
                            fail++;
                            onProgress(Pct(i + 1, total), pass, fail, skip);
                            continue;
                        }

                        // ── Sheet Series parameter ───────────────────────────
                        if (!string.IsNullOrWhiteSpace(SheetSeries))
                            WriteSeries(sheet, ref seriesWarned);

                        // ── Trim each placed view's annotation crop ──────────
                        // The composite source view is never trimmed — its callout
                        // boundaries and section heads must stay visible.
                        if (TrimBubbles)
                            foreach (var depId in candidateIds)
                            {
                                if (sourceView != null && depId == sourceView.Id) continue;
                                if (doc.GetElement(depId) is View depView)
                                    TrimAnnotationCrop(depView, trimPaperFt);
                            }

                        // ── Which views can actually be placed ───────────────
                        var toPlace = new List<View>(candidateIds.Count);
                        foreach (var depId in candidateIds)
                        {
                            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, depId))
                            {
                                var dv0 = doc.GetElement(depId) as View;
                                Log(LemoineStrings.T("testing.placeDependentViews.log.cantPlaceView", dv0?.Name ?? depId.ToString()), "warn");
                                skip++;
                                continue;
                            }
                            if (doc.GetElement(depId) is View dv) toPlace.Add(dv);
                        }
                        if (sourceView != null && (toPlace.Count == 0 || toPlace[0].Id != sourceView.Id))
                        {
                            Log(LemoineStrings.T("testing.placeDependentViews.log.sourceCantPlace", sourceView.Name, sheetNumber), "fail");
                            fail++;
                            onProgress(Pct(i + 1, total), pass, fail, skip);
                            continue;
                        }
                        if (toPlace.Count == 0)
                        {
                            Log(LemoineStrings.T("testing.placeDependentViews.log.nothingToPlace", sheetNumber, sheetName), "warn");
                            onProgress(Pct(i + 1, total), pass, fail, skip);
                            continue;
                        }

                        int placedCount = 0;

                        if (layout == LayoutMode.Estimate)
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
                                Log(LemoineStrings.T("testing.placeDependentViews.log.noTbSizeEstimate", sheetNumber), "warn");
                                LemoineLog.Warn("PlaceDependentViews", $"No title-block bbox on sheet {sheetNumber}.");
                                pass++;
                                onProgress(Pct(i + 1, total), pass, fail, skip);
                                continue;
                            }

                            // Estimate footprints from the crop box, lay out, place directly — no
                            // regen. The untrimmed composite source gets the default annotation pad.
                            var rects = toPlace
                                .Select(v => EstimateRect(
                                    v,
                                    TrimBubbles && (sourceView == null || v.Id != sourceView.Id),
                                    trimPaperFt))
                                .ToList();
                            var (centers, overflow) = LayOutSheet(rects, sourceView != null,
                                                                  areaW, areaH, gap, sheetNumber);
                            for (int k = 0; k < toPlace.Count; k++)
                            {
                                var p = centers[k];
                                try
                                {
                                    Viewport.Create(doc, sheet.Id, toPlace[k].Id,
                                                    new XYZ(areaMinX + p.CenterX, areaMinY + p.CenterY, 0));
                                    placedCount++;
                                }
                                catch (Exception ex)
                                {
                                    Log(LemoineStrings.T("testing.placeDependentViews.log.placeFail", toPlace[k].Name, ex.Message), "fail");
                                    LemoineLog.Swallowed("PlaceDependentViews: Viewport.Create (estimate)", ex);
                                    fail++;
                                }
                            }
                            if (overflow)
                                Log(LemoineStrings.T("testing.placeDependentViews.log.overflowEstimate", sheetNumber), "warn");
                        }
                        else
                        {
                            // Accurate / grouped: place provisionally, then size each viewport. In grouped
                            // mode a view whose (crop+scale) group is already cached reuses that measured
                            // footprint; only unseen groups (and uncacheable views) are measured — so a sheet
                            // with no new groups skips the regen entirely.
                            bool grouped = layout == LayoutMode.Grouped;
                            var recs = new List<PlaceRec>(toPlace.Count);
                            foreach (var dv in toPlace)
                            {
                                Viewport vp;
                                try { vp = Viewport.Create(doc, sheet.Id, dv.Id, XYZ.Zero); }
                                catch (Exception ex)
                                {
                                    Log(LemoineStrings.T("testing.placeDependentViews.log.placeFail", dv.Name, ex.Message), "fail");
                                    LemoineLog.Swallowed("PlaceDependentViews: Viewport.Create", ex);
                                    fail++;
                                    continue;
                                }
                                // The composite source is never trimmed (non-uniform annotation crop) and a
                                // crop-inactive view's footprint isn't crop-bound, so neither is cacheable.
                                bool cacheable = grouped && dv.CropBoxActive
                                                 && (sourceView == null || dv.Id != sourceView.Id);
                                var key = cacheable ? GroupKey(dv) : default((long, long, int));
                                recs.Add(new PlaceRec(vp, cacheable, key, cacheable && sizeCache.ContainsKey(key)));
                            }
                            if (recs.Count == 0)
                            {
                                Log(LemoineStrings.T("testing.placeDependentViews.log.nothingPlaced", sheetNumber, sheetName), "warn");
                                onProgress(Pct(i + 1, total), pass, fail, skip);
                                continue;
                            }

                            // Outlines are only valid after a regen; skip it when every group is cached
                            // and the drawing area is already known.
                            bool anyMeasure = false;
                            foreach (var r in recs) if (!r.Cached) { anyMeasure = true; break; }
                            if (anyMeasure || !areaKnown) doc.Regenerate();

                            if (!areaKnown)
                                areaKnown = TryGetDrawingArea(doc, sheet, marginLeft, marginRight, marginTop, marginBottom,
                                                             out areaMinX, out areaMinY, out areaW, out areaH);
                            if (!areaKnown)
                            {
                                Log(LemoineStrings.T("testing.placeDependentViews.log.noTbSizeAccurate", sheetNumber), "warn");
                                LemoineLog.Warn("PlaceDependentViews", $"No title-block bbox on sheet {sheetNumber}.");
                                pass++;
                                onProgress(Pct(i + 1, total), pass, fail, skip);
                                continue;
                            }

                            var rects = new List<SheetLayoutPacker.Rect>(recs.Count);
                            var keep  = new List<Viewport>(recs.Count);
                            int measured = 0, reused = 0;
                            foreach (var r in recs)
                            {
                                double w, h;
                                bool have;
                                if (r.Cached)
                                {
                                    var s = sizeCache[r.Key];
                                    w = s.w; h = s.h; have = true; reused++;
                                }
                                else
                                {
                                    have = TryGetOutlineSize(r.Vp, out w, out h);
                                    if (have)
                                    {
                                        measured++;
                                        if (r.Cacheable) sizeCache[r.Key] = (w, h);
                                    }
                                }
                                if (have)
                                {
                                    rects.Add(new SheetLayoutPacker.Rect(w, h));
                                    keep.Add(r.Vp);
                                }
                                else
                                {
                                    Log(LemoineStrings.T("testing.placeDependentViews.log.vpNoSize"), "warn");
                                }
                            }
                            if (grouped)
                                Log(LemoineStrings.T("testing.placeDependentViews.log.groupedSummary", keep.Count, measured, reused), "info");

                            // Composite layout anchors on the source view's measured size, so a
                            // source that reported no outline fails the whole sheet (viewports
                            // are left at the origin for manual cleanup).
                            if (sourceView != null && (keep.Count == 0 || keep[0].ViewId != sourceView.Id))
                            {
                                Log(LemoineStrings.T("testing.placeDependentViews.log.sourceNoSize", sourceView.Name, sheetNumber), "fail");
                                fail++;
                                onProgress(Pct(i + 1, total), pass, fail, skip);
                                continue;
                            }

                            if (keep.Count > 0)
                            {
                                var (centers, overflow) = LayOutSheet(rects, sourceView != null,
                                                                      areaW, areaH, gap, sheetNumber);
                                for (int k = 0; k < keep.Count; k++)
                                {
                                    var p = centers[k];
                                    keep[k].SetBoxCenter(new XYZ(areaMinX + p.CenterX, areaMinY + p.CenterY, 0));
                                }
                                if (overflow)
                                    Log(LemoineStrings.T("testing.placeDependentViews.log.overflowAccurate", sheetNumber, keep.Count), "warn");
                            }
                            placedCount = keep.Count;
                        }

                        pass++;
                        Log(composite
                            ? LemoineStrings.T("testing.placeDependentViews.log.doneComposite", sheetNumber, sheetName, Math.Max(0, placedCount - 1))
                            : LemoineStrings.T("testing.placeDependentViews.log.doneDependents", sheetNumber, sheetName, placedCount), "pass");
                        onProgress(Pct(i + 1, total), pass, fail, skip);
                    }

                    tx.Commit();
                }

                long issues = LemoineLog.IssuesSince(issues0);
                if (issues > 0)
                    Log(LemoineStrings.T("testing.placeDependentViews.log.issuesRecorded", issues), "warn");

                onProgress(100, pass, fail, skip);
                onComplete(pass, fail, skip);
            }
            catch (Exception ex)
            {
                Log(LemoineStrings.T("testing.placeDependentViews.log.fatalError", ex.Message), "fail");
                LemoineLog.Error("PlaceDependentViews.Execute", ex);
                onComplete(pass, fail + 1, skip);
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                ParentViewIds = new List<ElementId>();
            }
        }

        // ── Layout per sheet ──────────────────────────────────────────────────
        /// <summary>
        /// Runs the layout for one sheet's rects: composite mode anchors the first rect
        /// (the source view) top- or left-center with the rest in aligned rows/columns;
        /// dependents mode packs everything without overlap. Centers come back in rect
        /// order, relative to the drawing-area bottom-left.
        /// </summary>
        private (IReadOnlyList<SheetLayoutPacker.Placement> Centers, bool Overflow) LayOutSheet(
            List<SheetLayoutPacker.Rect> rects, bool composite,
            double areaW, double areaH, double gap, string sheetNumber)
        {
            if (composite)
            {
                var res = CompositeSheetLayout.Pack(rects[0], rects.Skip(1).ToList(), areaW, areaH, gap);
                var centers = new List<SheetLayoutPacker.Placement>(rects.Count) { res.Parent };
                centers.AddRange(res.Children);
                Log(LemoineStrings.T("testing.placeDependentViews.log.sheetLayout", sheetNumber, (res.ParentOnTop ? LemoineStrings.T("testing.placeDependentViews.log.layoutTop") : LemoineStrings.T("testing.placeDependentViews.log.layoutLeft"))), "info");
                return (centers, res.Overflow);
            }
            var packed = SheetLayoutPacker.Pack(rects, areaW, areaH, gap);
            return (packed.Placements, packed.Overflow);
        }

        // ── Composite sub-view discovery ──────────────────────────────────────
        /// <summary>
        /// Finds the views whose markers are visible in <paramref name="source"/>:
        /// section/callout markers (OST_Viewers, resolved to views by their unique
        /// name) plus elevation markers (real view ids per used index). The scoped
        /// collector only returns visible markers, so hidden ones are excluded.
        /// </summary>
        private List<View> DiscoverSubViews(Document doc, View source, Dictionary<string, View> viewsByName)
        {
            var subs = new List<View>();
            var seen = new HashSet<ElementId> { source.Id };

            try
            {
                foreach (Element marker in new FilteredElementCollector(doc, source.Id)
                             .OfCategory(BuiltInCategory.OST_Viewers)
                             .WhereElementIsNotElementType())
                {
                    string name = marker.Name ?? "";
                    if (viewsByName.TryGetValue(name, out var v))
                    {
                        if (seen.Add(v.Id)) subs.Add(v);
                    }
                    else
                    {
                        Log(LemoineStrings.T("testing.placeDependentViews.log.markerUnresolved", name, source.Name), "warn");
                        LemoineLog.Warn("PlaceDependentViews",
                            $"Unresolved viewer marker '{name}' in view {source.Id.Value}.");
                    }
                }

                foreach (ElevationMarker m in new FilteredElementCollector(doc, source.Id)
                             .OfClass(typeof(ElevationMarker)))
                {
                    for (int idx = 0; idx < m.MaximumViewCount; idx++)
                    {
                        ElementId vid;
                        try { vid = m.GetViewId(idx); }
                        catch (Exception ex)
                        {
                            // An unused marker index can throw rather than return invalid.
                            LemoineLog.Swallowed("PlaceDependentViews: ElevationMarker.GetViewId", ex);
                            continue;
                        }
                        if (vid == null || vid == ElementId.InvalidElementId) continue;
                        if (doc.GetElement(vid) is View ev && !ev.IsTemplate && seen.Add(ev.Id))
                            subs.Add(ev);
                    }
                }
            }
            catch (Exception ex)
            {
                Log(LemoineStrings.T("testing.placeDependentViews.log.scanMarkersFail", source.Name, ex.Message), "warn");
                LemoineLog.Error($"PlaceDependentViews: discover sub views in {source.Id.Value}", ex);
            }

            return subs;
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

        /// <summary>A provisionally-placed viewport plus its grouped-layout size source.</summary>
        private readonly struct PlaceRec
        {
            public PlaceRec(Viewport vp, bool cacheable, (long, long, int) key, bool cached)
            {
                Vp = vp; Cacheable = cacheable; Key = key; Cached = cached;
            }
            /// <summary>The placed viewport (positioned later via SetBoxCenter).</summary>
            public Viewport Vp { get; }
            /// <summary>Whether this view's measured size may be stored and reused for its group.</summary>
            public bool Cacheable { get; }
            /// <summary>The (cropW, cropH, scale) group key; default when not cacheable.</summary>
            public (long, long, int) Key { get; }
            /// <summary>Whether the group's size is already known (reuse it, skip measuring).</summary>
            public bool Cached { get; }
        }

        /// <summary>
        /// Group key for grouped layout: views sharing crop-box size and scale render to the same
        /// on-sheet outline (with a uniform annotation crop). Paper dimensions are bucketed to a
        /// 1/64" grid so near-identical crops collapse into one group. Only called for crop-active views.
        /// </summary>
        private static (long, long, int) GroupKey(View v)
        {
            double scale = v.Scale > 0 ? v.Scale : 1.0;
            BoundingBoxXYZ cb = v.CropBox;
            double wPaper = Math.Abs(cb.Max.X - cb.Min.X) / scale;
            double hPaper = Math.Abs(cb.Max.Y - cb.Min.Y) / scale;
            long Bucket(double ft) => (long)Math.Round(ft * 12.0 * 64.0);   // 1/64" units
            return (Bucket(wPaper), Bucket(hPaper), (int)Math.Round(scale));
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
                    Log(LemoineStrings.T("testing.placeDependentViews.log.seriesParamMissing", SeriesParamName), "warn");
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
