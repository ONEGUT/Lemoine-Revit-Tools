using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Automation;
using System.Windows.Shapes;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.Testing.LegendCreator;
using LemoineTools.Tools.Testing;
using LemoineTools.Tools.Ceilings;
using LemoineTools.Tools.LinkViews;

namespace LemoineTools.Lemoine
{
    public partial class GlobalSettingsWindow : Window
    {
        // ── Theme rows (General tab) ──────────────────────────────────────────
        private readonly List<(Border row, LemoineTheme theme)>  _themeRows = new List<(Border, LemoineTheme)>();
        private readonly List<(Border row, LemoineUiSize size)>  _sizeRows  = new List<(Border, LemoineUiSize)>();

        // ── Nav pill state ────────────────────────────────────────────────────
        private string _activeTabId = "general";
        private readonly Dictionary<string, Border> _navPills = new Dictionary<string, Border>();

        // ── Live project pattern lists (populated by OpenSettingsCommand) ───────
        internal IReadOnlyList<string> FillPatternNames { get; private set; } = Array.Empty<string>();
        internal IReadOnlyList<string> LinePatternNames { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Called by <c>OpenSettingsCommand</c> with pattern names queried from the active document.
        /// Safe to call before or after <see cref="Show"/>.
        /// </summary>
        internal void SetPatternLists(IEnumerable<string> fillPatterns, IEnumerable<string> linePatterns)
        {
            FillPatternNames = new System.Collections.ObjectModel.ReadOnlyCollection<string>(fillPatterns.ToList());
            LinePatternNames = new System.Collections.ObjectModel.ReadOnlyCollection<string>(linePatterns.ToList());
        }

        // ── Filter tab state ─────────────────────────────────────────────────
        private List<FilterTradeConfig>? _filterTrades;
        private string? _fActiveTradeId;
        private string? _fActiveRuleId;           // V3: rules live directly on trade
        // Panels refreshed in-place:
        private StackPanel?  _fRuleListPanel;     // left-panel rule rows
        private Border?      _fEditorBorder;      // right-panel 280px editor host
        private Border?      _fTradeSwitcherBorder; // trade header (dot+label+chevron+gear)
        private TextBlock?   _fStatusText;        // footer status label
        private StackPanel?  _filterToolbarBtns;  // shown only when Filters tab is active

        // ── Selected rule row border (kept to avoid full list rebuild on selection) ──
        private Border?     _fActiveRowBorder;   // the currently highlighted rule row
        private TextBlock?  _fActiveNameTb;      // name label in the active row (updated in-place by editor)

        // ── Active drag state (rule reorder) ─────────────────────────────────
        private string?  _dragRuleId;
        private Border?  _dragSourceBorder;   // the pill being dragged
        private int      _dragSourceOrigIdx;  // its original index in _fRuleListPanel
        private Point    _dragGhostClickOffset; // where inside the pill the user clicked

        // ── Editor re-entry guard ─────────────────────────────────────────────
        // Prevents chip Changed events fired during FRefreshRuleEditor from
        // triggering a re-entrant FRefreshRuleList call that clears the panel
        // while it's still being built.
        private bool _isRefreshingEditor;

        // ── Drag state: trade reorder ────────────────────────────────────────
        private string?  _dragTradeId;
        private Point    _tradeDragStart;
        // ── Drag ghost popup ──────────────────────────────────────────────────
        private Popup?   _dragGhost;

        // P/Invoke for cursor position (used by drag ghost)
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint pt);
        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }
        // ── Double-click rename timing ───────────────────────────────────────────
        private DateTime _lastClickTime   = DateTime.MinValue;
        private string?  _lastClickItemId;

        // ─────────────────────────────────────────────────────────────────────
        public GlobalSettingsWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;

            LemoineSettings.Instance.ThemeChanged += t => Dispatcher.Invoke(() =>
            {
                LemoineSettings.Instance.ApplyTo(Resources);
                Background = t.PageBg;
                _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                RefreshThemeRows();
            });

