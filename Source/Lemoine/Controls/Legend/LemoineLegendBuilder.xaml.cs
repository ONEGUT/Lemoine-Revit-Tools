using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.Testing.LegendCreator;
using Microsoft.Win32;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Top-level orchestrator for the Legend Creator canvas. Mutates an in-memory
    /// editing buffer (Layout + Rows) that callers commit on global Apply.
    ///
    /// Layout (single-column — title/subtitle, sizing and the preview are all
    /// managed by the host window; this control is just the drag canvas + counts):
    ///   ┌──────────────────────────────────────────────────────────────────────┐
    ///   │  [top drop bar]                                                       │
    ///   │  [left] [rows scroll] [right]                                         │
    ///   │  [bottom drop bar]                                                    │
    ///   └──────────────────────────────────────────────────────────────────────┘
    ///   │  footer: counts                                                        │
    ///
    /// <see cref="BulkEditorChanged"/> fires with the bulk editor UIElement when
    /// 2+ blocks are selected, or <see langword="null"/> to restore the palette.
    /// </summary>
    public partial class LemoineLegendBuilder : UserControl
    {
        // ── Editing buffer ─────────────────────────────────────────────────────
        public LegendLayoutConfig    Layout         { get; private set; } = new LegendLayoutConfig();
        public List<LegendRowConfig> Rows           { get; private set; } = new List<LegendRowConfig>();
        public bool                  PreviewVisible => _previewVisible;

        // ── Events ─────────────────────────────────────────────────────────────
        /// <summary>
        /// Fires with the bulk-editor UIElement when 2+ blocks are selected,
        /// or <see langword="null"/> when the palette should be restored.
        /// The host window swaps its right-panel palette slot based on this.
        /// </summary>
        public event Action<UIElement?>? BulkEditorChanged;

        /// <summary>
        /// Raised whenever the editing buffer (Layout/Rows) changes. The host
        /// window subscribes to refresh its window-level preview overlay.
        /// </summary>
        public event Action? Edited;

        // ── Child references ───────────────────────────────────────────────────
        // The legend title/subtitle is edited from the host window's tab pencil;
        // this control no longer hosts a top layout bar.
        private ScrollViewer?           _rowsScroll;
        private StackPanel?             _rowsStack;
        private TextBlock?              _countsTb;

        // Canvas-level drop bars
        private Border? _topDropBar;
        private Border? _bottomDropBar;
        private Border? _leftDropBar;
        private Border? _rightDropBar;
        private List<Border> _betweenRowBars = new List<Border>();

        // Preview is hosted by the settings window; this only carries the
        // persisted "was visible" flag for LoadFrom / templates.
        private bool _previewVisible = false;

        // Multi-select state
        private HashSet<string> _lSelectedBlockIds  = new HashSet<string>();
        private string?         _lActiveBlockId;
        private string?         _lShiftAnchorBlockId;
        private bool LBulkEditing => _lSelectedBlockIds.Count >= 2;

        // ── Constructor ────────────────────────────────────────────────────────
        public LemoineLegendBuilder()
        {
            InitializeComponent();
            Loaded   += (s, e) => BuildAll();
            Unloaded += OnUnloaded;
        }

        public void LoadFrom(LegendEntry entry)
        {
            Layout          = LegendCreatorSettings.DeepCopy(entry.Layout ?? new LegendLayoutConfig());
            Rows            = LegendCreatorSettings.DeepCopy(entry.Rows   ?? new List<LegendRowConfig>());
            _previewVisible = entry.PreviewVisible;
            if (IsLoaded) BuildAll();
        }

        /// <summary>Shows the templates popup anchored to the given element.</summary>
        public void ShowTemplatesPopup(UIElement anchor) => ShowLegendTemplatesPopup(anchor);

        // ── Called by host window to notify the builder that it is the active tab ──
        public void NotifyActivated() { }

        /// <summary>
        /// Called by the host window's sizing controls after they mutate <see cref="Layout"/>.
        /// Triggers a canvas rebuild and preview refresh.
        /// </summary>
        public void NotifyLayoutChanged() => OnEdited();

        // ─────────────────────────────────────────────────────────────────────
        // Build skeleton
        // ─────────────────────────────────────────────────────────────────────
        private void BuildAll()
        {
            _root.RowDefinitions.Clear();
            _root.ColumnDefinitions.Clear();
            _root.Children.Clear();

            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // row 0: canvas
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // row 1: footer

            // ── Row 0: Canvas ──────────────────────────────────────────────────
            var canvasGrid = BuildCanvasGrid();
            Grid.SetRow(canvasGrid, 0);
            _root.Children.Add(canvasGrid);

            // ── Row 1: Footer ──────────────────────────────────────────────────
            var footer = BuildFooter();
            Grid.SetRow(footer, 1);
            _root.Children.Add(footer);

            // ── Subscribe to filter saves ─────────────────────────────────────
            AutoFiltersSettings.Saved += OnFiltersSaved;

            // ── Populate ───────────────────────────────────────────────────────
            RebuildRows();
            Edited?.Invoke();
            UpdateCounts();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Canvas grid (drop bars wrapping the rows scroll)
        // ─────────────────────────────────────────────────────────────────────
        private Grid BuildCanvasGrid()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });

            _topDropBar = new Border { Height = 18, AllowDrop = true, Background = Brushes.Transparent };
            WireExternalDropBar(_topDropBar, DropBarRole.Top);
            Grid.SetRow(_topDropBar, 0);
            grid.Children.Add(_topDropBar);

            var innerGrid = new Grid();
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });

            _leftDropBar = new Border { Width = 18, AllowDrop = true, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch };
            WireExternalDropBar(_leftDropBar, DropBarRole.Left);
            Grid.SetColumn(_leftDropBar, 0);
            innerGrid.Children.Add(_leftDropBar);

            _rowsScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0, 4, 0, 4),
            };
            _rowsStack = new StackPanel { Orientation = Orientation.Vertical };
            _rowsScroll.Content = _rowsStack;
            LemoineControlStyles.WireBubblingScroll(_rowsScroll); // bubble wheel to parent at scroll limits
            Grid.SetColumn(_rowsScroll, 1);
            innerGrid.Children.Add(_rowsScroll);

            _rightDropBar = new Border { Width = 18, AllowDrop = true, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch };
            WireExternalDropBar(_rightDropBar, DropBarRole.Right);
            Grid.SetColumn(_rightDropBar, 2);
            innerGrid.Children.Add(_rightDropBar);

            Grid.SetRow(innerGrid, 1);
            grid.Children.Add(innerGrid);

            _bottomDropBar = new Border { Height = 18, AllowDrop = true, Background = Brushes.Transparent };
            WireExternalDropBar(_bottomDropBar, DropBarRole.Bottom);
            Grid.SetRow(_bottomDropBar, 2);
            grid.Children.Add(_bottomDropBar);

            return grid;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Footer
        // ─────────────────────────────────────────────────────────────────────
        private UIElement BuildFooter()
        {
            var border = new Border
            {
                Height          = 32,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding         = new Thickness(12, 0, 12, 0),
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            border.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");

            _countsTb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            _countsTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            _countsTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            _countsTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            border.Child = _countsTb;
            return border;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Drop bar roles
        // ─────────────────────────────────────────────────────────────────────
        private enum DropBarRole { Top, Bottom, Left, Right, Between }

        private void WireExternalDropBar(Border bar, DropBarRole role, int betweenIndex = -1)
        {
            void ApplyIdle()
            {
                bar.Background      = Brushes.Transparent;
                bar.BorderThickness = new Thickness(0);
                bar.BorderBrush     = null;
            }

            void ApplyHint()
            {
                bar.BorderThickness = new Thickness(1);
                bar.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                bar.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            }

            void ApplyActive()
            {
                bar.BorderThickness = new Thickness(1);
                bar.SetResourceReference(Border.BackgroundProperty,  "LemoineAccent");
                bar.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            }

            void OnSessionStart(LegendDragPayload p)
            {
                if (p.What == LegendDragPayload.Kind.PaletteCategory ||
                    p.What == LegendDragPayload.Kind.Group)
                    ApplyHint();
            }

            void OnSessionEnd() => ApplyIdle();

            bar.Loaded += (s, e) =>
            {
                LegendDragSession.Started += OnSessionStart;
                LegendDragSession.Ended   += OnSessionEnd;
                if (LegendDragSession.Active && LegendDragSession.Current != null)
                    OnSessionStart(LegendDragSession.Current);
            };
            bar.Unloaded += (s, e) =>
            {
                LegendDragSession.Started -= OnSessionStart;
                LegendDragSession.Ended   -= OnSessionEnd;
            };

            bar.DragEnter += (s, e) =>
            {
                var p = GetGroupPayload(e);
                if (p != null) { ApplyActive(); e.Effects = DragDropEffects.Move; e.Handled = true; }
                else e.Effects = DragDropEffects.None;
            };
            bar.DragOver += (s, e) =>
            {
                e.Effects = GetGroupPayload(e) != null ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            bar.DragLeave += (s, e) =>
            {
                if (LegendDragSession.Active && LegendDragSession.Current != null &&
                    (LegendDragSession.Current.What == LegendDragPayload.Kind.PaletteCategory ||
                     LegendDragSession.Current.What == LegendDragPayload.Kind.Group))
                    ApplyHint();
                else
                    ApplyIdle();
            };
            bar.Drop += (s, e) =>
            {
                ApplyIdle();
                var p = GetGroupPayload(e);
                if (p == null) return;

                switch (role)
                {
                    case DropBarRole.Top:
                    {
                        var newRow = BuildNewRowFromPayload(p);
                        if (newRow != null) { Rows.Insert(0, newRow); OnEdited(); }
                        break;
                    }
                    case DropBarRole.Bottom:
                    {
                        var newRow = BuildNewRowFromPayload(p);
                        if (newRow != null) { Rows.Add(newRow); OnEdited(); }
                        break;
                    }
                    case DropBarRole.Left:
                    {
                        var row = FindNearestRow(e.GetPosition(_rowsScroll).Y);
                        if (row != null)
                        {
                            var grp = BuildGroupFromPayload(p);
                            if (grp != null) { row.Groups.Insert(0, grp); DropEmptyRows(); OnEdited(); }
                        }
                        break;
                    }
                    case DropBarRole.Right:
                    {
                        var row = FindNearestRow(e.GetPosition(_rowsScroll).Y);
                        if (row != null)
                        {
                            var grp = BuildGroupFromPayload(p);
                            if (grp != null) { row.Groups.Add(grp); DropEmptyRows(); OnEdited(); }
                        }
                        break;
                    }
                    case DropBarRole.Between:
                    {
                        var newRow = BuildNewRowFromPayload(p);
                        int idx = Math.Max(0, Math.Min(betweenIndex + 1, Rows.Count));
                        if (newRow != null) { Rows.Insert(idx, newRow); OnEdited(); }
                        break;
                    }
                }
                e.Handled = true;
            };
        }

        private static LegendDragPayload? GetGroupPayload(DragEventArgs e)
        {
            var p = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
            if (p == null) return null;
            return (p.What == LegendDragPayload.Kind.PaletteCategory ||
                    p.What == LegendDragPayload.Kind.Group) ? p : null;
        }

        private LegendRowConfig? BuildNewRowFromPayload(LegendDragPayload p)
        {
            var grp = BuildGroupFromPayload(p);
            if (grp == null) return null;
            var row = new LegendRowConfig { Id = LegendIdGen.New("r") };
            row.Groups.Add(grp);
            return row;
        }

        private LegendGroupConfig? BuildGroupFromPayload(LegendDragPayload p)
        {
            if (p.What == LegendDragPayload.Kind.PaletteCategory)
                return BuildGroupFromTrade(p.SourceTradeId);
            if (p.What == LegendDragPayload.Kind.Group)
                return TakeGroup(p.GroupId);
            return null;
        }

        private LegendRowConfig? FindNearestRow(double dropY)
        {
            if (_rowsStack == null || Rows.Count == 0) return null;
            LegendRowConfig? nearest = null;
            double minDist = double.MaxValue;
            foreach (var child in _rowsStack.Children)
            {
                if (child is LemoineLegendRow rowCtrl && _rowsScroll != null)
                {
                    var pt = rowCtrl.TranslatePoint(new Point(0, rowCtrl.ActualHeight / 2), _rowsScroll);
                    double d = Math.Abs(pt.Y - dropY);
                    if (d < minDist) { minDist = d; nearest = rowCtrl.Row; }
                }
            }
            return nearest;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Rebuild the rows panel
        // ─────────────────────────────────────────────────────────────────────
        private void RebuildRows()
        {
            if (_rowsStack == null) return;
            // A row with no groups carries nothing — drop it so it never renders
            // (covers group deletion, cross-row moves, etc.).
            DropEmptyRows();
            _rowsStack.Children.Clear();
            _betweenRowBars.Clear();

            for (int i = 0; i < Rows.Count; i++)
            {
                if (i > 0)
                {
                    int capturedIdx = i - 1;
                    var bar = new Border { Height = 8, AllowDrop = true, Background = Brushes.Transparent, Margin = new Thickness(0) };
                    WireExternalDropBar(bar, DropBarRole.Between, capturedIdx);
                    _betweenRowBars.Add(bar);
                    _rowsStack.Children.Add(bar);
                }

                var row     = Rows[i];
                var rowCtrl = new LemoineLegendRow();
                rowCtrl.SetSelectionContext(_lSelectedBlockIds, _lActiveBlockId);
                rowCtrl.Bind(row);

                rowCtrl.Changed                 += (s, e) => OnEdited();
                rowCtrl.BlockDropRequested      += (s, args) => HandleBlockDrop(args);
                rowCtrl.GroupDropInRowRequested += (s, args) => HandleGroupDropInRow(args);
                rowCtrl.GroupDeleteRequested    += (s, gid) => { DeleteGroup(gid); OnEdited(); };
                rowCtrl.BlockClickedOnCanvas    += (bId, ctrl, shift) => OnCanvasBlockClicked(bId, ctrl, shift);

                _rowsStack.Children.Add(rowCtrl);
            }

            // ── "Add New Group" affordance ────────────────────────────────────
            var addGrpBorder = new Border
            {
                Margin          = new Thickness(8, 8, 8, 4),
                Padding         = new Thickness(10, 6, 10, 6),
                CornerRadius    = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Cursor          = Cursors.Hand,
                Background      = Brushes.Transparent,
            };
            addGrpBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var addGrpLabel = new TextBlock
            {
                Text                = "＋  Add New Group",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            addGrpLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            addGrpLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            addGrpLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            addGrpBorder.Child = addGrpLabel;

            addGrpBorder.MouseEnter += (s, e) =>
            {
                addGrpBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                addGrpBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                addGrpLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            };
            addGrpBorder.MouseLeave += (s, e) =>
            {
                addGrpBorder.Background = Brushes.Transparent;
                addGrpBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                addGrpLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            };
            addGrpBorder.MouseLeftButtonUp += (s, e) =>
            {
                var newGrp = new LegendGroupConfig
                {
                    Id = LegendIdGen.New("g"), Title = "", SourceTradeId = "",
                    Blocks = new List<LegendBlockConfig>(),
                };
                // Always start a fresh row at the bottom for a new group.
                Rows.Add(new LegendRowConfig { Id = LegendIdGen.New("r"), Groups = new List<LegendGroupConfig> { newGrp } });
                OnEdited();
            };
            _rowsStack.Children.Add(addGrpBorder);

            RefreshSelectionVisuals();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Multi-select
        // ─────────────────────────────────────────────────────────────────────
        private void OnCanvasBlockClicked(string blockId, bool ctrl, bool shift)
        {
            if (shift && _lShiftAnchorBlockId != null)
            {
                var allIds = AllBlockIdsInOrder().ToList();
                int anchorIdx  = allIds.IndexOf(_lShiftAnchorBlockId);
                int clickedIdx = allIds.IndexOf(blockId);
                if (anchorIdx >= 0 && clickedIdx >= 0)
                {
                    int lo = Math.Min(anchorIdx, clickedIdx);
                    int hi = Math.Max(anchorIdx, clickedIdx);
                    _lSelectedBlockIds.Clear();
                    for (int i = lo; i <= hi; i++) _lSelectedBlockIds.Add(allIds[i]);
                    _lActiveBlockId = blockId;
                }
                else
                {
                    _lSelectedBlockIds.Clear();
                    _lSelectedBlockIds.Add(blockId);
                    _lActiveBlockId      = blockId;
                    _lShiftAnchorBlockId = blockId;
                }
            }
            else if (ctrl)
            {
                if (_lSelectedBlockIds.Contains(blockId))
                {
                    _lSelectedBlockIds.Remove(blockId);
                    if (_lActiveBlockId == blockId)
                        _lActiveBlockId = _lSelectedBlockIds.FirstOrDefault();
                }
                else
                {
                    _lSelectedBlockIds.Add(blockId);
                    _lActiveBlockId      = blockId;
                    _lShiftAnchorBlockId = blockId;
                }
            }
            else
            {
                _lSelectedBlockIds.Clear();
                _lSelectedBlockIds.Add(blockId);
                _lActiveBlockId      = blockId;
                _lShiftAnchorBlockId = blockId;
            }

            RefreshSelectionVisuals();
            BulkEditorChanged?.Invoke(LBulkEditing ? (UIElement)BuildBulkBlockEditor() : null);
        }

        private IEnumerable<string> AllBlockIdsInOrder()
        {
            foreach (var row in Rows)
                foreach (var grp in row.Groups ?? new List<LegendGroupConfig>())
                    foreach (var b in grp.Blocks ?? new List<LegendBlockConfig>())
                        yield return b.Id;
        }

        private void RefreshSelectionVisuals()
        {
            if (_rowsStack == null) return;
            foreach (var child in _rowsStack.Children.OfType<LemoineLegendRow>())
                child.RefreshBlockSelection(_lSelectedBlockIds, _lActiveBlockId);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Bulk block editor — built on demand and handed to the host window
        // ─────────────────────────────────────────────────────────────────────
        private UIElement BuildBulkBlockEditor()
        {
            var outer = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

            var stack = new StackPanel { Margin = new Thickness(10, 10, 10, 10) };

            var header = new TextBlock { Text = "BATCH BLOCK EDIT" };
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            stack.Children.Add(header);

            stack.Children.Add(MakeBulkSep());

            var note = new TextBlock { Text = $"{_lSelectedBlockIds.Count} blocks selected.", FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 4) };
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            stack.Children.Add(note);

            stack.Children.Add(MakeBulkSep());

            var selectedBlocks = AllSelectedBlocks().ToList();

            // COLOR
            {
                string firstColor = selectedBlocks.FirstOrDefault()?.Color ?? "#888888";
                var colorRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) };
                var colorLabel   = MakeBulkRowLabel("COLOR");
                var swatchBorder = new Border
                {
                    Width = 22, Height = 14, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(BrushHelper.ColorFromHex(firstColor, LemoineTheme.FallbackGrey)),
                    Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                swatchBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                swatchBorder.MouseLeftButtonUp += (s, e) =>
                {
                    var initial = BrushHelper.ColorFromHex(
                        AllSelectedBlocks().FirstOrDefault()?.Color ?? "#888888",
                        LemoineTheme.FallbackGrey);

                    var container = new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1) };
                    container.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
                    container.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

                    var win = Window.GetWindow(this);
                    if (win != null)
                        foreach (var key in win.Resources.Keys)
                            container.Resources[key] = win.Resources[key];

                    var pickerPanel = new LemoineColorPickerPanel { SelectedColor = initial };
                    var applyBtn  = LemoineControlStyles.BuildButton("Apply Color", LemoineControlStyles.LemoineButtonVariant.Primary);
                    var cancelBtn = LemoineControlStyles.BuildButton("Cancel",      LemoineControlStyles.LemoineButtonVariant.Ghost);
                    cancelBtn.Margin = new Thickness(0, 0, 6, 0);

                    var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
                    btnRow.Children.Add(cancelBtn);
                    btnRow.Children.Add(applyBtn);

                    var content = new StackPanel();
                    content.Children.Add(pickerPanel);
                    content.Children.Add(btnRow);
                    container.Child = content;

                    var popup = new Popup
                    {
                        PlacementTarget = swatchBorder, Placement = PlacementMode.Bottom,
                        StaysOpen = false, AllowsTransparency = false, Child = container,
                    };

                    applyBtn.Click += (as2, ae) =>
                    {
                        var c   = pickerPanel.SelectedColor;
                        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                        foreach (var b in AllSelectedBlocks()) { b.Color = hex; b.ColorOverride = true; }
                        pickerPanel.AddToRecent(c);
                        swatchBorder.Background = new SolidColorBrush(c);
                        popup.IsOpen = false;
                        OnEdited();
                    };
                    cancelBtn.Click += (cs2, ce) => popup.IsOpen = false;
                    popup.IsOpen = true;
                };
                colorRow.Children.Add(colorLabel);
                colorRow.Children.Add(swatchBorder);
                stack.Children.Add(colorRow);
            }

            // VISIBLE
            {
                bool anyVisible = selectedBlocks.Any(b => b.Visible);
                var visRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) };
                var visLabel = MakeBulkRowLabel("VISIBLE");
                var eyeBtn = new Border
                {
                    Width = 28, Height = 22, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                    Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 0, 0),
                    Child = LemoineEyeGlyph.Make(anyVisible, size: 16),
                };
                eyeBtn.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                eyeBtn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                bool currentState = anyVisible;
                eyeBtn.MouseLeftButtonUp += (s, e) =>
                {
                    currentState = !currentState;
                    foreach (var b in AllSelectedBlocks()) b.Visible = currentState;
                    eyeBtn.Child = LemoineEyeGlyph.Make(currentState, size: 16);
                    OnEdited();
                };
                visRow.Children.Add(visLabel);
                visRow.Children.Add(eyeBtn);
                stack.Children.Add(visRow);
            }

            stack.Children.Add(MakeBulkSep());

            // Delete All
            bool _confirmDelete = false;
            var deleteRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var deleteBorder = new Border
            {
                Padding = new Thickness(10, 4, 10, 4), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand,
            };
            deleteBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineRed");
            deleteBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");

            var deleteTb = new TextBlock { Text = "Delete All ✕", VerticalAlignment = VerticalAlignment.Center };
            deleteTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            deleteTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            deleteTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            deleteBorder.Child = deleteTb;

            deleteBorder.MouseEnter += (s, e) => deleteBorder.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
            deleteBorder.MouseLeave += (s, e) => deleteBorder.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            deleteBorder.MouseLeftButtonUp += (s, e) =>
            {
                if (!_confirmDelete) { deleteTb.Text = "Confirm — click again"; _confirmDelete = true; }
                else
                {
                    var ids = new HashSet<string>(_lSelectedBlockIds);
                    foreach (var row in Rows)
                        foreach (var grp in row.Groups ?? new List<LegendGroupConfig>())
                            grp.Blocks?.RemoveAll(b => ids.Contains(b.Id));
                    DropEmptyRows();
                    _lSelectedBlockIds.Clear();
                    _lActiveBlockId      = null;
                    _lShiftAnchorBlockId = null;
                    OnEdited();
                }
            };

            deleteRow.Children.Add(deleteBorder);
            stack.Children.Add(deleteRow);

            outer.Content = stack;
            return outer;
        }

        private IEnumerable<LegendBlockConfig> AllSelectedBlocks()
        {
            foreach (var row in Rows)
                foreach (var grp in row.Groups ?? new List<LegendGroupConfig>())
                    foreach (var b in grp.Blocks ?? new List<LegendBlockConfig>())
                        if (_lSelectedBlockIds.Contains(b.Id))
                            yield return b;
        }

        private static TextBlock MakeBulkRowLabel(string text)
        {
            var tb = new TextBlock { Text = text, Width = 52, VerticalAlignment = VerticalAlignment.Center };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        private static Border MakeBulkSep()
        {
            var sep = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6) };
            sep.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
            return sep;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Drop handlers (from row controls)
        // ─────────────────────────────────────────────────────────────────────
        private void HandleBlockDrop(BlockDropArgs args)
        {
            var p   = args.Payload;
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
                    var block = new LegendBlockConfig { Id=LegendIdGen.New("b"), Name="Custom", Custom=true, Color="#888888", Kind="square", Fill="solid", Visible=true };
                    int idx = Math.Max(0, Math.Min(args.TargetIndex, grp.Blocks.Count));
                    grp.Blocks.Insert(idx, block);
                    break;
                }
                case LegendDragPayload.Kind.Block:
                {
                    if (!TryTakeBlock(p.BlockId, out var block, out var fromGrp)) return;
                    int idx = args.TargetIndex;
                    if (idx > grp.Blocks.Count) idx = grp.Blocks.Count;
                    grp.Blocks.Insert(idx, block!);
                    break;
                }
            }
            DropEmptyRows();
            OnEdited();
        }

        private void HandleGroupDropInRow(GroupDropArgs args)
        {
            var p   = args.Payload;
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

        // ─────────────────────────────────────────────────────────────────────
        // Buffer mutations
        // ─────────────────────────────────────────────────────────────────────
        private LegendGroupConfig BuildGroupFromTrade(string tradeId)
        {
            var trade = AutoFiltersSettings.Instance.Trades?.FirstOrDefault(t => t.Id == tradeId);
            var grp = new LegendGroupConfig
            {
                Id = LegendIdGen.New("g"),
                Title = (trade?.Label ?? "Custom").ToUpperInvariant(),
                SourceTradeId = trade?.Id ?? "",
            };
            if (trade?.Rules != null)
            {
                foreach (var rule in trade.Rules.Where(r => r.Enabled)
                             .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                {
                    grp.Blocks.Add(new LegendBlockConfig
                    {
                        Id = LegendIdGen.New("b"), Name = rule.Name ?? "",
                        SourceTradeId = trade.Id, SourceRuleId = rule.Id,
                        Color = rule.SurfColor ?? "#888888", Kind = "square", Fill = "solid", Visible = true,
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
            if (rule == null) return null;
            return new LegendBlockConfig
            {
                Id = LegendIdGen.New("b"), Name = rule.Name ?? "",
                SourceTradeId = trade.Id, SourceRuleId = rule.Id,
                Color = rule.SurfColor ?? "#888888", Kind = "square", Fill = "solid", Visible = true,
            };
        }

        private LegendGroupConfig? TakeGroup(string groupId)
        {
            foreach (var row in Rows)
            {
                int i = row.Groups.FindIndex(g => g.Id == groupId);
                if (i >= 0) { var g = row.Groups[i]; row.Groups.RemoveAt(i); return g; }
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
                if (i >= 0) { block = grp.Blocks[i]; fromGroup = grp; grp.Blocks.RemoveAt(i); return true; }
            }
            return false;
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
        // Preview
        // ─────────────────────────────────────────────────────────────────────
        // The live preview overlay is now hosted by the settings window (so it can
        // grow from/to the floating Preview pill). This control just exposes the
        // current Layout/Rows and raises <see cref="Edited"/> whenever the buffer
        // changes so the host can refresh its preview.

        // ─────────────────────────────────────────────────────────────────────
        // Templates popup (called by host window's sidebar Templates button)
        // ─────────────────────────────────────────────────────────────────────
        private void ShowLegendTemplatesPopup(UIElement anchor)
        {
            var store = LegendCreatorSettings.Templates;

            var popup = new Popup
            {
                PlacementTarget    = anchor,
                Placement          = PlacementMode.Bottom,
                StaysOpen          = false,
                AllowsTransparency = true,
                PopupAnimation     = PopupAnimation.Fade,
                MinWidth           = 270,
            };

            var outer = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Margin          = new Thickness(0, 4, 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 12, Opacity = 0.22, ShadowDepth = 4, Direction = 270,
                },
            };
            outer.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var root = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };

            void AddSectionHeader(string text)
            {
                var tb = new TextBlock { Text = text, Margin = new Thickness(12, 8, 12, 4) };
                tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                root.Children.Add(tb);
            }

            void AddSep()
            {
                var sep = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6) };
                sep.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
                root.Children.Add(sep);
            }

            Border AddMenuRow(string icon, string label, Action onClick)
            {
                var row = new Border
                {
                    Padding = new Thickness(10, 4, 12, 4), CornerRadius = new CornerRadius(12),
                    BorderThickness = new Thickness(1), Margin = new Thickness(4, 2, 4, 2), Cursor = Cursors.Hand,
                };
                row.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                row.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

                var iconTb = new TextBlock { Text = icon, Width = 18, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
                iconTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                iconTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                iconTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                var labelTb = new TextBlock { Text = label, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
                labelTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                labelTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                labelTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(iconTb);
                content.Children.Add(labelTb);
                row.Child = content;

                row.MouseEnter += (s, e) => { row.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim"); row.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent"); };
                row.MouseLeave += (s, e) => { row.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");    row.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder"); };
                row.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
                root.Children.Add(row);
                return row;
            }

            // ── Saved templates list ──────────────────────────────────────────
            AddSectionHeader("SAVED TEMPLATES");
            var templateListPanel = new StackPanel();

            void RebuildTemplateList()
            {
                templateListPanel.Children.Clear();
                var current = store.List();
                if (current.Count == 0)
                {
                    var empty = new TextBlock { Text = "No saved templates yet.", Margin = new Thickness(12, 4, 12, 4), FontStyle = FontStyles.Italic };
                    empty.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    empty.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    empty.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                    templateListPanel.Children.Add(empty);
                    return;
                }

                foreach (var tmpl in current)
                {
                    var t          = tmpl;
                    var pillBorder = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Margin = new Thickness(4, 2, 4, 2), Cursor = Cursors.Hand, ClipToBounds = true };
                    pillBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                    pillBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                    pillBorder.MouseEnter += (s, e) => { pillBorder.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim"); pillBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent"); };
                    pillBorder.MouseLeave += (s, e) => { pillBorder.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");    pillBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder"); };

                    var rowGrid = new Grid();
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var loadBorder = new Border { Padding = new Thickness(10, 4, 6, 4) };
                    Grid.SetColumn(loadBorder, 0);

                    var nameStack = new StackPanel();
                    var nameTb    = new TextBlock { Text = t.Name };
                    nameTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    nameTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    nameTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                    var dateTb = new TextBlock { Text = t.Created.ToString("MMM d, yyyy"), Margin = new Thickness(0, 1, 0, 0) };
                    dateTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    dateTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    dateTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

                    nameStack.Children.Add(nameTb);
                    nameStack.Children.Add(dateTb);
                    loadBorder.Child = nameStack;

                    loadBorder.MouseLeftButtonUp += (s, e) =>
                    {
                        if (e.OriginalSource is FrameworkElement src)
                        {
                            var el = src;
                            while (el != null && el != rowGrid)
                            {
                                if (el.Tag as string == "deleteBtn") { e.Handled = true; return; }
                                el = VisualTreeHelper.GetParent(el) as FrameworkElement;
                            }
                        }
                        e.Handled = true;
                        popup.IsOpen = false;
                        if (store.Load(t, out var loaded, out _) && loaded != null)
                        {
                            // Load from the first legend entry in the template
                            var entry = loaded.Legends?.Count > 0 ? loaded.Legends[0] : null;
                            if (entry != null)
                            {
                                Layout = LegendCreatorSettings.DeepCopy(entry.Layout ?? new LegendLayoutConfig());
                                Rows   = LegendCreatorSettings.DeepCopy(entry.Rows   ?? new List<LegendRowConfig>());
                            }
                            else
                            {
                                Layout = new LegendLayoutConfig();
                                Rows   = new List<LegendRowConfig>();
                            }
                            BuildAll();
                        }
                        else
                        {
                            MessageBox.Show("Failed to load template.", "Template Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    };

                    rowGrid.Children.Add(loadBorder);

                    var delBtn = BuildTemplateDeleteButton(() => { store.Delete(t, out _); RebuildTemplateList(); });
                    ((FrameworkElement)delBtn).Tag               = "deleteBtn";
                    ((FrameworkElement)delBtn).Margin            = new Thickness(0, 0, 6, 0);
                    ((FrameworkElement)delBtn).VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn((UIElement)delBtn, 1);
                    rowGrid.Children.Add((UIElement)delBtn);

                    pillBorder.Child = rowGrid;
                    templateListPanel.Children.Add(pillBorder);
                }
            }

            RebuildTemplateList();
            root.Children.Add(templateListPanel);

            // ── Save current as template ──────────────────────────────────────
            AddSep();
            var saveRowHost = new Border { Margin = new Thickness(4, 0, 4, 0) };

            void ShowSaveInputState()
            {
                var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
                var nameBox  = new TextBox { Width = 140, MaxLength = 50, VerticalContentAlignment = VerticalAlignment.Center };
                nameBox.SetResourceReference(TextBox.BackgroundProperty,  "LemoineSelectBg");
                nameBox.SetResourceReference(TextBox.ForegroundProperty,  "LemoineText");
                nameBox.SetResourceReference(TextBox.BorderBrushProperty, "LemoineBorder");
                nameBox.SetResourceReference(TextBox.FontSizeProperty,    "LemoineFS_SM");
                nameBox.SetResourceReference(TextBox.FontFamilyProperty,  "LemoineMonoFont");

                void DoSave()
                {
                    var name = nameBox.Text.Trim();
                    if (string.IsNullOrEmpty(name)) return;
                    var snapshot = new LegendCreatorSettings
                    {
                        Legends = new List<LegendEntry>
                        {
                            new LegendEntry
                            {
                                Id     = LegendIdGen.New("legend"),
                                Layout = LegendCreatorSettings.DeepCopy(Layout),
                                Rows   = LegendCreatorSettings.DeepCopy(Rows),
                                PreviewVisible = _previewVisible,
                            }
                        },
                    };
                    if (store.Save(name, snapshot, out string? err))
                    {
                        RebuildTemplateList();
                        ShowSaveButtonState();
                    }
                    else
                    {
                        MessageBox.Show("Save failed: " + err, "Template Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }

                nameBox.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Return) { e.Handled = true; DoSave(); }
                    if (e.Key == Key.Escape) { e.Handled = true; ShowSaveButtonState(); }
                };

                var confirmBtn = MakeFlatSmBtn("Save");
                confirmBtn.Margin = new Thickness(6, 0, 0, 0);
                confirmBtn.Click += (s, e) => DoSave();

                var cancelBtn = MakeFlatSmBtn("✕");
                cancelBtn.Margin = new Thickness(4, 0, 0, 0);
                cancelBtn.Click += (s, e) => ShowSaveButtonState();

                inputRow.Children.Add(nameBox);
                inputRow.Children.Add(confirmBtn);
                inputRow.Children.Add(cancelBtn);
                saveRowHost.Child = inputRow;

                nameBox.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                    new Action(() => nameBox.Focus()));
            }

            void ShowSaveButtonState()
            {
                var btn = new Border
                {
                    Padding = new Thickness(10, 4, 12, 4), CornerRadius = new CornerRadius(12),
                    BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Margin = new Thickness(4, 2, 4, 2),
                };
                btn.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                btn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

                var icon = new TextBlock { Text = "＋", Width = 18, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
                icon.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                icon.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                icon.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                var lbl = new TextBlock { Text = "Save Current as Template", Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(icon);
                row.Children.Add(lbl);
                btn.Child = row;

                btn.MouseEnter += (s, e) => { btn.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim"); btn.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent"); };
                btn.MouseLeave += (s, e) => { btn.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");    btn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder"); };
                btn.MouseLeftButtonUp += (s, e) => { e.Handled = true; ShowSaveInputState(); };
                saveRowHost.Child = btn;
            }

            ShowSaveButtonState();
            root.Children.Add(saveRowHost);

            // ── File operations ───────────────────────────────────────────────
            AddSep();
            AddSectionHeader("FILE");

            AddMenuRow("↑", "Import from File", () =>
            {
                popup.IsOpen = false;
                var dlg = new OpenFileDialog { Title = "Import Legend Template", Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*" };
                if (dlg.ShowDialog() == true)
                {
                    var loaded = LegendCreatorSettings.TryLoad(dlg.FileName);
                    var entry  = loaded?.Legends?.Count > 0 ? loaded.Legends[0] : null;
                    if (entry != null)
                    {
                        Layout = LegendCreatorSettings.DeepCopy(entry.Layout ?? new LegendLayoutConfig());
                        Rows   = LegendCreatorSettings.DeepCopy(entry.Rows   ?? new List<LegendRowConfig>());
                        BuildAll();
                    }
                    else
                    {
                        MessageBox.Show("Failed to import file.", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            });

            AddMenuRow("↓", "Export to File", () =>
            {
                popup.IsOpen = false;
                var dlg = new SaveFileDialog { Title = "Export Legend Template", Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*", DefaultExt = "xml" };
                if (dlg.ShowDialog() == true)
                {
                    var snapshot = new LegendCreatorSettings
                    {
                        Legends = new List<LegendEntry>
                        {
                            new LegendEntry
                            {
                                Id     = LegendIdGen.New("legend"),
                                Layout = LegendCreatorSettings.DeepCopy(Layout),
                                Rows   = LegendCreatorSettings.DeepCopy(Rows),
                                PreviewVisible = _previewVisible,
                            }
                        },
                    };
                    LegendCreatorSettings.ExportTo(dlg.FileName, snapshot);
                }
            });

            outer.Child = root;
            popup.Child = outer;
            popup.IsOpen = true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Popup helpers
        // ─────────────────────────────────────────────────────────────────────
        private static Button MakeFlatSmBtn(string text)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 3, 8, 3), FocusVisualStyle = null };
            b.SetResourceReference(Control.ForegroundProperty,  "LemoineText");
            b.SetResourceReference(Control.FontFamilyProperty,  "LemoineMonoFont");
            b.SetResourceReference(Control.FontSizeProperty,    "LemoineFS_SM");
            b.SetResourceReference(Control.BackgroundProperty,  "LemoineRaised");
            b.SetResourceReference(Control.BorderBrushProperty, "LemoineBorder");
            b.Template = LemoineControlStyles.BuildFlatButtonTemplate();
            return b;
        }

        private UIElement BuildTemplateDeleteButton(Action onConfirm)
        {
            bool confirmed = false;
            var btn = new Border { Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
            btn.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            btn.SetResourceReference(Border.BorderBrushProperty, "LemoineRed");

            var tb = new TextBlock { Text = "✕" };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            btn.Child = tb;

            btn.MouseEnter += (s, e) => btn.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
            btn.MouseLeave += (s, e) => btn.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            btn.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (!confirmed) { tb.Text = "Sure?"; confirmed = true; }
                else onConfirm();
            };
            return btn;
        }

        // ─────────────────────────────────────────────────────────────────────
        // After-edit hooks
        // ─────────────────────────────────────────────────────────────────────
        private void OnEdited()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RebuildRows();
                BulkEditorChanged?.Invoke(LBulkEditing ? (UIElement)BuildBulkBlockEditor() : null);
                Edited?.Invoke();
                UpdateCounts();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void UpdateCounts()
        {
            if (_countsTb == null) return;
            int rows   = Rows.Count;
            int groups = Rows.Sum(r => r.Groups?.Count ?? 0);
            int blocks = Rows.SelectMany(r => r.Groups ?? new List<LegendGroupConfig>()).Sum(g => g.Blocks?.Count ?? 0);
            int hidden = Rows.SelectMany(r => r.Groups ?? new List<LegendGroupConfig>())
                             .SelectMany(g => g.Blocks ?? new List<LegendBlockConfig>())
                             .Count(b => !b.Visible);
            _countsTb.Text = $"● {blocks} entries · {groups} groups · {rows} rows · {hidden} hidden";
        }

        // ─────────────────────────────────────────────────────────────────────
        // Event handlers
        // ─────────────────────────────────────────────────────────────────────
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            AutoFiltersSettings.Saved -= OnFiltersSaved;
        }

        internal void OnFiltersSaved()
        {
            Dispatcher.Invoke(() =>
            {
                RebuildRows();
                Edited?.Invoke();
                UpdateCounts();
            });
        }
    }
}
