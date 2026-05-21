using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Captures all project filters and views on the main thread,
    /// then launches the Apply Filters to Views step-flow window.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ApplyFiltersToViewsLaunchCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            // FIX: bring existing window to front via Dispatcher (STA-safe)
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

            var doc = commandData.Application.ActiveUIDocument.Document;

            // All ParameterFilterElements in the project
            var filterNames = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .OrderBy(f => f.Name)
                .Select(f => f.Name)
                .ToList();

            // All views that support graphic overrides and are not templates
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.AreGraphicsOverridesAllowed())
                .OrderBy(v => v.ViewType.ToString())
                .ThenBy(v => v.Name)
                .Select(v => (v.Name, v.ViewType.ToString()))
                .ToList();

            var vm = new ApplyFiltersToViewsViewModel(
                App.ApplyFiltersToViewsHandler!,
                App.ApplyFiltersToViewsEvent!,
                filterNames,
                views);

            // FIX: open window on dedicated STA thread so real-time progress works
            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new Thread(() =>
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
