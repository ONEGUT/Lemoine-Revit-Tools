using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LemoineTools.Framework
{
    // ─────────────────────────────────────────────────────────────────────────
    // Read-only "Tools Overview" window — a guided field-guide to every tool in
    // the plugin and how they tie together.
    //
    //   Row 0  Toolbar (TitleBar)
    //   Row 1  Category tab strip — one tab per ribbon panel, in ribbon order
    //   Row 2  Body — scrolling tool-card pane for the selected category
    //   Row 3  Footer (Close)
    //
    // Modelled on GlobalSettingsWindow (NOT StepFlowWindow — no wizard chrome).
    // Makes no Revit API calls; opened on Revit's main STA thread by OpenOverviewCommand.
    // All colors/sizes via SetResourceReference; named global-event handlers are
    // detached on Closed to avoid the leaked-subscription Revit crash.
    //
    // Previously a two-layer nav (a "workflow stage" strip over a left category rail);
    // collapsed to one layer because the categories already are the ribbon panels in
    // ribbon order, so the stage grouping was a redundant indirection (WS-11).
    // ─────────────────────────────────────────────────────────────────────────
    public partial class ToolsOverviewWindow : Window
    {
        private const string IconFont = "Segoe MDL2 Assets";

        private string _activeCategoryId = "";
        private readonly Dictionary<string, Border> _catTabs = new Dictionary<string, Border>();
        private Border? _cardsHost;

        // Navigation: tool name → its card (for the current category), the cards scroll
        // viewer, and lookup maps so a "feeds/fed by" chip can jump to its target.
        private readonly Dictionary<string, Border> _toolCards = new Dictionary<string, Border>();
        private ScrollViewer? _cardsScroll;
        private static readonly Dictionary<string, (string CatId, string ToolName)> _toolIndex = BuildToolIndex();
        private static readonly Dictionary<string, string> _catByName = BuildCatIndex();

        private static Dictionary<string, (string, string)> BuildToolIndex()
        {
            var d = new Dictionary<string, (string, string)>();
            foreach (var c in ToolsOverviewCatalog.Categories)
                foreach (var t in c.Tools)
                    d[t.Name] = (c.Id, t.Name);
            return d;
        }

        private static Dictionary<string, string> BuildCatIndex()
        {
            var d = new Dictionary<string, string>();
            foreach (var c in ToolsOverviewCatalog.Categories)
                d[c.Name] = c.Id;
            return d;
        }

        public ToolsOverviewWindow()
        {
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

        // ── Global-event handlers ─────────────────────────────────────────────
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
            BuildCategoryTabs();
            BuildBody();
            BuildFooterBar();
            SelectCategory(ToolsOverviewCatalog.Categories[0].Id);

            AutomationProperties.SetName(this, AppStrings.T("overview.window.automationName"));
        }

        private void UpdateRowHeights()
        {
            if (_root == null) return;
            _root.RowDefinitions[0].Height = new GridLength(AppSettings.Instance.ToolbarHeight);
            _root.RowDefinitions[3].Height = new GridLength(AppSettings.Instance.FooterHeight);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Toolbar
        // ═════════════════════════════════════════════════════════════════════
        private void BuildToolbar()
        {
            _toolbarBorder.BorderThickness = new Thickness(0);

            var closeBtn = BuildGhostButton("×");
            closeBtn.Uid = "toolbar-close";
            closeBtn.Margin = new Thickness(8, 0, 0, 0);
            closeBtn.SetResourceReference(Button.ForegroundProperty, "LemoineTextDim");
            closeBtn.Click += (s, e) => Close();

            var rightPanel = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rightPanel.Children.Add(closeBtn);

            _toolbarBorder.Child = new Controls.TitleBar
            {
                Title          = AppStrings.T("overview.window.title"),
                IconGlyph      = char.ConvertFromUtf32(0xE946), // ⓘ Info
                AllowsMaximize = true,
                RightContent   = rightPanel,
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        // Category tab strip (Row 1) — one tab per ribbon panel, in ribbon order
        // ═════════════════════════════════════════════════════════════════════
        private void BuildCategoryTabs()
        {
            _catTabs.Clear();

            _flowBorder.Padding         = new Thickness(12, 8, 12, 0);
            _flowBorder.BorderThickness = new Thickness(0, 0, 0, 1);
            _flowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var cat in ToolsOverviewCatalog.Categories)
            {
                var tab = BuildCategoryTab(cat);
                _catTabs[cat.Id] = tab;
                stack.Children.Add(tab);
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = stack,
            };
            _flowBorder.Child = scroll;
        }

        private Border BuildCategoryTab(OverviewCategory cat)
        {
            bool active = cat.Id == _activeCategoryId;

            var tab = new Border
            {
                Uid             = "cat-tab-" + cat.Id,
                Cursor          = Cursors.Hand,
                CornerRadius    = active ? new CornerRadius(6, 6, 0, 0) : new CornerRadius(6),
                BorderThickness = active ? new Thickness(1, 1, 1, 0) : new Thickness(1, 1, 1, 1),
                Margin          = active ? new Thickness(0, 0, 4, -1) : new Thickness(0, 2, 4, 1),
                Padding         = new Thickness(10, 7, 10, 7),
            };
            tab.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            if (active) tab.SetResourceReference(Border.BackgroundProperty, "LemoineBg");
            else        tab.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            var glyph = new TextBlock
            {
                Uid               = "cat-tab-" + cat.Id + "-glyph",
                Text              = cat.Glyph,
                FontFamily        = new FontFamily(IconFont),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 7, 0),
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, active ? "LemoineAccent" : "LemoineTextDim");
            glyph.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");

            var name = new TextBlock
            {
                Uid                 = "cat-tab-" + cat.Id + "-label",
                Text                = cat.Name,
                VerticalAlignment   = VerticalAlignment.Center,
                TextTrimming        = TextTrimming.CharacterEllipsis,
                FontWeight          = active ? FontWeights.SemiBold : FontWeights.Normal,
            };
            name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            name.SetResourceReference(TextBlock.ForegroundProperty, active ? "LemoineText" : "LemoineTextDim");

            row.Children.Add(glyph);
            row.Children.Add(name);
            tab.Child = row;

            tab.MouseLeftButtonDown += (s, e) => SelectCategory(cat.Id);
            tab.MouseEnter += (s, e) =>
            {
                if (cat.Id != _activeCategoryId)
                    tab.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            };
            tab.MouseLeave += (s, e) =>
            {
                if (cat.Id != _activeCategoryId)
                    tab.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            };
            return tab;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Body — cards (Row 2)
        // ═════════════════════════════════════════════════════════════════════
        private void BuildBody()
        {
            _cardsHost = new Border();
            _bodyBorder.Child = _cardsHost;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Selection
        // ═════════════════════════════════════════════════════════════════════
        // internal (not private): the LemoinePreview design-twin parity CaptureRunner
        // calls this to step through every category tab while capturing snapshots.
        internal void SelectCategory(string categoryId)
        {
            var cat = ToolsOverviewCatalog.FindCategory(categoryId);
            if (cat == null) return;
            _activeCategoryId = categoryId;

            StyleCategoryTabs();
            BuildCards(cat);
        }

        private void StyleCategoryTabs()
        {
            foreach (var cat in ToolsOverviewCatalog.Categories)
            {
                if (!_catTabs.TryGetValue(cat.Id, out var tab)) continue;
                bool active = cat.Id == _activeCategoryId;

                tab.CornerRadius    = active ? new CornerRadius(6, 6, 0, 0) : new CornerRadius(6);
                tab.BorderThickness = active ? new Thickness(1, 1, 1, 0) : new Thickness(1, 1, 1, 1);
                tab.Margin          = active ? new Thickness(0, 0, 4, -1) : new Thickness(0, 2, 4, 1);
                tab.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                if (active) tab.SetResourceReference(Border.BackgroundProperty, "LemoineBg");
                else        tab.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");

                if (tab.Child is StackPanel row && row.Children.Count >= 2)
                {
                    if (row.Children[0] is TextBlock glyphTb)
                        glyphTb.SetResourceReference(TextBlock.ForegroundProperty, active ? "LemoineAccent" : "LemoineTextDim");
                    if (row.Children[1] is TextBlock nameTb)
                    {
                        nameTb.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                        nameTb.SetResourceReference(TextBlock.ForegroundProperty, active ? "LemoineText" : "LemoineTextDim");
                    }
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Cards
        // ═════════════════════════════════════════════════════════════════════
        private void BuildCards(OverviewCategory cat)
        {
            if (_cardsHost == null) return;

            var stack = new StackPanel { Uid = "cards-stack", Margin = new Thickness(16, 14, 16, 14) };

            // Header: category name
            var headerDock = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 3) };

            var title = new TextBlock { Uid = "cards-title", Text = cat.Name, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            title.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XL");
            title.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            headerDock.Children.Add(title);
            stack.Children.Add(headerDock);

            var intro = new TextBlock { Uid = "cards-intro", Text = cat.Intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            intro.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            intro.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            intro.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            stack.Children.Add(intro);

            _toolCards.Clear();
            foreach (var tool in cat.Tools)
            {
                var card = BuildCard(tool);
                _toolCards[tool.Name] = card;
                stack.Children.Add(card);
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack,
            };
            _cardsScroll = scroll;
            _cardsHost.Child = scroll;
        }

        // Stable, twin-joinable id for a tool card — matches the design twin's
        // data-id="card-<slug>" convention (see devtools/design-twin/pages/tools-overview.html).
        private static string Slug(string name)
        {
            var chars = name.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray();
            return string.Join("-", new string(chars).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private Border BuildCard(OverviewTool tool)
        {
            string slug = Slug(tool.Name);
            var card = new Border
            {
                Uid             = "card-" + slug,
                CornerRadius    = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(11, 10, 11, 11),
                Margin          = new Thickness(0, 0, 0, 9),
            };
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var stack = new StackPanel();

            // Top: icon + name (left), "Dummy run ▶" (right)
            var top = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 5) };

            if (ToolsOverviewDemos.For(tool.Name) != null)
            {
                var runBtn = ControlStyles.BuildSmallButton(AppStrings.T("overview.window.runButton"),
                    ControlStyles.ButtonVariant.Primary);
                runBtn.Uid = "card-" + slug + "-run";
                runBtn.VerticalAlignment = VerticalAlignment.Center;
                runBtn.ToolTip = AppStrings.T("overview.window.runTooltip", tool.Name);
                runBtn.Click += (s, e) => LaunchDemo(tool.Name);
                DockPanel.SetDock(runBtn, Dock.Right);
                top.Children.Add(runBtn);
            }

            var left = new StackPanel { Orientation = Orientation.Horizontal };

            var iconBox = new Border
            {
                Uid = "card-" + slug + "-icon",
                Width = 26, Height = 26, CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            iconBox.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
            iconBox.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            var icon = new TextBlock
            {
                Text                = tool.Glyph,
                FontFamily          = new FontFamily(IconFont),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            icon.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
            iconBox.Child = icon;
            left.Children.Add(iconBox);

            var name = new TextBlock { Uid = "card-" + slug + "-name", Text = tool.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            left.Children.Add(name);
            top.Children.Add(left);  // fills remaining width (LastChildFill)
            stack.Children.Add(top);

            // Blurb
            var blurb = new TextBlock { Uid = "card-" + slug + "-blurb", Text = tool.Blurb, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7) };
            blurb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            blurb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            blurb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            stack.Children.Add(blurb);

            // Relationships
            var fedBy = BuildRelationship(AppStrings.T("overview.window.fedBy"), tool.FedBy, accent: false, "card-" + slug + "-fedby");
            if (fedBy != null) stack.Children.Add(fedBy);
            var feeds = BuildRelationship(AppStrings.T("overview.window.feeds"), tool.Feeds, accent: true, "card-" + slug + "-feeds");
            if (feeds != null) stack.Children.Add(feeds);

            // Example
            if (!string.IsNullOrEmpty(tool.Example))
            {
                var exBorder = new Border { Uid = "card-" + slug + "-example", BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(7, 0, 0, 0), Margin = new Thickness(0, 7, 0, 0) };
                exBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                var ex = new TextBlock { Text = tool.Example, TextWrapping = TextWrapping.Wrap };
                ex.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                ex.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                ex.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                exBorder.Child = ex;
                stack.Children.Add(exBorder);
            }

            card.Child = stack;
            return card;
        }

        private FrameworkElement? BuildRelationship(string label, string[] items, bool accent, string uid)
        {
            if (items == null || items.Length == 0) return null;

            var wrap = new WrapPanel { Uid = uid, Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };

            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            wrap.Children.Add(lbl);

            foreach (var item in items)
            {
                bool linkable = TryResolveTarget(item, out var catId, out var toolName);
                var prefix = accent ? item + " →" : "← " + item;
                var chip = new Border
                {
                    CornerRadius    = new CornerRadius(3),
                    BorderThickness = new Thickness(1),
                    Padding         = new Thickness(7, 2, 7, 2),
                    Margin          = new Thickness(0, 0, 5, 4),
                    Cursor          = linkable ? Cursors.Hand : Cursors.Arrow,
                };
                chip.SetResourceReference(Border.BackgroundProperty,  accent ? "LemoineAccentDim" : "LemoineGreenDim");
                chip.SetResourceReference(Border.BorderBrushProperty, accent ? "LemoineAccent"    : "LemoineGreen");
                var tb = new TextBlock { Text = prefix };
                tb.SetResourceReference(TextBlock.ForegroundProperty, accent ? "LemoineAccent" : "LemoineGreen");
                tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                if (linkable)
                {
                    tb.TextDecorations = TextDecorations.Underline;
                    chip.ToolTip = AppStrings.T("overview.window.goToTooltip", toolName ?? catId);
                    chip.MouseLeftButtonDown += (s, e) => { NavigateToTool(catId, toolName); e.Handled = true; };
                }
                chip.Child = tb;
                wrap.Children.Add(chip);
            }
            return wrap;
        }

        // ── Chip → tool/category navigation ───────────────────────────────────
        // Strips the "(…)" qualifier and matches a tool name first, then a category
        // name. Items that resolve to neither (e.g. "Discover (auto-detect rules)")
        // render as plain, non-clickable chips.
        private static bool TryResolveTarget(string raw, out string catId, out string? toolName)
        {
            catId = ""; toolName = null;
            var clean = raw;
            int paren = clean.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) clean = clean.Substring(0, paren);
            clean = clean.Trim();

            if (_toolIndex.TryGetValue(clean, out var hit)) { catId = hit.CatId; toolName = hit.ToolName; return true; }
            if (_catByName.TryGetValue(clean, out var cid))  { catId = cid; toolName = null; return true; }
            return false;
        }

        private void NavigateToTool(string catId, string? toolName)
        {
            SelectCategory(catId);   // rebuilds the cards pane + repopulates _toolCards
            if (string.IsNullOrEmpty(toolName)) return;

            // Defer until layout settles, then scroll the target card into view and flash it.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (toolName != null && _toolCards.TryGetValue(toolName, out var card))
                {
                    card.BringIntoView();
                    FlashCard(card);
                }
            }), DispatcherPriority.Loaded);
        }

        private void FlashCard(Border card)
        {
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            };
            timer.Start();
        }

        // ── Dummy run — open the tool's demo in a real StepFlowWindow ─────────
        // Mirrors the tool commands: the StepFlowWindow runs on its own dedicated STA
        // thread with a message pump, so the overview stays responsive. The demo tool
        // is Revit-free, so no UIApplication/document is needed.
        private void LaunchDemo(string toolName)
        {
            var spec = ToolsOverviewDemos.For(toolName);
            if (spec == null) return;

            var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                // Fully qualified: a bare `Dispatcher` inside this Window subclass would bind
                // to the inherited instance property, not the static class.
                try
                {
                    var win = new StepFlowWindow(new OverviewDemoTool(spec));
                    win.Closed += (s, e) => System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                    win.Show();
                    ready.Set();                       // released only after the window is up
                    System.Windows.Threading.Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    // Without this, a throw during construction would leave ready.Wait() (below)
                    // blocking Revit's main thread forever.
                    DiagnosticsLog.Error("ToolsOverview: launch demo run", ex);
                    ready.Set();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            ready.Wait();
        }

        // ═════════════════════════════════════════════════════════════════════
        // Footer
        // ═════════════════════════════════════════════════════════════════════
        private void BuildFooterBar()
        {
            _footerBorder.SetResourceReference(Border.PaddingProperty, "LemoineTh_FooterPad");
            _footerBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            _footerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var hint = new TextBlock
            {
                Uid               = "footer-hint",
                Text              = AppStrings.T("overview.window.footerHint"),
                VerticalAlignment = VerticalAlignment.Center,
                FontStyle         = FontStyles.Italic,
            };
            hint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            hint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var closeBtn = BuildGhostButton(AppStrings.T("overview.window.close"));
            closeBtn.Uid = "footer-close";
            closeBtn.Click += (s, e) => Close();

            var dp = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(closeBtn, Dock.Right);
            dp.Children.Add(closeBtn);
            dp.Children.Add(hint);
            _footerBorder.Child = dp;
        }

        private static Button BuildGhostButton(string label) =>
            ControlStyles.BuildButton(label, ControlStyles.ButtonVariant.Ghost);
    }
}
