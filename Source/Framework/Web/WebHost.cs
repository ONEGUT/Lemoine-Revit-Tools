using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// The shared WebView2 host layer. Every WebView2 control in the plugin is created
    /// through here so the four historical silent-blank-control failure modes (see
    /// plan-webview2-ui-migration.md rules R1-R8) can never be regressed piecemeal:
    ///
    ///   R1  the environment always uses an explicit, writable user-data folder
    ///       (%LocalAppData%\LemoineTools\WebView2) - never the default beside Revit.exe.
    ///   R3  nothing touches a control before EnsureCoreWebView2Async completes.
    ///   R5  one environment PER WINDOW (per STA thread), shared by every control in that
    ///       window - see below for why it can't be process-wide.
    ///   R7  production CoreWebView2Settings applied once a control is ready.
    ///   R8  DefaultBackgroundColor = active theme Bg, so there is no white flash.
    ///
    /// NEVER call CoreWebView2Environment.CreateAsync anywhere else (rule R36).
    ///
    /// Why per-thread, not process-wide: every tool window runs on its own dedicated STA
    /// thread that shuts down when the window closes. CoreWebView2Environment is an STA
    /// thread-affine COM object, so a single cached instance handed to a SECOND window
    /// (a new STA thread, after the first window's thread died) throws
    /// InvalidComObjectException ("COM object separated from its underlying RCW") the moment
    /// it is touched. Caching the environment in a [ThreadStatic] field gives each window its
    /// own environment (all pointing at the same user-data folder with identical options,
    /// which WebView2 permits) and lets it die cleanly with its creating thread, while
    /// controls WITHIN one window still share it.
    /// </summary>
    public static class WebHost
    {
        [ThreadStatic] private static CoreWebView2Environment? _env;
        [ThreadStatic] private static Task<CoreWebView2Environment>? _envTask;

        /// <summary>Absolute path of the shared, writable user-data folder (rule R1).</summary>
        public static string UserDataFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LemoineTools", "WebView2");

        /// <summary>
        /// The environment for the CALLING window's STA thread, created once per thread on
        /// first use (rule R5). Must be awaited on the window's dispatcher thread so the
        /// continuation resumes there and the COM object stays thread-affine. Throws if
        /// creation fails so the caller can surface it (never silent).
        /// </summary>
        public static Task<CoreWebView2Environment> EnvironmentAsync()
        {
            // [ThreadStatic] fields are per-thread, so no lock is needed: a single STA thread
            // runs this sequentially between awaits.
            if (_env != null) return Task.FromResult(_env);
            if (_envTask != null) return _envTask;
            _envTask = CreateEnvironmentAsync();
            return _envTask;
        }

        private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
        {
            try
            {
                string folder = UserDataFolder;
                Directory.CreateDirectory(folder);                   // R1 - guarantee writable target
                var env = await CoreWebView2Environment.CreateAsync(null, folder, null);
                _env = env; // back on the same STA thread (dispatcher SynchronizationContext)
                DiagnosticsLog.Info("WebHost: environment created", $"runtime {env.BrowserVersionString}, folder {folder}");
                return env;
            }
            catch
            {
                // Don't cache a faulted task - a transient failure (e.g. runtime not yet
                // installed) must be retryable on the next EnvironmentAsync() call.
                _envTask = null;
                throw;
            }
        }

        /// <summary>
        /// Creates a themed, initialized <see cref="WebView2"/> control ready to navigate:
        /// sets <see cref="WebView2.DefaultBackgroundColor"/> (R8), awaits
        /// <see cref="WebView2.EnsureCoreWebView2Async"/> against the shared environment (R3),
        /// then applies production settings (R7). Must be called on the owning window's STA
        /// thread (the control is a WPF element). Throws on init failure - the caller logs and
        /// surfaces it. Diagnostic events (init/nav/process-failed) are wired by the caller via
        /// <see cref="WebBridge"/> and the tool's own handlers (R6).
        /// </summary>
        public static async Task<WebView2> CreateViewAsync()
        {
            var env  = await EnvironmentAsync();
            var view = new WebView2();
            ApplyThemeBackground(view);                              // R8 - before init
            await view.EnsureCoreWebView2Async(env);                // R3
            ConfigureProductionSettings(view.CoreWebView2);         // R7
            return view;
        }

        /// <summary>Sets the control's pre-navigation background to the active theme so a
        /// not-yet-loaded control never flashes white (rule R8).</summary>
        public static void ApplyThemeBackground(WebView2 view)
        {
            var c = AppSettings.Instance.ActiveTheme.Bg.Color;
            view.DefaultBackgroundColor = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }

        /// <summary>
        /// Applies the production hosting settings (rule R7): the page must feel like a native
        /// window, not a browser. Dev tools stay available only in DEBUG builds. Never throws -
        /// a settings-write failure is logged and the control stays usable.
        /// </summary>
        public static void ConfigureProductionSettings(CoreWebView2 core)
        {
            try
            {
                var s = core.Settings;
                s.AreDefaultContextMenusEnabled   = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
                s.IsZoomControlEnabled            = false;
                s.IsStatusBarEnabled              = false;
                s.IsSwipeNavigationEnabled        = false;
#if DEBUG
                s.AreDevToolsEnabled = true;
#else
                s.AreDevToolsEnabled = false;
#endif
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("WebHost: configure settings", ex);
            }
        }
    }
}
