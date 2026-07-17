using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Tools.Dimensioning;
using LemoineTools.Tools.Dimensioning.ElevationTag;

namespace LemoineTools.Tools.Dimensioning
{
    /// <summary>
    /// Runs the Clash Finder for sections / elevations: for each selected <see cref="ClashDefinition"/>
    /// it detects clashes and draws coloured round markers in the view's vertical plane (one tagged
    /// diameter line per clash), then places a spot elevation at the chosen top / centre / bottom point
    /// of each round. Detection runs once per definition (view-independent, read-only); marking commits
    /// one transaction per view so cancel preserves finished views; the elevation-tag pass opens its own
    /// transaction afterwards (so the freshly placed marker lines are queryable). Clear-previous is
    /// applied per view inside that view's marking transaction.
    /// </summary>
    public class ClashElevationFinderEventHandler : IExternalEventHandler
    {
        // ── Inputs (set by the ViewModel before Raise()) ──────────────────────
        public List<ElementId>       ViewIds          { get; set; } = new List<ElementId>();
        public List<ClashDefinition> Definitions      { get; set; } = new List<ClashDefinition>();
        public bool                  ClearPrevious    { get; set; } = true;
        public string                AnchorMode       { get; set; } = "Centre";  // "Top" | "Centre" | "Bottom"
        public ElementId             SpotTypeId       { get; set; } = ElementId.InvalidElementId;
        public double                RoundSizeMm      { get; set; } = 0.0;       // marker oversize added to the Group 1 element size; 0 = exact

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }
        public Action<IReadOnlyList<ResultChip>>? OnResultChips { get; set; }

        public string GetName() => "LemoineTools.Tools.Dimensioning.ClashElevationFinderEventHandler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            // pass = clash markers placed (one per clash — the deliverable). Elevation tags
            // are a label on each marker, tracked separately so a successful run isn't reported
            // as ~2× the clash count by summing markers + tags.
            int pass = 0, fail = 0, skip = 0;
            int tagsPlaced = 0;

