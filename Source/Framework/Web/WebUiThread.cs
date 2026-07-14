using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace LemoineTools.Framework.Web
{
    // =========================================================================
    // WebUiThread - one persistent STA thread that hosts every WebStepFlowWindow.
    //
    // Why: each per-window STA thread that dies on close also kills the WebView2
    // browser process (msedgewebview2.exe), so EVERY window open paid a full cold
    // start. Running all web tool windows on a single long-lived thread means:
    //   * the [ThreadStatic] WebHost environment is created ONCE and reused, and
    //   * a persistent hidden "warm" control (created on first use) keeps the
    //     browser process alive for the session,
    // so only the FIRST web-tool open is cold; every open after it is instant.
    //
    // Nothing is created until the first web tool opens (no idle/startup cost). The
    // thread + one browser process then live until Revit closes. Multiple web tool
    // windows share this one dispatcher, which WPF supports.
    // =========================================================================
    public static class WebUiThread
    {
        private static Dispatcher? _dispatcher;
        private static readonly object _gate = new object();
        private static Window?   _warmHost;   // persistent hidden window holding the warm control
        private static WebView2? _warmView;   // never disposed for the session - keeps the process alive

        /// <summary>Runs <paramref name="action"/> on the shared web-UI thread, starting the
        /// thread (and warming the browser process) on first call. Blocks until it completes.</summary>
        public static void Invoke(Action action) => EnsureStarted().Invoke(action);

        private static Dispatcher EnsureStarted()
        {
            lock (_gate)
            {
                if (_dispatcher != null) return _dispatcher;

                var ready = new ManualResetEventSlim(false);
                Dispatcher? created = null;
                var thread = new Thread(() =>
                {
                    created = Dispatcher.CurrentDispatcher;
                    // Establish a SynchronizationContext so async awaits on this thread (WebView2
                    // init, warm-up) resume back here rather than on a thread-pool thread.
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(created));
                    ready.Set();
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name         = "LemoineWebUI",
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.Wait();

                _dispatcher = created;
                _dispatcher!.BeginInvoke(new Action(WarmUp)); // warm the browser process off the critical path
                return _dispatcher;
            }
        }

        // Creates a tiny offscreen host window with a WebView2 and initializes it, holding the
        // browser process alive for the session so tool windows attach to a warm process. Runs on
        // the web-UI thread. Never throws - a warm-up failure just means the first tool window
        // cold-starts as before.
        private static async void WarmUp()
        {
            try
            {
                var env = await WebHost.EnvironmentAsync();
                _warmHost = new Window
                {
                    Width         = 1,
                    Height        = 1,
                    Left          = -32000,
                    Top           = -32000,
                    WindowStyle   = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                };
                var grid = new Grid();
                _warmView = new WebView2();
                grid.Children.Add(_warmView);
                _warmHost.Content = grid;
                _warmHost.Show();                            // realize an HWND so the controller can init
                await _warmView.EnsureCoreWebView2Async(env);
                DiagnosticsLog.Info("WebUiThread: browser process warmed for the session");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("WebUiThread: warm-up", ex);
            }
        }
    }
}
