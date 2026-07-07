using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Tools.ModifyElements;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SplitByCellCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            if (_window != null)
            {
                try
                {
                    _window.Dispatcher.Invoke(() =>
                    {
                        if (_window.IsVisible) _window.Activate();
                        else _window = null;
                    });
                    if (_window != null) return Result.Succeeded;
                }
                catch { _window = null; }
            }

            var uiApp = commandData.Application;
            SplitByCellViewModel BuildTool()
            {
                var uidoc      = uiApp.ActiveUIDocument;
                var doc        = uidoc.Document;
                var activeViewId = uidoc.ActiveView?.Id;

                // ── Element counts (A) — active view scope matches Cell's runtime scope ──
                var counts = new Dictionary<string, int>();
                if (activeViewId != null)
                {
                    foreach (var bic in SplitByCellHelpers.SupportedCategories)
                    {
                        if (!SplitByCellViewModel.CatLabels.TryGetValue(bic, out string? label)) continue;
                        counts[label] = new FilteredElementCollector(doc, activeViewId)
                            .OfCategoryId(new ElementId(bic))
                            .WhereElementIsNotElementType()
                            .ToList().Count;
                    }
                }

                // ── Pre-selection (E) ──────────────────────────────────────────────
                var supportedBics = new HashSet<BuiltInCategory>(SplitByCellHelpers.SupportedCategories);
                var rawSelection  = uidoc.Selection.GetElementIds()
                    .Select(id => doc.GetElement(id))
                    .Where(e => e?.Category?.Id != null &&
                                supportedBics.Contains((BuiltInCategory)(int)e.Category.Id.Value))
                    .ToList();
                var preSelectedIds = rawSelection.Select(e => e.Id).ToList();
                var preSelectedCats = rawSelection
                    .Select(e => (BuiltInCategory)(int)e.Category.Id.Value)
                    .Where(bic => SplitByCellViewModel.CatLabels.ContainsKey(bic))
                    .Select(bic => SplitByCellViewModel.CatLabels[bic])
                    .Distinct().ToList();

                var vm    = new SplitByCellViewModel(
                    App.SplitByCellHandler!, App.SplitByCellEvent!,
                    counts, preSelectedIds, preSelectedCats);

                return vm;
            }
            var vm = BuildTool();
            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new System.Threading.Thread(() =>
            {
                win = new StepFlowWindow(vm, BuildTool);
                win.Closed += (s, e) =>
                {
                    _window = null;
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                };
                win.Show();
                ready.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            ready.Wait();
            _window = win;
            return Result.Succeeded;
        }
    }
}
