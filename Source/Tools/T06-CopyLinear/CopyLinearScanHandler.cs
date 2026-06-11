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
    /// </summary>
    public sealed class CopyLinearScanHandler : IExternalEventHandler
    {
        // Inputs (set before Raise) — note: filter keyword lists on the spec are ignored here;
        // the scan reports every value so the user can then choose among them.
        public CopyLinearSourceSpec Spec { get; set; } = new CopyLinearSourceSpec();

        // Callbacks
        public Action<CopyLinearScanResult>? OnScanned { get; set; }
        public Action<string>?               OnError   { get; set; }

        public string GetName() => "LemoineTools.Tools.CopyLinear.CopyLinearScanHandler";

        public void Execute(UIApplication app)
        {
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
                foreach (var el in elems)
                {
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
        }
    }
}
