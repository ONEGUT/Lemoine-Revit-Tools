using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LemoineTools.Lemoine;
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

        // Single live-marker group placement (Option A).
        // _markerLayer overlays the rows viewport; it is hit-testable ONLY while a
        // group-compatible drag is in flight (toggled from LegendDragSession), so the
        // block drag/drop system underneath is never intercepted. _marker is the one
        // accent indicator (vertical bar = in-row gutter, horizontal lane = new row).
        private Canvas?           _markerLayer;
        private Border?           _marker;
        private GroupDropTarget?  _pendingDrop;

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
            // Named handlers (detached in OnUnloaded) toggle the placement overlay.
            LegendDragSession.Started += OnDragSessionStarted;
            LegendDragSession.Ended   += OnDragSessionEnded;
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
        // Canvas grid — rows scroll + a single-marker placement overlay on top.
        // The overlay (Canvas) only becomes hit-testable while a group-compatible
        // drag is in flight (see OnDragSessionStarted), so it never intercepts the
        // block drag/drop happening inside the cards underneath it.
        // ─────────────────────────────────────────────────────────────────────
        private Grid BuildCanvasGrid()
        {
            var grid = new Grid();

            _rowsScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0, 4, 0, 4),
            };
            _rowsStack = new StackPanel { Orientation = Orientation.Vertical };
            _rowsScroll.Content = _rowsStack;
            LemoineControlStyles.WireBubblingScroll(_rowsScroll); // bubble wheel to parent at scroll limits
            grid.Children.Add(_rowsScroll);

            // Overlay: transparent so it is hit-testable when enabled, but inert
            // (IsHitTestVisible = false) until a group drag begins.
            _markerLayer = new Canvas
            {
                Background        = Brushes.Transparent,
                AllowDrop         = true,
                IsHitTestVisible  = false,
            };
            _markerLayer.DragOver  += OnOverlayDragOver;
            _markerLayer.DragLeave += OnOverlayDragLeave;
            _markerLayer.Drop      += OnOverlayDrop;

            _marker = new Border
            {
                CornerRadius     = new CornerRadius(2),
                Visibility       = Visibility.Collapsed,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9,
                    Color = System.Windows.Media.Color.FromRgb(0x4f, 0x8f, 0xc4),
                },
            };
            _marker.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
            _markerLayer.Children.Add(_marker);

            grid.Children.Add(_markerLayer);
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
        // Group placement overlay (single live marker)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>One resolved landing spot for a dragged group.</summary>
        private sealed class GroupDropTarget
        {
            public bool   NewRow;       // true = insert a brand-new row at RowIndex
            public int    RowIndex;     // target row (existing row for in-row; insert pos for new row)
            public int    GroupIndex;   // insert index within the row (in-row only)
            // Marker geometry, in overlay (viewport) coordinates.
            public bool   Vertical;     // true = in-row gutter bar; false = new-row lane
            public double X, Y, W, H;
        }

        // Toggle the overlay's hit-testing with the drag session so it captures
        // group/category drags but stays inert for block drags.
        private void OnDragSessionStarted(LegendDragPayload p)
        {
            if (_markerLayer == null) return;
            if (p.What == LegendDragPayload.Kind.Group ||
                p.What == LegendDragPayload.Kind.PaletteCategory)
                _markerLayer.IsHitTestVisible = true;
        }

        private void OnDragSessionEnded()
        {
            if (_markerLayer != null) _markerLayer.IsHitTestVisible = false;
            HideMarker();
            _pendingDrop = null;
        }

        private void OnOverlayDragOver(object sender, DragEventArgs e)
        {
            var p = GetGroupPayload(e);
            if (p == null) { e.Effects = DragDropEffects.None; HideMarker(); _pendingDrop = null; e.Handled = true; return; }

            _pendingDrop = ComputeGroupDropTarget(e.GetPosition(_markerLayer));
            if (_pendingDrop != null) DrawMarker(_pendingDrop);
            else HideMarker();

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void OnOverlayDragLeave(object sender, DragEventArgs e)
        {
            HideMarker();
            _pendingDrop = null;
        }

        private void OnOverlayDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            HideMarker();
            var p = GetGroupPayload(e);
            if (p == null) { _pendingDrop = null; return; }

            var target = _pendingDrop ?? ComputeGroupDropTarget(e.GetPosition(_markerLayer));
            _pendingDrop = null;
            if (target == null) return;
            ApplyGroupDrop(p, target);
        }

        // Decide the single landing spot for the cursor, in overlay coordinates.
        private GroupDropTarget? ComputeGroupDropTarget(Point p)
        {
            if (_rowsStack == null || _markerLayer == null) return null;

            var rows = _rowsStack.Children.OfType<LemoineLegendRow>().ToList();
            double laneW = Math.Max(40, _markerLayer.ActualWidth - 20);

            // Empty legend → one new-row target at the top.
            if (rows.Count == 0)
                return new GroupDropTarget { NewRow = true, RowIndex = 0, Vertical = false, X = 10, Y = 8, W = laneW, H = 4 };

            // Row vertical spans in overlay space.
            var tops    = new double[rows.Count];
            var bottoms = new double[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                var origin = rows[i].TranslatePoint(new Point(0, 0), _markerLayer);
                tops[i]    = origin.Y;
                bottoms[i] = origin.Y + rows[i].ActualHeight;
            }

            // Above the first row → new row at 0.
            if (p.Y < tops[0])
                return NewRowTarget(0, tops[0] - 4, laneW);

            for (int i = 0; i < rows.Count; i++)
            {
                if (p.Y <= bottoms[i])
                {
                    if (p.Y >= tops[i])
                        return InRowTarget(rows[i], i, p.X, tops[i], bottoms[i] - tops[i]);
                    // In the gap above row i (between row i-1 and i) → new row at i.
                    double midY = i > 0 ? (bottoms[i - 1] + tops[i]) / 2.0 : tops[i] - 4;
                    return NewRowTarget(i, midY, laneW);
                }
            }

            // Below the last row → new row at the end.
            return NewRowTarget(rows.Count, bottoms[rows.Count - 1] + 4, laneW);
        }

        private GroupDropTarget NewRowTarget(int rowIndex, double y, double laneW) =>
            new GroupDropTarget { NewRow = true, RowIndex = rowIndex, Vertical = false, X = 10, Y = y - 2, W = laneW, H = 4 };

        private GroupDropTarget InRowTarget(LemoineLegendRow row, int rowIndex, double xOverlay, double rowTop, double rowH)
        {
            var cards = row.GroupCards;
            if (cards.Count == 0)
                return new GroupDropTarget { NewRow = false, RowIndex = rowIndex, GroupIndex = 0, Vertical = true, X = 6, Y = rowTop, W = 4, H = rowH };

            var lefts  = new double[cards.Count];
            var rights = new double[cards.Count];
            for (int j = 0; j < cards.Count; j++)
            {
                var o = cards[j].TranslatePoint(new Point(0, 0), _markerLayer!);
                lefts[j]  = o.X;
                rights[j] = o.X + cards[j].ActualWidth;
            }

            int insert = cards.Count;
            double markerX = rights[cards.Count - 1] + 2;
            for (int j = 0; j < cards.Count; j++)
            {
                double center = (lefts[j] + rights[j]) / 2.0;
                if (xOverlay < center)
                {
                    insert = j;
                    markerX = j == 0 ? lefts[0] - 2 : (rights[j - 1] + lefts[j]) / 2.0;
                    break;
                }
            }
            return new GroupDropTarget
            {
                NewRow = false, RowIndex = rowIndex, GroupIndex = insert,
                Vertical = true, X = markerX - 2, Y = rowTop, W = 4, H = rowH,
            };
        }

        private void DrawMarker(GroupDropTarget t)
        {
            if (_marker == null || _markerLayer == null) return;
            _marker.Width  = Math.Max(4, t.W);
            _marker.Height = Math.Max(4, t.H);
            // Clamp into the visible viewport so the marker never paints off-screen.
            double x = Math.Max(0, Math.Min(t.X, Math.Max(0, _markerLayer.ActualWidth  - _marker.Width)));
            double y = Math.Max(0, Math.Min(t.Y, Math.Max(0, _markerLayer.ActualHeight - _marker.Height)));
            Canvas.SetLeft(_marker, x);
            Canvas.SetTop(_marker, y);
            _marker.Visibility = Visibility.Visible;
        }

        private void HideMarker()
        {
            if (_marker != null) _marker.Visibility = Visibility.Collapsed;
        }

        // Commit a group drop: move an existing group, or create one from a category.
        private void ApplyGroupDrop(LegendDragPayload p, GroupDropTarget t)
        {
            if (p.What == LegendDragPayload.Kind.Group)
            {
                // Resolve source location BEFORE removal so we can correct same-row indices.
                FindGroupLocation(p.GroupId, out var srcRow, out int srcIdx);
                var grp = TakeGroup(p.GroupId);
                if (grp == null) return;

                if (t.NewRow)
                {
                    // The now-empty source row (if any) is collapsed by DropEmptyRows after
                    // insertion, which keeps the new row's position correct.
                    int idx = Math.Max(0, Math.Min(t.RowIndex, Rows.Count));
                    Rows.Insert(idx, new LegendRowConfig { Id = LegendIdGen.New("r"), Groups = new List<LegendGroupConfig> { grp } });
                }
                else
                {
                    int ri = Math.Max(0, Math.Min(t.RowIndex, Rows.Count - 1));
                    var row = Rows[ri];
                    int gi = t.GroupIndex;
                    if (ReferenceEquals(row, srcRow) && srcIdx >= 0 && srcIdx < gi) gi--; // removal shifted indices
                    gi = Math.Max(0, Math.Min(gi, row.Groups.Count));
                    row.Groups.Insert(gi, grp);
                }
            }
            else if (p.What == LegendDragPayload.Kind.PaletteCategory)
            {
                var grp = BuildGroupFromTrade(p.SourceTradeId);
                if (t.NewRow)
                {
                    int idx = Math.Max(0, Math.Min(t.RowIndex, Rows.Count));
                    Rows.Insert(idx, new LegendRowConfig { Id = LegendIdGen.New("r"), Groups = new List<LegendGroupConfig> { grp } });
                }
                else
                {
                    int ri = Math.Max(0, Math.Min(t.RowIndex, Rows.Count - 1));
                    int gi = Math.Max(0, Math.Min(t.GroupIndex, Rows[ri].Groups.Count));
                    Rows[ri].Groups.Insert(gi, grp);
                }
            }
            else return;

            DropEmptyRows();
            OnEdited();
        }

        private void FindGroupLocation(string groupId, out LegendRowConfig? row, out int index)
        {
            foreach (var r in Rows)
            {
                int i = r.Groups.FindIndex(g => g.Id == groupId);
                if (i >= 0) { row = r; index = i; return; }
            }
            row = null; index = -1;
        }

        private static LegendDragPayload? GetGroupPayload(DragEventArgs e)
        {
            var p = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
            if (p == null) return null;
            return (p.What == LegendDragPayload.Kind.PaletteCategory ||
                    p.What == LegendDragPayload.Kind.Group) ? p : null;
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

            for (int i = 0; i < Rows.Count; i++)
            {
                if (i > 0) _rowsStack.Children.Add(MakeRowDivider());

                var row     = Rows[i];
                var rowCtrl = new LemoineLegendRow();
                rowCtrl.SetSelectionContext(_lSelectedBlockIds, _lActiveBlockId);
                rowCtrl.Bind(row);

                rowCtrl.Changed              += (s, e) => OnEdited();
                rowCtrl.BlockDropRequested   += (s, args) => HandleBlockDrop(args);
                rowCtrl.GroupDeleteRequested += (s, gid) => { DeleteGroup(gid); OnEdited(); };
                rowCtrl.BlockClickedOnCanvas += (bId, ctrl, shift) => OnCanvasBlockClicked(bId, ctrl, shift);

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
                Text                = LemoineStrings.T("testing.legendCreator.builder.builder.canvas.addNewGroup"),
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

        // Visible, non-interactive lane boundary. The vertical gap it creates also
        // serves as the "new row" band the placement overlay snaps its lane marker to.
        private Border MakeRowDivider()
        {
            var d = new Border { Height = 1, Margin = new Thickness(12, 7, 12, 7), IsHitTestVisible = false };
            d.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
            return d;
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

            var header = new TextBlock { Text = LemoineStrings.T("testing.legendCreator.builder.builder.bulkEditor.header") };
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            stack.Children.Add(header);

            stack.Children.Add(MakeBulkSep());

            var note = new TextBlock { Text = LemoineStrings.T("testing.legendCreator.builder.builder.bulkEditor.selectedCount", _lSelectedBlockIds.Count), FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 4) };
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
                var colorLabel   = MakeBulkRowLabel(LemoineStrings.T("testing.legendCreator.builder.builder.bulkEditor.colorLabel"));
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
                    var applyBtn  = LemoineControlStyles.BuildButton(LemoineStrings.T("testing.legendCreator.builder.builder.bulkEditor.applyColor"), LemoineControlStyles.LemoineButtonVariant.Primary);
                    var cancelBtn = LemoineControlStyles.BuildButton(LemoineStrings.T("testing.legendCreator.builder.builder.bulkEditor.cancel"),      LemoineControlStyles.LemoineButtonVariant.Ghost);
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
                var visLabel = MakeBulkRowLabel(LemoineStrings.T("testing.legendCreator.builder.builder.bulkEditor.visibleLabel"));
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

            var deleteTb = new TextBlock { Text = LemoineStrings.T("testing.legendCreator.builder.builder.bulkEditor.deleteAll"), VerticalAlignment = VerticalAlignment.Center };
            deleteTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            deleteTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            deleteTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            deleteBorder.Child = deleteTb;

            deleteBorder.MouseEnter += (s, e) => deleteBorder.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
            deleteBorder.MouseLeave += (s, e) => deleteBorder.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            deleteBorder.MouseLeftButtonUp += (s, e) =>
            {
                if (!_confirmDelete) { deleteTb.Text = LemoineStrings.T("testing.legendCreator.builder.builder.bulkEditor.confirmDelete"); _confirmDelete = true; }
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
                    var block = new LegendBlockConfig { Id=LegendIdGen.New("b"), Name=LemoineStrings.T("testing.legendCreator.builder.builder.defaults.customLabel"), Custom=true, Color="#888888", Kind="square", Fill="solid", Visible=true };
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

        // ─────────────────────────────────────────────────────────────────────
        // Buffer mutations
        // ─────────────────────────────────────────────────────────────────────
        private LegendGroupConfig BuildGroupFromTrade(string tradeId)
        {
            var trade = AutoFiltersSettings.Instance.Trades?.FirstOrDefault(t => t.Id == tradeId);
            var grp = new LegendGroupConfig
            {
                Id = LegendIdGen.New("g"),
                Title = (trade?.Label ?? LemoineStrings.T("testing.legendCreator.builder.builder.defaults.customLabel")).ToUpperInvariant(),
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
            AddSectionHeader(LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.savedTemplatesHeader"));
            var templateListPanel = new StackPanel();

            void RebuildTemplateList()
            {
                templateListPanel.Children.Clear();
                var current = store.List();
                if (current.Count == 0)
                {
                    var empty = new TextBlock { Text = LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.noSavedTemplates"), Margin = new Thickness(12, 4, 12, 4), FontStyle = FontStyles.Italic };
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
                            MessageBox.Show(LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.loadFailedMessage"), LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.templateErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                        MessageBox.Show(LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.saveFailedMessage", err), LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.templateErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }

                nameBox.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Return) { e.Handled = true; DoSave(); }
                    if (e.Key == Key.Escape) { e.Handled = true; ShowSaveButtonState(); }
                };

                var confirmBtn = MakeFlatSmBtn(LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.saveButton"));
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

                var lbl = new TextBlock { Text = LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.saveCurrentAsTemplate"), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
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
            AddSectionHeader(LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.fileSectionHeader"));

            AddMenuRow("↑", LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.importFromFile"), () =>
            {
                popup.IsOpen = false;
                var dlg = new OpenFileDialog { Title = LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.importDialogTitle"), Filter = LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.xmlFileFilter") };
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
                        MessageBox.Show(LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.importFailedMessage"), LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.importErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            });

            AddMenuRow("↓", LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.exportToFile"), () =>
            {
                popup.IsOpen = false;
                var dlg = new SaveFileDialog { Title = LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.exportDialogTitle"), Filter = LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.xmlFileFilter"), DefaultExt = "xml" };
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
                if (!confirmed) { tb.Text = LemoineStrings.T("testing.legendCreator.builder.builder.templatesPopup.deleteConfirm"); confirmed = true; }
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
            LegendDragSession.Started -= OnDragSessionStarted;
            LegendDragSession.Ended   -= OnDragSessionEnded;
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
