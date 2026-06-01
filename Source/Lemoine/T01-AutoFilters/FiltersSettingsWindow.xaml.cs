using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Shapes;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Lemoine
{
    public partial class FiltersSettingsWindow : Window
    {
        // ── Live project pattern lists ───────────────────────────────────────
        internal IReadOnlyList<string> FillPatternNames { get; private set; } = Array.Empty<string>();
        internal IReadOnlyList<string> LinePatternNames { get; private set; } = Array.Empty<string>();

        internal void SetPatternLists(IEnumerable<string> fillPatterns, IEnumerable<string> linePatterns)
        {
            FillPatternNames = new ReadOnlyCollection<string>(fillPatterns.ToList());
            LinePatternNames = new ReadOnlyCollection<string>(linePatterns.ToList());
        }

        // ── Filter state ─────────────────────────────────────────────────────
        private List<FilterTradeConfig>? _filterTrades;
        private string? _fActiveTradeId;
        private string? _fActiveRuleId;
        private StackPanel?  _fRuleListPanel;
        private Border?      _fEditorBorder;
        private Border?      _fTradesSidebar;
        private StackPanel?  _fTradeListPanel;
        private LemoineListReorder? _fTradeReorder;   // drag-to-reorder trades
        private UIElement?   _fAddTradeAnchor;
        private TextBlock?   _fStatusText;
        private Border?      _fStatusChip;
        private ScrollViewer? _fRuleScroll;     // for auto-scroll-to-new on add
        private ScrollViewer? _fTradeScroll;
        private string       _filtersSnapshot = ""; // serialized buffer at load (dirty check)
        private Border?     _fActiveRowBorder;
        private TextBlock?  _fActiveNameTb;
        private readonly HashSet<string>            _fSelectedRuleIds    = new HashSet<string>();
        private          string?                    _fShiftAnchorRuleId;
        private readonly Dictionary<string, Border> _fMultiSelectBorders = new Dictionary<string, Border>();

        // ── Active drag state (rule reorder) ─────────────────────────────────
        private string?  _dragRuleId;
        private Border?  _dragSourceBorder;
        private int      _dragSourceOrigIdx;
        private Point    _dragGhostClickOffset;
        private readonly LemoineDragGhost _ruleGhost = new LemoineDragGhost();   // rule-row drag ghost
        private Border?  _dragReadyBorder;
        private bool     _isRefreshingEditor;

        // ── Drag state: trade reorder ────────────────────────────────────────
        private string?  _dragTradeId;
        private Point    _tradeDragStart;

        // ── Double-click rename timing ───────────────────────────────────────
        private DateTime _lastClickTime   = DateTime.MinValue;
        private string?  _lastClickItemId;


        // ─────────────────────────────────────────────────────────────────────
        public FiltersSettingsWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;

            LemoineSettings.Instance.ThemeChanged += t => Dispatcher.Invoke(() =>
            {
                LemoineSettings.Instance.ApplyTo(Resources);
                Background = t.PageBg;
                _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            });

            LemoineSettings.Instance.UiSizeChanged += _ => Dispatcher.Invoke(() =>
            {
                LemoineSettings.Instance.ApplyScaleTo(Resources);
                LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 8);
                UpdateRowHeights();
            });
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LemoineSettings.Instance.ApplyTo(Resources);
            LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 8);
            Background = LemoineSettings.Instance.ActiveTheme.PageBg;
            _root.SetResourceReference(Grid.BackgroundProperty, "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _outerBorder.CornerRadius = new CornerRadius(8);

            UpdateRowHeights();
            BuildToolbar();
            _contentBorder.Child = BuildFiltersContent();
            BuildFloatingActions();
        }

        // Persist buffered edits when the window closes — the footer Apply button
        // was removed, so this is the catch-all save path (Create also saves).
        // Only writes when the buffer actually changed since load (dirty check).
        protected override void OnClosed(EventArgs e)
        {
            if (_filterTrades != null && SerializeTrades(_filterTrades) != _filtersSnapshot)
            {
                AutoFiltersSettings.Instance.Trades = _filterTrades;
                AutoFiltersSettings.Instance.Save();
            }
            base.OnClosed(e);
        }

        // Serializes the trade buffer for a cheap structural dirty comparison.
        // On failure we return a unique token so the window saves (never loses edits).
        private static string SerializeTrades(List<FilterTradeConfig>? trades)
        {
            if (trades == null) return "";
            try
            {
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(List<FilterTradeConfig>));
                using (var sw = new System.IO.StringWriter())
                {
                    xs.Serialize(sw, trades);
                    return sw.ToString();
                }
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("FiltersSettingsWindow.SerializeTrades", ex);
                return Guid.NewGuid().ToString(); // treat as dirty → save
            }
        }

        private void UpdateRowHeights()
        {
            if (_root == null) return;
            _root.RowDefinitions[0].Height = new GridLength(LemoineSettings.Instance.ToolbarHeight);
        }

        // ── Toolbar ───────────────────────────────────────────────────────────
        // Identical structure to the Legend window: [⚙ icon + title] … [× close].
        // The "Create Filters" action moved to the floating bottom-right pill.
        private void BuildToolbar()
        {
            _toolbarBorder.BorderThickness = new Thickness(0);

            var closeBtn = BuildFlatButton("×");
            closeBtn.SetResourceReference(Button.ForegroundProperty, "LemoineTextDim");
            closeBtn.Click += (s, e) => Close();

            var rightPanel = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rightPanel.Children.Add(closeBtn);

            _toolbarBorder.Child = new Controls.LemoineTitleBar
            {
                Title        = "Auto Filters",
                IconGlyph    = "⚙",
                RightContent = rightPanel,
            };
        }

        private void CreateFilters()
        {
            // Save current edits to settings before firing the event
            if (_filterTrades != null)
            {
                AutoFiltersSettings.Instance.Trades = _filterTrades;
                AutoFiltersSettings.Instance.Save();
            }

            var handler = App.AutoFiltersHandler;
            var evt     = App.AutoFiltersEvent;
            if (handler == null || evt == null)
            {
                FlashStatus("Event handler unavailable.");
                return;
            }

            handler.CreateOnly          = true;
            handler.SelectedDisciplines = new List<string>();
            handler.SelectedLinkTitles  = new List<string>();
            handler.PushLog             = null;
            handler.OnProgress          = null;

            // ⚠ OnComplete is invoked on Revit's main thread — marshal back to this STA dispatcher.
            var windowDispatcher = Dispatcher;
            handler.OnComplete = (pass, fail, skip, removed) =>
                windowDispatcher.BeginInvoke(new Action(() =>
                {
                    string removedSuffix = removed > 0 ? $", {removed} removed." : ".";
                    if (fail == 0 && pass > 0)
                        FlashStatus($"{pass} filter(s) created{removedSuffix}");
                    else if (fail == 0 && removed > 0)
                        FlashStatus($"{removed} filter(s) removed.");
                    else if (fail == 0)
                        FlashStatus("Filters up to date.");
                    else if (pass > 0)
                        FlashStatus($"{pass} created, {fail} failed{removedSuffix}");
                    else
                        FlashStatus("Failed — check Revit journal.");
                }));

            evt.Raise();
            FlashStatus("Creating filters…");
        }

        // ── Floating bottom-right action pills ──────────────────────────────────
        private void BuildFloatingActions()
        {
            _fStatusChip = LemoineControlStyles.BuildStatusChip(out _fStatusText);
            _fStatusChip.HorizontalAlignment = HorizontalAlignment.Right;
            _fStatusChip.Margin              = new Thickness(0, 0, 0, 6);

            var createPill = LemoineControlStyles.BuildActionPill("Create Filters", primary: true, () => CreateFilters());
            createPill.ToolTip = "Create/update the project's view filters from these trades";

            var stack = new StackPanel
            {
                Orientation         = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Bottom,
            };
            stack.Children.Add(_fStatusChip);
            stack.Children.Add(createPill);

            _floatingSlot.Content = stack;

            // Reserve space at the bottom of the editor so its last card clears the pill.
            _floatingSlot.SizeChanged += (s, e) => ApplyFloatingInset(e.NewSize.Height);
            ApplyFloatingInset(_floatingSlot.ActualHeight);
        }

        // Insets the editor's bottom so content isn't hidden behind the floating pill.
        private void ApplyFloatingInset(double pillsHeight)
        {
            if (_fEditorBorder == null) return;
            double inset = Math.Max(0, pillsHeight) + 16;
            _fEditorBorder.Padding = new Thickness(0, 0, 0, inset);
        }

        private void FlashStatus(string msg)
        {
            if (_fStatusText == null) return;
            _fStatusText.Text = msg;
            if (_fStatusChip != null) _fStatusChip.Visibility = Visibility.Visible;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, e) =>
            {
                _fStatusText.Text = "";
                if (_fStatusChip != null) _fStatusChip.Visibility = Visibility.Collapsed;
                timer.Stop();
            };
            timer.Start();
        }

        // ═════════════════════════════════════════════════════════════════════
        // Shared helpers (used by the partial Filters class)
        // ═════════════════════════════════════════════════════════════════════

        private static SolidColorBrush BrushFromHex(string? hex) =>
            BrushHelper.BrushFromHex(hex, LemoineTheme.FallbackGrey);

        private static Color HexToMediaColor(string hex) =>
            BrushHelper.ColorFromHex(hex, LemoineTheme.FallbackGrey);

        private static TextBlock MiniLabel(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 0) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }

        private static Button BuildFlatButton(string label) =>
            LemoineControlStyles.BuildButton(label, LemoineControlStyles.LemoineButtonVariant.Ghost);

        private static Button FlatSmBtn(string label) =>
            LemoineControlStyles.BuildSmallButton(label, LemoineControlStyles.LemoineButtonVariant.Ghost);

        private static ComboBox BuildAutoCompleteBox(
            string[] items, string initial, Action<string> onChange, double width = 200)
        {
            var combo = new ComboBox
            {
                IsEditable          = true,
                IsTextSearchEnabled = false,
                StaysOpenOnEdit     = true,
                MaxDropDownHeight   = 200,
                Width               = width,
                Text                = initial,
                ItemsSource         = items,
            };
            combo.SetResourceReference(ComboBox.BackgroundProperty,  "LemoineSelectBg");
            combo.SetResourceReference(ComboBox.ForegroundProperty,  "LemoineText");
            combo.SetResourceReference(ComboBox.FontSizeProperty,    "LemoineFS_SM");
            combo.SetResourceReference(ComboBox.FontFamilyProperty,  "LemoineMonoFont");
            combo.SetResourceReference(ComboBox.BorderBrushProperty, "LemoineBorderMid");

            bool suppressing = false;

            combo.PreviewTextInput += (s, e) =>
            {
                if (suppressing) return;
                combo.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (suppressing) return;
                    string typed = combo.Text;
                    var innerTb = combo.Template?.FindName("PART_EditableTextBox", combo) as TextBox;
                    int caret = innerTb != null ? innerTb.SelectionStart + innerTb.SelectionLength : typed.Length;
                    var filtered = items.Where(i => i.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();

                    suppressing = true;
                    combo.ItemsSource = filtered.Length > 0 ? (System.Collections.IEnumerable)filtered : items;
                    combo.Text = typed;
                    if (innerTb != null) innerTb.SelectionStart = Math.Min(caret, typed.Length);
                    combo.IsDropDownOpen = filtered.Length > 0;
                    suppressing = false;
                }));
            };

            combo.SelectionChanged += (s, e) =>
            {
                if (!suppressing && combo.SelectedItem is string sel)
                    onChange(sel);
            };

            return combo;
        }

        private UIElement BuildTrashConfirmButton(string confirmLabel, Action onConfirm)
        {
            var btn = new Border
            {
                Cursor              = Cursors.Hand,
                Padding             = new Thickness(5, 5, 5, 5),
                BorderThickness     = new Thickness(1),
                CornerRadius        = new CornerRadius(3),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background          = Brushes.Transparent,
            };
            btn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var icon = new TextBlock
            {
                Text                = "",
                FontFamily          = new FontFamily("Segoe MDL2 Assets"),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment       = TextAlignment.Center,
                IsHitTestVisible    = false,
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            icon.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            btn.Child = icon;

            btn.MouseEnter += (s, e) =>
            {
                btn.SetResourceReference(Border.BorderBrushProperty, "LemoineRed");
                icon.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            };
            btn.MouseLeave += (s, e) =>
            {
                btn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                icon.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            };
            btn.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                var popup = new Popup
                {
                    PlacementTarget    = btn,
                    Placement          = PlacementMode.Bottom,
                    StaysOpen          = false,
                    AllowsTransparency = false,
                };
                var outer = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(4),
                    Padding         = new Thickness(10, 8, 10, 8),
                    Margin          = new Thickness(0, 2, 0, 0),
                };
                outer.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
                outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
                var confirmBtn = FlatSmBtn(confirmLabel);
                confirmBtn.SetResourceReference(Button.ForegroundProperty,  "LemoineRed");
                confirmBtn.SetResourceReference(Button.BorderBrushProperty, "LemoineRed");
                confirmBtn.Click += (ss, ee) => { popup.IsOpen = false; onConfirm(); };
                var cancelBtn = FlatSmBtn("Cancel");
                cancelBtn.Margin = new Thickness(6, 0, 0, 0);
                cancelBtn.Click += (ss, ee) => popup.IsOpen = false;
                var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
                btnRow.Children.Add(confirmBtn);
                btnRow.Children.Add(cancelBtn);
                outer.Child  = btnRow;
                popup.Child  = outer;
                popup.IsOpen = true;
            };
            return btn;
        }
    }
}
