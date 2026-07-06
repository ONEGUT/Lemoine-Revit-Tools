using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Scope Box Manager window on a dedicated STA thread. The window fetches
    /// its own data via App.ScopeBoxManagerScanEvent once shown (nothing needs capturing
    /// on the main thread here beyond confirming a document is open).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ScopeBoxManagerCommand : IExternalCommand
    {
        private static ScopeBoxManagerWindow? _window;

        public Result Execute(ExternalCommandData commandData,
                              ref string message, ElementSet elements)
        {
            // Bring existing window to front
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

            var ready = new ManualResetEventSlim(false);
            ScopeBoxManagerWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new ScopeBoxManagerWindow();
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
