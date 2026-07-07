using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.FiltersLegends.LegendCreator;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Live themed preview of the layout. Reads Layout + Rows in place and
    /// renders one TextBlock-per-block with a LemoineSwatchGlyph beside it.
    /// </summary>
    public partial class LemoineLegendPreview : UserControl
    {
        private LegendLayoutConfig    _layout = new LegendLayoutConfig();
        private List<LegendRowConfig> _rows   = new List<LegendRowConfig>();
        // Per-role text cap heights (paper inches) — from each role's real TextNoteType, so
        // the preview's text proportions match the generated legend instead of a single
        // font-point size. Falls back to the layout's FontPt when no type is captured.
        private LegendRoleCaps        _caps   = LegendRoleCaps.FromFontPt(9);

        // The legend stores swatch/gap sizes in paper inches. The preview renders at true
        // paper scale (1 in = 96 px) so spacing, sizing and proportions match the generated
        // legend. View scale only affects model-space placement, not paper appearance.
        private const double PxPerInch = 96.0;
        private static double InPx(double inches) => inches * PxPerInch;
        private static double CapPx(double capIn) => Math.Max(1.0, InPx(capIn));

        public LemoineLegendPreview()
        {
            InitializeComponent();
            Loaded += (s, e) => Redraw();
        }

        public void Update(LegendLayoutConfig layout, List<LegendRowConfig> rows)
        {
            _layout = layout ?? new LegendLayoutConfig();
            _rows   = rows   ?? new List<LegendRowConfig>();
            _caps   = LegendRoleCaps.FromFontPt(_layout.FontPt);
            if (IsLoaded) Redraw();
        }

        /// <summary>
        /// Update with explicit per-role text cap heights so the preview matches the real
        /// TextNoteType sizes the generated legend will use.
        /// </summary>
        public void Update(LegendLayoutConfig layout, List<LegendRowConfig> rows, LegendRoleCaps caps)
        {
            _layout = layout ?? new LegendLayoutConfig();
            _rows   = rows   ?? new List<LegendRowConfig>();
            _caps   = caps;
            if (IsLoaded) Redraw();
        }

        private void Redraw()
        {
            _outer.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            _outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            _root.Children.Clear();
            _root.RowDefinitions.Clear();
            _root.ColumnDefinitions.Clear();
            _root.ColumnDefinitions.Add(new ColumnDefinition());

            int r = 0;

            // Title + subtitle
            if (!string.IsNullOrEmpty(_layout.Title))
            {
                _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var t = new TextBlock
                {
                    Text = _layout.Title,
                    FontWeight = FontWeights.SemiBold,
                };
                t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                t.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                t.FontSize = CapPx(_caps.TitleIn);
                Grid.SetRow(t, r++);
                _root.Children.Add(t);
            }
            if (!string.IsNullOrEmpty(_layout.Subtitle))
            {
                _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var s = new TextBlock { Text = _layout.Subtitle, Margin = new Thickness(0, 0, 0, 6) };
                s.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
                s.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                s.FontSize = CapPx(_caps.SubtitleIn);
                Grid.SetRow(s, r++);
                _root.Children.Add(s);
            }

            // Rows
            foreach (var row in _rows)
            {
                _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 2, 0, InPx(_layout.RowGap)),
                };
                foreach (var grp in row.Groups ?? new List<LegendGroupConfig>())
                    rowPanel.Children.Add(RenderGroup(grp));
                Grid.SetRow(rowPanel, r++);
                _root.Children.Add(rowPanel);
            }
        }

        private UIElement RenderGroup(LegendGroupConfig grp)
        {
            var border = new Border
            {
                Margin = new Thickness(0, 0, InPx(_layout.ColGap), 0),
                Padding = new Thickness(2, 0, 2, 0),
            };
            var stack = new StackPanel { Orientation = Orientation.Vertical };

            // Group header — no underline (the generated legend draws none, so the preview
            // must not either, or it reads as content that the output won't produce).
            var titleTb = new TextBlock
            {
                Text = (grp.Title ?? "").ToUpperInvariant(),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, InPx(LegendLayout.HeaderPadIn)),
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            titleTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            titleTb.FontSize = CapPx(_caps.HeaderIn);
            stack.Children.Add(titleTb);

            // Blocks
            foreach (var b in grp.Blocks ?? new List<LegendBlockConfig>())
            {
                if (!b.Visible) continue;
                stack.Children.Add(RenderBlock(b));
            }

            border.Child = stack;
            return border;
        }

        private UIElement RenderBlock(LegendBlockConfig b)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 1, 0, 1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var glyph = new LemoineSwatchGlyph
            {
                Kind        = b.Kind ?? "square",
                Fill        = b.Fill ?? "solid",
                SwatchColor = ResolveBlockColor(b),
                GlyphWidth  = InPx(_layout.SwatchW),
                GlyphHeight = InPx(_layout.SwatchH),
                Margin      = new Thickness(0, 0, InPx(_layout.SwatchLabelGap), 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(glyph);
            var label = new TextBlock
            {
                Text = ResolveBlockName(b),
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            label.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            label.FontSize = CapPx(_caps.LabelIn);
            row.Children.Add(label);
            return row;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Lookups
        // ─────────────────────────────────────────────────────────────────────
        private static Color ResolveBlockColor(LegendBlockConfig b)
        {
            Color fallback = LemoineTheme.FallbackGrey;
            if (b.ColorOverride) return BrushHelper.ColorFromHex(b.Color, fallback);
            var rule = LookupRule(b);
            if (rule != null) return BrushHelper.ColorFromHex(rule.SurfColor, fallback);
            return BrushHelper.ColorFromHex(b.Color, fallback);
        }

        private static string ResolveBlockName(LegendBlockConfig b)
        {
            if (b.Custom) return b.Name ?? "";
            if (b.NameOverride && !string.IsNullOrEmpty(b.Name)) return b.Name;
            return LookupRule(b)?.Name ?? b.Name ?? "";
        }

        private static FilterRuleConfig? LookupRule(LegendBlockConfig b)
        {
            if (string.IsNullOrEmpty(b.SourceTradeId) || string.IsNullOrEmpty(b.SourceRuleId)) return null;
            var trades = AutoFiltersSettings.Instance.Trades;
            if (trades == null) return null;
            var trade = trades.FirstOrDefault(t => t.Id == b.SourceTradeId);
            if (trade?.Rules == null) return null;
            return trade.Rules.FirstOrDefault(rr => rr.Id == b.SourceRuleId);
        }
    }
}
