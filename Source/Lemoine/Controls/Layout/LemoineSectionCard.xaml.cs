using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Reusable section card: an uppercase header + bordered container used wherever
    /// a labelled group of settings or content needs to be visually separated.
    ///
    /// Replaces the ad-hoc AddSection() imperative build in LemoineSettingsWindow and
    /// the bare TextBlock group headers in StepFlowWindow.BuildSettingsPanel().
    ///
    /// Usage:
    ///   var card = new LemoineSectionCard
    ///   {
    ///       Header      = "Appearance",
    ///       CardContent = mySettingsPanel,
    ///       Margin      = new Thickness(0, 0, 0, 16),
    ///   };
    /// </summary>
    public partial class LemoineSectionCard : UserControl
    {
        // ── Dependency properties ─────────────────────────────────────────────

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(
                nameof(Header), typeof(string), typeof(LemoineSectionCard),
                new PropertyMetadata(string.Empty, OnHeaderChanged));

        public static readonly DependencyProperty CardContentProperty =
            DependencyProperty.Register(
                nameof(CardContent), typeof(UIElement), typeof(LemoineSectionCard),
                new PropertyMetadata(null, OnCardContentChanged));

        public string    Header      { get => (string)GetValue(HeaderProperty);    set => SetValue(HeaderProperty, value); }
        public UIElement CardContent { get => (UIElement)GetValue(CardContentProperty); set => SetValue(CardContentProperty, value); }

        // ── Internal references ───────────────────────────────────────────────
        private TextBlock? _headerText;
        private Border?    _contentSlot;

        // ── Constructor ───────────────────────────────────────────────────────
        public LemoineSectionCard()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        // ── Build ─────────────────────────────────────────────────────────────
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Card chrome — matches LemoineSettingsWindow.AddSection() geometry
            _card.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_MD");
            _card.Padding      = new Thickness(12);
            _card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            _card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            // Section header — TextSub style, size driven by LemoineFS_SM resource key
            _headerText = new TextBlock
            {
                Text       = Header?.ToUpperInvariant() ?? string.Empty,
                FontWeight = FontWeights.Medium,
                Margin     = new Thickness(0, 4, 0, 10),
            };
            _headerText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _headerText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            _headerText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            // Content slot
            _contentSlot = new Border { Child = CardContent };

            _inner.Children.Clear();
            _inner.Children.Add(_headerText);
            _inner.Children.Add(_contentSlot);
        }

        // ── Property-changed callbacks ────────────────────────────────────────
        private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LemoineSectionCard card && card._headerText != null)
                card._headerText.Text = ((string)e.NewValue)?.ToUpperInvariant() ?? string.Empty;
        }

        private static void OnCardContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LemoineSectionCard card && card._contentSlot != null)
                card._contentSlot.Child = e.NewValue as UIElement;
        }
    }
}
