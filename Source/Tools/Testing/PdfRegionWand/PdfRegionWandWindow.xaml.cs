using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;
using LemoineTools.PdfGeometry.Engine;
using LemoineTools.PdfGeometry.Graph;
using LemoineTools.PdfGeometry.Pdf;
using LemoineTools.PdfGeometry.Plans;
using LemoineTools.PdfGeometry.Primitives;
using LemoineTools.PdfGeometry.Raster;
using WpfGrid = System.Windows.Controls.Grid;
using WpfImage = System.Windows.Controls.Image;
using WpfPoint = System.Windows.Point;
using WpfVisibility = System.Windows.Visibility;

namespace LemoineTools.Tools.Testing.PdfRegionWand
{
    /// <summary>A floor type or level choice handed in by the command.</summary>
    public sealed class NamedId
    {
        public string Name { get; }
        public ElementId Id { get; }
        public NamedId(string name, ElementId id) { Name = name; Id = id; }
    }

    /// <summary>
    /// The PDF Region Wand palette: a persistent modeless window. The user
    /// selects a placed PDF underlay, then repeatedly wands regions (pick →
    /// flood/loop trace → preview → confirm → one floor per transaction) and
    /// draws split lines with Revit's native line tool as flood barriers.
    /// All Revit API work goes through <see cref="PdfRegionWandEventHandler"/>;
    /// geometry work runs on a background task against the Revit-free engine.
    /// </summary>
    public partial class PdfRegionWandWindow : Window
    {
        private readonly PdfRegionWandEventHandler _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent _event;
        private readonly List<NamedId> _floorTypes;
        private readonly List<NamedId> _levels;
        private readonly IRegionOutputAdapter _adapter = new FloorAdapter();

        // ── Session state (UI thread unless noted) ──────────────────────────
        private ImageSelection? _selection;
        private PdfPageProbe? _probe;
        private PdfToModelTransform? _transform;
        private RegionWandEngine? _engine;
        private PageRaster? _raster;
        private WandResult? _pending;          // trace awaiting confirm/discard
        private bool _busy;
        private bool _drawingSplitLines;
        private readonly Queue<int> _createQueue = new Queue<int>();

        // ── UI elements built in code ───────────────────────────────────────
        private TextBlock? _sourceText;
        private LemoineFileBrowser? _fileBrowser;
        private LemoineInlineStepper? _pageStepper;
        private LemoineSingleSelect? _scaleSelect;
        private TextBlock? _modeText;
        private LemoineSingleSelect? _modeSelect;
        private Button? _pickPointBtn;
        private Button? _splitBtn;
        private LemoineSectionCard? _previewCard;
        private Canvas? _previewCanvas;
        private TextBlock? _previewText;
        private CheckBox? _createOnConfirmCheck;
        private StackPanel? _regionStack;
        private Button? _createAllBtn;
        private LemoineSingleSelect? _typeSelect;
        private LemoineSingleSelect? _levelSelect;
        private CheckBox? _structuralCheck;
        private StackPanel? _logStack;
        private ScrollViewer? _logScroll;
        private TextBlock? _statusText;

        private static readonly (string Label, double InchesPerFoot)[] _scales =
        {
            ("1/16\" = 1'-0\"", 1.0 / 16), ("3/32\" = 1'-0\"", 3.0 / 32),
            ("1/8\" = 1'-0\"", 1.0 / 8),   ("3/16\" = 1'-0\"", 3.0 / 16),
            ("1/4\" = 1'-0\"", 1.0 / 4),   ("3/8\" = 1'-0\"", 3.0 / 8),
            ("1/2\" = 1'-0\"", 1.0 / 2),   ("3/4\" = 1'-0\"", 3.0 / 4),
            ("1\" = 1'-0\"", 1.0),         ("1-1/2\" = 1'-0\"", 1.5),
        };

        public PdfRegionWandWindow(
            PdfRegionWandEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent,
            List<NamedId> floorTypes,
            List<NamedId> levels)
        {
            _handler = handler;
            _event = externalEvent;
            _floorTypes = floorTypes;
            _levels = levels;

            InitializeComponent();
            Loaded += OnLoaded;

            // Named handlers so they can be detached — a leaked subscription to a
            // closed window's dispatcher crashes Revit on the next theme change.
            LemoineSettings.Instance.ThemeChanged += OnThemeChanged;
            LemoineSettings.Instance.UiSizeChanged += OnUiSizeChanged;
            Closed += OnWindowClosed;

            // Last-resort net: an unhandled throw on this dedicated STA dispatcher
            // (e.g. in an async-void continuation) terminates Revit with no log
            // entry. Route it through LemoineLog and keep the palette alive.
            // Named handler, detached in OnWindowClosed (same as StepFlowWindow).
            Dispatcher.UnhandledException += OnDispatcherUnhandledException;

            WireHandlerCallbacks();
        }

        // ════════════════════════════════════════════════════════ lifecycle

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LemoineSettings.Instance.ApplyTo(Resources);
            LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 5);
            Background = LemoineSettings.Instance.ActiveTheme.PageBg;
            _root.SetResourceReference(WpfGrid.BackgroundProperty, "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _outerBorder.CornerRadius = new CornerRadius(8);
            UpdateRowHeights();

            // Deliberately NOT owned to Revit's HWND — per the project-wide decision
            // (see CLAUDE.md "Run Lifecycle"), tool windows stay independent of
            // Revit's z-order; failures surface in the palette log instead.

            BuildToolbar();
            BuildContent();
            BuildLog();
            BuildFooter();
            SetStatus(LemoineStrings.T("pdfRegionWand.status.selectToBegin"));
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            Dispatcher.UnhandledException -= OnDispatcherUnhandledException;
            LemoineSettings.Instance.ThemeChanged -= OnThemeChanged;
            LemoineSettings.Instance.UiSizeChanged -= OnUiSizeChanged;

            // Detach the DocumentChanged watchdog on Revit's thread, then sever
            // every handler→window delegate so the closed window can collect.
            try
            {
                _handler.Op = PdfWandOp.Detach;
                var request = _event.Raise();
                if (request != Autodesk.Revit.UI.ExternalEventRequest.Accepted)
                    LemoineLog.Warn("PdfRegionWand",
                        $"Detach raise returned {request} — DocumentChanged watchdog stays attached until Revit exits.");
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("PdfRegionWand: raise Detach on close", ex);
            }
            _handler.OnImageSelected = null;
            _handler.OnSeedPicked = null;
            _handler.OnSplitLinesStarted = null;
            _handler.OnSplitLinesCaptured = null;
            _handler.OnRegionCreated = null;
            _handler.OnTrackedElementsDeleted = null;
            _handler.PushLog = null;
        }

