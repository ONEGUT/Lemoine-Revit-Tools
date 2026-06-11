using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Clash.AutoDimension;

namespace LemoineTools.Tools.Clash
{
    /// <summary>
    /// Runs the Clash Finder: for each selected <see cref="ClashDefinition"/> it detects clashes
    /// and places coloured, ES-tagged markers (one transaction for the whole run). When the
    /// dimension-pass checkbox is set, it then runs the auto-dimension engine to place dimensions
    /// from each clash marker out to the chosen target (grids or slab edges).
    /// Clear-previous is run-level (applied once, before the definition loop) so multi-definition
    /// runs never wipe earlier markers.
    /// </summary>
    public class ClashFinderEventHandler : IExternalEventHandler
    {
        // ── Inputs (set by the ViewModel before Raise()) ──────────────────────
        public List<ElementId>      ViewIds          { get; set; } = new List<ElementId>();
        public List<ClashDefinition> Definitions     { get; set; } = new List<ClashDefinition>();
        public bool                 ClearPrevious    { get; set; } = true;
        public bool                 RunDimensionPass { get; set; } = false;
        public string               DimTargetType    { get; set; } = "Grid";   // dimension-pass target: "Grid" | "SlabEdge" | "ManualDatum"
        public double               RoundSizeMm      { get; set; } = 0.0;    // marker oversize added to the Group 1 element size; 0 = exact
        public System.Collections.Generic.List<AutoDimension.Resolvers.SlabScope> SlabScopes { get; set; }
            = new System.Collections.Generic.List<AutoDimension.Resolvers.SlabScope>();  // up-front picked slab(s); empty = all floors

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }
        public Action<IReadOnlyList<ResultChip>>? OnResultChips { get; set; }

        public string GetName() => "LemoineTools.Tools.Clash.ClashFinderEventHandler";

