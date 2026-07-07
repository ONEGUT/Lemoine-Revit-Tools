using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using LemoineTools.Framework.Controls;
using LemoineTools.Tools.Setup;

namespace LemoineTools.Framework
{
    /// <summary>
    /// Link Audit — a read-only report window (NOT a StepFlowWindow tool: there is nothing to
    /// run, so no wizard chrome, no Confirm/Run button). Modelled on ToolsOverviewWindow /
    /// GlobalSettingsWindow. Data is captured once, on Revit's main thread, by the launching
    /// command before this window is constructed; re-opening the tool re-captures fresh data.
    /// </summary>
    public partial class LinkAuditWindow : Window
    {
        private readonly LinkAuditData _data;

        public LinkAuditWindow(LinkAuditData data)
        {
            _data = data ?? new LinkAuditData();
            InitializeComponent();
            Loaded += OnLoaded;

            // Named handlers (not lambdas) so they can be detached on close — a leaked
            // subscription to a window whose dispatcher has shut down crashes/hangs Revit.
            AppSettings.Instance.ThemeChanged  += OnThemeChanged;
            AppSettings.Instance.UiSizeChanged += OnUiSizeChanged;
            Closed += (s, e) =>
            {
                AppSettings.Instance.ThemeChanged  -= OnThemeChanged;
                AppSettings.Instance.UiSizeChanged -= OnUiSizeChanged;
            };
        }

        private void OnThemeChanged(ThemePalette t)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppSettings.Instance.ApplyTo(Resources);
                Background = t.PageBg;
                _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            }));
        }

        private void OnUiSizeChanged(UiSize _)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppSettings.Instance.ApplyScaleTo(Resources);
                ControlStyles.InjectInto(Resources, scrollBarWidth: 8);
                UpdateRowHeights();
            }));
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            AppSettings.Instance.ApplyTo(Resources);
            ControlStyles.InjectInto(Resources, scrollBarWidth: 8);
            Background = AppSettings.Instance.ActiveTheme.PageBg;
            _root.SetResourceReference(Grid.BackgroundProperty, "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _outerBorder.CornerRadius = new CornerRadius(8); // matches Windows 11 DWM rounding

            UpdateRowHeights();
            BuildToolbar();
            BuildBody();
            BuildFooter();

            AutomationProperties.SetName(this, AppStrings.T("setup.linkAudit.window.automationName"));
        }

        private void UpdateRowHeights()
        {
            if (_root == null) return;
            _root.RowDefinitions[0].Height = new GridLength(AppSettings.Instance.ToolbarHeight);
            _root.RowDefinitions[2].Height = new GridLength(AppSettings.Instance.FooterHeight);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Toolbar
        // ═════════════════════════════════════════════════════════════════════
        private void BuildToolbar()
        {
            _toolbarBorder.BorderThickness = new Thickness(0);

            var closeBtn = BuildGhostButton("×");
            closeBtn.Margin = new Thickness(8, 0, 0, 0);
            closeBtn.SetResourceReference(Button.ForegroundProperty, "LemoineTextDim");
            closeBtn.Click += (s, e) => Close();

            _toolbarBorder.Child = new TitleBar
            {
                Title          = AppStrings.T("setup.linkAudit.window.title"),
                IconGlyph      = char.ConvertFromUtf32(0xE9D9), // Diagnostic
                AllowsMaximize = true,
                RightContent   = closeBtn,
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        // Body — the report itself
        // ═════════════════════════════════════════════════════════════════════
        private void BuildBody()
        {
            _bodyBorder.SetResourceReference(Border.PaddingProperty, "LemoineTh_CardPad");

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var outer  = new StackPanel();

            if (_data.Rows.Count == 0)
            {
                outer.Children.Add(Dim(AppStrings.T("setup.linkAudit.labels.noneFound")));
            }
            else
            {
                int flagged = _data.Rows.Count(r => r.IsWarning);
                outer.Children.Add(Label(AppStrings.T("setup.linkAudit.labels.summary", _data.Rows.Count, flagged)));

                foreach (var row in _data.Rows)
                {
                    var content = new StackPanel();
                    content.Children.Add(FieldRow(AppStrings.T("setup.linkAudit.labels.fieldStatus"), row.LoadStatus,
                        !row.LoadStatus.Equals("Loaded", StringComparison.OrdinalIgnoreCase)));
                    content.Children.Add(FieldRow(AppStrings.T("setup.linkAudit.labels.fieldPositioning"), row.Positioning, false));
                    content.Children.Add(FieldRow(AppStrings.T("setup.linkAudit.labels.fieldDisplay"), row.DisplayMode,
                        !(row.DisplayMode.Equals("By Host View", StringComparison.OrdinalIgnoreCase)
                          || row.DisplayMode.Equals("n/a", StringComparison.OrdinalIgnoreCase))));
                    content.Children.Add(FieldRow(AppStrings.T("setup.linkAudit.labels.fieldPinned"),
                        row.Pinned ? AppStrings.T("setup.linkAudit.labels.yes") : AppStrings.T("setup.linkAudit.labels.no"), false));
                    content.Children.Add(FieldRow(AppStrings.T("setup.linkAudit.labels.fieldWorkset"), row.WorksetName, false));
                    content.Children.Add(FieldRow(AppStrings.T("setup.linkAudit.labels.fieldAttachment"), row.AttachmentType, false));
                    content.Children.Add(FieldRow(AppStrings.T("setup.linkAudit.labels.fieldLastSaved"), row.LastSaved, false));

                    var card = new SectionCard { Header = row.Name, CardContent = content, Margin = new Thickness(0, 0, 0, 10) };
                    outer.Children.Add(card);
                }
            }

            scroll.Content   = outer;
            _bodyBorder.Child = scroll;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Footer — hint text + Close (no Confirm/Run button; nothing to run)
        // ═════════════════════════════════════════════════════════════════════
        private void BuildFooter()
        {
            _footerBorder.SetResourceReference(Border.PaddingProperty, "LemoineTh_FooterPad");
            _footerBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            _footerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var hint = new TextBlock
            {
                Text              = AppStrings.T("setup.linkAudit.window.footerHint"),
                VerticalAlignment = VerticalAlignment.Center,
                FontStyle         = FontStyles.Italic,
            };
            hint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            hint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var closeBtn = BuildGhostButton(AppStrings.T("setup.linkAudit.window.close"));
            closeBtn.Click += (s, e) => Close();

            var dp = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(closeBtn, Dock.Right);
            dp.Children.Add(closeBtn);
            dp.Children.Add(hint);
            _footerBorder.Child = dp;
        }

        private static Button BuildGhostButton(string label) =>
            ControlStyles.BuildButton(label, ControlStyles.ButtonVariant.Ghost);

        // ── helpers ──────────────────────────────────────────────────────────────
        private static TextBlock Label(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock Dim(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static FrameworkElement FieldRow(string label, string value, bool warn)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            DockPanel.SetDock(lbl, Dock.Left);

            var val = new TextBlock
            {
                Text = value, HorizontalAlignment = HorizontalAlignment.Right, TextWrapping = TextWrapping.Wrap,
            };
            val.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            val.SetResourceReference(TextBlock.ForegroundProperty, warn ? "LemoineRed" : "LemoineText");
            val.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            dp.Children.Add(lbl);
            dp.Children.Add(val);
            return dp;
        }
    }
}
