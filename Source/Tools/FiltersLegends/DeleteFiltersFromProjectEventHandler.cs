using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

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
                DiagnosticsLog.Error("AutoFilters: delete filters from project aborted", ex); Log(AppStrings.T("autofilters.deleteFromProject.log.error", ex.Message), "fail");
                fail++;
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                SelectedFilterNames = new List<string>();
            }

            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        // ── Core logic ──────────────────────────────────────────────────────────
        private void Delete(Document doc, ref int pass, ref int fail, ref int skip)
        {
            if (SelectedFilterNames == null || SelectedFilterNames.Count == 0)
            {
                Log(AppStrings.T("autofilters.deleteFromProject.log.noneSelected"), "fail");
                fail++;
                return;
            }

            // Resolve names to ElementIds (case-insensitive, matching the rest of the filter system).
            var selectedSet = new HashSet<string>(SelectedFilterNames, StringComparer.OrdinalIgnoreCase);
            var filterMap = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .Where(f => selectedSet.Contains(f.Name))
                .ToDictionary(f => f.Name, f => f.Id, StringComparer.OrdinalIgnoreCase);

            var notFound = SelectedFilterNames.Where(n => !filterMap.ContainsKey(n)).ToList();
            foreach (var n in notFound)
            {
                Log(AppStrings.T("autofilters.deleteFromProject.log.notFound", n), "info");
                skip++;
            }

            if (filterMap.Count == 0)
            {
                Log(AppStrings.T("autofilters.deleteFromProject.log.noneExist"), "fail");
                fail++;
                return;
            }

            Log(AppStrings.T("autofilters.deleteFromProject.log.deletingN", filterMap.Count), "info");

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
                    if (RunState.CancelRequested)
                    {
                        Log(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                        break;
                    }

                    string    name = pair.Key;
                    ElementId id   = pair.Value;
                    try
                    {
                        doc.Delete(id);
                        Log(AppStrings.T("autofilters.deleteFromProject.log.deleted", name), "info");
                        pass++;
                    }
                    catch (Exception ex)
                    {
                        Log(AppStrings.T("autofilters.deleteFromProject.log.deleteFailed", name, ex.Message), "fail");
                        fail++;
                    }

                    done++;
                    Progress((int)(done * 90.0 / total), pass, fail, skip);
                }

                tx.Commit();
            }

            Log(AppStrings.T("autofilters.deleteFromProject.log.complete", pass, fail, skip), "pass");
        }

        // ── Callback wrappers ────────────────────────────────────────────────────
        private void Log(string text, string status) => PushLog?.Invoke(text, status);
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
