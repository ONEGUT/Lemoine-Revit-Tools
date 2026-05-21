using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.Testing.LegendCreator;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Top-level orchestrator for the Legend Creator tab. Mutates an in-memory
    /// editing buffer (Layout + Rows) that callers commit on global Apply.
    ///
    /// Layout:
    ///   ┌────────────────── LemoineLegendLayoutBar ───────────────────┐
    ///   ├──────────────────────────────────────────────┬──────────────┤
    ///   │  rows ScrollViewer                           │ palette      │
    ///   │   + "add group (new row)"                    │ (top)        │
    ///   │   + trash drop zone                          │ ────────────  │
    ///   │                                              │ preview      │
    ///   │                                              │ (bottom)     │
    ///   ├──────────────────────────────────────────────┴──────────────┤
    ///   │ footer: counts · Preview ▢                                  │
    ///   └─────────────────────────────────────────────────────────────┘
    /// </summary>
    public partial class LemoineLegendBuilder : UserControl
    {
        // ── Editing buffer (commit on Apply) ───────────────────────────────
        public LegendLayoutConfig    Layout { get; private set; } = new LegendLayoutConfig();
        public List<LegendRowConfig> Rows   { get; private set; } = new List<LegendRowConfig>();
        public bool PreviewVisible { get; private set; } = true;

        // ── Children ───────────────────────────────────────────────────────
        private LemoineLegendLayoutBar? _layoutBar;
        private ScrollViewer?           _rowsScroll;
        private StackPanel?             _rowsStack;
        private LemoineLegendPalette?   _palette;
        private LemoineLegendPreview?   _preview;
        private Border?                 _previewHost;
        private TextBlock?              _countsTb;
        private Border?                 _trashBorder;
        private Button?                 _previewToggle;

        public LemoineLegendBuilder()
        {
            InitializeComponent();
            Loaded += (s, e) => BuildAll();
            Unloaded += OnUnloaded;
        }

        public void LoadFrom(LegendCreatorSettings settings)
        {
            Layout = LegendCreatorSettings.DeepCopy(settings.Layout ?? new LegendLayoutConfig());
            Rows   = LegendCreatorSettings.DeepCopy(settings.Rows   ?? new List<LegendRowConfig>());
            PreviewVisible = settings.PreviewVisible;
            if (IsLoaded) BuildAll();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Build skeleton
        // ─────────────────────────────────────────────────────────────────────
        private void BuildAll()
        {
            _root.RowDefinitions.Clear();
            _root.ColumnDefinitions.Clear();
            _root.Children.Clear();

            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // layout bar
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // footer

            // ── Row 0: Layout bar ──────────────────────────────────────────
            _layoutBar = new LemoineLegendLayoutBar { Layout = Layout };
            _layoutBar.Changed += (s, e) => OnEdited();
            Grid.SetRow(_layoutBar, 0);
            _root.Children.Add(_layoutBar);

            // ── Row 1: main split ──────────────────────────────────────────
            var mainGrid = new Grid();
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });

            // Rows scroller (left side)
            _rowsScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0, 4, 0, 4),
            };
            _rowsStack = new StackPanel { Orientation = Orientation.Vertical };
            _rowsScroll.Content = _rowsStack;
            Grid.SetColumn(_rowsScroll, 0);
            mainGrid.Children.Add(_rowsScroll);

            // Side panel (right)
            var sideGrid = new Grid { Margin = new Thickness(8, 4, 10, 4) };
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var paletteBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 4),
            };
            paletteBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            paletteBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            _palette = new LemoineLegendPalette();
            paletteBorder.Child = _palette;
            Grid.SetRow(paletteBorder, 0);
            sideGrid.Children.Add(paletteBorder);

            _previewHost = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Margin = new Thickness(0, 4, 0, 0),
            };
            _previewHost.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _previewHost.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            _preview = new LemoineLegendPreview();
            _previewHost.Child = _preview;
            _previewHost.Visibility = PreviewVisible ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetRow(_previewHost, PreviewVisible ? 1 : 0);
            sideGrid.Children.Add(_previewHost);

            Grid.SetColumn(sideGrid, 1);
            mainGrid.Children.Add(sideGrid);

            Grid.SetRow(mainGrid, 1);
            _root.Children.Add(mainGrid);

            // ── Row 2: footer ─────────────────────────────────────────────
            _root.Children.Add(BuildFooter());

            // ── Subscribe to live filter changes so palette refreshes ─────
            AutoFiltersSettings.Saved += OnFiltersSaved;

            // ── Populate ──────────────────────────────────────────────────
            RebuildRows();
            ApplySidePanelLayout();
            UpdatePreview();
            UpdateCounts();
        }

        private UIElement BuildFooter()
        {
            var border = new Border
            {
                Height = 32,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(12, 0, 12, 0),
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            border.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _countsTb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            _countsTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            _countsTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            _countsTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Grid.SetColumn(_countsTb, 0);
            grid.Children.Add(_countsTb);

            _previewToggle = LemoineControlStyles.BuildButton(
                PreviewVisible ? "Preview ☑" : "Preview ☐",
                LemoineControlStyles.LemoineButtonVariant.Ghost);
            _previewToggle.Click += (s, e) =>
            {
                PreviewVisible = !PreviewVisible;
                _previewToggle.Content = PreviewVisible ? "Preview ☑" : "Preview ☐";
                ApplySidePanelLayout();
                OnEdited();
            };
            Grid.SetColumn(_previewToggle, 1);
            grid.Children.Add(_previewToggle);

            border.Child = grid;
            Grid.SetRow(border, 2);
            return border;
        }

        private void ApplySidePanelLayout()
        {
            if (_previewHost == null) return;
            _previewHost.Visibility = PreviewVisible ? Visibility.Visible : Visibility.Collapsed;
            // When hidden, palette gets all the row space. Re-row the side grid.
            var parent = _previewHost.Parent as Grid;
            if (parent == null) return;
            // Find palette host (the other row)
            UIElement? paletteHost = null;
            foreach (UIElement c in parent.Children)
                if (c != _previewHost) { paletteHost = c; break; }

            parent.RowDefinitions.Clear();
            if (PreviewVisible)
            {
                parent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                parent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                if (paletteHost != null) Grid.SetRow(paletteHost, 0);
                Grid.SetRow(_previewHost, 1);
            }
            else
            {
                parent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                if (paletteHost != null) Grid.SetRow(paletteHost, 0);
                Grid.SetRow(_previewHost, 0);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            AutoFiltersSettings.Saved -= OnFiltersSaved;
        }

        private void OnFiltersSaved()
        {
            // Marshal to UI thread
            Dispatcher.Invoke(() =>
            {
                _palette?.Refresh();
                RebuildRows();
                UpdatePreview();
                UpdateCounts();
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Rebuild the rows panel
        // ─────────────────────────────────────────────────────────────────────
        private void RebuildRows()
        {
            if (_rowsStack == null) return;
            _rowsStack.Children.Clear();

            for (int i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                var rowCtrl = new LemoineLegendRow();
                rowCtrl.Bind(row);
                int idx = i;
                rowCtrl.Changed                 += (s, e) => OnEdited();
                rowCtrl.BlockDropRequested      += (s, args) => HandleBlockDrop(args);
                rowCtrl.GroupDropInRowRequested += (s, args) => HandleGroupDropInRow(args);
                rowCtrl.RowSplitDropRequested   += (s, args) => HandleRowSplitDrop(args);
                rowCtrl.GroupDeleteRequested    += (s, gid) =>
                {
                    DeleteGroup(gid);
                    OnEdited();
                };
                _rowsStack.Children.Add(rowCtrl);
            }

            // "+ add group (new row)" trigger
            _rowsStack.Children.Add(BuildAddRowAffordance());

            // Trash drop zone
            _trashBorder = BuildTrashZone();
            _rowsStack.Children.Add(_trashBorder);
        }

        private UIElement BuildAddRowAffordance()
        {
            var border = new Border
            {
                BorderThickness = new Thickness(1.4),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(10, 4, 10, 4),
                Padding = new Thickness(8, 8, 8, 8),
                AllowDrop = true,
                MinHeight = 38,
                Cursor = Cursors.Hand,
            };

            const string IdleText = "+ add group (new row)";
            const string DragText = "drop a category here to start a new row →";

            // ── Visual states ────────────────────────────────────────────────
            void ApplyIdle()
            {
                border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
                border.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            }
            void ApplyHint()
            {
                border.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                border.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
            }
            void ApplyActive()
            {
                border.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                border.SetResourceReference(Border.BackgroundProperty,  "LemoineAccent");
            }
            ApplyIdle();

            var tb = new TextBlock
            {
                Text = IdleText,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontStyle = FontStyles.Italic,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            border.Child = tb;

            // ── Click → add new empty group in a new row ─────────────────────
            border.MouseLeftButtonUp += (s, e) =>
            {
                if (LegendDragSession.Active) return; // don't fire during a drop
                var newGroup = new LegendGroupConfig
                {
                    Id    = LegendIdGen.New("g"),
                    Title = "NEW GROUP",
                };
                var newRow = new LegendRowConfig { Id = LegendIdGen.New("r") };
                newRow.Groups.Add(newGroup);
                Rows.Add(newRow);
                OnEdited();
            };

            // ── Drag-session subscription ────────────────────────────────────
            // Show drag hint text only while a group/category drag is in flight.
            void OnStart(LegendDragPayload p)
            {
                if (p.What == LegendDragPayload.Kind.PaletteCategory ||
                    p.What == LegendDragPayload.Kind.Group)
                {
                    tb.Text = DragText;
                    ApplyHint();
                }
            }
            void OnEnd()
            {
                tb.Text = IdleText;
                ApplyIdle();
            }
            border.Loaded   += (s, e) => { LegendDragSession.Started += OnStart; LegendDragSession.Ended += OnEnd; };
            border.Unloaded += (s, e) => { LegendDragSession.Started -= OnStart; LegendDragSession.Ended -= OnEnd; };

            border.DragEnter += (s, e) =>
            {
                var p = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
                if (p != null && (p.What == LegendDragPayload.Kind.PaletteCategory || p.What == LegendDragPayload.Kind.Group))
                {
                    ApplyActive();
                    e.Effects = DragDropEffects.Move;
                    e.Handled = true;
                }
                else e.Effects = DragDropEffects.None;
            };
            border.DragOver  += (s, e) =>
            {
                var p = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
                e.Effects = (p != null && (p.What == LegendDragPayload.Kind.PaletteCategory || p.What == LegendDragPayload.Kind.Group))
                    ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            border.DragLeave += (s, e) =>
            {
                if (LegendDragSession.Active && LegendDragSession.Current != null &&
                    (LegendDragSession.Current.What == LegendDragPayload.Kind.PaletteCategory ||
                     LegendDragSession.Current.What == LegendDragPayload.Kind.Group))
                    ApplyHint();
                else
                {
                    tb.Text = IdleText;
                    ApplyIdle();
                }
            };
            border.Drop += (s, e) =>
            {
                tb.Text = IdleText;
                ApplyIdle();
                var p = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
                if (p == null) return;
                if (p.What == LegendDragPayload.Kind.PaletteCategory)
                {
                    var newRow = new LegendRowConfig { Id = LegendIdGen.New("r") };
                    newRow.Groups.Add(BuildGroupFromTrade(p.SourceTradeId));
                    Rows.Add(newRow);
                }
                else if (p.What == LegendDragPayload.Kind.Group)
                {
                    var grp = TakeGroup(p.GroupId);
                    if (grp != null)
                    {
                        var newRow = new LegendRowConfig { Id = LegendIdGen.New("r") };
                        newRow.Groups.Add(grp);
                        Rows.Add(newRow);
                    }
                }
                DropEmptyRows();
                OnEdited();
                e.Handled = true;
            };
            return border;
        }

        private Border BuildTrashZone()
        {
            var border = new Border
            {
                BorderThickness = new Thickness(1.4),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(10, 4, 10, 10),
                Padding = new Thickness(8, 8, 8, 8),
                AllowDrop = true,
                MinHeight = 38,
            };
            void ApplyIdle()
            {
                border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                border.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            }
            void ApplyHint()
            {
                border.SetResourceReference(Border.BorderBrushProperty, "LemoineRed");
                border.SetResourceReference(Border.BackgroundProperty,  "LemoineRedDim");
            }
            void ApplyActive()
            {
                border.SetResourceReference(Border.BorderBrushProperty, "LemoineRed");
                border.SetResourceReference(Border.BackgroundProperty,  "LemoineRed");
            }
            ApplyIdle();
            border.Visibility = Visibility.Collapsed; // hidden until a block drag starts

            var tb = new TextBlock
            {
                Text = "🗑  drop a block here to delete",
                HorizontalAlignment = HorizontalAlignment.Center,
                FontStyle = FontStyles.Italic,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            border.Child = tb;

            // Session subscription — only existing-Block drags can be trashed.
            // Show the zone only while a block drag is in flight; hide it otherwise.
            void OnStart(LegendDragPayload p)
            {
                if (p.What == LegendDragPayload.Kind.Block)
                {
                    border.Visibility = Visibility.Visible;
                    ApplyHint();
                }
            }
            void OnEnd()
            {
                border.Visibility = Visibility.Collapsed;
                ApplyIdle();
            }
            border.Loaded   += (s, e) =>
            {
                LegendDragSession.Started += OnStart;
                LegendDragSession.Ended   += OnEnd;
                if (LegendDragSession.Active && LegendDragSession.Current != null)
                    OnStart(LegendDragSession.Current);
            };
            border.Unloaded += (s, e) =>
            {
                LegendDragSession.Started -= OnStart;
                LegendDragSession.Ended   -= OnEnd;
            };

            border.DragEnter += (s, e) =>
            {
                var p = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
                if (p != null && p.What == LegendDragPayload.Kind.Block)
                {
                    ApplyActive();
                    e.Effects = DragDropEffects.Move;
                    e.Handled = true;
                }
                else e.Effects = DragDropEffects.None;
            };
            border.DragOver  += (s, e) =>
            {
                var p = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
                e.Effects = (p != null && p.What == LegendDragPayload.Kind.Block) ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            border.DragLeave += (s, e) =>
            {
                if (LegendDragSession.Active && LegendDragSession.Current != null
                    && LegendDragSession.Current.What == LegendDragPayload.Kind.Block)
                    ApplyHint();
                else
                    ApplyIdle();
            };
            border.Drop += (s, e) =>
            {
                ApplyIdle();
                var p = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
                if (p == null || p.What != LegendDragPayload.Kind.Block) return;
                DeleteBlock(p.BlockId);
                DropEmptyRows();
                OnEdited();
                e.Handled = true;
            };
            return border;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Drop handlers
        // ─────────────────────────────────────────────────────────────────────
        private void HandleBlockDrop(BlockDropArgs args)
        {
            var p = args.Payload;
            var grp = Rows.SelectMany(r => r.Groups).FirstOrDefault(g => g.Id == args.TargetGroupId);
            if (grp == null) return;

            switch (p.What)
            {
                case LegendDragPayload.Kind.PaletteFilter:
                {
                    var block = BuildBlockFromRule(p.SourceTradeId, p.SourceRuleId);
                    if (block == null) return;
                    int idx = Math.Max(0, Math.Min(args.TargetIndex, grp.Blocks.Count));
                    grp.Blocks.Insert(idx, block);
                    break;
                }
                case LegendDragPayload.Kind.PaletteCustom:
                {
                    var block = new LegendBlockConfig
                    {
                        Id = LegendIdGen.New("b"),
                        Name = "Custom",
                        Custom = true,
                        Color = "#888888",
                        Kind = "square", Fill = "solid",
                        Visible = true,
                    };
                    int idx = Math.Max(0, Math.Min(args.TargetIndex, grp.Blocks.Count));
                    grp.Blocks.Insert(idx, block);
                    break;
                }
                case LegendDragPayload.Kind.Block:
                {
                    // Move within / between groups
                    if (!TryTakeBlock(p.BlockId, out var block, out var fromGrp)) return;
                    int idx = args.TargetIndex;
                    if (fromGrp == grp)
                    {
                        // Compensate for index shift when moving downward within same group
                        // We already removed it; ensure idx is in range.
                        if (idx > grp.Blocks.Count) idx = grp.Blocks.Count;
                    }
                    else if (idx > grp.Blocks.Count) idx = grp.Blocks.Count;
                    grp.Blocks.Insert(idx, block!);
                    break;
                }
            }
            DropEmptyRows();
            OnEdited();
        }

        private void HandleGroupDropInRow(GroupDropArgs args)
        {
            var p = args.Payload;
            var row = Rows.FirstOrDefault(r => r.Id == args.TargetRowId);
            if (row == null) return;

            switch (p.What)
            {
                case LegendDragPayload.Kind.PaletteCategory:
                {
                    var grp = BuildGroupFromTrade(p.SourceTradeId);
                    int idx = Math.Max(0, Math.Min(args.TargetIndex, row.Groups.Count));
                    row.Groups.Insert(idx, grp);
                    break;
                }
                case LegendDragPayload.Kind.Group:
                {
                    var grp = TakeGroup(p.GroupId);
                    if (grp == null) return;
                    int idx = Math.Max(0, Math.Min(args.TargetIndex, row.Groups.Count));
                    row.Groups.Insert(idx, grp);
                    break;
                }
            }
            DropEmptyRows();
            OnEdited();
        }

        private void HandleRowSplitDrop(RowSplitDropArgs args)
        {
            var p = args.Payload;
            int anchorIdx = Rows.FindIndex(r => r.Id == args.AnchorRowId);
            if (anchorIdx < 0) return;
            int insertAt = args.Above ? anchorIdx : anchorIdx + 1;

            switch (p.What)
            {
                case LegendDragPayload.Kind.PaletteCategory:
                {
                    var newRow = new LegendRowConfig { Id = LegendIdGen.New("r") };
                    newRow.Groups.Add(BuildGroupFromTrade(p.SourceTradeId));
                    Rows.Insert(insertAt, newRow);
                    break;
                }
                case LegendDragPayload.Kind.Group:
                {
                    var grp = TakeGroup(p.GroupId);
                    if (grp == null) return;
                    var newRow = new LegendRowConfig { Id = LegendIdGen.New("r") };
                    newRow.Groups.Add(grp);
                    // Recompute insertAt because TakeGroup may have changed indices
                    int reIdx = Rows.FindIndex(r => r.Id == args.AnchorRowId);
                    if (reIdx < 0) reIdx = Rows.Count - 1;
                    insertAt = args.Above ? reIdx : reIdx + 1;
                    Rows.Insert(insertAt, newRow);
                    break;
                }
            }
            DropEmptyRows();
            OnEdited();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Buffer mutations
        // ─────────────────────────────────────────────────────────────────────
        private LegendGroupConfig BuildGroupFromTrade(string tradeId)
        {
            var trade = AutoFiltersSettings.Instance.Trades?.FirstOrDefault(t => t.Id == tradeId);
            var grp = new LegendGroupConfig
            {
                Id            = LegendIdGen.New("g"),
                Title         = (trade?.Label ?? "Custom").ToUpperInvariant(),
                SourceTradeId = trade?.Id ?? "",
            };
            if (trade?.Rules != null)
            {
                foreach (var rule in trade.Rules.Where(r => r.Enabled).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                {
                    grp.Blocks.Add(new LegendBlockConfig
                    {
                        Id            = LegendIdGen.New("b"),
                        Name          = rule.Name ?? "",
                        SourceTradeId = trade.Id,
                        SourceRuleId  = rule.Id,
                        Color         = rule.SurfColor ?? "#888888",
                        Kind = "square", Fill = "solid",
                        Visible = true,
                    });
                }
            }
            return grp;
        }

        private LegendBlockConfig? BuildBlockFromRule(string tradeId, string ruleId)
        {
            var trade = AutoFiltersSettings.Instance.Trades?.FirstOrDefault(t => t.Id == tradeId);
            if (trade?.Rules == null) return null;
            var rule = trade.Rules.FirstOrDefault(r => r.Id == ruleId);
            if (rule != null)
            {
                return new LegendBlockConfig
                {
                    Id = LegendIdGen.New("b"),
                    Name = rule.Name ?? "",
                    SourceTradeId = trade.Id,
                    SourceRuleId  = rule.Id,
                    Color         = rule.SurfColor ?? "#888888",
                    Kind = "square", Fill = "solid",
                    Visible = true,
                };
            }
            return null;
        }

        private LegendGroupConfig? TakeGroup(string groupId)
        {
            foreach (var row in Rows)
            {
                int i = row.Groups.FindIndex(g => g.Id == groupId);
                if (i >= 0)
                {
                    var g = row.Groups[i];
                    row.Groups.RemoveAt(i);
                    return g;
                }
            }
            return null;
        }

        private bool TryTakeBlock(string blockId, out LegendBlockConfig? block, out LegendGroupConfig? fromGroup)
        {
            block = null; fromGroup = null;
            foreach (var row in Rows)
            foreach (var grp in row.Groups)
            {
                int i = grp.Blocks.FindIndex(b => b.Id == blockId);
                if (i >= 0)
                {
                    block = grp.Blocks[i];
                    fromGroup = grp;
                    grp.Blocks.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        private void DeleteBlock(string blockId)
        {
            foreach (var row in Rows)
            foreach (var grp in row.Groups)
            {
                int i = grp.Blocks.FindIndex(b => b.Id == blockId);
                if (i >= 0) { grp.Blocks.RemoveAt(i); return; }
            }
        }

        private void DeleteGroup(string groupId)
        {
            foreach (var row in Rows)
            {
                int i = row.Groups.FindIndex(g => g.Id == groupId);
                if (i >= 0) { row.Groups.RemoveAt(i); return; }
            }
        }

        private void DropEmptyRows()
        {
            for (int i = Rows.Count - 1; i >= 0; i--)
                if (Rows[i].Groups == null || Rows[i].Groups.Count == 0)
                    Rows.RemoveAt(i);
        }

        // ─────────────────────────────────────────────────────────────────────
        // After-edit hooks
        // ─────────────────────────────────────────────────────────────────────
        private void OnEdited()
        {
            // Defer one tick — many event handlers will have references to the
            // very controls we'd otherwise destroy in RebuildRows().
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                RebuildRows();
                UpdatePreview();
                UpdateCounts();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void UpdatePreview()
        {
            _preview?.Update(Layout, Rows);
        }

        private void UpdateCounts()
        {
            if (_countsTb == null) return;
            int rows = Rows.Count;
            int groups = Rows.Sum(r => r.Groups?.Count ?? 0);
            int blocks = Rows.SelectMany(r => r.Groups ?? new List<LegendGroupConfig>())
                             .Sum(g => g.Blocks?.Count ?? 0);
            int hidden = Rows.SelectMany(r => r.Groups ?? new List<LegendGroupConfig>())
                             .SelectMany(g => g.Blocks ?? new List<LegendBlockConfig>())
                             .Count(b => !b.Visible);
            _countsTb.Text = $"● {blocks} entries · {groups} groups · {rows} rows · {hidden} hidden";
        }
    }
}
