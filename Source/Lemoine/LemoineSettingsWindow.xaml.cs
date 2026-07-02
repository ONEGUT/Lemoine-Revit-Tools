using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// Modeless settings window — follows the exact same visual language as StepFlowWindow.
    ///
    /// Contains:
    ///   • Appearance — theme selection (moved here from toolbar)
    ///   • (Extensible) — add new sections via AddSection() from your tool or App.cs
    ///
    /// Usage:
    ///   var settings = new LemoineSettingsWindow(currentTheme, onThemeChanged);
    ///   settings.Owner = this;
    ///   settings.Show();
    ///
    /// Adding a section from a tool (e.g. tool-specific defaults):
    ///   settings.AddSection("My Tool Defaults", BuildMySettingsPanel());
    /// </summary>
    public partial class LemoineSettingsWindow : Window
    {
        private LemoineTheme                _currentTheme;
        private readonly Action<LemoineTheme> _onThemeChanged;

        // Keep track of which swatch border is "active" so we can re-border on switch
        private Border[]? _swatches;

        public LemoineSettingsWindow(LemoineTheme currentTheme, Action<LemoineTheme> onThemeChanged)
        {
            InitializeComponent();

            _currentTheme   = currentTheme;
            _onThemeChanged = onThemeChanged;

            // Inherit theme resources from the parent window at construction time.
            // The owner must be set before Show() for this to work.
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Copy resource references from owner so DynamicResource works here too
            if (Owner != null)
                foreach (var key in Owner.Resources.Keys)
                    Resources[key] = Owner.Resources[key];

            ApplyChrome();
            BuildAppearanceSection();
            BuildFooter();
            // Distinguish this window from GlobalSettingsWindow for screen readers
            AutomationProperties.SetName(this, "Appearance Settings");
        }

        // ── Theme pass-through — called by StepFlowWindow when the theme changes ──
        public void SyncTheme(LemoineTheme theme)
        {
            _currentTheme = theme;
            theme.ApplyTo(Resources);
            Background = theme.PageBg;
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            RefreshSwatches();
        }

        // ═════════════════════════════════════════════════════════════════════
        // Chrome
        // ═════════════════════════════════════════════════════════════════════
        private void ApplyChrome()
        {
            this.SetResourceReference(Window.BackgroundProperty, "LemoinePageBg");
            _root.SetResourceReference(Grid.BackgroundProperty, "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            // LemoineTitleBar manages its own Surface/Border styling and drag-move.
            // Close × — ghost styling only; red is reserved for destructive actions.
            var closeX = LemoineControlStyles.BuildButton("x", LemoineControlStyles.LemoineButtonVariant.Ghost);
            closeX.Click += (s, e) => Close();
            closeX.ToolTip = LemoineStrings.T("appearanceSettings.close");
            _toolbarBorder.BorderThickness = new Thickness(0);
            _toolbarBorder.Child = new Controls.LemoineTitleBar
            {
                Title        = LemoineStrings.T("appearanceSettings.title"),
                IconGlyph    = "",   // Segoe MDL2 "Color" paint-roller
                RightContent = closeX,
            };
        }

        // _outerBorder is declared by x:Name in XAML — no code-behind property needed

        // ═════════════════════════════════════════════════════════════════════
        // Appearance section — theme selection
        // ═════════════════════════════════════════════════════════════════════
        private void BuildAppearanceSection()
        {
            _swatches = new Border[LemoineTheme.All.Length];

            var panel = new StackPanel();

            // Theme grid — one card per theme
            var themeGrid = new WrapPanel { Orientation = Orientation.Horizontal };

            for (int i = 0; i < LemoineTheme.All.Length; i++)
            {
                var theme     = LemoineTheme.All[i];
                var captured  = theme;
                var isActive  = theme == _currentTheme;

                var card = new Border
                {
                    Width           = 160,
                    Margin          = new Thickness(0, 0, 8, 8),
                    BorderThickness = new Thickness(2),
                    CornerRadius    = new CornerRadius(8),
                    Padding         = new Thickness(10, 10, 10, 10),
                    Cursor          = Cursors.Hand,
                    Background      = theme.Raised,
                    BorderBrush     = isActive ? theme.Accent : theme.Border,
                };

                // Mini preview strip
                var preview = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                preview.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = theme.Surface, Margin = new Thickness(0, 0, 0, 3) });
                preview.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = theme.Raised,  Margin = new Thickness(0, 0, 0, 3) });
                var accentStrip = new StackPanel { Orientation = Orientation.Horizontal };
                accentStrip.Children.Add(new Border { Width = 20, Height = 6, CornerRadius = new CornerRadius(3), Background = theme.Accent, Margin = new Thickness(0, 0, 3, 0) });
                accentStrip.Children.Add(new Border { Width = 12, Height = 6, CornerRadius = new CornerRadius(3), Background = theme.Green,  Margin = new Thickness(0, 0, 3, 0) });
                accentStrip.Children.Add(new Border { Width = 12, Height = 6, CornerRadius = new CornerRadius(3), Background = theme.Red });
                preview.Children.Add(accentStrip);

                // Theme name
                var nameText = new TextBlock
                {
                    Text       = theme.Name,
                    FontWeight = FontWeights.Medium,
                    Foreground = theme.Text,
                    FontFamily = theme.UiFont,
                };
                nameText.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");

                var activeTag = new Border
                {
                    Visibility      = isActive ? Visibility.Visible : Visibility.Collapsed,
                    Padding         = new Thickness(6, 2, 6, 2),
                    CornerRadius    = new CornerRadius(999),
                    Margin          = new Thickness(0, 4, 0, 0),
                    Background      = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new System.Windows.Thickness(1),
                    BorderBrush     = theme.Accent,
                };
                var activeText = new TextBlock { Text = LemoineStrings.T("appearanceSettings.active"), Foreground = theme.Accent, FontFamily = theme.UiFont };
                activeTag.Child = activeText;

                var inner = new StackPanel();
                inner.Children.Add(preview);
                inner.Children.Add(nameText);
                inner.Children.Add(activeTag);
                card.Child = inner;

                card.MouseLeftButtonDown += (s, e) => SelectTheme(captured);

                _swatches[i] = card;
                themeGrid.Children.Add(card);
            }

            panel.Children.Add(themeGrid);
            AddSection(LemoineStrings.T("appearanceSettings.appearanceSection"), panel);
        }

        private void SelectTheme(LemoineTheme theme)
        {
            _currentTheme = theme;
            _onThemeChanged?.Invoke(theme);
            RefreshSwatches();
        }

        private void RefreshSwatches()
        {
            for (int i = 0; i < LemoineTheme.All.Length; i++)
            {
                var t      = LemoineTheme.All[i];
                var card   = _swatches?[i];
                if (card == null) continue;
                bool active = t == _currentTheme;
                card.BorderBrush = active ? t.Accent : t.Border;

                // Refresh the active tag visibility and border colour
                if (card.Child is StackPanel sp && sp.Children.Count >= 3 && sp.Children[2] is Border tag)
                {
                    tag.Visibility   = active ? Visibility.Visible : Visibility.Collapsed;
                    tag.BorderBrush  = t.Accent;
                    if (tag.Child is TextBlock tagTb) tagTb.Foreground = t.Accent;
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Public API — add tool-specific settings sections
        // ═════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Add a named settings section below the built-in ones.
        /// Call from App.OnStartup or from a tool viewmodel after creating the window.
        ///
        /// Example:
        ///   settingsWindow.AddSection("Ceiling Grid Defaults", BuildMyPanel());
        /// </summary>
        public void AddSection(string title, UIElement content)
        {
            // LemoineSectionCard is the canonical reusable primitive for titled sections.
            // It bakes in the Rec-4-corrected header style (11pt TextSub, +4px top margin).
            var card = new Controls.LemoineSectionCard
            {
                Header      = title,
                CardContent = content,
                Margin      = new Thickness(0, 0, 0, 16),
            };
            _settingsStack.Children.Add(card);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Footer
        // ═════════════════════════════════════════════════════════════════════
        private void BuildFooter()
        {
            var closeBtn = LemoineControlStyles.BuildButton(LemoineStrings.T("appearanceSettings.close"), LemoineControlStyles.LemoineButtonVariant.Primary);
            closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
            closeBtn.Click += (s, e) => Close();

            _footerBorder.Child = closeBtn;
        }

        // CreateFlatButtonTemplate() removed — use LemoineControlStyles.BuildFlatButtonTemplate()
        // or LemoineControlStyles.BuildButton() instead.

        private static ControlTemplate CreateFlatButtonTemplate() =>
            LemoineControlStyles.BuildFlatButtonTemplate();
    }
}
