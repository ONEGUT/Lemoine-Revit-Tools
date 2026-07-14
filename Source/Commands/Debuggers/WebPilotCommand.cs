using System;
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
    /// ReadOnly transaction mode.
    ///
    /// The window is created on the shared <see cref="WebUiThread"/> (not a per-command STA
    /// thread) so the WebView2 browser process stays warm across opens - only the first web-tool
    /// open of the session is cold. The window's Closed handler just clears the static; it must
    /// NOT shut down the dispatcher, which lives for the whole session.
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
                    WebUiThread.Invoke(() =>
                    {
                        var w = _window;
                        if (w != null && w.IsVisible) w.Activate();
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

            var vm = new WebPilotTool(App.WebPilotHandler!, App.WebPilotEvent!);
            WebUiThread.Invoke(() =>
            {
                var win = new WebStepFlowWindow(vm);
                win.Closed += (s, e) => { _window = null; }; // thread persists - no InvokeShutdown
                win.Show();
                _window = win;
            });
            return Result.Succeeded;
        }
    }
}
