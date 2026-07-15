using System;
using System.Collections.Generic;
using System.Linq;
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
        private TextBlock? _loading;
        private bool _isRunning;

        public WebStepFlowWindow(IWebTool tool)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            Title     = tool.Title;
            Width     = 520;
            Height    = 660;
            MinWidth  = 380;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Borderless: remove the OS title bar; the designed HTML top bar drives
            // drag/minimize/close via the bridge. Mirrors WPF StepFlowWindow's chrome.
            WindowStyle        = WindowStyle.None;
            ResizeMode         = ResizeMode.CanResizeWithGrip;
            AllowsTransparency = false;
            ShowInTaskbar      = true;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight         = 0,                       // no OS drag caption; we drag from HTML
                ResizeBorderThickness = new Thickness(8),        // keep resizable edges
                GlassFrameThickness   = new Thickness(0),
                UseAeroCaptionButtons = false,
            });

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

            // Instant feedback while WebView2 cold-starts (the browser process spin-up is the
            // dominant open cost). Sits on top of the control's themed DefaultBackgroundColor and
            // is collapsed on the first successful navigation, so the user never sees a blank window.
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
                    if (e.IsSuccess)
                    {
                        WebAssets.ApplyVariablesLive(_view);         // R12
                        if (_loading != null) _loading.Visibility = Visibility.Collapsed;
                    }
                    else DiagnosticsLog.Warn("WebStepFlowWindow: navigation", $"WebErrorStatus={e.WebErrorStatus}");
                };
                _view.CoreWebView2.ProcessFailed += (s, e) =>
                    DiagnosticsLog.Warn("WebStepFlowWindow: process failed", e.ProcessFailedKind.ToString());

                _bridge = new WebBridge(_view.CoreWebView2);
                _bridge.On("state",  OnStateMessage);
                _bridge.On("action", OnActionMessage);
                _tool.ValidationChanged += OnToolValidationChanged;
                if (_tool is IWebRunPausable pausable) pausable.AwaitingUserChanged += OnAwaitingUserChanged;
                if (_tool is IWebStepRefresh refresh)  refresh.StepInputsChanged    += OnStepInputsChanged;

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
            var confirmable = _tool as IWebConfirmable;
            var steps = _steps.Select(s => s.ToPayload(
                _tool.SummaryFor(s.Id), _tool.IsStepVisible(s.Id), confirmable?.ConfirmLabelFor(s.Id))).ToList();
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

            // The last step is the review/run step (contract: hideable steps are never last). Its
            // content (a review block) is derived from current selections, so refresh it live as
            // earlier steps change. Editable inputs live on earlier steps, so this never disrupts typing.
            if (_steps.Count > 0) SendStepInputs(_steps[_steps.Count - 1].Id);
        }

        // Re-sends one step's inputs, rebuilt from the tool's CURRENT state (BuildSteps reflects
        // the latest selections), so a review block updates without a full page reload.
        private void SendStepInputs(string stepId)
        {
            var fresh = _tool.BuildSteps().FirstOrDefault(s => s.Id == stepId);
            if (fresh == null) return;
            _bridge?.Send("stepInputs", new Dictionary<string, object?>
            {
                ["stepId"] = stepId,
                ["inputs"] = fresh.Inputs.Select(i => i.ToPayload()).ToList(),
            });
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
                // ── Native OS dialogs (rule R26 - JS never touches the filesystem) ──
                case "browseFolder": BrowseFolder(Str(p, "stepId"), Str(p, "inputId")); break;
                case "browseFile":   BrowseFile(Str(p, "stepId"), Str(p, "inputId")); break;
                // ── Tool-declared action buttons (scan / pick-in-Revit) ─────────────
                case "tool":
                    try { (_tool as IWebToolAction)?.OnToolAction(Str(p, "stepId"), Str(p, "inputId")); }
                    catch (Exception ex) { DiagnosticsLog.Error("WebStepFlowWindow: tool action", ex); }
                    break;
                // ── Pausable run (mirrors IRunPausable) ─────────────────────────────
                case "continueRun": (_tool as IWebRunPausable)?.ContinueRun(); break;
                case "skipItem":    (_tool as IWebRunPausable)?.SkipCurrentItem(); break;
                // ── Window chrome (the HTML top bar replaces the OS title bar) ──────
                case "drag":     BeginNativeDrag(); break;
                case "minimize": WindowState = WindowState.Minimized; break;
                case "close":    Close(); break;
                case "confirm":
                    try { (_tool as IWebConfirmable)?.OnStepConfirm(Str(p, "stepId")); }
                    catch (Exception ex) { DiagnosticsLog.Error("WebStepFlowWindow: step confirm", ex); }
                    SendValidationAndSummaries();
                    break;
                default: // navigate
                    SendValidationAndSummaries();
                    break;
            }
        }

        // Opens the OS folder/file picker on this (STA) thread and pushes the chosen path back
        // to both the tool (OnState) and the page (setInput). Runs on the WebUiThread, which is
        // STA, so the WinForms common dialogs are valid here. Never throws into the bridge.
        private void BrowseFolder(string stepId, string inputId)
        {
            try
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog();
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    ApplyPickedPath(stepId, inputId, dlg.SelectedPath);
            }
            catch (Exception ex) { DiagnosticsLog.Error("WebStepFlowWindow: browse folder", ex); }
        }

        private void BrowseFile(string stepId, string inputId)
        {
            try
            {
                using var dlg = new System.Windows.Forms.OpenFileDialog { CheckFileExists = true };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    ApplyPickedPath(stepId, inputId, dlg.FileName);
            }
            catch (Exception ex) { DiagnosticsLog.Error("WebStepFlowWindow: browse file", ex); }
        }

        private void ApplyPickedPath(string stepId, string inputId, string path)
        {
            try { _tool.OnState(stepId, inputId, path); }
            catch (Exception ex) { DiagnosticsLog.Error($"WebStepFlowWindow: OnState (browse) '{inputId}'", ex); }
            _bridge?.Send("setInput", new Dictionary<string, object?>
            { ["stepId"] = stepId, ["inputId"] = inputId, ["value"] = path });
            SendValidationAndSummaries();
        }

        // Starts the OS window-move loop from the HTML title bar's mousedown. DragMove() is
        // unreliable here because the WebView2 child HWND owns the mouse, so use the Win32
        // ReleaseCapture + WM_NCLBUTTONDOWN(HTCAPTION) technique on the window handle.
        private void BeginNativeDrag()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                ReleaseCapture();
                SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebStepFlowWindow: begin drag", ex); }
        }

        private const uint WM_NCLBUTTONDOWN = 0x00A1;
        private const int  HTCAPTION        = 0x0002;
        [DllImport("user32.dll")] private static extern bool   ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

        // IWebRunPausable — raised from any thread; show/hide the Continue/Skip footer buttons.
        private void OnAwaitingUserChanged(bool awaiting, string? continueLabel, string? skipLabel) =>
            SafeUi(() => _bridge?.Send("pause", new Dictionary<string, object?>
            {
                ["awaiting"]      = awaiting,
                ["continueLabel"] = continueLabel,
                ["skipLabel"]     = skipLabel,
            }));

        // IWebStepRefresh — tool signals a step's inputs are stale (async scan/pick landed).
        private void OnStepInputsChanged(string stepId) => SafeUi(() =>
        {
            SendStepInputs(stepId);
            SendValidationAndSummaries();
        });

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
            if (_tool is IWebRunPausable pausable) pausable.AwaitingUserChanged -= OnAwaitingUserChanged;
            if (_tool is IWebStepRefresh refresh)  refresh.StepInputsChanged    -= OnStepInputsChanged;
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
