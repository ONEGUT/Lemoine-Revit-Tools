using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LemoineTools.Framework;

namespace LemoineTools.Preview
{
    /// <summary>
    /// Minimal Revit-free launcher for manually eyeballing real production windows
    /// and driving the design-twin parity capture without Revit installed.
    ///
    /// This replaces a much larger gallery (controls demo, icon browser, fictional
    /// "Room Tagger" demo tool) that dated from before the Source/Lemoine ->
    /// Source/Framework rename and no longer matched the current namespace/API —
    /// it did not build. See plan-design-twin-parity-and-webview2-overview.md for
    /// why this project was rebuilt minimal rather than repaired file-by-file.
    /// Restoring the fuller gallery is a separate task if still wanted.
    /// </summary>
    internal sealed class PreviewMainWindow : Window
    {
        public PreviewMainWindow()
        {
            Title = "Lemoine Preview";
            Width = 360;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            AppSettings.Instance.ApplyTo(Resources);
            AppSettings.Instance.ApplyScaleTo(Resources);
            ControlStyles.InjectInto(Resources, scrollBarWidth: 8);
            SetResourceReference(BackgroundProperty, "LemoineBg");

            var stack = new StackPanel { Margin = new Thickness(16) };

            var heading = new TextBlock { Text = "Lemoine Preview", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            heading.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_LG");
            heading.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            stack.Children.Add(heading);

            stack.Children.Add(BuildLaunchButton("Tools Overview", () => new ToolsOverviewWindow().Show()));

            var themeLabel = new TextBlock { Text = "Theme", Margin = new Thickness(0, 12, 0, 4) };
            themeLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            themeLabel.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            themeLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            stack.Children.Add(themeLabel);

            var themeRow = new WrapPanel();
            foreach (var theme in ThemePalette.All)
            {
                var swatch = new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(4),
                    Background = theme.Swatch, BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 6, 6), Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = theme.Name,
                };
                swatch.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                swatch.MouseLeftButtonDown += (s, e) => AppSettings.Instance.SetTheme(theme);
                themeRow.Children.Add(swatch);
            }
            stack.Children.Add(themeRow);

            var sizeLabel = new TextBlock { Text = "UI Size", Margin = new Thickness(0, 8, 0, 4) };
            sizeLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            sizeLabel.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            sizeLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            stack.Children.Add(sizeLabel);

            var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var size in new[] { UiSize.Small, UiSize.Medium, UiSize.Large })
            {
                var btn = ControlStyles.BuildSmallButton(size.ToString());
                btn.Margin = new Thickness(0, 0, 6, 0);
                btn.Click += (s, e) => AppSettings.Instance.SetUiSize(size);
                sizeRow.Children.Add(btn);
            }
            stack.Children.Add(sizeRow);

            Content = stack;

            // Named handlers, detached on Closed — see CLAUDE.md "Why leaked
            // global-event subscriptions crash Revit". Not Revit-hosted here, but
            // the pattern is kept consistent with every other Lemoine window.
            AppSettings.Instance.ThemeChanged += OnThemeChanged;
            Closed += (s, e) => AppSettings.Instance.ThemeChanged -= OnThemeChanged;
        }

        private void OnThemeChanged(ThemePalette t)
        {
            Dispatcher.BeginInvoke(new System.Action(() => AppSettings.Instance.ApplyTo(Resources)));
        }

        private static Button BuildLaunchButton(string label, System.Action onClick)
        {
            var btn = ControlStyles.BuildButton(label);
            btn.Margin = new Thickness(0, 0, 0, 6);
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            btn.Click += (s, e) => onClick();
            return btn;
        }
    }
}
