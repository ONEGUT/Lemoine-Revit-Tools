using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.CopyLinear
{
    /// <summary>
    /// Phase-1 (read-only) scan: enumerates the chosen source document's category-filtered elements
    /// and reports the distinct System / Size / Phase keyword values that actually exist, so the
    /// step-1 filter chips only ever offer real values (never a hand-typed string). Raised as an
    /// ExternalEvent because reading a link document must happen on Revit's main thread.
    /// Also services the Replace step's family-parameter lookup (the length-parameter dropdown):
    /// set <see cref="FamilyParamRequest"/> before Raise and the writable double-storage instance
    /// parameter names of that family come back through <see cref="OnFamilyParams"/>.
    /// </summary>
    public sealed class CopyLinearScanHandler : IExternalEventHandler
    {
        // Inputs (set before Raise) — note: filter keyword lists on the spec are ignored here;
        // the scan reports every value so the user can then choose among them.
        public CopyLinearSourceSpec Spec { get; set; } = new CopyLinearSourceSpec();

        // True only when a source scan is wanted; a family-parameter request alone must not
        // run the source scan with a stale spec and clobber the filter UI.
        public bool ScanRequested { get; set; }

        // Family-parameter lookup job: the FamilySymbol ElementId value to inspect, or null.
        public long? FamilyParamRequest { get; set; }

        // Callbacks
        public Action<CopyLinearScanResult>? OnScanned { get; set; }
        public Action<string>?               OnError   { get; set; }
        public Action<long, List<string>>?   OnFamilyParams { get; set; }   // (symbolId, names)

        public string GetName() => "LemoineTools.Tools.CopyLinear.CopyLinearScanHandler";

        public void Execute(UIApplication app)
        {
            // Both jobs can be pending at once (ExternalEvent.Raise coalesces) — service each.
            long? famReq = FamilyParamRequest;
            if (famReq.HasValue)
            {
                FamilyParamRequest = null;
                ServeFamilyParams(app, famReq.Value);
            }

            if (!ScanRequested) return;
            ScanRequested = false;
            try
            {
                var hostDoc = app.ActiveUIDocument?.Document;
                if (hostDoc == null) { OnError?.Invoke("No active document."); return; }

                var src = CopyLinearSource.Resolve(hostDoc, Spec.LinkInstId);
                if (src?.Doc == null) { OnError?.Invoke("Source document is not loaded."); return; }

                var elems = CopyLinearSource.Collect(src.Doc, Spec);

                // Collect distinct formatted values for every non-double parameter across all elements.
                // Double-storage params (dimensions, areas) are skipped — each element has a unique
                // value, producing lists that are too long to be useful as filter chips.
                var paramValues = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                int scanned = 0;
                foreach (var el in elems)
                {
                    if (LemoineRun.CancelRequested)
                    {
                        OnError?.Invoke($"Stopped by user — {scanned} of {elems.Count} scanned; partial values returned.");
                        break;   // falls through to the OnScanned callback with what was collected
                    }
                    scanned++;
                    try
                    {
                        foreach (Parameter p in el.Parameters)
                        {
                            try
                            {
                                if (p == null || p.StorageType == StorageType.None || p.StorageType == StorageType.Double) continue;
                                var def = p.Definition;
                                if (def == null) continue;
                                string name = def.Name;
                                if (string.IsNullOrWhiteSpace(name)) continue;

                                string val = CopyLinearSource.ReadParamDisplay(p);
                                if (!paramValues.TryGetValue(name, out var set))
                                    paramValues[name] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                                set.Add(val);
                            }
                            catch (Exception ex) { LemoineLog.Swallowed("CopyLinearScan: read param", ex); }
                        }
                    }
                    catch (Exception ex) { LemoineLog.Swallowed("CopyLinearScan: iterate element params", ex); }
                }

                // Keep only params with ≥2 distinct values ("value" and "(no value)" count as two).
                var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var kv in paramValues.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    if (kv.Value.Count >= 2)
                        result[kv.Key] = kv.Value.ToList();

                OnScanned?.Invoke(new CopyLinearScanResult
                {
                    ParameterValues = result,
                    ElementCount    = elems.Count,
                });
            }
            catch (Exception ex)
            {
                LemoineLog.Error("CopyLinearScanHandler.Execute", ex);
                OnError?.Invoke(ex.Message);
            }
            finally
            {
                // Session-long static handler — drop the scan's payload.
                Spec = new CopyLinearSourceSpec();
            }
        }

        // Names of the family's writable double-storage instance parameters (length-like values
        // an interval can be written into). Fast path: read them off any existing instance of the
        // family in the host. Fallback: open the family document once via EditFamily — instance
        // parameter definitions are not reachable from the FamilySymbol alone. Always calls back
        // (an empty list on failure) so the UI never waits forever.
        private void ServeFamilyParams(UIApplication app, long symbolId)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var doc    = app.ActiveUIDocument?.Document;
                var symbol = doc?.GetElement(new ElementId(symbolId)) as FamilySymbol;
                var family = symbol?.Family;
                if (doc == null || family == null) { OnFamilyParams?.Invoke(symbolId, new List<string>()); return; }

                var sample = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .FirstOrDefault(fi => fi.Symbol?.Family?.Id == family.Id);

                if (sample != null)
                {
                    foreach (Parameter p in sample.Parameters)
                        if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double
                            && !string.IsNullOrWhiteSpace(p.Definition?.Name))
                            names.Add(p.Definition!.Name);
                }
                else if (family.IsEditable)
                {
                    Document? famDoc = null;
                    try
                    {
                        famDoc = doc.EditFamily(family);
                        foreach (FamilyParameter fp in famDoc.FamilyManager.GetParameters())
                            if (fp != null && fp.IsInstance && fp.StorageType == StorageType.Double
                                && !fp.IsReporting && string.IsNullOrEmpty(fp.Formula)
                                && !string.IsNullOrWhiteSpace(fp.Definition?.Name))
                                names.Add(fp.Definition!.Name);
                    }
                    finally
                    {
                        try { famDoc?.Close(false); }
                        catch (Exception ex) { LemoineLog.Swallowed("CopyLinearScan: close family doc", ex); }
                    }
                }
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("CopyLinearScan: family parameters", ex);
            }
            OnFamilyParams?.Invoke(symbolId, names.ToList());
        }
    }
}
