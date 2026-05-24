using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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

            var stack = new StackPanel();

            int n = Group.Blocks?.Count ?? 0;
            if (n == 0)
            {
                // Empty body — single big drop zone
                stack.Children.Add(MakeBlockIntoDrop());
            }
            else
            {
                // Drop zone at top (before first block)
                stack.Children.Add(MakeBlockDrop(0));

                for (int i = 0; i < n; i++)
                {
                    var row = new LemoineLegendBlockRow();
                    int capturedI = i;
                    row.SetSelectionContext(
                        _selectionContext,
                        _activeContext);
                    row.Bind(Group.Blocks![i]);
                    row.BlockClicked += (bId, ctrl, shift) => BlockClickedOnCanvas?.Invoke(bId, ctrl, shift);
                    bool bIsActive = Group.Blocks![capturedI].Id == _activeContext;
                    bool bIsMulti  = !bIsActive && _selectionContext.Contains(Group.Blocks![capturedI].Id);
                    if (bIsActive || bIsMulti) row.SetSelectionState(bIsActive, bIsMulti);
                    row.Changed         += (s, e) => { Changed?.Invoke(this, EventArgs.Empty); /* count refresh */ BuildHeader(ResolveTradeColor()); };
                    row.DeleteRequested += (s, e) =>
                    {
                        Group.Blocks!.RemoveAt(capturedI);
                        Changed?.Invoke(this, EventArgs.Empty);
                        Dispatcher.BeginInvoke(new System.Action(BuildAll),
                            System.Windows.Threading.DispatcherPriority.Background);
                    };
                    row.DragInitiated += (s, e) =>
                    {
                        var payload = new LegendDragPayload
                        {
                            What    = LegendDragPayload.Kind.Block,
                            BlockId = Group.Blocks![capturedI].Id,
                            GroupId = Group.Id,
                        };
                        try
                        {
                            LegendDragSession.Begin(payload);
                            DragDrop.DoDragDrop(row, new DataObject(LemoineLegendPalette.DragFormat, payload),
                                DragDropEffects.Move | DragDropEffects.Copy);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[GroupCard] block drag fail: {ex.Message}");
                        }
                        finally { LegendDragSession.End(); }
                    };
                    stack.Children.Add(row);
                    // Between / after this block
                    stack.Children.Add(MakeBlockDrop(i + 1));
                }
            }

            _body.Child = stack;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Drop targets
        // ─────────────────────────────────────────────────────────────────────
        private Border MakeBlockDrop(int targetIndex)
        {
            var border = new Border
            {
                Height = 6,
                Margin = new Thickness(0, 0, 0, 0),
                CornerRadius = new CornerRadius(2),
                Background = Brushes.Transparent,
                AllowDrop = true,
                SnapsToDevicePixels = true,
            };
            WireDropTarget(border, targetIndex, slim: true);
            return border;
        }

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
