using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.CopyLinear;

namespace LemoineTools.Tools.CopyFromLink
{
    /// <summary>
    /// Phase-1 (read-only) scan: enumerates the chosen link's category-filtered, workset-filtered
    /// elements and reports the distinct "Family: Type" keys that actually exist, grouped by category
    /// display name, so the family step only ever offers real types. Raised as an ExternalEvent
    /// because reading a link document must happen on Revit's main thread.
    /// </summary>
    public sealed class CopyFromLinkScanHandler : IExternalEventHandler
    {
        // Inputs (set before Raise).
        public CopyFromLinkSpec Spec { get; set; } = new CopyFromLinkSpec();
        public bool ScanRequested { get; set; }

        // Callbacks.
        public Action<CopyFromLinkScanResult>? OnScanned { get; set; }
        public Action<string>?                 OnError   { get; set; }

        public string GetName() => "LemoineTools.Tools.CopyFromLink.CopyFromLinkScanHandler";

        public void Execute(UIApplication app)
        {
            if (!ScanRequested) return;
            ScanRequested = false;
            try
            {
                var hostDoc = app.ActiveUIDocument?.Document;
                if (hostDoc == null) { OnError?.Invoke("No active document."); return; }

                var src = CopyFromLinkSource.Resolve(hostDoc, Spec.LinkInstId);
                if (src?.Doc == null) { OnError?.Invoke("Source link is not loaded."); return; }

                var elems = CopyLinearSource.Collect(src.Doc, CopyFromLinkSource.ToLinearSpec(Spec));

                var byCat = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                int scanned = 0;
                foreach (var el in elems)
                {
                    if (LemoineRun.CancelRequested)
                    {
                        OnError?.Invoke($"Stopped by user — {scanned} of {elems.Count} scanned; partial results returned.");
                        break;
                    }
                    scanned++;
                    try
                    {
                        string cat = CopyFromLinkSource.CategoryDisplay(el);
                        string key = CopyFromLinkSource.TypeKey(src.Doc, el);
                        if (!byCat.TryGetValue(cat, out var set))
                            byCat[cat] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(key);
                    }
                    catch (Exception ex) { LemoineLog.Swallowed("CopyFromLinkScan: read element type", ex); }
                }

                var result = new CopyFromLinkScanResult { ElementCount = elems.Count };
                foreach (var kv in byCat.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    result.TypesByCategory[kv.Key] = kv.Value.ToList();

                OnScanned?.Invoke(result);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("CopyFromLinkScanHandler.Execute", ex);
                OnError?.Invoke(ex.Message);
            }
            finally
            {
                // Session-long static handler — drop the scan's payload.
                Spec = new CopyFromLinkSpec();
            }
        }
    }
}
