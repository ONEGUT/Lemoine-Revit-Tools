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
            Open(commandData.Application);
            return Result.Succeeded;
        }

        /// <summary>
        /// Opens (or brings to front) the Delete Filters from Project window. Must run on Revit's
        /// main thread — it enumerates the project's ParameterFilterElements before spawning the
        /// window on its own STA thread. Shared by the ribbon command and the Auto Filters window's
        /// "Delete from Project" button (via an ExternalEvent).
        /// </summary>
        public static void Open(UIApplication uiApp)
        {
            // Bring existing window to front via Dispatcher (STA-safe)
            if (_window != null)
            {
                try
                {
                    _window.Dispatcher.Invoke(() =>
                    {
                        if (_window.IsVisible) _window.Activate();
                        else _window = null;
                    });
                    if (_window != null) return;
                }
                catch (System.Exception ex)
                {
                    LemoineLog.Swallowed("DeleteFromProject: bring-to-front", ex);
                    _window = null;
                }
            }

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
        }
    }
}
