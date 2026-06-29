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

namespace LemoineTools.Lemoine
{
    // ─────────────────────────────────────────────────────────────────────────
    // Read-only "Tools Overview" window — a guided field-guide to every tool in
    // the plugin and how they tie together. Layout = Option A (Pipeline + Catalog):
    //
    //   Row 0  Toolbar (LemoineTitleBar)
    //   Row 1  Workflow strip — six stages, click to jump to that stage's category
    //   Row 2  Body — left category rail + right scrolling tool-card pane
    //   Row 3  Footer (Close)
    //
    // Modelled on GlobalSettingsWindow (NOT StepFlowWindow — no wizard chrome).
    // Makes no Revit API calls; opened on Revit's main STA thread by OpenOverviewCommand.
    // All colors/sizes via SetResourceReference; named global-event handlers are
    // detached on Closed to avoid the leaked-subscription Revit crash.
    // ─────────────────────────────────────────────────────────────────────────
    public partial class ToolsOverviewWindow : Window
    {
        private const string IconFont = "Segoe MDL2 Assets";

        private string _activeCategoryId = "";
        private readonly Dictionary<string, Border> _railRows   = new Dictionary<string, Border>();
        private readonly Dictionary<string, (TextBlock name, TextBlock glyph)> _railText =
            new Dictionary<string, (TextBlock, TextBlock)>();
        private readonly Dictionary<string, Border> _stageChips = new Dictionary<string, Border>();
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
            LemoineSettings.Instance.ThemeChanged  += OnThemeChanged;
            LemoineSettings.Instance.UiSizeChanged += OnUiSizeChanged;
            Closed += (s, e) =>
            {
                LemoineSettings.Instance.ThemeChanged  -= OnThemeChanged;
                LemoineSettings.Instance.UiSizeChanged -= OnUiSizeChanged;
            };
        }