            try
            {
                if (Definitions == null || Definitions.Count == 0)
                {
                    Log(AppStrings.T("clash.elevationFinder.log.noDefs"), "fail");
                    fail++;
                }
                else if (ViewIds == null || ViewIds.Count == 0)
                {
                    Log(AppStrings.T("clash.elevationFinder.log.noViews"), "fail");
                    fail++;
                }
                else
                {
                    // ── Phase 1: detect each definition's clashes ONCE (view-independent,
                    // read-only — no transaction needed) ──
                    var dets = new List<(ClashEngine engine, ClashEngine.ClashDetection det, Dictionary<string, ElementId?> cache)>();
                    int defsDetected = 0;
                    foreach (var def in Definitions)
                    {
                        // Abandon mid-run during detection: nothing is committed in this phase;
                        // Phase 2 sees the same flag and stops too.
                        if (RunState.CancelRequested)
                        {
                            Log(AppStrings.T("common.log.stoppedByUser", defsDetected, Definitions.Count), "warn");
                            break;
                        }

                        Progress(5 + (int)(defsDetected * 25.0 / Definitions.Count), pass, fail, skip);
                        Log(AppStrings.T("clash.elevationFinder.log.defHeader", def.Name), "info");

                        var opts = new ClashMarkingOptions
                        {
                            ToleranceMm       = def.ToleranceMm,
                            FillStyle         = def.FillStyle,
                            FallbackColorHex  = def.FallbackColorHex,
                            CrossLineTypeName = def.CrossLineTypeName,
                            DimTarget         = def.DimTarget,
                            MaxClashes        = def.MaxClashes,
                            StoreyMarginMm    = 0.0,        // no plan storey band — section/elevation views gate on their own crop
                            RoundSizeMm       = RoundSizeMm,
                            PhaseMode         = def.PhaseMode,
                            SpecificPhaseName = def.SpecificPhaseName,
                            ElevationMode     = true,
                        };

                        var engine = new ClashEngine(opts, (t, s) => Log(t, s));
                        var det = engine.Detect(doc, def.Group1, def.Group2);
                        dets.Add((engine, det, new Dictionary<string, ElementId?>()));
                        defsDetected++;
                    }

                    // ── Phase 2: mark one view per transaction, so cancel works per view and
                    // every finished view's markers are preserved — the old single whole-run
                    // transaction couldn't be stopped once the first definition started
                    // (matches the plan-view Clash Finder's structure). ──
                    int processed = 0;
                    foreach (var viewId in ViewIds)
                    {
                        if (RunState.CancelRequested)
                        {
                            Log(AppStrings.T("common.log.stoppedByUser", processed, ViewIds.Count), "warn");
                            break;
                        }
                        processed++;

                        var view = doc.GetElement(viewId) as View;
                        if (view == null)
                        {
                            // A selection can outlive its view — say so, don't just tally a skip.
                            Log(AppStrings.T("clash.elevationFinder.log.viewGone", viewId.Value), "warn");
                            skip++;
                            continue;
                        }

                        try
                        {
                            using (var tx = new Transaction(doc, $"Lemoine - Clash Finder (Elevation Tags) · {view.Name}"))
                            {
                                tx.Start();
                                ConfigureFailures(tx);

                                if (ClearPrevious)
                                {
                                    int removed = ClearViewMarkers(doc, viewId);
                                    if (removed > 0) Log(AppStrings.T("clash.elevationFinder.log.clearedView", removed, view.Name), "info");
                                }

                                foreach (var (engine, det, cache) in dets)
                                {
                                    if (det.Failed || det.ClashCount == 0) continue;
                                    var pr = engine.PlaceInView(doc, view, det, cache);
                                    pass += pr.Markers;
                                    fail += pr.Fails;
                                }

                                tx.Commit();
                            }
                        }
                        catch (Exception ex)
                        {
                            DiagnosticsLog.Error("ClashElevationFinder: mark view", ex);
                            Log(AppStrings.T("clash.elevationFinder.log.viewFailed", view.Name, ex.Message), "fail");
                            fail++;
                        }

                        Progress(30 + (int)(processed * 50.0 / ViewIds.Count), pass, fail, skip);
                    }
                    Log(AppStrings.T("clash.elevationFinder.log.markingDone", pass, fail), pass > 0 ? "pass" : "fail");

                    // Elevation-tag pass: place a spot elevation at each marker. Runs after the marking
                    // transaction has committed, so the freshly placed diameter lines are queryable.
                    if (pass > 0)
                    {
                        if (SpotTypeId == ElementId.InvalidElementId)
                        {
                            Log(AppStrings.T("clash.elevationFinder.log.noSpotType"), "fail");
                            fail++;
                        }
                        else
                        {
                            Progress(85, pass, fail, skip);
                            Log(AppStrings.T("clash.elevationFinder.log.placingTags"), "info");

                            var tagResult = ElevationTagRunner.Run(doc, ViewIds, AnchorMode, SpotTypeId,
                                (t, s) => Log(t, s),
                                p => Progress(85 + (int)(p * 0.13), pass, fail, skip));
                            tagsPlaced += tagResult.Placed;
                            fail       += tagResult.Failures;
                            Log(AppStrings.T("clash.elevationFinder.log.tagsDone", tagResult.Placed, tagResult.Failures),
                                tagResult.Placed > 0 ? "pass" : "fail");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("clash.elevationFinder.log.fatal", ex.Message), "fail");
                fail++;
            }
            finally
            {
                // Report results, then drop the run's payload. The reporting is wrapped so a
                // throwing callback can never skip the payload clear (memory discipline).
                try
                {
                    Log(AppStrings.T("clash.elevationFinder.log.done", pass, tagsPlaced, fail),
                        pass > 0 ? "pass" : fail > 0 ? "fail" : "info");
                    Progress(100, pass, fail, skip);
                    OnResultChips?.Invoke(new List<ResultChip>
                    {
                        new ResultChip("markers", pass,       "LemoineGreen"),
                        new ResultChip("tags",    tagsPlaced, "LemoineGreen"),
                        new ResultChip("failed",  fail,       "LemoineRed"),
                    });
                    Complete(pass, fail, skip);
                }
                catch (Exception cbEx) { DiagnosticsLog.Swallowed("ClashElevationFinder: completion callback", cbEx); }

                // Session-long static handler (App.ClashElevationFinderHandler) — drop the run's payload.
                ViewIds     = new List<ElementId>();
                Definitions = new List<ClashDefinition>();
            }
        }

        /// <summary>Clears ONE view's prior Lemoine markers and tags (tagged lines, filled
        /// regions, and spot elevations), inside the caller's open per-view transaction.</summary>
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
            catch (Exception ex) { DiagnosticsLog.Swallowed("ClashElevationFinder: collect tagged lines for cleanup", ex); }

            try
            {
                toDelete.AddRange(new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(FilledRegion))
                    .WhereElementIsNotElementType()
                    .Where(ClashTagSchema.IsOurs)
                    .Select(e => e.Id));
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ClashElevationFinder: collect tagged filled regions for cleanup", ex); }

            try
            {
                toDelete.AddRange(new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(SpotDimension))
                    .WhereElementIsNotElementType()
                    .Where(ClashTagSchema.IsOurs)
                    .Select(e => e.Id));
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ClashElevationFinder: collect tagged spot elevations for cleanup", ex); }

            foreach (var id in toDelete)
            {
                try { if (doc.Delete(id).Count > 0) removed++; }
                catch (Exception ex) { DiagnosticsLog.Swallowed("ClashElevationFinder: delete tagged element", ex); }
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
