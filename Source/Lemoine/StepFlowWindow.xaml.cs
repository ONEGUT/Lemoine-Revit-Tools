using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Lemoine
{
    public partial class StepFlowWindow : Window
    {
        private readonly ILemoineTool _tool;

        // Callbacks the window hands to the static ExternalEvent handlers / tool ViewModels
        // (which outlive the window) capture this sink instead of `this`. Severing it on close
        // lets the whole window + visual tree be garbage-collected even though the long-lived
        // handler still holds the delegate. The sink itself is tiny.
        private sealed class WindowSink { public StepFlowWindow? Win; }
        private readonly WindowSink _sink = new WindowSink();

        // Theme is now owned by LemoineSettings singleton
        private LemoineTheme ActiveTheme => LemoineSettings.Instance.ActiveTheme;

        private int  _activeStep = 0;
        private bool _isRunning  = false;
        private bool _isDone     = false;

        private LemoineReviewSummary? _reviewSummary; // framework-built review for ILemoineReviewable tools

        // Named XAML elements — _outerBorder is declared by x:Name in XAML

        private Border[]    _stepRows        = Array.Empty<Border>();
        private Border[]    _accentBars      = Array.Empty<Border>();
        private Border[]    _stepCircles     = Array.Empty<Border>();
        private TextBlock[] _circleTexts     = Array.Empty<TextBlock>();
        private TextBlock[] _stepTitles      = Array.Empty<TextBlock>();
        private TextBlock[] _summaryTexts    = Array.Empty<TextBlock>();
        private TextBlock[] _waitingTexts    = Array.Empty<TextBlock>();
        private TextBlock[] _runningTexts    = Array.Empty<TextBlock>();
        private Border[]    _contentBorders  = Array.Empty<Border>();
        private TextBlock[] _validationTexts = Array.Empty<TextBlock>();
        private Button[]    _confirmBtns     = Array.Empty<Button>();
        private Button?[]   _backBtns        = Array.Empty<Button?>();

        private Rectangle[]  _pipRects       = Array.Empty<Rectangle>();
        private TextBlock?   _stepCounter;
        private TextBlock    _statusText      = null!;
        private Rectangle    _progressFill    = null!;
        private Border       _progressTrack   = null!;
        private int          _progressPct     = 0;
        private TextBlock    _passText        = null!;
        private TextBlock    _failText        = null!;
        private TextBlock    _skipText        = null!;

        private StackPanel   _logStack  = null!;
        private ScrollViewer _logScroll = null!;
        private Button       _resetBtn  = null!;
        private Button       _closeBtn  = null!;

        // Log resize drag state
        private bool   _isResizingLog   = false;
        private double _logDragStartY   = 0;
        private double _logDragStartH   = 0;
        private const double _logMinH   = 60;
        private const double _logMaxH   = 600;

        public StepFlowWindow(ILemoineTool tool)
        {
            InitializeComponent();
            _tool  = tool ?? throw new ArgumentNullException(nameof(tool));
            _sink.Win = this;
            Title = tool.Title;

            LemoineSettings.Instance.ApplyTo(Resources);
            Background = ActiveTheme.PageBg;

            LemoineSettings.Instance.ThemeChanged  += OnThemeChanged;
            LemoineSettings.Instance.UiSizeChanged += OnUiSizeChanged;
            // Last-resort safety net for this window's dedicated STA thread. An unhandled
            // exception in any tool's event handler would otherwise tear down this dispatcher
            // and hard-crash Revit with NO diagnostics.log entry (there is no other
            // DispatcherUnhandledException handler in the app). Route it through LemoineLog and
            // keep the window alive. Named handler, detached on Closed.
            Dispatcher.UnhandledException += OnDispatcherUnhandledException;
            Closed += (s, e) =>
            {
                Dispatcher.UnhandledException -= OnDispatcherUnhandledException;
                LemoineSettings.Instance.ThemeChanged  -= OnThemeChanged;
                LemoineSettings.Instance.UiSizeChanged -= OnUiSizeChanged;
                // The tool (VM) is retained by its static ExternalEvent handler and outlives
                // this window — detach so a late ValidationChanged can't touch a dead window.
                _tool.ValidationChanged -= OnToolValidationChanged;
                // Let the tool detach any callbacks it parked on its static ExternalEvent handler.
                (_tool as ILemoineToolCleanup)?.OnWindowClosed();
                // Sever the sink so the static handler's retained run/refresh/activate/navigate
                // callbacks no longer root this window — it (and its visual tree) can now be GC'd.
                _sink.Win = null;
                // Defensive: if the window is closed mid-run, stop routing Revit failures here.
                LemoineRunLog.Clear();
            };

            BuildChrome();
            _tool.ValidationChanged += OnToolValidationChanged;
            if (_tool is ILemoineNavigable nav)
                nav.NavigateRequested += (s, idx) => { var w = _sink.Win; if (w != null) w.SafeBeginInvoke(() => w.ActivateStep(idx)); };
        }

        // ValidationChanged can be raised from a tool's refresh callback on Revit's main
        // thread (not this window's STA thread) and could even arrive after the window closed.
        // Stay synchronous on the UI thread (preserves validation timing); otherwise marshal,
        // guarded against a terminated dispatcher.
        private void OnToolValidationChanged(object? sender, EventArgs e)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (!Dispatcher.CheckAccess()) { SafeBeginInvoke(() => OnToolValidationChanged(sender, e)); return; }
            RefreshStepVisibility(); RefreshStepState(_activeStep); PopulateReview();
        }

        // Catches any exception that bubbles to this STA thread's dispatcher (event handlers,
        // layout, etc.). Without this an unhandled UI exception terminates Revit with no trace;
        // here it is logged and swallowed so the window stays usable. The originating handler's
        // partial work may be lost, but the process survives.
        private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LemoineLog.Error($"StepFlowWindow '{_tool.Title}' unhandled UI exception", e.Exception);
            e.Handled = true;
        }

        private void OnThemeChanged(LemoineTheme t)
        {
            // Non-blocking marshal back to this window's own dispatcher (the event fires on the
            // theme-change thread). A blocking Invoke here can deadlock against Revit's main
            // thread; bail if this dispatcher is already shutting down.
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LemoineSettings.Instance.ApplyTo(Resources);
                Background = t.PageBg;
                ApplyContainerStyles();
            }));
        }

        private void OnUiSizeChanged(LemoineUiSize _)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LemoineSettings.Instance.ApplyScaleTo(Resources);
                LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 5);
                UpdateRowHeights();
                // Re-measure progress fill width since track may have changed
                _progressTrack?.UpdateLayout();
                ResizeProgressFill();
            }));
        }

        // Marshals back onto this window's dispatcher, but bails if the dispatcher has begun
        // shutting down (window closing/closed). Callbacks passed to the static ExternalEvent
        // handlers and tool ViewModels outlive this window, so a late callback calling
        // Dispatcher.BeginInvoke on a terminated dispatcher would throw on Revit's main thread
        // and hard-crash Revit. This guard makes such a late callback a no-op.
        private void SafeBeginInvoke(Action action)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(action);
        }

        // Rebuilds a step's content in place (IStepAware refresh). Runs on the UI thread.
        private void RefreshStepContent(string stepId)
        {
            int idx = Array.FindIndex(_tool.Steps, s => s.Id == stepId);
            if (idx < 0) return;
            var newContent = _tool.GetStepContent(stepId) ?? new Grid();
            if (_contentBorders[idx].Child is StackPanel cs && cs.Children.Count > 0)
            {
                cs.Children.RemoveAt(0);
                cs.Children.Insert(0, newContent);
            }
        }

        // Pulls this window back to the foreground after a Revit interaction (IWindowActivatable).
        private void BringToForeground()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            // Nudge to the top without staying pinned, then return keyboard focus.
            Topmost = true; Topmost = false;
            Focus();
        }

        private void UpdateRowHeights()
        {
            if (_root == null) return;
            _root.RowDefinitions[0].Height = new GridLength(LemoineSettings.Instance.ToolbarHeight);
            _root.RowDefinitions[4].Height = new GridLength(LemoineSettings.Instance.FooterHeight);
        }

        // ═══════════════════════════════════════ CHROME ═══════════════════════
        private void BuildChrome()
        {
            BuildToolbar();
            BuildProgressStrip();
            BuildLogArea();        // creates _logScroll/_logStack before BuildStepAccordion needs them
            BuildStepAccordion();
            BuildFooter();
            InjectControlStyles();        // ← themed scrollbars, ComboBox, CheckBox
            ApplyContainerStyles();

            // Wire step-activation callbacks for tools that implement IStepAware.
            if (_tool is IStepAware stepAware)
            {
                // Captures the sink (not `this`) so a retained refresh callback can't keep the
                // window alive after close; guarded so a late refresh doesn't hit a dead dispatcher.
                stepAware.SetContentRefreshCallback(stepId =>
                {
                    var w = _sink.Win;
                    if (w != null) w.SafeBeginInvoke(() => w.RefreshStepContent(stepId));
                });
            }

            // Give an activatable tool a way to pull its host window back to the foreground after a
            // Revit interaction (e.g. a slab PickObject) brings Revit's main window forward.
            if (_tool is IWindowActivatable activatable)
            {
                activatable.SetActivateCallback(() =>
                {
                    var w = _sink.Win;
                    if (w != null) w.SafeBeginInvoke(() => w.BringToForeground());
                });
            }

            // Defer tab-bar rendering until after callers can RegisterLogTab()
            Loaded += (s, e) => BuildTabBar();
            ActivateStep(0);
        }

        /// <summary>
        /// Sets SetResourceReference on every named container element that
        /// needs a theme-driven Background or BorderBrush.
        /// Called once on build and again on every ThemeChanged event.
        /// This is the equivalent of GlobalSettingsWindow.ApplyChrome().
        /// </summary>
        private void ApplyContainerStyles()
        {
            _root.SetResourceReference(Grid.BackgroundProperty, "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _outerBorder.CornerRadius = new CornerRadius(8); // matches Windows 11 DWM rounding
        }

        /// <summary>
        /// Injects full ControlTemplate overrides via XamlReader.Parse.
        /// FrameworkElementFactory.AppendChild cannot be used on Track (not IAddChild),
        /// so we define the ScrollBar and ComboBox templates as XAML strings instead.
        /// </summary>
        private void InjectControlStyles()
            => LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 5);


        private void BuildToolbar()
        {
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            _stepCounter = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            _stepCounter.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _stepCounter.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _stepCounter.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var cBorder = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6, 0, 0, 0),
            };
            cBorder.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_SM");
            cBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            cBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            cBorder.Child = _stepCounter;
            right.Children.Add(cBorder);

            var closeBtn = new Button
            {
                Content = "×",
                Cursor  = Cursors.Hand,
                BorderThickness = new Thickness(1),
                Margin  = new Thickness(6, 0, 0, 0),
                Template = FlatBtnTemplate(),
                ToolTip  = "Close",
            };
            closeBtn.SetResourceReference(Button.HeightProperty,     "LemoineH_BtnSm");
            closeBtn.SetResourceReference(Button.PaddingProperty,    "LemoineTh_BtnSmPad");
            closeBtn.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_SM");
            closeBtn.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            // Neutral ghost styling — red is reserved for destructive actions only
            closeBtn.Background = Brushes.Transparent;
            closeBtn.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
            closeBtn.SetResourceReference(Button.ForegroundProperty,  "LemoineTextDim");
            closeBtn.Click += (s, e) => Close();
            right.Children.Add(closeBtn);

            // Wire LemoineTitleBar — passes tool title, no icon glyph (placeholder reserved for future SVG icons),
            // right content panel, and AllowsMaximize=true for double-click restore/maximize.
            _toolbarBorder.Child = new Controls.LemoineTitleBar
            {
                Title          = _tool.Title,
                AllowsMaximize = true,
                RightContent   = right,
            };
            UpdateStepCounter();
        }

        // ─────────────────── Progress strip ───────────────────────────────────
        private void BuildProgressStrip()
        {
            _progressStrip.SetResourceReference(StackPanel.MarginProperty, "LemoineTh_ProgressMar");

            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

            _statusText = new TextBlock { MinWidth = 115, VerticalAlignment = VerticalAlignment.Center };
            _statusText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            _statusText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            _statusText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            DockPanel.SetDock(_statusText, Dock.Left);
            top.Children.Add(_statusText);

            var counters = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _passText = Ctr("LemoineGreen");   counters.Children.Add(_passText); counters.Children.Add(CtrLbl(" pass  "));
            _failText = Ctr("LemoineRed");     counters.Children.Add(_failText); counters.Children.Add(CtrLbl(" fail  "));
            _skipText = Ctr("LemoineTextDim"); counters.Children.Add(_skipText); counters.Children.Add(CtrLbl(" skip"));
            DockPanel.SetDock(counters, Dock.Right);
            top.Children.Add(counters);

            _progressTrack = new Border
            {
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,  // center with status text
                ClipToBounds = true,
            };
            _progressTrack.SetResourceReference(Border.HeightProperty,     "LemoineH_ProgBar");
            _progressTrack.SetResourceReference(Border.BackgroundProperty,  "LemoineBorder");
            _progressFill = new Rectangle { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, RadiusX = 2, RadiusY = 2 };
            _progressFill.SetResourceReference(Rectangle.FillProperty, "LemoineAccent");
            _progressFill.SetResourceReference(Rectangle.HeightProperty, "LemoineH_ProgBar");
            var fg = new Grid(); fg.Children.Add(_progressFill);
            _progressTrack.Child = fg;
            _progressTrack.SizeChanged += (s, e) => ResizeProgressFill();
            top.Children.Add(_progressTrack);
            _progressStrip.Children.Add(top);

            var steps  = _tool.Steps;
            var pipRow = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 10) };
            pipRow.SetResourceReference(UniformGrid.HeightProperty, "LemoineH_Pip");
            _pipRects = new Rectangle[steps.Length];
            for (int i = 0; i < steps.Length; i++)
            {
                var p = new Rectangle { Margin = new Thickness(i > 0 ? 3 : 0, 0, 0, 0), RadiusX = 2, RadiusY = 2 };
                p.SetResourceReference(Rectangle.FillProperty, "LemoineBorder");
                _pipRects[i] = p; pipRow.Children.Add(p);
            }
            _progressStrip.Children.Add(pipRow);
            SetStatus("● Configuring…"); SetCounts(0, 0, 0);
        }

        // ─────────────────── Step accordion ───────────────────────────────────
        private void BuildStepAccordion()
        {
            var steps = _tool.Steps;
            _stepRows = new Border[steps.Length]; _accentBars = new Border[steps.Length];
            _stepCircles = new Border[steps.Length]; _circleTexts = new TextBlock[steps.Length];
            _stepTitles = new TextBlock[steps.Length]; _summaryTexts = new TextBlock[steps.Length];
            _waitingTexts = new TextBlock[steps.Length]; _runningTexts = new TextBlock[steps.Length];
            _contentBorders = new Border[steps.Length]; _validationTexts = new TextBlock[steps.Length];
            _confirmBtns = new Button[steps.Length]; _backBtns = new Button?[steps.Length];

            for (int i = 0; i < steps.Length; i++)
            {
                FrameworkElement content;
                // A reviewable tool's LAST step is framework-rendered as a review summary —
                // the tool need not build any content for it. This guarantees the final step
                // is review-only by construction.
                if (i == steps.Length - 1 && _tool is ILemoineReviewable)
                {
                    _reviewSummary = new LemoineReviewSummary();
                    content        = _reviewSummary;
                }
                else
                {
                    content = SafeBuildStepContent(steps[i].Id);
                }
                _stepRows[i] = BuildStepRow(i, steps[i], content);
                _stepStack.Children.Add(_stepRows[i]);
            }
            PopulateReview();
        }

        // Builds a step's content, but never lets a faulty tool take down Revit: a thrown
        // exception is logged to diagnostics.log and replaced with a visible error panel so
        // the window still opens. (Step content is built eagerly at window construction.)
        private FrameworkElement SafeBuildStepContent(string stepId)
        {
            try
            {
                return _tool.GetStepContent(stepId) ?? new Grid();
            }
            catch (Exception ex)
            {
                LemoineLog.Error($"GetStepContent('{stepId}') for tool '{_tool.Title}'", ex);
                return BuildStepErrorPanel(stepId, ex);
            }
        }

        private FrameworkElement BuildStepErrorPanel(string stepId, Exception ex)
        {
            var sp = new StackPanel();
            var hdr = new TextBlock
            {
                Text         = $"This step ('{stepId}') failed to load.",
                TextWrapping = TextWrapping.Wrap,
                FontWeight   = FontWeights.SemiBold,
            };
            hdr.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            hdr.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            hdr.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            sp.Children.Add(hdr);

            var detail = new TextBlock
            {
                Text         = $"{ex.GetType().Name}: {ex.Message}\nSee %AppData%\\LemoineTools\\diagnostics.log for the full stack trace.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 4, 0, 0),
            };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            detail.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            detail.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            sp.Children.Add(detail);
            return sp;
        }

        // Fills the framework-rendered review summary from the tool's ILemoineReviewable
        // contract. No-op for non-reviewable tools. Called on build, on entering the last
        // step, and whenever validation changes so the values stay current. Guarded so a
        // throwing ReviewValues degrades to a logged error instead of crashing Revit.
        private void PopulateReview()
        {
            if (_reviewSummary == null || !(_tool is ILemoineReviewable r)) return;
            try
            {
                _reviewSummary.SetItems(r.ReviewItems, r.ReviewValues, r.ReviewChips, r.ReviewNote, r.ReviewWarning);
            }
            catch (Exception ex)
            {
                LemoineLog.Error($"PopulateReview for tool '{_tool.Title}'", ex);
            }
        }

        private Border BuildStepRow(int idx, StepDefinition step, FrameworkElement content)
        {
            var row = new Border
            {
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(0, 0, 0, 8),
            };
            row.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
            row.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            var ig  = new Grid();
            ig.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
            ig.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // CornerRadius(8,0,0,8) matches the left-side curve of the outer step pill.
            var bar = new Border { CornerRadius = new CornerRadius(8, 0, 0, 8) }; _accentBars[idx] = bar;
            Grid.SetColumn(bar, 0); ig.Children.Add(bar);

            var main = new StackPanel(); Grid.SetColumn(main, 1); ig.Children.Add(main);
            row.Child = ig;

            // Header
            var hdr = new DockPanel();
            hdr.SetResourceReference(DockPanel.MarginProperty, "LemoineTh_RowPad");

            var circle = new Border
            {
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,   // center with single-line title
                Margin = new Thickness(0, 0, 8, 0),
            };
            circle.CornerRadius = new CornerRadius(LemoineSettings.Instance.S(9));
            circle.SetResourceReference(Border.WidthProperty,  "LemoineH_Circle");
            circle.SetResourceReference(Border.HeightProperty, "LemoineH_Circle");

            var circTb = new TextBlock
            {
                Text = (idx + 1).ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            circTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            circTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            circle.Child = circTb;
            _stepCircles[idx] = circle;
            _circleTexts[idx] = circTb;
            DockPanel.SetDock(circle, Dock.Left);
            hdr.Children.Add(circle);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            // Step ID and title on the same baseline row
            var idTitle = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var idText = new TextBlock
            {
                Text = step.Id,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            idText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            idText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            idText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var titleTb = new TextBlock
            {
                Text = step.Title, FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            titleTb.SetResourceReference(TextBlock.FontSizeProperty,  "LemoineFS_MD");
            titleTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _stepTitles[idx] = titleTb;

            idTitle.Children.Add(idText);
            idTitle.Children.Add(titleTb);
            textStack.Children.Add(idTitle);

            // Summary — shown when step is done. Italic, main text color
            var summaryTb = new TextBlock
            {
                Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed, FontStyle = FontStyles.Italic,
            };
            summaryTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            summaryTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            summaryTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _summaryTexts[idx] = summaryTb;
            textStack.Children.Add(summaryTb);

            // "Waiting…" — italic, main text color
            var waitingTb = new TextBlock
            {
                Text = "Waiting…", Margin = new Thickness(0, 2, 0, 0),
                Visibility = Visibility.Collapsed, FontStyle = FontStyles.Italic,
            };
            waitingTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            waitingTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            _waitingTexts[idx] = waitingTb;
            textStack.Children.Add(waitingTb);

            var runningTb = new TextBlock
            {
                Text = "● Processing in Revit…", Margin = new Thickness(0, 2, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            runningTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            runningTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            runningTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            _runningTexts[idx] = runningTb;
            textStack.Children.Add(runningTb);

            hdr.Children.Add(textStack);
            main.Children.Add(hdr);

            // Collapsible content area
            var cb = new Border { ClipToBounds = true, MaxHeight = 0 };
            cb.SetResourceReference(Border.PaddingProperty, "LemoineTh_ContentPad");

            var cs = new StackPanel();
            cs.Children.Add(content);

            var validTb = new TextBlock
            {
                Text = "✗ Required before proceeding",
                Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };
            validTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            validTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            validTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            _validationTexts[idx] = validTb;
            cs.Children.Add(validTb);

            // Grid with 3 columns: back | spacer | confirm
            // This gives the back button the same offset from the left as confirm has from the right
            var btnGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var captIdx = idx;
            string btnLabel;
            if (idx == _tool.Steps.Length - 1)
                btnLabel = _tool.RunLabel;
            else if (_tool is ILemoineStepConfirmable sc && sc.ConfirmLabelFor(step.Id) is string lbl)
                btnLabel = lbl;
            else
                btnLabel = "Confirm →";
            var confirmBtn = BuildButton(btnLabel, true);
            confirmBtn.Click += (s, e) =>
            {
                if (_tool is ILemoineStepConfirmable sc2)
                    sc2.OnStepConfirm(_tool.Steps[captIdx].Id);
                ConfirmStep(captIdx);
            };
            _confirmBtns[idx] = confirmBtn;
            Grid.SetColumn(confirmBtn, 2);
            btnGrid.Children.Add(confirmBtn);

            if (idx > 0)
            {
                var backBtn = BuildButton("← Back", false);
                backBtn.Width = 90;
                backBtn.Click += (s, e) => { int prev = PrevVisible(captIdx); if (prev >= 0) ActivateStep(prev); };
                _backBtns[idx] = backBtn;
                Grid.SetColumn(backBtn, 0);
                btnGrid.Children.Add(backBtn);
            }
            cs.Children.Add(btnGrid);

            // Last step: embed the log area below the run button
            if (idx == _tool.Steps.Length - 1)
            {
                cs.Children.Add(HSep(8));
                _logAreaContainer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                cs.Children.Add(_logAreaContainer);
            }

            cb.Child = cs;
            _contentBorders[idx] = cb;
            main.Children.Add(cb);
            return row;
        }

        // ─────────────────── Log tab area ─────────────────────────────────────
        // Tab framework — add future tabs via RegisterLogTab() before Show()
        private readonly List<(string id, string label, FrameworkElement content)> _logTabs
            = new List<(string, string, FrameworkElement)>();
        private string      _activeLogTab    = "log";
        private StackPanel  _tabButtonBar    = null!;
        private Border      _tabContentHost  = null!;
        private StackPanel  _logAreaContainer = null!;  // injected into the last step's content

        /// <summary>
        /// Register an additional tab in the log area.
        /// Call before Show(). Each tab's content is shown/hidden on click.
        /// </summary>
        public void RegisterLogTab(string id, string label, FrameworkElement content)
            => _logTabs.Add((id, label, content));

        private void BuildLogArea()
        {
            // Default "Output Log" tab content
            var logBrd = new Border { BorderThickness = new Thickness(0) };
            logBrd.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");

            _logScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(8, 6, 8, 6),
            };
            _logStack = new StackPanel();
            _logScroll.SetResourceReference(ScrollViewer.HeightProperty, "LemoineH_LogArea");
            _logScroll.Content = _logStack;
            LemoineControlStyles.WireBubblingScroll(_logScroll);

            // Handle is docked to the bottom so dragging is natural (pull down = taller)
            var logDp = new DockPanel { LastChildFill = true };
            var handle = BuildLogResizeHandle();
            DockPanel.SetDock(handle, Dock.Bottom);
            logDp.Children.Add(handle);
            logDp.Children.Add(_logScroll);
            logBrd.Child = logDp;

            // Seed with the default tab — extra tabs added via RegisterLogTab()
            _logTabs.Insert(0, ("log", "Output Log", logBrd));

            // _logArea (XAML Grid.Row 3) is unused — log lives inside the last step
            _logArea.Visibility = Visibility.Collapsed;

            PushLog("Ready.", "info");
        }

        private void BuildTabBar()
        {
            _logAreaContainer.Children.Clear();

            // ── Tab bar (only rendered when there are multiple tabs) ───────────
            if (_logTabs.Count > 1)
            {
                var tabBarBorder = new Border { BorderThickness = new Thickness(0) };
                _tabButtonBar = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(4, 0, 0, 0),
                };
                tabBarBorder.Child = _tabButtonBar;
                _logAreaContainer.Children.Add(tabBarBorder);
            }
            else
            {
                _tabButtonBar = new StackPanel();  // inert placeholder
            }

            // ── Tab content host ──────────────────────────────────────────────
            _tabContentHost = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Margin          = new Thickness(0),
            };
            _tabContentHost.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            _tabContentHost.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _logAreaContainer.Children.Add(_tabContentHost);

            // Build tab buttons and wire up content
            foreach (var tab in _logTabs)
                _tabButtonBar.Children.Add(BuildTabButton(tab.id, tab.label));

            // Show the first tab
            ActivateLogTab(_activeLogTab);
        }

        private Border BuildLogResizeHandle()
        {
            var handle = new Border
            {
                Height              = 12,
                Cursor              = Cursors.SizeNS,
                Background          = Brushes.Transparent,
                VerticalAlignment   = VerticalAlignment.Top,
            };

            // ── Grip indicator: three short horizontal bars ───────────────────
            var grip = new StackPanel
            {
                Orientation         = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            for (int i = 0; i < 3; i++)
            {
                var bar = new Rectangle { Width = 24, Height = 2, RadiusX = 1, RadiusY = 1,
                    Margin = new Thickness(0, i > 0 ? 2 : 0, 0, 0) };
                bar.SetResourceReference(Rectangle.FillProperty, "LemoineBorderMid");
                grip.Children.Add(bar);
            }
            handle.Child = grip;

            handle.MouseEnter += (s, e) =>
                handle.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            handle.MouseLeave += (s, e) =>
            {
                if (!_isResizingLog) handle.Background = Brushes.Transparent;
            };

            handle.MouseLeftButtonDown += (s, e) =>
            {
                _isResizingLog = true;
                _logDragStartY = e.GetPosition(this).Y;
                _logDragStartH = _logScroll.ActualHeight > 0
                    ? _logScroll.ActualHeight : _logScroll.Height;
                handle.CaptureMouse();
                e.Handled = true;
            };
            handle.MouseMove += (s, e) =>
            {
                if (!_isResizingLog) return;
                double delta = e.GetPosition(this).Y - _logDragStartY;
                _logScroll.Height = Math.Max(_logMinH, Math.Min(_logMaxH, _logDragStartH + delta));
            };
            handle.MouseLeftButtonUp += (s, e) =>
            {
                _isResizingLog = false;
                handle.Background = Brushes.Transparent;
                handle.ReleaseMouseCapture();
                e.Handled = true;
            };

            return handle;
        }

        private Border BuildTabButton(string id, string label)
        {
            bool isActive = id == _activeLogTab;

            var btn = new Border
            {
                Padding = new Thickness(12, 0, 12, 0),
                Cursor  = Cursors.Hand,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Tag = id,
            };
            if (isActive) btn.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            else          btn.BorderBrush = Brushes.Transparent;
            btn.Background = Brushes.Transparent;
            btn.SetResourceReference(Border.HeightProperty,     "LemoineH_BtnMin");

            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.ForegroundProperty,
                isActive ? "LemoineText" : "LemoineTextDim");
            btn.Child = lbl;

            btn.MouseLeftButtonDown += (s, e) => ActivateLogTab(id);
            btn.MouseEnter += (s, e) =>
            {
                if ((string)btn.Tag != _activeLogTab)
                    lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            };
            btn.MouseLeave += (s, e) =>
            {
                if ((string)btn.Tag != _activeLogTab)
                    lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            };
            return btn;
        }

        private void ActivateLogTab(string id)
        {
            _activeLogTab = id;

            // Update button styles
            foreach (Border btn in _tabButtonBar.Children)
            {
                bool active = (string)btn.Tag == id;
                if (active) btn.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                else        btn.BorderBrush = Brushes.Transparent;
                if (btn.Child is TextBlock lbl)
                    lbl.SetResourceReference(TextBlock.ForegroundProperty,
                        active ? "LemoineText" : "LemoineTextDim");
            }

            // Show matching content
            var tab = _logTabs.Find(t => t.id == id);
            _tabContentHost.Child = tab != default ? tab.content : null;
        }

        // ─────────────────── Footer ────────────────────────────────────────────
        private void BuildFooter()
        {
            _footerBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            _footerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _footerBorder.SetResourceReference(Border.PaddingProperty, "LemoineTh_FooterPad");

            var dp = new DockPanel { VerticalAlignment = VerticalAlignment.Center };

            _resetBtn = BuildButton("Reset", false);
            _resetBtn.Width = 90;
            // While a run is in flight the button is a Cancel control; otherwise it resets.
            _resetBtn.Click += (s, e) => { if (_isRunning) RequestCancelRun(); else ResetAll(); };
            DockPanel.SetDock(_resetBtn, Dock.Left);
            dp.Children.Add(_resetBtn);

            _closeBtn = BuildButton("Close", true);
            _closeBtn.Width = 100;
            _closeBtn.IsEnabled = false;
            _closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
            _closeBtn.Click += (s, e) => Close();
            DockPanel.SetDock(_closeBtn, Dock.Right);
            dp.Children.Add(_closeBtn);

            _footerBorder.Child = dp;
        }

        // ═══════════════════════════════════════ NAVIGATION ═══════════════════
        private void ActivateStep(int index)
        {
            _activeStep = index;
            RefreshStepVisibility();
            var steps = _tool.Steps;
            int visibleOrdinal = 0;
            for (int i = 0; i < steps.Length; i++)
            {
                // Number visible steps 1..N so hidden conditional steps don't leave gaps.
                if (IsStepVisible(i)) visibleOrdinal++;
                bool isActive = i == index, isDone = i < index, isFuture = i > index;
                _stepRows[i].Background = Brushes.Transparent;
                if (isDone)        _accentBars[i].SetResourceReference(Border.BackgroundProperty, "LemoineGreen");
                else if (isActive) _accentBars[i].SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
                else               _accentBars[i].Background = Brushes.Transparent;
                if (isDone)        _stepCircles[i].SetResourceReference(Border.BorderBrushProperty, "LemoineGreen");
                else if (isActive) _stepCircles[i].SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                else               _stepCircles[i].SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                if (isDone)        _stepCircles[i].SetResourceReference(Border.BackgroundProperty, "LemoineGreen");
                else if (isActive) _stepCircles[i].SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
                else               _stepCircles[i].Background = Brushes.Transparent;
                _circleTexts[i].Text = isDone ? "✓" : visibleOrdinal.ToString();
                if (isDone || isActive)
                    _circleTexts[i].SetResourceReference(TextBlock.ForegroundProperty, "LemoineKnobOn");
                else
                    _circleTexts[i].SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                _stepTitles[i].FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
                _stepTitles[i].SetResourceReference(TextBlock.ForegroundProperty, isActive ? "LemoineAccent" : "LemoineText");
                _summaryTexts[i].Visibility = isDone ? Visibility.Visible : Visibility.Collapsed;
                if (isDone) _summaryTexts[i].Text = _tool.SummaryFor(steps[i].Id);
                _waitingTexts[i].Visibility = isFuture ? Visibility.Visible : Visibility.Collapsed;
                _runningTexts[i].Visibility = Visibility.Collapsed;
                _pipRects[i].SetResourceReference(Rectangle.FillProperty, (_isDone || isDone) ? "LemoineGreen" : isActive ? "LemoineAccent" : "LemoineBorder");
                AnimateContent(_contentBorders[i], isActive);
            }
            RefreshStepState(index);
            UpdateStepCounter();
            // Refresh the framework review when the (last) review step opens so it reflects
            // the latest input values.
            if (index == _tool.Steps.Length - 1) PopulateReview();
            (_tool as IStepAware)?.OnStepActivated(_tool.Steps[index].Id);
        }

        private void RefreshStepState(int index)
        {
            if (index < 0 || index >= _tool.Steps.Length) return;
            var s = _tool.Steps[index]; bool valid = _tool.IsValid(s.Id);
            _validationTexts[index].Visibility = s.Required && !valid ? Visibility.Visible : Visibility.Collapsed;
            _confirmBtns[index].IsEnabled = !(s.Required && !valid);
        }

        private void ConfirmStep(int i)
        {
            int next = NextVisible(i);
            if (next >= 0) ActivateStep(next); else StartRun();
        }

        // ── Conditional-step support (ILemoineConditionalSteps) ────────────────
        // Tools that don't implement the interface have every step visible, so these
        // helpers reduce to the original linear behaviour.

        private bool IsStepVisible(int i)
        {
            if (i < 0 || i >= _tool.Steps.Length) return false;
            return !(_tool is ILemoineConditionalSteps cs) || cs.IsStepVisible(_tool.Steps[i].Id);
        }

        /// <summary>First visible step index strictly after <paramref name="from"/>, or -1 if none.</summary>
        private int NextVisible(int from)
        {
            for (int i = from + 1; i < _tool.Steps.Length; i++)
                if (IsStepVisible(i)) return i;
            return -1;
        }

        /// <summary>Last visible step index strictly before <paramref name="from"/>, or -1 if none.</summary>
        private int PrevVisible(int from)
        {
            for (int i = from - 1; i >= 0; i--)
                if (IsStepVisible(i)) return i;
            return -1;
        }

        // Collapses the rows + pips of hidden steps. No-op for non-conditional tools.
        private void RefreshStepVisibility()
        {
            if (!(_tool is ILemoineConditionalSteps) || _stepRows == null) return;
            for (int i = 0; i < _tool.Steps.Length; i++)
            {
                var vis = IsStepVisible(i) ? Visibility.Visible : Visibility.Collapsed;
                if (_stepRows[i] != null) _stepRows[i].Visibility = vis;
                if (_pipRects != null && _pipRects[i] != null) _pipRects[i].Visibility = vis;
            }
            UpdateStepCounter();
        }

        private void AnimateContent(Border b, bool expand)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            if (expand)
            {
                // Measure the content's natural height so the step isn't capped at a
                // fixed ceiling. MaxHeight animates 0 → measured height, then is released
                // to unbounded on completion so tall content is never clipped and can
                // reflow freely afterwards (validation text, content refresh, etc.).
                double target = MeasureContentHeight(b);

                var a = new DoubleAnimation
                {
                    To             = target,
                    Duration       = TimeSpan.FromMilliseconds(LemoineSettings.Instance.AnimExpand),
                    EasingFunction = ease,
                };
                a.Completed += (s, e) =>
                {
                    b.BeginAnimation(FrameworkElement.MaxHeightProperty, null); // drop the animation hold
                    b.MaxHeight = double.PositiveInfinity;                      // no ceiling once open
                };
                b.BeginAnimation(FrameworkElement.MaxHeightProperty, a);
                LemoineMotion.FadeSlideIn(b);   // content fades up as the step opens
            }
            else
            {
                // Collapsing: MaxHeight may currently be PositiveInfinity (released on a
                // prior expand), so pin a concrete From before animating down to 0.
                var a = new DoubleAnimation
                {
                    From           = b.ActualHeight,
                    To             = 0,
                    Duration       = TimeSpan.FromMilliseconds(LemoineSettings.Instance.AnimMed),
                    EasingFunction = ease,
                };
                b.BeginAnimation(FrameworkElement.MaxHeightProperty, a);
            }
        }

        // Natural (uncapped) height of a step's content border, used as the expand
        // animation target. Measures the inner child against the border's laid-out
        // width plus its vertical padding; falls back to a generous default if the
        // border hasn't been laid out yet (e.g. first activation during construction).
        private double MeasureContentHeight(Border b)
        {
            if (b.Child is FrameworkElement child)
            {
                double width = b.ActualWidth > 0 ? b.ActualWidth : 1000;
                child.Measure(new Size(width, double.PositiveInfinity));
                double h = child.DesiredSize.Height + b.Padding.Top + b.Padding.Bottom;
                if (h > 0) return h;
            }
            return 1200;
        }

        // ═══════════════════════════════════════ RUN / RESET ══════════════════
        private void StartRun()
        {
            _isRunning = true; SetStatus("● Running…"); SetCounts(0, 0, 0);
            _runningTexts[_tool.Steps.Length - 1].Visibility = Visibility.Visible;
            foreach (var b in _confirmBtns) if (b != null) b.IsEnabled = false;
            foreach (var b in _backBtns)    if (b != null) b.IsEnabled = false;
            // Keep the footer button live so the run can be abandoned; flip it to Cancel.
            LemoineRun.Begin();
            SetResetButtonToCancel();
            // True-hide every step except the last (which hosts the run controls + output
            // log), then extend the log to its max height so the run output fills the area.
            HideStepsForRun(true);
            _logScroll.Height = _logMaxH;
            _logStack.Children.Clear(); PushLog("Starting…", "info");
            // SafeBeginInvoke (non-blocking, shutdown-guarded) — lets Execute() keep running while
            // the window's dedicated STA thread processes UI updates in real time, and no-ops if
            // the window has been closed mid-run (the handler is static and keeps calling these
            // after the dispatcher is gone).
            Action<string, string> pushLog = (t, s) => { var w = _sink.Win; if (w != null) w.SafeBeginInvoke(() => w.PushLog(t, s)); };
            // Route Revit's own failures/dialogs (caught process-wide by LemoineFailureCapture)
            // into this run's Output log for the duration of the run.
            LemoineFailureCapture.BeginRun();
            LemoineRunLog.Set(pushLog);
            _tool.Run(
                pushLog:    pushLog,
                onProgress: (pct, p, f, s)  => { var w = _sink.Win; if (w != null) w.SafeBeginInvoke(() => { w.SetCounts(p, f, s); w.SetProgress(pct); }); },
                onComplete: (p, f, s)       => { var w = _sink.Win; if (w != null) w.SafeBeginInvoke(() => w.CompleteRun(p, f, s)); });
        }

        private void CompleteRun(int pass, int fail, int skip)
        {
            _isRunning = false; _isDone = true;
            bool wasCancelled = LemoineRun.CancelRequested;
            LemoineRun.End();
            LemoineRunLog.Clear();
            SetResetButtonToReset();
            SetStatus(wasCancelled ? "● Stopped" : "● Done"); SetCounts(pass, fail, skip); SetProgress(100);
            string finishKey = wasCancelled ? "LemoineWarnText" : "LemoineGreen";
            _statusText.SetResourceReference(TextBlock.ForegroundProperty, finishKey);
            foreach (var p in _pipRects) p.SetResourceReference(Rectangle.FillProperty, finishKey);
            _progressFill.SetResourceReference(Rectangle.FillProperty, finishKey);
            _closeBtn.Content = wasCancelled ? "Close" : "Close ✓"; _closeBtn.IsEnabled = true;
            _closeBtn.SetResourceReference(Button.BackgroundProperty,  wasCancelled ? "LemoineWarnBg"     : "LemoineGreenDim");
            _closeBtn.SetResourceReference(Button.BorderBrushProperty, wasCancelled ? "LemoineWarnBorder" : "LemoineGreen");
            _closeBtn.SetResourceReference(Button.ForegroundProperty,  finishKey);
            _runningTexts[_tool.Steps.Length - 1].Visibility = Visibility.Collapsed;
            // Prominent, self-describing summary in the output log (the top bar keeps the
            // generic pass/fail/skip): "42 segments" plus any per-output chip breakdown.
            PushRunSummary(pass, fail, skip);
            UpdateStepCounter();
        }

        // Appends a large-text result summary to the output log. Every tool gets a headline
        // line built from its ILemoineRunResult.ResultNoun ("42 segments · 2 skipped · 0 failed");
        // multi-output tools add a second line of labelled chip figures ("120 markers · 118 dims").
        private void PushRunSummary(int pass, int fail, int skip)
        {
            var rr   = _tool as ILemoineRunResult;
            var noun = rr?.ResultNoun;
            if (string.IsNullOrWhiteSpace(noun)) noun = "done";

            var block = new StackPanel { Margin = new Thickness(0, 10, 0, 4) };

            // Headline: big coloured primary count + noun, then skipped / failed.
            var headline = new TextBlock { TextWrapping = TextWrapping.Wrap };
            headline.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            headline.Inlines.Add(SummaryRun(pass.ToString(),  "LemoineGreen",   "LemoineFS_XL", true));
            headline.Inlines.Add(SummaryRun($" {noun}",        "LemoineText",    "LemoineFS_XL", true));
            headline.Inlines.Add(SummaryRun($"    {skip} skipped", "LemoineTextDim", "LemoineFS_LG", false));
            headline.Inlines.Add(SummaryRun($"    {fail} failed",
                fail > 0 ? "LemoineRed" : "LemoineTextDim", "LemoineFS_LG", false));
            block.Children.Add(headline);

            // Optional per-output breakdown.
            var chips = rr?.ResultChips;
            if (chips != null && chips.Count > 0)
            {
                var chipLine = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                chipLine.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                bool first = true;
                foreach (var chip in chips)
                {
                    if (!first) chipLine.Inlines.Add(SummaryRun("  ·  ", "LemoineTextDim", "LemoineFS_LG", false));
                    first = false;
                    var colorKey = string.IsNullOrWhiteSpace(chip.ColorKey) ? "LemoineText" : chip.ColorKey;
                    chipLine.Inlines.Add(SummaryRun(chip.Count.ToString(), colorKey, "LemoineFS_LG", true));
                    chipLine.Inlines.Add(SummaryRun($" {chip.Label}", "LemoineTextDim", "LemoineFS_LG", false));
                }
                block.Children.Add(chipLine);
            }

            _logStack.Children.Add(block);
            _logScroll.ScrollToEnd();
        }

        private static System.Windows.Documents.Run SummaryRun(string text, string colorKey, string fsKey, bool bold)
        {
            var run = new System.Windows.Documents.Run(text) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, colorKey);
            run.SetResourceReference(System.Windows.Documents.TextElement.FontSizeProperty, fsKey);
            return run;
        }

        private void ResetAll()
        {
            _isRunning = false; _isDone = false;
            LemoineRun.End();
            LemoineRunLog.Clear();
            SetResetButtonToReset();
            _closeBtn.Content = "Close"; _closeBtn.IsEnabled = false;
            _closeBtn.SetResourceReference(Button.BackgroundProperty,  "LemoineAccentDim");
            _closeBtn.SetResourceReference(Button.BorderBrushProperty, "LemoineAccent");
            _closeBtn.SetResourceReference(Button.ForegroundProperty,  "LemoineAccent");
            foreach (var b in _confirmBtns) if (b != null) b.IsEnabled = true;
            foreach (var b in _backBtns)    if (b != null) b.IsEnabled = true;
            // Restore the step rows hidden for the run and return the log to its default
            // height. ActivateStep(0) → RefreshStepVisibility re-collapses conditional steps.
            HideStepsForRun(false);
            _logScroll.SetResourceReference(ScrollViewer.HeightProperty, "LemoineH_LogArea");
            _logStack.Children.Clear(); PushLog("Ready.", "info");
            SetStatus("● Configuring…"); SetCounts(0, 0, 0); SetProgress(0);
            _progressFill.SetResourceReference(Rectangle.FillProperty, "LemoineAccent");
            _statusText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            ActivateStep(0);
        }

        // During a run, true-hide every step row except the last (which hosts the run
        // controls + output log) so the log can expand to fill the freed space. Passing
        // false restores all rows to visible; conditional-step re-hiding is then handled
        // by the subsequent RefreshStepVisibility call.
        private void HideStepsForRun(bool hide)
        {
            if (_stepRows == null) return;
            int last = _tool.Steps.Length - 1;
            for (int i = 0; i < _tool.Steps.Length; i++)
            {
                if (i == last || _stepRows[i] == null) continue;
                _stepRows[i].Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        // ═══════════════════════════════════════ LOG ══════════════════════════
        public void PushLog(string text, string status = "info")
        {
            var now    = DateTime.Now;
            var row    = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 1) };
            var colKey = status == "pass" ? "LemoineGreen"
                       : status == "fail" ? "LemoineRed"
                       : status == "warn" ? "LemoineWarnText"
                       : "LemoineTextDim";

            var timeTb = new TextBlock { Text = $"{now:HH:mm:ss}.{now.Millisecond:000}", Margin = new Thickness(0, 0, 8, 0), MinWidth = 80 };
            timeTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            timeTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            timeTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var iconTb = new TextBlock { Text = status == "pass" ? "✓" : status == "fail" ? "✗" : status == "warn" ? "■" : "·", Margin = new Thickness(0, 0, 6, 0) };
            iconTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            iconTb.SetResourceReference(TextBlock.ForegroundProperty, colKey);
            iconTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var msgTb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            msgTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            msgTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            msgTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            row.Children.Add(timeTb); row.Children.Add(iconTb); row.Children.Add(msgTb);
            _logStack.Children.Add(row);
            _logScroll.ScrollToEnd();
        }

        // ═══════════════════════════════════════ HELPERS ══════════════════════
        private void SetProgress(int pct)
        {
            _progressPct = pct;
            _progressTrack.UpdateLayout();
            double w = _progressTrack.ActualWidth > 0 ? _progressTrack.ActualWidth : 200;
            var a = new DoubleAnimation { To = w * pct / 100.0, Duration = TimeSpan.FromMilliseconds(pct == 0 ? 0 : LemoineSettings.Instance.AnimProgress) };
            _progressFill.BeginAnimation(FrameworkElement.WidthProperty, a);
        }

        private void ResizeProgressFill()
        {
            double w = _progressTrack.ActualWidth;
            if (w <= 0) return;
            _progressFill.BeginAnimation(FrameworkElement.WidthProperty, null); // release animation hold
            _progressFill.Width = w * _progressPct / 100.0;
        }

        private void SetStatus(string t) => _statusText.Text = t;
        private void SetCounts(int p, int f, int s) { _passText.Text = p.ToString(); _failText.Text = f.ToString(); _skipText.Text = s.ToString(); }
        private void UpdateStepCounter()
        {
            if (_stepCounter == null) return;
            if (_isDone)    { _stepCounter.Text = "Complete"; return; }
            if (_isRunning) { _stepCounter.Text = "Running…"; return; }
            int total = 0, pos = 0;
            for (int i = 0; i < _tool.Steps.Length; i++)
            {
                if (!IsStepVisible(i)) continue;
                total++;
                if (i <= _activeStep) pos++;
            }
            _stepCounter.Text = $"Step {pos} / {total}";
        }

        private TextBlock SectionLabel(string t)
        {
            var tb = new TextBlock { Text = t.ToUpper(), Margin = new Thickness(0, 0, 0, 8) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }

        private TextBlock SectionSub(string t)
        {
            var tb = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private Rectangle VSep()
        {
            var r = new Rectangle { Width = 1, Margin = new Thickness(6, 0, 6, 0) };
            r.SetResourceReference(Rectangle.HeightProperty, "LemoineH_Icon_SM");
            r.SetResourceReference(Rectangle.FillProperty,   "LemoineBorder");
            return r;
        }

        private Rectangle HSep(double margin = 0)
        {
            var r = new Rectangle { Height = 1, Margin = new Thickness(0, margin, 0, margin) };
            r.SetResourceReference(Rectangle.FillProperty, "LemoineBorder");
            return r;
        }

        private TextBlock Ctr(string colorKey)
        {
            var tb = new TextBlock { Text = "0" };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }

        private TextBlock CtrLbl(string t)
        {
            var tb = new TextBlock { Text = t };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }

        // MakeGear removed — icon glyphs now go through LemoineTitleBar.IconGlyph.
        // GlobalSettingsWindow has its own gear glyph via Segoe UI Symbol.

        // Updated signature — pass a resource key string, not a float size
        private TextBlock Clickable(string text, string fontSizeKey, string normalKey, string hoverKey, Action onClick)
        {
            var tb = new TextBlock { Text = text, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   fontSizeKey);
            tb.SetResourceReference(TextBlock.ForegroundProperty, normalKey);
            tb.MouseLeftButtonDown += (s, e) => onClick();
            tb.MouseEnter += (s, e) => tb.SetResourceReference(TextBlock.ForegroundProperty, hoverKey);
            tb.MouseLeave += (s, e) => tb.SetResourceReference(TextBlock.ForegroundProperty, normalKey);
            return tb;
        }

        // BuildButton is the primary factory used by steps
        // ═══════════════════════════════════════ CANCEL ══════════════════════
        // User clicked the footer button while a run is in flight. Request the stop;
        // the running handler halts at its next cancel check (LemoineRun.CancelRequested),
        // commits the work done so far, and calls onComplete → CompleteRun finalises.
        private void RequestCancelRun()
        {
            if (!_isRunning || LemoineRun.CancelRequested) return;
            LemoineRun.RequestCancel();
            _resetBtn.IsEnabled = false;
            _resetBtn.Content   = "Cancelling…";
            PushLog("Cancelling — will stop at the next checkpoint…", "warn");
        }

        // Footer button → red "Cancel" affordance for the duration of a run.
        private void SetResetButtonToCancel()
        {
            _resetBtn.IsEnabled = true;
            _resetBtn.Content   = "Cancel";
            _resetBtn.SetResourceReference(Button.BorderBrushProperty, "LemoineWarnBorder");
            _resetBtn.SetResourceReference(Button.ForegroundProperty,  "LemoineWarnText");
        }

        // Footer button → default "Reset" affordance (matches BuildButton's non-accent look).
        private void SetResetButtonToReset()
        {
            _resetBtn.IsEnabled = true;
            _resetBtn.Content   = "Reset";
            _resetBtn.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
            _resetBtn.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
        }

        private Button BuildButton(string label, bool accent)
        {
            var btn = new Button
            {
                Content = label, BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand, Template = FlatBtnTemplate(),
            };
            btn.SetResourceReference(Button.MinHeightProperty,  "LemoineH_BtnMin");
            btn.SetResourceReference(Button.PaddingProperty,    "LemoineTh_BtnPad");
            btn.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_MD");
            btn.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            if (accent)
            {
                btn.SetResourceReference(Button.BackgroundProperty,  "LemoineAccentDim");
                btn.SetResourceReference(Button.BorderBrushProperty, "LemoineAccent");
                btn.SetResourceReference(Button.ForegroundProperty,  "LemoineAccent");
            }
            else
            {
                // Use Brushes.Transparent directly — a null/unresolved background
                // disables hit testing on the empty areas of the button.
                btn.Background = Brushes.Transparent;
                btn.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
                btn.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
            }
            LemoineMotion.WirePress(btn);   // snappy tap-down feedback
            return btn;
        }

        private static ControlTemplate FlatBtnTemplate()
            => LemoineControlStyles.BuildFlatButtonTemplate();
    }
}
