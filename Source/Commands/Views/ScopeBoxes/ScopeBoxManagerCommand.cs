using System;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Scope Box Manager window on a dedicated STA thread. The window fetches
    /// its own data via App.ScopeBoxManagerScanEvent once shown (nothing needs capturing
    /// on the main thread here beyond confirming a document is open). Behind the Web UI flag
    /// the HTML version (<see cref="WebScopeBoxManagerWindow"/>) opens instead; the WPF window
    /// stays as the fallback (rule R25).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ScopeBoxManagerCommand : IExternalCommand
    {
        private static ScopeBoxManagerWindow?    _window;
        private static WebScopeBoxManagerWindow? _webWindow;

        public Result Execute(ExternalCommandData commandData,
                              ref string message, ElementSet elements)
        {
            bool web = WebUiSettings.Instance.Enabled;

            // Bring an existing window (of the active flavour) to front.
            if (web && _webWindow != null)
            {
                try
                {
                    WebUiThread.Invoke(() =>
                    {
                        var w = _webWindow;
                        if (w != null && w.IsVisible) w.Activate();
                        else _webWindow = null;
                    });
                    if (_webWindow != null) return Result.Succeeded;
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed("ScopeBoxManager: activate web window", ex); _webWindow = null; }
            }
            else if (!web && _window != null)
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

            if (web)
            {
                WebUiThread.Invoke(() =>
                {
                    var w = new WebScopeBoxManagerWindow();
                    w.Closed += (s, e) => { _webWindow = null; };
                    w.Show();
                    _webWindow = w;
                });
                return Result.Succeeded;
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
