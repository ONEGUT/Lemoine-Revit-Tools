using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Lemoine
{
    public partial class StepFlowWindow : Window
    {
        private readonly ILemoineTool _tool;

        // Theme is now owned by LemoineSettings singleton
        private LemoineTheme ActiveTheme => LemoineSettings.Instance.ActiveTheme;

        private int  _activeStep = 0;
        private bool _isRunning  = false;
        private bool _isDone     = false;

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
            Title = tool.Title;

            LemoineSettings.Instance.ApplyTo(Resources);
            Background = ActiveTheme.PageBg;

            LemoineSettings.Instance.ThemeChanged  += OnThemeChanged;
            LemoineSettings.Instance.UiSizeChanged += OnUiSizeChanged;
            Closed += (s, e) =>
            {
                LemoineSettings.Instance.ThemeChanged  -= OnThemeChanged;
                LemoineSettings.Instance.UiSizeChanged -= OnUiSizeChanged;
            };

            BuildChrome();
            _tool.ValidationChanged += (s, e) => RefreshStepState(_activeStep);
        }

        private void OnThemeChanged(LemoineTheme t)
        {
            Dispatcher.Invoke(() =>
            {
                LemoineSettings.Instance.ApplyTo(Resources);
                Background = t.PageBg;
                ApplyContainerStyles();
            });
        }

        private void OnUiSizeChanged(LemoineUiSize _)
        {
            Dispatcher.Invoke(() =>
            {
                LemoineSettings.Instance.ApplyScaleTo(Resources);
                LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 5);
                UpdateRowHeights();
                // Re-measure progress fill width since track may have changed
                _progressTrack?.UpdateLayout();
                ResizeProgressFill();
            });
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
                _stepRows[i] = BuildStepRow(i, steps[i], _tool.GetStepContent(steps[i].Id) ?? new Grid());
                _stepStack.Children.Add(_stepRows[i]);
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

            var captIdx    = idx;
            var confirmBtn = BuildButton(idx == _tool.Steps.Length - 1 ? _tool.RunLabel : "Confirm →", true);
            confirmBtn.Click += (s, e) => ConfirmStep(captIdx);
            _confirmBtns[idx] = confirmBtn;
            Grid.SetColumn(confirmBtn, 2);
            btnGrid.Children.Add(confirmBtn);

            if (idx > 0)
            {
                var backBtn = BuildButton("← Back", false);
                backBtn.Width = 90;
                backBtn.Click += (s, e) => ActivateStep(captIdx - 1);
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
            _logScroll.Height  = Math.Round(90 * LemoineSettings.Instance.Scale);
            _logScroll.Content = _logStack;
            LemoineControlStyles.WireBubblingScroll(_logScroll);

            // Handle is placed at the top of logBrd so it appears inside the log box
            var logDp = new DockPanel { LastChildFill = true };
            var handle = BuildLogResizeHandle();
            DockPanel.SetDock(handle, Dock.Top);
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
            btn.SetResourceReference(Border.BorderBrushProperty,
                isActive ? "LemoineAccent" : "Transparent");
            btn.SetResourceReference(Border.BackgroundProperty, "Transparent");
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
                    lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
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
                btn.SetResourceReference(Border.BorderBrushProperty,
                    active ? "LemoineAccent" : "Transparent");
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
            _resetBtn.Click += (s, e) => ResetAll();
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
            var steps = _tool.Steps;
            for (int i = 0; i < steps.Length; i++)
            {
                bool isActive = i == index, isDone = i < index, isFuture = i > index;
                _stepRows[i].Background = Brushes.Transparent;
                _accentBars[i].SetResourceReference(Border.BackgroundProperty, isDone ? "LemoineGreen" : isActive ? "LemoineAccent" : "Transparent");
                _stepCircles[i].SetResourceReference(Border.BorderBrushProperty, isDone ? "LemoineGreen" : isActive ? "LemoineAccent" : "LemoineBorder");
                _stepCircles[i].SetResourceReference(Border.BackgroundProperty,  isDone ? "LemoineGreen" : isActive ? "LemoineAccent" : "Transparent");
                _circleTexts[i].Text = isDone ? "✓" : (i + 1).ToString();
                if (isDone || isActive)
                    _circleTexts[i].Foreground = Brushes.White;
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
        }

        private void RefreshStepState(int index)
        {
            if (index < 0 || index >= _tool.Steps.Length) return;
            var s = _tool.Steps[index]; bool valid = _tool.IsValid(s.Id);
            _validationTexts[index].Visibility = s.Required && !valid ? Visibility.Visible : Visibility.Collapsed;
            _confirmBtns[index].IsEnabled = !(s.Required && !valid);
        }

        private void ConfirmStep(int i) { if (i < _tool.Steps.Length - 1) ActivateStep(i + 1); else StartRun(); }

        private void AnimateContent(Border b, bool expand)
        {
            var a = new DoubleAnimation { To = expand ? 1200 : 0, Duration = TimeSpan.FromMilliseconds(expand ? LemoineSettings.Instance.AnimExpand : LemoineSettings.Instance.AnimMed), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            b.BeginAnimation(FrameworkElement.MaxHeightProperty, a);
        }

        // ═══════════════════════════════════════ RUN / RESET ══════════════════
        private void StartRun()
        {
            _isRunning = true; SetStatus("● Running…"); SetCounts(0, 0, 0);
            _runningTexts[_tool.Steps.Length - 1].Visibility = Visibility.Visible;
            foreach (var b in _confirmBtns) if (b != null) b.IsEnabled = false;
            foreach (var b in _backBtns)    if (b != null) b.IsEnabled = false;
            _resetBtn.IsEnabled = false;
            _logStack.Children.Clear(); PushLog("Starting…", "info");
            _tool.Run(
                // BeginInvoke (non-blocking) — lets Execute() keep running while
                // the window's dedicated STA thread processes UI updates in real time.
                pushLog:    (t, s) => Dispatcher.BeginInvoke(
                                (Action)(() => PushLog(t, s))),
                onProgress: (pct, p, f, s) => Dispatcher.BeginInvoke(
                                (Action)(() => { SetCounts(p, f, s); SetProgress(pct); })),
                onComplete: (p, f, s)       => Dispatcher.BeginInvoke(
                                (Action)(() => CompleteRun(p, f, s))));
        }

        private void CompleteRun(int pass, int fail, int skip)
        {
            _isRunning = false; _isDone = true;
            SetStatus("● Done"); SetCounts(pass, fail, skip); SetProgress(100);
            _statusText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineGreen");
            foreach (var p in _pipRects) p.SetResourceReference(Rectangle.FillProperty, "LemoineGreen");
            _progressFill.SetResourceReference(Rectangle.FillProperty, "LemoineGreen");
            _closeBtn.Content = "Close ✓"; _closeBtn.IsEnabled = true;
            _closeBtn.SetResourceReference(Button.BackgroundProperty,  "LemoineGreenDim");
            _closeBtn.SetResourceReference(Button.BorderBrushProperty, "LemoineGreen");
            _closeBtn.SetResourceReference(Button.ForegroundProperty,  "LemoineGreen");
            _resetBtn.IsEnabled = true;
            _runningTexts[_tool.Steps.Length - 1].Visibility = Visibility.Collapsed;
            UpdateStepCounter();
        }

        private void ResetAll()
        {
            _isRunning = false; _isDone = false;
            _closeBtn.Content = "Close"; _closeBtn.IsEnabled = false;
            _closeBtn.SetResourceReference(Button.BackgroundProperty,  "LemoineAccentDim");
            _closeBtn.SetResourceReference(Button.BorderBrushProperty, "LemoineAccent");
            _closeBtn.SetResourceReference(Button.ForegroundProperty,  "LemoineAccent");
            foreach (var b in _confirmBtns) if (b != null) b.IsEnabled = true;
            foreach (var b in _backBtns)    if (b != null) b.IsEnabled = true;
            _resetBtn.IsEnabled = true;
            _logStack.Children.Clear(); PushLog("Ready.", "info");
            SetStatus("● Configuring…"); SetCounts(0, 0, 0); SetProgress(0);
            _progressFill.SetResourceReference(Rectangle.FillProperty, "LemoineAccent");
            _statusText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            ActivateStep(0);
        }

        // ═══════════════════════════════════════ LOG ══════════════════════════
        public void PushLog(string text, string status = "info")
        {
            var now    = DateTime.Now;
            var row    = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 1) };
            var colKey = status == "pass" ? "LemoineGreen" : status == "fail" ? "LemoineRed" : "LemoineTextDim";

            var timeTb = new TextBlock { Text = $"{now:HH:mm:ss}.{now.Millisecond:000}", Margin = new Thickness(0, 0, 8, 0), MinWidth = 80 };
            timeTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            timeTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            timeTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var iconTb = new TextBlock { Text = status == "pass" ? "✓" : status == "fail" ? "✗" : "·", Margin = new Thickness(0, 0, 6, 0) };
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
        private void UpdateStepCounter() { if (_stepCounter == null) return; _stepCounter.Text = _isDone ? "Complete" : _isRunning ? "Running…" : $"Step {_activeStep + 1} / {_tool.Steps.Length}"; }

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
            return btn;
        }

        private static ControlTemplate FlatBtnTemplate()
            => LemoineControlStyles.BuildFlatButtonTemplate();
    }
}