        // ── Global-event handlers ─────────────────────────────────────────────
        private void OnThemeChanged(LemoineTheme t)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LemoineSettings.Instance.ApplyTo(Resources);
                Background = t.PageBg;
                _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            }));
        }

        private void OnUiSizeChanged(LemoineUiSize _)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LemoineSettings.Instance.ApplyScaleTo(Resources);
                LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 8);
                UpdateRowHeights();
            }));
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
            BuildWorkflowStrip();
            BuildBody();
            BuildFooterBar();
            SelectCategory(ToolsOverviewCatalog.Categories[0].Id);

            AutomationProperties.SetName(this, "Tools Overview");
        }

        private void UpdateRowHeights()
        {
            if (_root == null) return;
            _root.RowDefinitions[0].Height = new GridLength(LemoineSettings.Instance.ToolbarHeight);
            _root.RowDefinitions[3].Height = new GridLength(LemoineSettings.Instance.FooterHeight);
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

            var rightPanel = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rightPanel.Children.Add(closeBtn);

            _toolbarBorder.Child = new Controls.LemoineTitleBar
            {
                Title          = "Lemoine Tools — Overview",
                IconGlyph      = char.ConvertFromUtf32(0xE946), // ⓘ Info
                AllowsMaximize = true,
                RightContent   = rightPanel,
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        // Workflow strip (Row 1)
        // ═════════════════════════════════════════════════════════════════════
        private void BuildWorkflowStrip()
        {
            _stageChips.Clear();

            _flowBorder.Padding         = new Thickness(12, 10, 12, 10);
            _flowBorder.BorderThickness = new Thickness(0, 0, 0, 1);
            _flowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var stages = ToolsOverviewCatalog.Stages;
            var grid   = new Grid();
            for (int i = 0; i < stages.Length; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (i < stages.Length - 1)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            for (int i = 0; i < stages.Length; i++)
            {
                var chip = BuildStageChip(stages[i]);
                _stageChips[stages[i].Number] = chip;
                Grid.SetColumn(chip, i * 2);
                grid.Children.Add(chip);

                if (i < stages.Length - 1)
                {
                    var arrow = new TextBlock
                    {
                        Text                = "→",
                        VerticalAlignment   = VerticalAlignment.Center,
                        Margin              = new Thickness(4, 0, 4, 0),
                    };
                    arrow.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    arrow.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
                    Grid.SetColumn(arrow, i * 2 + 1);
                    grid.Children.Add(arrow);
                }
            }

            _flowBorder.Child = grid;
        }

        private Border BuildStageChip(OverviewStage stage)
        {
            var chip = new Border
            {
                CornerRadius    = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(9, 7, 9, 7),
                Cursor          = Cursors.Hand,
            };

            var stack = new StackPanel();

            var num = new TextBlock { Text = stage.Number };
            num.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            num.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            num.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var name = new TextBlock { Text = stage.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 1, 0, 0) };
            name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var tag = new TextBlock { Text = stage.Tagline, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
            tag.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tag.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tag.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            stack.Children.Add(num);
            stack.Children.Add(name);
            stack.Children.Add(tag);
            chip.Child = stack;

            chip.MouseLeftButtonDown += (s, e) => SelectCategory(stage.CategoryIds[0]);
            chip.MouseEnter += (s, e) =>
            {
                if (!IsStageActive(stage))
                    chip.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            };
            chip.MouseLeave += (s, e) =>
            {
                if (!IsStageActive(stage))
                    chip.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            };
            return chip;
        }

        private bool IsStageActive(OverviewStage stage)
        {
            var owning = ToolsOverviewCatalog.StageForCategory(_activeCategoryId);
            return owning != null && owning.Number == stage.Number;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Body — rail + cards (Row 2)
        // ═════════════════════════════════════════════════════════════════════
        private void BuildBody()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(206) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ── Left rail ─────────────────────────────────────────────────────
            var railBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0) };
            railBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            railBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var railStack = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            foreach (var cat in ToolsOverviewCatalog.Categories)
            {
                var row = BuildRailRow(cat);
                _railRows[cat.Id] = row;
                railStack.Children.Add(row);
            }

            var railScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = railStack,
            };
            railBorder.Child = railScroll;
            Grid.SetColumn(railBorder, 0);
            grid.Children.Add(railBorder);

            // ── Right cards host (filled per selection) ───────────────────────
            _cardsHost = new Border();
            Grid.SetColumn(_cardsHost, 1);
            grid.Children.Add(_cardsHost);

            _bodyBorder.Child = grid;
        }

        private Border BuildRailRow(OverviewCategory cat)
        {
            var row = new Border
            {
                Padding         = new Thickness(14, 8, 12, 8),
                BorderThickness = new Thickness(2, 0, 0, 0), // left accent bar slot
                Cursor          = Cursors.Hand,
            };

            var dock = new DockPanel { LastChildFill = true };

            var glyph = new TextBlock
            {
                Text              = cat.Glyph,
                FontFamily        = new FontFamily(IconFont),
                VerticalAlignment = VerticalAlignment.Center,
                Width             = 18,
                TextAlignment     = TextAlignment.Center,
                Margin            = new Thickness(0, 0, 9, 0),
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            glyph.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            DockPanel.SetDock(glyph, Dock.Left);

            var count = new TextBlock
            {
                Text              = cat.Tools.Length.ToString(),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 0, 0, 0),
            };
            count.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            count.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            count.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            DockPanel.SetDock(count, Dock.Right);

            var name = new TextBlock
            {
                Text              = cat.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            dock.Children.Add(glyph);
            dock.Children.Add(count);
            dock.Children.Add(name);
            row.Child = dock;
            _railText[cat.Id] = (name, glyph);

            row.MouseLeftButtonDown += (s, e) => SelectCategory(cat.Id);
            row.MouseEnter += (s, e) =>
            {
                if (cat.Id != _activeCategoryId)
                    row.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            };
            row.MouseLeave += (s, e) =>
            {
                if (cat.Id != _activeCategoryId)
                    row.Background = Brushes.Transparent;
            };
            return row;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Selection
        // ═════════════════════════════════════════════════════════════════════
        private void SelectCategory(string categoryId)
        {
            var cat = ToolsOverviewCatalog.FindCategory(categoryId);
            if (cat == null) return;
            _activeCategoryId = categoryId;

            StyleRailRows();
            StyleStageChips();
            BuildCards(cat);
        }

        private void StyleRailRows()
        {
            foreach (var cat in ToolsOverviewCatalog.Categories)
            {
                if (!_railRows.TryGetValue(cat.Id, out var row)) continue;
                bool active = cat.Id == _activeCategoryId;

                if (active)
                {
                    row.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                    row.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                }
                else
                {
                    row.Background   = Brushes.Transparent;
                    row.BorderBrush  = Brushes.Transparent;
                }

                if (_railText.TryGetValue(cat.Id, out var t))
                {
                    t.name.SetResourceReference(TextBlock.ForegroundProperty,  active ? "LemoineText"   : "LemoineTextSub");
                    t.glyph.SetResourceReference(TextBlock.ForegroundProperty, active ? "LemoineAccent" : "LemoineTextDim");
                }
            }
        }

        private void StyleStageChips()
        {
            foreach (var stage in ToolsOverviewCatalog.Stages)
            {
                if (!_stageChips.TryGetValue(stage.Number, out var chip)) continue;
                bool active = IsStageActive(stage);

                if (active)
                {
                    chip.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                    chip.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                }
                else
                {
                    chip.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                    chip.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                }

                if (chip.Child is StackPanel sp && sp.Children.Count >= 2 && sp.Children[1] is TextBlock nameTb)
                    nameTb.SetResourceReference(TextBlock.ForegroundProperty, active ? "LemoineAccent" : "LemoineText");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Cards
        // ═════════════════════════════════════════════════════════════════════
        private void BuildCards(OverviewCategory cat)
        {
            if (_cardsHost == null) return;

            var stack = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

            // Header: category name + stage badge
            var headerDock = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 3) };

            var stage = ToolsOverviewCatalog.StageForCategory(cat.Id);
            if (stage != null)
            {
                var badge = new Border
                {
                    CornerRadius = new CornerRadius(3),
                    Padding      = new Thickness(7, 2, 7, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                badge.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
                var badgeTb = new TextBlock { Text = $"STAGE {stage.Number} · {stage.Name.ToUpperInvariant()}" };
                badgeTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                badgeTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                badgeTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                badge.Child = badgeTb;
                DockPanel.SetDock(badge, Dock.Right);
                headerDock.Children.Add(badge);
            }

            var title = new TextBlock { Text = cat.Name, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            title.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XL");
            title.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            headerDock.Children.Add(title);
            stack.Children.Add(headerDock);

            var intro = new TextBlock { Text = cat.Intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
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

        private Border BuildCard(OverviewTool tool)
        {
            var card = new Border
            {
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
                var runBtn = LemoineControlStyles.BuildSmallButton("Dummy run ▶",
                    LemoineControlStyles.LemoineButtonVariant.Primary);
                runBtn.VerticalAlignment = VerticalAlignment.Center;
                runBtn.ToolTip = $"Step through {tool.Name} in a real tool window. It's a demo, so nothing gets changed.";
                runBtn.Click += (s, e) => LaunchDemo(tool.Name);
                DockPanel.SetDock(runBtn, Dock.Right);
                top.Children.Add(runBtn);
            }

            var left = new StackPanel { Orientation = Orientation.Horizontal };

            var iconBox = new Border
            {
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

            var name = new TextBlock { Text = tool.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            left.Children.Add(name);
            top.Children.Add(left);  // fills remaining width (LastChildFill)
            stack.Children.Add(top);

            // Blurb
            var blurb = new TextBlock { Text = tool.Blurb, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7) };
            blurb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            blurb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            blurb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            stack.Children.Add(blurb);

            // Relationships
            var fedBy = BuildRelationship("FED BY", tool.FedBy, accent: false);
            if (fedBy != null) stack.Children.Add(fedBy);
            var feeds = BuildRelationship("FEEDS", tool.Feeds, accent: true);
            if (feeds != null) stack.Children.Add(feeds);

            // Example
            if (!string.IsNullOrEmpty(tool.Example))
            {
                var exBorder = new Border { BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(7, 0, 0, 0), Margin = new Thickness(0, 7, 0, 0) };
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

        private FrameworkElement? BuildRelationship(string label, string[] items, bool accent)
        {
            if (items == null || items.Length == 0) return null;

            var wrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };

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
                    chip.ToolTip = $"Go to {toolName ?? catId}";
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
                    LemoineLog.Error("ToolsOverview: launch demo run", ex);
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
                Text              = "A guide to every Lemoine tool and how they tie together.",
                VerticalAlignment = VerticalAlignment.Center,
                FontStyle         = FontStyles.Italic,
            };
            hint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            hint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var closeBtn = BuildGhostButton("Close");
            closeBtn.Click += (s, e) => Close();

            var dp = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(closeBtn, Dock.Right);
            dp.Children.Add(closeBtn);
            dp.Children.Add(hint);
            _footerBorder.Child = dp;
        }

        private static Button BuildGhostButton(string label) =>
            LemoineControlStyles.BuildButton(label, LemoineControlStyles.LemoineButtonVariant.Ghost);
    }
}
