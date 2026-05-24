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
    ///   1. Scope row — "All" pill + trade dropdown pill
    ///   2. FILTERS — list of every (enabled) Rule under the selected scope.
    ///      Each row is draggable into a group.
    ///   3. CUSTOM — empty-swatch tile draggable to create a Custom block.
    /// </summary>
    public partial class LemoineLegendPalette : UserControl
    {
        // ── Public DnD payload contract ────────────────────────────────────
        public const string DragFormat = "LemoineTools.LegendDragPayload";

        // ── State ───────────────────────────────────────────────────────────
        private string _scope = "All";   // "All" | Trade.Id

        // Built lazily on Loaded
        private StackPanel? _scopeRow;
        private StackPanel? _filterList;

        public LemoineLegendPalette()
        {
            InitializeComponent();
            Loaded   += (s, e) => { Build(); Refresh(); };
            Unloaded += (s, e) => { /* no global subscription to detach */ };
        }

        // External hook so a host can rebuild after AutoFiltersSettings.Save.
        public void Refresh()
        {
            BuildScopeRow();
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

            // header=0, scope row=1, filters label=2, filters=3, custom label=4, custom tile=5
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // scope row
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // filters label
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // filters
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // custom label
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // custom tile

            AddRow(0, MakeMonoLabel("PALETTE"));

            _scopeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 6),
            };
            AddRow(1, _scopeRow);

            AddRow(2, MakeMonoLabel("FILTERS — drag into a group"));

            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 4, 0, 8),
            };
            _filterList = new StackPanel();
            sv.Content = _filterList;
            AddRow(3, sv);

            AddRow(4, MakeMonoLabel("CUSTOM"));
            AddRow(5, BuildCustomTile());
        }

        private void AddRow(int row, UIElement el)
        {
            Grid.SetRow(el, row);
            _root.Children.Add(el);
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
        // Scope row — "All" pill + trade dropdown pill
        // ─────────────────────────────────────────────────────────────────────
        private void BuildScopeRow()
        {
            if (_scopeRow == null) return;
            _scopeRow.Children.Clear();

            var trades = AutoFiltersSettings.Instance.Trades ?? new List<FilterTradeConfig>();

            // "All" pill
            var allPill = MakeScopePill("All", _scope == "All");
            allPill.MouseLeftButtonUp += (s, e) =>
            {
                _scope = "All";
                BuildScopeRow();
                BuildFilterList();
            };
            _scopeRow.Children.Add(allPill);

            // Trade dropdown pill
            string tradeLabel = "All trades";
            bool tradeActive = _scope != "All";
            if (tradeActive)
            {
                var found = trades.FirstOrDefault(t => t.Id == _scope);
                if (found != null) tradeLabel = found.Label ?? found.Id;
            }

            var tradePill = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 3, 6, 3),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
            };
            tradePill.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
            if (tradeActive)
            {
                tradePill.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                tradePill.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
            }
            else
            {
                tradePill.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                tradePill.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            }

            var tradePillInner = new StackPanel { Orientation = Orientation.Horizontal };
            var tradePillLabel = new TextBlock
            {
                Text = tradeLabel,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (tradeActive)
                tradePillLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            else
                tradePillLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tradePillLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tradePillLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            var tradePillChevron = new TextBlock
            {
                Text = " ˅",
                VerticalAlignment = VerticalAlignment.Center,
            };
            tradePillChevron.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tradePillChevron.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            tradePillChevron.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            tradePillInner.Children.Add(tradePillLabel);
            tradePillInner.Children.Add(tradePillChevron);
            tradePill.Child = tradePillInner;

            tradePill.MouseLeftButtonUp += (s, e) =>
            {
                if (trades.Count == 0) return;
                OpenTradeDropdown(tradePill, trades);
            };

            _scopeRow.Children.Add(tradePill);
        }

        private Border MakeScopePill(string label, bool active)
        {
            var pill = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 3, 6, 3),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            pill.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
            if (active)
            {
                pill.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                pill.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
            }
            else
            {
                pill.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                pill.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            }

            var tb = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            pill.Child = tb;
            return pill;
        }

        private void OpenTradeDropdown(UIElement anchor, List<FilterTradeConfig> trades)
        {
            var listPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(4),
            };

            var popup = new Popup
            {
                PlacementTarget    = anchor,
                Placement          = PlacementMode.Bottom,
                StaysOpen          = false,
                AllowsTransparency = false,
            };

            foreach (var trade in trades)
            {
                string capturedId = trade.Id;
                string capturedLabel = trade.Label ?? trade.Id;

                var item = new Border
                {
                    Padding = new Thickness(8, 4, 8, 4),
                    CornerRadius = new CornerRadius(3),
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 0, 0, 2),
                };
                item.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");

                var itemLabel = new TextBlock { Text = capturedLabel };
                itemLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                itemLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                itemLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                item.Child = itemLabel;

                item.MouseEnter += (s, e) =>
                    item.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
                item.MouseLeave += (s, e) =>
                    item.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");

                item.MouseLeftButtonUp += (s, e) =>
                {
                    _scope = capturedId;
                    popup.IsOpen = false;
                    BuildScopeRow();
                    BuildFilterList();
                };

                listPanel.Children.Add(item);
            }

            var outerBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(2),
                Child = listPanel,
            };
            outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            outerBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");

            popup.Child = outerBorder;
            popup.IsOpen = true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Filter list — flattened across Categories under each Trade
        // ─────────────────────────────────────────────────────────────────────
        private void BuildFilterList()
        {
            if (_filterList == null) return;
            _filterList.Children.Clear();

            var trades = AutoFiltersSettings.Instance.Trades ?? new List<FilterTradeConfig>();

            // Flatten (Trade, Rule) with respect to scope filter.
            var rows = new List<(FilterTradeConfig trade, FilterRuleConfig rule)>();
            foreach (var t in trades)
            {
                if (_scope != "All" && t.Id != _scope) continue;
                if (t.Rules == null) continue;
                foreach (var rule in t.Rules.Where(r => r.Enabled).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                {
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
