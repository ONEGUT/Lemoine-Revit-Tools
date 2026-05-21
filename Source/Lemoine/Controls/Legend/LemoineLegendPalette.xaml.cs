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
    /// Side-panel palette for the Legend Creator tab.
    /// Live mirror of <see cref="AutoFiltersSettings.Trades"/>.
    ///
    /// Three sections, top-to-bottom:
    ///   1. Search box (filters the rules below)
    ///   2. CATEGORIES — one chip per Trade. Three visible + "more ▾" overflow.
    ///      Click to filter / drag to spawn a populated group.
    ///   3. FILTERS — list of every (enabled) Rule under the selected scope.
    ///      Each row is draggable into a group.
    ///   4. CUSTOM — empty-swatch tile draggable to create a Custom block.
    /// </summary>
    public partial class LemoineLegendPalette : UserControl
    {
        // ── Public DnD payload contract ────────────────────────────────────
        public const string DragFormat = "LemoineTools.LegendDragPayload";

        // ── State ───────────────────────────────────────────────────────────
        private string _query = "";
        private string _scope = "All";   // "All" | Trade.Id

        // Built lazily on Loaded
        private WrapPanel?  _chipRow;
        private StackPanel? _filterList;
        private TextBox?    _searchBox;

        public LemoineLegendPalette()
        {
            InitializeComponent();
            Loaded   += (s, e) => { Build(); Refresh(); };
            Unloaded += (s, e) => { /* no global subscription to detach */ };
        }

        // External hook so a host can rebuild after AutoFiltersSettings.Save.
        public void Refresh()
        {
            BuildChipRow();
            BuildFilterList();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Build skeleton
        // ─────────────────────────────────────────────────────────────────────
        private void Build()
        {
            _root.RowDefinitions.Clear();
            _root.Children.Clear();
            _root.Margin = new Thickness(10, 10, 10, 10);

            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // search
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // chips label
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // chips
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // filters label
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // filters
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // custom label
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // custom tile

            AddRow(0, MakeMonoLabel("PALETTE"));
            AddRow(1, BuildSearchBox());
            AddRow(2, MakeMonoLabel("CATEGORIES"));

            _chipRow = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 4, 0, 6),
            };
            // Wrap the chip row in a horizontal-scroll-disabled ScrollViewer so the
            // WrapPanel always receives a finite available width and wraps properly.
            var chipScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
                Content = _chipRow,
            };
            AddRow(3, chipScroll);

            AddRow(4, MakeMonoLabel("FILTERS — drag into a group"));

            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 4, 0, 8),
            };
            _filterList = new StackPanel();
            sv.Content = _filterList;
            AddRow(5, sv);

            AddRow(6, MakeMonoLabel("CUSTOM"));
            AddRow(7, BuildCustomTile());
        }

        private void AddRow(int row, UIElement el)
        {
            Grid.SetRow(el, row);
            _root.Children.Add(el);
        }

        private UIElement BuildSearchBox()
        {
            var border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Padding         = new Thickness(8, 3, 8, 3),
                Margin          = new Thickness(0, 4, 0, 8),
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            border.SetResourceReference(Border.BackgroundProperty,  "LemoineSelectBg");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            var icon = new TextBlock
            {
                Text = "⌕",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            icon.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            _searchBox = new TextBox
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            _searchBox.SetResourceReference(TextBox.ForegroundProperty, "LemoineText");
            _searchBox.SetResourceReference(TextBox.FontFamilyProperty, "LemoineUiFont");
            _searchBox.SetResourceReference(TextBox.FontSizeProperty,   "LemoineFS_SM");
            _searchBox.SetResourceReference(TextBox.CaretBrushProperty, "LemoineText");
            _searchBox.TextChanged += (s, e) =>
            {
                _query = _searchBox.Text ?? "";
                BuildFilterList();
            };
            Grid.SetColumn(_searchBox, 1);
            grid.Children.Add(_searchBox);

            border.Child = grid;
            return border;
        }

        private UIElement BuildCustomTile()
        {
            var border = new Border
            {
                BorderThickness = new Thickness(1.4),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(8, 4, 8, 4),
                Margin          = new Thickness(0, 4, 0, 0),
                Cursor          = Cursors.Hand,
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            border.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var glyph = new LemoineSwatchGlyph
            {
                Kind = "square", Fill = "solid",
                SwatchColor = LemoineTheme.FallbackGrey,
                GlyphWidth = 22, GlyphHeight = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            var label = new TextBlock
            {
                Text = "Empty swatch + label",
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            label.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            label.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            row.Children.Add(glyph);
            row.Children.Add(label);
            border.Child = row;

            // Drag start
            border.PreviewMouseLeftButtonDown += (s, e) =>
            {
                var payload = new LegendDragPayload { What = LegendDragPayload.Kind.PaletteCustom };
                StartDrag(border, payload);
            };
            return border;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Chip row — every Trade renders; WrapPanel handles overflow
        // ─────────────────────────────────────────────────────────────────────
        private void BuildChipRow()
        {
            if (_chipRow == null) return;
            _chipRow.Children.Clear();

            var trades = AutoFiltersSettings.Instance.Trades ?? new List<FilterTradeConfig>();

            // "All" chip — non-draggable, click only
            _chipRow.Children.Add(MakeChip("All", isAll: true, trade: null, active: _scope == "All"));

            foreach (var trade in trades)
                _chipRow.Children.Add(MakeChip(trade.Label, isAll: false, trade: trade, active: _scope == trade.Id));
        }

        private LemoineCategoryChip MakeChip(string label, bool isAll, FilterTradeConfig? trade, bool active)
        {
            var chip = new LemoineCategoryChip
            {
                Label  = label,
                Active = active,
                AccentColor = isAll
                    ? GetThemeAccent()
                    : BrushHelper.ColorFromHex(trade!.Color, GetThemeAccent()),
                Draggable = !isAll,
                // Right + bottom margin so wrapped chips on row 2+ get vertical spacing.
                Margin = new Thickness(0, 0, 4, 4),
            };

            chip.Clicked += (s, e) =>
            {
                _scope = isAll ? "All" : trade!.Id;
                BuildChipRow();
                BuildFilterList();
            };

            if (!isAll)
            {
                chip.DragInitiated += (s, e) =>
                {
                    var payload = new LegendDragPayload
                    {
                        What          = LegendDragPayload.Kind.PaletteCategory,
                        SourceTradeId = trade!.Id,
                    };
                    StartDrag(chip, payload);
                };
            }
            return chip;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Filter list — flattened across Categories under each Trade
        // ─────────────────────────────────────────────────────────────────────
        private void BuildFilterList()
        {
            if (_filterList == null) return;
            _filterList.Children.Clear();

            var trades = AutoFiltersSettings.Instance.Trades ?? new List<FilterTradeConfig>();
            string q = (_query ?? "").Trim().ToLowerInvariant();

            // Flatten (Trade, Rule) with respect to scope filter.
            var rows = new List<(FilterTradeConfig trade, FilterRuleConfig rule)>();
            foreach (var t in trades)
            {
                if (_scope != "All" && t.Id != _scope) continue;
                if (t.Rules == null) continue;
                foreach (var rule in t.Rules.Where(r => r.Enabled).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(q) &&
                        !((rule.Name ?? "").ToLowerInvariant().Contains(q) ||
                          (t.Label   ?? "").ToLowerInvariant().Contains(q)))
                        continue;
                    rows.Add((t, rule));
                }
            }

            if (rows.Count == 0)
            {
                var tb = new TextBlock
                {
                    Text = "no matches",
                    FontStyle = FontStyles.Italic,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 8),
                };
                tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                _filterList.Children.Add(tb);
                return;
            }

            foreach (var (t, rule) in rows)
                _filterList.Children.Add(MakeFilterRow(t, rule));
        }

        private UIElement MakeFilterRow(FilterTradeConfig t, FilterRuleConfig rule)
        {
            var border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Padding         = new Thickness(6, 3, 6, 3),
                Margin          = new Thickness(0, 0, 0, 3),
                Cursor          = Cursors.Hand,
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            border.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var glyph = new LemoineSwatchGlyph
            {
                Kind = "square", Fill = "solid",
                SwatchColor = BrushHelper.ColorFromHex(rule.SurfColor, LemoineTheme.FallbackGrey),
                GlyphWidth = 22, GlyphHeight = 12,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(glyph);

            var stack = new StackPanel { Orientation = Orientation.Vertical, MinWidth = 0 };
            var name = new TextBlock
            {
                Text = rule.Name ?? "",
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            stack.Children.Add(name);
            var subtitle = new TextBlock
            {
                Text = t.Label ?? "",
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            subtitle.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            subtitle.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            subtitle.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            stack.Children.Add(subtitle);
            row.Children.Add(stack);

            border.Child = row;

            string capturedTradeId = t.Id;
            string capturedRuleId  = rule.Id;
            border.PreviewMouseLeftButtonDown += (s, e) =>
            {
                var payload = new LegendDragPayload
                {
                    What          = LegendDragPayload.Kind.PaletteFilter,
                    SourceTradeId = capturedTradeId,
                    SourceRuleId  = capturedRuleId,
                };
                StartDrag(border, payload);
            };
            return border;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────
        private TextBlock MakeMonoLabel(string text, bool asDim = false)
        {
            var tb = new TextBlock { Text = text };
            tb.SetResourceReference(TextBlock.ForegroundProperty, asDim ? "LemoineTextDim" : "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.Margin = new Thickness(0, 4, 0, 0);
            return tb;
        }

        private Color GetThemeAccent()
        {
            if (Resources["LemoineAccent"] is SolidColorBrush b) return b.Color;
            return LemoineTheme.DarkMono.Accent.Color;
        }

        private void StartDrag(DependencyObject source, LegendDragPayload payload)
        {
            try
            {
                LegendDragSession.Begin(payload);
                var data = new DataObject(DragFormat, payload);
                DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LegendPalette] drag failed: {ex.Message}");
            }
            finally
            {
                LegendDragSession.End();
            }
        }
    }

    // =========================================================================
    // Drag payload — shared by every legend drop target
    // =========================================================================
    public sealed class LegendDragPayload
    {
        public enum Kind { Block, PaletteFilter, PaletteCustom, Group, PaletteCategory }
        public Kind   What;
        public string SourceTradeId = "";
        public string SourceRuleId  = "";
        public string BlockId       = "";
        public string GroupId       = "";
    }

    // =========================================================================
    // Drag session — broadcasts Begin/End so passive drop targets (row split
    // zones, group block-insertion lines) can pre-light themselves without
    // having to wait for the cursor to actually enter them. Any code that
    // calls DragDrop.DoDragDrop should wrap the call in Begin / End.
    // =========================================================================
    public static class LegendDragSession
    {
        /// <summary>True while a legend drag is in flight.</summary>
        public static bool Active { get; private set; }

        /// <summary>The payload of the in-flight drag, or null if none.</summary>
        public static LegendDragPayload? Current { get; private set; }

        public static event Action<LegendDragPayload>? Started;
        public static event Action?                    Ended;

        public static void Begin(LegendDragPayload payload)
        {
            Active  = true;
            Current = payload;
            try { Started?.Invoke(payload); } catch { }
        }

        public static void End()
        {
            Active  = false;
            Current = null;
            try { Ended?.Invoke(); } catch { }
        }
    }

}