        public void Execute(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            var doc   = uidoc.Document;
            int viewsDone = 0, viewsFailed = 0, viewsSkipped = 0;
            // The tool's real deliverables are markers and dimensions placed — these drive the
            // pass/fail counters so the headline reflects what was produced, not how many views
            // were touched. View tallies are reported in the summary log line.
            int totalMarkers = 0, totalMarkerFails = 0, totalDims = 0, totalDimFails = 0;

            try
            {
                if (Definitions == null || Definitions.Count == 0)
                {
                    Log("No clash definitions selected.", "fail");
                    viewsFailed++;
                }
                else if (ViewIds == null || ViewIds.Count == 0)
                {
                    Log("No views selected.", "fail");
                    viewsFailed++;
                }
                else
                {
                    // ── Phase 1: detect each definition's clashes ONCE (view-independent) ──
                    Progress(5, viewsDone, viewsFailed, viewsSkipped);
                    var dets = new List<(ClashEngine engine, ClashEngine.ClashDetection det, Dictionary<string, ElementId?> cache)>();
                    foreach (var def in Definitions)
                    {
                        Log($"— Detecting '{def.Name}' —", "info");
                        var opts = new ClashMarkingOptions
                        {
                            ToleranceMm       = def.ToleranceMm,
                            FillStyle         = def.FillStyle,
                            FallbackColorHex  = def.FallbackColorHex,
                            CrossLineTypeName = def.CrossLineTypeName,
                            DimTarget         = def.DimTarget,
                            MaxClashes        = def.MaxClashes,
                            StoreyMarginMm    = ClashDimensionSettings.Instance.StoreyMarginMm,
                            RoundSizeMm       = RoundSizeMm,
                            PhaseMode         = def.PhaseMode,
                            SpecificPhaseName = def.SpecificPhaseName,
                        };
                        var engine = new ClashEngine(opts, (t, s) => Log(t, s));
                        var det = engine.Detect(doc, def.Group1, def.Group2);
                        dets.Add((engine, det, new Dictionary<string, ElementId?>()));
                    }

                    // ── Dimension-pass prep: apply run-level config overrides once (restored in
                    // finally), and pick manual datums up front so all picks stay together. ──
                    var dimCfg = AutoDimensionConfig.Instance;
                    var snapTarget = dimCfg.TargetType;
                    System.Collections.Generic.IDictionary<ElementId, System.Collections.Generic.List<AutoDimension.Resolvers.ManualDatum>>? datums = null;
                    bool slabEdge = string.Equals(DimTargetType, "SlabEdge", StringComparison.OrdinalIgnoreCase);
                    try
                    {
                        if (RunDimensionPass)
                        {
                            // Destination is the only per-run override; chaining, callouts, and
                            // grouping tolerances always come from the saved settings.
                            dimCfg.TargetType = DimTargetType;
                            if (string.Equals(DimTargetType, "ManualDatum", StringComparison.OrdinalIgnoreCase))
                                datums = AutoDimension.ManualDatumPicker.PickForViews(uidoc, ViewIds, (t, s) => Log(t, s));
                        }

                        // ── Phase 2: one view at a time — mark, then dimension, then next view ──
                        int processed = 0;
                        foreach (var viewId in ViewIds)
                        {
                            processed++;
                            var view = doc.GetElement(viewId) as View;
                            if (view == null)
                            {
                                viewsSkipped++;
                                Progress(10 + (int)(processed * 88.0 / ViewIds.Count),
                                     totalMarkers + totalDims, totalMarkerFails + totalDimFails, viewsSkipped);
                                continue;
                            }

                            Log($"— View '{view.Name}' —", "info");
                            int markers = 0, markerFails = 0;
                            bool viewFailed = false;
                            try
                            {
                                // Mark this view: clear its old markers, then place every definition's
                                // detected clashes that fall in this view — one transaction per view.
                                using (var tx = new Transaction(doc, $"Lemoine - Clash Finder · {view.Name}"))
                                {
                                    tx.Start();
                                    ConfigureFailures(tx);
                                    if (ClearPrevious)
                                    {
                                        int removed = ClearViewMarkers(doc, viewId);
                                        if (removed > 0) Log($"Cleared {removed} previous marker element(s) in '{view.Name}'.", "info");
                                    }
                                    foreach (var (engine, det, cache) in dets)
                                    {
                                        if (det.Failed || det.ClashCount == 0) continue;
                                        var pr = engine.PlaceInView(doc, view, det, cache);
                                        markers     += pr.Markers;
                                        markerFails += pr.Fails;
                                        totalMarkers     += pr.Markers;
                                        totalMarkerFails += pr.Fails;
                                    }
                                    tx.Commit();
                                }
                                Log($"View '{view.Name}': {markers} marker(s) placed.", markers > 0 ? "pass" : "info");

                                // Dimension this view (its own transaction), only when it has markers.
                                if (RunDimensionPass && markers > 0)
                                {
                                    System.Collections.Generic.IDictionary<ElementId, System.Collections.Generic.List<AutoDimension.Resolvers.ManualDatum>>? oneDatum = null;
                                    if (datums != null && datums.TryGetValue(viewId, out var vd) && vd != null)
                                        oneDatum = new Dictionary<ElementId, System.Collections.Generic.List<AutoDimension.Resolvers.ManualDatum>> { [viewId] = vd };

                                    System.Collections.Generic.IDictionary<ElementId, System.Collections.Generic.List<AutoDimension.Resolvers.SlabScope>>? oneScope = null;
                                    if (slabEdge && SlabScopes.Count > 0)
                                        oneScope = new Dictionary<ElementId, System.Collections.Generic.List<AutoDimension.Resolvers.SlabScope>> { [viewId] = SlabScopes };

                                    // Callout tier: EXTREME dense areas (too packed even for chained
                                    // strings) get an enlarged-plan callout each — marked like any view,
                                    // dimensioned at the computed scale — and are excluded from this
                                    // parent view's dimension pass. Moderate areas stay on the chain tier.
                                    // Each callout REMOVES its clashes from the parent, which changes the
                                    // clustering of everything left behind — so the survey re-runs on the
                                    // remaining markers until no further dense area qualifies, and only
                                    // then is the parent dimensioned (full reassessment per promotion).
                                    var dimViewIds = new List<ElementId> { viewId };
                                    System.Collections.Generic.IDictionary<ElementId, List<string>>? excludes = null;
                                    HashSet<ElementId>? cropBounded = null;
                                    if (dimCfg.DenseCalloutsEnabled)
                                    {
                                        var deferred    = new List<string>();
                                        var calloutIds  = new List<ElementId>();
                                        var allRequests = new List<AutoDimension.DenseCalloutRequest>();
                                        int calloutSeq  = 0;
                                        const int maxCalloutPasses = 4;   // each pass deletes markers, so this terminates fast
                                        for (int pass = 0; pass < maxCalloutPasses; pass++)
                                        {
                                            var requests = AutoDimension.AutoDimensionEngine.SurveyDenseAreas(
                                                doc, view, dimCfg, (t, s) => Log(t, s));
                                            if (requests.Count == 0) break;
                                            // Continuous ids across passes — a re-survey restarts at c000
                                            // and must not collide with (and silently reuse) an earlier
                                            // pass's callout view of the same name.
                                            foreach (var r in requests)
                                                r.ClusterId = "c" + (calloutSeq++).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                                            if (pass > 0)
                                                Log($"Re-assessed the remaining clashes — {requests.Count} more dense area(s) qualify for callouts.", "info");
                                            var ids = CreateDenseCallouts(doc, view, requests, dets, deferred,
                                                ref totalMarkers, ref totalMarkerFails);
                                            allRequests.AddRange(requests);
                                            calloutIds.AddRange(ids);
                                            if (ids.Count == 0) break;   // every callout failed — re-surveying would loop on the same areas
                                        }

                                        if (ClearPrevious)
                                        {
                                            // Clear callouts a previous run left behind that this run did
                                            // not reuse (their bubbles + stale markers) — after the loop,
                                            // so the sweep keeps every callout created this run.
                                            using (var tx = new Transaction(doc, $"Lemoine - Stale Callout Cleanup · {view.Name}"))
                                            {
                                                tx.Start();
                                                ConfigureFailures(tx);
                                                SweepStaleCallouts(doc, view, allRequests);
                                                tx.Commit();
                                            }
                                        }

                                        if (calloutIds.Count > 0)
                                        {
                                            dimViewIds.AddRange(calloutIds);
                                            excludes = new Dictionary<ElementId, List<string>> { [viewId] = deferred };
                                            // Callout dims must land on references visible IN the callout.
                                            cropBounded = new HashSet<ElementId>(calloutIds);
                                        }
                                    }

                                    var dimResult = AutoDimensionRunner.Run(doc, dimViewIds, dimCfg, (t, s) => Log(t, s), null, oneDatum, oneScope, excludes, cropBounded);
                                    totalDims     += dimResult.Placed;
                                    totalDimFails += dimResult.Failures;
                                    Log($"View '{view.Name}': {dimResult.Placed} dimension(s) placed across it and {dimViewIds.Count - 1} callout(s), {dimResult.Failures} failure(s).",
                                        dimResult.Placed > 0 ? "pass" : "info");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"View '{view.Name}': failed — {ex.Message}", "fail");
                                viewFailed = true;
                            }

                            // Classify the view for the top-bar tracker.
                            if (viewFailed)            viewsFailed++;   // exception while processing this view
                            else if (markers > 0)      viewsDone++;     // marked (and dimensioned, if enabled)
                            else if (markerFails > 0)  viewsFailed++;   // attempted but every marker failed
                            else                       viewsSkipped++;  // no clash fell in this view's volume

                            Progress(10 + (int)(processed * 88.0 / ViewIds.Count),
                                     totalMarkers + totalDims, totalMarkerFails + totalDimFails, viewsSkipped);
                        }
                    }
                    finally
                    {
                        dimCfg.TargetType = snapTarget;
                    }

                    Log($"Done — {totalMarkers} marker(s) and {totalDims} dimension(s) placed "
                      + $"across {viewsDone} view(s); {totalMarkerFails + totalDimFails} placement failure(s); "
                      + $"{viewsFailed} view(s) failed, {viewsSkipped} view(s) with nothing to mark.",
                        totalMarkers + totalDims > 0 ? "pass" : (totalMarkerFails + totalDimFails) > 0 ? "fail" : "info");
                }
            }
            catch (Exception ex)
            {
                Log($"Fatal: {ex.Message}", "fail");
                totalMarkerFails++;
            }

            Progress(100, totalMarkers + totalDims, totalMarkerFails + totalDimFails, viewsSkipped);
            OnResultChips?.Invoke(new List<ResultChip>
            {
                new ResultChip("markers", totalMarkers,                       "LemoineGreen"),
                new ResultChip("dims",    totalDims,                          "LemoineGreen"),
                new ResultChip("failed",  totalMarkerFails + totalDimFails,   "LemoineRed"),
                new ResultChip("empty views", viewsSkipped,                   "LemoineTextDim"),
            });
            Complete(totalMarkers + totalDims, totalMarkerFails + totalDimFails, viewsSkipped);
        }

