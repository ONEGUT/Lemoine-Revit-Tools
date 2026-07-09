using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LemoineTools.Framework.Web
{
    // ─────────────────────────────────────────────────────────────────────────
    // WebView2 pilot copy of ToolsOverviewWindow — same data (ToolsOverviewCatalog),
    // same visual language, rendered as HTML/CSS/JS instead of WPF. See
    // plan-design-twin-parity-and-webview2-overview.md Part B: this exists to be
    // opened side-by-side with the real ToolsOverviewWindow so the user can compare
    // startup time, theme switching, text rendering, and memory footprint before
    // deciding whether to migrate the UI layer to WebView2.
    //
    // Deliberately minimal — no StepFlowWindow chrome, no design-twin parity tagging.
    // Kept off the production ribbon (Developer panel only).
    //
    // Opened on Revit's main STA thread exactly like ToolsOverviewWindow (no new
    // threading model). Not owned to Revit's HWND, matching every other Lemoine
    // window (see CLAUDE.md "Run Lifecycle — Window Ownership").
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class ToolsOverviewWebWindow : Window
    {
        private const string HostName = "lemoine.overview";

        private WebView2? _webView;
        private string _activeCategoryId = "setup";
        private bool _coreReady;

        public ToolsOverviewWebWindow()
        {
            Title = "Lemoine Tools — Overview (Web)";
            Width = 1080; Height = 680;
            MinWidth = 860; MinHeight = 540;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            Background = AppSettings.Instance.ActiveTheme.PageBg;

            _webView = new WebView2 { DefaultBackgroundColor = ToDrawingColor(AppSettings.Instance.ActiveTheme.Bg) };
            Content = _webView;

            Loaded += OnLoaded;
            Closed += OnClosed;

            // Named handlers, detached on Closed — leaked subscriptions to a global
            // event crash/hang Revit once this window's dispatcher has shut down
            // (see CLAUDE.md "Why leaked global-event subscriptions crash Revit").
            AppSettings.Instance.ThemeChanged += OnThemeChanged;
            AppSettings.Instance.UiSizeChanged += OnUiSizeChanged;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await InitializeWebViewAsync();
            }
            catch (Exception ex)
            {
                // A missing/broken WebView2 Evergreen Runtime (or any other init
                // failure) must never crash Revit or leave a silent blank window —
                // report it in both places, same discipline as every other tool.
                DiagnosticsLog.Error("ToolsOverviewWebWindow: WebView2 initialization failed", ex);
                ShowRuntimeUnavailable(ex);
            }
        }

        private async System.Threading.Tasks.Task InitializeWebViewAsync()
        {
            if (_webView == null) return;
            await _webView.EnsureCoreWebView2Async();
            if (_webView.CoreWebView2 == null)
                throw new InvalidOperationException("CoreWebView2 did not initialize.");

            string? asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string webDir = Path.Combine(asmDir ?? "", "Source", "Resources", "Web", "overview");
            if (!Directory.Exists(webDir))
                throw new DirectoryNotFoundException("WebView2 pilot assets not found: " + webDir);

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                HostName, webDir, CoreWebView2HostResourceAccessKind.DenyCors);
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _coreReady = true;

            _webView.CoreWebView2.Navigate($"https://{HostName}/index.html");
        }

        private void ShowRuntimeUnavailable(Exception ex)
        {
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "WebView2 runtime is not available on this machine, so the web pilot "
                       + "of Tools Overview can't render.\n\n" + ex.Message,
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
            };
        }

        // ── Host <-> page messaging ─────────────────────────────────────────────
        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // Fires on the WebView2 thread; marshal back before touching AppSettings/WPF.
            // This window is a plain Window, not StepFlowWindow, so it has no dispatcher-
            // level UnhandledException safety net (see CLAUDE.md "Unhandled UI Exceptions
            // Crash Revit") — an auto-firing callback like this one must guard its own body,
            // same discipline as FiltersSettingsWindow's DispatcherTimer.Tick.
            string json = e.WebMessageAsJson;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    HandleMessage(json);
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("ToolsOverviewWebWindow: WebMessageReceived", ex);
                }
            }), DispatcherPriority.Normal);
        }

        private void HandleMessage(string json)
        {
            if (!(MiniJson.Parse(json) is Dictionary<string, object?> msg))
            {
                DiagnosticsLog.Warn("ToolsOverviewWebWindow", "web message was not a JSON object: " + json);
                return;
            }
            if (!(msg.TryGetValue("type", out var typeObj) && typeObj is string type))
            {
                DiagnosticsLog.Warn("ToolsOverviewWebWindow", "web message had no string 'type': " + json);
                return;
            }

            switch (type)
            {
                case "ready":
                    PushState();
                    break;
                case "selectCategory":
                    if (msg.TryGetValue("categoryId", out var catObj) && catObj is string catId)
                    {
                        _activeCategoryId = catId;
                        PushState();
                    }
                    break;
                case "runDemo":
                    if (msg.TryGetValue("toolName", out var toolObj) && toolObj is string toolName)
                        LaunchDemo(toolName);
                    break;
                case "close":
                    Close();
                    break;
                default:
                    DiagnosticsLog.Warn("ToolsOverviewWebWindow", "unrecognized web message type: " + type);
                    break;
            }
        }

        private void PushState()
        {
            if (!_coreReady || _webView?.CoreWebView2 == null) return;
            _webView.CoreWebView2.PostWebMessageAsJson(BuildStateJson());
        }

        // ── Serialize ToolsOverviewCatalog + active theme to the page's state schema ──
        private string BuildStateJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"state\",");
            sb.Append("\"theme\":").Append(Q(ToKebab(AppSettings.Instance.ActiveTheme.Name))).Append(',');
            sb.Append("\"activeCategoryId\":").Append(Q(_activeCategoryId)).Append(',');
            sb.Append("\"windowTitle\":").Append(Q(AppStrings.T("overview.window.title"))).Append(',');
            sb.Append("\"footerHint\":").Append(Q(AppStrings.T("overview.window.footerHint"))).Append(',');
            sb.Append("\"runButtonLabel\":").Append(Q(AppStrings.T("overview.window.runButton"))).Append(',');
            sb.Append("\"categories\":[");

            bool firstCat = true;
            foreach (var cat in ToolsOverviewCatalog.Categories)
            {
                if (!firstCat) sb.Append(',');
                firstCat = false;
                sb.Append("{\"id\":").Append(Q(cat.Id)).Append(',');
                sb.Append("\"name\":").Append(Q(cat.Name)).Append(',');
                sb.Append("\"intro\":").Append(Q(cat.Intro)).Append(',');
                sb.Append("\"tools\":[");
                bool firstTool = true;
                foreach (var tool in cat.Tools)
                {
                    if (!firstTool) sb.Append(',');
                    firstTool = false;
                    bool hasDemo = ToolsOverviewDemos.For(tool.Name) != null;
                    sb.Append("{\"name\":").Append(Q(tool.Name)).Append(',');
                    sb.Append("\"blurb\":").Append(Q(tool.Blurb)).Append(',');
                    sb.Append("\"hasDemo\":").Append(hasDemo ? "true" : "false").Append('}');
                }
                sb.Append("]}");
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static string Q(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string ToKebab(string displayName) => displayName.ToLowerInvariant().Replace(" ", "-");

        private static System.Drawing.Color ToDrawingColor(SolidColorBrush brush)
        {
            var c = brush.Color;
            return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        // ── Dummy run — mirrors ToolsOverviewWindow.LaunchDemo (see there for the
        // STA-thread + message-pump rationale). Duplicated rather than shared: this
        // is a standalone pilot window, not a refactor of the production window. ──
        private void LaunchDemo(string toolName)
        {
            var spec = ToolsOverviewDemos.For(toolName);
            if (spec == null) return;

            var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try
                {
                    var win = new StepFlowWindow(new OverviewDemoTool(spec));
                    win.Closed += (s, e) => Dispatcher.CurrentDispatcher.InvokeShutdown();
                    win.Show();
                    ready.Set();
                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("ToolsOverviewWebWindow: launch demo run", ex);
                    ready.Set();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            ready.Wait();
        }

        // ── Global-event handlers ───────────────────────────────────────────────
        private void OnThemeChanged(ThemePalette t)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Background = t.PageBg;
                PushState();
            }));
        }

        private void OnUiSizeChanged(UiSize _)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(PushState));
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            AppSettings.Instance.ThemeChanged -= OnThemeChanged;
            AppSettings.Instance.UiSizeChanged -= OnUiSizeChanged;
            if (_webView?.CoreWebView2 != null)
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            _webView?.Dispose();
            _webView = null;
        }
    }
}
