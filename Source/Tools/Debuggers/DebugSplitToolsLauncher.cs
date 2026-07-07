using System.Threading;
using System.Windows.Threading;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Debuggers
{
    /// <summary>
    /// Opens the split-tools debug harness (see DebugSplitToolsViewModel) on its own dedicated
    /// STA thread, mirroring every other Lemoine tool window's launch pattern — but triggered
    /// from a button inside GlobalSettingsWindow rather than a Revit ribbon command, since this
    /// harness has no document-derived data to pre-fetch (each test re-queries the model itself
    /// when its "Run Test" button is clicked). Singleton: re-activates the existing window
    /// instead of opening a second one.
    /// </summary>
    internal static class DebugSplitToolsLauncher
    {
        private static StepFlowWindow? _window;

        internal static void Launch()
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
                    if (_window != null) return;
                }
                catch { _window = null; }
            }

            var vm = new DebugSplitToolsViewModel(App.DebugSplitToolsHandler!, App.DebugSplitToolsEvent!);
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
        }
    }
}
