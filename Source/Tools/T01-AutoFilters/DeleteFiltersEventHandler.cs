using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// Removes selected filters from the active view (detach only — not deleted from project).
    /// </summary>
    public class DeleteFiltersEventHandler : IExternalEventHandler
    {
        // ── Set by ViewModel before Raise() ────────────────────────────────────
        public IList<string> SelectedFilterNames { get; set; } = new List<string>();

        // ── Callbacks ──────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.DeleteFiltersEventHandler";

        public void Execute(UIApplication app)
        {
            var view = app.ActiveUIDocument.ActiveView;
            var doc  = app.ActiveUIDocument.Document;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                if (SelectedFilterNames == null || SelectedFilterNames.Count == 0)
                {
                    Log("No filters selected.", "fail");
                    fail++;
                    Progress(100, pass, fail, skip);
                    Complete(pass, fail, skip);
                    return;
                }

                // Build name → ElementId lookup from active view's current filters (case-insensitive
                // to match the rest of the filter system).
                var appliedFilters = view.GetFilters()
                    .Select(id => doc.GetElement(id) as ParameterFilterElement)
                    .Where(f => f != null)
                    .Select(f => f!)
                    .ToDictionary(f => f.Name, f => f.Id, StringComparer.OrdinalIgnoreCase);

                var toRemove = SelectedFilterNames
                    .Where(n => appliedFilters.ContainsKey(n))
                    .Select(n => appliedFilters[n])
                    .ToList();

                int total = toRemove.Count;
                int done  = 0;

                using (var tx = new Transaction(doc, "Remove Filters from View"))
                {
                    var fho = tx.GetFailureHandlingOptions();
                    fho.SetClearAfterRollback(true);
                    fho.SetDelayedMiniWarnings(true);
                    tx.SetFailureHandlingOptions(fho);
                    tx.Start();

                    foreach (var id in toRemove)
                    {
                        try
                        {
                            view.RemoveFilter(id);
                            pass++;
                        }
                        catch (Exception ex)
                        {
                            Log($"Failed to remove filter {id}: {ex.Message}", "fail");
                            fail++;
                        }
                        done++;
                        Progress((int)(done * 90.0 / total), pass, fail, skip);
                    }

                    tx.Commit();
                }

                Log($"Complete — {pass} removed, {fail} failed.", "pass");
            }
            catch (Exception ex)
            {
                LemoineLog.Error("AutoFilters: delete filters aborted", ex); Log($"Error: {ex.Message}", "fail");
                fail++;
            }

            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        private void Log(string text, string status) => PushLog?.Invoke(text, status);
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