        private void OnDispatcherUnhandledException(
            object? sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LemoineLog.Error("PdfRegionWand palette unhandled UI exception", e.Exception);
            e.Handled = true;
        }

        private void OnThemeChanged(LemoineTheme t)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LemoineSettings.Instance.ApplyTo(Resources);
                Background = t.PageBg;
                _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
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
            }));
        }

        private void UpdateRowHeights()
        {
            _root.RowDefinitions[0].Height = new GridLength(LemoineSettings.Instance.ToolbarHeight);
            _root.RowDefinitions[3].Height = new GridLength(LemoineSettings.Instance.FooterHeight);
        }

        // ═══════════════════════════════════════════════════ handler wiring

        /// <summary>
        /// Handler callbacks fire on Revit's main thread — every one marshals
        /// onto this window's dispatcher with a non-blocking BeginInvoke and a
        /// shutdown guard.
        /// </summary>
        private void WireHandlerCallbacks()
        {
            _handler.PushLog = (msg, status) => OnUi(() => AddLog(msg, status));
            _handler.OnImageSelected = sel => OnUi(() => HandleImageSelected(sel));
            _handler.OnSeedPicked = p => OnUi(() => HandleSeedPicked(p));
            _handler.OnSplitLinesStarted = ok => OnUi(() => HandleSplitLinesStarted(ok));
            _handler.OnSplitLinesCaptured = lines => OnUi(() => HandleSplitLinesCaptured(lines));
            _handler.OnRegionCreated = (faceId, idValue) => OnUi(() => HandleRegionCreated(faceId, idValue));
            _handler.OnTrackedElementsDeleted = ids => OnUi(() => HandleTrackedDeleted(ids));
        }

        private void OnUi(Action action)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(action);
        }

        private void RaiseOp(PdfWandOp op)
        {
            _handler.Op = op;
            var request = _event.Raise();
            if (request != Autodesk.Revit.UI.ExternalEventRequest.Accepted)
            {
                // Denied/Pending means the callback will never fire — surface it
                // and release the busy latch instead of hanging the palette.
                AddLog(LemoineStrings.T("pdfRegionWand.log.raiseRejected", op, request), "fail");
                LemoineLog.Warn("PdfRegionWand", $"ExternalEvent.Raise({op}) returned {request}");
                SetBusy(false);
            }
        }

        // ════════════════════════════════════════════════════════ UI build

        private void BuildToolbar()
        {
            _toolbarBorder.BorderThickness = new Thickness(0);
            var closeBtn = LemoineControlStyles.BuildButton("×");
            closeBtn.Margin = new Thickness(8, 0, 0, 0);
            closeBtn.SetResourceReference(ForegroundProperty, "LemoineTextDim");
            closeBtn.Click += (s, e) => Close();

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(closeBtn);

            _toolbarBorder.Child = new LemoineTitleBar
            {
                Title = LemoineStrings.T("pdfRegionWand.window.title"),
                IconGlyph = char.ConvertFromUtf32(0xE790), // Segoe MDL2: color/wand
                RightContent = right,
            };
        }

        private void BuildContent()
        {
            _contentStack.Margin = new Thickness(10, 8, 10, 8);
            _contentStack.Children.Add(BuildSourceCard());
            _contentStack.Children.Add(BuildActionsCard());
            _contentStack.Children.Add(BuildPreviewCard());
            _contentStack.Children.Add(BuildRegionsCard());
            _contentStack.Children.Add(BuildOutputCard());
            _contentStack.Children.Add(BuildTuningCard());
        }

        private UIElement BuildSourceCard()
        {
            var stack = new StackPanel();

            var selectBtn = LemoineControlStyles.BuildButton(LemoineStrings.T("pdfRegionWand.source.selectButton"), LemoineControlStyles.LemoineButtonVariant.Primary);
            selectBtn.Click += (s, e) => StartSelectImage();
            stack.Children.Add(selectBtn);

            _sourceText = BodyText(LemoineStrings.T("pdfRegionWand.source.noneSelected"));
            _sourceText.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(_sourceText);

            _fileBrowser = new LemoineFileBrowser
            {
                Label = LemoineStrings.T("pdfRegionWand.source.fileLabel"),
                Filter = "PDF files|*.pdf",
                DialogTitle = LemoineStrings.T("pdfRegionWand.source.dialogTitle"),
                Placeholder = LemoineStrings.T("pdfRegionWand.source.placeholder"),
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = WpfVisibility.Collapsed,
                AccessibleName = LemoineStrings.T("pdfRegionWand.source.pathAccessible"),
            };
            _fileBrowser.PathChanged += path =>
            {
                if (_selection == null) return;
                _selection.PdfPath = path;
                PrepareSources(resetSession: true);
            };
            stack.Children.Add(_fileBrowser);

            var row = new WpfGrid { Margin = new Thickness(0, 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var pageLabel = BodyText(LemoineStrings.T("pdfRegionWand.source.pageLabel"));
            pageLabel.VerticalAlignment = VerticalAlignment.Center;
            pageLabel.Margin = new Thickness(0, 0, 8, 0);
            WpfGrid.SetColumn(pageLabel, 0);
            row.Children.Add(pageLabel);

            _pageStepper = new LemoineInlineStepper
            {
                MinValue = 1, MaxValue = 1, Value = 1, Step = 1, Decimals = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            _pageStepper.ValueChanged += (s, v) =>
            {
                if (_selection == null) return;
                _selection.PageNumber = (int)v;
                AddLog(LemoineStrings.T("pdfRegionWand.log.pageSet", (int)v), "info");
                PrepareSources(resetSession: true);
            };
            WpfGrid.SetColumn(_pageStepper, 1);
            row.Children.Add(_pageStepper);
            stack.Children.Add(row);

            _scaleSelect = new LemoineSingleSelect
            {
                Label = LemoineStrings.T("pdfRegionWand.source.scaleLabel"),
                Items = _scales.Select(sc => sc.Label).ToList(),
                Margin = new Thickness(0, 8, 0, 0),
                AccessibleName = LemoineStrings.T("pdfRegionWand.source.scaleLabel"),
            };
            var settings = PdfRegionWandSettings.Instance;
            var def = _scales.FirstOrDefault(sc => Math.Abs(sc.InchesPerFoot - settings.DefaultScaleInchesPerFoot) < 1e-9);
            _scaleSelect.SelectedItem = def.Label ?? _scales[4].Label;
            _scaleSelect.SelectionChanged += sel =>
            {
                var hit = _scales.FirstOrDefault(sc => sc.Label == sel);
                if (hit.Label == null) return;
                settings.DefaultScaleInchesPerFoot = hit.InchesPerFoot;
                settings.Save();
                WarnOnScaleMismatch();
            };
            stack.Children.Add(_scaleSelect);

            _modeText = BodyText(LemoineStrings.T("pdfRegionWand.source.modeUnknown"));
            _modeText.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(_modeText);

            _modeSelect = new LemoineSingleSelect
            {
                Label = LemoineStrings.T("pdfRegionWand.source.modeLabel"),
                Items = new List<string> { "Auto (detected)", "Vector", "Raster" },
                SelectedItem = "Auto (detected)",
                Margin = new Thickness(0, 4, 0, 0),
                AccessibleName = LemoineStrings.T("pdfRegionWand.source.modeLabel"),
            };
            _modeSelect.SelectionChanged += _ => PrepareSources(resetSession: false);
            stack.Children.Add(_modeSelect);

            return new LemoineSectionCard { Header = LemoineStrings.T("pdfRegionWand.source.header"), CardContent = stack, Margin = new Thickness(0, 0, 0, 8) };
        }

        private UIElement BuildActionsCard()
        {
            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _pickPointBtn = LemoineControlStyles.BuildButton(LemoineStrings.T("pdfRegionWand.actions.pickPoint"), LemoineControlStyles.LemoineButtonVariant.Primary);
            _pickPointBtn.Click += (s, e) => StartPickPoint();
            WpfGrid.SetColumn(_pickPointBtn, 0);
            grid.Children.Add(_pickPointBtn);

            _splitBtn = LemoineControlStyles.BuildButton(LemoineStrings.T("pdfRegionWand.actions.drawSplitLines"));
            _splitBtn.Click += (s, e) => ToggleSplitLines();
            WpfGrid.SetColumn(_splitBtn, 2);
            grid.Children.Add(_splitBtn);

            var stack = new StackPanel();
            stack.Children.Add(grid);
            var hint = BodyText(LemoineStrings.T("pdfRegionWand.actions.hint"));
            hint.FontStyle = FontStyles.Italic;
            hint.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(hint);

            return new LemoineSectionCard { Header = LemoineStrings.T("pdfRegionWand.actions.header"), CardContent = stack, Margin = new Thickness(0, 0, 0, 8) };
        }

        private UIElement BuildPreviewCard()
        {
            var stack = new StackPanel();

            _previewCanvas = new Canvas { Height = 180, ClipToBounds = true, SnapsToDevicePixels = true };
            var canvasBorder = new Border { BorderThickness = new Thickness(1), Child = _previewCanvas };
            canvasBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            canvasBorder.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            stack.Children.Add(canvasBorder);

            _previewText = BodyText("");
            _previewText.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(_previewText);

            var grid = new WpfGrid { Margin = new Thickness(0, 8, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var discardBtn = LemoineControlStyles.BuildButton(LemoineStrings.T("pdfRegionWand.preview.discard"), LemoineControlStyles.LemoineButtonVariant.Danger);
            discardBtn.Click += (s, e) => DiscardPending();
            WpfGrid.SetColumn(discardBtn, 0);
            grid.Children.Add(discardBtn);

            var confirmBtn = LemoineControlStyles.BuildButton(LemoineStrings.T("pdfRegionWand.preview.confirm"), LemoineControlStyles.LemoineButtonVariant.Primary);
            confirmBtn.Click += (s, e) => ConfirmPending();
            WpfGrid.SetColumn(confirmBtn, 2);
            grid.Children.Add(confirmBtn);
            stack.Children.Add(grid);

            _createOnConfirmCheck = new CheckBox
            {
                Content = LemoineStrings.T("pdfRegionWand.preview.createOnConfirm"),
                IsChecked = PdfRegionWandSettings.Instance.CreateOnConfirm,
                Margin = new Thickness(0, 8, 0, 0),
            };
            _createOnConfirmCheck.Checked += (s, e) => SaveCreateOnConfirm(true);
            _createOnConfirmCheck.Unchecked += (s, e) => SaveCreateOnConfirm(false);
            stack.Children.Add(_createOnConfirmCheck);

            _previewCard = new LemoineSectionCard
            {
                Header = LemoineStrings.T("pdfRegionWand.preview.header"),
                CardContent = stack,
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = WpfVisibility.Collapsed,
            };
            return _previewCard;
        }

        private static void SaveCreateOnConfirm(bool value)
        {
            PdfRegionWandSettings.Instance.CreateOnConfirm = value;
            PdfRegionWandSettings.Instance.Save();
        }

        private UIElement BuildRegionsCard()
        {
            var stack = new StackPanel();
            _regionStack = new StackPanel();
            stack.Children.Add(_regionStack);

            _createAllBtn = LemoineControlStyles.BuildButton(LemoineStrings.T("pdfRegionWand.regions.createAll", 0));
            _createAllBtn.Margin = new Thickness(0, 8, 0, 0);
            _createAllBtn.IsEnabled = false;
            _createAllBtn.Click += (s, e) => StartCreateAll();
            stack.Children.Add(_createAllBtn);

            return new LemoineSectionCard { Header = LemoineStrings.T("pdfRegionWand.regions.header"), CardContent = stack, Margin = new Thickness(0, 0, 0, 8) };
        }

        private UIElement BuildOutputCard()
        {
            var stack = new StackPanel();

            _typeSelect = new LemoineSingleSelect
            {
                Label = LemoineStrings.T("pdfRegionWand.output.floorType"),
                Items = _floorTypes.Select(t => t.Name).ToList(),
                SelectedItem = _floorTypes.FirstOrDefault()?.Name,
                AccessibleName = LemoineStrings.T("pdfRegionWand.output.floorType"),
            };
            stack.Children.Add(_typeSelect);

            _levelSelect = new LemoineSingleSelect
            {
                Label = LemoineStrings.T("pdfRegionWand.output.level"),
                Items = _levels.Select(l => l.Name).ToList(),
                SelectedItem = _levels.FirstOrDefault()?.Name,
                Margin = new Thickness(0, 8, 0, 0),
                AccessibleName = LemoineStrings.T("pdfRegionWand.output.level"),
            };
            stack.Children.Add(_levelSelect);

            _structuralCheck = new CheckBox { Content = LemoineStrings.T("pdfRegionWand.output.structural"), Margin = new Thickness(0, 8, 0, 0) };
            stack.Children.Add(_structuralCheck);

            return new LemoineSectionCard { Header = LemoineStrings.T("pdfRegionWand.output.header"), CardContent = stack, Margin = new Thickness(0, 0, 0, 8) };
        }

        private UIElement BuildTuningCard()
        {
            var settings = PdfRegionWandSettings.Instance;
            var stack = new StackPanel();

            stack.Children.Add(StepperRow(LemoineStrings.T("pdfRegionWand.tuning.rasterDpi"), settings.RasterDpi, 72, 600, 1, 0, v =>
            {
                settings.RasterDpi = (int)v; settings.Save();
                PrepareSources(resetSession: false);
            }));
            stack.Children.Add(StepperRow(LemoineStrings.T("pdfRegionWand.tuning.binarizeThreshold"), settings.BinarizeThreshold, 0.05, 0.95, 0.05, 2, v =>
            {
                settings.BinarizeThreshold = v; settings.Save();
                PrepareSources(resetSession: false);
            }));
            stack.Children.Add(StepperRow(LemoineStrings.T("pdfRegionWand.tuning.simplify"), settings.SimplifyModelInches, 0.1, 12, 0.1, 1, v =>
            {
                settings.SimplifyModelInches = v; settings.Save();
            }));
            stack.Children.Add(StepperRow(LemoineStrings.T("pdfRegionWand.tuning.orthoSnap"), settings.OrthoSnapAngleDeg, 0, 10, 0.5, 1, v =>
            {
                settings.OrthoSnapAngleDeg = v; settings.Save();
            }));
            stack.Children.Add(StepperRow(LemoineStrings.T("pdfRegionWand.tuning.maxRegion"), settings.MaxRegionAreaFt2, 100, 1000000, 100, 0, v =>
            {
                settings.MaxRegionAreaFt2 = v; settings.Save();
            }));

            var keepCheck = new CheckBox
            {
                Content = LemoineStrings.T("pdfRegionWand.tuning.keepSplitLines"),
                IsChecked = settings.KeepSplitLines,
                Margin = new Thickness(0, 8, 0, 0),
            };
            keepCheck.Checked += (s, e) => { settings.KeepSplitLines = true; settings.Save(); };
            keepCheck.Unchecked += (s, e) => { settings.KeepSplitLines = false; settings.Save(); };
            stack.Children.Add(keepCheck);

            var modelLineCheck = new CheckBox
            {
                Content = LemoineStrings.T("pdfRegionWand.tuning.modelLines"),
                IsChecked = settings.UseModelLines,
                Margin = new Thickness(0, 6, 0, 0),
            };
            modelLineCheck.Checked += (s, e) => { settings.UseModelLines = true; settings.Save(); };
            modelLineCheck.Unchecked += (s, e) => { settings.UseModelLines = false; settings.Save(); };
            stack.Children.Add(modelLineCheck);

            return new LemoineSectionCard { Header = LemoineStrings.T("pdfRegionWand.tuning.header"), CardContent = stack, Margin = new Thickness(0, 0, 0, 8) };
        }

        private UIElement StepperRow(string label, double value, double min, double max,
                                     double step, int decimals, Action<double> onChanged)
        {
            var row = new WpfGrid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lbl = BodyText(label);
            lbl.VerticalAlignment = VerticalAlignment.Center;
            WpfGrid.SetColumn(lbl, 0);
            row.Children.Add(lbl);

            var stepper = new LemoineInlineStepper
            {
                MinValue = min, MaxValue = max, Value = value, Step = step, Decimals = decimals,
                ValueWidth = 56,
            };
            stepper.ValueChanged += (s, v) => onChanged(v);
            WpfGrid.SetColumn(stepper, 1);
            row.Children.Add(stepper);
            return row;
        }

        private TextBlock BodyText(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private void BuildLog()
        {
            _logBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            _logBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            _logStack = new StackPanel { Margin = new Thickness(10, 4, 10, 4) };
            _logScroll = new ScrollViewer
            {
                Height = 110,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _logStack,
            };
            LemoineControlStyles.SetSelfContainedScroll(_logScroll, true);
            _logBorder.Child = _logScroll;
        }

        private void BuildFooter()
        {
            _footerBorder.SetResourceReference(Border.PaddingProperty, "LemoineTh_FooterPad");
            _footerBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            _footerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            _statusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontStyle = FontStyles.Italic };
            _statusText.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            _statusText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _statusText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var closeBtn = LemoineControlStyles.BuildButton("Close");
            closeBtn.Click += (s, e) => Close();

            var dp = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(closeBtn, Dock.Right);
            dp.Children.Add(closeBtn);
            dp.Children.Add(_statusText);
            _footerBorder.Child = dp;
        }

        // ═══════════════════════════════════════════════════════ logging/UI

        private void AddLog(string message, string status)
        {
            if (_logStack == null) return;
            var tb = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            string key = status == "pass" ? "LemoineGreen"
                       : status == "fail" ? "LemoineRed"
                       : status == "warn" ? "LemoineAccent"
                       : "LemoineTextDim";
            tb.SetResourceReference(TextBlock.ForegroundProperty, key);
            _logStack.Children.Add(tb);
            if (_logStack.Children.Count > 400) _logStack.Children.RemoveAt(0);
            _logScroll?.ScrollToEnd();
        }

        private void SetStatus(string text)
        {
            if (_statusText != null) _statusText.Text = text;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            bool ready = !busy && _engine != null;
            if (_pickPointBtn != null) _pickPointBtn.IsEnabled = ready && !_drawingSplitLines;
            if (_splitBtn != null) _splitBtn.IsEnabled = !busy && _engine != null;
            if (_createAllBtn != null)
                _createAllBtn.IsEnabled = ready && CreatableFaces().Any();
        }

        // ════════════════════════════════════════════════ image select flow

        private void StartSelectImage()
        {
            if (_busy) return;
            SetBusy(true);
            SetStatus(LemoineStrings.T("pdfRegionWand.status.pickUnderlay"));
            RaiseOp(PdfWandOp.SelectImage);
        }

        private void HandleImageSelected(ImageSelection? sel)
        {
            if (sel == null)
            {
                SetBusy(false);
                SetStatus(LemoineStrings.T("pdfRegionWand.status.selectionCancelled"));
                return;
            }
            _selection = sel;
            _engine = null;
            _raster = null;
            _probe = null;
            _pending = null;
            _previewCard!.Visibility = WpfVisibility.Collapsed;
            RebuildRegionRows();

            _sourceText!.Text = $"{sel.DisplayName}" +
                (sel.PdfPath != null ? $"  ({System.IO.Path.GetFileName(sel.PdfPath)})" : LemoineStrings.T("pdfRegionWand.source.notFound"));
            _fileBrowser!.Visibility = sel.PdfPath == null ? WpfVisibility.Visible : WpfVisibility.Collapsed;

            if (sel.PdfPath == null)
            {
                SetBusy(false);
                SetStatus(LemoineStrings.T("pdfRegionWand.status.browseToContinue"));
                return;
            }
            PrepareSources(resetSession: true);
        }

        /// <summary>
        /// Probes the PDF, builds the transform, and loads the active mode's
        /// source data (rasterize or vector-extract) on a background task.
        /// resetSession discards the region graph (new image/page); source-only
        /// changes (mode, DPI) keep the session and just reload the inputs.
        /// </summary>
        private async void PrepareSources(bool resetSession)
        {
            var sel = _selection;
            if (sel?.PdfPath == null) return;
            if (_busy && !resetSession) return;
            SetBusy(true);
            SetStatus(LemoineStrings.T("pdfRegionWand.status.readingPdf"));

            var settings = PdfRegionWandSettings.Instance;
            string path = sel.PdfPath;
            int page = sel.PageNumber;
            string modeChoice = _modeSelect?.SelectedItem ?? "Auto (detected)";

            try
            {
                await Task.Run(() =>
                {
                    _probe = PdfContentProbe.Probe(path, page, settings.VectorDetectThreshold);
                });

                _pageStepper!.MaxValue = Math.Max(1, _probe!.PageCount);

                var transform = PdfToModelTransform.FromPlacement(
                    sel.Placement, _probe.PageWidthPts, _probe.PageHeightPts,
                    (m, s) => AddLog(m, s));
                if (transform == null)
                {
                    SetBusy(false);
                    SetStatus(LemoineStrings.T("pdfRegionWand.status.transformFailed"));
                    return;
                }
                _transform = transform;

                if (resetSession || _engine == null)
                {
                    _engine = new RegionWandEngine(BuildConfig(transform, settings));
                    _engine.Session.Diagnostic += msg => OnUi(() =>
                    {
                        AddLog(msg, "info");
                        LemoineLog.Info("PdfRegionWand.Session", msg);
                    });
                    _raster = null;
                    RebuildRegionRows();
                }

                var detected = _probe.SuggestedMode;
                var mode = modeChoice == "Vector" ? ExtractionMode.Vector
                         : modeChoice == "Raster" ? ExtractionMode.Raster
                         : detected == "Vector" ? ExtractionMode.Vector : ExtractionMode.Raster;
                _engine.Mode = mode;
                _modeText!.Text = LemoineStrings.T("pdfRegionWand.source.modeText", mode, detected, _probe.PathCommandCount);
                AddLog(LemoineStrings.T("pdfRegionWand.log.detected", detected, _probe.PathCommandCount, mode), "info");
                WarnOnScaleMismatch();

                if (mode == ExtractionMode.Raster && (_raster == null || !_engine.HasRaster))
                {
                    SetStatus(LemoineStrings.T("pdfRegionWand.status.rasterizing", page, settings.RasterDpi));
                    var engine = _engine;
                    await Task.Run(() =>
                    {
                        var raster = new PdfiumPageRasterizer().Rasterize(path, page, settings.RasterDpi);
                        engine.LoadRaster(raster);
                        _raster = raster;
                    });
                    AddLog(LemoineStrings.T("pdfRegionWand.log.rasterized", _raster!.Width, _raster.Height), "info");
                }
                else if (mode == ExtractionMode.Vector && !_engine.HasVector)
                {
                    SetStatus(LemoineStrings.T("pdfRegionWand.status.extractingVector"));
                    var engine = _engine;
                    await Task.Run(() =>
                    {
                        var segs = PdfPathExtractor.Extract(path, page, new PathExtractOptions());
                        engine.LoadVectorSegments(segs);
                    });
                    AddLog(LemoineStrings.T("pdfRegionWand.log.vectorLoaded"), "info");
                }

                SetStatus(LemoineStrings.T("pdfRegionWand.status.ready"));
            }
            catch (Exception ex)
            {
                LemoineLog.Error("PdfRegionWand: prepare sources", ex);
                AddLog(LemoineStrings.T("pdfRegionWand.log.pdfReadFailed", ex.Message), "fail");
                SetStatus(LemoineStrings.T("pdfRegionWand.status.pdfNotPrepared"));
            }
            finally
            {
                SetBusy(false);
            }
        }

        private WandConfig BuildConfig(PdfToModelTransform transform, PdfRegionWandSettings settings)
        {
            double simplifyPts = Math.Max(0.25, transform.ModelFtToPts(settings.SimplifyModelInches / 12.0));
            double ptPerFt = transform.ModelFtToPts(1.0);
            return new WandConfig
            {
                Tol = new GeomTol
                {
                    SimplifyTol = simplifyPts,
                    OrthoSnapAngleDeg = settings.OrthoSnapAngleDeg,
                    ReconcileTol = Math.Max(simplifyPts * 2, 1.0),
                    VectorJoinTol = Math.Max(simplifyPts, 0.5),
                    ArcFitTol = Math.Max(simplifyPts * 0.5, 0.25),
                    MinFaceArea = ptPerFt * ptPerFt * 0.5, // half a square foot
                },
                BinarizeThreshold = settings.BinarizeThreshold,
                MaxRegionAreaPts2 = settings.MaxRegionAreaFt2 * ptPerFt * ptPerFt,
            };
        }

        private void WarnOnScaleMismatch()
        {
            if (_transform == null) return;
            var declared = PdfRegionWandSettings.Instance.DefaultScaleInchesPerFoot;
            double implied = _transform.Core.ImpliedInchesPerFoot;
            if (Math.Abs(implied - declared) / declared > 0.05)
                AddLog(LemoineStrings.T("pdfRegionWand.log.scaleMismatch", implied, declared), "warn");
        }

        // ═══════════════════════════════════════════════════════ wand flow

        private void StartPickPoint()
        {
            if (_busy || _engine == null || _transform == null) return;
            if (_pending != null)
            {
                AddLog(LemoineStrings.T("pdfRegionWand.log.confirmFirst"), "info");
                return;
            }
            SetBusy(true);
            SetStatus(LemoineStrings.T("pdfRegionWand.status.pickPoint"));
            RaiseOp(PdfWandOp.PickSeedPoint);
        }

        private async void HandleSeedPicked(XYZ? point)
        {
            if (point == null || _engine == null || _transform == null)
            {
                SetBusy(false);
                SetStatus(LemoineStrings.T("pdfRegionWand.status.pickCancelled"));
                return;
            }

            SetStatus(LemoineStrings.T("pdfRegionWand.status.tracing"));
            var seed = _transform.ModelToPdf(point);
            var engine = _engine;
            WandResult? result = null;
            try
            {
                await Task.Run(() => { result = engine.Wand(seed); });
            }
            catch (Exception ex)
            {
                LemoineLog.Error("PdfRegionWand: wand", ex);
                AddLog(LemoineStrings.T("pdfRegionWand.log.traceFailedMsg", ex.Message), "fail");
                SetBusy(false);
                SetStatus(LemoineStrings.T("pdfRegionWand.status.traceFailed"));
                return;
            }

            HandleWandResult(result!);
        }

        private void HandleWandResult(WandResult result)
        {
            switch (result.Status)
            {
                case WandStatus.Ok:
                    if (!string.IsNullOrEmpty(result.Message)) AddLog(result.Message!, "warn");
                    _pending = result;
                    ShowPreview(result);
                    SetStatus(LemoineStrings.T("pdfRegionWand.status.review"));
                    break;

                case WandStatus.Duplicate:
                    AddLog(LemoineStrings.T("pdfRegionWand.log.alreadyTracedAs", result.Face!.FaceId), "info");
                    SetStatus(LemoineStrings.T("pdfRegionWand.status.alreadyTraced"));
                    break;

                default:
                    AddLog(result.Message ?? LemoineStrings.T("pdfRegionWand.log.traceFailedStatus", result.Status), "fail");
                    LemoineLog.Warn("PdfRegionWand", $"Wand {result.Status}: {result.Message}");
                    SetStatus(LemoineStrings.T("pdfRegionWand.status.noRegion"));
                    break;
            }
            RebuildRegionRows();
            SetBusy(false);
        }

        private void ConfirmPending()
        {
            var pending = _pending;
            if (pending?.Face == null) return;
            _pending = null;
            _previewCard!.Visibility = WpfVisibility.Collapsed;
            AddLog(LemoineStrings.T("pdfRegionWand.log.confirmed", pending.Face.FaceId, AreaText(pending.Plan!.AreaSqPts)), "pass");
            RebuildRegionRows();

            if (_createOnConfirmCheck?.IsChecked == true)
                StartCreate(pending.Face.FaceId);
        }

        private void DiscardPending()
        {
            var pending = _pending;
            if (pending?.Face == null) return;
            _pending = null;
            _engine?.Session.RemoveFace(pending.Face.FaceId);
            _previewCard!.Visibility = WpfVisibility.Collapsed;
            AddLog(LemoineStrings.T("pdfRegionWand.log.discarded", pending.Face.FaceId), "info");
            RebuildRegionRows();
            SetStatus(LemoineStrings.T("pdfRegionWand.status.ready"));
        }

        // ═══════════════════════════════════════════════════ preview canvas

        private void ShowPreview(WandResult result)
        {
            if (_previewCanvas == null || result.Plan == null) return;
            _previewCanvas.Children.Clear();
            _previewCard!.Visibility = WpfVisibility.Visible;
            _previewText!.Text = LemoineStrings.T("pdfRegionWand.preview.info",
                result.Face!.FaceId, AreaText(result.Plan.AreaSqPts),
                result.Plan.OuterLoop.Count, result.Plan.Holes.Count, result.Plan.ExtractionMode);

            var bounds = Bounds2.FromPoints(result.Plan.OuterLoop).Inflate(12);
            double cw = Math.Max(50, _previewCanvas.ActualWidth > 0 ? _previewCanvas.ActualWidth : 420);
            double ch = _previewCanvas.Height;
            double scale = Math.Min(cw / bounds.Width, ch / bounds.Height);
            double ox = (cw - bounds.Width * scale) / 2;
            double oy = (ch - bounds.Height * scale) / 2;

            WpfPoint Map(Pt2 p) => new WpfPoint(
                ox + (p.X - bounds.MinX) * scale,
                oy + (bounds.MaxY - p.Y) * scale); // flip: canvas Y is down

            if (_raster != null) DrawRasterSnippet(bounds, scale, ox, oy);

            var outerPoly = new Polygon { StrokeThickness = 2, Fill = Brushes.Transparent };
            outerPoly.SetResourceReference(Shape.StrokeProperty, "LemoineAccent");
            foreach (var p in result.Plan.OuterLoop) outerPoly.Points.Add(Map(p));
            _previewCanvas.Children.Add(outerPoly);

            foreach (var hole in result.Plan.Holes)
            {
                var holePoly = new Polygon { StrokeThickness = 1.5, Fill = Brushes.Transparent };
                holePoly.SetResourceReference(Shape.StrokeProperty, "LemoineRed");
                foreach (var p in hole) holePoly.Points.Add(Map(p));
                _previewCanvas.Children.Add(holePoly);
            }
        }

        private void DrawRasterSnippet(Bounds2 boundsPts, double scale, double ox, double oy)
        {
            try
            {
                var r = _raster!;
                double ppp = r.PixelsPerPoint;
                int x0 = Math.Max(0, (int)(boundsPts.MinX * ppp));
                int x1 = Math.Min(r.Width - 1, (int)Math.Ceiling(boundsPts.MaxX * ppp));
                int y0 = Math.Max(0, (int)((r.PageHeightPts - boundsPts.MaxY) * ppp));
                int y1 = Math.Min(r.Height - 1, (int)Math.Ceiling((r.PageHeightPts - boundsPts.MinY) * ppp));
                int w = x1 - x0 + 1, h = y1 - y0 + 1;
                if (w <= 1 || h <= 1) return;

                // Cap the snippet at ~2 Mpx so huge rooms can't stall the UI thread.
                int stride = Math.Max(1, (int)Math.Sqrt((double)w * h / 2_000_000));
                int sw = w / stride, sh = h / stride;
                if (sw < 1 || sh < 1) return;
                var bytes = new byte[sw * sh];
                for (int y = 0; y < sh; y++)
                    for (int x = 0; x < sw; x++)
                        bytes[y * sw + x] = r.Gray[(y0 + y * stride) * r.Width + x0 + x * stride];

                var bmp = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Gray8, null, bytes, sw);
                bmp.Freeze();
                var img = new WpfImage
                {
                    Source = bmp,
                    Width = w / ppp * scale,
                    Height = h / ppp * scale,
                    Stretch = Stretch.Fill,
                    Opacity = 0.45,
                };
                Canvas.SetLeft(img, ox + (x0 / ppp - boundsPts.MinX) * scale);
                Canvas.SetTop(img, oy + (boundsPts.MaxY - (r.PageHeightPts - y0 / ppp)) * scale);
                _previewCanvas!.Children.Add(img);
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("PdfRegionWand: preview snippet", ex);
            }
        }

        // ═══════════════════════════════════════════════════ split-line flow

        private void ToggleSplitLines()
        {
            if (_engine == null) return;
            if (!_drawingSplitLines)
            {
                if (_busy) return;
                SetBusy(true);
                var settings = PdfRegionWandSettings.Instance;
                _handler.UseModelLines = settings.UseModelLines;
                _handler.KeepSplitLines = settings.KeepSplitLines;
                SetStatus(LemoineStrings.T("pdfRegionWand.status.startingLineTool"));
                RaiseOp(PdfWandOp.StartSplitLines);
            }
            else
            {
                SetBusy(true);
                SetStatus(LemoineStrings.T("pdfRegionWand.status.collectingLines"));
                RaiseOp(PdfWandOp.FinishSplitLines);
            }
        }

        private void HandleSplitLinesStarted(bool ok)
        {
            if (ok)
            {
                _drawingSplitLines = true;
                _splitBtn!.Content = LemoineStrings.T("pdfRegionWand.actions.doneDrawing");
                SetStatus(LemoineStrings.T("pdfRegionWand.status.drawLines"));
            }
            else
            {
                SetStatus(LemoineStrings.T("pdfRegionWand.status.lineToolFailed"));
            }
            SetBusy(false);
        }

        private async void HandleSplitLinesCaptured(List<List<XYZ>> polylines)
        {
            _drawingSplitLines = false;
            _splitBtn!.Content = LemoineStrings.T("pdfRegionWand.actions.drawSplitLines");

            if (_engine == null || _transform == null || polylines.Count == 0)
            {
                AddLog(LemoineStrings.T("pdfRegionWand.log.noSplitLines"), "info");
                SetBusy(false);
                SetStatus(LemoineStrings.T("pdfRegionWand.status.ready"));
                return;
            }

            var engine = _engine;
            var transform = _transform;
            int invalidatedTotal = 0;
            try
            {
                await Task.Run(() =>
                {
                    foreach (var line in polylines)
                    {
                        var pdfPts = line.Select(transform.ModelToPdf).ToList();
                        var res = engine.AddSplitLine(pdfPts);
                        invalidatedTotal += res.InvalidatedFaceIds.Count;
                    }
                });
                AddLog(LemoineStrings.T("pdfRegionWand.log.splitCaptured", polylines.Count) +
                       (invalidatedTotal > 0 ? LemoineStrings.T("pdfRegionWand.log.splitInvalidated", invalidatedTotal) : ""),
                       "pass");
            }
            catch (Exception ex)
            {
                LemoineLog.Error("PdfRegionWand: register split lines", ex);
                AddLog(LemoineStrings.T("pdfRegionWand.log.splitCaptureFailed", ex.Message), "fail");
            }
            RebuildRegionRows();
            SetBusy(false);
            SetStatus(LemoineStrings.T("pdfRegionWand.status.ready"));
        }

        // ═════════════════════════════════════════════════════ create flow

        private IEnumerable<RegionFace> CreatableFaces() =>
            _engine?.Session.Faces.Where(f => f.Status == FaceStatus.Traced || f.Status == FaceStatus.Available)
            ?? Enumerable.Empty<RegionFace>();

        private void StartCreate(int faceId)
        {
            if (_engine == null || _transform == null) return;
            if (_busy)
            {
                _createQueue.Enqueue(faceId);
                AddLog(LemoineStrings.T("pdfRegionWand.log.queued", faceId), "info");
                return;
            }

            var options = BuildOutputOptions();
            if (options == null) return;

            RegionPlan plan;
            try
            {
                plan = _engine.Session.BuildPlan(faceId);
            }
            catch (Exception ex)
            {
                LemoineLog.Error($"PdfRegionWand: build plan for face {faceId}", ex);
                AddLog(LemoineStrings.T("pdfRegionWand.log.planFailed", faceId), "fail");
                return;
            }

            SetBusy(true);
            SetStatus(LemoineStrings.T("pdfRegionWand.status.creatingFloor", faceId));
            _handler.Plan = plan;
            _handler.Options = options;
            _handler.Adapter = _adapter;
            _handler.FaceId = faceId;
            _handler.Transform = _transform;
            RaiseOp(PdfWandOp.CreateRegion);
        }

        private RegionOutputOptions? BuildOutputOptions()
        {
            var type = _floorTypes.FirstOrDefault(t => t.Name == _typeSelect?.SelectedItem);
            var level = _levels.FirstOrDefault(l => l.Name == _levelSelect?.SelectedItem);
            if (type == null || level == null)
            {
                AddLog(LemoineStrings.T("pdfRegionWand.log.selectTypeLevel"), "fail");
                return null;
            }
            return new RegionOutputOptions
            {
                TypeId = type.Id,
                LevelId = level.Id,
                Structural = _structuralCheck?.IsChecked == true,
                MinHoleAreaFt2 = PdfRegionWandSettings.Instance.MinHoleAreaFt2,
            };
        }

        private void StartCreateAll()
        {
            if (_engine == null) return;
            _createQueue.Clear();
            foreach (var face in CreatableFaces()) _createQueue.Enqueue(face.FaceId);
            if (_createQueue.Count == 0) return;
            AddLog(LemoineStrings.T("pdfRegionWand.log.creatingCount", _createQueue.Count), "info");
            StartCreate(_createQueue.Dequeue());
        }

        private void HandleRegionCreated(int faceId, long elementIdValue)
        {
            if (elementIdValue > 0)
            {
                _engine?.Session.MarkElementCreated(faceId, elementIdValue);
                AddLog(LemoineStrings.T("pdfRegionWand.log.floorCreated", faceId, elementIdValue), "pass");
            }
            // Failure is already logged by the handler; the session keeps the
            // face Traced so the user can retry — the run continues either way.

            RebuildRegionRows();
            SetBusy(false);
            if (_createQueue.Count > 0)
                StartCreate(_createQueue.Dequeue());
            else
                SetStatus(LemoineStrings.T("pdfRegionWand.status.ready"));
        }

        private void HandleTrackedDeleted(List<long> elementIdValues)
        {
            if (_engine == null) return;
            foreach (var value in elementIdValues)
            {
                var face = _engine.Session.ReleaseByElement(value);
                if (face != null)
                    AddLog(LemoineStrings.T("pdfRegionWand.log.floorDeleted", face.FaceId), "info");
            }
            RebuildRegionRows();
        }

        // ═══════════════════════════════════════════════════════ region list

        private void RebuildRegionRows()
        {
            if (_regionStack == null) return;
            _regionStack.Children.Clear();

            var faces = _engine?.Session.Faces ?? (IReadOnlyList<RegionFace>)new List<RegionFace>();
            if (faces.Count == 0)
            {
                var empty = BodyText(LemoineStrings.T("pdfRegionWand.regions.empty"));
                empty.FontStyle = FontStyles.Italic;
                _regionStack.Children.Add(empty);
            }

            foreach (var face in faces)
                _regionStack.Children.Add(BuildRegionRow(face));

            int n = CreatableFaces().Count();
            if (_createAllBtn != null)
            {
                _createAllBtn.Content = LemoineStrings.T("pdfRegionWand.regions.createAll", n);
                _createAllBtn.IsEnabled = n > 0 && !_busy;
            }
        }

        private UIElement BuildRegionRow(RegionFace face)
        {
            var row = new WpfGrid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var id = BodyText($"#{face.FaceId}");
            id.VerticalAlignment = VerticalAlignment.Center;
            WpfGrid.SetColumn(id, 0);
            row.Children.Add(id);

            var area = BodyText(AreaText(face.AreaSqPts));
            area.VerticalAlignment = VerticalAlignment.Center;
            WpfGrid.SetColumn(area, 1);
            row.Children.Add(area);

            var status = BodyText(StatusLabel(face.Status));
            status.VerticalAlignment = VerticalAlignment.Center;
            status.SetResourceReference(TextBlock.ForegroundProperty, StatusColorKey(face.Status));
            WpfGrid.SetColumn(status, 2);
            row.Children.Add(status);

            if (face.Status == FaceStatus.Traced || face.Status == FaceStatus.Available)
            {
                var createBtn = LemoineControlStyles.BuildSmallButton(LemoineStrings.T("pdfRegionWand.regions.create"));
                createBtn.Margin = new Thickness(4, 0, 0, 0);
                int fid = face.FaceId;
                createBtn.Click += (s, e) => StartCreate(fid);
                WpfGrid.SetColumn(createBtn, 3);
                row.Children.Add(createBtn);
            }

            if (face.Status != FaceStatus.ElementCreated)
            {
                var removeBtn = LemoineControlStyles.BuildSmallButton(
                    char.ConvertFromUtf32(0xE74D), LemoineControlStyles.LemoineButtonVariant.Danger);
                removeBtn.Margin = new Thickness(4, 0, 0, 0);
                removeBtn.ToolTip = LemoineStrings.T("pdfRegionWand.regions.removeTip");
                int fid = face.FaceId;
                removeBtn.Click += (s, e) =>
                {
                    _engine?.Session.RemoveFace(fid);
                    AddLog(LemoineStrings.T("pdfRegionWand.log.removed", fid), "info");
                    RebuildRegionRows();
                };
                WpfGrid.SetColumn(removeBtn, 4);
                row.Children.Add(removeBtn);
            }

            return row;
        }

        private static string StatusLabel(FaceStatus status)
        {
            switch (status)
            {
                case FaceStatus.ElementCreated: return LemoineStrings.T("pdfRegionWand.regions.status.created");
                case FaceStatus.Available: return LemoineStrings.T("pdfRegionWand.regions.status.available");
                case FaceStatus.Invalidated: return LemoineStrings.T("pdfRegionWand.regions.status.invalidated");
                default: return LemoineStrings.T("pdfRegionWand.regions.status.traced");
            }
        }

        private static string StatusColorKey(FaceStatus status)
        {
            switch (status)
            {
                case FaceStatus.ElementCreated: return "LemoineGreen";
                case FaceStatus.Invalidated: return "LemoineRed";
                case FaceStatus.Available: return "LemoineAccent";
                default: return "LemoineText";
            }
        }

        private string AreaText(double areaSqPts) =>
            _transform == null ? LemoineStrings.T("pdfRegionWand.area.pts", areaSqPts) : LemoineStrings.T("pdfRegionWand.area.ft2", _transform.AreaPtsToFt2(areaSqPts));
    }
}
