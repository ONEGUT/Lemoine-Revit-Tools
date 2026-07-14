using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LemoineTools.Framework.Web
{
    // =========================================================================
    // WebStepFlowWindow (Phase 2b) - the HTML analogue of StepFlowWindow.
    //
    // A code-only WPF Window hosting a single WebView2 that loads stepflow.html
    // (over the lemoine.app virtual host) and drives an IWebTool entirely through
    // the bridge message contract:
    //   C# -> JS : init / validation / log / progress / complete / stepSummary /
    //              stepHidden
    //   JS -> C# : state (input changed) / action (confirm/navigate/reset/run)
    //
    // The run lifecycle mirrors StepFlowWindow.StartRun exactly (RunState.Begin,
    // RevitFailureCapture.BeginRun, RunLogSink.Set, marshalled callbacks), so a
    // tool's existing ExternalEvent handler wiring is unchanged. Theme/UI-size
    // changes push CSS variables live (no reload). Runs on its own STA thread; the
    // Dispatcher.UnhandledException net keeps a stray throw from hard-crashing Revit.
    // =========================================================================
    public class WebStepFlowWindow : Window
    {
        private readonly IWebTool _tool;
        private IReadOnlyList<WebStep> _steps = Array.Empty<WebStep>();
        private WebView2?  _view;
        private WebBridge? _bridge;
        private bool _isRunning;

        public WebStepFlowWindow(IWebTool tool)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            Title  = tool.Title;
            Width  = 520;
            Height = 660;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            AppSettings.Instance.ApplyTo(Resources);
            Background = AppSettings.Instance.ActiveTheme.PageBg;

            AppSettings.Instance.ThemeChanged  += OnThemeChanged;
            AppSettings.Instance.UiSizeChanged += OnUiSizeChanged;
            Dispatcher.UnhandledException      += OnUnhandled;   // last-resort net (non-StepFlowWindow)
            Closed                             += OnClosed;

            var host = new Grid();
            _view = new WebView2();
            WebHost.ApplyThemeBackground(_view);                 // R8 - no white flash
            host.Children.Add(_view);
            Content = host;

            // Kick init from Loaded, NOT the constructor: the constructor runs before
            // Dispatcher.Run(), so there is no SynchronizationContext yet and the async
            // continuations in InitAsync would resume on a thread-pool thread and touch the
            // STA-affine CoreWebView2 from the wrong thread (InvalidComObjectException). Loaded
            // fires under the pumping dispatcher, so awaits resume on this STA thread.
            Loaded += OnLoaded;
        }

        private bool _initStarted;
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_initStarted) return;   // Loaded can fire more than once
            _initStarted = true;
            _ = InitAsync();            // failures are logged inside, never silent
        }

        private async Task InitAsync()
        {
            try
            {
                var env = await WebHost.EnvironmentAsync();       // R5 - per-thread env
                await _view!.EnsureCoreWebView2Async(env);        // R3
                WebHost.ConfigureProductionSettings(_view.CoreWebView2); // R7
                WebAssets.MapVirtualHost(_view.CoreWebView2);     // R16
                _view.NavigationCompleted += (s, e) =>
                {
                    if (e.IsSuccess) WebAssets.ApplyVariablesLive(_view); // R12
                    else DiagnosticsLog.Warn("WebStepFlowWindow: navigation", $"WebErrorStatus={e.WebErrorStatus}");
                };
                _view.CoreWebView2.ProcessFailed += (s, e) =>
                    DiagnosticsLog.Warn("WebStepFlowWindow: process failed", e.ProcessFailedKind.ToString());

                _bridge = new WebBridge(_view.CoreWebView2);
                _bridge.On("state",  OnStateMessage);
                _bridge.On("action", OnActionMessage);
                _tool.ValidationChanged += OnToolValidationChanged;

                _steps = _tool.BuildSteps();
                _view.CoreWebView2.Navigate(WebAssets.PageUrl("stepflow.html"));
                // Queued by WebBridge until the page's `ready` arrives, then flushed (R18).
                SendInit();
                SendValidationAndSummaries();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("WebStepFlowWindow: init", ex);
            }
        }

        // ── Outbound (C# -> JS) ──────────────────────────────────────────────
        private void SendInit()
        {
            var steps = _steps.Select(s => s.ToPayload(_tool.SummaryFor(s.Id), _tool.IsStepVisible(s.Id))).ToList();
            _bridge?.Send("init", new Dictionary<string, object?>
            {
                ["title"]    = _tool.Title,
                ["runLabel"] = _tool.RunLabel,
                ["steps"]    = steps,
            });
        }

        private void SendValidationAndSummaries()
        {
            var stepValid = new Dictionary<string, object?>();
            foreach (var s in _steps) stepValid[s.Id] = _tool.IsStepValid(s.Id);
            _bridge?.Send("validation", new Dictionary<string, object?>
            {
                ["steps"]  = stepValid,
                ["canRun"] = _tool.CanRun() && !_isRunning,
            });
            foreach (var s in _steps)
            {
                _bridge?.Send("stepSummary", new Dictionary<string, object?> { ["stepId"] = s.Id, ["text"] = _tool.SummaryFor(s.Id) });
                _bridge?.Send("stepHidden",  new Dictionary<string, object?> { ["stepId"] = s.Id, ["hidden"] = !_tool.IsStepVisible(s.Id) });
            }
        }

        // ── Inbound (JS -> C#) - both arrive on this window's dispatcher thread ──
        private void OnStateMessage(IReadOnlyDictionary<string, object?> p)
        {
            string stepId  = Str(p, "stepId");
            string inputId = Str(p, "inputId");
            p.TryGetValue("value", out var value);
            try { _tool.OnState(stepId, inputId, value); }
            catch (Exception ex) { DiagnosticsLog.Error($"WebStepFlowWindow: OnState '{inputId}'", ex); }
            SendValidationAndSummaries();
        }

        private void OnActionMessage(IReadOnlyDictionary<string, object?> p)
        {
            switch (Str(p, "action"))
            {
                case "run":
                    if (!_isRunning) StartRun();
                    break;
                case "reset":
                    // During a run the footer button means Cancel (cooperative stop); otherwise
                    // re-send the spec to reset the page.
                    if (_isRunning) RunState.RequestCancel();
                    else { SendInit(); SendValidationAndSummaries(); }
                    break;
                default: // confirm / navigate
                    SendValidationAndSummaries();
                    break;
            }
        }

        // ── Run lifecycle (mirrors StepFlowWindow.StartRun) ──────────────────
        private void StartRun()
        {
            _isRunning = true;
            RunState.Begin();
            RevitFailureCapture.BeginRun();

            Action<string, string> pushLog = (t, st) =>
                SafeUi(() => _bridge?.Send("log", new Dictionary<string, object?> { ["text"] = t, ["status"] = st }));
            RunLogSink.Set(pushLog);

            SendValidationAndSummaries(); // canRun=false while running
            pushLog("Starting...", "info");

            _tool.Run(
                pushLog: pushLog,
                onProgress: (pct, pass, fail, skip) => SafeUi(() =>
                    _bridge?.Send("progress", new Dictionary<string, object?>
                    { ["pct"] = pct, ["pass"] = pass, ["fail"] = fail, ["skip"] = skip })),
                onComplete: (pass, fail, skip) => SafeUi(() =>
                {
                    _isRunning = false;
                    RunState.End();
                    RunLogSink.Clear();
                    _bridge?.Send("complete", new Dictionary<string, object?>
                    { ["pass"] = pass, ["fail"] = fail, ["skip"] = skip });
                    SendValidationAndSummaries(); // re-enable Run
                }));
        }

        private void OnToolValidationChanged(object? sender, EventArgs e) => SafeUi(SendValidationAndSummaries);

        // ── Theme / lifetime ─────────────────────────────────────────────────
        private void OnThemeChanged(ThemePalette t) => SafeUi(() =>
        {
            AppSettings.Instance.ApplyTo(Resources);
            Background = t.PageBg;
            if (_view != null) WebAssets.ApplyVariablesLive(_view);
        });

        private void OnUiSizeChanged(UiSize _) => SafeUi(() =>
        {
            if (_view != null) WebAssets.ApplyVariablesLive(_view);
        });

        private void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            DiagnosticsLog.Error($"WebStepFlowWindow '{_tool.Title}' unhandled UI exception", e.Exception);
            e.Handled = true; // keep the window (and Revit) alive
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            AppSettings.Instance.ThemeChanged  -= OnThemeChanged;
            AppSettings.Instance.UiSizeChanged -= OnUiSizeChanged;
            Dispatcher.UnhandledException      -= OnUnhandled;
            _tool.ValidationChanged            -= OnToolValidationChanged;
            RunLogSink.Clear();
            (_tool as IWebToolCleanup)?.OnWindowClosed();
            try { _view?.Dispose(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebStepFlowWindow: dispose view", ex); }
            _view = null;
            _bridge = null;
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private void SafeUi(Action action)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action);
        }

        private static string Str(IReadOnlyDictionary<string, object?> p, string key) =>
            (p.TryGetValue(key, out var v) ? v as string : null) ?? "";
    }
}
