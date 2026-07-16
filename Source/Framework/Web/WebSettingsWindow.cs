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
    /// HTML analogue of <see cref="GlobalSettingsWindow"/>. Hosts settings.html and drives it
    /// through the bridge: C# sends <c>init</c> (the tab + General-tab payload from
    /// <see cref="WebSettings"/>); JS sends <c>action</c> messages (setTheme / setSize /
    /// setLanguage / openLog / switchTab / drag / minimize / close). Theme and size changes
    /// re-send the payload so the active highlight updates, and also propagate live to every open
    /// web window via the shared ThemeChanged / UiSizeChanged events.
    /// Only the General tab is web-backed today; other tabs render a placeholder.
    /// </summary>
    public class WebSettingsWindow : Window
    {
        private WebView2?  _view;
        private WebBridge? _bridge;
        private TextBlock? _loading;
        private string     _activeTab = "general";

        public WebSettingsWindow()
        {
            Title     = AppStrings.T("globalSettings.window.title");
            Width     = 760;
            Height    = 640;
            MinWidth  = 560;
            MinHeight = 480;
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
                    else DiagnosticsLog.Warn("WebSettingsWindow: navigation", $"WebErrorStatus={ev.WebErrorStatus}");
                };
                _view.CoreWebView2.ProcessFailed += (s, ev) =>
                    DiagnosticsLog.Warn("WebSettingsWindow: process failed", ev.ProcessFailedKind.ToString());

                _bridge = new WebBridge(_view.CoreWebView2);
                _bridge.On("action", OnActionMessage);

                _view.CoreWebView2.Navigate(WebAssets.PageUrl("settings.html"));
                SendInit();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("WebSettingsWindow: init", ex);
            }
        }

        private void SendInit() =>
            _bridge?.Send("init", WebSettings.BuildPayload(_activeTab));

        // WebBridge already unwraps the message envelope and invokes handlers with the payload
        // dict directly (see WebStepFlowWindow.OnActionMessage) — there is no nested "payload"
        // key here, so read the action/args straight off p.
        private void OnActionMessage(IReadOnlyDictionary<string, object?> p)
        {
            string action = Str(p, "action");
            switch (action)
            {
                case "switchTab":  _activeTab = Str(p, "tabId"); SendInit(); break;
                case "setTheme":   WebSettings.ApplyTheme(Str(p, "name"));      SendInit(); break;
                case "setSize":    WebSettings.ApplySize(Str(p, "id"));         SendInit(); break;
                case "setLanguage":
                    // Language reload rebuilds AppStrings; re-send so labels + active mark update.
                    WebSettings.ApplyLanguage(Str(p, "culture"));
                    SendInit();
                    break;
                case "setField":
                    // A spec-tab field changed. The control keeps its own displayed value, so we
                    // apply + save without re-sending init (that would steal focus mid-edit).
                    WebSettings.SetField(_activeTab, Str(p, "fieldId"),
                        p.TryGetValue("value", out var fieldVal) ? fieldVal : null);
                    break;
                case "openLog":    WebSettings.OpenLog(); break;
                case "drag":       BeginNativeDrag(); break;
                case "minimize":   WindowState = WindowState.Minimized; break;
                case "close":      Close(); break;
            }
        }

        // ── Theme / size live apply ───────────────────────────────────────────
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
            DiagnosticsLog.Error("WebSettingsWindow: dispatcher unhandled", e.Exception);
            e.Handled = true;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            AppSettings.Instance.ThemeChanged  -= OnThemeChanged;
            AppSettings.Instance.UiSizeChanged -= OnUiSizeChanged;
            Dispatcher.UnhandledException      -= OnUnhandled;
            try { _view?.Dispose(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebSettingsWindow: dispose view", ex); }
            _view = null;
            _bridge = null;
        }

        private void SafeUi(Action action)
        {
            try
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.BeginInvoke(action);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebSettingsWindow: SafeUi", ex); }
        }

        private static string Str(IReadOnlyDictionary<string, object?> d, string key) =>
            d.TryGetValue(key, out var v) ? v as string ?? "" : "";

        // ── Native window drag from the HTML title bar (mirrors WebStepFlowWindow) ──
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
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebSettingsWindow: begin drag", ex); }
        }
    }
}
