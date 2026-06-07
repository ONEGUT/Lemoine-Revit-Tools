using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Automation;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.Ceilings;
using LemoineTools.Tools.Testing.LegendCreator;

namespace LemoineTools.Lemoine
{
    public partial class GlobalSettingsWindow : Window
    {
        // ── Theme rows (General tab) ──────────────────────────────────────────
        private readonly List<(Border row, LemoineTheme theme)>  _themeRows = new List<(Border, LemoineTheme)>();
        private readonly List<(Border row, LemoineUiSize size)>  _sizeRows  = new List<(Border, LemoineUiSize)>();

        // ── Nav pill state ────────────────────────────────────────────────────
        private string _activeTabId = "general";
        private readonly Dictionary<string, Border> _navTabs = new Dictionary<string, Border>();

        // ── Footer status text ───────────────────────────────────────────────
        private TextBlock? _fStatusText;
        private TextBlock? _fBuildInfoText;

        // ─────────────────────────────────────────────────────────────────────
        public GlobalSettingsWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;

            LemoineSettings.Instance.ThemeChanged += t => Dispatcher.Invoke(() =>
            {
                LemoineSettings.Instance.ApplyTo(Resources);
                Background = t.PageBg;
                _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                RefreshThemeRows();
            });

            LemoineSettings.Instance.UiSizeChanged += _ => Dispatcher.Invoke(() =>
            {
                LemoineSettings.Instance.ApplyScaleTo(Resources);
                LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 8);
                UpdateRowHeights();
                RefreshSizeRows();
            });
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LemoineSettings.Instance.ApplyTo(Resources);
            LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 8);
            Background = LemoineSettings.Instance.ActiveTheme.PageBg;
            _root.SetResourceReference(Grid.BackgroundProperty, "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _outerBorder.CornerRadius = new CornerRadius(8); // matches Windows 11 DWM rounding

            UpdateRowHeights();
            BuildToolbar();
            BuildTabNav();
            BuildFooter();
            SwitchTab(_activeTabId);
            // Distinguish from LemoineSettingsWindow ("Appearance Settings") for screen readers
            AutomationProperties.SetName(this, "Manage Settings");
        }

        private void UpdateRowHeights()
        {
            if (_root == null) return;
            _root.RowDefinitions[0].Height = new GridLength(LemoineSettings.Instance.ToolbarHeight);
            _root.RowDefinitions[3].Height = new GridLength(LemoineSettings.Instance.FooterHeight);
        }        // ═════════════════════════════════════════════════════════════════════
        // ActivateTab — switch to a tab programmatically
        // ═════════════════════════════════════════════════════════════════════

        internal void ActivateTab(string tabId)
        {
            if (_activeTabId == tabId) return;
            SwitchTab(tabId);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Toolbar
        // ═════════════════════════════════════════════════════════════════════
        private void BuildToolbar()
        {
            // LemoineTitleBar provides: Surface background, Border bottom line, drag-to-move,
            // icon glyph, and title text. Only the right-side actions are built inline here.
            _toolbarBorder.BorderThickness = new Thickness(0);

            var closeBtn = BuildFlatButton("\u00d7");
            closeBtn.Margin = new Thickness(8, 0, 0, 0);
            closeBtn.SetResourceReference(Button.ForegroundProperty, "LemoineTextDim");
            closeBtn.Click += (s, e) => Close();

            // Right slot: close button only — Templates button now lives in the trade bar
            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rightPanel.Children.Add(closeBtn);

            _toolbarBorder.Child = new Controls.LemoineTitleBar
            {
                Title        = "Settings",
                IconGlyph    = "⚙",   // ⚙ gear
                RightContent = rightPanel,
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        // Top pill navigation
        // ═════════════════════════════════════════════════════════════════════
        private static readonly (string Id, string Label)[] _navDefs =
        {
            ("general",    "General"),
            ("dimensions", "Dimensions"),
        };

        private void BuildTabNav()
        {
            _navTabs.Clear();

            _pillNavBorder.Padding        = new Thickness(8, 6, 8, 0);
            _pillNavBorder.BorderThickness = new Thickness(0, 0, 0, 1);
            _pillNavBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var grid = new Grid();
            foreach (var _ in _navDefs)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < _navDefs.Length; i++)
            {
                var (id, label) = _navDefs[i];
                var tab = BuildNavTab(id, label);
                _navTabs[id] = tab;
                Grid.SetColumn(tab, i);
                grid.Children.Add(tab);
            }

            _pillNavBorder.Child = grid;
        }

        private Border BuildNavTab(string tabId, string label)
        {
            bool active = tabId == _activeTabId;

            var tab = new Border
            {
                Cursor          = Cursors.Hand,
                CornerRadius    = active ? new CornerRadius(6, 6, 0, 0) : new CornerRadius(6),
                BorderThickness = active ? new Thickness(1, 1, 1, 0) : new Thickness(1, 1, 1, 1),
                Margin          = active ? new Thickness(1, 2, 1, -1) : new Thickness(1, 4, 1, 2),
                Padding         = new Thickness(6, 6, 6, 6),
            };
            tab.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            if (active) tab.SetResourceReference(Border.BackgroundProperty, "LemoineBg");
            else        tab.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");

            var lbl = new TextBlock
            {
                Text                = label,
                TextAlignment       = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                TextTrimming        = TextTrimming.CharacterEllipsis,
                FontWeight          = active ? FontWeights.SemiBold : FontWeights.Normal,
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, active ? "LemoineText" : "LemoineTextDim");
            tab.Child = lbl;

            tab.MouseLeftButtonDown += (s, e) => SwitchTab(tabId);
            tab.MouseEnter += (s, e) =>
            {
                if (tabId != _activeTabId)
                    tab.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            };
            tab.MouseLeave += (s, e) =>
            {
                if (tabId != _activeTabId)
                    tab.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            };
            return tab;
        }

        private void SwitchTab(string tabId)
        {
            _activeTabId = tabId;

            // Refresh tab styles
            foreach (var (id, _) in _navDefs)
            {
                if (!_navTabs.TryGetValue(id, out var tab)) continue;
                bool active = id == tabId;

                tab.CornerRadius    = active ? new CornerRadius(6, 6, 0, 0) : new CornerRadius(6);
                tab.BorderThickness = active ? new Thickness(1, 1, 1, 0) : new Thickness(1, 1, 1, 1);
                tab.Margin          = active ? new Thickness(1, 2, 1, -1) : new Thickness(1, 4, 1, 2);
                tab.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                if (active) tab.SetResourceReference(Border.BackgroundProperty, "LemoineBg");
                else        tab.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");

                if (tab.Child is TextBlock lbl)
                {
                    lbl.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                    lbl.SetResourceReference(TextBlock.ForegroundProperty, active ? "LemoineText" : "LemoineTextDim");
                }
            }

            // Build content for the selected tab
            UIElement content;
            switch (tabId)
            {
                case "general":    content = BuildGeneralContent();    break;
                case "dimensions": content = BuildDimensionsContent(); break;
                case "t08":        content = LegendCreatorTabContent.BuildContent(this); break;
                // (Create Sheets settings tab removed — values are now per-run in the tool.)
                default:         content = BuildNoSettingsContent(); break;
            }

            _contentBorder.Child = content;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Footer
        // ═════════════════════════════════════════════════════════════════════
        private void BuildFooter()
        {
            _footerBorder.SetResourceReference(Border.PaddingProperty, "LemoineTh_FooterPad");
            _footerBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            _footerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            _fStatusText = new TextBlock
            {
                Text = "", VerticalAlignment = VerticalAlignment.Center,
                FontStyle = FontStyles.Italic,
            };
            _fStatusText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _fStatusText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineGreen");
            _fStatusText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            // Settings auto-save the moment each control changes (theme/size on click,
            // tool fields via ApplySettings on change), so there is no Apply button —
            // only Close. _fStatusText is retained for the Filters template messages.
            var closeBtn = BuildFlatButton("Close");
            closeBtn.Click += (s, e) => Close();

            // Build-info line (build time · branch @ commit) — dim, monospace, docked
            // left. Values are stamped at compile time by the GenerateBuildInfo target;
            // they read "unknown" if that target did not run.
            _fBuildInfoText = new TextBlock
            {
                Text              = BuildInfo.Summary,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip           = "Build metadata (build time · git branch @ commit)",
            };
            _fBuildInfoText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _fBuildInfoText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _fBuildInfoText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var dp = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            var btnStack = new StackPanel { Orientation = Orientation.Horizontal };
            btnStack.Children.Add(closeBtn);
            DockPanel.SetDock(btnStack, Dock.Right);
            DockPanel.SetDock(_fBuildInfoText, Dock.Left);
            dp.Children.Add(btnStack);
            dp.Children.Add(_fBuildInfoText);
            dp.Children.Add(_fStatusText); // fill — transient template/save messages

            _footerBorder.Child = dp;
        }

        private void FlashStatus(string msg)
        {
            if (_fStatusText == null) return;
            _fStatusText.Text = msg;
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            timer.Tick += (s, e) => { _fStatusText.Text = ""; timer.Stop(); };
            timer.Start();
        }

        // ═════════════════════════════════════════════════════════════════════
        // General tab — theme + UI size
        // ═════════════════════════════════════════════════════════════════════
        // Spec-based tool settings tab
        // ═════════════════════════════════════════════════════════════════════
        // General tab helpers — theme rows / size rows
        // ═════════════════════════════════════════════════════════════════════
        // Spec control builder (for tool tabs)
        // ═════════════════════════════════════════════════════════════════════
        // UI helpers
        // ═════════════════════════════════════════════════════════════════════

        private TextBlock ContentHeader(string text)
        {
            var tb = new TextBlock { Text = text, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 18) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XL");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock SubLabel(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 8) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }

        // Delegates to the shared factory — all Lemoine windows now use the same
        // ControlTemplate and resource references for flat buttons.
        private static Button BuildFlatButton(string label) =>
            LemoineControlStyles.BuildButton(label, LemoineControlStyles.LemoineButtonVariant.Ghost);

        // Delegates to the shared factory — height, padding, and font all scale with LemoineH_BtnSm / LemoineTh_BtnSmPad.
        private static Button FlatSmBtn(string label) =>
            LemoineControlStyles.BuildSmallButton(label, LemoineControlStyles.LemoineButtonVariant.Ghost);


    }
}
