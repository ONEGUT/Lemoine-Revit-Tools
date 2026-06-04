using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LemoineTools.Lemoine;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Modal hosting <see cref="LemoineColorPickerPanel"/> + OK / Cancel buttons.
    /// Use <see cref="PickColor"/> for a one-line invocation.
    /// </summary>
    public partial class LemoineColorPickerWindow : Window
    {
        private readonly LemoineColorPickerPanel _panel;

        public Color? Result { get; private set; }

        public LemoineColorPickerWindow(Color initial)
        {
            InitializeComponent();
            _panel = new LemoineColorPickerPanel { SelectedColor = initial };
            _panelSlot.Content = _panel;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Inherit theme resources from owner so dynamic refs resolve.
            if (Owner != null)
                foreach (var key in Owner.Resources.Keys)
                    Resources[key] = Owner.Resources[key];

            ApplyChrome();
            BuildFooter();
        }

        private void ApplyChrome()
        {
            this.SetResourceReference(Window.BackgroundProperty, "LemoinePageBg");
            _root.SetResourceReference(Grid.BackgroundProperty, "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var closeX = LemoineControlStyles.BuildButton("✕", LemoineControlStyles.LemoineButtonVariant.Ghost);
            closeX.Click += (s, e) => { Result = null; DialogResult = false; Close(); };
            closeX.ToolTip = "Cancel";

            _toolbarBorder.BorderThickness = new Thickness(0);
            _toolbarBorder.Child = new LemoineTitleBar
            {
                Title        = "Pick Color",
                IconGlyph    = "◐",
                RightContent = closeX,
            };
        }

        private void BuildFooter()
        {
            _footerBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            _footerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var cancelBtn = LemoineControlStyles.BuildButton("Cancel", LemoineControlStyles.LemoineButtonVariant.Ghost);
            cancelBtn.Margin = new Thickness(0, 0, 6, 0);
            cancelBtn.Click += (s, e) => { Result = null; DialogResult = false; Close(); };

            var okBtn = LemoineControlStyles.BuildButton("Apply Color", LemoineControlStyles.LemoineButtonVariant.Primary);
            okBtn.Click += (s, e) =>
            {
                Result = _panel.SelectedColor;
                _panel.AddToRecent(_panel.SelectedColor);
                DialogResult = true;
                Close();
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            stack.Children.Add(cancelBtn);
            stack.Children.Add(okBtn);
            _footerBorder.Child = stack;
        }

        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Convenience: show modal, return chosen Color or null on Cancel.
        /// </summary>
        public static Color? PickColor(Window? owner, Color initial)
        {
            var w = new LemoineColorPickerWindow(initial)
            {
                Owner = owner,
            };
            return w.ShowDialog() == true ? w.Result : null;
        }

        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Reusable color-selection control. Returns a horizontal panel containing
        /// a clickable themed swatch (24×24) and an optional adjacent hex label.
        /// Clicking the swatch opens <see cref="PickColor"/>; on Apply the helper
        /// updates the swatch + label and invokes <paramref name="setHex"/> with
        /// an uppercase "#RRGGBB" string.
        ///
        /// Replaces the duplicate hex-TextBox + read-only swatch patterns that
        /// previously existed in StepFlowWindow, GlobalSettingsWindow.CeilingHeatmap,
        /// and the Filters Add-Trade popup.
        /// </summary>
        /// <param name="getHex">Returns the current "#RRGGBB" string. Called once
        /// at construction and again when the user reopens the picker (so the
        /// caller can store the source of truth wherever it likes).</param>
        /// <param name="setHex">Invoked when the user applies a new color, with
        /// the uppercase "#RRGGBB" hex string.</param>
        /// <param name="showHexLabel">When true (default), a monospace hex label
        /// is rendered next to the swatch and updated on each pick.</param>
        public static FrameworkElement BuildColorPickerSwatch(
            Func<string> getHex,
            Action<string> setHex,
            bool showHexLabel = true)
        {
            if (getHex == null) throw new ArgumentNullException(nameof(getHex));
            if (setHex == null) throw new ArgumentNullException(nameof(setHex));

            var panel = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var swatch = new Border
            {
                Width               = 24,
                Height              = 24,
                CornerRadius        = new CornerRadius(3),
                BorderThickness     = new Thickness(1),
                Cursor              = Cursors.Hand,
                Margin              = new Thickness(0, 0, 8, 0),
                VerticalAlignment   = VerticalAlignment.Center,
                Background          = new SolidColorBrush(ParseHex(getHex())),
                SnapsToDevicePixels = true,
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            LemoineMotion.WireSwatchHover(swatch, "LemoineBorder");

            TextBlock? hexLbl = null;
            if (showHexLabel)
            {
                hexLbl = new TextBlock
                {
                    Text              = getHex(),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                hexLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                hexLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                hexLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            }

            swatch.MouseLeftButtonUp += (s, e) =>
            {
                var win     = Window.GetWindow(swatch);
                var initial = ParseHex(getHex());
                var picked  = PickColor(win, initial);
                if (picked.HasValue)
                {
                    var c = picked.Value;
                    string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                    setHex(hex);
                    swatch.Background = new SolidColorBrush(c);
                    if (hexLbl != null) hexLbl.Text = hex;
                }
            };

            panel.Children.Add(swatch);
            if (hexLbl != null) panel.Children.Add(hexLbl);
            return panel;
        }

        private static Color ParseHex(string? hex)
        {
            if (!string.IsNullOrEmpty(hex) && hex!.Length == 7 && hex[0] == '#')
            {
                try
                {
                    byte r = Convert.ToByte(hex.Substring(1, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(3, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(5, 2), 16);
                    return Color.FromRgb(r, g, b);
                }
                catch (Exception __lex) { LemoineLog.Swallowed("ColorPicker window: parse colour hex", __lex); }
            }
            return Colors.Gray;
        }
    }
}
