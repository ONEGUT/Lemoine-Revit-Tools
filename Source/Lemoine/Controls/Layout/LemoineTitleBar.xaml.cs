using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Reusable title bar / window chrome strip used by LemoineSettingsWindow,
    /// GlobalSettingsWindow, and StepFlowWindow.
    ///
    /// Provides:
    ///   • Theme-aware background (LemoineSurface) + bottom border (LemoineBorder)
    ///   • Left side: optional Segoe MDL2 glyph + title text in LemoineMonoFont
    ///   • Right side: arbitrary UIElement slot (close button, step counter, etc.)
    ///   • Drag-to-move: clicking anywhere on the bar moves the owning Window
    ///
    /// Usage:
    ///   var bar = new LemoineTitleBar
    ///   {
    ///       Title        = "Appearance",
    ///       IconGlyph    = "",          // Segoe MDL2 paint-roller glyph
    ///       RightContent = myCloseButton,
    ///   };
    ///
    ///   // To change the title or icon after construction:
    ///   bar.Title     = "New Title";
    ///   bar.IconGlyph = "⚙";
    /// </summary>
    public partial class LemoineTitleBar : UserControl
    {
        // ── Dependency properties ─────────────────────────────────────────────

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title), typeof(string), typeof(LemoineTitleBar),
                new PropertyMetadata(string.Empty, OnTitleChanged));

        public static readonly DependencyProperty IconGlyphProperty =
            DependencyProperty.Register(
                nameof(IconGlyph), typeof(string), typeof(LemoineTitleBar),
                new PropertyMetadata(string.Empty, OnIconGlyphChanged));

        /// <summary>
        /// UIElement placed on the right side of the bar (close button, counter chip, etc.).
        /// Set before the control loads, or reassign at any time — the bar rebuilds the right slot.
        /// </summary>
        public static readonly DependencyProperty RightContentProperty =
            DependencyProperty.Register(
                nameof(RightContent), typeof(UIElement), typeof(LemoineTitleBar),
                new PropertyMetadata(null, OnRightContentChanged));

        /// <summary>
        /// When true (default), single-click drag moves the owning window and
        /// double-click toggles between Normal and Maximized.
        /// Set false for non-resizable windows (e.g. modeless settings flyouts).
        /// </summary>
        public static readonly DependencyProperty AllowsMaximizeProperty =
            DependencyProperty.Register(
                nameof(AllowsMaximize), typeof(bool), typeof(LemoineTitleBar),
                new PropertyMetadata(false));

        public string    Title          { get => (string)GetValue(TitleProperty);    set => SetValue(TitleProperty, value); }
        public string    IconGlyph      { get => (string)GetValue(IconGlyphProperty); set => SetValue(IconGlyphProperty, value); }
        public UIElement RightContent   { get => (UIElement)GetValue(RightContentProperty); set => SetValue(RightContentProperty, value); }
        public bool      AllowsMaximize { get => (bool)GetValue(AllowsMaximizeProperty); set => SetValue(AllowsMaximizeProperty, value); }

        // ── Internal references ───────────────────────────────────────────────
        private TextBlock? _iconText;
        private TextBlock? _titleText;
        private Border?    _rightSlot;

        // ── Constructor ───────────────────────────────────────────────────────
        public LemoineTitleBar()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        // ── Build ─────────────────────────────────────────────────────────────
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Chrome background + bottom border line
            _chrome.SetResourceReference(Border.BackgroundProperty,   "LemoineSurface");
            _chrome.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
            _dp.SetResourceReference(DockPanel.MarginProperty, "LemoineTh_ToolbarMar");

            // Drag handle — single-click moves the window; double-click toggles maximize
            // if AllowsMaximize is true (default false for modeless flyouts).
            _chrome.MouseLeftButtonDown += (s, ev) =>
            {
                var w = Window.GetWindow(this);
                if (!(w is Window)) return;
                if (AllowsMaximize && ev.ClickCount == 2)
                    w.WindowState = w.WindowState == WindowState.Maximized
                        ? WindowState.Normal : WindowState.Maximized;
                else
                    w.DragMove();
            };

            // ── Left: icon + title ────────────────────────────────────────────
            _iconText = new TextBlock
            {
                FontFamily        = new FontFamily("Segoe MDL2 Assets"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 8, 0),
                Text              = IconGlyph,
                Visibility        = string.IsNullOrEmpty(IconGlyph)
                                        ? Visibility.Collapsed
                                        : Visibility.Visible,
            };
            _iconText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
            _iconText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");

            _titleText = new TextBlock
            {
                Text              = Title,
                FontWeight        = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _titleText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XL");
            _titleText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            _titleText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            // ── Right: caller-supplied content (close button, step counter…) ─
            // Must be added to the DockPanel BEFORE the left panel so that
            // LastChildFill="True" expands the title (last child) rather than
            // pushing the right-side buttons off to the left.
            _rightSlot = new Border
            {
                VerticalAlignment = VerticalAlignment.Center,
                Child             = RightContent,
            };
            DockPanel.SetDock(_rightSlot, Dock.Right);
            _dp.Children.Add(_rightSlot);

            var left = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            left.Children.Add(_iconText);
            left.Children.Add(_titleText);
            DockPanel.SetDock(left, Dock.Left);
            _dp.Children.Add(left);
        }

        // ── Property-changed callbacks ────────────────────────────────────────
        private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LemoineTitleBar bar && bar._titleText != null)
                bar._titleText.Text = (string)e.NewValue;
        }

        private static void OnIconGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LemoineTitleBar bar && bar._iconText != null)
            {
                var glyph = (string)e.NewValue;
                bar._iconText.Text       = glyph;
                bar._iconText.Visibility = string.IsNullOrEmpty(glyph)
                                               ? Visibility.Collapsed
                                               : Visibility.Visible;
            }
        }

        private static void OnRightContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LemoineTitleBar bar && bar._rightSlot != null)
                bar._rightSlot.Child = e.NewValue as UIElement;
        }
    }
}
