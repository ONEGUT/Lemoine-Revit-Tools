using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// Shared base for bespoke (non-step-flow) web windows — Link Audit, Tools Overview, Clash
    /// Definitions, etc. Encapsulates the exact WebView2 hosting lifecycle proven in
    /// <see cref="WebSettingsWindow"/> (per-thread env, EnsureCoreWebView2Async before any use,
    /// production settings, virtual-host mapping, live theme/size CSS-variable push, the borderless
    /// drag/minimize/maximize/close chrome, and the Dispatcher.UnhandledException net). A subclass
    /// supplies its page file name and initial payload, and may handle page-specific actions.
    /// Runs on the shared <see cref="WebUiThread"/>, same as the step-flow web windows.
    /// </summary>
    public abstract class WebWindowBase : Window
    {
        private WebView2?  _view;
        private WebBridge? _bridge;
        private TextBlock? _loading;

        /// <summary>The page under Source/Web/ to navigate to, e.g. "linkaudit.html".</summary>
        protected abstract string PageFileName { get; }

        /// <summary>The <c>init</c> payload the page renders from. Called on first load and whenever
        /// <see cref="SendInit"/> is invoked.</summary>
        protected abstract Dictionary<string, object?> BuildInitPayload();

        /// <summary>Handle a page action beyond the built-in drag/minimize/maximize/close.
        /// Return true if handled.</summary>
        protected virtual bool HandleAction(string action, IReadOnlyDictionary<string, object?> payload) => false;

        protected WebBridge? Bridge => _bridge;

        protected WebWindowBase(string title, double width, double height, double minWidth = 420, double minHeight = 360)
        {
            Title     = title;
            Width     = width;
            Height    = height;
            MinWidth  = minWidth;
            MinHeight = minHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            WindowStyle        = WindowStyle.None;
            ResizeMode         = ResizeMode.CanResizeWithGrip;
            AllowsTransparency = false;
            ShowInTaskbar      = true;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight         = 0,
                ResizeBorderThickness = new Thickness(8),
                GlassFrameThickness   = new Thickness(0),
                UseAeroCaptionButtons = false,
            });

            AppSettings.Instance.ApplyTo(Resources);
            Background = AppSettings.Instance.ActiveTheme.PageBg;

            AppSettings.Instance.ThemeChanged  += OnThemeChanged;
            AppSettings.Instance.UiSizeChanged += OnUiSizeChanged;
            Dispatcher.UnhandledException      += OnUnhandled;
            Closed                             += OnClosed;

            var host = new Grid();
            _view = new WebView2();
            WebHost.ApplyThemeBackground(_view);
            host.Children.Add(_view);

            _loading = new TextBlock
            {
                Text                = "Loading...",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            _loading.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _loading.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _loading.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            host.Children.Add(_loading);

            Content = host;
            Loaded += OnLoaded;
        }

        private bool _initStarted;
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_initStarted) return;
            _initStarted = true;
            _ = InitAsync();
        }

        private async Task InitAsync()
        {
            try
            {
                var env = await WebHost.EnvironmentAsync();
                await _view!.EnsureCoreWebView2Async(env);
                WebHost.ConfigureProductionSettings(_view.CoreWebView2);
                WebAssets.MapVirtualHost(_view.CoreWebView2);
                _view.NavigationCompleted += (s, ev) =>
                {
                    if (ev.IsSuccess)
                    {
                        if (_loading != null) _loading.Visibility = Visibility.Collapsed;
                        WebAssets.ApplyVariablesLive(_view);
                    }
                    else DiagnosticsLog.Warn($"{GetType().Name}: navigation", $"WebErrorStatus={ev.WebErrorStatus}");
                };
                _view.CoreWebView2.ProcessFailed += (s, ev) =>
                    DiagnosticsLog.Warn($"{GetType().Name}: process failed", ev.ProcessFailedKind.ToString());

                _bridge = new WebBridge(_view.CoreWebView2);
                _bridge.On("action", OnActionMessage);

                _view.CoreWebView2.Navigate(WebAssets.PageUrl(PageFileName));
                SendInit(); // queued by WebBridge until the page's `ready` arrives (R18)
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"{GetType().Name}: init", ex);
            }
        }

        /// <summary>Re-send the init payload (e.g. after a mutation refreshes the read model).</summary>
        protected void SendInit() => _bridge?.Send("init", BuildInitPayload());

        private void OnActionMessage(IReadOnlyDictionary<string, object?> p)
        {
            string action = Str(p, "action");
            switch (action)
            {
                case "drag":     BeginNativeDrag(); return;
                case "minimize": WindowState = WindowState.Minimized; return;
                case "maximize": WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; return;
                case "close":    Close(); return;
            }
            try
            {
                if (!HandleAction(action, p))
                    DiagnosticsLog.Warn($"{GetType().Name}: unhandled action", action);
            }
            catch (Exception ex) { DiagnosticsLog.Error($"{GetType().Name}: action '{action}'", ex); }
        }

        // ── Theme / size live push ───────────────────────────────────────────
        private void OnThemeChanged(ThemePalette t) => SafeUi(() =>
        {
            AppSettings.Instance.ApplyTo(Resources);
            Background = t.PageBg;
            if (_view != null) WebAssets.ApplyVariablesLive(_view);
        });

        private void OnUiSizeChanged(UiSize _) => SafeUi(() =>
        {
            AppSettings.Instance.ApplyScaleTo(Resources);
            if (_view != null) WebAssets.ApplyVariablesLive(_view);
        });

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            DiagnosticsLog.Error($"{GetType().Name}: dispatcher unhandled", e.Exception);
            e.Handled = true;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            AppSettings.Instance.ThemeChanged  -= OnThemeChanged;
            AppSettings.Instance.UiSizeChanged -= OnUiSizeChanged;
            Dispatcher.UnhandledException      -= OnUnhandled;
            OnWindowClosed();
            try { _view?.Dispose(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed($"{GetType().Name}: dispose view", ex); }
            _view = null;
            _bridge = null;
        }

        /// <summary>Subclass cleanup hook (null callbacks, etc.). Runs before the view is disposed.</summary>
        protected virtual void OnWindowClosed() { }

        private void SafeUi(Action action)
        {
            try
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.BeginInvoke(action);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed($"{GetType().Name}: SafeUi", ex); }
        }

        /// <summary>Marshal an action onto this window's dispatcher (STA), guarded against a
        /// shut-down dispatcher — for callbacks arriving from Revit's main thread (e.g. an
        /// ExternalEvent pick result). Never throws into the caller thread.</summary>
        protected void RunOnUiThread(Action action) => SafeUi(action);

        protected static string Str(IReadOnlyDictionary<string, object?> d, string key) =>
            d.TryGetValue(key, out var v) ? v as string ?? "" : "";

        // ── Native window drag from the HTML title bar (mirrors WebSettingsWindow) ──
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION        = 0x0002;
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private void BeginNativeDrag()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                ReleaseCapture();
                SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed($"{GetType().Name}: begin drag", ex); }
        }
    }
}
