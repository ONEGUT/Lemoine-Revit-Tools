using System.Windows;
using System.Windows.Controls;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// Entry point for the in-tool gear-icon overlay for Auto Filters.
    /// The full editor lives in GlobalSettingsWindow as an embedded Filters tab.
    /// This class opens that window and jumps to the correct tab.
    /// </summary>
    public static class AutoFiltersSettingsWindow
    {
        /// <summary>
        /// Returns a UIElement for StepFlowWindow's gear-icon overlay —
        /// a description + button that opens GlobalSettingsWindow at the Filters tab.
        /// </summary>
        public static UIElement BuildPanel()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            var desc = new TextBlock
            {
                Text = "Edit trades, categories, color rules and graphic overrides " +
                       "in the Filters / Color tab of the Settings window.",
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 0, 0, 10),
            };
            desc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            desc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            desc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            panel.Children.Add(desc);

            var btn = LemoineControlStyles.BuildButton("Open Filter Settings\u2026");
            btn.HorizontalAlignment = HorizontalAlignment.Left;
            btn.Click += (s, e) => OpenGlobalSettings(btn);
            panel.Children.Add(btn);
            return panel;
        }

        private static void OpenGlobalSettings(FrameworkElement anchor)
        {
            var owner = Window.GetWindow(anchor);
            if (App.GlobalSettings == null || !App.GlobalSettings.IsVisible)
            {
                App.GlobalSettings = new Lemoine.GlobalSettingsWindow();
                if (owner != null) App.GlobalSettings.Owner = owner;
                App.GlobalSettings.Closed += (s, e) => App.GlobalSettings = null;
                App.GlobalSettings.Show();
            }
            App.GlobalSettings.ActivateFiltersTab();
            App.GlobalSettings.Activate();
        }
    }
}