        // Clears this view's prior Lemoine clash markers (cross-lines + filled regions; deleting the
        // cross-lines also removes any dimensions that referenced them). One view at a time, inside the
        // caller's open transaction.
        // ── Callout tier ──────────────────────────────────────────────────────
        /// <summary>
        /// Creates (or reuses, by name) one enlarged-plan callout per extreme dense area, marks
        /// each with every definition's clashes, and returns the callout view ids. Source keys of
        /// successfully calloutted areas are appended to <paramref name="deferred"/> so the parent
        /// view's dimension pass skips them — a failed callout leaves its area dimensioning in the
        /// parent (degrade to the chain tier, never drop). One transaction for all callouts.
        /// </summary>
        private List<ElementId> CreateDenseCallouts(
            Document doc, View parentView, List<AutoDimension.DenseCalloutRequest> requests,
            List<(ClashEngine engine, ClashEngine.ClashDetection det, Dictionary<string, ElementId?> cache)> dets,
            List<string> deferred, ref int totalMarkers, ref int totalMarkerFails)
        {
            var ids = new List<ElementId>();
            using (var tx = new Transaction(doc, $"Lemoine - Dense Area Callouts · {parentView.Name}"))
            {
                tx.Start();
                ConfigureFailures(tx);
                foreach (var req in requests)
                {
                    try
                    {
                        View? cv = GetOrCreateCalloutView(doc, parentView, req);
                        if (cv == null) continue;
                        doc.Regenerate();   // few callouts per view — the new view must exist before marking

                        if (ClearPrevious)
                        {
                            int removed = ClearViewMarkers(doc, cv.Id);
                            if (removed > 0) Log($"Cleared {removed} previous marker element(s) in '{cv.Name}'.", "info");
                        }

                        int placed = 0;
                        foreach (var (engine, det, cache) in dets)
                        {
                            if (det.Failed || det.ClashCount == 0) continue;
                            var pr = engine.PlaceInView(doc, cv, det, cache);
                            placed           += pr.Markers;
                            totalMarkers     += pr.Markers;
                            totalMarkerFails += pr.Fails;
                        }

                        // The callout owns these clashes now: remove their parent-view markers so
                        // they show ONLY in the callout (the parent keeps the callout bubble).
                        int parentRemoved = DeleteParentMarkers(doc, parentView.Id, req.SourceKeys);

                        ids.Add(cv.Id);
                        deferred.AddRange(req.SourceKeys);
                        Log($"Dense area {req.ClusterId} → callout '{cv.Name}' at 1:{cv.Scale} — "
                          + $"{placed} marker(s) placed there; {parentRemoved} parent marker element(s) removed, "
                          + $"its {req.ClashCount} clash(es) show and dimension only in the callout.", "pass");
                    }
                    catch (Exception ex)
                    {
                        LemoineLog.Error("ClashFinderEventHandler: create dense-area callout", ex);
                        Log($"Dense area {req.ClusterId}: callout failed ({ex.Message}) — its clashes stay dimensioned in the parent view.", "fail");
                    }
                }

                // The bubbles live in the parent on the Callouts annotation category — if the
                // parent (often via its template) hides it, every callout is INVISIBLE there.
                // Unhide when we can; when the template controls V/G, tell the user loudly.
                if (ids.Count > 0)
                {
                    try
                    {
                        var calloutsCat = new ElementId(BuiltInCategory.OST_Callouts);
                        if (parentView.GetCategoryHidden(calloutsCat))
                        {
                            bool unhidden = false;
                            try { parentView.SetCategoryHidden(calloutsCat, false); unhidden = true; }
                            catch (Exception ex) { LemoineLog.Swallowed("ClashFinderEventHandler: unhide Callouts category", ex); }
                            Log(unhidden
                                ? $"'{parentView.Name}' had the Callouts annotation category hidden — enabled it so the bubbles show."
                                : $"'{parentView.Name}' HIDES the Callouts annotation category (its view template controls visibility) — "
                                  + "the callout bubbles exist but are invisible until that category is enabled in the template.",
                                unhidden ? "info" : "fail");
                        }
                    }
                    catch (Exception ex) { LemoineLog.Swallowed("ClashFinderEventHandler: check Callouts visibility", ex); }
                }
                tx.Commit();
            }
            return ids;
        }

