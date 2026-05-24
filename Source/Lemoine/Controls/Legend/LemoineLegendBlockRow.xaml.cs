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
    /// Single legend entry tile. Composition:
    ///   [eye]  [color chip ▣]  [shape picker]  [name (inline edit)]  [CUST]  [missing⚠]  [✕]
    /// </summary>
    public partial class LemoineLegendBlockRow : UserControl
    {
        public LegendBlockConfig Block { get; private set; } = new LegendBlockConfig();

        public event EventHandler? Changed;        // any block field changed
        public event EventHandler? DeleteRequested;
        public event EventHandler<MouseEventArgs>? DragInitiated;
        public event Action<string, bool, bool>? BlockClicked; // blockId, ctrl, shift

        // ── State for click-vs-drag detection ──────────────────────────────
        private Point _dragStart;
        private bool  _mouseDown;

        public LemoineLegendBlockRow()
        {
            InitializeComponent();
            Loaded += (s, e) => BuildAll();
        }

        public void Bind(LegendBlockConfig block)
        {
            Block = block ?? new LegendBlockConfig();
            if (IsLoaded) BuildAll();
        }

        public void SetSelectionState(bool isActive, bool isMulti)
        {
            if (isActive)
            {
                _outer.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                _outer.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            }
            else if (isMulti)
            {
                _outer.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                _outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            }
            else
            {
                _outer.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                _outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        private void BuildAll()
        {
            _outer.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            _outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            _root.Children.Clear();
            _root.ColumnDefinitions.Clear();

            // Columns: eye · shape (also color) · name (*) · CUST · missing · delete
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 0 eye
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 1 shape (popup also picks color)
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 2 name
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 3 CUST tag
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 4 missing indicator
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 5 delete

            // ── 0: Eye toggle ──────────────────────────────────────────────
            var eyeBtn = MakeIconHostButton(
                child:   LemoineEyeGlyph.Make(Block.Visible, size: 16),
                tooltip: Block.Visible ? "Hide" : "Show");
            eyeBtn.BorderThickness = new Thickness(0); // no chip outline; eye is enough
            eyeBtn.Click += (s, e) =>
            {
                Block.Visible = !Block.Visible;
                Changed?.Invoke(this, EventArgs.Empty);
                BuildAll();
            };
            Grid.SetColumn(eyeBtn, 0);
            _root.Children.Add(eyeBtn);

            // ── 1: Shape + color picker (single popup) ─────────────────────
            var resolvedColor = ResolveColor();
            var shapePreview = new LemoineSwatchGlyph
            {
                Kind        = Block.Kind ?? "square",
                Fill        = Block.Fill ?? "solid",
                SwatchColor = resolvedColor,
                GlyphWidth  = 22, GlyphHeight = 14,
                Margin      = new Thickness(2, 0, 2, 0),
            };
            var shapeBtn = MakeIconHostButton(shapePreview, "Pick shape, fill, and color");
            shapeBtn.Click += (s, e) => OpenShapePopup(shapeBtn, resolvedColor);
            Grid.SetColumn(shapeBtn, 1);
            _root.Children.Add(shapeBtn);

            // ── 2: Name (inline edit) ──────────────────────────────────────
            var name = new LemoineInlineEdit
            {
                Text = ResolveName(),
                Placeholder = Block.Custom ? "Custom label" : "(rule name)",
                FontSizeKey = "LemoineFS_MD",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
            };
            name.TextCommitted += (s, t) =>
            {
                Block.Name = t ?? "";
                Block.NameOverride = !Block.Custom && t != (LookupRule()?.Name ?? "");
                Changed?.Invoke(this, EventArgs.Empty);
            };
            Grid.SetColumn(name, 2);
            _root.Children.Add(name);

            // ── 3: CUST tag ───────────────────────────────────────────────
            if (Block.Custom)
            {
                var cust = new Border
                {
                    Padding = new Thickness(4, 1, 4, 1),
                    CornerRadius = new CornerRadius(2),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(4, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                cust.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                cust.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                var custLbl = new TextBlock { Text = "CUST" };
                custLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                custLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                custLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                cust.Child = custLbl;
                Grid.SetColumn(cust, 3);
                _root.Children.Add(cust);
            }

            // ── 4: Missing-source indicator ────────────────────────────────
            if (!Block.Custom && string.IsNullOrEmpty(Block.SourceRuleId) == false)
            {
                if (LookupRule() == null)
                {
                    var miss = MakeGlyphLabel("⚠", "LemoineRed");
                    miss.ToolTip = "Source rule missing — value frozen at last seen.";
                    miss.Margin = new Thickness(4, 0, 4, 0);
                    miss.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(miss, 4);
                    _root.Children.Add(miss);
                }
            }

            // ── 5: Delete ──────────────────────────────────────────────────
            var del = MakeIconButton("✕", "Delete");
            del.SetResourceReference(Control.ForegroundProperty, "LemoineTextDim");
            del.Click += (s, e) => DeleteRequested?.Invoke(this, EventArgs.Empty);
            Grid.SetColumn(del, 5);
            _root.Children.Add(del);

            // ── Click-selection handler (fires before drag detection) ─────
            _outer.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is DependencyObject d && IsInsideInteractive(d)) return;
                bool ctrl  = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                BlockClicked?.Invoke(Block.Id, ctrl, shift);
                // Do NOT set e.Handled — drag detection still needs the event
            };

            // ── Drag-source wiring (whole row is grabbable) ───────────────
            _outer.MouseLeftButtonDown += OnRowMouseDown;
            _outer.MouseMove           += OnRowMouseMove;
            _outer.MouseLeftButtonUp   += OnRowMouseUp;
            _outer.MouseLeave          += (s, e) => _mouseDown = false;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Drag detection
        // ─────────────────────────────────────────────────────────────────────
        private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && IsInsideInteractive(d)) return;
            _mouseDown = true;
            _dragStart = e.GetPosition(this);
        }
        private void OnRowMouseMove(object sender, MouseEventArgs e)
        {
            if (!_mouseDown) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragStart.X) > 6 || Math.Abs(p.Y - _dragStart.Y) > 6)
            {
                _mouseDown = false;
                DragInitiated?.Invoke(this, e);
            }
        }
        private void OnRowMouseUp(object sender, MouseButtonEventArgs e)
        {
            _mouseDown = false;
        }

        /// <summary>Don't start a drag if the user clicks an interactive child (button, textbox, etc.).</summary>
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
        // Color picker invocation
        // ─────────────────────────────────────────────────────────────────────
        private void OpenColorPicker(Color initial)
        {
            var owner = Window.GetWindow(this);
            var picked = LemoineColorPickerWindow.PickColor(owner, initial);
            if (picked == null) return;
            var c = picked.Value;
            Block.Color = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            Block.ColorOverride = true;
            Changed?.Invoke(this, EventArgs.Empty);
            BuildAll();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Shape popup
        // ─────────────────────────────────────────────────────────────────────
        private void OpenShapePopup(FrameworkElement anchor, Color resolvedColor)
        {
            var picker = new LemoineSwatchPicker
            {
                Kind = Block.Kind ?? "square",
                Fill = Block.Fill ?? "solid",
                SwatchColor = resolvedColor,
                Title = "BLOCK",
                AllowColorPick = true,
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
                Block.Kind = args.Kind;
                Block.Fill = args.Fill;
                Changed?.Invoke(this, EventArgs.Empty);
                BuildAll();
                popup.IsOpen = false;
            };
            picker.ColorRequested += (s, args) =>
            {
                // Close the popup first so the modal color picker takes focus.
                popup.IsOpen = false;
                OpenColorPicker(resolvedColor);
            };
            popup.IsOpen = true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Live mirror lookups
        // ─────────────────────────────────────────────────────────────────────
        private FilterRuleConfig? LookupRule()
        {
            if (string.IsNullOrEmpty(Block.SourceRuleId) || string.IsNullOrEmpty(Block.SourceTradeId)) return null;
            var trades = AutoFiltersSettings.Instance.Trades;
            if (trades == null) return null;
            var trade = trades.FirstOrDefault(t => t.Id == Block.SourceTradeId);
            if (trade?.Rules == null) return null;
            return trade.Rules.FirstOrDefault(r => r.Id == Block.SourceRuleId);
        }

        private string ResolveName()
        {
            if (Block.Custom) return Block.Name ?? "";
            if (Block.NameOverride && !string.IsNullOrEmpty(Block.Name)) return Block.Name;
            return LookupRule()?.Name ?? Block.Name ?? "";
        }

        private Color ResolveColor()
        {
            Color fallback = LemoineTheme.FallbackGrey;
            if (Block.ColorOverride) return BrushHelper.ColorFromHex(Block.Color, fallback);
            var rule = LookupRule();
            if (rule != null) return BrushHelper.ColorFromHex(rule.SurfColor, fallback);
            return BrushHelper.ColorFromHex(Block.Color, fallback);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Tiny widget helpers
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

        private static Button MakeIconHostButton(UIElement child, string tooltip)
        {
            var b = new Button
            {
                Content = child,
                ToolTip = tooltip,
                Padding = new Thickness(3, 1, 3, 1),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                FocusVisualStyle = null,
            };
            b.SetResourceReference(Control.BorderBrushProperty, "LemoineBorder");
            b.Template = LemoineControlStyles.BuildFlatButtonTemplate();
            return b;
        }

        private static TextBlock MakeGlyphLabel(string glyph, string brushKey)
        {
            var tb = new TextBlock { Text = glyph };
            tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            return tb;
        }
    }
}
