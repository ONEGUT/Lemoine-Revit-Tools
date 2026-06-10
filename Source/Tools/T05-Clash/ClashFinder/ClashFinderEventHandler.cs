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
        public bool                 ShowAllDocuments { get; set; } = false;
        public bool                 RunDimensionPass { get; set; } = false;
        public string               DimTargetType    { get; set; } = "Grid";   // dimension-pass target: "Grid" | "SlabEdge" | "ManualDatum"
        public double               StoreyMarginMm   { get; set; } = 609.6;  // 2 ft: sub-floor depth still counted as a level's storey
        public double               RoundSizeMm      { get; set; } = 0.0;    // marker oversize added to the Group 1 element size; 0 = exact
        public bool                 DimChainAligned  { get; set; } = true;   // group clashes into runs (chain along, single across)
        public double               DimRunGapMm      { get; set; } = 1524.0; // 5 ft: max along-run gap between adjacent run members
        public double               DimRunCrossMm    { get; set; } = 152.4;  // 0.5 ft: off-line tolerance + across-run snap
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
                            StoreyMarginMm    = StoreyMarginMm,
                            RoundSizeMm       = RoundSizeMm,
                        };
                        var engine = new ClashEngine(opts, (t, s) => Log(t, s));
                        var det = engine.Detect(doc, EffectiveSpec(def.Group1), EffectiveSpec(def.Group2));
                        dets.Add((engine, det, new Dictionary<string, ElementId?>()));
                    }

                    // ── Dimension-pass prep: apply run-level config overrides once (restored in
                    // finally), and pick manual datums up front so all picks stay together. ──
                    var dimCfg = AutoDimensionConfig.Instance;
                    var snapTarget = dimCfg.TargetType;
                    var snapChain  = dimCfg.ChainAligned;
                    var snapGap    = dimCfg.RunGapMm;
                    var snapCross  = dimCfg.RunCrossToleranceMm;
                    System.Collections.Generic.IDictionary<ElementId, System.Collections.Generic.List<AutoDimension.Resolvers.ManualDatum>>? datums = null;
                    bool slabEdge = string.Equals(DimTargetType, "SlabEdge", StringComparison.OrdinalIgnoreCase);
                    try
                    {
                        if (RunDimensionPass)
                        {
                            dimCfg.TargetType          = DimTargetType;
                            dimCfg.ChainAligned        = DimChainAligned;
                            dimCfg.RunGapMm            = DimRunGapMm;
                            dimCfg.RunCrossToleranceMm = DimRunCrossMm;
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

                                    var dimResult = AutoDimensionRunner.Run(doc, new List<ElementId> { viewId }, dimCfg, (t, s) => Log(t, s), null, oneDatum, oneScope);
                                    totalDims     += dimResult.Placed;
                                    totalDimFails += dimResult.Failures;
                                    Log($"View '{view.Name}': {dimResult.Placed} dimension(s) placed, {dimResult.Failures} failure(s).",
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
                        dimCfg.TargetType          = snapTarget;
                        dimCfg.ChainAligned        = snapChain;
                        dimCfg.RunGapMm            = snapGap;
                        dimCfg.RunCrossToleranceMm = snapCross;
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

        // When "Show all documents" is on, ignore the definition's saved per-group source
        // filter so every loaded document is scanned. Otherwise honour the stored selection.
        private ClashGroupSpec EffectiveSpec(ClashGroupSpec spec)
        {
            if (!ShowAllDocuments) return spec;
            return new ClashGroupSpec
            {
                Mode          = spec.Mode,
                RuleKeys      = spec.RuleKeys,
                Categories    = spec.Categories,
                ElemIds       = spec.ElemIds,
                ElemLinkIds   = spec.ElemLinkIds,
                SourceLinkIds = new List<long>(),   // empty = scan every available document
            };
        }

        // Clears this view's prior Lemoine clash markers (cross-lines + filled regions; deleting the
        // cross-lines also removes any dimensions that referenced them). One view at a time, inside the
        // caller's open transaction.
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
