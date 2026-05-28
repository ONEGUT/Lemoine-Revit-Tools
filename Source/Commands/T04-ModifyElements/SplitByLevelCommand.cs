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
    public class SplitByLevelCommand : IExternalCommand
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

            var uidoc        = commandData.Application.ActiveUIDocument;
            var doc          = uidoc.Document;
            var activeViewId = uidoc.ActiveView?.Id;

            // Dynamic category counts: all model categories present in the document.
            var counts = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(e => e.Category?.CategoryType == CategoryType.Model && e.Category.Name != null)
                .GroupBy(e => e.Category!.Name)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Count());

            // Pre-selection: accept any selected model element.
            var rawSelection = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(e => e?.Category?.CategoryType == CategoryType.Model && e.Category.Name != null)
                .ToList();
            var preSelectedIds  = rawSelection.Select(e => e.Id).ToList();
            var preSelectedCats = rawSelection
                .Select(e => e.Category!.Name)
                .Distinct().ToList();

            var allLevels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            var vm = new SplitByLevelViewModel(
                App.SplitByLevelHandler!, App.SplitByLevelEvent!,
                allLevels, counts, activeViewId,
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
