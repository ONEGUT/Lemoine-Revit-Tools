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
        private readonly WebNaming _naming;

        public WebSettingsWindow(Naming.ParameterCatalogSnapshot? namingSnapshot = null)
        {
            _naming = new WebNaming(namingSnapshot);
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
            Activated                          += OnActivated;   // route keyboard focus into the WebView2
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
                        FocusWebView();  // ensure HTML inputs are typeable once content is live
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

        private void SendInit()
        {
            var payload = WebSettings.BuildPayload(_activeTab);
            if (_activeTab == "naming") payload["naming"] = _naming.BuildPayload();
            _bridge?.Send("init", payload);
        }

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
                case "namingSave":  OnNamingSave(p); break;
                case "namingDelete": _naming.Delete(Str(p, "key")); SendInit(); break;
                case "openLog":    WebSettings.OpenLog(); break;
                case "drag":       BeginNativeDrag(); break;
                case "minimize":   WindowState = WindowState.Minimized; break;
                case "close":      Close(); break;
            }
        }

        // Build the DTO from the page's editor form, validate + persist. On success re-send init
        // (the token list refreshes and the page reloads the editor with the saved token); on a
        // validation error, send it back for inline display without losing the in-progress form.
        private void OnNamingSave(IReadOnlyDictionary<string, object?> p)
        {
            string? originalKey = string.IsNullOrEmpty(Str(p, "originalKey")) ? null : Str(p, "originalKey");
            var dto = new Naming.UserTokenDto
            {
                Key           = Str(p, "key"),
                Label         = Str(p, "label"),
                Subject       = Str(p, "subject"),
                Entity        = Str(p, "entity"),
                ParameterName = Str(p, "paramName"),
                ParameterGuid = Str(p, "paramGuid"),
                FallbackText  = Str(p, "fallback"),
                Description   = Str(p, "description"),
            };
            string? error = _naming.Save(dto, originalKey);
            if (error != null)
                _bridge?.Send("namingError", new Dictionary<string, object?> { ["message"] = error });
            else
            {
                // Re-send with a hint so the page reopens the editor on the just-saved token.
                var payload = WebSettings.BuildPayload(_activeTab);
                payload["naming"] = _naming.BuildPayload();
                payload["namingSelectKey"] = dto.Key;
                _bridge?.Send("init", payload);
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
            Activated                          -= OnActivated;
            try { _view?.Dispose(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebSettingsWindow: dispose view", ex); }
            _view = null;
            _bridge = null;
        }

        // Borderless (WindowStyle=None) windows on a background STA thread don't reliably route
        // keyboard focus into the hosted WebView2, so HTML inputs render but can't be typed into.
        // Focusing the control (WPF) moves focus into the CoreWebView2, restoring text entry.
        private void OnActivated(object? sender, EventArgs e) => FocusWebView();
        private void FocusWebView()
        {
            try { _view?.Focus(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebSettingsWindow: focus view", ex); }
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
