using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.ScopeBoxes;

using WpfGrid       = System.Windows.Controls.Grid;
using WpfVisibility = System.Windows.Visibility;
using WpfColor      = System.Windows.Media.Color;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// Scope Box Manager — Filters-window-style management surface (not a step flow):
    /// a sidebar listing every scope box with its usage (views + datums), a live editor
    /// for the active box (rename, assign to views via the browser-tree picker, assign
    /// to grids/levels/reference planes, delete), and bulk actions over checked boxes
    /// (slot rename, delete unused). Every action runs on Revit's main thread through
    /// <see cref="ScopeBoxManagerRunHandler"/> and refreshes from a fresh usage scan.
    /// </summary>
    public partial class ScopeBoxManagerWindow : Window
    {
        // ── State ─────────────────────────────────────────────────────────────
        private ManagerScanResult? _scan;
        private long               _activeBoxId = -1;
        private readonly HashSet<long> _checkedBoxes   = new HashSet<long>();
        private readonly HashSet<long> _checkedViews   = new HashSet<long>();
        private readonly HashSet<long> _checkedDatums  = new HashSet<long>();
        private string _filterMode = "All";   // "All" | "Used" | "Unused" (logic tokens)
        private bool   _busy;

        // ── UI handles ────────────────────────────────────────────────────────
        private StackPanel?   _boxListPanel;
        private TextBlock?    _sideHeader;
        private StackPanel?   _mainPanel;
        private Border?       _mainHost;
        private Border?       _statusChip;
        private TextBlock?    _statusText;
        private DispatcherTimer? _statusTimer;

        public ScopeBoxManagerWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;

            // Named handlers (not lambdas) so they can be detached in OnClosed — a leaked
            // subscription to this STA window after its dispatcher has shut down crashes/
            // hangs Revit on the next theme change (see CLAUDE.md).
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

        protected override void OnClosed(EventArgs e)
        {
            LemoineSettings.Instance.ThemeChanged  -= OnThemeChanged;
            LemoineSettings.Instance.UiSizeChanged -= OnUiSizeChanged;
            _statusTimer?.Stop();
            _statusTimer = null;

            // The scan/run handlers are session-long statics — null their callbacks so this
            // closed window (and its whole visual tree) isn't retained until the next run.
            var scan = App.ScopeBoxManagerScanHandler;
            if (scan != null) { scan.OnScanComplete = null; scan.OnError = null; }
            var run = App.ScopeBoxManagerRunHandler;
            if (run != null) run.OnActionComplete = null;

            base.OnClosed(e);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LemoineSettings.Instance.ApplyTo(Resources);
            LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 8);
            Background = LemoineSettings.Instance.ActiveTheme.PageBg;
            _root.SetResourceReference(WpfGrid.BackgroundProperty, "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _outerBorder.CornerRadius = new CornerRadius(8);

            UpdateRowHeights();
            BuildToolbar();
            _contentBorder.Child = BuildLayout();
            BuildFloatingStatus();
            RaiseScan();
        }

        private void UpdateRowHeights()
        {
            if (_root == null) return;
            _root.RowDefinitions[0].Height = new GridLength(LemoineSettings.Instance.ToolbarHeight);
        }

        // ── Toolbar ───────────────────────────────────────────────────────────
        private void BuildToolbar()
        {
            _toolbarBorder.BorderThickness = new Thickness(0);

            var refreshBtn = LemoineControlStyles.BuildSmallButton(
                LemoineStrings.T("scopeBoxes.manager.toolbar.refresh"));
            refreshBtn.VerticalAlignment = VerticalAlignment.Center;
            refreshBtn.Margin            = new Thickness(0, 0, 12, 0);
            refreshBtn.ToolTip           = LemoineStrings.T("scopeBoxes.manager.toolbar.refreshTooltip");
            refreshBtn.Click += (s, e) => RaiseScan();

            var closeBtn = LemoineControlStyles.BuildButton("×");
            closeBtn.SetResourceReference(Button.ForegroundProperty, "LemoineTextDim");
            closeBtn.Click += (s, e) => Close();

            var rightPanel = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rightPanel.Children.Add(refreshBtn);
            rightPanel.Children.Add(closeBtn);

            _toolbarBorder.Child = new LemoineTitleBar
            {
                Title        = LemoineStrings.T("scopeBoxes.manager.toolbar.title"),
                IconGlyph    = char.ConvertFromUtf32(0xE7B8),  // Segoe MDL2: CubeShape
                RightContent = rightPanel,
            };
        }

        // ── Layout skeleton ───────────────────────────────────────────────────
        private FrameworkElement BuildLayout()
        {
            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Sidebar
            var side = new Border { BorderThickness = new Thickness(0, 0, 1, 0) };
            side.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            side.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            WpfGrid.SetColumn(side, 0);

            var sideDock = new DockPanel { LastChildFill = true };

            // Header + filter chips
            var top = new StackPanel { Margin = new Thickness(12, 10, 12, 6) };
            _sideHeader = new TextBlock();
            _sideHeader.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _sideHeader.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _sideHeader.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            top.Children.Add(_sideHeader);

            var chipRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 8, 0, 0),
            };
            void AddFilterChip(string token, string label)
            {
                var b = LemoineControlStyles.BuildSmallButton(label,
                    _filterMode == token
                        ? LemoineControlStyles.LemoineButtonVariant.Primary
                        : LemoineControlStyles.LemoineButtonVariant.Ghost);
                b.Margin = new Thickness(0, 0, 6, 0);
                b.Click += (s, e) =>
                {
                    _filterMode = token;
                    // Rebuild the chip row highlight + list
                    chipRow.Children.Clear();
                    AddFilterChip("All",    LemoineStrings.T("scopeBoxes.manager.side.filterAll"));
                    AddFilterChip("Used",   LemoineStrings.T("scopeBoxes.manager.side.filterUsed"));
                    AddFilterChip("Unused", LemoineStrings.T("scopeBoxes.manager.side.filterUnused"));
                    RefreshSidebar();
                };
                chipRow.Children.Add(b);
            }
            AddFilterChip("All",    LemoineStrings.T("scopeBoxes.manager.side.filterAll"));
            AddFilterChip("Used",   LemoineStrings.T("scopeBoxes.manager.side.filterUsed"));
            AddFilterChip("Unused", LemoineStrings.T("scopeBoxes.manager.side.filterUnused"));
            top.Children.Add(chipRow);

            DockPanel.SetDock(top, Dock.Top);
            sideDock.Children.Add(top);

            // Footer bulk actions
            var foot = new StackPanel { Margin = new Thickness(12, 8, 12, 12) };
            var renameBtn = LemoineControlStyles.BuildSmallButton(
                LemoineStrings.T("scopeBoxes.manager.side.renameChecked"));
            renameBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            renameBtn.Margin = new Thickness(0, 0, 0, 6);
            renameBtn.Click += (s, e) => ShowRenameOverlay();
            foot.Children.Add(renameBtn);

            var delUnusedBtn = LemoineControlStyles.BuildSmallButton(
                LemoineStrings.T("scopeBoxes.manager.side.deleteUnused"),
                LemoineControlStyles.LemoineButtonVariant.Danger);
            delUnusedBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            delUnusedBtn.Click += (s, e) => ShowDeleteUnusedOverlay();
            foot.Children.Add(delUnusedBtn);

            var footNote = new TextBlock
            {
                Text = LemoineStrings.T("scopeBoxes.manager.side.footNote"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            };
            footNote.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            footNote.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            footNote.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            foot.Children.Add(footNote);

            DockPanel.SetDock(foot, Dock.Bottom);
            sideDock.Children.Add(foot);

            // Box list (fill)
            _boxListPanel = new StackPanel();
            var sideScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _boxListPanel,
            };
            sideDock.Children.Add(sideScroll);

            side.Child = sideDock;
            grid.Children.Add(side);

            // Main editor
            _mainPanel = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
            var mainScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _mainPanel,
            };
            _mainHost = new Border { Child = mainScroll };
            WpfGrid.SetColumn(_mainHost, 1);
            grid.Children.Add(_mainHost);

            return grid;
        }

        // ── Floating status chip ──────────────────────────────────────────────
        private void BuildFloatingStatus()
        {
            _statusChip = LemoineControlStyles.BuildStatusChip(out _statusText);
            _statusChip.HorizontalAlignment = HorizontalAlignment.Right;
            _statusChip.VerticalAlignment   = VerticalAlignment.Bottom;
            _statusChip.Visibility          = WpfVisibility.Collapsed;
            _floatingSlot.Content = _statusChip;
        }

        private void FlashStatus(string msg)
        {
            if (_statusText == null || _statusChip == null) return;
            _statusText.Text      = msg;
            _statusChip.Visibility = WpfVisibility.Visible;
            _statusTimer?.Stop();
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            // Guard the unattended tick — this window has no dispatcher exception net.
            _statusTimer.Tick += (s, e) =>
            {
                try
                {
                    _statusText.Text = "";
                    _statusChip.Visibility = WpfVisibility.Collapsed;
                    _statusTimer?.Stop();
                }
                catch (Exception ex) { LemoineLog.Swallowed("ScopeBoxManager: status chip tick", ex); }
            };
            _statusTimer.Start();
        }

        // ── Scan round-trip ───────────────────────────────────────────────────
        private void RaiseScan()
        {
            var handler = App.ScopeBoxManagerScanHandler;
            var evt     = App.ScopeBoxManagerScanEvent;
            if (handler == null || evt == null)
            {
                FlashStatus(LemoineStrings.T("scopeBoxes.manager.status.noHandler"));
                return;
            }

            handler.OnScanComplete = result =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ApplyScan(result); }
                    catch (Exception ex) { LemoineLog.Error("ScopeBoxManager: apply scan", ex); }
                }));
            };
            handler.OnError = msg =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.BeginInvoke(new Action(() =>
                    FlashStatus(LemoineStrings.T("scopeBoxes.manager.status.scanFailed", msg))));
            };
            evt.Raise();
        }

        private void ApplyScan(ManagerScanResult result)
        {
            _scan = result;
            _busy = false;

            var known = new HashSet<long>(result.Boxes.Select(b => b.Id.Value));
            _checkedBoxes.RemoveWhere(id => !known.Contains(id));
            if (!known.Contains(_activeBoxId))
                _activeBoxId = result.Boxes.FirstOrDefault()?.Id.Value ?? -1;
            _checkedViews.Clear();
            _checkedDatums.Clear();

            RefreshSidebar();
            RefreshMain();
        }

        // ── Sidebar ───────────────────────────────────────────────────────────
        private IEnumerable<ScopeBoxUsage> FilteredBoxes()
        {
            if (_scan == null) return Enumerable.Empty<ScopeBoxUsage>();
            switch (_filterMode)
            {
                case "Used":   return _scan.Boxes.Where(b => !b.IsUnused);
                case "Unused": return _scan.Boxes.Where(b => b.IsUnused);
                default:       return _scan.Boxes;
            }
        }

        private void RefreshSidebar()
        {
            if (_boxListPanel == null || _scan == null) return;
            _boxListPanel.Children.Clear();

            int unused = _scan.Boxes.Count(b => b.IsUnused);
            if (_sideHeader != null)
                _sideHeader.Text = LemoineStrings.T("scopeBoxes.manager.side.header",
                    _scan.Boxes.Count, unused);

            if (_scan.Boxes.Count == 0)
            {
                var none = new TextBlock
                {
                    Text = LemoineStrings.T("scopeBoxes.manager.side.empty"),
                    TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic,
                    Margin = new Thickness(12, 8, 12, 8),
                };
                none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                _boxListPanel.Children.Add(none);
                return;
            }

            foreach (var box in FilteredBoxes())
            {
                long id = box.Id.Value;
                bool isActive = id == _activeBoxId;

                var row = new Border
                {
                    Padding = new Thickness(10, 7, 10, 7),
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Cursor = Cursors.Hand,
                };
                if (isActive)
                {
                    row.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                    row.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                }
                else
                {
                    row.Background  = Brushes.Transparent;
                    row.BorderBrush = Brushes.Transparent;
                }

                var g = new WpfGrid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var cb = new CheckBox
                {
                    IsChecked = _checkedBoxes.Contains(id),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                cb.Checked   += (s, e) => _checkedBoxes.Add(id);
                cb.Unchecked += (s, e) => _checkedBoxes.Remove(id);
                WpfGrid.SetColumn(cb, 0);
                g.Children.Add(cb);

                var name = new TextBlock
                {
                    Text = box.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                WpfGrid.SetColumn(name, 1);
                g.Children.Add(name);

                FrameworkElement tail;
                if (box.IsUnused)
                {
                    var badge = new Border
                    {
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(6, 1, 6, 1),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    badge.SetResourceReference(Border.BackgroundProperty, "LemoineRedDim");
                    var bt = new TextBlock { Text = LemoineStrings.T("scopeBoxes.manager.side.unusedBadge") };
                    bt.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    bt.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
                    bt.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    badge.Child = bt;
                    tail = badge;
                }
                else
                {
                    var use = new TextBlock
                    {
                        Text = LemoineStrings.T("scopeBoxes.manager.side.usage",
                            box.Views.Count, box.Datums.Count),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    use.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    use.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    use.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    tail = use;
                }
                WpfGrid.SetColumn(tail, 2);
                g.Children.Add(tail);

                row.Child = g;
                row.MouseLeftButtonUp += (s, e) =>
                {
                    // Checkbox clicks toggle without changing the active box.
                    if (e.OriginalSource is CheckBox) return;
                    _activeBoxId = id;
                    _checkedViews.Clear();
                    _checkedDatums.Clear();
                    RefreshSidebar();
                    RefreshMain();
                };
                if (!isActive)
                {
                    row.MouseEnter += (s, e) => row.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
                    row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
                }

                _boxListPanel.Children.Add(row);
            }
        }

        // ── Main editor ───────────────────────────────────────────────────────
        private ScopeBoxUsage? ActiveBox()
            => _scan?.Boxes.FirstOrDefault(b => b.Id.Value == _activeBoxId);

        private void RefreshMain()
        {
            if (_mainPanel == null) return;
            _mainPanel.Children.Clear();

            var box = ActiveBox();
            if (box == null)
            {
                var none = new TextBlock
                {
                    Text = LemoineStrings.T("scopeBoxes.manager.main.noSelection"),
                    TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic,
                };
                none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                _mainPanel.Children.Add(none);
                return;
            }

            // ── Name / size / delete section ───────────────────────────
            var nameCard = NewCard();
            var nameGrid = new WpfGrid();
            nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameEdit = new LemoineInlineEdit
            {
                Text = box.Name,
                Bold = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            nameEdit.TextCommitted += (s, newName) =>
            {
                if (string.IsNullOrWhiteSpace(newName) || newName == box.Name)
                {
                    nameEdit.Text = box.Name;
                    return;
                }
                RaiseAction(h =>
                {
                    h.Action = "Rename";
                    h.RenamePairs = new List<(ElementId, string)> { (box.Id, newName.Trim()) };
                });
            };
            WpfGrid.SetColumn(nameEdit, 0);
            nameGrid.Children.Add(nameEdit);

            var size = new TextBlock
            {
                Text = LemoineStrings.T("scopeBoxes.manager.main.size",
                    box.WidthFt.ToString("0.#"), box.DepthFt.ToString("0.#"), box.HeightFt.ToString("0.#")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
            };
            size.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            size.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            size.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            WpfGrid.SetColumn(size, 1);
            nameGrid.Children.Add(size);

            var delBtn = LemoineControlStyles.BuildSmallButton(
                LemoineStrings.T("scopeBoxes.manager.main.deleteBox"),
                LemoineControlStyles.LemoineButtonVariant.Danger);
            delBtn.Click += (s, e) => ShowDeleteBoxOverlay(box);
            WpfGrid.SetColumn(delBtn, 2);
            nameGrid.Children.Add(delBtn);

            nameCard.Child = nameGrid;
            _mainPanel.Children.Add(nameCard);

            // ── Views section ──────────────────────────────────────────
            var viewsCard = NewCard();
            var viewsPanel = new StackPanel();

            viewsPanel.Children.Add(SectionHeader(
                LemoineStrings.T("scopeBoxes.manager.main.viewsHeader", box.Views.Count),
                (LemoineStrings.T("scopeBoxes.manager.main.assignViews"),
                 LemoineControlStyles.LemoineButtonVariant.Primary,
                 () => ShowAssignViewsOverlay(box)),
                (LemoineStrings.T("scopeBoxes.manager.main.clearChecked"),
                 LemoineControlStyles.LemoineButtonVariant.Ghost,
                 () => ClearCheckedViews(box))));

            if (box.Views.Count == 0)
                viewsPanel.Children.Add(EmptyNote(LemoineStrings.T("scopeBoxes.manager.main.viewsEmpty")));
            else
                viewsPanel.Children.Add(BuildRefList(
                    box.Views.Select(v => (v.Id.Value, v.Name, v.TypeLabel)), _checkedViews));

            viewsPanel.Children.Add(EmptyNote(LemoineStrings.T("scopeBoxes.manager.main.viewsHint")));
            viewsCard.Child = viewsPanel;
            _mainPanel.Children.Add(viewsCard);

            // ── Datums section ─────────────────────────────────────────
            var datumsCard = NewCard();
            var datumsPanel = new StackPanel();

            viewsCard.Margin = new Thickness(0, 0, 0, 12);
            datumsPanel.Children.Add(SectionHeader(
                LemoineStrings.T("scopeBoxes.manager.main.datumsHeader", box.Datums.Count),
                (LemoineStrings.T("scopeBoxes.manager.main.assignDatums"),
                 LemoineControlStyles.LemoineButtonVariant.Primary,
                 () => ShowAssignDatumsOverlay(box)),
                (LemoineStrings.T("scopeBoxes.manager.main.clearChecked"),
                 LemoineControlStyles.LemoineButtonVariant.Ghost,
                 () => ClearCheckedDatums(box))));

            if (box.Datums.Count == 0)
                datumsPanel.Children.Add(EmptyNote(LemoineStrings.T("scopeBoxes.manager.main.datumsEmpty")));
            else
                datumsPanel.Children.Add(BuildRefList(
                    box.Datums.Select(d => (d.Id.Value, d.Name, KindLabel(d.Kind))), _checkedDatums));

            datumsCard.Child = datumsPanel;
            _mainPanel.Children.Add(datumsCard);
        }

        private static string KindLabel(string kind)
        {
            switch (kind)
            {
                case "Level":    return LemoineStrings.T("scopeBoxes.manager.main.kindLevel");
                case "RefPlane": return LemoineStrings.T("scopeBoxes.manager.main.kindRefPlane");
                default:         return LemoineStrings.T("scopeBoxes.manager.main.kindGrid");
            }
        }

        private static Border NewCard()
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10),
                Padding         = new Thickness(14, 12, 14, 12),
                Margin          = new Thickness(0, 0, 0, 12),
            };
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            return card;
        }

        private FrameworkElement SectionHeader(
            string title,
            params (string Label, LemoineControlStyles.LemoineButtonVariant Variant, Action OnClick)[] actions)
        {
            var g = new WpfGrid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tb = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center,
                                     FontWeight = FontWeights.SemiBold };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            WpfGrid.SetColumn(tb, 0);
            g.Children.Add(tb);

            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (label, variant, onClick) in actions)
            {
                var b = LemoineControlStyles.BuildSmallButton(label, variant);
                b.Margin = new Thickness(6, 0, 0, 0);
                b.Click += (s, e) => onClick();
                btns.Children.Add(b);
            }
            WpfGrid.SetColumn(btns, 1);
            g.Children.Add(btns);

            return g;
        }

        private static TextBlock EmptyNote(string text)
        {
            var tb = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 6, 0, 0),
            };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        // Checkable list of (id, name, tag) rows backed by a live checked-id set.
        private FrameworkElement BuildRefList(
            IEnumerable<(long Id, string Name, string Tag)> rows, HashSet<long> checkedSet)
        {
            var listBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
            };
            listBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var panel = new StackPanel();
            bool first = true;
            foreach (var (id, rowName, tag) in rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                var row = new Border { Padding = new Thickness(10, 5, 10, 5) };
                if (!first) row.BorderThickness = new Thickness(0, 1, 0, 0);
                row.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                first = false;

                var g = new WpfGrid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                long rowId = id;
                var cb = new CheckBox
                {
                    IsChecked = checkedSet.Contains(rowId),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                cb.Checked   += (s, e) => checkedSet.Add(rowId);
                cb.Unchecked += (s, e) => checkedSet.Remove(rowId);
                WpfGrid.SetColumn(cb, 0);
                g.Children.Add(cb);

                var nameTb = new TextBlock
                {
                    Text = rowName, TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                nameTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                nameTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                nameTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                WpfGrid.SetColumn(nameTb, 1);
                g.Children.Add(nameTb);

                var tagTb = new TextBlock
                {
                    Text = tag, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                tagTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                tagTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                tagTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                WpfGrid.SetColumn(tagTb, 2);
                g.Children.Add(tagTb);

                row.Child = g;
                panel.Children.Add(row);
            }

            listBorder.Child = panel;
            return listBorder;
        }

        // ── Actions ───────────────────────────────────────────────────────────
        private void RaiseAction(Action<ScopeBoxManagerRunHandler> configure)
        {
            if (_busy) { FlashStatus(LemoineStrings.T("scopeBoxes.manager.status.busy")); return; }

            var handler = App.ScopeBoxManagerRunHandler;
            var evt     = App.ScopeBoxManagerRunEvent;
            if (handler == null || evt == null)
            {
                FlashStatus(LemoineStrings.T("scopeBoxes.manager.status.noHandler"));
                return;
            }

            _busy = true;
            configure(handler);
            handler.OnActionComplete = (ok, failed, summary, refreshed) =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        _busy = false;
                        FlashStatus(summary);
                        if (refreshed != null) ApplyScan(refreshed);
                    }
                    catch (Exception ex) { LemoineLog.Error("ScopeBoxManager: apply action result", ex); }
                }));
            };
            evt.Raise();
        }

        private void ClearCheckedViews(ScopeBoxUsage box)
        {
            if (_checkedViews.Count == 0)
            {
                FlashStatus(LemoineStrings.T("scopeBoxes.manager.status.nothingChecked"));
                return;
            }
            var keep = box.Views.Where(v => !_checkedViews.Contains(v.Id.Value))
                               .Select(v => v.Id).ToList();
            RaiseAction(h => { h.Action = "SetViews"; h.BoxId = box.Id; h.ViewIds = keep; });
        }

        private void ClearCheckedDatums(ScopeBoxUsage box)
        {
            if (_checkedDatums.Count == 0)
            {
                FlashStatus(LemoineStrings.T("scopeBoxes.manager.status.nothingChecked"));
                return;
            }
            var keep = box.Datums.Where(d => !_checkedDatums.Contains(d.Id.Value))
                                .Select(d => d.Id).ToList();
            RaiseAction(h => { h.Action = "SetDatums"; h.BoxId = box.Id; h.DatumIds = keep; });
        }

        // ── Overlays (in-window modal cards — never Popups) ───────────────────
        private void ShowOverlay(FrameworkElement card)
        {
            _overlayHost.Children.Clear();

            var dim = new Border { Background = new SolidColorBrush(WpfColor.FromArgb(0x99, 0, 0, 0)) };
            dim.MouseLeftButtonUp += (s, e) => HideOverlay();
            _overlayHost.Children.Add(dim);

            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment   = VerticalAlignment.Center;
            _overlayHost.Children.Add(card);

            _overlayHost.Visibility = WpfVisibility.Visible;
        }

        private void HideOverlay()
        {
            _overlayHost.Children.Clear();
            _overlayHost.Visibility = WpfVisibility.Collapsed;
        }

        private Border NewOverlayCard(string title, double width)
        {
            var card = new Border
            {
                Width           = width,
                MaxHeight       = 560,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10),
                Padding         = new Thickness(16, 14, 16, 14),
            };
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");

            var panel = new DockPanel { LastChildFill = true };
            var hdr = new TextBlock
            {
                Text = title, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10),
            };
            hdr.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            hdr.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            hdr.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            DockPanel.SetDock(hdr, Dock.Top);
            panel.Children.Add(hdr);

            card.Child = panel;
            return card;
        }

        private FrameworkElement OverlayFooter(string applyLabel, Action onApply,
            LemoineControlStyles.LemoineButtonVariant applyVariant = LemoineControlStyles.LemoineButtonVariant.Primary)
        {
            var g = new WpfGrid { Margin = new Thickness(0, 12, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cancel = LemoineControlStyles.BuildSmallButton(
                LemoineStrings.T("scopeBoxes.manager.overlay.cancel"));
            cancel.Click += (s, e) => HideOverlay();
            WpfGrid.SetColumn(cancel, 0);
            g.Children.Add(cancel);

            var apply = LemoineControlStyles.BuildSmallButton(applyLabel, applyVariant);
            apply.Click += (s, e) => { HideOverlay(); onApply(); };
            WpfGrid.SetColumn(apply, 2);
            g.Children.Add(apply);

            return g;
        }

        // ── Assign views overlay ──────────────────────────────────────────────
        private void ShowAssignViewsOverlay(ScopeBoxUsage box)
        {
            if (_scan?.Tree == null) return;

            var card = NewOverlayCard(
                LemoineStrings.T("scopeBoxes.manager.overlay.assignViewsTitle", box.Name), 520);
            var host = (DockPanel)card.Child;

            var picker = new LemoineBrowserTreePicker { MinHeight = 320 };
            picker.SetTree(_scan.Tree,
                eligibleIds:     _scan.EligibleViews.Select(v => v.Id.Value),
                initialSelected: box.Views.Select(v => v.Id.Value));

            var footer = OverlayFooter(
                LemoineStrings.T("scopeBoxes.manager.overlay.applyAssign"),
                () =>
                {
                    var ids = picker.SelectedIds.Select(v => new ElementId(v)).ToList();
                    RaiseAction(h => { h.Action = "SetViews"; h.BoxId = box.Id; h.ViewIds = ids; });
                });
            DockPanel.SetDock(footer, Dock.Bottom);
            host.Children.Add(footer);
            host.Children.Add(picker);

            ShowOverlay(card);
        }

        // ── Assign datums overlay ─────────────────────────────────────────────
        private void ShowAssignDatumsOverlay(ScopeBoxUsage box)
        {
            if (_scan == null) return;

            var card = NewOverlayCard(
                LemoineStrings.T("scopeBoxes.manager.overlay.assignDatumsTitle", box.Name), 460);
            var host = (DockPanel)card.Child;

            // Group datums by kind; keys carry the element id so duplicates are safe.
            var keyToId = new Dictionary<string, ElementId>(StringComparer.Ordinal);
            var groups  = new Dictionary<string, List<string>>();
            foreach (var d in _scan.Datums)
            {
                string grp = KindLabel(d.Kind);
                string key = $"{d.Name}  [{d.Id.Value}]";
                keyToId[key] = d.Id;
                if (!groups.ContainsKey(grp)) groups[grp] = new List<string>();
                groups[grp].Add(key);
            }

            var selected = new HashSet<long>(box.Datums.Select(d => d.Id.Value));
            var tabs = new LemoineMultiSelectTabs();
            tabs.SelectionChanged += sel =>
            {
                selected.Clear();
                foreach (var k in sel)
                    if (keyToId.TryGetValue(k, out var id)) selected.Add(id.Value);
            };
            tabs.SetGroups(groups,
                keyToId.Where(kv => selected.Contains(kv.Value.Value)).Select(kv => kv.Key).ToList());

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 360,
                Content   = tabs,
            };
            LemoineControlStyles.SetSelfContainedScroll(scroll, true);

            var footer = OverlayFooter(
                LemoineStrings.T("scopeBoxes.manager.overlay.applyAssign"),
                () =>
                {
                    var ids = selected.Select(v => new ElementId(v)).ToList();
                    RaiseAction(h => { h.Action = "SetDatums"; h.BoxId = box.Id; h.DatumIds = ids; });
                });
            DockPanel.SetDock(footer, Dock.Bottom);
            host.Children.Add(footer);
            host.Children.Add(scroll);

            ShowOverlay(card);
        }

        // ── Bulk rename overlay ───────────────────────────────────────────────
        private static readonly string[] RenameTokens =
        {
            "None", "Current Name", "Number", "Custom",
        };

        private void ShowRenameOverlay()
        {
            if (_scan == null) return;
            var targets = _scan.Boxes.Where(b => _checkedBoxes.Contains(b.Id.Value)).ToList();
            if (targets.Count == 0)
            {
                FlashStatus(LemoineStrings.T("scopeBoxes.manager.status.nothingChecked"));
                return;
            }

            var card = NewOverlayCard(
                LemoineStrings.T("scopeBoxes.manager.overlay.renameTitle", targets.Count), 460);
            var host = (DockPanel)card.Child;

            var body = new StackPanel();
            var state = new NamingSlotsState { Front = "Current Name", Center = "None", End = "None" };

            var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            preview.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            preview.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            preview.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            List<string> NewNames() =>
                targets.Select((b, i) =>
                {
                    var parts = state.ResolveParts(token =>
                    {
                        switch (token)
                        {
                            case "Current Name": return b.Name;
                            case "Number":       return (i + 1).ToString("00");
                            default:             return "";
                        }
                    });
                    return parts.Count == 0 ? b.Name : string.Join(" - ", parts);
                }).ToList();

            void UpdatePreview()
            {
                var names = NewNames();
                preview.Text = names.Count == 1
                    ? names[0]
                    : LemoineStrings.T("scopeBoxes.manager.overlay.renamePreviewTwo",
                        names[0], names[1]);
            }

            var slots = new LemoineNamingSlots(RenameTokens, state);
            slots.Changed += UpdatePreview;
            body.Children.Add(slots);
            body.Children.Add(preview);
            UpdatePreview();

            var footer = OverlayFooter(
                LemoineStrings.T("scopeBoxes.manager.overlay.applyRename"),
                () =>
                {
                    var names = NewNames();
                    var pairs = targets.Select((b, i) => (b.Id, names[i])).ToList();
                    RaiseAction(h => { h.Action = "Rename"; h.RenamePairs = pairs; });
                });
            DockPanel.SetDock(footer, Dock.Bottom);
            host.Children.Add(footer);
            host.Children.Add(body);

            ShowOverlay(card);
        }

        // ── Delete overlays ───────────────────────────────────────────────────
        private void ShowDeleteUnusedOverlay()
        {
            if (_scan == null) return;
            var unused = _scan.Boxes.Where(b => b.IsUnused).ToList();
            if (unused.Count == 0)
            {
                FlashStatus(LemoineStrings.T("scopeBoxes.manager.status.noUnused"));
                return;
            }
            ShowDeleteConfirm(
                LemoineStrings.T("scopeBoxes.manager.overlay.deleteUnusedTitle", unused.Count),
                unused.Select(b => b.Name),
                unused.Select(b => b.Id).ToList());
        }

        private void ShowDeleteBoxOverlay(ScopeBoxUsage box)
        {
            ShowDeleteConfirm(
                LemoineStrings.T("scopeBoxes.manager.overlay.deleteBoxTitle", box.Name),
                box.IsUnused
                    ? new[] { LemoineStrings.T("scopeBoxes.manager.overlay.deleteBoxUnused") }
                    : new[] { LemoineStrings.T("scopeBoxes.manager.overlay.deleteBoxInUse",
                                  box.Views.Count, box.Datums.Count) },
                new List<ElementId> { box.Id });
        }

        private void ShowDeleteConfirm(string title, IEnumerable<string> lines, List<ElementId> ids)
        {
            var card = NewOverlayCard(title, 420);
            var host = (DockPanel)card.Child;

            var body = new StackPanel();
            foreach (var line in lines)
            {
                var tb = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap,
                                         Margin = new Thickness(0, 0, 0, 4) };
                tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                body.Children.Add(tb);
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 300,
                Content   = body,
            };
            LemoineControlStyles.SetSelfContainedScroll(scroll, true);

            var footer = OverlayFooter(
                LemoineStrings.T("scopeBoxes.manager.overlay.applyDelete"),
                () => RaiseAction(h => { h.Action = "Delete"; h.BoxIds = ids; }),
                LemoineControlStyles.LemoineButtonVariant.Danger);
            DockPanel.SetDock(footer, Dock.Bottom);
            host.Children.Add(footer);
            host.Children.Add(scroll);

            ShowOverlay(card);
        }
    }
}
