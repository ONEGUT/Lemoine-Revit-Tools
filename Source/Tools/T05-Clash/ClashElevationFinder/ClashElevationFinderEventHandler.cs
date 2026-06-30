using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Clash;
using LemoineTools.Tools.Testing.ElevationTag;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// Runs the Clash Finder for sections / elevations: for each selected <see cref="ClashDefinition"/>
    /// it detects clashes and draws coloured round markers in the view's vertical plane (one tagged
    /// diameter line per clash), then places a spot elevation at the chosen top / centre / bottom point
    /// of each round. Detection + marking happen in one transaction; the elevation-tag pass opens its own
    /// afterwards (so the freshly placed marker lines are queryable). Clear-previous is run-level.
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

        public string GetName() => "LemoineTools.Tools.Testing.ClashElevationFinderEventHandler";

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
                    Log(LemoineStrings.T("clash.elevationFinder.log.noDefs"), "fail");
                    fail++;
                }
                else if (ViewIds == null || ViewIds.Count == 0)
                {
                    Log(LemoineStrings.T("clash.elevationFinder.log.noViews"), "fail");
                    fail++;
                }
                else
                {
                    using (var tx = new Transaction(doc, "Lemoine - Clash Finder (Elevation Tags)"))
                    {
                        tx.Start();
                        ConfigureFailures(tx);

                        if (ClearPrevious)
                        {
                            int removed = ClearPreviousMarkers(doc);
                            if (removed > 0) Log(LemoineStrings.T("clash.elevationFinder.log.cleared", removed), "info");
                        }

                        int done = 0;
                        foreach (var def in Definitions)
                        {
                            // Abandon mid-run: stop detecting/marking more definitions but let tx.Commit()
                            // (below) still run so markers placed for definitions done so far are preserved.
                            if (LemoineRun.CancelRequested)
                            {
                                Log(LemoineStrings.T("common.log.stoppedByUser", done, Definitions.Count), "warn");
                                break;
                            }

                            Progress(10 + (int)(done * 70.0 / Definitions.Count), pass, fail, skip);
                            Log(LemoineStrings.T("clash.elevationFinder.log.defHeader", def.Name), "info");

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
                            var r = engine.Run(doc, ViewIds, def.Group1, def.Group2);

                            pass += r.Markers;
                            fail += r.Fails;
                            done++;
                        }

                        tx.Commit();
                        Log(LemoineStrings.T("clash.elevationFinder.log.markingDone", pass, fail), pass > 0 ? "pass" : "fail");
                    }

                    // Elevation-tag pass: place a spot elevation at each marker. Runs after the marking
                    // transaction has committed, so the freshly placed diameter lines are queryable.
                    if (pass > 0)
                    {
                        if (SpotTypeId == ElementId.InvalidElementId)
                        {
                            Log(LemoineStrings.T("clash.elevationFinder.log.noSpotType"), "fail");
                            fail++;
                        }
                        else
                        {
                            Progress(85, pass, fail, skip);
                            Log(LemoineStrings.T("clash.elevationFinder.log.placingTags"), "info");

                            var tagResult = ElevationTagRunner.Run(doc, ViewIds, AnchorMode, SpotTypeId, (t, s) => Log(t, s));
                            tagsPlaced += tagResult.Placed;
                            fail       += tagResult.Failures;
                            Log(LemoineStrings.T("clash.elevationFinder.log.tagsDone", tagResult.Placed, tagResult.Failures),
                                tagResult.Placed > 0 ? "pass" : "fail");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(LemoineStrings.T("clash.elevationFinder.log.fatal", ex.Message), "fail");
                fail++;
            }
            finally
            {
                // Report results, then drop the run's payload. The reporting is wrapped so a
                // throwing callback can never skip the payload clear (memory discipline).
                try
                {
                    Log(LemoineStrings.T("clash.elevationFinder.log.done", pass, tagsPlaced, fail),
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
                catch (Exception cbEx) { LemoineLog.Swallowed("ClashElevationFinder: completion callback", cbEx); }

                // Session-long static handler (App.ClashElevationFinderHandler) — drop the run's payload.
                ViewIds     = new List<ElementId>();
                Definitions = new List<ClashDefinition>();
            }
        }

        private int ClearPreviousMarkers(Document doc)
        {
            int removed = 0;
            foreach (var viewId in ViewIds)
            {
                var toDelete = new List<ElementId>();

                try
                {
                    toDelete.AddRange(new FilteredElementCollector(doc, viewId)
                        .OfCategory(BuiltInCategory.OST_Lines)
                        .WhereElementIsNotElementType()
                        .Where(ClashTagSchema.IsOurs)
                        .Select(e => e.Id));
                }
                catch (Exception ex) { LemoineLog.Swallowed("ClashElevationFinder: collect tagged lines for cleanup", ex); }

                try
                {
                    toDelete.AddRange(new FilteredElementCollector(doc, viewId)
                        .OfClass(typeof(FilledRegion))
                        .WhereElementIsNotElementType()
                        .Where(ClashTagSchema.IsOurs)
                        .Select(e => e.Id));
                }
                catch (Exception ex) { LemoineLog.Swallowed("ClashElevationFinder: collect tagged filled regions for cleanup", ex); }

                try
                {
                    toDelete.AddRange(new FilteredElementCollector(doc, viewId)
                        .OfClass(typeof(SpotDimension))
                        .WhereElementIsNotElementType()
                        .Where(ClashTagSchema.IsOurs)
                        .Select(e => e.Id));
                }
                catch (Exception ex) { LemoineLog.Swallowed("ClashElevationFinder: collect tagged spot elevations for cleanup", ex); }

                foreach (var id in toDelete)
                {
                    try { if (doc.Delete(id).Count > 0) removed++; }
                    catch (Exception ex) { LemoineLog.Swallowed("ClashElevationFinder: delete tagged element", ex); }
                }
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
