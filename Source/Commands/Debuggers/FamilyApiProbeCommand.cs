using System;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Tools.Debuggers;

namespace LemoineTools.Commands
{
    /// <summary>
    /// TEMPORARY Phase-0 harness launcher for the Family Modification Tools plan.
    /// Delete this file, FamilyApiProbeViewModel, FamilyApiProbeHandler, and the temporary
    /// ribbon button in App.cs once the probe results are captured.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class FamilyApiProbeCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

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
                    DiagnosticsLog.Swallowed("FamilyApiProbeCommand: reactivate window", ex);
                    _window = null;
                }
            }

            FamilyApiProbeViewModel BuildTool()
                => new FamilyApiProbeViewModel(App.FamilyApiProbeHandler, App.FamilyApiProbeEvent);

            var vm    = BuildTool();
            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new StepFlowWindow(vm, BuildTool);
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
