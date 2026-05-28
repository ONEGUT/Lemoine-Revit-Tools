using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.Testing.LegendCreator;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Single category card. Header (drag handle) + body containing block rows
    /// with between-block drop zones, plus an "empty body" drop zone when blocks
    /// are absent.
    ///
    /// Owns no state — operates against <see cref="Group"/> in place. Raises
    /// <see cref="Changed"/> after any mutation so the parent builder can save
    /// and refresh dependent UI (counts, preview).
    ///
    /// Drop / drag-source routing is centralised on the builder via
    /// <see cref="DropRequested"/> and <see cref="DragInitiated"/>; the card
    /// just emits intents.
    /// </summary>
    public partial class LemoineLegendGroupCard : UserControl
    {
        public LegendGroupConfig Group { get; private set; } = new LegendGroupConfig();

        public event EventHandler? Changed;
        public event EventHandler? DeleteRequested;
        /// <summary>(payload, droppedAtBlockIndex) — index = group.Blocks.Count to append.</summary>
        public event EventHandler<BlockDropArgs>? BlockDropRequested;
        public event EventHandler<MouseEventArgs>? GroupDragInitiated;
        public event Action<string, bool, bool>? BlockClickedOnCanvas; // blockId, ctrl, shift

        private Point _dragStart;
        private bool  _mouseDown;

        // ── Selection context ────────────────────────────────────────────────
        private HashSet<string> _selectionContext = new HashSet<string>();
        private string? _activeContext;

        // ── Block reorder drag state ─────────────────────────────────────────
        private StackPanel?            _blockStack;
        private LemoineLegendBlockRow? _dragBlockRow;
        private string?                _dragBlockId;
        private int                    _dragBlockOrigIdx;

        // ── Cross-group live-snap placeholder ────────────────────────────────
        private Border? _crossInsertPlaceholder;

        // ── Ghost drag (mirrors GlobalSettingsWindow pattern) ────────────────
        private Popup? _dragGhost;
        private Point  _dragGhostClickOffset;

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint pt);
        [DllImport("user32.dll")] private static extern int  GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        private const int GWL_EXSTYLE       = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }

        public LemoineLegendGroupCard()
        {
            InitializeComponent();
            Loaded += (s, e) => BuildAll();
        }

        public void Bind(LegendGroupConfig group)
        {
            Group = group ?? new LegendGroupConfig();
            if (IsLoaded) BuildAll();
        }

        public void SetSelectionContext(HashSet<string> selectedIds, string? activeId)
        {
            _selectionContext = selectedIds ?? new HashSet<string>();
            _activeContext = activeId;
        }

        /// <summary>
        /// Updates block row selection visuals in-place without rebuilding the body.
        /// Call instead of Bind() when only selection state changes.
        /// </summary>
        public void RefreshBlockSelection(HashSet<string> selectedIds, string? activeId)
        {
            _selectionContext = selectedIds ?? new HashSet<string>();
            _activeContext    = activeId;
            if (_blockStack == null) return;
            foreach (var row in _blockStack.Children.OfType<LemoineLegendBlockRow>())
            {
                bool isActive = row.Block.Id == activeId;
                bool isMulti  = !isActive && selectedIds.Contains(row.Block.Id);
                row.SetSelectionState(isActive, isMulti);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        private void BuildAll()
        {
            var tradeColor = ResolveTradeColor();

            _outer.Background = Brushes.Transparent;
            _outer.ClipToBounds = true;

            BuildHeader(tradeColor);
            BuildBody();
        }

        // ── Header ──────────────────────────────────────────────────────────
        private void BuildHeader(Color tradeColor)
        {
            // Header background: tinted with trade color (semi-translucent overlay) on top of LemoineRaised.
            var bg = new SolidColorBrush(tradeColor) { Opacity = 0.18 };
            bg.Freeze();
            _header.Background = bg;
            _header.BorderThickness = new Thickness(1, 1, 1, 1);
            _header.CornerRadius = new CornerRadius(6, 6, 0, 0);
            _header.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 0: collapse arrow
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 1: accent dot
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 2: title (auto — no wider than text)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 3: spacer (*)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 4: count
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 5: eye-all
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 6: shape-all
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 7: + custom
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 8: delete

            // Collapse chevron
            var chev = MakeIconButton(Group.Collapsed ? "▸" : "▾", Group.Collapsed ? "Expand" : "Collapse");
            chev.Click += (s, e) => { Group.Collapsed = !Group.Collapsed; Changed?.Invoke(this, EventArgs.Empty); BuildAll(); };
            Grid.SetColumn(chev, 0);
            grid.Children.Add(chev);

            // Color dot
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 10, Height = 10,
                Fill = new SolidColorBrush(tradeColor),
                Margin = new Thickness(2, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(dot, 1);
            grid.Children.Add(dot);

            // Title (inline edit, uppercase)
            var title = new LemoineInlineEdit
            {
                Text = Group.Title ?? "",
                Bold = true,
                Uppercase = true,
                FontSizeKey = "LemoineFS_MD",
                Placeholder = string.IsNullOrEmpty(Group.SourceTradeId) ? "CUSTOM" : "(group)",
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 160,
            };
            title.TextCommitted += (s, t) => { Group.Title = t ?? ""; Changed?.Invoke(this, EventArgs.Empty); };
            Grid.SetColumn(title, 2);
            grid.Children.Add(title);

            // col 3 is the star spacer — no child needed

            // Count
            var count = new TextBlock
            {
                Text = $"{Group.Blocks?.Count ?? 0}",
                Margin = new Thickness(8, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            count.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            count.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            count.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Grid.SetColumn(count, 4);
            grid.Children.Add(count);

            // Eye-all (toggle Visible on all blocks)
            bool anyHidden = Group.Blocks?.Any(b => !b.Visible) == true;
            bool allVisible = !anyHidden && (Group.Blocks?.Count ?? 0) > 0;
            var eyeAll = new Button
            {
                Content = LemoineEyeGlyph.Make(allVisible, size: 16),
                ToolTip = anyHidden ? "Show all" : "Hide all",
                Width = 24, Height = 22,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FocusVisualStyle = null,
            };
            eyeAll.Template = LemoineControlStyles.BuildFlatButtonTemplate();
            eyeAll.Click += (s, e) =>
            {
                bool target = anyHidden; // make them all Visible if any hidden, else hide all
                if (Group.Blocks != null) foreach (var b in Group.Blocks) b.Visible = target;
                Changed?.Invoke(this, EventArgs.Empty);
                BuildAll();
            };
            Grid.SetColumn(eyeAll, 5);
            grid.Children.Add(eyeAll);

            // Bulk shape picker
            var bulkBtn = MakeIconButton("▤", "Set shape/fill for all blocks");
            bulkBtn.Click += (s, e) => OpenBulkShapePopup(bulkBtn);
            Grid.SetColumn(bulkBtn, 6);
            grid.Children.Add(bulkBtn);

            // Add custom block inside this group
            var addCustom = MakeIconButton("+", "Add custom block");
            addCustom.Click += (s, e) =>
            {
                Group.Blocks ??= new List<LegendBlockConfig>();
                Group.Blocks.Add(new LegendBlockConfig
                {
                    Id = LegendIdGen.New("b"),
                    Name = "New",
                    Custom = true,
                    Color = "#888888",
                    Kind = "square", Fill = "solid",
                    Visible = true,
                });
                Changed?.Invoke(this, EventArgs.Empty);
                BuildAll();
            };
            Grid.SetColumn(addCustom, 7);
            grid.Children.Add(addCustom);

            // Delete
            var del = MakeIconButton("✕", "Delete group");
            del.Click += (s, e) => DeleteRequested?.Invoke(this, EventArgs.Empty);
            Grid.SetColumn(del, 8);
            grid.Children.Add(del);

            _header.Child = grid;

            // Drag source — header is the drag handle for the whole group
            _header.MouseLeftButtonDown += OnHeaderMouseDown;
            _header.MouseMove           += OnHeaderMouseMove;
            _header.MouseLeftButtonUp   += OnHeaderMouseUp;
            _header.MouseLeave          += (s, e) => _mouseDown = false;
            _header.Cursor = Cursors.SizeAll;
        }

        // ── Body ────────────────────────────────────────────────────────────
        private void BuildBody()
        {
            _body.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            _body.BorderThickness = new Thickness(1, 0, 1, 1);
            _body.CornerRadius = new CornerRadius(0, 0, 6, 6);
            _body.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            if (Group.Collapsed)
            {
                _body.Visibility = Visibility.Collapsed;
                return;
            }
            _body.Visibility = Visibility.Visible;

            if (_dragBlockId != null) return; // don't rebuild during an active block drag

            _blockStack = new StackPanel { AllowDrop = true, Background = Brushes.Transparent };

            int n = Group.Blocks?.Count ?? 0;
            if (n == 0)
            {
                // Empty body — big drop zone for palette drops
                _blockStack.Children.Add(MakeBlockIntoDrop());
            }
            else
            {
                // Live-snap reorder on the panel (handles gaps between rows)
                _blockStack.DragOver  += OnBlockStackDragOver;
                _blockStack.Drop      += OnBlockStackDrop;
                // Remove cross-group placeholder only when cursor truly leaves the panel.
                // DragLeave bubbles from children, so check bounds to avoid false fires.
                _blockStack.DragLeave += (s, e) =>
                {
                    if (_crossInsertPlaceholder == null) return;
                    var pos  = e.GetPosition(_blockStack);
                    var size = _blockStack.RenderSize;
                    if (pos.X < 0 || pos.Y < 0 || pos.X > size.Width || pos.Y > size.Height)
                        RemoveCrossInsertPlaceholder();
                };

                for (int i = 0; i < n; i++)
                {
                    var block = Group.Blocks![i];
                    var row   = new LemoineLegendBlockRow();
                    row.Tag       = block.Id; // read by CommitBlockReorder
                    row.AllowDrop = true;

                    row.SetSelectionContext(_selectionContext, _activeContext);
                    row.Bind(block);
                    row.BlockClicked += (bId, ctrl, shift) => BlockClickedOnCanvas?.Invoke(bId, ctrl, shift);

                    bool bIsActive = block.Id == _activeContext;
                    bool bIsMulti  = !bIsActive && _selectionContext.Contains(block.Id);
                    if (bIsActive || bIsMulti) row.SetSelectionState(bIsActive, bIsMulti);

                    row.Changed += (s, e) =>
                    {
                        Changed?.Invoke(this, EventArgs.Empty);
                        BuildHeader(ResolveTradeColor());
                    };

                    var blockId = block.Id; // stable capture independent of list order
                    row.DeleteRequested += (s, e) =>
                    {
                        Group.Blocks!.RemoveAll(b => b.Id == blockId);
                        Changed?.Invoke(this, EventArgs.Empty);
                        Dispatcher.BeginInvoke(new System.Action(BuildAll),
                            System.Windows.Threading.DispatcherPriority.Background);
                    };

                    // Reorder drag with ghost; also carries LegendDragPayload for cross-group drops
                    row.DragInitiated += (s, e) =>
                    {
                        if (_dragBlockRow != null) return; // already dragging
                        _dragBlockRow     = row;
                        _dragBlockId      = blockId;
                        _dragBlockOrigIdx = _blockStack?.Children.IndexOf(row) ?? -1;
                        if (_dragBlockOrigIdx < 0) { _dragBlockRow = null; _dragBlockId = null; return; }

                        row.Opacity = 0; // invisible — ghost represents it
                        ShowBlockDragGhost(block);

                        // Dual payload: StringFormat for same-group live-snap,
                        // LegendDragPayload for cross-group and session pre-lighting
                        var blockPayload = new LegendDragPayload
                        {
                            What    = LegendDragPayload.Kind.Block,
                            BlockId = blockId,
                            GroupId = Group.Id,
                        };
                        var data = new DataObject();
                        data.SetData(DataFormats.StringFormat, "BLOCK:" + blockId);
                        data.SetData(LemoineLegendPalette.DragFormat, blockPayload);

                        QueryContinueDragEventHandler ghostUpdater = (fs, fe) => UpdateDragGhostPos();
                        row.QueryContinueDrag += ghostUpdater;
                        try
                        {
                            LegendDragSession.Begin(blockPayload);
                            DragDrop.DoDragDrop(row, data, DragDropEffects.Move);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[BlockDrag] {ex.Message}");
                        }
                        finally { LegendDragSession.End(); }

                        row.QueryContinueDrag -= ghostUpdater;
                        HideDragGhost();

                        // Drop never fired (drag cancelled) — restore visual order
                        if (_dragBlockRow != null && _blockStack != null)
                        {
                            int cur = _blockStack.Children.IndexOf(row);
                            if (cur >= 0 && cur != _dragBlockOrigIdx)
                            {
                                _blockStack.Children.RemoveAt(cur);
                                _blockStack.Children.Insert(
                                    Math.Min(_dragBlockOrigIdx, _blockStack.Children.Count), row);
                            }
                            row.Opacity   = 1.0;
                            _dragBlockRow = null;
                        }
                        _dragBlockId = null;
                    };

                    // Live-snap when cursor is directly over this row
                    row.DragOver += OnBlockRowDragOver;
                    row.Drop     += OnBlockRowDrop;

                    _blockStack.Children.Add(row);
                }
            }

            _body.Child = _blockStack;
        }

        // ── Block live-snap helpers ──────────────────────────────────────────
        private void OnBlockStackDragOver(object sender, DragEventArgs e)
        {
            var d = e.Data.GetData(DataFormats.StringFormat) as string;
            if (d != null && d.StartsWith("BLOCK:"))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                var pos = e.GetPosition(_blockStack);
                if (_dragBlockRow != null) LiveSnapBlock(pos);
                else                       LiveSnapCrossGroup(pos);
                return;
            }
            // Allow palette drops to pass through to the Drop handler
            var pal = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
            if (pal != null && (pal.What == LegendDragPayload.Kind.PaletteFilter ||
                                pal.What == LegendDragPayload.Kind.PaletteCustom))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            }
        }

        private void OnBlockRowDragOver(object sender, DragEventArgs e)
        {
            var d = e.Data.GetData(DataFormats.StringFormat) as string;
            if (d == null || !d.StartsWith("BLOCK:")) return;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            if (_blockStack == null) return;
            var pos = e.GetPosition(_blockStack);
            if (_dragBlockRow != null) LiveSnapBlock(pos);
            else                       LiveSnapCrossGroup(pos);
        }

        private void LiveSnapBlock(Point cursorInStack)
        {
            if (_dragBlockRow == null || _blockStack == null) return;
            var children = _blockStack.Children;
            int srcIdx = children.IndexOf(_dragBlockRow);
            if (srcIdx < 0) return;
            double curY      = cursorInStack.Y;
            int    insertIdx = children.Count; // default = append after last
            double runY      = 0;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] == _crossInsertPlaceholder) continue; // ignore placeholder
                if (children[i] is FrameworkElement fe)
                {
                    if (curY < runY + fe.ActualHeight / 2.0) { insertIdx = i; break; }
                    runY += fe.ActualHeight + fe.Margin.Bottom;
                }
            }
            // No upper clamp — insertIdx == children.Count means append at end, which is valid.
            insertIdx = Math.Max(0, insertIdx);
            if (srcIdx < insertIdx) insertIdx--;
            if (insertIdx == srcIdx) return;
            children.RemoveAt(srcIdx);
            children.Insert(Math.Min(insertIdx, children.Count), _dragBlockRow);
        }

        private void LiveSnapCrossGroup(Point cursorInStack)
        {
            if (_blockStack == null) return;
            EnsureCrossInsertPlaceholder();
            var children = _blockStack.Children;
            double curY      = cursorInStack.Y;
            int    insertIdx = children.Count; // default = append after last
            double runY      = 0;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] == _crossInsertPlaceholder) continue;
                if (children[i] is FrameworkElement fe)
                {
                    if (curY < runY + fe.ActualHeight / 2.0) { insertIdx = i; break; }
                    runY += fe.ActualHeight + fe.Margin.Bottom;
                }
            }
            insertIdx = Math.Max(0, insertIdx);
            // Remove placeholder and re-insert at new position
            int curIdx = children.IndexOf(_crossInsertPlaceholder);
            if (curIdx >= 0)
            {
                if (curIdx == insertIdx) return; // already in right spot
                children.RemoveAt(curIdx);
                if (curIdx < insertIdx) insertIdx--;
            }
            children.Insert(Math.Min(insertIdx, children.Count), _crossInsertPlaceholder!);
        }

        private void EnsureCrossInsertPlaceholder()
        {
            if (_crossInsertPlaceholder != null) return;
            _crossInsertPlaceholder = new Border
            {
                Height           = 3,
                Margin           = new Thickness(4, 1, 4, 1),
                IsHitTestVisible = false, // cursor passes through to the StackPanel
            };
            _crossInsertPlaceholder.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
        }

        private void RemoveCrossInsertPlaceholder()
        {
            if (_crossInsertPlaceholder == null || _blockStack == null) return;
            _blockStack.Children.Remove(_crossInsertPlaceholder);
            _crossInsertPlaceholder = null;
        }

        private void OnBlockStackDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            var d = e.Data.GetData(DataFormats.StringFormat) as string;
            if (d != null && d.StartsWith("BLOCK:"))
            {
                // _dragBlockRow set → same-group reorder; null → cross-group move
                if (_dragBlockRow != null) CommitBlockReorder();
                else                       HandleCrossGroupBlockDrop(e);
                return;
            }
            // Palette drop on non-empty group → append at end
            var payload = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
            if (payload != null && (payload.What == LegendDragPayload.Kind.PaletteFilter ||
                                    payload.What == LegendDragPayload.Kind.PaletteCustom))
                BlockDropRequested?.Invoke(this, new BlockDropArgs(payload, Group.Id, Group.Blocks?.Count ?? 0));
        }

        private void OnBlockRowDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            var d = e.Data.GetData(DataFormats.StringFormat) as string;
            if (d != null && d.StartsWith("BLOCK:"))
            {
                if (_dragBlockRow != null) CommitBlockReorder();
                else                       HandleCrossGroupBlockDrop(e);
                return;
            }
            // Palette item dropped on a block row — append to group
            var payload = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
            if (payload != null && (payload.What == LegendDragPayload.Kind.PaletteFilter ||
                                    payload.What == LegendDragPayload.Kind.PaletteCustom))
                BlockDropRequested?.Invoke(this, new BlockDropArgs(payload, Group.Id, Group.Blocks?.Count ?? 0));
        }

        private void HandleCrossGroupBlockDrop(DragEventArgs e)
        {
            var payload = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
            if (payload?.What != LegendDragPayload.Kind.Block) return;
            // Capture placeholder position before removing it — counts only block rows before it.
            int dropIdx = Group.Blocks?.Count ?? 0; // default = append
            if (_crossInsertPlaceholder != null && _blockStack != null)
            {
                int placeholderPos = _blockStack.Children.IndexOf(_crossInsertPlaceholder);
                if (placeholderPos >= 0)
                {
                    // Count only LemoineLegendBlockRow children before the placeholder
                    dropIdx = _blockStack.Children
                        .Cast<UIElement>()
                        .Take(placeholderPos)
                        .OfType<LemoineLegendBlockRow>()
                        .Count();
                }
            }
            RemoveCrossInsertPlaceholder();
            BlockDropRequested?.Invoke(this, new BlockDropArgs(payload, Group.Id, dropIdx));
        }

        private void CommitBlockReorder()
        {
            if (_dragBlockRow != null) _dragBlockRow.Opacity = 1.0;
            _dragBlockRow = null;
            if (_blockStack == null) { _dragBlockId = null; return; }

            var newOrderIds = _blockStack.Children
                .OfType<FrameworkElement>()
                .Select(el => el.Tag as string)
                .Where(id => id != null)
                .ToList();
            var reordered = newOrderIds
                .Select(id => Group.Blocks?.FirstOrDefault(b => b.Id == id))
                .Where(b => b != null)
                .ToList();
            if (Group.Blocks != null && reordered.Count == Group.Blocks.Count)
            {
                Group.Blocks.Clear();
                foreach (var b in reordered) Group.Blocks.Add(b!);
            }
            _dragBlockId = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Ghost drag popup (mirrors GlobalSettingsWindow pattern)
        // ─────────────────────────────────────────────────────────────────────
        private void ShowBlockDragGhost(LegendBlockConfig block)
        {
            HideDragGhost();

            // Resolve resources from a live tree element so TryFindResource works.
            FrameworkElement res = _blockStack ?? (FrameworkElement)this;
            Brush BrushRes(string key, Brush fallback)
                => res.TryFindResource(key) as Brush ?? fallback;
            double DoubleRes(string key, double fallback)
                => res.TryFindResource(key) is double d ? d : fallback;
            FontFamily FontRes(string key)
                => res.TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI");

            var color = ResolveBlockColorForGhost(block);

            var inner = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // Colored swatch square
            var swatch = new Border
            {
                Width             = 14, Height = 10,
                CornerRadius      = new CornerRadius(2),
                Background        = new SolidColorBrush(color),
                Margin            = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            inner.Children.Add(swatch);

            var nameTb = new TextBlock
            {
                Text              = ResolveBlockNameForGhost(block),
                FontWeight        = FontWeights.SemiBold,
                Foreground        = BrushRes("LemoineText", Brushes.White),
                FontSize          = DoubleRes("LemoineFS_SM", 12),
                FontFamily        = FontRes("LemoineUiFont"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            inner.Children.Add(nameTb);

            var pill = new Border
            {
                CornerRadius    = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(8, 4, 8, 4),
                Background      = BrushRes("LemoineRaised", new SolidColorBrush(Color.FromRgb(50, 50, 50))),
                BorderBrush     = BrushRes("LemoineBorder", Brushes.Gray),
                Child           = inner,
            };

            _dragGhostClickOffset = new Point(16, 8);
            GetCursorPos(out var pt);
            var lpt = ToLogicalPoint(pt.X, pt.Y);

            _dragGhost = new Popup
            {
                AllowsTransparency = true,
                IsHitTestVisible   = false,
                Placement          = PlacementMode.AbsolutePoint,
                HorizontalOffset   = lpt.X - _dragGhostClickOffset.X,
                VerticalOffset     = lpt.Y - _dragGhostClickOffset.Y,
                Child              = pill,
            };
            _dragGhost.Opened += MakeGhostHwndTransparent;
            _dragGhost.IsOpen  = true;
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

        private void MakeGhostHwndTransparent(object sender, EventArgs e)
        {
            if (_dragGhost?.Child != null &&
                PresentationSource.FromVisual(_dragGhost.Child) is HwndSource hs)
            {
                int ex = GetWindowLong(hs.Handle, GWL_EXSTYLE);
                SetWindowLong(hs.Handle, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT);
            }
        }

        // ⚠ PresentationSource.FromVisual requires this control to be in the live visual tree.
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

        private string ResolveBlockNameForGhost(LegendBlockConfig block)
        {
            if (block.Custom) return block.Name ?? "Custom";
            if (block.NameOverride && !string.IsNullOrEmpty(block.Name)) return block.Name;
            var trade = AutoFiltersSettings.Instance.Trades?.FirstOrDefault(t => t.Id == block.SourceTradeId);
            return trade?.Rules?.FirstOrDefault(r => r.Id == block.SourceRuleId)?.Name ?? block.Name ?? "Block";
        }

        private Color ResolveBlockColorForGhost(LegendBlockConfig block)
        {
            Color fallback = LemoineTheme.FallbackGrey;
            if (block.ColorOverride) return BrushHelper.ColorFromHex(block.Color, fallback);
            var trade = AutoFiltersSettings.Instance.Trades?.FirstOrDefault(t => t.Id == block.SourceTradeId);
            var rule  = trade?.Rules?.FirstOrDefault(r => r.Id == block.SourceRuleId);
            if (rule != null) return BrushHelper.ColorFromHex(rule.SurfColor, fallback);
            return BrushHelper.ColorFromHex(block.Color, fallback);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Drop targets
        // ─────────────────────────────────────────────────────────────────────
        private Border MakeBlockIntoDrop()
        {
            var border = new Border
            {
                MinHeight = 36,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 2, 0, 2),
                Padding = new Thickness(8, 6, 8, 6),
                Background = Brushes.Transparent,
                AllowDrop = true,
            };

            var tb = new TextBlock
            {
                Text = "drop a filter / custom here",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontStyle = FontStyles.Italic,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            border.Child = tb;

            WireDropTarget(border, 0, slim: false);
            return border;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Block-drop visual states. Three levels:
        //   • idle     — invisible (slim) or BG-toned (empty body)
        //   • hint     — pre-lit insertion line / dashed empty body, applied
        //                whenever LegendDragSession has a block-compatible drag
        //   • active   — solid accent, applied while cursor is hovering
        // ─────────────────────────────────────────────────────────────────────
        private void WireDropTarget(Border target, int targetIndex, bool slim)
        {
            void ApplyIdle()
            {
                if (slim)
                {
                    target.Height = 6;
                    target.Background = Brushes.Transparent;
                    target.BorderThickness = new Thickness(0);
                    target.Margin = new Thickness(0);
                }
                else
                {
                    target.BorderBrush = null;
                    target.Background = Brushes.Transparent;
                    target.BorderThickness = new Thickness(0);
                    target.Margin = new Thickness(0, 2, 0, 2);
                }
            }
            void ApplyHint()
            {
                if (slim)
                {
                    target.Height = 6;
                    target.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
                    target.BorderThickness = new Thickness(0);
                    target.Margin = new Thickness(4, 1, 4, 1);
                }
                else
                {
                    target.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                    target.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                    target.BorderThickness = new Thickness(1.4);
                }
            }
            void ApplyActive()
            {
                if (slim)
                {
                    target.Height = 10;
                    target.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
                    target.BorderThickness = new Thickness(0);
                    target.Margin = new Thickness(2, 1, 2, 1);
                }
                else
                {
                    target.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                    target.SetResourceReference(Border.BackgroundProperty,  "LemoineAccent");
                    target.BorderThickness = new Thickness(1.6);
                }
            }

            // Session subscription — pre-light when a block-compatible drag starts.
            void OnStart(LegendDragPayload p)
            {
                if (p.What == LegendDragPayload.Kind.Block ||
                    p.What == LegendDragPayload.Kind.PaletteFilter ||
                    p.What == LegendDragPayload.Kind.PaletteCustom)
                    ApplyHint();
            }
            void OnEnd() => ApplyIdle();
            target.Loaded   += (s, e) =>
            {
                LegendDragSession.Started += OnStart;
                LegendDragSession.Ended   += OnEnd;
                // If a session is already in flight when the target mounts (race
                // with quick rebuild during drag), apply the hint immediately.
                if (LegendDragSession.Active && LegendDragSession.Current != null)
                    OnStart(LegendDragSession.Current);
            };
            target.Unloaded += (s, e) =>
            {
                LegendDragSession.Started -= OnStart;
                LegendDragSession.Ended   -= OnEnd;
            };

            target.DragEnter += (s, e) =>
            {
                if (IsBlockAccepting(e))
                {
                    ApplyActive();
                    e.Effects = DragDropEffects.Move;
                    e.Handled = true;
                }
                else e.Effects = DragDropEffects.None;
            };
            target.DragOver  += (s, e) =>
            {
                e.Effects = IsBlockAccepting(e) ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            target.DragLeave += (s, e) =>
            {
                // If session still active, snap back to the hint, not full idle
                if (LegendDragSession.Active && LegendDragSession.Current != null
                    && (LegendDragSession.Current.What == LegendDragPayload.Kind.Block
                     || LegendDragSession.Current.What == LegendDragPayload.Kind.PaletteFilter
                     || LegendDragSession.Current.What == LegendDragPayload.Kind.PaletteCustom))
                    ApplyHint();
                else
                    ApplyIdle();
            };
            target.Drop      += (s, e) =>
            {
                ApplyIdle();
                var payload = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
                if (payload == null) return;
                if (payload.What != LegendDragPayload.Kind.Block &&
                    payload.What != LegendDragPayload.Kind.PaletteFilter &&
                    payload.What != LegendDragPayload.Kind.PaletteCustom)
                    return;
                BlockDropRequested?.Invoke(this, new BlockDropArgs(payload, Group.Id, targetIndex));
                e.Handled = true;
            };
        }

        private static bool IsBlockAccepting(DragEventArgs e)
        {
            var p = e.Data.GetData(LemoineLegendPalette.DragFormat) as LegendDragPayload;
            if (p == null) return false;
            return p.What == LegendDragPayload.Kind.Block ||
                   p.What == LegendDragPayload.Kind.PaletteFilter ||
                   p.What == LegendDragPayload.Kind.PaletteCustom;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Header drag (group reorder)
        // ─────────────────────────────────────────────────────────────────────
        private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && IsInsideInteractive(d)) return;
            _mouseDown = true;
            _dragStart = e.GetPosition(this);
        }
        private void OnHeaderMouseMove(object sender, MouseEventArgs e)
        {
            if (!_mouseDown) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragStart.X) > 6 || Math.Abs(p.Y - _dragStart.Y) > 6)
            {
                _mouseDown = false;
                GroupDragInitiated?.Invoke(this, e);
            }
        }
        private void OnHeaderMouseUp(object sender, MouseButtonEventArgs e) { _mouseDown = false; }

        private static bool IsInsideInteractive(DependencyObject d)
        {
            while (d != null)
            {
                if (d is Button || d is TextBox || d is LemoineInlineEdit) return true;
                d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Bulk shape popup
        // ─────────────────────────────────────────────────────────────────────
        private void OpenBulkShapePopup(FrameworkElement anchor)
        {
            string first = Group.Blocks != null && Group.Blocks.Count > 0
                ? (Group.Blocks[0].Kind ?? "square") : "square";
            string firstFill = Group.Blocks != null && Group.Blocks.Count > 0
                ? (Group.Blocks[0].Fill ?? "solid") : "solid";
            var picker = new LemoineSwatchPicker
            {
                Kind = first, Fill = firstFill,
                SwatchColor = ResolveTradeColor(),
                Title = "GROUP",
            };
            var popup = new Popup
            {
                PlacementTarget    = anchor,
                Placement          = PlacementMode.Bottom,
                StaysOpen          = false,
                AllowsTransparency = false,
                Child              = picker,
            };
            picker.SelectionChanged += (s, args) =>
            {
                if (Group.Blocks != null)
                {
                    foreach (var b in Group.Blocks) { b.Kind = args.Kind; b.Fill = args.Fill; }
                }
                Changed?.Invoke(this, EventArgs.Empty);
                BuildAll();
                popup.IsOpen = false;
            };
            popup.IsOpen = true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Color resolution (trade color or neutral)
        // ─────────────────────────────────────────────────────────────────────
        private Color ResolveTradeColor()
        {
            if (string.IsNullOrEmpty(Group.SourceTradeId)) return LemoineTheme.FallbackGrey;
            var trade = AutoFiltersSettings.Instance.Trades?
                .FirstOrDefault(t => t.Id == Group.SourceTradeId);
            if (trade == null) return LemoineTheme.FallbackGrey;
            return BrushHelper.ColorFromHex(trade.Color, LemoineTheme.FallbackGrey);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Small widget helpers
        // ─────────────────────────────────────────────────────────────────────
        private static Button MakeIconButton(string glyph, string tooltip)
        {
            var b = new Button
            {
                Content = glyph,
                ToolTip = tooltip,
                Width = 22, Height = 22,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FocusVisualStyle = null,
            };
            b.SetResourceReference(Control.ForegroundProperty, "LemoineText");
            b.SetResourceReference(Control.FontFamilyProperty, "LemoineMonoFont");
            b.SetResourceReference(Control.FontSizeProperty,   "LemoineFS_MD");
            b.Template = LemoineControlStyles.BuildFlatButtonTemplate();
            return b;
        }
    }

    public sealed class BlockDropArgs : EventArgs
    {
        public LegendDragPayload Payload { get; }
        public string TargetGroupId      { get; }
        public int    TargetIndex        { get; }
        public BlockDropArgs(LegendDragPayload p, string gid, int i)
        { Payload = p; TargetGroupId = gid; TargetIndex = i; }
    }
}
