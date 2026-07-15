using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;
using LemoineTools.Tools.Ceilings;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Projected Ceiling Grids window on a dedicated STA thread so
    /// its WPF dispatcher runs independently from Revit's main thread.
    ///
    /// With a separate dispatcher, BeginInvoke calls in StartRun() queue UI
    /// updates onto the window's thread while Execute() keeps processing —
    /// giving true real-time progress bar and log updates during the run.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProjectedCeilingGridsCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // If a window is already alive, bring it to front via its own dispatcher
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

            ProjectedCeilingGridsViewModel BuildTool()
                => new ProjectedCeilingGridsViewModel(App.ProjectHandler!, App.ProjectEvent!);
            if (WebToolLauncher.Enabled)
            {
                WebToolLauncher.Open("projectedCeilingGrids", () =>
                {
                    return new ProjectedCeilingGridsWebTool(App.ProjectHandler!, App.ProjectEvent!);
                });
                return Result.Succeeded;
            }
            var vm = BuildTool();

            // ManualResetEvent lets the main thread wait until the STA thread
            // has created and shown the window before continuing.
            var ready           = new ManualResetEventSlim(false);
            StepFlowWindow? win  = null;

            var thread = new Thread(() =>
            {
                win = new StepFlowWindow(vm, BuildTool);
                win.Closed += (s, e) =>
                {
                    _window = null;
                    Dispatcher.CurrentDispatcher.InvokeShutdown();  // clean up the thread
                };
                win.Show();
                ready.Set();                  // unblock the main thread
                Dispatcher.Run();             // pump messages on this dedicated thread
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;       // thread dies when Revit closes
            thread.Start();

            ready.Wait();                     // wait until window is visible
            _window = win;

            return Result.Succeeded;
        }
    }
}
