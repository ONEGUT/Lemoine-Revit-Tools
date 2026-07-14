using System;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;
using LemoineTools.Tools.Debuggers;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Phase 2b pilot in a WebStepFlowWindow: the first real IWebTool running
    /// end-to-end through the HTML StepFlow shell. Read-only run (element count), so
    /// ReadOnly transaction mode. STA-thread window pattern mirrors the other tool commands.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class WebPilotCommand : IExternalCommand
    {
        private static WebStepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
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
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("WebPilotCommand: activate existing window", ex);
                    _window = null;
                }
            }

            var vm    = new WebPilotTool(App.WebPilotHandler!, App.WebPilotEvent!);
            var ready = new ManualResetEventSlim(false);
            WebStepFlowWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new WebStepFlowWindow(vm);
                win.Closed += (s, e) => { _window = null; Dispatcher.CurrentDispatcher.InvokeShutdown(); };
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
