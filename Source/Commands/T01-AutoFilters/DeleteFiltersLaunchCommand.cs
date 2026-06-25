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
    /// Captures filters on the active view then launches the Remove Filters step-flow window.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DeleteFiltersLaunchCommand : IExternalCommand
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
            DeleteFiltersViewModel BuildTool()
            {
                var uiDoc = uiApp.ActiveUIDocument;
                var doc   = uiDoc.Document;
                var view  = uiDoc.ActiveView;

                // Capture current view's filter names on the main thread
                var filterNames = view.GetFilters()
                    .Select(id => doc.GetElement(id) as ParameterFilterElement)
                    .Where(f => f != null)
                    .Select(f => f!)
                    .OrderBy(f => f.Name)
                    .Select(f => f.Name)
                    .ToList();

                var vm = new DeleteFiltersViewModel(
                    App.DeleteFiltersHandler!,
                    App.DeleteFiltersEvent!,
                    filterNames,
                    view.Name);

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