            LemoineSettings.Instance.UiSizeChanged += _ => Dispatcher.Invoke(() =>
            {
                LemoineSettings.Instance.ApplyScaleTo(Resources);
                UpdateRowHeights();
                RefreshSizeRows();
            });
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LemoineSettings.Instance.ApplyTo(Resources);
            LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 8);
            Background = LemoineSettings.Instance.ActiveTheme.PageBg;
            _root.SetResourceReference(Grid.BackgroundProperty, "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            UpdateRowHeights();
            BuildToolbar();
            BuildPillNav();
            BuildFooter();
            SwitchTab(_activeTabId);
            // Distinguish from LemoineSettingsWindow ("Appearance Settings") for screen readers
            AutomationProperties.SetName(this, "Manage Settings");
        }

        private void UpdateRowHeights()
        {
            if (_root == null) return;
            _root.RowDefinitions[0].Height = new GridLength(LemoineSettings.Instance.ToolbarHeight);
            _root.RowDefinitions[3].Height = new GridLength(LemoineSettings.Instance.FooterHeight);
        }        // ═════════════════════════════════════════════════════════════════════
        // Activate filters tab (called externally from gear-icon button)
        // ═════════════════════════════════════════════════════════════════════
        // Toolbar
        // ═════════════════════════════════════════════════════════════════════
        private void BuildToolbar()
        {
            // LemoineTitleBar provides: Surface background, Border bottom line, drag-to-move,
            // icon glyph, and title text. Only the right-side actions are built inline here.
            _toolbarBorder.BorderThickness = new Thickness(0);

            var closeBtn = BuildFlatButton("\u00d7");
            closeBtn.Margin = new Thickness(8, 0, 0, 0);
            closeBtn.SetResourceReference(Button.ForegroundProperty, "LemoineTextDim");
            closeBtn.Click += (s, e) => Close();

            // Filter-tab-only buttons — shown/hidden by SwitchTab()
            _filterToolbarBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Visibility = Visibility.Collapsed,
            };

            // Single "Templates ˅" button replaces the old Import / Export / Restore trio.
            // The dropdown popup is built in ShowTemplatesPopup() on the Filters partial class.
            var tbTemplates = FlatSmBtn("Templates  ˅");
            tbTemplates.Margin = new Thickness(0, 0, 6, 0);
            tbTemplates.Click += (s, e) => ShowTemplatesPopup(tbTemplates);
            _filterToolbarBtns.Children.Add(tbTemplates);
            DockPanel.SetDock(_filterToolbarBtns, Dock.Right);

            // Right slot: filter-tab buttons (shown/hidden per tab) + close button
            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rightPanel.Children.Add(_filterToolbarBtns);
            rightPanel.Children.Add(closeBtn);

            _toolbarBorder.Child = new Controls.LemoineTitleBar
            {
                Title        = "Settings",
                IconGlyph    = "⚙",   // ⚙ gear
                RightContent = rightPanel,
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        // Top pill navigation
        // ═════════════════════════════════════════════════════════════════════
        private static readonly (string Id, string Label)[] _navDefs =
        {
            ("general", "General"),
            ("filters", "Filters / Color"),
            ("t03",     "Ceiling Heatmap"),
            ("t04",     "Link Views"),
            ("t08",     "Legend Creator"),
            ("tx",      "Batch Export"),
            ("ty",      "Batch Dimension"),
            ("tz",      "Create Sheets"),
            ("tw",      "Sheet Pack"),
        };

        private const int _pillNavVisible = 6;

        private void BuildPillNav()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            _navPills.Clear();

            int vis = Math.Min(_pillNavVisible, _navDefs.Length);
            for (int i = 0; i < vis; i++)
            {
                var (id, label) = _navDefs[i];
                var pill = BuildNavPill(id, label);
                _navPills[id] = pill;
                panel.Children.Add(pill);
            }

            if (_navDefs.Length > vis)
            {
                int overflow = _navDefs.Length - vis;
                panel.Children.Add(BuildMorePill($"More +{overflow} ▾"));
            }

            _pillNavBorder.Child = panel;
        }

        private Border BuildNavPill(string tabId, string label)
        {
            bool active = tabId == _activeTabId;

            var pill = new Border
            {
                Cursor          = Cursors.Hand,
                CornerRadius    = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(8, 4, 8, 4),
                Margin          = new Thickness(0, 0, 6, 0),
            };
            pill.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            if (active)
            {
                pill.SetResourceReference(Border.BackgroundProperty,  "LemoineAccent");
                pill.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            }
            else
            {
                pill.SetResourceReference(Border.BackgroundProperty, "LemoineBg");
            }

            var lbl = new TextBlock
            {
                Text       = label,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            if (active) lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineBg");
            else        lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            pill.Child = lbl;

            pill.MouseLeftButtonDown += (s, e) => SwitchTab(tabId);
            pill.MouseEnter += (s, e) =>
            {
                if (tabId != _activeTabId)
                    pill.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            };
            pill.MouseLeave += (s, e) =>
            {
                if (tabId != _activeTabId)
                    pill.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            };
            return pill;
        }

        private Border BuildMorePill(string label)
        {
            var pill = new Border
            {
                Cursor          = Cursors.Hand,
                CornerRadius    = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(8, 4, 8, 4),
                Margin          = new Thickness(0, 0, 6, 0),
            };
            pill.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            pill.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var lbl = new TextBlock { Text = label };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            pill.Child = lbl;

            pill.MouseLeftButtonDown += (s, e) => ShowMorePillPopup(pill);
            return pill;
        }

        private void ShowMorePillPopup(Border anchor)
        {
            var popup = new Popup
            {
                PlacementTarget    = anchor,
                Placement          = PlacementMode.Bottom,
                StaysOpen          = false,
                AllowsTransparency = false,
            };
            var outer = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(4),
                Margin          = new Thickness(0, 2, 0, 0),
            };
            outer.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var stack = new StackPanel();
            for (int i = _pillNavVisible; i < _navDefs.Length; i++)
            {
                var (tabId, tabLabel) = _navDefs[i];
                var captId = tabId;
                var item = new Border
                {
                    Cursor       = Cursors.Hand,
                    CornerRadius = new CornerRadius(4),
                    Padding      = new Thickness(12, 6, 12, 6),
                    Background   = Brushes.Transparent,
                };
                var itemLbl = new TextBlock { Text = tabLabel };
                itemLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                itemLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                itemLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                item.Child = itemLbl;
                item.MouseLeftButtonDown += (s, e) => { popup.IsOpen = false; SwitchTab(captId); };
                item.MouseEnter += (s, e) => item.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
                item.MouseLeave += (s, e) => item.Background = Brushes.Transparent;
                stack.Children.Add(item);
            }
            outer.Child = stack;
            popup.Child = outer;
            popup.IsOpen = true;
        }

        private void SwitchTab(string tabId)
        {
            _activeTabId = tabId;

            // Refresh nav pill styles
            foreach (var (id, _) in _navDefs)
            {
                if (!_navPills.TryGetValue(id, out var pill)) continue;
                bool active = id == tabId;
                if (active)
                {
                    pill.SetResourceReference(Border.BackgroundProperty,  "LemoineAccent");
                    pill.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                }
                else
                {
                    pill.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
                    pill.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                }

                if (pill.Child is TextBlock lbl)
                {
                    lbl.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                    if (active) lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineBg");
                    else        lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                }
            }

            // Show filter toolbar buttons only on the filters tab
            if (_filterToolbarBtns != null)
                _filterToolbarBtns.Visibility = tabId == "filters"
                    ? Visibility.Visible : Visibility.Collapsed;

            // Build content for the selected tab
            UIElement content;
            switch (tabId)
            {
                case "general":  content = BuildGeneralContent();  break;
                case "filters":  content = BuildFiltersContent();  break;
                case "t03":      content = BuildSpecContent(new CeilingHeatmapViewModel(null, null), "Ceiling Heatmap"); break;
                case "t04":      content = BuildSpecContent(new LinkViewsLevelViewModel(null, null, null, null, null), "Link Views"); break;
                case "t08":      content = LegendCreatorTabContent.BuildContent(this); break;
                case "tx":       content = BuildSpecContent(new BatchExportViewModel(null, null, null), "Batch Export"); break;
                case "ty":       content = BuildSpecContent(new BatchDimensionViewModel(null, null, null, null), "Batch Dimension"); break;
                case "tz":       content = BuildSpecContent(new CreateSheetsViewModel(), "Create Sheets"); break;
                case "tw":       content = BuildSpecContent(new SheetPackViewModel(), "Sheet Pack"); break;
                default:         content = BuildNoSettingsContent(); break;
            }

            _contentBorder.Child = content;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Footer
        // ═════════════════════════════════════════════════════════════════════
        private void BuildFooter()
        {
            _footerBorder.SetResourceReference(Border.PaddingProperty, "LemoineTh_FooterPad");

            _fStatusText = new TextBlock
            {
                Text = "", VerticalAlignment = VerticalAlignment.Center,
                FontStyle = FontStyles.Italic,
            };
            _fStatusText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _fStatusText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineGreen");
            _fStatusText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var applyBtn = BuildFlatButton("Apply");
            applyBtn.Margin = new Thickness(0, 0, 6, 0);
            applyBtn.Click  += (s, e) => ApplyCurrentTab();

            var closeBtn = BuildFlatButton("Close");
            closeBtn.Click += (s, e) => Close();

            var dp = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            var btnStack = new StackPanel { Orientation = Orientation.Horizontal };
            btnStack.Children.Add(applyBtn);
            btnStack.Children.Add(closeBtn);
            DockPanel.SetDock(btnStack, Dock.Right);
            dp.Children.Add(btnStack);
            dp.Children.Add(_fStatusText);

            _footerBorder.Child = dp;
        }

        private void ApplyCurrentTab()
        {
            switch (_activeTabId)
            {
                case "filters":
                    if (_filterTrades != null)
                    {
                        AutoFiltersSettings.Instance.Trades = _filterTrades;
                        AutoFiltersSettings.Instance.Save();
                        FlashStatus("Saved.");
                    }
                    break;
                case "t08":
                    LegendCreatorTabContent.Apply();
                    FlashStatus("Legend layout saved.");
                    break;
                default:
                    // General and spec tabs apply changes live — nothing extra needed
                    FlashStatus("Applied.");
                    break;
            }
        }

        private void FlashStatus(string msg)
        {
            if (_fStatusText == null) return;
            _fStatusText.Text = msg;
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            timer.Tick += (s, e) => { _fStatusText.Text = ""; timer.Stop(); };
            timer.Start();
        }

        // ═════════════════════════════════════════════════════════════════════
        // General tab — theme + UI size
        // ═════════════════════════════════════════════════════════════════════
        // Spec-based tool settings tab
        // ═════════════════════════════════════════════════════════════════════
        // General tab helpers — theme rows / size rows
        // ═════════════════════════════════════════════════════════════════════
        // Spec control builder (for tool tabs)
        // ═════════════════════════════════════════════════════════════════════
        // UI helpers
        // ═════════════════════════════════════════════════════════════════════

        private static UIElement BuildColorBar(List<FilterRuleConfig> rules, int height)
        {
            var bars = new Grid { Height = height };
            if (rules.Count == 0)
            {
                var empty = new Border { Height = height };
                empty.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
                return empty;
            }
            for (int i = 0; i < rules.Count; i++)
                bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < rules.Count; i++)
            {
                var seg = new Rectangle { Fill = BrushHelper.BrushFromHex(rules[i].CutColor, LemoineTheme.FallbackGrey) };
                Grid.SetColumn(seg, i);
                bars.Children.Add(seg);
            }
            var outer = new Border { CornerRadius = new CornerRadius(1), Child = bars, ClipToBounds = true };
            outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            outer.BorderThickness = new Thickness(1);
            return outer;
        }

        // Thin wrapper — delegates to the shared BrushHelper so all partial-class callers
        // continue to compile without needing their call sites updated.
        private static SolidColorBrush BrushFromHex(string? hex) =>
            BrushHelper.BrushFromHex(hex, LemoineTheme.FallbackGrey);

        private static Color HexToMediaColor(string hex)
            => BrushHelper.ColorFromHex(hex, LemoineTheme.FallbackGrey);

        private TextBlock ContentHeader(string text)
        {
            var tb = new TextBlock { Text = text, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 18) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XL");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock MiniLabel(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 0) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }

        private static TextBlock SubLabel(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 8) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }

        // Delegates to the shared factory — all Lemoine windows now use the same
        // ControlTemplate and resource references for flat buttons.
        private static Button BuildFlatButton(string label) =>
            LemoineControlStyles.BuildButton(label, LemoineControlStyles.LemoineButtonVariant.Ghost);

        // Delegates to the shared factory — height, padding, and font all scale with LemoineH_BtnSm / LemoineTh_BtnSmPad.
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
                // Delay until after WPF commits the typed character to combo.Text
                combo.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (suppressing) return;
                    string typed = combo.Text;
                    // Capture caret from the inner TextBox (ComboBox doesn't expose SelectionStart directly)
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

        // ── Drag ghost helpers ─────────────────────────────────────────────────
        /// <summary>Converts physical screen pixels (from GetCursorPos) to WPF logical units.</summary>
        private Point ToLogicalPoint(int physX, int physY)
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                var m = source.CompositionTarget.TransformFromDevice;
                return new Point(physX * m.M11, physY * m.M22);
            }
            return new Point(physX, physY);
        }

        private void ShowDragGhost(string label, string subtext, string colorHex)
        {
            // Resolve theme resources from a live element.
            // Popup children have no logical parent, so SetResourceReference never
            // resolves inside them — use TryFindResource on a live tree element instead.
            FrameworkElement res = _fRuleListPanel ?? (FrameworkElement)this;
            Brush BrushRes(string key, Brush fallback)
                => res.TryFindResource(key) as Brush ?? fallback;
            double DoubleRes(string key, double fallback)
                => res.TryFindResource(key) is double d ? d : fallback;
            FontFamily FontRes(string key)
                => res.TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI");

            var dot = new Ellipse
            {
                Width             = 14, Height = 14,
                Fill              = BrushFromHex(colorHex),
                Stroke            = BrushRes("LemoineBorder", Brushes.Gray),
                StrokeThickness   = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 9, 0),
            };

            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var lbl = new TextBlock
            {
                Text       = label,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushRes("LemoineText",   Brushes.Black),
                FontSize   = DoubleRes("LemoineFS_SM", 12),
                FontFamily = FontRes("LemoineUiFont"),
            };
            nameStack.Children.Add(lbl);
            if (!string.IsNullOrEmpty(subtext))
            {
                var sub = new TextBlock
                {
                    Text         = subtext,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth     = 200,
                    Foreground   = BrushRes("LemoineTextDim",  Brushes.Gray),
                    FontSize     = DoubleRes("LemoineFS_XS",   11),
                    FontFamily   = FontRes("LemoineMonoFont"),
                };
                nameStack.Children.Add(sub);
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot);
            row.Children.Add(nameStack);
            var ghost = new Border
            {
                Child           = row,
                Padding         = new Thickness(10, 7, 14, 7),
                CornerRadius    = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Opacity         = 0.92,
                Background      = BrushRes("LemoineSurface", Brushes.White),
                BorderBrush     = BrushRes("LemoineAccent",  Brushes.CornflowerBlue),
            };
            HideDragGhost();
            GetCursorPos(out var pt);
            var lpt = ToLogicalPoint(pt.X, pt.Y);
            _dragGhost = new Popup
            {
                AllowsTransparency = true,
                IsHitTestVisible   = false,
                Placement          = PlacementMode.AbsolutePoint,
                HorizontalOffset   = lpt.X + 14,
                VerticalOffset     = lpt.Y + 14,
                StaysOpen          = true,
                Child              = ghost,
            };
            _dragGhost.IsOpen = true;
        }

        /// <summary>
        /// Snapshots <paramref name="element"/> at the correct display DPI and shows it
        /// as the drag ghost, positioned so the cursor sits at <paramref name="clickOffset"/>
        /// within the ghost — the pill appears to be grabbed from where the user clicked.
        /// </summary>
        private void ShowDragGhostFromElement(FrameworkElement element, Point clickOffset)
        {
            if (element.ActualWidth < 1 || element.ActualHeight < 1)
            {
                // Element may have just been added to the panel and not yet arranged
                // (layout runs at Render priority, MouseMove fires at Input priority).
                // Force a measure/arrange so we can snapshot the correct size.
                double availW = _fRuleListPanel?.ActualWidth > 0
                    ? _fRuleListPanel.ActualWidth : 300;
                element.Measure(new Size(availW, double.PositiveInfinity));
                element.Arrange(new Rect(element.DesiredSize));
                if (element.ActualWidth < 1 || element.ActualHeight < 1) return;
            }

            // Resolve display DPI so the snapshot is crisp on high-DPI screens
            double dpiX = 96, dpiY = 96;
            var ps = PresentationSource.FromVisual(element);
            if (ps?.CompositionTarget != null)
            {
                dpiX = 96 * ps.CompositionTarget.TransformToDevice.M11;
                dpiY = 96 * ps.CompositionTarget.TransformToDevice.M22;
            }
            int pixW = Math.Max(1, (int)(element.ActualWidth  * dpiX / 96));
            int pixH = Math.Max(1, (int)(element.ActualHeight * dpiY / 96));

            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                pixW, pixH, dpiX, dpiY, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(element);

            var img = new System.Windows.Controls.Image
            {
                Source = bmp,
                Width  = element.ActualWidth,   // display at logical size
                Height = element.ActualHeight,
            };
            var ghost = new Border
            {
                Child   = img,
                Opacity = 0.82,
                Effect  = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius  = 14, Opacity = 0.40, ShadowDepth = 4, Direction = 270,
                },
            };

            _dragGhostClickOffset = clickOffset; // persist for UpdateDragGhostPos
            HideDragGhost();
            GetCursorPos(out var pt);
            var lpt = ToLogicalPoint(pt.X, pt.Y);
            _dragGhost = new Popup
            {
                AllowsTransparency = true,
                IsHitTestVisible   = false,
                Placement          = PlacementMode.AbsolutePoint,
                HorizontalOffset   = lpt.X - clickOffset.X,
                VerticalOffset     = lpt.Y - clickOffset.Y,
                StaysOpen          = true,
                Child              = ghost,
            };
            _dragGhost.IsOpen = true;
        }

        private void HideDragGhost()
        {
            if (_dragGhost != null) { _dragGhost.IsOpen = false; _dragGhost = null; }
        }

        private void UpdateDragGhostPos()
        {
            if (_dragGhost == null) return;
            GetCursorPos(out var pt);
            var lpt = ToLogicalPoint(pt.X, pt.Y);
            _dragGhost.HorizontalOffset = lpt.X - _dragGhostClickOffset.X;
            _dragGhost.VerticalOffset   = lpt.Y - _dragGhostClickOffset.Y;
        }

        // ── Reusable trashcan button with inline confirmation popup ────────────
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
                Text              = "",
                FontFamily        = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible  = false,
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            icon.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            // Force line-height to match LemoineUiFont (Segoe UI) so button height equals the pencil button
            icon.Loaded += (s2, e2) =>
            {
                if (icon.TryFindResource("LemoineFS_SM") is double sm)
                    icon.LineHeight = Math.Ceiling(sm * 1.35);
            };
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
