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
        private TextBlock?   _fStatusText;     // transient status (template load/save/import/export)
        private Border?      _fStatusChip;
        private ScrollViewer? _fRuleScroll;     // for auto-scroll-to-new on add
        private ScrollViewer? _fTradeScroll;
        private string       _filtersSnapshot = ""; // serialized buffer at load (dirty check)
        private Border?     _fActiveRowBorder;
        private TextBlock?  _fActiveNameTb;
        private Border?     _fActiveColorDot;  // active rule-row swatch — repainted live on FG colour change
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

            // Named handlers (not lambdas) so they can be detached in OnClosed — a leaked
            // subscription to this STA window after its dispatcher has shut down crashes/hangs
            // Revit on the next theme change.
            LemoineSettings.Instance.ThemeChanged  += OnThemeChanged;
            LemoineSettings.Instance.UiSizeChanged += OnUiSizeChanged;
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
                LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 8);
                UpdateRowHeights();
            }));
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
            BuildFloatingStatus();
        }

        // Persist buffered edits and auto-create the project's filters when the window closes.
        // There is no Create button anymore — closing the window is the single commit point.
        // Filters are (re)generated only when something changed since load, or when the saved
        // manifest no longer matches the rules (missing or orphaned filters). Generation runs
        // on Revit's main thread via the external event; any failures are reported there by a
        // TaskDialog, because this STA window is already tearing down.
        protected override void OnClosed(EventArgs e)
        {
            LemoineSettings.Instance.ThemeChanged  -= OnThemeChanged;
            LemoineSettings.Instance.UiSizeChanged -= OnUiSizeChanged;

            if (_filterTrades != null)
            {
                bool dirty = SerializeTrades(_filterTrades) != _filtersSnapshot;
                if (dirty)
                {
                    AutoFiltersSettings.Instance.Trades = _filterTrades;
                    AutoFiltersSettings.Instance.Save();
                }

                var expected     = AutoFiltersSettings.ComputeExpectedFilterNames(_filterTrades);
                bool manifestStale = !expected.SetEquals(
                    AutoFiltersSettings.Instance.CreatedFilterNames ?? new List<string>());

                if ((dirty || manifestStale) && expected.Count > 0)
                {
                    // Only refresh the definitions of rules whose category/parameter/match
                    // definition actually changed in the menu — every other existing filter is
                    // left untouched so edits made outside the menu (Revit's own filter editor)
                    // survive. Missing filters are still (re)created and orphans removed.
                    var snapshotTrades = DeserializeTrades(_filtersSnapshot);
                    var changed = AutoFiltersSettings.ComputeChangedFilterNames(
                        snapshotTrades, _filterTrades);
                    RaiseAutoCreate(changed);
                }
            }

            base.OnClosed(e);
        }

        // Queues the filter-creation pass for Revit's main thread. Raise() returns immediately;
        // Revit runs the handler at its next idle moment, after this window has closed — which
        // is why failures are surfaced by the handler's own TaskDialog (ShowFailureDialog),
        // never marshalled back to this dispatcher.
        private void RaiseAutoCreate(HashSet<string>? changedFilterNames)
        {
            var handler = App.AutoFiltersHandler;
            var evt     = App.AutoFiltersEvent;
            if (handler == null || evt == null)
            {
                LemoineLog.Warn("FiltersSettingsWindow", "Auto-create skipped: event handler unavailable.");
                return;
            }

            handler.CreateOnly          = true;
            handler.ShowFailureDialog   = true;
            handler.ChangedFilterNames  = changedFilterNames;
            handler.SelectedDisciplines = new List<string>();
            handler.SelectedLinkTitles  = new List<string>();
            handler.PushLog             = null;
            handler.OnProgress          = null;
            handler.OnComplete          = null;

            evt.Raise();
        }

        // Creates and applies ONLY the active trade's filters to Revit's current view.
        // Persists the working buffer first so the handler reads the latest (possibly
        // just-merged) rules, then refreshes the dirty snapshot so OnClosed doesn't run a
        // second redundant pass. OverwriteFilterDefinition is forced true so an existing
        // filter created before a rule was merged is rebuilt with ALL its keywords — fixing
        // the "only one of the merged text values applied" symptom on re-apply.
        private void ApplyActiveTradeToView()
        {
            if (_filterTrades == null) return;
            var trade = _filterTrades.FirstOrDefault(t => t.Id == _fActiveTradeId);
            if (trade == null) { FlashStatus("No trade selected."); return; }
            if (trade.ExternallyManaged)
            {
                FlashStatus($"“{trade.Label}” is managed by another tool — apply it from there.");
                return;
            }

            var handler = App.AutoFiltersHandler;
            var evt     = App.AutoFiltersEvent;
            if (handler == null || evt == null)
            {
                LemoineLog.Warn("FiltersSettingsWindow", "Apply to view skipped: event handler unavailable.");
                FlashStatus("Apply unavailable.");
                return;
            }

            // Persist current edits so the handler (which reads AutoFiltersSettings.Instance)
            // sees them, and keep the snapshot in sync so closing won't re-run needlessly.
            AutoFiltersSettings.Instance.Trades = _filterTrades;
            AutoFiltersSettings.Instance.Save();
            _filtersSnapshot = SerializeTrades(_filterTrades);

            handler.CreateOnly                = false;
            handler.OverwriteFilterDefinition = true;
            handler.KeepExistingOverrides     = false;
            handler.ShowFailureDialog         = true;
            handler.ChangedFilterNames        = null;
            handler.SelectedDisciplines       = new List<string> { trade.Label };
            handler.SelectedLinkTitles        = new List<string>();
            handler.PushLog                   = null;
            handler.OnProgress                = null;
            handler.OnComplete                = null;

            evt.Raise();
            FlashStatus($"Applying “{trade.Label}” to the active view…");
        }

        // ── Floating bottom-right status chip ───────────────────────────────────
        // The Create pill was removed (filters auto-create on close); the chip remains
        // to surface transient template messages (load / save / import / export).
        private void BuildFloatingStatus()
        {
            _fStatusChip = LemoineControlStyles.BuildStatusChip(out _fStatusText);
            _fStatusChip.HorizontalAlignment = HorizontalAlignment.Right;
            _fStatusChip.VerticalAlignment   = VerticalAlignment.Bottom;
            _fStatusChip.Visibility          = Visibility.Collapsed;

            _floatingSlot.Content = _fStatusChip;

            // Reserve space at the bottom of the editor so its last card clears the chip.
            _floatingSlot.SizeChanged += (s, e) => ApplyFloatingInset(e.NewSize.Height);
            ApplyFloatingInset(_floatingSlot.ActualHeight);
        }

        // Insets the editor's bottom so content isn't hidden behind the floating chip.
        private void ApplyFloatingInset(double chipHeight)
        {
            if (_fEditorBorder == null) return;
            double inset = Math.Max(0, chipHeight) + 16;
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

        // Reverses SerializeTrades for the load-time snapshot so the close pass can diff it
        // against the current buffer and refresh only the rules that actually changed. On any
        // failure we return an empty list, which makes ComputeChangedFilterNames treat every
        // current rule as changed — the safe fallback that preserves the old "refresh all".
        private static List<FilterTradeConfig> DeserializeTrades(string? xml)
        {
            if (string.IsNullOrEmpty(xml)) return new List<FilterTradeConfig>();
            try
            {
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(List<FilterTradeConfig>));
                using (var sr = new System.IO.StringReader(xml))
                    return (List<FilterTradeConfig>)xs.Deserialize(sr) ?? new List<FilterTradeConfig>();
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("FiltersSettingsWindow.DeserializeTrades", ex);
                return new List<FilterTradeConfig>();
            }
        }

        private void UpdateRowHeights()
        {
            if (_root == null) return;
            _root.RowDefinitions[0].Height = new GridLength(LemoineSettings.Instance.ToolbarHeight);
        }

        // ── Toolbar ───────────────────────────────────────────────────────────
        // Identical structure to the Legend window: [⚙ icon + title] … [× close].
        // Filters are created automatically on close — there is no Create action here.
        private void BuildToolbar()
        {
            _toolbarBorder.BorderThickness = new Thickness(0);

            var applyBtn = FlatSmBtn("Apply to view");
            applyBtn.VerticalAlignment = VerticalAlignment.Center;
            applyBtn.Margin            = new Thickness(0, 0, 8, 0);
            applyBtn.ToolTip           = "Create and apply the active trade's filters to the current view.";
            applyBtn.Click += (s, e) => ApplyActiveTradeToView();

            var closeBtn = BuildFlatButton("×");
            closeBtn.SetResourceReference(Button.ForegroundProperty, "LemoineTextDim");
            closeBtn.Click += (s, e) => Close();

            var rightPanel = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rightPanel.Children.Add(applyBtn);
            rightPanel.Children.Add(closeBtn);

            _toolbarBorder.Child = new Controls.LemoineTitleBar
            {
                Title        = "Auto Filters",
                IconGlyph    = "⚙",
                RightContent = rightPanel,
            };
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
