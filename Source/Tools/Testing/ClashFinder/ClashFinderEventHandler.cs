using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// Runs the Clash Finder: for each selected <see cref="ClashDefinition"/> it detects clashes
    /// and places coloured, ES-tagged markers (one transaction for the whole run). When the
    /// dimension-pass checkbox is set, it then runs the read-only discovery pass and reports it.
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

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Testing.ClashFinderEventHandler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                if (Definitions == null || Definitions.Count == 0)
                {
                    Log("No clash definitions selected.", "fail");
                    fail++;
                }
                else if (ViewIds == null || ViewIds.Count == 0)
                {
                    Log("No views selected.", "fail");
                    fail++;
                }
                else
                {
                    using (var tx = new Transaction(doc, "Lemoine - Clash Finder"))
                    {
                        tx.Start();
                        ConfigureFailures(tx);

                        if (ClearPrevious)
                        {
                            int removed = ClearPreviousMarkers(doc);
                            if (removed > 0) Log($"Cleared {removed} previous marker element(s).", "info");
                        }

                        int done = 0;
                        foreach (var def in Definitions)
                        {
                            Progress(10 + (int)(done * 70.0 / Definitions.Count), pass, fail, skip);
                            Log($"— Definition '{def.Name}' —", "info");

                            var opts = new ClashMarkingOptions
                            {
                                ToleranceMm       = def.ToleranceMm,
                                FillStyle         = def.FillStyle,
                                FallbackColorHex  = def.FallbackColorHex,
                                CrossLineTypeName = def.CrossLineTypeName,
                                DimTarget         = def.DimTarget,
                                MaxClashes        = def.MaxClashes,
                            };

                            var engine = new ClashEngine(opts, (t, s) => Log(t, s));
                            var r = engine.Run(doc, ViewIds,
                                EffectiveSpec(def.Group1), EffectiveSpec(def.Group2));

                            pass += r.Markers;
                            fail += r.Fails;
                            done++;
                        }

                        tx.Commit();
                        Log($"Marking done — {pass} marker(s), {fail} failure(s).", pass > 0 ? "pass" : "fail");
                    }

                    // Dimension pass runs after marking, read-only (no transaction).
                    if (RunDimensionPass)
                    {
                        Progress(85, pass, fail, skip);
                        Log("Running dimension pass (discovery)…", "info");
                        ClashDimensionPass.Run(doc, ViewIds, (t, s) => Log(t, s));
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Fatal: {ex.Message}", "fail");
                fail++;
            }

            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
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