        /// <summary>Deletes this parent's dense-area callout views from earlier runs that this
        /// run does not reuse — leftovers keep stale bubbles (and stale markers) on the parent.
        /// Runs inside the caller's open transaction.</summary>
        private void SweepStaleCallouts(Document doc, View parentView, List<AutoDimension.DenseCalloutRequest> requests)
        {
            try
            {
                string prefix = parentView.Name + " - Dense ";
                var keep = new HashSet<string>(requests.Select(r => prefix + r.ClusterId), StringComparer.Ordinal);
                foreach (var stale in new FilteredElementCollector(doc)
                             .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                             .Where(v => !v.IsTemplate
                                      && v.Name.StartsWith(prefix, StringComparison.Ordinal)
                                      && !keep.Contains(v.Name))
                             .ToList())
                {
                    string staleName = stale.Name;
                    try
                    {
                        doc.Delete(stale.Id);
                        Log($"Deleted stale dense-area callout '{staleName}'.", "info");
                    }
                    catch (Exception ex)
                    {
                        LemoineLog.Swallowed("ClashFinderEventHandler: delete stale callout", ex);
                        Log($"Could not delete stale callout '{staleName}' — remove it manually.", "fail");
                    }
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashFinderEventHandler: stale callout sweep", ex); }
        }

        /// <summary>Deletes the parent view's marker elements (tagged cross lines + filled
        /// regions) for the given clash groups — those clashes live in a dense-area callout now
        /// and must not also show in the parent. Runs inside the caller's open transaction.</summary>
        private static int DeleteParentMarkers(Document doc, ElementId parentViewId, List<string> groupKeys)
        {
            int removed = 0;
            var keys = new HashSet<string>(groupKeys, StringComparer.Ordinal);
            var toDelete = new List<ElementId>();
            try
            {
                toDelete.AddRange(new FilteredElementCollector(doc, parentViewId)
                    .OfCategory(BuiltInCategory.OST_Lines)
                    .WhereElementIsNotElementType()
                    .Where(e => ClashTagSchema.IsOurs(e) && keys.Contains(ClashTagSchema.ReadGroup(e)))
                    .Select(e => e.Id));
                toDelete.AddRange(new FilteredElementCollector(doc, parentViewId)
                    .OfClass(typeof(FilledRegion))
                    .WhereElementIsNotElementType()
                    .Where(e => ClashTagSchema.IsOurs(e) && keys.Contains(ClashTagSchema.ReadGroup(e)))
                    .Select(e => e.Id));
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashFinderEventHandler: collect parent markers of callout clashes", ex); }

            foreach (var id in toDelete)
            {
                try { if (doc.Delete(id).Count > 0) removed++; }
                catch (Exception ex) { LemoineLog.Swallowed("ClashFinderEventHandler: delete parent marker", ex); }
            }
            return removed;
        }

        /// <summary>Finds the prior run's callout view by its deterministic name, else creates a
        /// new plan callout over the request rectangle. Template (if any) is applied BEFORE the
        /// scale so the scale override wins; a template that pins the scale is reported.</summary>
        private View? GetOrCreateCalloutView(Document doc, View parentView, AutoDimension.DenseCalloutRequest req)
        {
            string name = $"{parentView.Name} - Dense {req.ClusterId}";

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.Ordinal));
            if (existing != null)
            {
                UnpinTemplate(existing);                 // a pinned template blocks the scale
                TrySetCalloutScale(existing, req.Scale);
                try
                {
                    var cb = existing.CropBox;   // refresh the crop to the current cluster extent
                    cb.Min = new XYZ(Math.Min(req.MinWorld.X, req.MaxWorld.X), Math.Min(req.MinWorld.Y, req.MaxWorld.Y), cb.Min.Z);
                    cb.Max = new XYZ(Math.Max(req.MinWorld.X, req.MaxWorld.X), Math.Max(req.MinWorld.Y, req.MaxWorld.Y), cb.Max.Z);
                    existing.CropBox = cb;
                }
                catch (Exception ex) { LemoineLog.Swallowed("ClashFinderEventHandler: refresh callout crop", ex); }
                return existing;
            }

            View callout = ViewSection.CreateCallout(doc, parentView.Id, parentView.GetTypeId(), req.MinWorld, req.MaxWorld);
            if (callout == null)
            {
                Log($"Dense area {req.ClusterId}: Revit returned no callout view — skipped.", "fail");
                return null;
            }

            // Template first (it copies V/G etc. onto the view), then UNPIN it — clearing the
            // assignment keeps the applied settings but unlocks the parameters a template pins
            // (the scale above all: a pinned 1:96 template silently defeated the whole tier).
            if (parentView.ViewTemplateId != null && parentView.ViewTemplateId != ElementId.InvalidElementId)
            {
                try { callout.ViewTemplateId = parentView.ViewTemplateId; }
                catch (Exception ex) { LemoineLog.Swallowed("ClashFinderEventHandler: apply template to callout", ex); }
            }
            UnpinTemplate(callout);
            TrySetCalloutScale(callout, req.Scale);

            try { callout.Name = name; }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("ClashFinderEventHandler: name callout (duplicate?)", ex);
                try { callout.Name = name + " " + Guid.NewGuid().ToString("N").Substring(0, 4); }
                catch (Exception ex2) { LemoineLog.Swallowed("ClashFinderEventHandler: name callout fallback", ex2); }
            }
            return callout;
        }

