using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

using WpfBrushes = System.Windows.Media.Brushes;

namespace LemoineTools.Tools.Debuggers
{
    // =========================================================================
    // WebView2 Test — debug harness (see CLAUDE.md "Crashes & Large Ambiguous
    // Issues" and plan-webview2-ui-migration.md).
    //
    // Now also the proving ground for the Phase 0 shared host layer: environment
    // creation goes through WebHost (rule R36 — never a bespoke CreateAsync),
    // pages are served loose from the deploy Web\ folder over the virtual host
    // (WebAssets), and the JS<->C# messaging runs through WebBridge. The manual,
    // per-phase buttons remain so a failing phase still names itself.
    //
    // Every suspect is lazily constructed behind a button: opening the window or
    // navigating steps must never touch WebView2. All strings are hardcoded
    // deliberately — developer-only tooling, exempt from AppStrings.
    // =========================================================================
    public class WebView2TestTool : IStepFlowTool, IToolCleanup
    {
        public string Title    => "WebView2 Test";
        public string RunLabel => "Write summary";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("preflight", "Preflight checks"),
            new StepDefinition("init",      "Environment & control init"),
            new StepDefinition("inputs",    "Common inputs over the bridge"),
        };

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public bool IsValid(string stepId) => true; // all probes optional

        // ── Probe state (drives collapsed summaries + the Run summary) ────────
        private bool? _runtimeOk;
        private bool? _loaderOk;
        private bool? _envOk;
        private bool? _initOk;
        private bool? _navOk;
        private int   _bridgeMessages;

        // ── WebView2 objects (all lazily created behind buttons) ─────────────
        private CoreWebView2Environment? _env;
        private WebView2?  _initView;
        private WebView2?  _inputsView;
        private WebBridge? _initBridge;
        private WebBridge? _inputsBridge;
        private bool _initEventsWired;

