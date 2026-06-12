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

                var systems = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var sizes   = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var phases  = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var el in elems)
                {
                    systems.Add(CopyLinearSource.ReadSystem(el));
                    sizes.Add(CopyLinearSource.ReadSize(el));
                    phases.Add(CopyLinearSource.ReadPhase(src.Doc, el));
                }

                OnScanned?.Invoke(new CopyLinearScanResult
                {
                    SystemTypes  = systems.ToList(),
                    Sizes        = sizes.ToList(),
                    Phases       = phases.ToList(),
                    ElementCount = elems.Count,
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
    }
}
