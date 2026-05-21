using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// Permanently deletes selected ParameterFilterElements from the project.
    /// Revit automatically removes them from all views that reference them.
    /// </summary>
    public class DeleteFiltersFromProjectEventHandler : IExternalEventHandler
    {
        // ── Set by ViewModel before Raise() ────────────────────────────────────
        public IList<string> SelectedFilterNames { get; set; } = new List<string>();

        // ── Callbacks ──────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.DeleteFiltersFromProjectEventHandler";

        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                Delete(doc, ref pass, ref fail, ref skip);
            }
            catch (Exception ex)
            {
                Log($"Fatal: {ex.Message}", "fail");
                fail++;
            }

            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        // ── Core logic ──────────────────────────────────────────────────────────
        private void Delete(Document doc, ref int pass, ref int fail, ref int skip)
        {
            if (SelectedFilterNames == null || SelectedFilterNames.Count == 0)
            {
                Log("No filters selected for deletion.", "fail");
                fail++;
                return;
            }

            // Resolve names to ElementIds
            var filterMap = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .Where(f => SelectedFilterNames.Contains(f.Name))
                .ToDictionary(f => f.Name, f => f.Id);

            var notFound = SelectedFilterNames.Except(filterMap.Keys).ToList();
            foreach (var n in notFound)
            {
                Log($"Not found in project: '{n}' — skipped.", "info");
                skip++;
            }

            if (filterMap.Count == 0)
            {
                Log("None of the selected filters exist in this project.", "fail");
                fail++;
                return;
            }

            Log($"Deleting {filterMap.Count} filter(s)…", "info");

            int total = filterMap.Count;
            int done  = 0;

            using (var tx = new Transaction(doc, "Delete Filters from Project"))
            {
                var fho = tx.GetFailureHandlingOptions();
                fho.SetClearAfterRollback(true);
                fho.SetDelayedMiniWarnings(true);
                tx.SetFailureHandlingOptions(fho);
                tx.Start();

                foreach (var pair in filterMap)
                {
                    string    name = pair.Key;
                    ElementId id   = pair.Value;
                    try
                    {
                        doc.Delete(id);
                        Log($"Deleted: '{name}'", "info");
                        pass++;
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to delete '{name}': {ex.Message}", "fail");
                        fail++;
                    }

                    done++;
                    Progress((int)(done * 90.0 / total), pass, fail, skip);
                }

                tx.Commit();
            }

            Log($"Complete — {pass} deleted, {fail} failed, {skip} skipped.", "pass");
        }

        // ── Callback wrappers ────────────────────────────────────────────────────
        private void Log(string text, string status) => PushLog?.Invoke(text, status);
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