        // ── Step content (built on the window's STA thread in GetStepContent) ──
        private ProbeLog _preflightLog = null!;
        private ProbeLog _initLog      = null!;
        private ProbeLog _inputsLog    = null!;
        private Border? _initHost;
        private Border? _inputsHost;

        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "preflight": return BuildPreflightStep();
                case "init":      return BuildInitStep();
                case "inputs":    return BuildInputsStep();
                default:          return null;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "preflight": return $"runtime: {Word(_runtimeOk)} · loader: {Word(_loaderOk)}";
                case "init":      return $"env: {Word(_envOk)} · init: {Word(_initOk)} · nav: {Word(_navOk)}";
                case "inputs":    return $"{_bridgeMessages} bridge message(s) received";
                default:          return "";
            }
        }

        private static string Word(bool? state) => state == true ? "ok" : state == false ? "FAILED" : "not run";

        private static string FileVer(string path)
        {
            try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion ?? "no version"; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebView2Test: read file version", ex); return "version unreadable"; }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Step 1 — Preflight (no WebView2 objects are created here)
        // ═════════════════════════════════════════════════════════════════════
        private FrameworkElement BuildPreflightStep()
        {
            _preflightLog = new ProbeLog(150);
            var panel = new StackPanel();
            panel.Children.Add(Desc(
                "Each probe runs only when its button is pressed — nothing runs on open. " +
                "Results go to this log and to diagnostics.log. If a button press hard-crashes " +
                "Revit with no log entry, that button IS the finding."));

            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };

            var btnRuntime = MakeBtn("Check WebView2 runtime");
            btnRuntime.Click += (s, e) => ProbeRuntime();
            row.Children.Add(btnRuntime);

            var btnLoader = MakeBtn("Check WebView2Loader.dll");
            btnLoader.Click += (s, e) => ProbeLoaderFile();
            row.Children.Add(btnLoader);

            var btnAsm = MakeBtn("Dump loaded WebView2 assemblies");
            btnAsm.Click += (s, e) => ProbeLoadedAssemblies();
            row.Children.Add(btnAsm);

            panel.Children.Add(row);
            panel.Children.Add(_preflightLog.Root);
            return panel;
        }

        private void ProbeRuntime()
        {
            try
            {
                string? v = CoreWebView2Environment.GetAvailableBrowserVersionString();
                if (string.IsNullOrEmpty(v))
                {
                    _runtimeOk = false;
                    _preflightLog.Fail("No WebView2 Evergreen runtime found on this machine.");
                    DiagnosticsLog.Warn("WebView2Test: runtime probe", "GetAvailableBrowserVersionString returned empty");
                }
                else
                {
                    _runtimeOk = true;
                    _preflightLog.Pass($"Evergreen runtime found: {v}");
                }
            }
            catch (Exception ex)
            {
                _runtimeOk = false;
                _preflightLog.Fail($"{ex.GetType().Name}: {ex.Message}");
                if (ex is DllNotFoundException)
                    _preflightLog.Dim("DllNotFoundException here means WebView2Loader.dll is missing — see the loader probe.");
                DiagnosticsLog.Error("WebView2Test: runtime probe", ex);
            }
            Fire();
        }

        private void ProbeLoaderFile()
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(WebView2TestTool).Assembly.Location) ?? "";
                _preflightLog.Info($"Plugin folder: {dir}");

                var top = Directory.GetFiles(dir, "*WebView2*.dll", SearchOption.TopDirectoryOnly);
                foreach (var f in top)
                    _preflightLog.Info($"  found: {Path.GetFileName(f)}  ({FileVer(f)})");

                string nested = Path.Combine(dir, "runtimes", "win-x64", "native", "WebView2Loader.dll");
                bool topLoader    = top.Any(f => Path.GetFileName(f).Equals("WebView2Loader.dll", StringComparison.OrdinalIgnoreCase));
                bool nestedLoader = File.Exists(nested);

                if (topLoader)
                {
                    _loaderOk = true;
                    _preflightLog.Pass("WebView2Loader.dll is beside the plugin DLL.");
                }
                else if (nestedLoader)
                {
                    _loaderOk = false;
                    _preflightLog.Fail("WebView2Loader.dll only exists under runtimes\\win-x64\\native\\ — " +
                                       "the P/Invoke probe won't find it there under Revit. Copy it beside LemoineTools.dll.");
                    DiagnosticsLog.Warn("WebView2Test: loader probe", "loader only in runtimes subfolder");
                }
                else
                {
                    _loaderOk = false;
                    _preflightLog.Fail("WebView2Loader.dll NOT found — init would fail silently. " +
                                       "Check the CopyWebView2Loader build step ran.");
                    DiagnosticsLog.Warn("WebView2Test: loader probe", "WebView2Loader.dll not deployed");
                }

                // Also confirm the Phase 0 web assets deployed (served over the virtual host).
                string webRoot = WebAssets.WebRoot;
                string smoke   = Path.Combine(webRoot, "debug", "smoke.html");
                string bridge  = Path.Combine(webRoot, "lemoine-bridge.js");
                if (File.Exists(smoke) && File.Exists(bridge))
                    _preflightLog.Pass($"Web assets deployed at {webRoot}.");
                else
                {
                    _preflightLog.Fail($"Web assets missing under {webRoot} — check the Source\\Web copy step.");
                    DiagnosticsLog.Warn("WebView2Test: web assets probe", $"missing under {webRoot}");
                }
            }
            catch (Exception ex)
            {
                _loaderOk = false;
                _preflightLog.Fail($"{ex.GetType().Name}: {ex.Message}");
                DiagnosticsLog.Error("WebView2Test: loader probe", ex);
            }
            Fire();
        }

        private void ProbeLoadedAssemblies()
        {
            try
            {
                var loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => (a.GetName().Name ?? "").StartsWith("Microsoft.Web.WebView2", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (loaded.Count == 0)
                    _preflightLog.Info("No WebView2 assemblies loaded in the AppDomain yet.");
                foreach (var a in loaded)
                {
                    string loc;
                    try { loc = a.IsDynamic ? "(dynamic)" : a.Location; }
                    catch (Exception locEx) { loc = "(no location: " + locEx.Message + ")"; DiagnosticsLog.Swallowed("WebView2Test: assembly location", locEx); }
                    _preflightLog.Info($"  {a.GetName().Name} {a.GetName().Version}  @  {loc}");
                }

                var core = typeof(CoreWebView2Environment).Assembly;
                _preflightLog.Info($"Our CoreWebView2Environment binds to: {core.GetName().Version}  @  {core.Location}");
                _preflightLog.Pass("Assembly dump complete — compare the path above against the plugin folder.");
            }
            catch (Exception ex)
            {
                _preflightLog.Fail($"{ex.GetType().Name}: {ex.Message}");
                DiagnosticsLog.Error("WebView2Test: assembly probe", ex);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Step 2 — Environment & control init (one phase per button)
        // ═════════════════════════════════════════════════════════════════════
        private FrameworkElement BuildInitStep()
        {
            _initLog = new ProbeLog(120);
            var panel = new StackPanel();
            panel.Children.Add(Desc(
                "The three init phases are separate buttons so the failing phase names itself. " +
                "Run them in order. Environment creation goes through the shared WebHost (explicit " +
                "user-data folder in %LocalAppData%\\LemoineTools\\WebView2), and pages are served " +
                "from the deploy Web\\ folder over the lemoine.app virtual host."));

            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };

            var btnEnv = MakeBtn("1 · Create environment (WebHost)");
            btnEnv.Click += async (s, e) => await CreateEnvironmentAsync(_initLog);
            row.Children.Add(btnEnv);

            var btnInit = MakeBtn("2 · Init WebView2 control");
            btnInit.Click += async (s, e) => await InitControlAsync();
            row.Children.Add(btnInit);

            var btnSmoke = MakeBtn("3 · Load smoke page");
            btnSmoke.Click += (s, e) => LoadInitPage("debug/smoke.html");
            row.Children.Add(btnSmoke);

            panel.Children.Add(row);

            _initHost = MakeHost(240, "WebView2 control not created yet — run the numbered buttons in order.");
            panel.Children.Add(_initHost);
            panel.Children.Add(_initLog.Root);
            return panel;
        }

        private async Task<bool> CreateEnvironmentAsync(ProbeLog log)
        {
            if (_env != null) { log.Info("Environment already created — reusing the shared WebHost environment."); return true; }
            try
            {
                log.Info($"User data folder: {WebHost.UserDataFolder}");
                _env = await WebHost.EnvironmentAsync();
                _envOk = true;
                log.Pass($"Environment ready (runtime {_env.BrowserVersionString}).");
                Fire();
                return true;
            }
            catch (Exception ex)
            {
                _envOk = false;
                log.Fail($"WebHost.EnvironmentAsync FAILED — {ex.GetType().Name}: {ex.Message}");
                DiagnosticsLog.Error("WebView2Test: create environment", ex);
                Fire();
                return false;
            }
        }

        private async Task InitControlAsync()
        {
            if (_env == null) { _initLog.Dim("Run '1 · Create environment' first."); return; }
            try
            {
                if (_initView == null)
                {
                    _initView = MakeWebView();
                    if (_initHost != null) _initHost.Child = _initView;
                    _initLog.Info("WebView2 WPF control created and added to the host area.");
                }
                if (_initView.CoreWebView2 != null) { _initLog.Info("Control already initialized."); return; }

                if (!_initEventsWired)
                {
                    _initEventsWired = true;
                    _initView.CoreWebView2InitializationCompleted += (s, e) =>
                    {
                        if (e.IsSuccess) _initLog.Pass("CoreWebView2InitializationCompleted: success.");
                        else
                        {
                            _initLog.Fail($"CoreWebView2InitializationCompleted: FAILED — {e.InitializationException?.Message}");
                            if (e.InitializationException != null)
                                DiagnosticsLog.Error("WebView2Test: init completed event", e.InitializationException);
                        }
                    };
                    _initView.NavigationCompleted += (s, e) =>
                    {
                        _navOk = e.IsSuccess;
                        if (e.IsSuccess)
                        {
                            WebAssets.ApplyVariablesLive(_initView!); // push live theme vars (R12)
                            _initLog.Pass("NavigationCompleted: success — the page should be visible above.");
                        }
                        else
                        {
                            _initLog.Fail($"NavigationCompleted: FAILED — WebErrorStatus = {e.WebErrorStatus}");
                            DiagnosticsLog.Warn("WebView2Test: navigation", $"WebErrorStatus={e.WebErrorStatus}");
                        }
                        Fire();
                    };
                }

                await _initView.EnsureCoreWebView2Async(_env); // R3 — nothing below runs before this

                WebHost.ConfigureProductionSettings(_initView.CoreWebView2); // R7
                WebAssets.MapVirtualHost(_initView.CoreWebView2);            // R16
                _initView.CoreWebView2.ProcessFailed += (s, e) =>
                {
                    _initLog.Fail($"ProcessFailed: {e.ProcessFailedKind} — the browser process died.");
                    DiagnosticsLog.Warn("WebView2Test: process failed", e.ProcessFailedKind.ToString());
                };
                _initBridge = new WebBridge(_initView.CoreWebView2);
                _initBridge.MessageReceived += (type, payload) => LogBridge(_initLog, type, payload, countIt: false);

                _initOk = true;
                _initLog.Pass("EnsureCoreWebView2Async completed — CoreWebView2 ready, bridge attached.");
            }
            catch (Exception ex)
            {
                _initOk = false;
                _initLog.Fail($"EnsureCoreWebView2Async FAILED — {ex.GetType().Name}: {ex.Message}");
                DiagnosticsLog.Error("WebView2Test: init control", ex);
            }
            Fire();
        }

        private void LoadInitPage(string relativePath)
        {
            if (_initView?.CoreWebView2 == null)
            {
                _initLog.Dim("Run '2 · Init WebView2 control' first — navigating before init is failure mode #3.");
                return;
            }
            try
            {
                _initView.CoreWebView2.Navigate(WebAssets.PageUrl(relativePath));
                _initLog.Info($"Navigate({WebAssets.PageUrl(relativePath)}) issued — waiting for NavigationCompleted…");
            }
            catch (Exception ex)
            {
                _navOk = false;
                _initLog.Fail($"Navigate FAILED — {ex.GetType().Name}: {ex.Message}");
                DiagnosticsLog.Error("WebView2Test: navigate init page", ex);
                Fire();
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Step 3 — Common inputs over the bridge (own control, shared env)
        // ═════════════════════════════════════════════════════════════════════
        private FrameworkElement BuildInputsStep()
        {
            _inputsLog = new ProbeLog(110);
            var panel = new StackPanel();
            panel.Children.Add(Desc(
                "The loose HTML recreations of InlineStepper and MultiSelectTabs, served from the " +
                "deploy Web\\ folder and themed live from the active theme. Interacting with them " +
                "posts messages over the WebBridge into this log. These buttons run any missing " +
                "init phase themselves."));

            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };

            var btnStepper = MakeBtn("Load stepper page");
            btnStepper.Click += async (s, e) => await LoadInputsPageAsync("debug/stepper.html", "stepper");
            row.Children.Add(btnStepper);

            var btnTabs = MakeBtn("Load multi-select tabs page");
            btnTabs.Click += async (s, e) => await LoadInputsPageAsync("debug/tabs.html", "multi-select tabs");
            row.Children.Add(btnTabs);

            panel.Children.Add(row);

            _inputsHost = MakeHost(300, "No page loaded yet — press one of the buttons above.");
            panel.Children.Add(_inputsHost);
            panel.Children.Add(_inputsLog.Root);
            return panel;
        }

        private async Task LoadInputsPageAsync(string relativePath, string pageName)
        {
            try
            {
                if (!await CreateEnvironmentAsync(_inputsLog)) return;

                if (_inputsView == null)
                {
                    _inputsView = MakeWebView();
                    if (_inputsHost != null) _inputsHost.Child = _inputsView;
                    _inputsView.NavigationCompleted += (s, e) =>
                    {
                        if (e.IsSuccess) WebAssets.ApplyVariablesLive(_inputsView!);
                        else DiagnosticsLog.Warn("WebView2Test: inputs navigation", $"WebErrorStatus={e.WebErrorStatus}");
                    };
                    _inputsLog.Info("Second WebView2 control created (shares the environment).");
                }
                if (_inputsView.CoreWebView2 == null)
                {
                    await _inputsView.EnsureCoreWebView2Async(_env);
                    WebHost.ConfigureProductionSettings(_inputsView.CoreWebView2);
                    WebAssets.MapVirtualHost(_inputsView.CoreWebView2);
                    _inputsView.CoreWebView2.ProcessFailed += (s, e) =>
                    {
                        _inputsLog.Fail($"ProcessFailed: {e.ProcessFailedKind}");
                        DiagnosticsLog.Warn("WebView2Test: inputs process failed", e.ProcessFailedKind.ToString());
                    };
                    _inputsBridge = new WebBridge(_inputsView.CoreWebView2);
                    _inputsBridge.MessageReceived += (type, payload) => LogBridge(_inputsLog, type, payload, countIt: true);
                    _inputsLog.Pass("Inputs control initialized, bridge attached.");
                }
                _inputsView.CoreWebView2.Navigate(WebAssets.PageUrl(relativePath));
                _inputsLog.Info($"Loaded the {pageName} page — interact with it and watch for bridge messages.");
            }
            catch (Exception ex)
            {
                _inputsLog.Fail($"Loading {pageName} page FAILED — {ex.GetType().Name}: {ex.Message}");
                DiagnosticsLog.Error($"WebView2Test: load {pageName} page", ex);
            }
        }

        // Renders one inbound bridge message. 'ready' is bridge bookkeeping (not counted);
        // 'error' is a page-side script fault (rule R35) shown red.
        private void LogBridge(ProbeLog log, string type, IReadOnlyDictionary<string, object?> payload, bool countIt)
        {
            if (type == "ready") { log.Dim("bridge: page ready"); return; }

            string text = payload.TryGetValue("text", out var t) && t != null
                ? t.ToString() ?? ""
                : string.Join(", ", payload.Select(kv => $"{kv.Key}={kv.Value}"));

            if (type == "error")
            {
                log.Fail($"page error: {text}");
                return;
            }

            if (countIt) { _bridgeMessages++; Fire(); }
            log.Bridge($"JS -> C# [{type}] {text}");
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Run — write the probe summary to the Output log (no Revit access)
        // ═════════════════════════════════════════════════════════════════════
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            int pass = 0, fail = 0;
            void Report(string name, bool? state)
            {
                if      (state == true)  { pass++; pushLog($"{name} — OK", "pass"); }
                else if (state == false) { fail++; pushLog($"{name} — FAILED (see the step's log and diagnostics.log)", "fail"); }
                else                     pushLog($"{name} — not run", "info");
            }

            pushLog("WebView2 harness summary:", "info");
            Report("Runtime probe        ", _runtimeOk);
            Report("Loader file probe    ", _loaderOk);
            Report("Environment creation ", _envOk);
            Report("Control init         ", _initOk);
            Report("Navigation           ", _navOk);
            pushLog($"Bridge messages received from JS: {_bridgeMessages}",
                    _bridgeMessages > 0 ? "pass" : "info");
            if (_bridgeMessages > 0) pass++;
            pushLog(@"Full details: %AppData%\LemoineTools\diagnostics.log", "info");

            onProgress(100, pass, fail, 0);
            onComplete(pass, fail, 0);
        }

        // ── IToolCleanup — release the browser processes with the window ──────
        public void OnWindowClosed()
        {
            try { _initView?.Dispose(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebView2Test: dispose init control", ex); }
            try { _inputsView?.Dispose(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebView2Test: dispose inputs control", ex); }
            _initView    = null;
            _inputsView  = null;
            _initBridge  = null;
            _inputsBridge = null;
            _env         = null; // the shared WebHost environment lives on (process lifetime, R5)
        }

        // ═════════════════════════════════════════════════════════════════════
        //  UI helpers
        // ═════════════════════════════════════════════════════════════════════
        private static WebView2 MakeWebView()
        {
            var view = new WebView2();
            WebHost.ApplyThemeBackground(view); // R8 — no white flash before navigation
            return view;
        }

        private static Border MakeHost(double height, string placeholder)
        {
            var ph = new TextBlock
            {
                Text                = placeholder,
                FontStyle           = FontStyles.Italic,
                TextWrapping        = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Margin              = new Thickness(12),
            };
            ph.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            ph.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            ph.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");

            var host = new Border
            {
                Height          = height,
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(0, 0, 0, 8),
                Child           = ph,
            };
            host.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            host.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            return host;
        }

        private static Button MakeBtn(string label)
        {
            var b = new Button
            {
                Content         = label,
                Margin          = new Thickness(0, 0, 6, 6),
                BorderThickness = new Thickness(1),
                Template        = ControlStyles.BuildFlatButtonTemplate(),
                Cursor          = Cursors.Hand,
            };
            b.SetResourceReference(Button.MinHeightProperty,  "LemoineH_BtnMin");
            b.SetResourceReference(Button.PaddingProperty,    "LemoineTh_BtnPad");
            b.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_MD");
            b.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            b.Background = WpfBrushes.Transparent; // ⚠ direct assignment — "Transparent" is not a resource key
            b.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
            b.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
            return b;
        }

        private static TextBlock Desc(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            return tb;
        }

        /// <summary>
        /// A small scrolling probe log (mono font) owned by one step. All appends happen on
        /// the window's dispatcher thread (click handlers, await continuations, and WebView2
        /// events all arrive there), so no marshalling is needed.
        /// </summary>
        private sealed class ProbeLog
        {
            public FrameworkElement Root { get; }
            private readonly StackPanel   _lines;
            private readonly ScrollViewer _scroll;

            public ProbeLog(double height)
            {
                _lines  = new StackPanel();
                _scroll = new ScrollViewer
                {
                    Height                        = height,
                    VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content                       = _lines,
                    Padding                       = new Thickness(6, 4, 6, 4),
                };
                var border = new Border { BorderThickness = new Thickness(1), Child = _scroll };
                border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                border.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
                Root = border;
                Dim("(probe log — nothing run yet)");
            }

            public void Pass(string text)   => Add(text, "LemoineGreen");
            public void Fail(string text)   => Add(text, "LemoineRed");
            public void Info(string text)   => Add(text, "LemoineText");
            public void Dim(string text)    => Add(text, "LemoineTextDim");
            public void Bridge(string text) => Add(text, "LemoineAccent");

            private void Add(string text, string brushKey)
            {
                var tb = new TextBlock
                {
                    Text         = $"{DateTime.Now:HH:mm:ss}  {text}",
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 0, 0, 2),
                };
                tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                _lines.Children.Add(tb);
                _scroll.ScrollToEnd();
            }
        }
    }
}
