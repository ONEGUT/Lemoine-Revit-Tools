using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.ModifyElements;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SplitByGridCommand : IExternalCommand
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

            var uidoc      = commandData.Application.ActiveUIDocument;
            var doc        = uidoc.Document;
            var activeViewId = uidoc.ActiveView?.Id;

            // ── Pre-selection (E) ──────────────────────────────────────────────
            var supportedBics  = new HashSet<long>(SplitElementsShared.GridSplitCategories.Select(c => (long)c.Cat));
            var catLabelLookup = SplitElementsShared.GridSplitCategories.ToDictionary(c => (long)c.Cat, c => c.Label);
            var rawSelection   = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(e => e?.Category?.Id != null && supportedBics.Contains(e.Category.Id.Value))
                .ToList();
            var preSelectedIds  = rawSelection.Select(e => e.Id).ToList();
            var preSelectedCats = rawSelection
                .Select(e => catLabelLookup[e.Category.Id.Value])
                .Distinct().ToList();

            // ── Element counts (A) ─────────────────────────────────────────────
            var counts = SplitElementsShared.GridSplitCategories.ToDictionary(
                c => c.Label,
                c => new FilteredElementCollector(doc)
                    .OfCategoryId(new ElementId(c.Cat))
                    .WhereElementIsNotElementType()
                    .ToList().Count);

            var allGrids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .Where(g => g.Curve is Line)
                .OrderBy(g => g.Name)
                .ToList();

            var vm    = new SplitByGridViewModel(
                App.SplitByGridHandler!, App.SplitByGridEvent!,
                allGrids, counts, activeViewId,
                preSelectedIds, preSelectedCats);

            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new System.Threading.Thread(() =>
            {
                win = new StepFlowWindow(vm);
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
