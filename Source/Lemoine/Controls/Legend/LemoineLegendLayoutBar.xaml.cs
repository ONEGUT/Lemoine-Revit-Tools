using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LemoineTools.Tools.Testing.LegendCreator;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Top ribbon of the Legend Creator. Single row:
    ///   [ Legend pill (title, accent-bordered) ] [ ✎ edit btn ] ── spacer ── [ Preview btn ] [ Templates ˅ pill ]
    /// </summary>
    public partial class LemoineLegendLayoutBar : UserControl
    {
        public event EventHandler? Changed;
        public event EventHandler? PreviewRequested;
        public event EventHandler? TemplatesRequested;

        private LegendLayoutConfig _layout = new LegendLayoutConfig();
        public LegendLayoutConfig Layout
        {
            get => _layout;
            set
            {
                _layout = value ?? new LegendLayoutConfig();
                if (IsLoaded) BuildAll();
            }
        }

        public LemoineLegendLayoutBar()
        {
            InitializeComponent();
            Loaded += (s, e) => BuildAll();
        }

        private void BuildAll()
        {
            _outer.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            _outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            _root.Children.Clear();

            // Single grid: [Auto legend pill] [Auto edit btn] [* spacer (preview centered here)] [Auto templates pill]
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 0 legend pill
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 1 edit btn
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 2 spacer (preview centered here)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 3 templates pill

            // ── Legend pill ──────────────────────────────────────────────────
            var pill = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            };
            pill.SetResourceReference(Border.CornerRadiusProperty,  "LemoineRadius_Chip");
            pill.SetResourceReference(Border.BorderBrushProperty,   "LemoineAccent");
            pill.SetResourceReference(Border.BackgroundProperty,    "LemoineAccentDim");

            var pillLabel = new TextBlock
            {
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            pillLabel.Text = _layout.Title ?? "Legend";
            pillLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
            pillLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            pillLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            pill.Child = pillLabel;
            Grid.SetColumn(pill, 0);
            grid.Children.Add(pill);

            // ── Edit button (✎) ──────────────────────────────────────────────
            var editBtn = new Border
            {
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            };
            editBtn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            editBtn.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");

            var editGlyph = new TextBlock { Text = "✎", VerticalAlignment = VerticalAlignment.Center };
            editGlyph.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            editGlyph.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            editGlyph.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            editBtn.Child = editGlyph;

            editBtn.MouseEnter += (s, e) =>
            {
                editBtn.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                editBtn.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
            };
            editBtn.MouseLeave += (s, e) =>
            {
                editBtn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                editBtn.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            };
            editBtn.MouseLeftButtonUp += (s, e) => ShowEditPopup(editBtn, pillLabel);

            Grid.SetColumn(editBtn, 1);
            grid.Children.Add(editBtn);

            // col 2 is the star spacer — no child needed

            // ── Preview button (centered in the star spacer) ─────────────────
            var previewBtn = LemoineControlStyles.BuildButton("Preview", LemoineControlStyles.LemoineButtonVariant.Ghost);
            previewBtn.VerticalAlignment   = VerticalAlignment.Center;
            previewBtn.HorizontalAlignment = HorizontalAlignment.Center;
            previewBtn.Margin              = new Thickness(0, 0, 4, 0);
            previewBtn.Click += (s, e) => PreviewRequested?.Invoke(this, EventArgs.Empty);
            Grid.SetColumn(previewBtn, 2);
            grid.Children.Add(previewBtn);

            // ── Templates pill ───────────────────────────────────────────────
            var templatesPill = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 10, 5),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            templatesPill.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
            templatesPill.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
            templatesPill.SetResourceReference(Border.BackgroundProperty,   "LemoineRaised");

            var templatesInner = new StackPanel { Orientation = Orientation.Horizontal };
            var templatesLabel = new TextBlock
            {
                Text = "Templates",
                VerticalAlignment = VerticalAlignment.Center,
            };
            templatesLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            templatesLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            templatesLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");

            var templatesChevron = new TextBlock
            {
                Text = " ˅",
                VerticalAlignment = VerticalAlignment.Center,
            };
            templatesChevron.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            templatesChevron.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            templatesChevron.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");

            templatesInner.Children.Add(templatesLabel);
            templatesInner.Children.Add(templatesChevron);
            templatesPill.Child = templatesInner;

            templatesPill.MouseEnter += (s, e) =>
            {
                templatesPill.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
            };
            templatesPill.MouseLeave += (s, e) =>
            {
                templatesPill.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            };
            templatesPill.MouseLeftButtonUp += (s, e) => TemplatesRequested?.Invoke(this, EventArgs.Empty);

            Grid.SetColumn(templatesPill, 3);
            grid.Children.Add(templatesPill);

            _root.Children.Add(grid);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Edit popup
        // ─────────────────────────────────────────────────────────────────────
        private void ShowEditPopup(UIElement anchor, TextBlock pillLabel)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(8),
                MinWidth = 200,
            };

            var titleLabel = new TextBlock { Text = "Title", Margin = new Thickness(0, 0, 0, 2) };
            titleLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            titleLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            titleLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            panel.Children.Add(titleLabel);

            var titleBox = new TextBox
            {
                Text = _layout.Title ?? "",
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(6, 4, 6, 4),
                BorderThickness = new Thickness(1),
            };
            titleBox.SetResourceReference(TextBox.ForegroundProperty,    "LemoineText");
            titleBox.SetResourceReference(TextBox.FontFamilyProperty,    "LemoineUiFont");
            titleBox.SetResourceReference(TextBox.FontSizeProperty,      "LemoineFS_MD");
            titleBox.SetResourceReference(TextBox.BorderBrushProperty,   "LemoineBorder");
            titleBox.SetResourceReference(TextBox.BackgroundProperty,    "LemoineSelectBg");
            titleBox.SetResourceReference(TextBox.CaretBrushProperty,    "LemoineText");
            panel.Children.Add(titleBox);

            var subLabel = new TextBlock { Text = "Subtitle", Margin = new Thickness(0, 0, 0, 2) };
            subLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            subLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            subLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            panel.Children.Add(subLabel);

            var subBox = new TextBox
            {
                Text = _layout.Subtitle ?? "",
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(6, 4, 6, 4),
                BorderThickness = new Thickness(1),
            };
            subBox.SetResourceReference(TextBox.ForegroundProperty,    "LemoineText");
            subBox.SetResourceReference(TextBox.FontFamilyProperty,    "LemoineUiFont");
            subBox.SetResourceReference(TextBox.FontSizeProperty,      "LemoineFS_MD");
            subBox.SetResourceReference(TextBox.BorderBrushProperty,   "LemoineBorder");
            subBox.SetResourceReference(TextBox.BackgroundProperty,    "LemoineSelectBg");
            subBox.SetResourceReference(TextBox.CaretBrushProperty,    "LemoineText");
            panel.Children.Add(subBox);

            var popup = new Popup
            {
                PlacementTarget    = anchor,
                Placement          = PlacementMode.Bottom,
                StaysOpen          = false,
                AllowsTransparency = false,
            };

            var saveBtn = LemoineControlStyles.BuildButton("Save", LemoineControlStyles.LemoineButtonVariant.Primary);
            saveBtn.Click += (s, e) =>
            {
                _layout.Title    = titleBox.Text.Trim();
                _layout.Subtitle = subBox.Text.Trim();
                pillLabel.Text   = _layout.Title;
                popup.IsOpen     = false;
                Changed?.Invoke(this, EventArgs.Empty);
            };
            panel.Children.Add(saveBtn);

            var outerBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = panel,
            };
            outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            outerBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");

            popup.Child = outerBorder;
            popup.IsOpen = true;
        }
    }
}
