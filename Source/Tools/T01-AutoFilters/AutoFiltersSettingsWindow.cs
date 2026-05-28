using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// Entry point for the in-tool gear-icon overlay for Auto Filters.
    /// Opens the standalone FiltersSettingsWindow on a dedicated STA thread.
    /// </summary>
    public static class AutoFiltersSettingsWindow
    {
        private static FiltersSettingsWindow? _window;

        /// <summary>
        /// Returns a UIElement for StepFlowWindow's gear-icon overlay —
        /// a description + button that opens the FiltersSettingsWindow.
        /// </summary>
        public static UIElement BuildPanel()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            var desc = new TextBlock
            {
                Text = "Edit trades, categories, color rules and graphic overrides " +
                       "in the Filters / Color Settings window.",
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 0, 0, 10),
            };
            desc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            desc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            desc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            panel.Children.Add(desc);

            var btn = LemoineControlStyles.BuildButton("Open Filter Settings…");
            btn.HorizontalAlignment = HorizontalAlignment.Left;
            btn.Click += (s, e) => OpenFiltersSettings();
            panel.Children.Add(btn);
            return panel;
        }

        private static void OpenFiltersSettings()
        {
            // Bring existing window to front if open
            if (_window != null)
            {
                try
                {
                    _window.Dispatcher.Invoke(() =>
                    {
                        if (_window.IsVisible) _window.Activate();
                        else _window = null;
                    });
                    if (_window != null) return;
                }
                catch { _window = null; }
            }

            // Open on a dedicated STA thread (callers are on their own STA dispatcher)
            var ready = new ManualResetEventSlim(false);
            FiltersSettingsWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new FiltersSettingsWindow();
                win.Closed += (s, e) =>
                {
                    _window = null;
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                };
                win.Show();
                ready.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            ready.Wait();
            _window = win;
        }
    }
}
