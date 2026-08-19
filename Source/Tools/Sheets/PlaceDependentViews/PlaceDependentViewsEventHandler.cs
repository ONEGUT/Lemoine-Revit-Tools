using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;

namespace LemoineTools.Tools.Sheets.PlaceDependentViews
{
    /// <summary>How the selected views are turned into sheets.</summary>
    public enum PlaceViewsMode
    {
        /// <summary>One sheet per parent view, holding that view's dependent views.</summary>
        DependentsPerParent,
        /// <summary>One sheet per source view, holding the view itself (anchored top- or
        /// left-center) plus every callout/section/elevation visible in it.</summary>
        CompositeOneSheet,
        /// <summary>One sheet per selected view, holding only that single view (one source view
        /// per page). No dependents, no callout/section discovery.</summary>
        OneViewPerSheet,
    }

    /// <summary>
    /// Creates one sheet per selected view and places either that view alone, that view's
    /// dependents (packed without overlap, centered), or — in composite mode — the view
    /// itself plus its visible callouts/sections/elevations (source anchored top- or
    /// left-center, sub views in aligned rows/columns).
    ///
    /// Views are placed, measured once per footprint group, then positioned: the first view of
    /// each (crop, scale, annotation-crop) group is measured for real and every later view of that
    /// group reuses its size, so a run of identical plans costs one regeneration rather than N.
    /// Runs on the Revit API thread; set all inputs before Raise().
    /// </summary>
    public sealed class PlaceDependentViewsEventHandler : IExternalEventHandler
    {
        // ── Inputs set by the view model before Raise() ───────────────────────
        public PlaceViewsMode  Mode           { get; set; } = PlaceViewsMode.OneViewPerSheet;
        public List<ElementId> ParentViewIds  { get; set; } = new List<ElementId>();

        /// <summary>The sheet number for each entry of <see cref="ParentViewIds"/>, same order and
        /// same length. Computed by the view model — which is where the numbering preview the user
        /// approved was computed too, so what they saw is what gets created. A number that has been
        /// taken since the window opened is reported per sheet, never silently shifted.</summary>
        public List<string>    SheetNumbers   { get; set; } = new List<string>();

        public ElementId       TitleBlockTypeId { get; set; } = ElementId.InvalidElementId;
        public string          NamingPattern  { get; set; } = "{ParentViewName}";

        /// <summary>The sheet parameter to write <see cref="SheetSeries"/> into, identified by GUID
        /// (shared) or definition id (project) — never by name.</summary>
        public SheetSeriesParam? SeriesParam  { get; set; }
        public string          SheetSeries    { get; set; } = "";

