using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.Testing.LegendCreator;

namespace LemoineTools.Lemoine
{
    public partial class LegendSettingsWindow : Window
    {
        private TextBlock? _statusText;

        public LegendSettingsWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;

            LemoineSettings.Instance.ThemeChanged += t => Dispatcher.Invoke(() =>
            {
                LemoineSettings.Instance.ApplyTo(Resources);
                Background = t.PageBg;
                _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            });

            LemoineSettings.Instance.UiSizeChanged += _ => Dispatcher.Invoke(() =>
            {
                LemoineSettings.Instance.ApplyScaleTo(Resources);
                LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 8);
                UpdateRowHeights();
            });
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LemoineSettings.Instance.ApplyTo(Resources);
            LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 8);
            Background = LemoineSettings.Instance.ActiveTheme.PageBg;
            _root.SetResourceReference(System.Windows.Controls.Grid.BackgroundProperty, "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _outerBorder.CornerRadius = new CornerRadius(8);

            UpdateRowHeights();
            BuildToolbar();
            BuildFooter();
            _contentBorder.Child = LegendCreatorTabContent.BuildContent(this);
        }

        protected override void OnClosed(EventArgs e)
        {
            LegendCreatorTabContent.DiscardEdits();
            base.OnClosed(e);
        }

        private void UpdateRowHeights()
        {
            if (_root == null) return;
            _root.RowDefinitions[0].Height = new GridLength(LemoineSettings.Instance.ToolbarHeight);
            _root.RowDefinitions[2].Height = new GridLength(LemoineSettings.Instance.FooterHeight);
        }

        private void BuildToolbar()
        {
            _toolbarBorder.BorderThickness = new Thickness(0);

            var closeBtn = LemoineControlStyles.BuildButton(
                "×", LemoineControlStyles.LemoineButtonVariant.Ghost);
            closeBtn.Margin = new Thickness(8, 0, 0, 0);
            closeBtn.SetResourceReference(Button.ForegroundProperty, "LemoineTextDim");
            closeBtn.Click += (s, e) => Close();

            var rightPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rightPanel.Children.Add(closeBtn);

            _toolbarBorder.Child = new LemoineTitleBar
            {
                Title        = "Legend Creator Settings",
                IconGlyph    = "⚙",
                RightContent = rightPanel,
            };
        }

        private void BuildFooter()
        {
            _footerBorder.SetResourceReference(Border.PaddingProperty, "LemoineTh_FooterPad");
            _footerBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            _footerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            _statusText = new TextBlock
            {
                Text = "", VerticalAlignment = VerticalAlignment.Center,
                FontStyle = FontStyles.Italic,
            };
            _statusText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _statusText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineGreen");
            _statusText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var applyBtn = LemoineControlStyles.BuildButton(
                "Apply", LemoineControlStyles.LemoineButtonVariant.Ghost);
            applyBtn.Margin = new Thickness(0, 0, 6, 0);
            applyBtn.Click += (s, e) => ApplyCurrent();

            var closeBtn = LemoineControlStyles.BuildButton(
                "Close", LemoineControlStyles.LemoineButtonVariant.Ghost);
            closeBtn.Click += (s, e) => Close();

            var btnStack = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            btnStack.Children.Add(applyBtn);
            btnStack.Children.Add(closeBtn);

            var dp = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(btnStack, Dock.Right);
            dp.Children.Add(btnStack);
            dp.Children.Add(_statusText);

            _footerBorder.Child = dp;
        }

        private void ApplyCurrent()
        {
            LegendCreatorTabContent.Apply();
            FlashStatus("Saved.");
        }

        private void FlashStatus(string msg)
        {
            if (_statusText == null) return;
            _statusText.Text = msg;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, e) => { _statusText.Text = ""; timer.Stop(); };
            timer.Start();
        }
    }
}