        /// <summary>Clears a view's template assignment, keeping the settings the template already
        /// applied but unlocking the parameters it pins (scale, V/G). Failure is logged.</summary>
        private static void UnpinTemplate(View v)
        {
            try
            {
                if (v.ViewTemplateId != null && v.ViewTemplateId != ElementId.InvalidElementId)
                    v.ViewTemplateId = ElementId.InvalidElementId;
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashFinderEventHandler: unpin callout template", ex); }
        }

        private void TrySetCalloutScale(View callout, int scale)
        {
            try
            {
                callout.Scale = scale;
                if (callout.Scale != scale)
                    Log($"Callout '{callout.Name}': scale stayed 1:{callout.Scale} after setting 1:{scale} — check its view template.", "fail");
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("ClashFinderEventHandler: set callout scale", ex);
                Log($"Callout '{callout.Name}': could not set scale 1:{scale} (view template may pin it) — set it manually.", "fail");
            }
        }

        private static int ClearViewMarkers(Document doc, ElementId viewId)
        {
            int removed = 0;
            var toDelete = new List<ElementId>();

            try
            {
                toDelete.AddRange(new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_Lines)
                    .WhereElementIsNotElementType()
                    .Where(ClashTagSchema.IsOurs)
                    .Select(e => e.Id));
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashFinder: collect tagged lines for cleanup", ex); }

            try
            {
                toDelete.AddRange(new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(FilledRegion))
                    .WhereElementIsNotElementType()
                    .Where(ClashTagSchema.IsOurs)
                    .Select(e => e.Id));
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashFinder: collect tagged filled regions for cleanup", ex); }

            try
            {
                toDelete.AddRange(new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(Dimension))
                    .WhereElementIsNotElementType()
                    .Where(ClashTagSchema.IsOurs)
                    .Select(e => e.Id));
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashFinder: collect tagged dimensions for cleanup", ex); }

            foreach (var id in toDelete)
            {
                try { if (doc.Delete(id).Count > 0) removed++; }
                catch (Exception ex) { LemoineLog.Swallowed("ClashFinder: delete tagged element", ex); }
            }
            return removed;
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private void Log(string text, string status) => PushLog?.Invoke(text, status);
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