        public double MarginTopIn    { get; set; } = 0.5;  // all paper inches
        public double MarginBottomIn { get; set; } = 0.5;
        public double MarginLeftIn   { get; set; } = 0.5;
        public double MarginRightIn  { get; set; } = 0.5;
        public double GapIn          { get; set; } = 0.25;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Sheets.PlaceDependentViews";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        public void Execute(UIApplication app)
        {
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            int pass = 0, fail = 0, skip = 0;
            long issues0 = DiagnosticsLog.IssueCount;

            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    Log(AppStrings.T("testing.placeDependentViews.log.noDoc"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }
                if (TitleBlockTypeId == ElementId.InvalidElementId)
                {
                    Log(AppStrings.T("testing.placeDependentViews.log.noTitleBlock"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }
                if (ParentViewIds.Count == 0)
                {
                    Log(AppStrings.T("testing.placeDependentViews.log.noViews"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                double marginTop    = MarginTopIn    / 12.0;
                double marginBottom = MarginBottomIn / 12.0;
                double marginLeft   = MarginLeftIn   / 12.0;
                double marginRight  = MarginRightIn  / 12.0;
                double gap          = Math.Max(0, GapIn) / 12.0;

                // The numbers come from the view model paired 1:1 with the views. A short list is a
                // wiring bug, not a user error, so it fails loudly here instead of silently
                // numbering the tail of the run wrong.
                if (SheetNumbers.Count != ParentViewIds.Count)
                {
                    Log(AppStrings.T("testing.placeDependentViews.log.numbersMismatch",
                                     SheetNumbers.Count, ParentViewIds.Count), "fail");
                    DiagnosticsLog.Error("PlaceDependentViews",
                        new InvalidOperationException($"SheetNumbers ({SheetNumbers.Count}) != ParentViewIds ({ParentViewIds.Count})"));
                    onComplete(0, 1, 0);
                    return;
                }

                int total = ParentViewIds.Count;

                using (var tx = new Transaction(doc, "Lemoine — Create Sheets"))
                {
                    ConfigureFailures(tx);
                    tx.Start();

                    bool seriesWarned = false;

                    // Existing sheet numbers, read once. The view model already skipped these when
                    // it built the number list; this is the re-check for anything created since the
                    // window opened, and it reports rather than reassigns.
                    var usedNumbers = new HashSet<string>(
                        new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
                            .Cast<ViewSheet>().Select(vs => vs.SheetNumber),
                        StringComparer.OrdinalIgnoreCase);

                    // The drawing area is identical for every sheet (same title block), so resolve
                    // it once and reuse — in estimate mode this is the ONLY explicit regen.
                    bool   areaKnown = false;
                    double areaMinX = 0, areaMinY = 0, areaW = 0, areaH = 0;

                    // Run-level cache of measured footprints. The first view of each group is
                    // measured for real; the rest reuse its size. The key now includes each view's
                    // own annotation-crop state (see GroupKey) — it used to rely on trimming to make
                    // every group's annotation crop uniform, and with trimming gone that assumption
                    // would have quietly turned every group into a mis-size.
                    var sizeCache = new Dictionary<GroupKeyRec, (double w, double h)>();

                    Log(AppStrings.T("testing.placeDependentViews.log.announceGrouped"), "info");

                    bool composite = Mode == PlaceViewsMode.CompositeOneSheet;
                    bool oneView   = Mode == PlaceViewsMode.OneViewPerSheet;

                    // Composite and one-view modes both need the set of views already on a sheet,
                    // pre-read so a placed source fails before its (would-be-empty) sheet is created.
                    // Composite additionally resolves section/callout markers to views by unique name.
                    Dictionary<string, View>? viewsByName  = null;
                    HashSet<ElementId>?       placedViewIds = null;
                    if (composite || oneView)
                        placedViewIds = new HashSet<ElementId>(
                            new FilteredElementCollector(doc).OfClass(typeof(Viewport))
                                .Cast<Viewport>().Select(vp => vp.ViewId));
                    if (composite)
                    {
                        viewsByName = new Dictionary<string, View>(StringComparer.Ordinal);
                        foreach (var v in new FilteredElementCollector(doc)
                                     .OfClass(typeof(View)).Cast<View>())
                        {
                            if (!v.IsTemplate && !viewsByName.ContainsKey(v.Name))
                                viewsByName[v.Name] = v;
                        }
                    }

                    // One sheet at a time: progress updates per sheet and (accurate mode) each regen
                    // only touches that sheet's changes, so the commit at the end is cheap.
                    for (int i = 0; i < ParentViewIds.Count; i++)
                    {
                        if (RunState.CancelRequested)
                        {
                            Log(AppStrings.T("testing.placeDependentViews.log.stoppedByUser", i, total), "warn");
                            break;   // falls through to tx.Commit() below
                        }
                        var parent = doc.GetElement(ParentViewIds[i]) as View;
                        if (parent == null)
                        {
                            Log(AppStrings.T("testing.placeDependentViews.log.selectedGone"), "warn");
                            DiagnosticsLog.Warn("PlaceDependentViews", $"Parent view {ParentViewIds[i]} not found.");
                            skip++;
                            onProgress(Pct(i + 1, total), pass, fail, skip);
                            continue;
                        }

                        // Views going onto this sheet, in placement order. Composite mode
                        // puts the source view first — the layout anchors it.
                        View? sourceView = null;
                        List<ElementId> candidateIds;

                        if (oneView)
                        {
                            // One view per sheet: just this view, no dependents or marker discovery.
                            if (placedViewIds!.Contains(parent.Id))
                            {
                                Log(AppStrings.T("testing.placeDependentViews.log.alreadyPlacedOneView", parent.Name), "fail");
                                fail++;
                                onProgress(Pct(i + 1, total), pass, fail, skip);
                                continue;
                            }
                            candidateIds = new List<ElementId>(1) { parent.Id };
                        }
                        else if (composite)
                        {
                            if (placedViewIds!.Contains(parent.Id))
                            {
                                Log(AppStrings.T("testing.placeDependentViews.log.alreadyPlaced", parent.Name), "fail");
                                fail++;
                                onProgress(Pct(i + 1, total), pass, fail, skip);
                                continue;
                            }

                            var subs = DiscoverSubViews(doc, parent, viewsByName!);
                            if (subs.Count == 0)
                            {
                                Log(AppStrings.T("testing.placeDependentViews.log.noVisibleMarkers", parent.Name), "warn");
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
                                Log(AppStrings.T("testing.placeDependentViews.log.noDependents", parent.Name), "warn");
                                skip++;
                                onProgress(Pct(i + 1, total), pass, fail, skip);
                                continue;
                            }
                        }

                        // ── Sheet number / name ──────────────────────────────
                        // The number was chosen (and shown) before the run. If something has taken
                        // it since, that is reported against this sheet — quietly sliding to the
                        // next free number would create sheets numbered differently from the list
                        // the user just approved on the Order step.
                        string sheetNumber = SheetNumbers[i] ?? "";
                        if (!usedNumbers.Add(sheetNumber))
                        {
                            Log(AppStrings.T("testing.placeDependentViews.log.numberTaken", sheetNumber, parent.Name), "fail");
                            DiagnosticsLog.Warn("PlaceDependentViews", $"Sheet number '{sheetNumber}' already in use.");
                            fail++;
                            onProgress(Pct(i + 1, total), pass, fail, skip);
                            continue;
                        }
                        // Target is deliberately NOT the sheet: it does not exist yet, and the
                        // pattern names it from the VIEW being placed. Every view-side token is
                        // supplied explicitly so the run resolves exactly what the naming step's
                        // preview resolved — a token that read the (absent) sheet would preview as
                        // the view's name and then create a sheet named nothing.
                        var namingCtx = new TokenContext { Doc = doc, Source = parent };
                        namingCtx.Computed["ViewType"]    = parent.ViewType.ToString();
                        namingCtx.Computed["ViewName"]    = parent.Name;
                        namingCtx.Computed["CurrentName"] = parent.Name;
                        namingCtx.Computed["SheetNumber"] = sheetNumber;
                        try { namingCtx.Computed["Level"] = parent.GenLevel?.Name ?? ""; }
                        catch (Exception ex) { DiagnosticsLog.Swallowed("PlaceDependentViews: read GenLevel", ex); namingCtx.Computed["Level"] = ""; }

                        string sheetName = TokenResolver.Resolve(NamingPattern, namingCtx, msg => Log(msg, "warn"));
                        sheetName = TokenResolver.GuardDegenerate(sheetName, namingCtx, parent.Name, msg => Log(msg, "warn"));

                        ViewSheet sheet;
                        try
                        {
                            sheet = ViewSheet.Create(doc, TitleBlockTypeId);
                            sheet.get_Parameter(BuiltInParameter.SHEET_NUMBER)?.Set(sheetNumber);
                            sheet.get_Parameter(BuiltInParameter.SHEET_NAME)?.Set(sheetName);
                        }
                        catch (Exception ex)
                        {
                            Log(AppStrings.T("testing.placeDependentViews.log.createSheetFail", parent.Name, ex.Message), "fail");
                            DiagnosticsLog.Error("PlaceDependentViews: create sheet", ex);
                            fail++;
                            onProgress(Pct(i + 1, total), pass, fail, skip);
                            continue;
                        }

                        // ── Sheet Series parameter ───────────────────────────
                        if (SeriesParam != null && !string.IsNullOrWhiteSpace(SheetSeries))
                            WriteSeries(sheet, sheetNumber, ref seriesWarned);

                        // ── Which views can actually be placed ───────────────
                        var toPlace = new List<View>(candidateIds.Count);
                        foreach (var depId in candidateIds)
                        {
                            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, depId))
                            {
                                var dv0 = doc.GetElement(depId) as View;
                                Log(AppStrings.T("testing.placeDependentViews.log.cantPlaceView", dv0?.Name ?? depId.ToString()), "warn");
                                skip++;
                                continue;
                            }
                            if (doc.GetElement(depId) is View dv) toPlace.Add(dv);
                        }
                        if (sourceView != null && (toPlace.Count == 0 || toPlace[0].Id != sourceView.Id))
                        {
                            Log(AppStrings.T("testing.placeDependentViews.log.sourceCantPlace", sourceView.Name, sheetNumber), "fail");
                            fail++;
                            onProgress(Pct(i + 1, total), pass, fail, skip);
                            continue;
                        }
                        if (toPlace.Count == 0)
                        {
                            Log(AppStrings.T("testing.placeDependentViews.log.nothingToPlace", sheetNumber, sheetName), "warn");
                            onProgress(Pct(i + 1, total), pass, fail, skip);
                            continue;
                        }

                        int placedCount = 0;

                        {
                            // Place provisionally, then size each viewport. A view whose group is
                            // already cached reuses that measured footprint; only unseen groups (and
                            // uncacheable views) are measured — so a sheet with no new groups skips
                            // the regen entirely.
                            var recs = new List<PlaceRec>(toPlace.Count);
                            foreach (var dv in toPlace)
                            {
                                Viewport vp;
                                try { vp = Viewport.Create(doc, sheet.Id, dv.Id, XYZ.Zero); }
                                catch (Exception ex)
                                {
                                    Log(AppStrings.T("testing.placeDependentViews.log.placeFail", dv.Name, ex.Message), "fail");
                                    DiagnosticsLog.Swallowed("PlaceDependentViews: Viewport.Create", ex);
                                    fail++;
                                    continue;
                                }
                                // A crop-inactive view's footprint isn't crop-bound, so its size can
                                // neither be predicted from the crop nor stand in for another view's.
                                // A view whose annotation crop cannot be read is uncacheable too:
                                // guessing "no annotation crop" would let it share a cached size with
                                // a view that really has none, and silently mis-size both.
                                GroupKeyRec key = default(GroupKeyRec);
                                bool cacheable = dv.CropBoxActive && TryGroupKey(dv, out key);
                                recs.Add(new PlaceRec(vp, cacheable, key, cacheable && sizeCache.ContainsKey(key)));
                            }
                            if (recs.Count == 0)
                            {
                                Log(AppStrings.T("testing.placeDependentViews.log.nothingPlaced", sheetNumber, sheetName), "warn");
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
                                Log(AppStrings.T("testing.placeDependentViews.log.noTbSizeAccurate", sheetNumber), "warn");
                                DiagnosticsLog.Warn("PlaceDependentViews", $"No title-block bbox on sheet {sheetNumber}.");
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
                                    Log(AppStrings.T("testing.placeDependentViews.log.vpNoSize"), "warn");
                                }
                            }
                            Log(AppStrings.T("testing.placeDependentViews.log.groupedSummary", keep.Count, measured, reused), "info");

                            // Composite layout anchors on the source view's measured size, so a
                            // source that reported no outline fails the whole sheet (viewports
                            // are left at the origin for manual cleanup).
                            if (sourceView != null && (keep.Count == 0 || keep[0].ViewId != sourceView.Id))
                            {
                                Log(AppStrings.T("testing.placeDependentViews.log.sourceNoSize", sourceView.Name, sheetNumber), "fail");
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
                                    Log(AppStrings.T("testing.placeDependentViews.log.overflowAccurate", sheetNumber, keep.Count), "warn");
                            }
                            placedCount = keep.Count;
                        }

                        pass++;
                        Log(composite
                            ? AppStrings.T("testing.placeDependentViews.log.doneComposite", sheetNumber, sheetName, Math.Max(0, placedCount - 1))
                            : oneView
                                ? AppStrings.T("testing.placeDependentViews.log.doneOneView", sheetNumber, sheetName)
                                : AppStrings.T("testing.placeDependentViews.log.doneDependents", sheetNumber, sheetName, placedCount), "pass");
                        onProgress(Pct(i + 1, total), pass, fail, skip);
                    }

                    tx.Commit();
                }

                long issues = DiagnosticsLog.IssuesSince(issues0);
                if (issues > 0)
                    Log(AppStrings.T("testing.placeDependentViews.log.issuesRecorded", issues), "warn");

                onProgress(100, pass, fail, skip);
                onComplete(pass, fail, skip);
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.placeDependentViews.log.fatalError", ex.Message), "fail");
                DiagnosticsLog.Error("PlaceDependentViews.Execute", ex);
                onComplete(pass, fail + 1, skip);
            }
            finally
            {
                // Session-long static handler — drop the run's payload so a closed window's inputs
                // (and the Revit objects behind the captured series parameter) are not held for the
                // rest of the Revit session.
                ParentViewIds = new List<ElementId>();
                SheetNumbers  = new List<string>();
                SeriesParam   = null;
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
                Log(AppStrings.T("testing.placeDependentViews.log.sheetLayout", sheetNumber, (res.ParentOnTop ? AppStrings.T("testing.placeDependentViews.log.layoutTop") : AppStrings.T("testing.placeDependentViews.log.layoutLeft"))), "info");
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
                        Log(AppStrings.T("testing.placeDependentViews.log.markerUnresolved", name, source.Name), "warn");
                        DiagnosticsLog.Warn("PlaceDependentViews",
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
                            DiagnosticsLog.Swallowed("PlaceDependentViews: ElevationMarker.GetViewId", ex);
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
                Log(AppStrings.T("testing.placeDependentViews.log.scanMarkersFail", source.Name, ex.Message), "warn");
                DiagnosticsLog.Error($"PlaceDependentViews: discover sub views in {source.Id.Value}", ex);
            }

            return subs;
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
                DiagnosticsLog.Swallowed($"PlaceDependentViews: read viewport outline {vp.Id.Value}", ex);
                return false;
            }
        }

        /// <summary>A provisionally-placed viewport plus its grouped-layout size source.</summary>
        private readonly struct PlaceRec
        {
            public PlaceRec(Viewport vp, bool cacheable, GroupKeyRec key, bool cached)
            {
                Vp = vp; Cacheable = cacheable; Key = key; Cached = cached;
            }
            /// <summary>The placed viewport (positioned later via SetBoxCenter).</summary>
            public Viewport Vp { get; }
            /// <summary>Whether this view's measured size may be stored and reused for its group.</summary>
            public bool Cacheable { get; }
            /// <summary>The group key; default when not cacheable.</summary>
            public GroupKeyRec Key { get; }
            /// <summary>Whether the group's size is already known (reuse it, skip measuring).</summary>
            public bool Cached { get; }
        }

        /// <summary>
        /// Identity of a footprint group: two views with the same key render to the same on-sheet
        /// outline, so the first one measured stands in for all of them.
        ///
        /// The annotation crop is part of the key, not an assumption. The old key was
        /// (crop, scale) alone and was only sound because trimming forced every view's annotation
        /// crop to the same offsets; with trimming removed, two views of identical crop and scale
        /// but different bubble margins would have shared one cached size and both been placed
        /// wrong — with nothing logged, because a cache hit looks like a success.
        /// </summary>
        private readonly struct GroupKeyRec : IEquatable<GroupKeyRec>
        {
            private readonly (long W, long H, int Scale, bool Anno, long T, long B, long L, long R) _v;

            public GroupKeyRec(long w, long h, int scale, bool anno, long t, long b, long l, long r)
                => _v = (w, h, scale, anno, t, b, l, r);

            public bool Equals(GroupKeyRec other)   => _v.Equals(other._v);
            public override bool Equals(object? obj) => obj is GroupKeyRec o && Equals(o);
            public override int GetHashCode()        => _v.GetHashCode();
        }

        /// <summary>
        /// Builds the group key for a crop-active view. Returns false when the view's annotation
        /// crop cannot be read — the caller then treats the view as uncacheable and measures it,
        /// rather than filing it under a key that claims it has no annotation crop.
        /// Paper dimensions are bucketed to 1/64" so near-identical crops collapse into one group.
        /// </summary>
        private static bool TryGroupKey(View v, out GroupKeyRec key)
        {
            key = default(GroupKeyRec);
            try
            {
                double scale = v.Scale > 0 ? v.Scale : 1.0;
                BoundingBoxXYZ cb = v.CropBox;
                if (cb == null) return false;

                double wPaper = Math.Abs(cb.Max.X - cb.Min.X) / scale;
                double hPaper = Math.Abs(cb.Max.Y - cb.Min.Y) / scale;

                bool anno = false;
                double at = 0, ab = 0, al = 0, ar = 0;
                var p = v.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (p != null && p.AsInteger() == 1)
                {
                    var sm = v.GetCropRegionShapeManager();
                    if (sm == null) return false;
                    anno = true;
                    // Offsets are model feet; the key compares PAPER sizes, so divide by the scale
                    // exactly as the crop dimensions above do.
                    at = sm.TopAnnotationCropOffset    / scale;
                    ab = sm.BottomAnnotationCropOffset / scale;
                    al = sm.LeftAnnotationCropOffset   / scale;
                    ar = sm.RightAnnotationCropOffset  / scale;
                }

                key = new GroupKeyRec(Bucket(wPaper), Bucket(hPaper), (int)Math.Round(scale),
                                      anno, Bucket(at), Bucket(ab), Bucket(al), Bucket(ar));
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"PlaceDependentViews: group key for view {v?.Id.Value}", ex);
                return false;
            }
        }

        /// <summary>Paper feet to 1/64" units, the bucket every key term shares.</summary>
        private static long Bucket(double ft) => (long)Math.Round(ft * 12.0 * 64.0);

        /// <summary>
        /// Writes the Sheet Series value, binding the parameter by IDENTITY and then reading it back.
        ///
        /// The previous version looked the parameter up by NAME. <c>LookupParameter</c> returns only
        /// the first name match and silently picks the wrong duplicate; <c>GetParameters(name)</c>
        /// returns all matches in no defined order. Either way the value could land on a different
        /// parameter, or nowhere, with nothing reported — which is exactly what this field did.
        /// The parameter is now identified by its shared-parameter GUID (or, for a project
        /// parameter, its definition id) captured from the document before the run.
        ///
        /// The read-back is not belt-and-braces: a silently dropped value is the failure being
        /// fixed, so the write is not allowed to fail quietly a second time.
        /// </summary>
        private void WriteSeries(ViewSheet sheet, string sheetNumber, ref bool warned)
        {
            var param = SeriesParam;
            if (param == null) return;

            Parameter? p;
            try { p = param.Resolve(sheet); }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.placeDependentViews.log.seriesResolveFail", param.Name, sheetNumber, ex.Message), "fail");
                DiagnosticsLog.Error($"PlaceDependentViews: resolve series parameter '{param.Name}'", ex);
                return;
            }

            if (p == null || p.IsReadOnly)
            {
                // One line for the whole run, not one per sheet: the parameter is either on sheets
                // or it is not, and repeating that N times buries everything else in the log.
                if (!warned)
                {
                    warned = true;
                    Log(AppStrings.T("testing.placeDependentViews.log.seriesParamMissing", param.Name), "warn");
                    DiagnosticsLog.Warn("PlaceDependentViews",
                        $"Series parameter '{param.Name}' is not present or not writable on sheets.");
                }
                return;
            }

            try
            {
                bool set = p.Set(SheetSeries);
                string readBack = p.AsString() ?? "";
                if (!set || !string.Equals(readBack, SheetSeries, StringComparison.Ordinal))
                {
                    Log(AppStrings.T("testing.placeDependentViews.log.seriesWriteFail",
                                     param.Name, sheetNumber, SheetSeries, readBack), "fail");
                    DiagnosticsLog.Warn("PlaceDependentViews",
                        $"Series write to '{param.Name}' on sheet {sheetNumber} did not take (read back '{readBack}').");
                }
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.placeDependentViews.log.seriesResolveFail", param.Name, sheetNumber, ex.Message), "fail");
                DiagnosticsLog.Error($"PlaceDependentViews: write series parameter '{param.Name}'", ex);
            }
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
                        DiagnosticsLog.Swallowed("PlaceDependentViews: read warning text", ex);
                        desc = "(unreadable warning)";
                    }
                    if (_seen.Add(desc))
                    {
                        _log?.Invoke($"[warning] {desc}", "warn");
                        DiagnosticsLog.Warn("PlaceDependentViews", desc);
                    }
                    fa.DeleteWarning(msg);
                }
                return FailureProcessingResult.Continue;
            }
        }
    }
}
