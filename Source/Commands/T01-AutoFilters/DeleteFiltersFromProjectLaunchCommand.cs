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
    /// Captures all ParameterFilterElements in the project on the main thread,
    /// then launches the Delete Filters from Project step-flow window.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DeleteFiltersFromProjectLaunchCommand : IExternalCommand
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

            var uiApp = commandData.Application;
            DeleteFiltersFromProjectViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var filterNames = new FilteredElementCollector(doc)
                    .OfClass(typeof(ParameterFilterElement))
                    .Cast<ParameterFilterElement>()
                    .OrderBy(f => f.Name)
                    .Select(f => f.Name)
                    .ToList();

                var vm = new DeleteFiltersFromProjectViewModel(
                    App.DeleteFiltersFromProjectHandler!,
                    App.DeleteFiltersFromProjectEvent!,
                    filterNames);

                // FIX: open window on dedicated STA thread so real-time progress works
                return vm;
            }
            var vm = BuildTool();
            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new Thread(() =>
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
