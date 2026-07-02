using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Automation;
using System.Windows.Shapes;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Lemoine
{
    public partial class FiltersSettingsWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        //  FILTERS — Split-pane  (V3: Trade → Rule, no Category layer)
        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildFiltersContent()
        {
            if (_filterTrades == null)
                _filterTrades = AutoFiltersSettings.DeepCopy(AutoFiltersSettings.Instance.Trades);

            // Snapshot for the close-time dirty check (avoids redundant saves).
            if (_filtersSnapshot.Length == 0)
                _filtersSnapshot = SerializeTrades(_filterTrades);

            if (_fActiveTradeId == null || !_filterTrades.Any(t => t.Id == _fActiveTradeId))
                _fActiveTradeId = _filterTrades.FirstOrDefault()?.Id;

            // Auto-select first rule
            var activeTrade = _filterTrades.FirstOrDefault(t => t.Id == _fActiveTradeId);
            if (_fActiveRuleId == null || activeTrade?.Rules.All(r => r.Id != _fActiveRuleId) == true)
                _fActiveRuleId = activeTrade?.Rules.FirstOrDefault()?.Id;

            // ── Root: four-column split (trades | rule list | splitter | editor) ──
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280), MinWidth = 280 });

            var splitter = new GridSplitter
            {
                Width               = 5,
                VerticalAlignment   = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ResizeDirection     = GridResizeDirection.Columns,
                ResizeBehavior      = GridResizeBehavior.PreviousAndNext,
                Background          = Brushes.Transparent,
                Cursor              = Cursors.SizeWE,
            };
            Grid.SetColumn(splitter, 2);
            root.Children.Add(splitter);

            // ── Left panel ───────────────────────────────────────────────────
            var leftDock = new DockPanel { LastChildFill = true };
            leftDock.SetResourceReference(DockPanel.BackgroundProperty, "LemoineBg");

            // "＋ Add Rule" floats as the last item inside the rule list
            // (AppendAddRulePill, called from FRefreshRuleList) — no sticky bar.

            // Rule list (fills remaining)
            // Background = Transparent (not null) so inter-row gaps are hit-testable during drag-drop.
            _fRuleListPanel = new StackPanel { Margin = new Thickness(6, 6, 6, 0), AllowDrop = true, Background = Brushes.Transparent };

            // Handle drops that land in the gaps between pills (not on any rowBorder).
            _fRuleListPanel.DragOver += (s, e) =>
            {
                if (!(e.Data.GetData(DataFormats.StringFormat) is string d) || !d.StartsWith("RULE:")) return;
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                if (_dragSourceBorder == null) return;
                var children = _fRuleListPanel.Children;
                int srcIdx = children.IndexOf(_dragSourceBorder);
                if (srcIdx < 0) return;
                // Find the insert position from the cursor Y within the panel
                double curY = e.GetPosition(_fRuleListPanel).Y;
                int insertIdx = children.Count;
                double runY = 0;
                for (int i = 0; i < children.Count; i++)
                {
                    if (children[i] is FrameworkElement fe)
                    {
                        if (curY < runY + fe.ActualHeight / 2.0) { insertIdx = i; break; }
                        runY += fe.ActualHeight + fe.Margin.Bottom;
                    }
                }
                insertIdx = Math.Max(0, Math.Min(insertIdx, children.Count - 1));
                if (srcIdx < insertIdx) insertIdx--;
                if (insertIdx == srcIdx) return;
                children.RemoveAt(srcIdx);
                children.Insert(insertIdx, _dragSourceBorder);
            };
            _fRuleListPanel.Drop += (s, e) =>
            {
                e.Handled = true;
                if (!(e.Data.GetData(DataFormats.StringFormat) is string srcData) || !srcData.StartsWith("RULE:")) return;
                var activeTrade = _filterTrades?.FirstOrDefault(t => t.Id == _fActiveTradeId);
                if (activeTrade == null) return;
                var newOrderIds = _fRuleListPanel.Children
                    .OfType<FrameworkElement>()
                    .Select(el => el.Tag as string)
                    .Where(id => id != null)
                    .ToList();
                var reordered = newOrderIds
                    .Select(id => activeTrade.Rules.FirstOrDefault(r => r.Id == id))
                    .Where(r => r != null)
                    .ToList();
                if (reordered.Count == activeTrade.Rules.Count)
                {
                    activeTrade.Rules.Clear();
                    foreach (var r in reordered) activeTrade.Rules.Add(r!);
                }
                _dragSourceBorder = null;
                _dragRuleId       = null;
                FRefreshRuleList();
            };

            var ruleScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                AllowDrop                     = true,
                Content                       = _fRuleListPanel,
            };
            _fRuleScroll = ruleScroll;

            // Docked footer: applies the active trade's filters to the current view.
            var applyTradeFooter = BuildApplyFooter(LemoineStrings.T("globalSettings.filters.ruleList.applyTradeFooterLabel"),
                LemoineStrings.T("globalSettings.filters.ruleList.applyTradeFooterTooltip"),
                ApplyActiveTradeToView);
            DockPanel.SetDock(applyTradeFooter, Dock.Bottom);
            leftDock.Children.Add(applyTradeFooter);

            leftDock.Children.Add(ruleScroll);
            FRefreshRuleList();

            var leftWrapper = new Border { BorderThickness = new Thickness(1, 0, 0, 0) };
            leftWrapper.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            leftWrapper.Child = leftDock;
            Grid.SetColumn(leftWrapper, 1);
            root.Children.Add(leftWrapper);

            // ── Right panel (resizable editor, min 280px) ────────────────────
            _fEditorBorder = new Border
            {
                BorderThickness = new Thickness(1, 0, 0, 0),
            };
            _fEditorBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            _fEditorBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            FRefreshRuleEditor();
            Grid.SetColumn(_fEditorBorder, 3);
            root.Children.Add(_fEditorBorder);

            // ── Trades sidebar (col 0) ────────────────────────────────────────────
            _fTradeListPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            var tradeScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _fTradeListPanel,
            };
            _fTradeScroll = tradeScroll;

            // Templates pill button at top of sidebar
            var templatesPill = new Border
            {
                BorderThickness     = new Thickness(1),
                Padding             = new Thickness(10, 5, 10, 5),
                Cursor              = Cursors.Hand,
                Margin              = new Thickness(8, 8, 8, 4),
            };
            templatesPill.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
            templatesPill.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
            templatesPill.SetResourceReference(Border.BackgroundProperty,   "LemoineRaised");

            var templatesInner = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var templatesLabel = new TextBlock { Text = LemoineStrings.T("globalSettings.filters.sidebar.templatesButton"), VerticalAlignment = VerticalAlignment.Center };
            templatesLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            templatesLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            templatesLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            var templatesCaret = new TextBlock
            {
                Text              = "˅",
                Margin            = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            templatesCaret.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            templatesCaret.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            templatesCaret.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            templatesInner.Children.Add(templatesLabel);
            templatesInner.Children.Add(templatesCaret);
            templatesPill.Child = templatesInner;

            templatesPill.MouseEnter += (s, e) =>
                templatesPill.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
            templatesPill.MouseLeave += (s, e) =>
                templatesPill.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            templatesPill.MouseLeftButtonUp += (s, e) => { e.Handled = true; ShowTemplatesPopup(templatesPill); };

            // Separator between templates button and trade list
            var templSep = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 0) };
            templSep.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");

            // "＋ Add Trade" now floats as the last item inside the trade list
            // (AppendAddTradePill, called from FRefreshTradesSidebar) — no sticky bar.

            // Docked footer: [Apply | Remove] for the checked trades, on the current view.
            var applyAllFooter = BuildApplyRemoveFooter(
                LemoineStrings.T("globalSettings.filters.sidebar.applyToViewLabel"),   LemoineStrings.T("globalSettings.filters.sidebar.applyToViewTooltip"),
                ApplySelectedTradesToView,
                LemoineStrings.T("globalSettings.filters.sidebar.removeFromViewLabel"), LemoineStrings.T("globalSettings.filters.sidebar.removeFromViewTooltip"),
                RemoveSelectedTradesFromView);
            DockPanel.SetDock(applyAllFooter, Dock.Bottom);

            var sidebarDock = new DockPanel { LastChildFill = true };
            sidebarDock.SetResourceReference(DockPanel.BackgroundProperty, "LemoineSurface");
            DockPanel.SetDock(templatesPill,   Dock.Top);
            DockPanel.SetDock(templSep,        Dock.Top);
            sidebarDock.Children.Add(templatesPill);
            sidebarDock.Children.Add(templSep);
            sidebarDock.Children.Add(applyAllFooter);
            sidebarDock.Children.Add(tradeScroll);

            _fTradesSidebar = new Border { BorderThickness = new Thickness(0) };
            _fTradesSidebar.Child = sidebarDock;
            Grid.SetColumn(_fTradesSidebar, 0);
            root.Children.Add(_fTradesSidebar);

            FRefreshTradesSidebar();

            return root;
        }

        // ── Trades sidebar ────────────────────────────────────────────────────────
        private void FRefreshTradesSidebar()
        {
            if (_fTradeListPanel == null) return;
            _fTradeListPanel.Children.Clear();

            var trades = _filterTrades ?? new List<FilterTradeConfig>();
            if (trades.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text         = LemoineStrings.T("globalSettings.filters.sidebar.noTrades"),
                    Margin       = new Thickness(12, 12, 12, 0),
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                };
                empty.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                empty.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                empty.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                _fTradeListPanel.Children.Add(empty);
                AppendAddTradePill();
                return;
            }

            // Drag-to-reorder (whole row is the handle, like the legend rule rows). The
            // working copy is reordered in place; the window persists it on apply/close.
            if (_fTradeReorder == null)
                _fTradeReorder = new LemoineListReorder(_fTradeListPanel, (from, to) =>
                {
                    LemoineListReorder.Move(_filterTrades!, from, to);
                    FRefreshTradesSidebar();
                });

            for (int i = 0; i < trades.Count; i++)
            {
                var rowEl = BuildTradeRow(trades[i]);
                _fTradeListPanel.Children.Add(rowEl);
                if (rowEl is FrameworkElement fe) _fTradeReorder.Arm(fe, i);
            }

            AppendAddTradePill();
        }

        // "＋ Add Trade" affordance that floats as the last item in the trade list.
        // The pill doubles as the anchor for the add-trade popup form.
        // Split [Discover | ＋ Add Trade] double button (floats as the last item in the trade list).
        // Left half opens the existing Discover Rules window; right half is the inline add-trade form
        // and doubles as the popup anchor.
        private void AppendAddTradePill()
        {
            if (_fTradeListPanel == null) return;

            var outer = new Border
            {
                Margin          = new Thickness(4, 6, 4, 6),
                BorderThickness = new Thickness(1),
                Background      = Brushes.Transparent,
            };
            outer.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Card");
            outer.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // CornerRadius 10 matches LemoineRadius_Card (the outer pill radius); partial corners
            // can't be set via SetResourceReference, so the per-half rounding is a literal here.
            Border MakeHalf(string text, int col, CornerRadius corner, bool accent, Action onClick)
            {
                var lbl = new TextBlock
                {
                    Text                = text,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    IsHitTestVisible    = false,
                };
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, accent ? "LemoineAccent" : "LemoineTextDim");

                var half = new Border
                {
                    Padding      = new Thickness(6, 7, 6, 7),
                    CornerRadius = corner,
                    Cursor       = Cursors.Hand,
                    Background   = Brushes.Transparent,
                    Child        = lbl,
                };
                half.MouseEnter += (s, e) =>
                {
                    half.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
                    lbl.SetResourceReference(TextBlock.ForegroundProperty, accent ? "LemoineAccent" : "LemoineText");
                };
                half.MouseLeave += (s, e) =>
                {
                    half.Background = Brushes.Transparent;
                    lbl.SetResourceReference(TextBlock.ForegroundProperty, accent ? "LemoineAccent" : "LemoineTextDim");
                };
                half.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
                Grid.SetColumn(half, col);
                return half;
            }

            var discoverHalf = MakeHalf(LemoineStrings.T("globalSettings.filters.sidebar.discoverPill"), 0, new CornerRadius(10, 0, 0, 10), accent: true, LaunchDiscover);
            discoverHalf.ToolTip = LemoineStrings.T("globalSettings.filters.sidebar.discoverTooltip");

            var divider = new Border { Width = 1 };
            divider.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
            Grid.SetColumn(divider, 1);

            Border addHalf = null!;
            addHalf = MakeHalf(LemoineStrings.T("globalSettings.filters.sidebar.addTradePill"), 2, new CornerRadius(0, 10, 10, 0), accent: false,
                () => ShowAddTradeForm(addHalf));
            addHalf.ToolTip = LemoineStrings.T("globalSettings.filters.sidebar.addTradeTooltip");

            grid.Children.Add(discoverHalf);
            grid.Children.Add(divider);
            grid.Children.Add(addHalf);

            outer.Child = grid;
            _fAddTradeAnchor = addHalf; // the add-trade popup anchors to its half
            _fTradeListPanel.Children.Add(outer);
        }

        private UIElement BuildTradeRow(FilterTradeConfig trade)
        {
            bool isActive = trade.Id == _fActiveTradeId;

            // Active tab: rounded on the left only, no right border, -1px right margin overlaps
            // the rule list's 1px left border to create a visual connection between tab and content.
            var rowBorder = new Border
            {
                CornerRadius    = isActive ? new CornerRadius(10, 0, 0, 10) : new CornerRadius(10),
                BorderThickness = isActive ? new Thickness(1, 1, 0, 1) : new Thickness(1),
                Margin          = isActive ? new Thickness(4, 1, -1, 1) : new Thickness(4, 1, 4, 1),
                Padding         = new Thickness(8, 6, 8, 6),
                Cursor          = isActive ? Cursors.Arrow : Cursors.Hand,
            };
            rowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            if (isActive)
                rowBorder.SetResourceReference(Border.BackgroundProperty, "LemoineBg");
            else
                rowBorder.Background = Brushes.Transparent;

            if (!isActive)
            {
                rowBorder.MouseEnter += (s, e) =>
                    rowBorder.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
                rowBorder.MouseLeave += (s, e) =>
                {
                    rowBorder.Background = Brushes.Transparent;
                    rowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                };
                string tid = trade.Id;
                rowBorder.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    _fActiveTradeId = tid;
                    var next = _filterTrades?.FirstOrDefault(x => x.Id == tid);
                    _fActiveRuleId  = next?.Rules.FirstOrDefault()?.Id;
                    FRefreshTradesSidebar();
                    FRefreshRuleList();
                    FRefreshRuleEditor();
                };
            }

            // Row: [Auto checkbox | Auto swatch | * label | Auto edit]
            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Quick apply-selection checkbox (drives "Apply selected trades to view").
            var applyCb = BuildTradeApplyCheckbox(trade);
            Grid.SetColumn(applyCb, 0);
            rowGrid.Children.Add(applyCb);

            var swatch = new Border
            {
                Width             = 10, Height = 10,
                BorderThickness   = new Thickness(1),
                CornerRadius      = new CornerRadius(2),
                Margin            = new Thickness(0, 0, 8, 0),
                Background        = BrushFromHex(trade.Color),
                VerticalAlignment = VerticalAlignment.Center,
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            Grid.SetColumn(swatch, 1);
            rowGrid.Children.Add(swatch);

            var labelTb = new TextBlock
            {
                Text              = trade.Label,
                FontWeight        = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            labelTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            labelTb.SetResourceReference(TextBlock.ForegroundProperty, isActive ? "LemoineAccent" : "LemoineText");
            labelTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            Grid.SetColumn(labelTb, 2);
            rowGrid.Children.Add(labelTb);

            string editId = trade.Id;
            var editBtn = BuildSidebarActionBtn("✎", "LemoineUiFont");
            editBtn.ToolTip = LemoineStrings.T("globalSettings.filters.sidebar.editTradeTooltip");
            editBtn.Margin  = new Thickness(4, 0, 0, 0);
            editBtn.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                var t = _filterTrades?.FirstOrDefault(x => x.Id == editId);
                if (t != null) ShowTradeEditPopup(t, editBtn);
            };
            Grid.SetColumn(editBtn, 3);
            rowGrid.Children.Add(editBtn);

            rowBorder.Child = rowGrid;
            return rowBorder;
        }

        // Docked footer bar carrying a full-width primary "apply to view" button.
        private static Border BuildApplyFooter(string label, string tooltip, Action onClick)
        {
            var bar = new Border
            {
                Padding         = new Thickness(8),
                BorderThickness = new Thickness(0, 1, 0, 0),
            };
            bar.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var btn = LemoineControlStyles.BuildButton(
                label, LemoineControlStyles.LemoineButtonVariant.Primary);
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            btn.ToolTip             = tooltip;
            btn.Click += (s, e) => onClick();

            bar.Child = btn;
            return bar;
        }

        // Docked footer carrying a segmented [Apply | Remove] double button. Both halves act on the
        // checked-trade selection. Apply is the accent-filled primary; Remove is the neutral
        // secondary (non-destructive — filters stay in the project). CornerRadius 6 matches the
        // single-button footer; partial corners can't be set via SetResourceReference.
        private static Border BuildApplyRemoveFooter(
            string applyLabel, string applyTip, Action applyAction,
            string removeLabel, string removeTip, Action removeAction)
        {
            var bar = new Border { Padding = new Thickness(8), BorderThickness = new Thickness(0, 1, 0, 0) };
            bar.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var outer = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
            outer.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border MakeSeg(string label, string tip, int col, CornerRadius corner, bool primary, Action onClick)
            {
                var lbl = new TextBlock
                {
                    Text                = label,
                    TextAlignment       = TextAlignment.Center,
                    TextWrapping        = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    IsHitTestVisible    = false,
                };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, primary ? "LemoineAccent" : "LemoineTextDim");

                var seg = new Border
                {
                    Padding      = new Thickness(6, 7, 6, 7),
                    CornerRadius = corner,
                    Cursor       = Cursors.Hand,
                    ToolTip      = tip,
                    Child        = lbl,
                };
                if (primary) seg.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
                else         seg.Background = Brushes.Transparent;

                seg.MouseEnter += (s, e) =>
                {
                    if (!primary) seg.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
                    lbl.SetResourceReference(TextBlock.ForegroundProperty, primary ? "LemoineAccent" : "LemoineText");
                };
                seg.MouseLeave += (s, e) =>
                {
                    if (primary) seg.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
                    else         seg.Background = Brushes.Transparent;
                    lbl.SetResourceReference(TextBlock.ForegroundProperty, primary ? "LemoineAccent" : "LemoineTextDim");
                };
                seg.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
                Grid.SetColumn(seg, col);
                return seg;
            }

            grid.Children.Add(MakeSeg(applyLabel, applyTip, 0, new CornerRadius(6, 0, 0, 6), true, applyAction));

            var dv = new Border { Width = 1 };
            dv.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
            Grid.SetColumn(dv, 1);
            grid.Children.Add(dv);

            grid.Children.Add(MakeSeg(removeLabel, removeTip, 2, new CornerRadius(0, 6, 6, 0), false, removeAction));

            outer.Child = grid;
            bar.Child   = outer;
            return bar;
        }

        // Per-trade checkbox driving the "Apply selected trades to view" footer. Selection is
        // tracked as an EXCLUSION set (_fApplyExcludedTradeIds), so a trade is checked by default.
        // The checkbox consumes its own click so it never also selects the trade for editing.
        private UIElement BuildTradeApplyCheckbox(FilterTradeConfig trade)
        {
            string tid = trade.Id;

            var box = new Border
            {
                Width             = 15,
                Height            = 15,
                CornerRadius      = new CornerRadius(3),
                BorderThickness   = new Thickness(1.5),
                Margin            = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor            = Cursors.Hand,
                ToolTip           = LemoineStrings.T("globalSettings.filters.sidebar.applyCheckboxTooltip"),
            };

            var check = new TextBlock
            {
                Text                = char.ConvertFromUtf32(0x2713), // ✓
                FontWeight          = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                IsHitTestVisible    = false,
            };
            check.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
            check.SetResourceReference(TextBlock.ForegroundProperty, "LemoineKnobOn");
            box.Child = check;

            void Paint()
            {
                if (!_fApplyExcludedTradeIds.Contains(tid))
                {
                    box.SetResourceReference(Border.BackgroundProperty,  "LemoineAccent");
                    box.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                    check.Opacity = 1.0;
                }
                else
                {
                    box.Background = Brushes.Transparent;
                    box.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                    check.Opacity = 0.0;
                }
            }
            Paint();

            box.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true; // never trigger row selection
                if (_fApplyExcludedTradeIds.Contains(tid)) _fApplyExcludedTradeIds.Remove(tid);
                else                                        _fApplyExcludedTradeIds.Add(tid);
                Paint();
            };

            return box;
        }

        private static Border BuildSidebarActionBtn(string glyph, string fontResourceKey)
        {
            var icon = new TextBlock
            {
                Text              = glyph,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible  = false,
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            icon.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            icon.SetResourceReference(TextBlock.FontFamilyProperty, fontResourceKey);

            var btn = new Border
            {
                Cursor              = Cursors.Hand,
                Padding             = new Thickness(5),
                BorderThickness     = new Thickness(1),
                CornerRadius        = new CornerRadius(3),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background          = Brushes.Transparent,
                Margin              = new Thickness(2, 0, 0, 0),
                Child               = icon,
            };
            btn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            btn.MouseEnter += (s, e) =>
            {
                btn.SetResourceReference(Border.BorderBrushProperty,    "LemoineAccent");
                icon.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            };
            btn.MouseLeave += (s, e) =>
            {
                btn.SetResourceReference(Border.BorderBrushProperty,    "LemoineBorder");
                icon.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            };
            return btn;
        }


        // ── In-place selection — swaps visual highlight without rebuilding the list ──
        // This keeps every rowBorder in the panel so drag-and-drop can always
        // find the source border via Children.IndexOf, and the border's ActualWidth
        // is always valid when the drag ghost snapshots it.
        private void SelectRuleInPlace(Border newRowBorder, string ruleId,
                                       TextBlock? newNameTb = null, Border? newColorDot = null)
        {
            // Remove highlight from the previously-active row
            if (_fActiveRowBorder != null && _fActiveRowBorder != newRowBorder)
            {
                _fActiveRowBorder.Background = Brushes.Transparent;
                _fActiveRowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            }
            // Apply highlight to the new row (gives it an opaque background so the
            // drag ghost bitmap is visible when snapshotted immediately after)
            newRowBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
            newRowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            _fActiveRowBorder = newRowBorder;
            _fActiveRuleId    = ruleId;
            if (newNameTb != null)   _fActiveNameTb   = newNameTb;
            if (newColorDot != null) _fActiveColorDot = newColorDot;
            FRefreshRuleEditor();
        }

        // ── Rule list (left panel) ────────────────────────────────────────────
        private void FRefreshRuleList()
        {
            if (_fRuleListPanel == null) return;
            // Never rebuild the list while a drag is in progress — any indirect call
            // (TextBox LostFocus → Commit, chip Changed, etc.) would detach
            // _dragSourceBorder from Children and break DragOver's IndexOf lookup.
            // The Drop handler clears _dragRuleId before calling us, so post-drop
            // rebuilds are unaffected.
            if (_dragRuleId != null) return;
            // Never rebuild the list re-entrantly while FRefreshRuleEditor is
            // constructing its controls — chip Changed events can fire during setup
            // and call back here, corrupting the panel mid-build.
            if (_isRefreshingEditor) return;
            _fActiveRowBorder = null;
            _fActiveNameTb    = null;
            _fActiveColorDot  = null;
            _fMultiSelectBorders.Clear(); // rows are being replaced; re-registered in BuildRuleListRow
            _fRuleListPanel.Children.Clear();

            var trade = _filterTrades?.FirstOrDefault(t => t.Id == _fActiveTradeId);
            if (trade == null)
            {
                var empty = new TextBlock
                {
                    Text = LemoineStrings.T("globalSettings.filters.ruleList.noTradesYet"),
                    Margin = new Thickness(14, 14, 14, 0),
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle = FontStyles.Italic,
                };
                empty.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                empty.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                empty.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                _fRuleListPanel.Children.Add(empty);
                return;
            }

            if (trade.Rules.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = LemoineStrings.T("globalSettings.filters.ruleList.noRulesYet"),
                    Margin = new Thickness(14, 14, 14, 0),
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle = FontStyles.Italic,
                };
                empty.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                empty.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                empty.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                _fRuleListPanel.Children.Add(empty);
                AppendAddRulePill();
                return;
            }

            foreach (var rule in trade.Rules)
                _fRuleListPanel.Children.Add(BuildRuleListRow(trade, rule));

            AppendAddRulePill();
        }

        // "＋ Add Rule" affordance that floats as the last item in the rule list.
        private void AppendAddRulePill()
        {
            if (_fRuleListPanel == null) return;
            var pill = LemoineControlStyles.BuildAddPill(LemoineStrings.T("globalSettings.filters.ruleList.addRulePill"), () =>
            {
                var trade = _filterTrades?.FirstOrDefault(t => t.Id == _fActiveTradeId);
                if (trade == null) return;
                var newRule = FilterRuleConfig.NewBlank();
                trade.Rules.Add(newRule);
                _fActiveRuleId = newRule.Id;
                FRefreshRuleList();
                FRefreshRuleEditor();
                Dispatcher.BeginInvoke(new Action(() => _fRuleScroll?.ScrollToBottom()),
                    DispatcherPriority.Background);
            });
            _fRuleListPanel.Children.Add(pill);
        }

        private UIElement BuildRuleListRow(FilterTradeConfig trade, FilterRuleConfig rule)
        {
            bool isActive        = rule.Id == _fActiveRuleId;
            bool isMultiSelected = !isActive && _fSelectedRuleIds.Contains(rule.Id);

            var rowBorder = new Border
            {
                Padding         = new Thickness(10, 8, 8, 8),
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(0, 0, 0, 4),
                AllowDrop       = true,
                Opacity         = rule.Enabled ? 1.0 : 0.55,
                Cursor          = Cursors.Hand,
                Tag             = rule.Id,   // used by Drop handler to read visual order
            };
            rowBorder.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
            if (isActive)
            {
                rowBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                rowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                _fActiveRowBorder = rowBorder;
            }
            else if (isMultiSelected)
            {
                rowBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                rowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                _fMultiSelectBorders[rule.Id] = rowBorder;
            }
            else
            {
                rowBorder.Background = Brushes.Transparent;
                rowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            }

            // ── Hover ─────────────────────────────────────────────────────────
            rowBorder.MouseEnter += (s, e) =>
            {
                if (_dragRuleId == null && rule.Id != _fActiveRuleId && !_fSelectedRuleIds.Contains(rule.Id))
                {
                    rowBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                    rowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                }
            };
            rowBorder.MouseLeave += (s, e) =>
            {
                if (rule.Id != _fActiveRuleId)
                {
                    if (_fSelectedRuleIds.Contains(rule.Id))
                    {
                        rowBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                        rowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                    }
                    else
                    {
                        rowBorder.Background = Brushes.Transparent;
                        rowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                    }
                }
            };

            // ── Drag-and-drop reorder (drop target) ───────────────────────────
            rowBorder.DragOver += (s, e) =>
            {
                if (!(e.Data.GetData(DataFormats.StringFormat) is string d) || !d.StartsWith("RULE:")) return;
                e.Effects = DragDropEffects.Move;
                e.Handled = true;

                if (_dragSourceBorder == null || _fRuleListPanel == null) return;

                // Live snap: move the source pill to the hovered position
                var children = _fRuleListPanel.Children;
                int srcIdx = children.IndexOf(_dragSourceBorder);
                int dstIdx = children.IndexOf(rowBorder);
                if (srcIdx < 0 || dstIdx < 0 || srcIdx == dstIdx) return;

                var pos = e.GetPosition(rowBorder);
                bool insertBefore = pos.Y < rowBorder.ActualHeight / 2.0;
                int insertIdx = insertBefore ? dstIdx : dstIdx + 1;
                if (srcIdx < insertIdx) insertIdx--; // removing src shifts later indices left
                if (insertIdx == srcIdx) return;

                children.RemoveAt(srcIdx);
                children.Insert(insertIdx, _dragSourceBorder);
            };
            rowBorder.DragLeave += (s, e) => { /* live snap provides visual feedback */ };
            rowBorder.Drop += (s, e) =>
            {
                e.Handled = true;
                if (!(e.Data.GetData(DataFormats.StringFormat) is string srcData) || !srcData.StartsWith("RULE:")) return;

                // Read the current visual order from the panel and commit to data model
                if (_fRuleListPanel != null)
                {
                    var newOrderIds = _fRuleListPanel.Children
                        .OfType<FrameworkElement>()
                        .Select(el => el.Tag as string)
                        .Where(id => id != null)
                        .ToList();

                    var reordered = newOrderIds
                        .Select(id => trade.Rules.FirstOrDefault(r => r.Id == id))
                        .Where(r => r != null)
                        .ToList();

                    if (reordered.Count == trade.Rules.Count)
                    {
                        trade.Rules.Clear();
                        foreach (var r in reordered) trade.Rules.Add(r!);
                    }
                }

                _dragSourceBorder = null;
                _dragRuleId       = null;
                FRefreshRuleList();
            };

            // ── Row content ───────────────────────────────────────────────────
            var outerRow = new Grid { AllowDrop = true };
            outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // dot
            outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // name+sub (auto — only as wide as text)
            outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // spacer
            outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // edit

            // Color swatch — square, height driven by the name+subtext stack
            var colorDot = new Border
            {
                Background        = BrushFromHex(rule.SurfColor ?? trade.Color),
                BorderThickness   = new Thickness(1.5),
                CornerRadius      = new CornerRadius(3),
                Margin            = new Thickness(4, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            colorDot.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            // Keep square: width tracks actual height after layout
            colorDot.SizeChanged += (s, e) =>
            {
                double h = e.NewSize.Height;
                if (h > 0 && colorDot.Width != h) colorDot.Width = h;
            };
            if (isActive) _fActiveColorDot = colorDot; // editor swatch live-updates this
            Grid.SetColumn(colorDot, 0);
            outerRow.Children.Add(colorDot);

            // Subtext: category display names (or "(all categories)" if none set),
            // suffixed with "· whole category" when the rule matches everything.
            string BuildSubtext()
            {
                string cats;
                if (rule.BuiltInCategories.Count > 0)
                {
                    var names = rule.BuiltInCategories
                        .Select(ost => AutoFiltersSettings.KnownCategoryMap
                            .FirstOrDefault(kv => kv.Value == ost).Key ?? ost)
                        .Where(n => !string.IsNullOrEmpty(n));
                    cats = string.Join(", ", names);
                }
                else cats = LemoineStrings.T("globalSettings.filters.ruleList.allCategories");

                return string.Equals(rule.MatchType, "all", StringComparison.OrdinalIgnoreCase)
                    ? cats + LemoineStrings.T("globalSettings.filters.ruleList.wholeCategorySuffix")
                    : cats;
            }
            var subtextTb = new TextBlock
            {
                Text              = BuildSubtext(),
                TextTrimming      = TextTrimming.CharacterEllipsis,
                Margin            = new Thickness(0, 2, 0, 0),
                IsHitTestVisible  = false,
            };
            subtextTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
            subtextTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            subtextTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            // Rule name — display-only label; renaming happens in the rule edit popup
            // (ShowRuleEditPopup, opened from the row ✎) so the list row stays stable.
            var nameTb = new TextBlock
            {
                Text              = rule.Name,
                FontWeight        = FontWeights.SemiBold,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            nameTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            nameTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            nameTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            if (isActive) _fActiveNameTb = nameTb; // editor header uses this to update label in-place

            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(nameTb);
            nameStack.Children.Add(subtextTb);
            Grid.SetColumn(nameStack, 1);
            outerRow.Children.Add(nameStack);

            // Edit (✎) — opens the rule edit popup (name + Move/Copy + Delete). The eye toggle
            // and inline trash were removed in Stage 2: enable/disable is the editor's single
            // "Apply" toggle (rule.Enabled), and delete now lives inside the edit popup.
            var editBtn = BuildSidebarActionBtn("✎", "LemoineUiFont");
            editBtn.ToolTip = LemoineStrings.T("globalSettings.filters.ruleList.editRuleTooltip");
            editBtn.Margin  = new Thickness(4, 0, 0, 0);
            editBtn.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                ShowRuleEditPopup(trade, rule, editBtn);
            };
            Grid.SetColumn(editBtn, 3);
            outerRow.Children.Add(editBtn);

            rowBorder.Child = outerRow;

            // ── Whole-pill drag — initiates reorder from anywhere on the pill ──
            // PreviewMouseMove (tunneling) fires on rowBorder as the event passes
            // DOWN through it toward the target child, so it fires even when the
            // pointer is over the edit (✎) button — before that child ever sees the
            // event. MouseMove (bubbling) doesn't reach rowBorder when a child marks
            // MouseLeftButtonDown as Handled.
            rowBorder.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _dragRuleId != null) return;
                if (_dragReadyBorder != rowBorder) return; // only drag when this press armed it

                // Only start drag once the pointer has moved beyond the system drag threshold
                // so that single clicks on toggle/pencil/trash still fire normally.
                var pos = e.GetPosition(rowBorder);
                if (Math.Abs(pos.X - _dragGhostClickOffset.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - _dragGhostClickOffset.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;

                // Consume the preview event so toggle/pencil/trash don't also react
                e.Handled = true;

                _dragRuleId        = rule.Id;
                _dragSourceBorder  = rowBorder;
                _dragSourceOrigIdx = _fRuleListPanel?.Children.IndexOf(rowBorder) ?? -1;

                // Abort if the border somehow isn't in the panel — avoids a broken drag
                // where DragOver can never find srcIdx and silently fails to reorder.
                if (_dragSourceOrigIdx < 0)
                {
                    _dragRuleId       = null;
                    _dragSourceBorder = null;
                    FRefreshRuleList();
                    return;
                }

                // Window-space adorner ghost (no Popup → no off-screen nudge / right-side drift),
                // anchored at the grab point. The ghost paints a themed backing behind the snapshot,
                // so a transparent-background inactive row still reads as a solid card.
                _ruleGhost.Begin(rowBorder, _dragGhostClickOffset);
                rowBorder.Opacity = 0;
                // IsHitTestVisible intentionally left true so the invisible (Opacity=0)
                // source pill can still receive Drop when the user releases in the gap.

                QueryContinueDragEventHandler ghostHandler = (fs, fe) => _ruleGhost.Update();
                rowBorder.QueryContinueDrag += ghostHandler;
                DragDrop.DoDragDrop(rowBorder,
                    new DataObject(DataFormats.StringFormat, "RULE:" + rule.Id),
                    DragDropEffects.Move);
                rowBorder.QueryContinueDrag -= ghostHandler;
                _ruleGhost.End();
                _dragReadyBorder = null;

                if (_dragSourceBorder != null) // Drop never fired — drag was cancelled
                {
                    // Restore pill to its original panel position
                    if (_fRuleListPanel != null)
                    {
                        int curIdx = _fRuleListPanel.Children.IndexOf(rowBorder);
                        if (curIdx >= 0 && curIdx != _dragSourceOrigIdx)
                        {
                            _fRuleListPanel.Children.RemoveAt(curIdx);
                            int restoreIdx = Math.Min(_dragSourceOrigIdx, _fRuleListPanel.Children.Count);
                            _fRuleListPanel.Children.Insert(restoreIdx, rowBorder);
                        }
                    }
                    rowBorder.Opacity          = rule.Enabled ? 1.0 : 0.55;
                    rowBorder.IsHitTestVisible = true;
                    _dragSourceBorder          = null;
                }
                _dragRuleId = null;
            };

            // ── Row click — select rule (Shift/Ctrl for range/toggle multi-select) ──
            rowBorder.PreviewMouseLeftButtonDown += (s, e) =>
            {
                _dragReadyBorder = null; // reset — buttons and modifier-clicks must never arm drag

                if (e.OriginalSource is FrameworkElement src)
                {
                    var hitEl = src;
                    while (hitEl != null && hitEl != rowBorder)
                    {
                        if (hitEl == editBtn)
                            return; // button hit — _dragReadyBorder stays null
                        hitEl = VisualTreeHelper.GetParent(hitEl) as FrameworkElement;
                    }
                }
                _dragGhostClickOffset = e.GetPosition(rowBorder);

                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    e.Handled = true;
                    string anchorId    = _fShiftAnchorRuleId ?? _fActiveRuleId ?? rule.Id;
                    int    anchorIdx   = trade.Rules.FindIndex(r => r.Id == anchorId);
                    int    clickedIdx  = trade.Rules.FindIndex(r => r.Id == rule.Id);
                    if (anchorIdx < 0) anchorIdx = clickedIdx;
                    int lo = Math.Min(anchorIdx, clickedIdx);
                    int hi = Math.Max(anchorIdx, clickedIdx);

                    ClearMultiSelection();
                    for (int i = lo; i <= hi; i++)
                        _fSelectedRuleIds.Add(trade.Rules[i].Id);

                    _fActiveRuleId = anchorId; // editor sources from anchor
                    FRefreshRuleList();
                    FRefreshRuleEditor();
                }
                else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    e.Handled = true; // suppress drag initiation on Ctrl+click
                    bool wasInBatch = _fSelectedRuleIds.Count >= 2;

                    if (_fSelectedRuleIds.Contains(rule.Id))
                    {
                        _fSelectedRuleIds.Remove(rule.Id);
                        _fMultiSelectBorders.Remove(rule.Id);
                        if (rule.Id != _fActiveRuleId)
                        {
                            rowBorder.Background = Brushes.Transparent;
                            rowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                        }
                    }
                    else
                    {
                        if (_fActiveRuleId != null) _fSelectedRuleIds.Add(_fActiveRuleId);
                        _fSelectedRuleIds.Add(rule.Id);
                        if (rule.Id != _fActiveRuleId)
                        {
                            _fMultiSelectBorders[rule.Id] = rowBorder;
                            rowBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                            rowBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                        }
                    }

                    bool isInBatch = _fSelectedRuleIds.Count >= 2;
                    if (wasInBatch && !isInBatch)
                        ClearMultiSelection();
                    FRefreshRuleEditor();
                }
                else
                {
                    _fShiftAnchorRuleId = rule.Id; // anchor updates on plain click
                    bool wasAlreadyActive = rule.Id == _fActiveRuleId || _fSelectedRuleIds.Contains(rule.Id);
                    if (_fSelectedRuleIds.Count > 0) ClearMultiSelection();
                    if (rule.Id != _fActiveRuleId)
                        SelectRuleInPlace(rowBorder, rule.Id, nameTb, colorDot);
                    // Only arm drag if the rule was already selected before this click;
                    // the first click selects, a subsequent press can drag.
                    if (wasAlreadyActive)
                        _dragReadyBorder = rowBorder;
                }
            };

            return rowBorder;
        }

        // ── Rule editor (right panel) ─────────────────────────────────────────
        private void FRefreshRuleEditor()
        {
            if (_fEditorBorder == null) return;

            _isRefreshingEditor = true;
            try
            {
                var trade = _filterTrades?.FirstOrDefault(t => t.Id == _fActiveTradeId);
                var rule  = trade?.Rules.FirstOrDefault(r => r.Id == _fActiveRuleId);

                if (trade == null || rule == null)
                {
                    var ph = new TextBlock
                    {
                        Text         = LemoineStrings.T("globalSettings.filters.editor.selectRulePrompt"),
                        Margin       = new Thickness(14, 20, 14, 0),
                        TextWrapping = TextWrapping.Wrap,
                        FontStyle    = FontStyles.Italic,
                    };
                    ph.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    ph.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    ph.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    _fEditorBorder.Child = ph;
                    return;
                }

                if (_fSelectedRuleIds.Count >= 2)
                {
                    _fEditorBorder.Child = BuildBatchRuleEditor(trade, rule);
                    return;
                }

                // Editor starts at FILTER LOGIC — the rule NAME card was removed in Stage 2
                // (renaming now happens in the rule edit popup opened from the row's ✎).
                var scrollContent = new StackPanel();
                scrollContent.Children.Add(BuildRuleEditor(trade, rule));

                var scroll = new ScrollViewer
                {
                    VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Padding = new Thickness(0, 0, 4, 0),
                };
                scroll.Content = scrollContent;
                LemoineControlStyles.WireBubblingScroll(scroll);

                _fEditorBorder.Child = scroll;
            }
            finally
            {
                _isRefreshingEditor = false;
            }
        }

        private UIElement BuildRuleEditor(FilterTradeConfig trade, FilterRuleConfig rule)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            // ── Filter Logic ──────────────────────────────────────────────────
            panel.Children.Add(BuildFilterLogicSection(trade, rule));

            // ── Override Style ────────────────────────────────────────────────
            panel.Children.Add(BuildOverrideStyleSection(rule));

            // ── Appearance & Visibility ───────────────────────────────────────
            panel.Children.Add(BuildAppearanceSection(rule));

            return panel;
        }

        // ── Filter Logic section ──────────────────────────────────────────────
        private UIElement BuildFilterLogicSection(FilterTradeConfig trade, FilterRuleConfig rule,
                                                   Action<string>? markDirty = null)
        {
            var outer = new StackPanel { Margin = new Thickness(0) };

            // Section label
            var sectionLbl = new Border
            {
                Padding         = new Thickness(12, 8, 12, 4),
                BorderThickness = new Thickness(0, 0, 0, 0),
            };
            var sectionTb = new TextBlock { Text = LemoineStrings.T("globalSettings.filters.sections.filterLogic") };
            sectionTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
            sectionTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            sectionTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            sectionLbl.Child = sectionTb;
            outer.Children.Add(sectionLbl);

            // Card
            var card = new Border
            {
                Margin          = new Thickness(10, 0, 10, 10),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10), // rounder card (matches LemoineRadius_Card)
                Padding         = new Thickness(10, 8, 10, 8),
            };
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var cardStack = new StackPanel();

            // ── CATEGORY row ──────────────────────────────────────────────────
            // Convert OST_ strings → display names for chip display
            var catDisplayNames = new ObservableCollection<string>(
                rule.BuiltInCategories
                    .Select(ost => AutoFiltersSettings.KnownCategoryMap
                        .FirstOrDefault(kv => kv.Value == ost).Key ?? ost)
                    .Where(n => !string.IsNullOrEmpty(n)));

            // ── PARAMETER row — declared first so catChip's lambda can capture it ──
            var paramOptions = AutoFiltersSettings.GetParametersFor(
                rule.BuiltInCategories.FirstOrDefault() ?? "");
            var paramSelected = new ObservableCollection<string>();
            if (!string.IsNullOrEmpty(rule.Parameter))
                paramSelected.Add(rule.Parameter);

            LemoineTagChipInput? paramChip = null;

            // ── CATEGORY row ─────────────────────────────────────────────────
            var catChip = new LemoineTagChipInput
            {
                ItemsSource   = AutoFiltersSettings.KnownCategoryDisplayNames,
                SelectedItems = catDisplayNames,
                Placeholder   = LemoineStrings.T("globalSettings.filters.filterLogic.categoryPlaceholder"),
                // Nest related sub-categories under their parent behind an expand caret.
                Hierarchy     = AutoFiltersSettings.CategorySubcategories,
            };
            catChip.Changed += (s, e) =>
            {
                markDirty?.Invoke("logic.categories");
                rule.BuiltInCategories = catDisplayNames
                    .Select(name =>
                        AutoFiltersSettings.KnownCategoryMap.TryGetValue(name, out var ost) ? ost : name)
                    .ToList();
                RefreshParameterChip(rule, paramChip!);
            };

            paramChip = new LemoineTagChipInput
            {
                ItemsSource   = paramOptions,
                SelectedItems = paramSelected,
                MaxItems      = 1,
                Placeholder   = LemoineStrings.T("globalSettings.filters.filterLogic.parameterPlaceholder"),
            };
            // Link-safety hint shown under the parameter row (see UpdateParamHint).
            var paramHint = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 3, 0, 0),
            };
            paramHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
            paramHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            void UpdateParamHint()
            {
                // Whole-category always matches linked elements (keys off the Category param).
                if (string.Equals(rule.MatchType, "all", StringComparison.OrdinalIgnoreCase))
                {
                    paramHint.Text = LemoineStrings.T("globalSettings.filters.filterLogic.hintWholeCategory");
                    paramHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    return;
                }
                string p = rule.Parameter ?? "";
                if (string.IsNullOrEmpty(p)) { paramHint.Text = ""; return; }
                if (AutoFiltersSettings.IsLinkSafeParameter(p))
                {
                    paramHint.Text = LemoineStrings.T("globalSettings.filters.filterLogic.hintLinkSafe");
                    paramHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                }
                else
                {
                    paramHint.Text = LemoineStrings.T("globalSettings.filters.filterLogic.hintNotLinkSafe");
                    paramHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineWarnText");
                }
            }

            paramChip.Changed += (s, e) =>
            {
                markDirty?.Invoke("logic.parameter");
                rule.Parameter = paramSelected.FirstOrDefault() ?? "";
                UpdateParamHint();
            };

            // ── SEARCH STRING row ─────────────────────────────────────────────
            var valSelected = new ObservableCollection<string>(rule.Match);
            var valChip = new LemoineTagChipInput
            {
                ItemsSource   = Array.Empty<string>(),   // free-text only
                SelectedItems = valSelected,
                AllowFreeText = true,
                Placeholder   = LemoineStrings.T("globalSettings.filters.filterLogic.keywordPlaceholder"),
            };
            valChip.Changed += (s, e) =>
            {
                markDirty?.Invoke("logic.match");
                rule.Match = valSelected.ToList();
                FRefreshRuleList();
            };

            // Match type dropdown (CONTAINS / EQUALS / ALL / etc.)
            var matchTypes = new[]
            {
                "contains", "does not contain",
                "equals", "does not equal",
                "begins with", "ends with",
                "has a value", "has no value",
            };
            var matchDd = new ComboBox
            {
                IsEditable          = true, IsReadOnly = true,
                IsTextSearchEnabled = false,
                Margin              = new Thickness(0, 4, 0, 0),
                MaxDropDownHeight   = 160,
            };
            foreach (var mt in matchTypes) matchDd.Items.Add(mt);
            string curMatch = rule.MatchType ?? "contains";
            matchDd.SelectedItem = matchDd.Items.Contains(curMatch) ? curMatch : "contains";
            matchDd.SetResourceReference(ComboBox.BackgroundProperty,  "LemoineSelectBg");
            matchDd.SetResourceReference(ComboBox.ForegroundProperty,  "LemoineText");
            matchDd.SetResourceReference(ComboBox.FontSizeProperty,    "LemoineFS_SM");
            matchDd.SetResourceReference(ComboBox.FontFamilyProperty,  "LemoineMonoFont");
            matchDd.SetResourceReference(ComboBox.BorderBrushProperty, "LemoineBorder");
            matchDd.SelectionChanged += (s, e) =>
            {
                if (matchDd.SelectedItem is string sel)
                {
                    markDirty?.Invoke("logic.matchtype");
                    rule.MatchType = sel;
                }
            };

            StackPanel AddRow(string label, UIElement ctrl, UIElement? extra = null)
            {
                var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 2) };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

                var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                wrap.Children.Add(lbl);
                wrap.Children.Add(ctrl);
                if (extra != null) wrap.Children.Add(extra);
                cardStack.Children.Add(wrap);
                return wrap;
            }

            AddRow(LemoineStrings.T("globalSettings.filters.filterLogic.categoryLabel"), catChip);

            // ── WHOLE-CATEGORY toggle ─────────────────────────────────────────
            // Makes it unambiguous when a rule selects EVERY element in the chosen
            // categories (MatchType == "all"). When on, the parameter and search-string
            // rows are disabled because they no longer affect the result.
            StackPanel? paramRow  = null;
            StackPanel? searchRow = null;

            void ApplyWholeCategory(bool on)
            {
                if (paramRow != null)  { paramRow.IsEnabled  = !on; paramRow.Opacity  = on ? 0.4 : 1.0; }
                if (searchRow != null) { searchRow.IsEnabled = !on; searchRow.Opacity = on ? 0.4 : 1.0; }
            }

            bool wholeCatOn = string.Equals(rule.MatchType, "all", StringComparison.OrdinalIgnoreCase);
            string lastKeywordMatch = wholeCatOn ? "contains" : (rule.MatchType ?? "contains");

            var wholeCatToggle = BuildWholeCategoryToggle(wholeCatOn, on =>
            {
                if (on)
                {
                    if (!string.Equals(rule.MatchType, "all", StringComparison.OrdinalIgnoreCase))
                        lastKeywordMatch = rule.MatchType ?? "contains";
                    rule.MatchType = "all";
                }
                else
                {
                    rule.MatchType = lastKeywordMatch;
                    matchDd.SelectedItem = matchDd.Items.Contains(lastKeywordMatch)
                        ? lastKeywordMatch : "contains";
                }
                markDirty?.Invoke("logic.matchtype");
                ApplyWholeCategory(on);
                UpdateParamHint();
                FRefreshRuleList();
            });
            cardStack.Children.Add(wholeCatToggle);

            paramRow  = AddRow(LemoineStrings.T("globalSettings.filters.filterLogic.parameterLabel"), paramChip, paramHint);
            searchRow = AddRow(LemoineStrings.T("globalSettings.filters.filterLogic.searchStringLabel"), valChip, matchDd);
            ApplyWholeCategory(wholeCatOn);
            UpdateParamHint();

            card.Child = cardStack;
            outer.Children.Add(card);
            return outer;
        }

        private void RefreshParameterChip(FilterRuleConfig rule, LemoineTagChipInput chip)
        {
            var firstOst  = rule.BuiltInCategories.FirstOrDefault() ?? "";
            chip.ItemsSource = AutoFiltersSettings.GetParametersFor(firstOst);
        }

        // ── "Whole category" toggle ───────────────────────────────────────────
        // A labelled pill that flips a rule into match-everything mode. Styled to
        // match the appearance-section toggles (MakeAppToggle).
        private UIElement BuildWholeCategoryToggle(bool initial, Action<bool> onChange)
        {
            bool on = initial;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 66 });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var toggleBtn = new Border
            {
                CornerRadius      = new CornerRadius(4),
                BorderThickness   = new Thickness(1),
                Padding           = new Thickness(8, 3, 8, 3),
                Margin            = new Thickness(0, 0, 8, 0),
                Cursor            = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip           = LemoineStrings.T("globalSettings.filters.filterLogic.wholeCategoryToggleTooltip"),
            };
            var toggleTb = new TextBlock
            {
                Text                = LemoineStrings.T("globalSettings.filters.filterLogic.wholeCategoryToggleLabel"),
                TextAlignment       = TextAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            toggleTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            toggleTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            toggleBtn.Child = toggleTb;

            void ApplyState(bool isOn)
            {
                if (isOn)
                {
                    toggleBtn.SetResourceReference(Border.BackgroundProperty,  "LemoineAccent");
                    toggleBtn.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                    toggleTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineKnobOn");
                }
                else
                {
                    toggleBtn.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                    toggleBtn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                    toggleTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                }
            }
            ApplyState(on);

            toggleBtn.MouseLeftButtonUp += (s, e) =>
            {
                on = !on;
                ApplyState(on);
                onChange(on);
                e.Handled = true;
            };

            Grid.SetColumn(toggleBtn, 0);
            row.Children.Add(toggleBtn);

            var descTb = new TextBlock
            {
                Text              = LemoineStrings.T("globalSettings.filters.filterLogic.wholeCategoryDesc"),
                TextWrapping      = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            descTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            descTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            descTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            Grid.SetColumn(descTb, 1);
            row.Children.Add(descTb);

            return row;
        }

        // ── Override Style section ────────────────────────────────────────────
        private UIElement BuildOverrideStyleSection(FilterRuleConfig rule, Action<string>? markDirty = null)
        {
            var outer = new StackPanel();

            var sectionLbl = new Border { Padding = new Thickness(12, 8, 12, 4) };
            var sectionTb  = new TextBlock { Text = LemoineStrings.T("globalSettings.filters.sections.overrideStyle") };
            sectionTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
            sectionTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            sectionTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            sectionLbl.Child = sectionTb;
            outer.Children.Add(sectionLbl);

            var card = new Border
            {
                Margin          = new Thickness(10, 0, 10, 10),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10), // rounder card (matches LemoineRadius_Card)
                Padding         = new Thickness(10, 8, 10, 8),
            };
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var cardStack = new StackPanel();

            // COLORS sub-label
            var colorsLbl = new TextBlock { Text = LemoineStrings.T("globalSettings.filters.sections.colors"), Margin = new Thickness(0, 0, 0, 8) };
            colorsLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
            colorsLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            colorsLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            cardStack.Children.Add(colorsLbl);

            string[] fillList = FillPatternNames.Count > 0
                ? FillPatternNames.ToArray()
                : new[] { "Solid Fill" };
            string[] lineList = LinePatternNames.Count > 0
                ? LinePatternNames.ToArray()
                : AutoFiltersSettings.KnownLinePatterns;

            // ── Single shared Grid — columns align across all three rows ──────
            // Col 0: toggle btn (fixed min-width)
            // Col 1: FG|BG layer switch (Surface/Cut rows only; empty for Lines)
            // Col 2: color swatch (auto)
            // Col 3: pattern dropdown (star — fills remaining space)
            // Col 4: weight (auto — only Lines row populates this)
            var colorGrid = new Grid();
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 66 });
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            colorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Surface
            colorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Cut
            colorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Lines

            // Adds a single color-override row directly into the shared Grid.
            // All interactive elements share LemoineH_Input height for consistent alignment.
            // When background accessors (getBgEnabled…) are supplied, an FG|BG segmented
            // switch is shown that swaps which layer the toggle/swatch/pattern edit.
            // onFgColorChanged fires only for foreground colour edits (used to live-update
            // the rule-list swatch).
            void AddColorRow(int rowIdx, string label, string fieldPrefix,
                Func<bool>   getEnabled, Action<bool>   setEnabled,
                Func<string> getColor,   Action<string> setColor,
                string[]     patterns,
                Func<string> getPattern, Action<string> setPattern,
                bool         showWeight = false,
                Func<int>?   getWeight  = null,
                Action<int>? setWeight  = null,
                Func<bool>?   getBgEnabled = null, Action<bool>?   setBgEnabled = null,
                Func<string>? getBgColor   = null, Action<string>? setBgColor   = null,
                Func<string>? getBgPattern = null, Action<string>? setBgPattern = null,
                Action<string>? onFgColorChanged = null)
            {
                bool hasBg     = getBgEnabled != null;
                bool editingBg = false;

                // ── Active-layer accessors (foreground unless the BG layer is selected) ──
                bool   CurEnabled()            => editingBg ? getBgEnabled!()  : getEnabled();
                void   CurSetEnabled(bool v)   { if (editingBg) setBgEnabled!(v); else setEnabled(v); }
                string CurColor()              => editingBg ? getBgColor!()    : getColor();
                void   CurSetColor(string h)   { if (editingBg) setBgColor!(h); else setColor(h); }
                string CurPattern()            => editingBg ? getBgPattern!()  : getPattern();
                void   CurSetPattern(string p) { if (editingBg) setBgPattern!(p); else setPattern(p); }

                // ── Toggle button (height pinned to LemoineH_Input) ───────────
                var toggleBtn = new Border
                {
                    CornerRadius      = new CornerRadius(4),
                    BorderThickness   = new Thickness(1),
                    Padding           = new Thickness(8, 0, 8, 0),
                    Margin            = new Thickness(0, 0, 8, rowIdx < 2 ? 6 : 0),
                    Cursor            = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                toggleBtn.SetResourceReference(FrameworkElement.HeightProperty, "LemoineH_Input");
                var toggleTb = new TextBlock
                {
                    Text                = label,
                    TextAlignment       = TextAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                toggleTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                toggleTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                toggleBtn.Child = toggleTb;
                Grid.SetRow(toggleBtn, rowIdx);
                Grid.SetColumn(toggleBtn, 0);
                colorGrid.Children.Add(toggleBtn);

                // ── Color swatch (height pinned to LemoineH_Input, square) ───
                var swatchBorder = new Border
                {
                    Width             = 26,
                    BorderThickness   = new Thickness(1),
                    CornerRadius      = new CornerRadius(3),
                    Cursor            = Cursors.Hand,
                    Margin            = new Thickness(0, 0, 6, rowIdx < 2 ? 6 : 0),
                    Background        = BrushFromHex(CurColor()),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                swatchBorder.SetResourceReference(FrameworkElement.HeightProperty, "LemoineH_Input");
                swatchBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                swatchBorder.MouseLeftButtonUp += (s, e) =>
                {
                    if (!CurEnabled()) return;
                    var win     = Window.GetWindow(swatchBorder);
                    var initial = HexToMediaColor(CurColor());
                    var picked  = LemoineColorPickerWindow.PickColor(win, initial);
                    if (picked.HasValue)
                    {
                        string hex = $"#{picked.Value.R:X2}{picked.Value.G:X2}{picked.Value.B:X2}";
                        markDirty?.Invoke($"{fieldPrefix}.color");
                        CurSetColor(hex);
                        swatchBorder.Background = BrushFromHex(hex);
                        if (!editingBg) onFgColorChanged?.Invoke(hex);
                    }
                };
                Grid.SetRow(swatchBorder, rowIdx);
                Grid.SetColumn(swatchBorder, 2);
                colorGrid.Children.Add(swatchBorder);

                // ── Pattern dropdown ──────────────────────────────────────────
                // Surface/Cut: span cols 3+4 so right edge aligns with Lines stepper
                Action<string> setPatternWithDirty = p => { CurSetPattern(p); markDirty?.Invoke($"{fieldPrefix}.pattern"); };
                var patternDd = BuildAutoCompleteBox(patterns, CurPattern(), setPatternWithDirty, double.NaN);
                patternDd.Margin            = new Thickness(0, 0, showWeight ? 4 : 0, rowIdx < 2 ? 6 : 0);
                patternDd.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(patternDd, rowIdx);
                Grid.SetColumn(patternDd, 3);
                if (!showWeight) Grid.SetColumnSpan(patternDd, 2);
                colorGrid.Children.Add(patternDd);

                // ── Integrated stepper for weight (Lines row only, 1–14) ──────
                FrameworkElement? weightContainer = null;
                if (showWeight && getWeight != null && setWeight != null)
                {
                    var stepper = new LemoineInlineStepper
                    {
                        Value             = getWeight(),
                        MinValue          = 1,
                        MaxValue          = 14,
                        Step              = 1,
                        Decimals          = 0,
                        ValueWidth        = 32,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    stepper.ValueChanged += (s2, v) => setWeight((int)v);
                    Grid.SetRow(stepper, rowIdx);
                    Grid.SetColumn(stepper, 4);
                    colorGrid.Children.Add(stepper);
                    weightContainer = stepper;
                }

                // ── Apply enabled state visuals ───────────────────────────────
                void ApplyState(bool on)
                {
                    if (on)
                    {
                        toggleBtn.SetResourceReference(Border.BackgroundProperty,  "LemoineAccent");
                        toggleBtn.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                        toggleTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineKnobOn");
                    }
                    else
                    {
                        toggleBtn.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                        toggleBtn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                        toggleTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    }
                    swatchBorder.Opacity          = on ? 1.0 : 0.35;
                    swatchBorder.IsHitTestVisible = on;
                    patternDd.Opacity   = on ? 1.0 : 0.35;
                    patternDd.IsEnabled = on;
                    if (weightContainer != null)
                    {
                        weightContainer.Opacity   = on ? 1.0 : 0.35;
                        weightContainer.IsEnabled = on;
                    }
                }
                ApplyState(CurEnabled());

                toggleBtn.MouseLeftButtonUp += (s, e) =>
                {
                    bool next = !CurEnabled();
                    markDirty?.Invoke($"{fieldPrefix}.enabled");
                    CurSetEnabled(next);
                    ApplyState(next);
                    e.Handled = true;
                };

                // ── FG|BG layer switch (Surface & Cut only) ───────────────────
                if (hasBg)
                {
                    Border fgSeg = null!, bgSeg = null!;

                    void StyleSeg(Border seg, bool active)
                    {
                        var tb = (TextBlock)seg.Child;
                        if (active)
                        {
                            seg.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
                            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineKnobOn");
                        }
                        else
                        {
                            seg.Background = Brushes.Transparent;
                            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                        }
                    }

                    Border MakeSeg(string text, bool isBg, CornerRadius radius)
                    {
                        var tb = new TextBlock
                        {
                            Text                = text,
                            VerticalAlignment   = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            IsHitTestVisible    = false,
                        };
                        tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
                        tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                        var seg = new Border
                        {
                            CornerRadius = radius,
                            Padding      = new Thickness(7, 0, 7, 0),
                            Cursor       = Cursors.Hand,
                            Background   = Brushes.Transparent,
                            Child        = tb,
                        };
                        seg.MouseLeftButtonUp += (s, e) =>
                        {
                            e.Handled = true;
                            if (editingBg == isBg) return;
                            editingBg = isBg;
                            StyleSeg(fgSeg, !editingBg);
                            StyleSeg(bgSeg, editingBg);
                            // Re-bind swatch / pattern / toggle to the now-active layer.
                            swatchBorder.Background = BrushFromHex(CurColor());
                            patternDd.Text          = CurPattern();
                            ApplyState(CurEnabled());
                        };
                        return seg;
                    }

                    fgSeg = MakeSeg(LemoineStrings.T("globalSettings.filters.overrideStyle.fg"), false, new CornerRadius(4, 0, 0, 4));
                    bgSeg = MakeSeg(LemoineStrings.T("globalSettings.filters.overrideStyle.bg"), true,  new CornerRadius(0, 4, 4, 0));
                    StyleSeg(fgSeg, true);
                    StyleSeg(bgSeg, false);

                    var segStack = new StackPanel { Orientation = Orientation.Horizontal };
                    segStack.Children.Add(fgSeg);
                    segStack.Children.Add(bgSeg);

                    var segWrap = new Border
                    {
                        BorderThickness   = new Thickness(1),
                        CornerRadius      = new CornerRadius(4),
                        Margin            = new Thickness(0, 0, 8, rowIdx < 2 ? 6 : 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child             = segStack,
                    };
                    segWrap.SetResourceReference(FrameworkElement.HeightProperty, "LemoineH_Input");
                    segWrap.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                    Grid.SetRow(segWrap, rowIdx);
                    Grid.SetColumn(segWrap, 1);
                    colorGrid.Children.Add(segWrap);
                }
            }

            AddColorRow(0, LemoineStrings.T("globalSettings.filters.overrideStyle.surface"), "style.surf",
                () => rule.OverrideSurf, v => rule.OverrideSurf = v,
                () => rule.SurfColor,    h => rule.SurfColor    = h,
                fillList, () => rule.SurfPattern ?? "", p => rule.SurfPattern = p,
                getBgEnabled: () => rule.OverrideSurfBg, setBgEnabled: v => rule.OverrideSurfBg = v,
                getBgColor:   () => rule.SurfBgColor,    setBgColor:   h => rule.SurfBgColor    = h,
                getBgPattern: () => rule.SurfBgPattern ?? "", setBgPattern: p => rule.SurfBgPattern = p,
                onFgColorChanged: hex => { if (_fActiveColorDot != null) _fActiveColorDot.Background = BrushFromHex(hex); });
            AddColorRow(1, LemoineStrings.T("globalSettings.filters.overrideStyle.cut"), "style.cut",
                () => rule.OverrideCut,  v => rule.OverrideCut  = v,
                () => rule.CutColor,     h => rule.CutColor     = h,
                fillList, () => rule.CutPattern ?? "", p => rule.CutPattern = p,
                getBgEnabled: () => rule.OverrideCutBg, setBgEnabled: v => rule.OverrideCutBg = v,
                getBgColor:   () => rule.CutBgColor,    setBgColor:   h => rule.CutBgColor    = h,
                getBgPattern: () => rule.CutBgPattern ?? "", setBgPattern: p => rule.CutBgPattern = p);
            AddColorRow(2, LemoineStrings.T("globalSettings.filters.overrideStyle.lines"), "style.line",
                () => rule.OverrideLine, v => rule.OverrideLine = v,
                () => rule.LineColor,    h => rule.LineColor    = h,
                lineList, () => rule.LinePattern ?? "Solid", p => rule.LinePattern = p,
                showWeight: true, getWeight: () => rule.LineWeight,
                setWeight: w => { markDirty?.Invoke("style.line.weight"); rule.LineWeight = w; });

            cardStack.Children.Add(colorGrid);

            // Transparency lives at the bottom of the colour-override card (moved out of
            // Appearance & Visibility).
            cardStack.Children.Add(BuildTransparencyControl(rule, markDirty));

            card.Child = cardStack;
            outer.Children.Add(card);
            return outer;
        }

        // Transparency slider + live % badge. Bound to rule.Transparency; the
        // "appearance.transparency" dirty key keeps batch-edit propagation working.
        private UIElement BuildTransparencyControl(FilterRuleConfig rule, Action<string>? markDirty)
        {
            var container = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

            var transpLbl = MiniLabel(LemoineStrings.T("globalSettings.filters.transparency.label"));
            transpLbl.Margin = new Thickness(0, 2, 0, 4);
            container.Children.Add(transpLbl);

            var transpRow = new Grid();
            transpRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            transpRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var transpSlider = new Slider
            {
                Minimum = 0, Maximum = 100, Value = rule.Transparency,
                SmallChange = 5, LargeChange = 10,
                VerticalAlignment = VerticalAlignment.Center,
            };
            transpSlider.SetResourceReference(Slider.ForegroundProperty, "LemoineAccent");
            Grid.SetColumn(transpSlider, 0);
            transpRow.Children.Add(transpSlider);

            var transpBadge = new Border
            {
                CornerRadius      = new CornerRadius(10),
                Padding           = new Thickness(6, 2, 6, 2),
                Margin            = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            transpBadge.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
            var transpVal = new TextBlock { Text = rule.Transparency + "%" };
            transpVal.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
            transpVal.SetResourceReference(TextBlock.ForegroundProperty, "LemoineKnobOn");
            transpVal.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            transpBadge.Child = transpVal;
            Grid.SetColumn(transpBadge, 1);
            transpRow.Children.Add(transpBadge);

            transpSlider.ValueChanged += (s, e) =>
            {
                markDirty?.Invoke("appearance.transparency");
                rule.Transparency = (int)transpSlider.Value;
                transpVal.Text    = rule.Transparency + "%";
            };
            container.Children.Add(transpRow);
            return container;
        }

        // ── Appearance & Visibility section ───────────────────────────────────
        private UIElement BuildAppearanceSection(FilterRuleConfig rule, Action<string>? markDirty = null)
        {
            var outer = new StackPanel();

            var sectionLbl = new Border { Padding = new Thickness(12, 4, 12, 4) };
            var sectionTb  = new TextBlock { Text = LemoineStrings.T("globalSettings.filters.sections.appearanceVisibility") };
            sectionTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
            sectionTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            sectionTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            sectionLbl.Child = sectionTb;
            outer.Children.Add(sectionLbl);

            var card = new Border
            {
                Margin          = new Thickness(10, 0, 10, 10),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10), // rounder card (matches LemoineRadius_Card)
                Padding         = new Thickness(10, 10, 10, 10),
            };
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var cardStack = new StackPanel();

            // ── Reusable toggle row — same visual style as Override Style toggles ──
            // Returns a row with a labelled toggle button matching MakeColorRow's style.
            UIElement MakeAppToggle(string label, string? tooltip, bool initial, Action<bool> onChange)
            {
                bool on = initial;

                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 66 }); // toggle (matches Override)
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // description

                var toggleBtn = new Border
                {
                    CornerRadius      = new CornerRadius(4),
                    BorderThickness   = new Thickness(1),
                    Padding           = new Thickness(8, 3, 8, 3),
                    Margin            = new Thickness(0, 0, 8, 0),
                    Cursor            = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip           = tooltip,
                };
                var toggleTb = new TextBlock
                {
                    Text                = label,
                    TextAlignment       = TextAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                toggleTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                toggleTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                toggleBtn.Child = toggleTb;

                void ApplyState(bool isOn)
                {
                    if (isOn)
                    {
                        toggleBtn.SetResourceReference(Border.BackgroundProperty,  "LemoineAccent");
                        toggleBtn.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                        toggleTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineKnobOn");
                    }
                    else
                    {
                        toggleBtn.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                        toggleBtn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                        toggleTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    }
                }
                ApplyState(on);

                toggleBtn.MouseLeftButtonUp += (s, e) =>
                {
                    on = !on;
                    ApplyState(on);
                    onChange(on);
                    e.Handled = true;
                };

                Grid.SetColumn(toggleBtn, 0);
                row.Children.Add(toggleBtn);

                // Descriptive label to the right of the toggle
                var descTb = new TextBlock
                {
                    Text              = tooltip ?? label,
                    TextWrapping      = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                descTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                descTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                descTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                Grid.SetColumn(descTb, 1);
                row.Children.Add(descTb);

                return row;
            }

            // Halftone toggle
            cardStack.Children.Add(MakeAppToggle(LemoineStrings.T("globalSettings.filters.appearance.halftone"), LemoineStrings.T("globalSettings.filters.appearance.halftone"), rule.Halftone,
                v => { markDirty?.Invoke("appearance.halftone"); rule.Halftone = v; }));

            // (Transparency moved to the bottom of the Override Style card.)

            // Elements visible toggle
            cardStack.Children.Add(MakeAppToggle(LemoineStrings.T("globalSettings.filters.appearance.visibleLabel"),
                LemoineStrings.T("globalSettings.filters.appearance.visibleTooltip"),
                rule.Visible, v => { markDirty?.Invoke("appearance.visible"); rule.Visible = v; }));

            // Apply toggle — the single enable concept (Stage 2 merged the old per-row "eye"
            // Enabled flag into this). When off, the rule is skipped entirely: no filter is
            // created or applied. Keeps FilterOn in sync so existing apply logic is unchanged.
            cardStack.Children.Add(MakeAppToggle(LemoineStrings.T("globalSettings.filters.appearance.applyLabel"),
                LemoineStrings.T("globalSettings.filters.appearance.applyTooltip"),
                rule.Enabled, v =>
                {
                    markDirty?.Invoke("appearance.apply");
                    rule.Enabled  = v;
                    rule.FilterOn = v;
                    if (_fActiveRowBorder != null && rule.Id == _fActiveRuleId)
                        _fActiveRowBorder.Opacity = v ? 1.0 : 0.55;
                }));

            card.Child = cardStack;
            outer.Children.Add(card);
            return outer;
        }

        // ── Rule edit popup (opens from the row ✎) ────────────────────────────
        // Mirrors ShowTradeEditPopup: a NAME field, a Move/Copy double button that pops the
        // destination-trade list, and a Delete. No Save — edits commit live / on window close.
        private void ShowRuleEditPopup(FilterTradeConfig trade, FilterRuleConfig rule, UIElement anchor)
        {
            var popup = new Popup
            {
                PlacementTarget    = anchor,
                Placement          = PlacementMode.Bottom,
                StaysOpen          = false,
                AllowsTransparency = true,
            };

            var outer = new Border
            {
                Width           = 248, Padding = new Thickness(14), BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6), Margin = new Thickness(0, 2, 0, 0),
            };
            outer.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");

            var panel = new StackPanel();
            panel.Children.Add(MiniLabel(LemoineStrings.T("globalSettings.filters.ruleEditPopup.nameLabel")));

            var nameBox = new TextBox
            {
                Text            = rule.Name,
                Padding         = new Thickness(6, 5, 6, 5),
                BorderThickness = new Thickness(1),
                AcceptsReturn   = false,
                TextWrapping    = TextWrapping.NoWrap,
                Margin          = new Thickness(0, 4, 0, 0),
            };
            nameBox.SetResourceReference(TextBox.BackgroundProperty,     "LemoineSelectBg");
            nameBox.SetResourceReference(TextBox.ForegroundProperty,     "LemoineText");
            nameBox.SetResourceReference(TextBox.BorderBrushProperty,    "LemoineBorder");
            nameBox.SetResourceReference(TextBox.CaretBrushProperty,     "LemoineText");
            nameBox.SetResourceReference(TextBox.SelectionBrushProperty, "LemoineAccentDim");
            nameBox.SetResourceReference(TextBox.FontSizeProperty,       "LemoineFS_MD");
            nameBox.SetResourceReference(TextBox.FontFamilyProperty,     "LemoineUiFont");
            nameBox.GotFocus  += (s, e2) => nameBox.SetResourceReference(TextBox.BorderBrushProperty, "LemoineAccent");
            nameBox.LostFocus += (s, e2) => nameBox.SetResourceReference(TextBox.BorderBrushProperty, "LemoineBorder");

            void CommitName()
            {
                string v = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(v)) { nameBox.Text = rule.Name; return; }
                if (v == rule.Name) return;
                rule.Name = v;
                // Live-update the active row's label in place; non-active rows are refreshed
                // when the popup closes (rebuilding the list now would detach this popup's anchor).
                if (_fActiveNameTb != null && rule.Id == _fActiveRuleId) _fActiveNameTb.Text = v;
            }
            nameBox.LostFocus += (s, e2) => CommitName();
            nameBox.KeyDown   += (s, e2) =>
            {
                if (e2.Key == Key.Enter)       { CommitName(); popup.IsOpen = false; e2.Handled = true; }
                else if (e2.Key == Key.Escape) { nameBox.Text = rule.Name; popup.IsOpen = false; e2.Handled = true; }
            };
            panel.Children.Add(nameBox);

            // Full-bleed separator
            var sep = new Border { Height = 1, Margin = new Thickness(-14, 13, -14, 13) };
            sep.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
            panel.Children.Add(sep);

            // Action row: [Move | Copy double button] … [Delete]
            var actionRow = new Grid();
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var moveCopy = BuildRuleMoveCopyDouble(trade, rule, () => popup.IsOpen = false);
            Grid.SetColumn(moveCopy, 0);
            actionRow.Children.Add(moveCopy);

            var trash = BuildTrashConfirmButton(LemoineStrings.T("globalSettings.filters.ruleEditPopup.deleteRule"), () =>
            {
                popup.IsOpen = false;
                if (_fSelectedRuleIds.Contains(rule.Id) && _fSelectedRuleIds.Count >= 2)
                {
                    var toDelete = _fSelectedRuleIds.ToList();
                    ClearMultiSelection();
                    foreach (var id in toDelete)
                        trade.Rules.RemoveAll(r => r.Id == id);
                    _fActiveRuleId = trade.Rules.FirstOrDefault()?.Id;
                }
                else
                {
                    trade.Rules.Remove(rule);
                    if (_fActiveRuleId == rule.Id)
                        _fActiveRuleId = trade.Rules.FirstOrDefault()?.Id;
                }
                FRefreshRuleList();
                FRefreshRuleEditor();
            });
            ((FrameworkElement)trash).Margin            = new Thickness(8, 0, 0, 0);
            ((FrameworkElement)trash).VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetColumn((UIElement)trash, 1);
            actionRow.Children.Add((UIElement)trash);

            panel.Children.Add(actionRow);

            // Refresh the list once on close so a renamed non-active row picks up its new label.
            popup.Closed += (s, e) => FRefreshRuleList();

            outer.Child  = panel;
            popup.Child  = outer;
            popup.IsOpen = true;

            nameBox.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => { nameBox.Focus(); nameBox.SelectAll(); }));
        }

        // Segmented [Move | Copy] double button. Either half pops the destination-trade list
        // (BuildTradeDestPopup) in the matching mode (no in-dropdown tabs), sized to the button;
        // picking a destination closes both this popup and the parent rule edit popup via onPicked.
        private UIElement BuildRuleMoveCopyDouble(FilterTradeConfig trade, FilterRuleConfig rule, Action onPicked)
        {
            List<FilterRuleConfig> RulesToMove() =>
                (_fSelectedRuleIds.Contains(rule.Id) && _fSelectedRuleIds.Count >= 2)
                    ? trade.Rules.Where(r => _fSelectedRuleIds.Contains(r.Id)).ToList()
                    : new List<FilterRuleConfig> { rule };

            var bar = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
            };
            bar.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border MakeSeg(string label, int col, bool copyMode)
            {
                var seg = new Border
                {
                    Padding    = new Thickness(0, 7, 0, 7),
                    Cursor     = Cursors.Hand,
                    Background = Brushes.Transparent, // full area hit-testable
                };
                seg.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
                var inner = new StackPanel
                {
                    Orientation         = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    IsHitTestVisible    = false,
                };
                var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                var caret = new TextBlock { Text = "˅", Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                caret.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
                caret.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                inner.Children.Add(lbl);
                inner.Children.Add(caret);
                seg.Child = inner;
                Grid.SetColumn(seg, col);

                seg.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    // Drop from the whole double button so the list aligns under it and shares its width.
                    double w = bar.ActualWidth > 0 ? bar.ActualWidth : 174;
                    var destPopup = BuildTradeDestPopup(trade, RulesToMove(), bar, w,
                        initialCopy: copyMode, onDone: onPicked);
                    destPopup.IsOpen = true;
                };
                return seg;
            }

            grid.Children.Add(MakeSeg(LemoineStrings.T("globalSettings.filters.ruleEditPopup.move"), 0, false));

            var divider = new Border { Width = 1 };
            divider.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
            Grid.SetColumn(divider, 1);
            grid.Children.Add(divider);

            grid.Children.Add(MakeSeg(LemoineStrings.T("globalSettings.filters.ruleEditPopup.copy"), 2, true));

            bar.Child = grid;
            return bar;
        }

        private Popup BuildTradeDestPopup(FilterTradeConfig srcTrade, List<FilterRuleConfig> rules,
                                          UIElement anchor, double width, bool initialCopy = false, Action? onDone = null)
        {
            var popup = new Popup
            {
                PlacementTarget    = anchor,
                Placement          = PlacementMode.Bottom,
                StaysOpen          = false,
                AllowsTransparency = true,
            };

            // Width matches the Move/Copy button it drops from. The mode (Move vs Copy) is chosen by
            // which half was clicked, so the old in-dropdown Move/Copy tabs are gone.
            var outer = new Border
            {
                Width           = width, Padding = new Thickness(0),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Margin          = new Thickness(0, 2, 0, 0),
            };
            outer.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            bool isCopyMode = initialCopy;

            // ── Trade list ─────────────────────────────────────────────────────
            var tradeListPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };

            void RebuildTradeList()
            {
                tradeListPanel.Children.Clear();
                foreach (var t in _filterTrades ?? new List<FilterTradeConfig>())
                {
                    bool same = t.Id == srcTrade.Id && !isCopyMode;
                    var row = new Border
                    {
                        Padding    = new Thickness(10, 7, 10, 7),
                        Background = Brushes.Transparent,
                        Cursor     = same ? Cursors.Arrow : Cursors.Hand,
                    };
                    var rc = new StackPanel { Orientation = Orientation.Horizontal };
                    var dot = new Ellipse { Width = 8, Height = 8, Fill = BrushFromHex(t.Color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                    var lbl = new TextBlock { Text = t.Label + (same ? LemoineStrings.T("globalSettings.filters.suffixes.current") : ""), Opacity = same ? 0.45 : 1.0 };
                    lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    rc.Children.Add(dot);
                    rc.Children.Add(lbl);
                    row.Child = rc;

                    if (!same)
                    {
                        row.MouseEnter += (s, e) => row.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
                        row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
                        string destId = t.Id;
                        row.MouseLeftButtonDown += (s, e) =>
                        {
                            popup.IsOpen = false;
                            if (!isCopyMode)
                            {
                                // Move all rules to dest trade
                                foreach (var r in rules) srcTrade.Rules.Remove(r);
                                var dest = _filterTrades?.FirstOrDefault(x => x.Id == destId);
                                if (dest != null) foreach (var r in rules) dest.Rules.Add(r);
                                ClearMultiSelection();
                                _fActiveTradeId = destId;
                                _fActiveRuleId  = rules[0].Id;
                                FRefreshTradesSidebar();
                                FRefreshRuleList();
                                FRefreshRuleEditor();
                            }
                            else
                            {
                                // Copy all rules to dest trade
                                var dest = _filterTrades?.FirstOrDefault(x => x.Id == destId);
                                if (dest != null)
                                {
                                    foreach (var rule in rules)
                                    {
                                        var clone = FilterRuleConfig.NewBlank();
                                        clone.Name = rule.Name + LemoineStrings.T("globalSettings.filters.suffixes.copy"); clone.Enabled = rule.Enabled;
                                        clone.CutColor = rule.CutColor; clone.SurfColor = rule.SurfColor;
                                        clone.LineColor = rule.LineColor; clone.LinePattern = rule.LinePattern;
                                        clone.LineWeight = rule.LineWeight; clone.Halftone = rule.Halftone;
                                        clone.Transparency = rule.Transparency; clone.Visible = rule.Visible;
                                        clone.FilterOn = rule.FilterOn; clone.Notes = rule.Notes;
                                        clone.Match = new List<string>(rule.Match);
                                        clone.BuiltInCategories = new List<string>(rule.BuiltInCategories);
                                        clone.Parameter = rule.Parameter;
                                        dest.Rules.Add(clone);
                                    }
                                }
                            }
                            e.Handled = true;
                            onDone?.Invoke();
                        };
                    }
                    tradeListPanel.Children.Add(row);
                }
            }

            // Mode is fixed by which half opened the dropdown — build the list once, no tabs.
            tradeListPanel.Margin = new Thickness(0, 4, 0, 4);
            RebuildTradeList();

            outer.Child  = tradeListPanel;
            popup.Child  = outer;
            return popup;
        }

        // ShowTradeManagementPopover removed — functionality merged into the combined
        // chevron dropdown — now lives in the trades sidebar.

        // ── Edit Trade popup (name + ID) ──────────────────────────────────────
        private void ShowTradeEditPopup(FilterTradeConfig trade, UIElement anchor)
        {
            var popup = new Popup
            {
                PlacementTarget    = anchor,
                Placement          = PlacementMode.Bottom,
                StaysOpen          = false,
                AllowsTransparency = true,
            };

            var outer = new Border
            {
                Width           = 230, Padding = new Thickness(14), BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6), Margin = new Thickness(0, 2, 0, 0),
            };
            outer.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var panel = new StackPanel();

            TextBox MakeEditBox(string value, bool mono = false)
            {
                var box = new TextBox
                {
                    Text            = value,
                    Padding         = new Thickness(6, 5, 6, 5),
                    BorderThickness = new Thickness(1),
                    AcceptsReturn   = false,
                    TextWrapping    = TextWrapping.NoWrap,
                    Margin          = new Thickness(0, 4, 0, 10),
                };
                box.SetResourceReference(TextBox.BackgroundProperty,     "LemoineSelectBg");
                box.SetResourceReference(TextBox.ForegroundProperty,     "LemoineText");
                box.SetResourceReference(TextBox.BorderBrushProperty,    "LemoineBorder");
                box.SetResourceReference(TextBox.CaretBrushProperty,     "LemoineText");
                box.SetResourceReference(TextBox.SelectionBrushProperty, "LemoineAccentDim");
                box.SetResourceReference(TextBox.FontSizeProperty,       "LemoineFS_MD");
                box.SetResourceReference(TextBox.FontFamilyProperty,     mono ? "LemoineMonoFont" : "LemoineUiFont");
                box.GotFocus  += (s, e2) => box.SetResourceReference(TextBox.BorderBrushProperty, "LemoineAccent");
                box.LostFocus += (s, e2) => box.SetResourceReference(TextBox.BorderBrushProperty, "LemoineBorder");
                return box;
            }

            panel.Children.Add(MiniLabel(LemoineStrings.T("globalSettings.filters.tradeEditPopup.nameLabel")));
            var labelBox = MakeEditBox(trade.Label);
            panel.Children.Add(labelBox);

            panel.Children.Add(MiniLabel(LemoineStrings.T("globalSettings.filters.common.tradeIdLabel")));
            var idBox = MakeEditBox(trade.Id, mono: true);
            idBox.MaxLength = 8;
            panel.Children.Add(idBox);

            // No Save button (Stage 2) — name/ID auto-commit on change. The sidebar refresh is
            // deferred to popup close so renaming doesn't rebuild (and detach) the popup anchor.
            void CommitTradeEdits()
            {
                string newLabel = labelBox.Text.Trim();
                if (!string.IsNullOrEmpty(newLabel)) trade.Label = newLabel;
                else                                 labelBox.Text = trade.Label;

                string newId = idBox.Text.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(newId)
                    || (newId != trade.Id && _filterTrades?.Any(t => t.Id == newId) == true))
                {
                    idBox.Text = trade.Id; // empty or duplicate — revert
                }
                else if (newId != trade.Id)
                {
                    trade.Id        = newId;
                    _fActiveTradeId = newId;
                }
            }
            labelBox.LostFocus += (s, e) => CommitTradeEdits();
            idBox.LostFocus    += (s, e) => CommitTradeEdits();
            void HandleTradeEditKey(KeyEventArgs e)
            {
                if (e.Key == Key.Enter)       { CommitTradeEdits(); popup.IsOpen = false; e.Handled = true; }
                else if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; }
            }
            labelBox.KeyDown += (s, e) => HandleTradeEditKey(e);
            idBox.KeyDown    += (s, e) => HandleTradeEditKey(e);
            popup.Closed     += (s, e) => FRefreshTradesSidebar();

            var actionSep = new Border { Height = 1, Margin = new Thickness(-14, 10, -14, 6) };
            actionSep.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
            panel.Children.Add(actionSep);

            var actionRow = new Grid();
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dupBtn = FlatSmBtn(LemoineStrings.T("globalSettings.filters.tradeEditPopup.duplicate"));
            dupBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(dupBtn, 0);
            dupBtn.Click += (s, e) =>
            {
                popup.IsOpen = false;
                var orig = _filterTrades?.FirstOrDefault(x => x.Id == trade.Id);
                if (orig == null) return;
                var copies = AutoFiltersSettings.DeepCopy(new List<FilterTradeConfig> { orig });
                var copy   = copies[0];
                copy.Id    = "T" + DateTime.Now.Ticks.ToString().Substring(11, 3);
                copy.Label = orig.Label + LemoineStrings.T("globalSettings.filters.suffixes.copy");
                int idx    = _filterTrades!.IndexOf(orig);
                _filterTrades.Insert(idx + 1, copy);
                _fActiveTradeId = copy.Id;
                _fActiveRuleId  = copy.Rules.FirstOrDefault()?.Id;
                FRefreshTradesSidebar();
                FRefreshRuleList();
                FRefreshRuleEditor();
            };
            actionRow.Children.Add(dupBtn);

            string delId = trade.Id;
            var delBtn = BuildTrashConfirmButton(LemoineStrings.T("globalSettings.filters.tradeEditPopup.deleteTrade"), () =>
            {
                _filterTrades?.RemoveAll(x => x.Id == delId);
                if (_fActiveTradeId == delId)
                {
                    _fActiveTradeId = _filterTrades?.FirstOrDefault()?.Id;
                    _fActiveRuleId  = null;
                }
                FRefreshTradesSidebar();
                FRefreshRuleList();
                FRefreshRuleEditor();
            });
            ((FrameworkElement)delBtn).Margin            = new Thickness(6, 0, 0, 0);
            ((FrameworkElement)delBtn).VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn((UIElement)delBtn, 1);
            actionRow.Children.Add((UIElement)delBtn);

            panel.Children.Add(actionRow);

            outer.Child  = panel;
            popup.Child  = outer;
            popup.IsOpen = true;

            // Focus label box after popup opens
            labelBox.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => { labelBox.Focus(); labelBox.SelectAll(); }));
        }

        // ── Add Trade form ────────────────────────────────────────────────────
        private void ShowAddTradeForm(UIElement anchor)
        {
            var popup = new Popup
            {
                PlacementTarget = anchor, Placement = PlacementMode.Bottom,
                StaysOpen = false, AllowsTransparency = true,
            };

            var outer = new Border
            {
                MinWidth = 280, Padding = new Thickness(14), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 2, 0, 0),
            };
            outer.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var panel = new StackPanel();
            panel.Children.Add(MiniLabel(LemoineStrings.T("globalSettings.filters.common.tradeIdLabel")));
            var idBox = new TextBox
            {
                Text            = "",
                MaxLength       = 6,
                Margin          = new Thickness(0, 4, 0, 10),
                Padding         = new Thickness(6, 5, 6, 5),
                BorderThickness = new Thickness(1),
                AcceptsReturn   = false,
                TextWrapping    = TextWrapping.NoWrap,
            };
            idBox.SetResourceReference(TextBox.BackgroundProperty, "LemoineSelectBg");
            idBox.SetResourceReference(TextBox.ForegroundProperty, "LemoineText");
            idBox.SetResourceReference(TextBox.BorderBrushProperty,"LemoineBorder");
            idBox.SetResourceReference(TextBox.FontFamilyProperty, "LemoineMonoFont");
            idBox.SetResourceReference(TextBox.FontSizeProperty,   "LemoineFS_MD");
            panel.Children.Add(idBox);

            panel.Children.Add(MiniLabel(LemoineStrings.T("globalSettings.filters.addTradeForm.labelLabel")));
            var labelBox = new TextBox
            {
                Text            = "",
                Margin          = new Thickness(0, 4, 0, 10),
                Padding         = new Thickness(6, 5, 6, 5),
                BorderThickness = new Thickness(1),
                AcceptsReturn   = false,
                TextWrapping    = TextWrapping.NoWrap,
            };
            labelBox.SetResourceReference(TextBox.BackgroundProperty, "LemoineSelectBg");
            labelBox.SetResourceReference(TextBox.ForegroundProperty, "LemoineText");
            labelBox.SetResourceReference(TextBox.BorderBrushProperty,"LemoineBorder");
            labelBox.SetResourceReference(TextBox.FontFamilyProperty, "LemoineUiFont");
            labelBox.SetResourceReference(TextBox.FontSizeProperty,   "LemoineFS_MD");
            panel.Children.Add(labelBox);

            // ── Color row: swatch + hex label (click to expand inline picker) ──
            panel.Children.Add(MiniLabel(LemoineStrings.T("globalSettings.filters.addTradeForm.colorLabel")));
            string newTradeColor = "#569cd6";

            var swatchRow = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 4, 0, 6),
            };

            var swatch = new Border
            {
                Width               = 24,
                Height              = 24,
                CornerRadius        = new CornerRadius(3),
                BorderThickness     = new Thickness(1),
                Cursor              = Cursors.Hand,
                Margin              = new Thickness(0, 0, 8, 0),
                VerticalAlignment   = VerticalAlignment.Center,
                Background          = new SolidColorBrush(HexToMediaColor(newTradeColor)),
                SnapsToDevicePixels = true,
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var hexLbl = new TextBlock
            {
                Text              = newTradeColor,
                VerticalAlignment = VerticalAlignment.Center,
            };
            hexLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            hexLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            hexLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            swatchRow.Children.Add(swatch);
            swatchRow.Children.Add(hexLbl);
            panel.Children.Add(swatchRow);

            // ── Inline collapsible LemoineColorPickerPanel ────────────────────
            var pickerPanel = new LemoineColorPickerPanel
            {
                SelectedColor = HexToMediaColor(newTradeColor),
            };

            var pickerSep = new Border
            {
                Height = 1,
                Margin = new Thickness(-14, 4, -14, 10),
            };
            pickerSep.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");

            var applyColorBtn  = LemoineControlStyles.BuildButton(LemoineStrings.T("globalSettings.filters.addTradeForm.applyColor"),
                LemoineControlStyles.LemoineButtonVariant.Primary);
            var cancelColorBtn = LemoineControlStyles.BuildButton(LemoineStrings.T("globalSettings.filters.common.cancel"),
                LemoineControlStyles.LemoineButtonVariant.Ghost);
            cancelColorBtn.Margin = new Thickness(0, 0, 6, 0);

            var pickerBtnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(0, 8, 0, 0),
            };
            pickerBtnRow.Children.Add(cancelColorBtn);
            pickerBtnRow.Children.Add(applyColorBtn);

            var pickerSection = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                Margin     = new Thickness(0, 0, 0, 10),
            };
            pickerSection.Children.Add(pickerSep);
            pickerSection.Children.Add(pickerPanel);
            pickerSection.Children.Add(pickerBtnRow);
            panel.Children.Add(pickerSection);

            // Swatch click: toggle picker open/closed
            swatch.MouseLeftButtonUp += (s, e) =>
            {
                if (pickerSection.Visibility == Visibility.Visible)
                {
                    pickerSection.Visibility = Visibility.Collapsed;
                }
                else
                {
                    pickerPanel.SelectedColor = HexToMediaColor(newTradeColor);
                    pickerSection.Visibility  = Visibility.Visible;
                }
            };

            cancelColorBtn.Click += (s, e) =>
            {
                pickerSection.Visibility = Visibility.Collapsed;
            };

            applyColorBtn.Click += (s, e) =>
            {
                var c   = pickerPanel.SelectedColor;
                string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                newTradeColor      = hex;
                swatch.Background  = new SolidColorBrush(c);
                hexLbl.Text        = hex;
                pickerPanel.AddToRecent(c);
                pickerSection.Visibility = Visibility.Collapsed;
            };

            // ── Add Trade button ──────────────────────────────────────────────
            var addBtn = BuildFlatButton(LemoineStrings.T("globalSettings.filters.addTradeForm.addTrade"));
            addBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            addBtn.Click += (s, e) =>
            {
                string id    = idBox.Text.Trim().ToUpperInvariant();
                string label = labelBox.Text.Trim();
                string color = newTradeColor;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(label)) return;
                if (_filterTrades?.Any(t => t.Id == id) == true)
                { idBox.Text = id + "2"; return; }
                _filterTrades?.Add(new FilterTradeConfig { Id = id, Label = label, Color = color });
                _fActiveTradeId = id;
                _fActiveRuleId  = null;
                popup.IsOpen    = false;
                FRefreshTradesSidebar();
                FRefreshRuleList();
                FRefreshRuleEditor();
                Dispatcher.BeginInvoke(new Action(() => _fTradeScroll?.ScrollToBottom()),
                    DispatcherPriority.Background);
            };
            panel.Children.Add(addBtn);
            outer.Child = panel;
            popup.Child = outer;
            popup.IsOpen = true;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  TEMPLATES POPUP
        //  Replaces the old Import / Export / Restore Defaults toolbar buttons.
        //  Sections:
        //    1. Saved templates  (load / delete)
        //    2. Save current as template  (inline name input)
        //    3. Divider — File operations
        //    4. Import from file / Export to file
        //    5. Restore defaults
        // ═════════════════════════════════════════════════════════════════════

        private void ShowTemplatesPopup(UIElement anchor)
        {
            var store = AutoFiltersSettings.Templates;

            var popup = new Popup
            {
                PlacementTarget    = anchor,
                Placement          = PlacementMode.Bottom,
                StaysOpen          = false,
                AllowsTransparency = true,
                PopupAnimation     = PopupAnimation.Fade,
                MinWidth           = 270,
            };

            var outer = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Margin          = new Thickness(0, 4, 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 12, Opacity = 0.22, ShadowDepth = 4, Direction = 270,
                },
            };
            outer.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var root = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };

            // ── Helper: section header ─────────────────────────────────────────
            void AddSectionHeader(string text)
            {
                var tb = new TextBlock
                {
                    Text   = text,
                    Margin = new Thickness(12, 8, 12, 4),
                };
                tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
                tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                root.Children.Add(tb);
            }

            // ── Helper: horizontal separator ──────────────────────────────────
            void AddSep(double topMargin = 6, double botMargin = 6)
            {
                var sep = new Border { Height = 1, Margin = new Thickness(0, topMargin, 0, botMargin) };
                sep.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
                root.Children.Add(sep);
            }

            // ── Helper: menu row (chip pill button) ───────────────────────────
            Border AddMenuRow(string icon, string label, Action onClick,
                              bool destructive = false, bool disabled = false)
            {
                var row = new Border
                {
                    Padding         = new Thickness(10, 4, 12, 4),
                    CornerRadius    = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    Margin          = new Thickness(4, 2, 4, 2),
                    Cursor          = disabled ? Cursors.Arrow : Cursors.Hand,
                    Opacity         = disabled ? 0.45 : 1.0,
                    IsHitTestVisible = !disabled,
                };
                row.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                row.SetResourceReference(Border.BorderBrushProperty, destructive ? "LemoineRed" : "LemoineBorder");

                var iconTb = new TextBlock
                {
                    Text              = icon,
                    Width             = 18,
                    TextAlignment     = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible  = false,
                };
                iconTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                iconTb.SetResourceReference(TextBlock.ForegroundProperty, destructive ? "LemoineRed" : "LemoineTextDim");
                iconTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                var labelTb = new TextBlock
                {
                    Text              = label,
                    Margin            = new Thickness(7, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible  = false,
                };
                labelTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                labelTb.SetResourceReference(TextBlock.ForegroundProperty, destructive ? "LemoineRed" : "LemoineText");
                labelTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                var rowContent = new StackPanel { Orientation = Orientation.Horizontal };
                rowContent.Children.Add(iconTb);
                rowContent.Children.Add(labelTb);
                row.Child = rowContent;

                if (!disabled)
                {
                    row.MouseEnter += (s, e) =>
                    {
                        row.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                        row.SetResourceReference(Border.BorderBrushProperty, destructive ? "LemoineRed" : "LemoineAccent");
                    };
                    row.MouseLeave += (s, e) =>
                    {
                        row.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                        row.SetResourceReference(Border.BorderBrushProperty, destructive ? "LemoineRed" : "LemoineBorder");
                    };
                    row.MouseLeftButtonUp += (s, e) =>
                    {
                        e.Handled = true;
                        onClick();
                    };
                }
                root.Children.Add(row);
                return row;
            }

            // ── 1. Saved templates list ───────────────────────────────────────
            AddSectionHeader(LemoineStrings.T("globalSettings.filters.templatesPopup.savedTemplatesHeader"));

            var templates = store.List();
            var templateListPanel = new StackPanel();
            LemoineListReorder? templateReorder = null;

            void RebuildTemplateList()
            {
                templateListPanel.Children.Clear();
                var current = store.List();

                // Drag-to-reorder (whole pill is the handle); the new order is persisted to
                // the store's sidecar index so it survives restarts.
                if (templateReorder == null)
                    templateReorder = new LemoineListReorder(templateListPanel, (from, to) =>
                    {
                        var ordered = store.List();
                        LemoineListReorder.Move(ordered, from, to);
                        store.SaveOrder(ordered);
                        RebuildTemplateList();
                    });

                if (current.Count == 0)
                {
                    var empty = new TextBlock
                    {
                        Text         = LemoineStrings.T("globalSettings.filters.templatesPopup.noSavedTemplates"),
                        Margin       = new Thickness(12, 4, 12, 4),
                        FontStyle    = FontStyles.Italic,
                    };
                    empty.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
                    empty.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    empty.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                    templateListPanel.Children.Add(empty);
                    return;
                }

                int idx = 0;
                foreach (var tmpl in current)
                {
                    // Capture for lambda closure
                    var t = tmpl;

                    // Pill chip wrapper
                    var pillBorder = new Border
                    {
                        CornerRadius    = new CornerRadius(10),
                        BorderThickness = new Thickness(1),
                        Margin          = new Thickness(4, 2, 4, 2),
                        Cursor          = Cursors.Hand,
                        ClipToBounds    = true,
                    };
                    pillBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                    pillBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

                    pillBorder.MouseEnter += (s, e) =>
                    {
                        pillBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                        pillBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                    };
                    pillBorder.MouseLeave += (s, e) =>
                    {
                        pillBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                        pillBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                    };

                    var rowGrid = new Grid();
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    // Load area (name + date)
                    var loadBorder = new Border { Padding = new Thickness(10, 4, 6, 4) };
                    Grid.SetColumn(loadBorder, 0);

                    var nameStack = new StackPanel();
                    var nameTb = new TextBlock { Text = t.Name };
                    nameTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    nameTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    nameTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                    var dateTb = new TextBlock
                    {
                        Text   = t.Created.ToString("MMM d, yyyy"),
                        Margin = new Thickness(0, 1, 0, 0),
                    };
                    dateTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
                    dateTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    dateTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

                    nameStack.Children.Add(nameTb);
                    nameStack.Children.Add(dateTb);
                    loadBorder.Child = nameStack;

                    loadBorder.MouseLeftButtonUp += (s, e) =>
                    {
                        if (e.OriginalSource is FrameworkElement src)
                        {
                            var el = src;
                            while (el != null && el != rowGrid)
                            {
                                if (el.Tag as string == "deleteBtn") { e.Handled = true; return; }
                                el = VisualTreeHelper.GetParent(el) as FrameworkElement;
                            }
                        }
                        e.Handled = true;
                        popup.IsOpen = false;
                        if (store.Load(t, out var trades, out string? loadErr) && trades != null)
                        {
                            _filterTrades   = AutoFiltersSettings.DeepCopy(trades);
                            _fActiveTradeId = _filterTrades.FirstOrDefault()?.Id;
                            _fActiveRuleId  = _filterTrades.FirstOrDefault()?.Rules.FirstOrDefault()?.Id;
                            _contentBorder.Child = BuildFiltersContent();
                            FlashStatus(LemoineStrings.T("globalSettings.filters.status.loaded", t.Name));
                        }
                        else
                        {
                            MessageBox.Show(LemoineStrings.T("globalSettings.filters.dialogs.loadFailed", loadErr), LemoineStrings.T("globalSettings.filters.dialogs.templateError"),
                                MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    };

                    rowGrid.Children.Add(loadBorder);

                    // Delete button
                    var delBtn = BuildTrashConfirmButton(LemoineStrings.T("globalSettings.filters.templatesPopup.deleteTemplate"), () =>
                    {
                        store.Delete(t, out _);
                        RebuildTemplateList();
                    });
                    ((FrameworkElement)delBtn).Tag = "deleteBtn";
                    ((FrameworkElement)delBtn).Margin = new Thickness(0, 0, 6, 0);
                    ((FrameworkElement)delBtn).VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn((UIElement)delBtn, 1);
                    rowGrid.Children.Add((UIElement)delBtn);

                    pillBorder.Child = rowGrid;
                    templateListPanel.Children.Add(pillBorder);
                    templateReorder.Arm(pillBorder, idx);
                    idx++;
                }
            }

            RebuildTemplateList();
            root.Children.Add(templateListPanel);

            // ── 2. Save Current as Template ────────────────────────────────────
            AddSep();

            // The save row toggles between "button" and "inline name input" states.
            var saveRowHost = new Border { Margin = new Thickness(4, 0, 4, 0) };

            void ShowSaveInputState()
            {
                var inputRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(8, 4, 8, 4),
                };

                var nameBox = new TextBox
                {
                    Width               = 140,
                    Text                = "",
                    MaxLength           = 50,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                nameBox.SetResourceReference(TextBox.BackgroundProperty,  "LemoineSelectBg");
                nameBox.SetResourceReference(TextBox.ForegroundProperty,  "LemoineText");
                nameBox.SetResourceReference(TextBox.BorderBrushProperty, "LemoineBorder");
                nameBox.SetResourceReference(TextBox.FontSizeProperty,    "LemoineFS_SM");
                nameBox.SetResourceReference(TextBox.FontFamilyProperty,  "LemoineMonoFont");

                void DoSave()
                {
                    var name = nameBox.Text.Trim();
                    if (string.IsNullOrEmpty(name)) return;
                    var tradesToSave = _filterTrades ?? new List<FilterTradeConfig>();
                    if (store.Save(name, tradesToSave, out string? saveErr))
                    {
                        RebuildTemplateList();
                        ShowSaveButtonState(); // revert to button after save
                        FlashStatus(LemoineStrings.T("globalSettings.filters.status.savedTemplate", name));
                    }
                    else
                    {
                        MessageBox.Show(LemoineStrings.T("globalSettings.filters.dialogs.saveFailed", saveErr), LemoineStrings.T("globalSettings.filters.dialogs.templateError"),
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }

                nameBox.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Return) { e.Handled = true; DoSave(); }
                    if (e.Key == Key.Escape) { e.Handled = true; ShowSaveButtonState(); }
                };

                var confirmBtn = FlatSmBtn(LemoineStrings.T("globalSettings.filters.templatesPopup.save"));
                confirmBtn.Margin = new Thickness(6, 0, 0, 0);
                confirmBtn.Click += (s, e) => DoSave();

                var cancelBtn = FlatSmBtn("✕");
                cancelBtn.Margin = new Thickness(4, 0, 0, 0);
                cancelBtn.Click += (s, e) => ShowSaveButtonState();

                inputRow.Children.Add(nameBox);
                inputRow.Children.Add(confirmBtn);
                inputRow.Children.Add(cancelBtn);
                saveRowHost.Child = inputRow;

                // Focus the text box after the popup re-lays out
                nameBox.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Input,
                    new Action(() => nameBox.Focus()));
            }

            void ShowSaveButtonState()
            {
                var btn = new Border
                {
                    Padding         = new Thickness(10, 4, 12, 4),
                    CornerRadius    = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    Cursor          = Cursors.Hand,
                    Margin          = new Thickness(4, 2, 4, 2),
                };
                btn.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                btn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

                var icon = new TextBlock
                {
                    Text              = "＋",
                    Width             = 18,
                    TextAlignment     = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible  = false,
                };
                icon.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                icon.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                icon.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                var lbl = new TextBlock
                {
                    Text              = LemoineStrings.T("globalSettings.filters.templatesPopup.saveCurrentAsTemplate"),
                    Margin            = new Thickness(7, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible  = false,
                };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(icon);
                row.Children.Add(lbl);
                btn.Child = row;

                btn.MouseEnter += (s, e) =>
                {
                    btn.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                    btn.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                };
                btn.MouseLeave += (s, e) =>
                {
                    btn.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                    btn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                };
                btn.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    ShowSaveInputState();
                };

                saveRowHost.Child = btn;
            }

            ShowSaveButtonState();
            root.Children.Add(saveRowHost);

            // ── 3. File operations ────────────────────────────────────────────
            AddSep();
            AddSectionHeader(LemoineStrings.T("globalSettings.filters.templatesPopup.fileHeader"));

            AddMenuRow("↑", LemoineStrings.T("globalSettings.filters.templatesPopup.importFromFile"), () =>
            {
                popup.IsOpen = false;
                ImportFiltersFromFile();
            });

            AddMenuRow("↓", LemoineStrings.T("globalSettings.filters.templatesPopup.exportToFile"), () =>
            {
                popup.IsOpen = false;
                ExportFiltersToFile();
            });

            // ── 4. Restore Defaults ───────────────────────────────────────────
            AddSep();

            // Restore shows an inline confirmation instead of closing the popup.
            var restoreRow = new Border
            {
                Padding         = new Thickness(10, 4, 12, 4),
                CornerRadius    = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(4, 2, 4, 2),
                Cursor          = Cursors.Hand,
            };
            restoreRow.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            restoreRow.SetResourceReference(Border.BorderBrushProperty, "LemoineRed");
            var restoreRowContent = new StackPanel { Orientation = Orientation.Horizontal };
            var restoreIcon = new TextBlock
            {
                Text              = "↺",
                Width             = 18,
                TextAlignment     = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible  = false,
            };
            restoreIcon.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            restoreIcon.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            restoreIcon.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            var restoreLabel = new TextBlock
            {
                Text              = LemoineStrings.T("globalSettings.filters.templatesPopup.restoreDefaults"),
                Margin            = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible  = false,
            };
            restoreLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            restoreLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            restoreLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            restoreRowContent.Children.Add(restoreIcon);
            restoreRowContent.Children.Add(restoreLabel);
            restoreRow.Child = restoreRowContent;

            restoreRow.MouseEnter += (s, e) =>
            {
                restoreRow.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                restoreRow.SetResourceReference(Border.BorderBrushProperty, "LemoineRed");
            };
            restoreRow.MouseLeave += (s, e) =>
            {
                restoreRow.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                restoreRow.SetResourceReference(Border.BorderBrushProperty, "LemoineRed");
            };
            restoreRow.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                // Swap to inline confirmation
                var confirmHost = new StackPanel
                {
                    Margin      = new Thickness(12, 6, 12, 6),
                    Orientation = Orientation.Vertical,
                };
                var warnTb = new TextBlock
                {
                    Text         = LemoineStrings.T("globalSettings.filters.templatesPopup.restoreConfirm"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 0, 0, 8),
                };
                warnTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
                warnTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                warnTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

                var confirmBtn = FlatSmBtn(LemoineStrings.T("globalSettings.filters.templatesPopup.yesRestore"));
                confirmBtn.SetResourceReference(Button.ForegroundProperty,  "LemoineRed");
                confirmBtn.SetResourceReference(Button.BorderBrushProperty, "LemoineRed");
                confirmBtn.Click += (ss, ee) =>
                {
                    popup.IsOpen         = false;
                    _filterTrades        = AutoFiltersSettings.BuildDefaultTrades();
                    _fActiveTradeId      = _filterTrades.FirstOrDefault()?.Id;
                    _contentBorder.Child = BuildFiltersContent();
                    FlashStatus(LemoineStrings.T("globalSettings.filters.status.restoredDefaults"));
                };
                var cancelBtn = FlatSmBtn(LemoineStrings.T("globalSettings.filters.common.cancel"));
                cancelBtn.Margin = new Thickness(6, 0, 0, 0);
                cancelBtn.Click += (ss, ee) =>
                {
                    // Swap back to normal restore row
                    var restoreParent = restoreRow.Parent as Panel;
                    if (restoreParent != null)
                    {
                        int idx = restoreParent.Children.IndexOf(confirmHost as UIElement ??
                            restoreParent.Children[restoreParent.Children.Count - 1]);
                        // Re-add the original row
                        root.Children.Remove(confirmHost);
                        root.Children.Add(restoreRow);
                    }
                };

                var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
                btnRow.Children.Add(confirmBtn);
                btnRow.Children.Add(cancelBtn);
                confirmHost.Children.Add(warnTb);
                confirmHost.Children.Add(btnRow);

                root.Children.Remove(restoreRow);
                root.Children.Add(confirmHost);
            };

            root.Children.Add(restoreRow);

            // ── Assemble & open ───────────────────────────────────────────────
            outer.Child  = root;
            popup.Child  = outer;
            popup.IsOpen = true;
        }

        // ── File-level import / export (called from the Templates popup) ──────

        private void ImportFiltersFromFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title      = LemoineStrings.T("globalSettings.filters.dialogs.importTitle"),
                Filter     = LemoineStrings.T("globalSettings.filters.dialogs.xmlFilter"),
                DefaultExt = ".xml",
            };
            if (dlg.ShowDialog() != true) return;
            if (AutoFiltersSettings.TryImportFrom(dlg.FileName, out string? error))
            {
                _filterTrades        = AutoFiltersSettings.DeepCopy(AutoFiltersSettings.Instance.Trades);
                _fActiveTradeId      = _filterTrades.FirstOrDefault()?.Id;
                _fActiveRuleId       = _filterTrades.FirstOrDefault()?.Rules.FirstOrDefault()?.Id;
                _contentBorder.Child = BuildFiltersContent();
                FlashStatus(LemoineStrings.T("globalSettings.filters.status.imported"));
            }
            else
            {
                MessageBox.Show(LemoineStrings.T("globalSettings.filters.dialogs.importFailed", error), LemoineStrings.T("globalSettings.filters.dialogs.importError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportFiltersToFile()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title    = LemoineStrings.T("globalSettings.filters.dialogs.exportTitle"),
                Filter   = LemoineStrings.T("globalSettings.filters.dialogs.xmlFilter"),
                FileName = "LemoineAutoFilters.xml",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                AutoFiltersSettings.ExportTo(dlg.FileName, _filterTrades ?? new List<FilterTradeConfig>());
                FlashStatus(LemoineStrings.T("globalSettings.filters.status.exported"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(LemoineStrings.T("globalSettings.filters.dialogs.exportFailed", ex.Message), LemoineStrings.T("globalSettings.filters.dialogs.exportError"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  MULTI-SELECT / BATCH EDIT
        // ═════════════════════════════════════════════════════════════════════

        private void ClearMultiSelection()
        {
            foreach (var kvp in _fMultiSelectBorders)
            {
                if (kvp.Key != _fActiveRuleId)
                {
                    kvp.Value.Background = Brushes.Transparent;
                    kvp.Value.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                }
            }
            _fMultiSelectBorders.Clear();
            _fSelectedRuleIds.Clear();
        }

        private UIElement BuildBatchRuleEditor(FilterTradeConfig trade, FilterRuleConfig rule)
        {
            int count = _fSelectedRuleIds.Count;

            Action<string> markDirty = key =>
                Dispatcher.BeginInvoke(new Action(() => ApplyBatchField(trade, rule, key)));

            var scrollContent = new StackPanel();
            scrollContent.Children.Add(BuildBatchHeader(count));
            scrollContent.Children.Add(BuildMergeSection(trade, rule));
            scrollContent.Children.Add(BuildFilterLogicSection(trade, rule, markDirty));
            scrollContent.Children.Add(BuildOverrideStyleSection(rule, markDirty));
            scrollContent.Children.Add(BuildAppearanceSection(rule, markDirty));

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 4, 0),
            };
            scroll.Content = scrollContent;
            LemoineControlStyles.WireBubblingScroll(scroll);

            return scroll;
        }

        private UIElement BuildBatchHeader(int count)
        {
            var outer = new StackPanel();

            var sectionLbl = new Border { Padding = new Thickness(12, 10, 12, 4) };
            var sectionTb  = new TextBlock { Text = LemoineStrings.T("globalSettings.filters.sections.batchEdit") };
            sectionTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            sectionTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            sectionTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            sectionLbl.Child = sectionTb;
            outer.Children.Add(sectionLbl);

            var card = new Border
            {
                Margin          = new Thickness(10, 0, 10, 10),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10), // rounder card (matches LemoineRadius_Card)
                Padding         = new Thickness(10, 8, 10, 8),
            };
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var descTb = new TextBlock
            {
                Text      = LemoineStrings.T("globalSettings.filters.batchEdit.editingDesc", count),
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
            };
            descTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            descTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            descTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            card.Child = descTb;
            outer.Children.Add(card);
            return outer;
        }

        private void ApplyBatchField(FilterTradeConfig trade, FilterRuleConfig source, string key)
        {
            foreach (var ruleId in _fSelectedRuleIds.Where(id => id != source.Id).ToList())
            {
                var target = trade.Rules.FirstOrDefault(r => r.Id == ruleId);
                if (target == null) continue;

                switch (key)
                {
                    case "logic.categories": target.BuiltInCategories = source.BuiltInCategories.ToList(); break;
                    case "logic.parameter":  target.Parameter = source.Parameter; break;
                    case "logic.match":      target.Match = source.Match.ToList(); break;
                    case "logic.matchtype":  target.MatchType = source.MatchType; break;

                    case "style.surf.enabled": target.OverrideSurf = source.OverrideSurf; break;
                    case "style.surf.color":   target.SurfColor    = source.SurfColor;    break;
                    case "style.surf.pattern": target.SurfPattern  = source.SurfPattern;  break;

                    case "style.cut.enabled":  target.OverrideCut  = source.OverrideCut;  break;
                    case "style.cut.color":    target.CutColor     = source.CutColor;     break;
                    case "style.cut.pattern":  target.CutPattern   = source.CutPattern;   break;

                    case "style.line.enabled": target.OverrideLine = source.OverrideLine; break;
                    case "style.line.color":   target.LineColor    = source.LineColor;    break;
                    case "style.line.pattern": target.LinePattern  = source.LinePattern;  break;
                    case "style.line.weight":  target.LineWeight   = source.LineWeight;   break;

                    case "appearance.halftone":     target.Halftone     = source.Halftone;     break;
                    case "appearance.transparency": target.Transparency = source.Transparency; break;
                    case "appearance.visible":      target.Visible      = source.Visible;      break;
                    case "appearance.apply":        target.Enabled      = source.Enabled;
                                                    target.FilterOn     = source.FilterOn;     break;
                }
            }
        }

        // ── Merge selected rules ──────────────────────────────────────────────
        //
        // Two actions, shown only in batch mode:
        //   • Merge into one  — anchor absorbs the others' keywords + categories; the
        //                        others are deleted (destructive).
        //   • Create combined — a NEW rule with the unioned definition is appended; all
        //                        originals are kept (non-destructive).
        // Both require the selected rules to share one parameter and be keyword-based —
        // a single ParameterFilterElement binds exactly one parameter, and whole-category
        // / value-predicate rules carry no keywords to union.
        private readonly struct MergePlan
        {
            public readonly bool         Ok;
            public readonly string?      Reason;     // why merge is blocked (Ok == false)
            public readonly string       Parameter;
            public readonly string       MatchType;  // resulting match type
            public readonly List<string> Keywords;   // unioned, case-insensitive dedupe
            public readonly List<string> Categories; // unioned OST strings
            public MergePlan(bool ok, string? reason, string parameter, string matchType,
                             List<string> keywords, List<string> categories)
            { Ok = ok; Reason = reason; Parameter = parameter; MatchType = matchType;
              Keywords = keywords; Categories = categories; }
            public static MergePlan Blocked(string reason) =>
                new MergePlan(false, reason, "", "", new List<string>(), new List<string>());
        }

        private static readonly HashSet<string> PositiveKeywordMatchTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "contains", "equals", "begins with", "ends with" };

        // Builds the merge plan from the currently-selected rules of the given trade.
        private MergePlan ComputeMergePlan(FilterTradeConfig trade, FilterRuleConfig anchor)
        {
            // Anchor first, then the rest in trade order, so the union is deterministic.
            var selected = new List<FilterRuleConfig> { anchor };
            selected.AddRange(trade.Rules.Where(r =>
                r.Id != anchor.Id && _fSelectedRuleIds.Contains(r.Id)));

            if (selected.Count < 2)
                return MergePlan.Blocked(LemoineStrings.T("globalSettings.filters.merge.blockedNeedTwo"));

            string param = anchor.Parameter ?? "";
            if (selected.Any(r => !string.Equals(r.Parameter ?? "", param, StringComparison.Ordinal)))
                return MergePlan.Blocked(LemoineStrings.T("globalSettings.filters.merge.blockedSameParameter"));

            // Every selected rule must be keyword-based (not whole-category / value-predicate).
            foreach (var r in selected)
            {
                string mt = (r.MatchType ?? "contains").ToLowerInvariant();
                if (mt == "all" || mt == "has a value" || mt == "has no value")
                    return MergePlan.Blocked(LemoineStrings.T("globalSettings.filters.merge.blockedKeywordOnly"));
            }

            // Resulting match type: same type if uniform; else, if all positive, widen to
            // "contains"; a mix involving a negative (does not contain/equal) is ambiguous.
            var matchTypes = selected
                .Select(r => (r.MatchType ?? "contains").ToLowerInvariant())
                .Distinct().ToList();
            string resultType;
            if (matchTypes.Count == 1)
                resultType = matchTypes[0];
            else if (matchTypes.All(mt => PositiveKeywordMatchTypes.Contains(mt)))
                resultType = "contains";
            else
                return MergePlan.Blocked(LemoineStrings.T("globalSettings.filters.merge.blockedMixedMatch"));

            var keywords = UnionStrings(selected.Select(r => r.Match ?? new List<string>()));
            var cats     = UnionStrings(selected.Select(r => r.BuiltInCategories ?? new List<string>()));
            return new MergePlan(true, null, param, resultType, keywords, cats);
        }

        private static List<string> UnionStrings(IEnumerable<IEnumerable<string>> lists)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outl = new List<string>();
            foreach (var l in lists)
                foreach (var x in l)
                    if (!string.IsNullOrEmpty(x) && seen.Add(x)) outl.Add(x);
            return outl;
        }

        private UIElement BuildMergeSection(FilterTradeConfig trade, FilterRuleConfig anchor)
        {
            var outer = new StackPanel();

            var sectionLbl = new Border { Padding = new Thickness(12, 4, 12, 4) };
            var sectionTb  = new TextBlock { Text = LemoineStrings.T("globalSettings.filters.sections.merge") };
            sectionTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            sectionTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            sectionTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            sectionLbl.Child = sectionTb;
            outer.Children.Add(sectionLbl);

            var card = new Border
            {
                Margin          = new Thickness(10, 0, 10, 10),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10),
                Padding         = new Thickness(10, 8, 10, 8),
            };
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var stack = new StackPanel();
            var plan  = ComputeMergePlan(trade, anchor);

            if (!plan.Ok)
            {
                var blockedTb = new TextBlock
                {
                    Text         = plan.Reason,
                    FontStyle    = FontStyles.Italic,
                    TextWrapping = TextWrapping.Wrap,
                };
                blockedTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                blockedTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                blockedTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                stack.Children.Add(blockedTb);
                card.Child = stack;
                outer.Children.Add(card);
                return outer;
            }

            string categoryWord = plan.Categories.Count == 1
                ? LemoineStrings.T("globalSettings.filters.merge.categoryWordSingular")
                : LemoineStrings.T("globalSettings.filters.merge.categoryWordPlural");
            var summaryTb = new TextBlock
            {
                Text = LemoineStrings.T("globalSettings.filters.merge.summary", _fSelectedRuleIds.Count, plan.Keywords.Count, plan.Categories.Count, categoryWord, plan.MatchType),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 8),
            };
            summaryTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            summaryTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            summaryTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            stack.Children.Add(summaryTb);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };

            var mergeBtn = FlatSmBtn(LemoineStrings.T("globalSettings.filters.merge.mergeIntoOne"));
            mergeBtn.ToolTip = LemoineStrings.T("globalSettings.filters.merge.mergeIntoOneTooltip", anchor.Name);
            mergeBtn.Click += (s, e) =>
                ShowMergeConfirmPopup(mergeBtn, trade, anchor, destructive: true);

            var combineBtn = FlatSmBtn(LemoineStrings.T("globalSettings.filters.merge.createCombined"));
            combineBtn.Margin  = new Thickness(6, 0, 0, 0);
            combineBtn.ToolTip = LemoineStrings.T("globalSettings.filters.merge.createCombinedTooltip");
            combineBtn.Click += (s, e) =>
                ShowMergeConfirmPopup(combineBtn, trade, anchor, destructive: false);

            btnRow.Children.Add(mergeBtn);
            btnRow.Children.Add(combineBtn);
            stack.Children.Add(btnRow);

            card.Child = stack;
            outer.Children.Add(card);
            return outer;
        }

        // Confirm popup (StaysOpen=false matches this window's other popups; it runs on
        // the settings window's own STA thread, not Revit's main loop). Shows the result
        // name + keyword/category lists, then applies the merge or creates a combined rule.
        private void ShowMergeConfirmPopup(
            UIElement anchorBtn, FilterTradeConfig trade, FilterRuleConfig anchor, bool destructive)
        {
            var plan = ComputeMergePlan(trade, anchor);
            if (!plan.Ok) return;

            string resultName = destructive ? anchor.Name : anchor.Name + LemoineStrings.T("globalSettings.filters.suffixes.combined");

            var popup = new Popup
            {
                PlacementTarget    = anchorBtn,
                Placement          = PlacementMode.Bottom,
                StaysOpen          = false,
                AllowsTransparency = false,
            };

            var panel = new StackPanel { Margin = new Thickness(10, 8, 10, 8), MaxWidth = 260 };

            void Line(string text, string fontKey, string colorKey, double bottom)
            {
                var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, bottom) };
                tb.SetResourceReference(TextBlock.FontSizeProperty,   fontKey);
                tb.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                panel.Children.Add(tb);
            }

            Line(destructive
                    ? LemoineStrings.T("globalSettings.filters.merge.confirmMerge", resultName, _fSelectedRuleIds.Count - 1)
                    : LemoineStrings.T("globalSettings.filters.merge.confirmCreate", resultName),
                "LemoineFS_SM", "LemoineText", 6);
            Line(LemoineStrings.T("globalSettings.filters.merge.keywordsLine", plan.Keywords.Count > 0 ? string.Join(", ", plan.Keywords) : LemoineStrings.T("globalSettings.filters.merge.none")),
                "LemoineFS_SM", "LemoineTextDim", 2);
            Line(LemoineStrings.T("globalSettings.filters.merge.categoriesLine", plan.Categories.Count > 0
                    ? string.Join(", ", plan.Categories.Select(AutoFiltersSettings.DisplayNameForOst))
                    : LemoineStrings.T("globalSettings.filters.merge.all")),
                "LemoineFS_SM", "LemoineTextDim", 8);

            var confirmBtn = FlatSmBtn(destructive ? LemoineStrings.T("globalSettings.filters.merge.mergeBtn") : LemoineStrings.T("globalSettings.filters.merge.createBtn"));
            if (destructive)
            {
                confirmBtn.SetResourceReference(Button.ForegroundProperty,  "LemoineRed");
                confirmBtn.SetResourceReference(Button.BorderBrushProperty, "LemoineRed");
            }
            confirmBtn.Click += (s, e) =>
            {
                popup.IsOpen = false;
                ApplyMerge(trade, anchor, plan, destructive);
            };
            var cancelBtn = FlatSmBtn(LemoineStrings.T("globalSettings.filters.common.cancel"));
            cancelBtn.Margin = new Thickness(6, 0, 0, 0);
            cancelBtn.Click += (s, e) => popup.IsOpen = false;

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(confirmBtn);
            row.Children.Add(cancelBtn);
            panel.Children.Add(row);

            var outerBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Margin          = new Thickness(0, 2, 0, 0),
                Child           = panel,
            };
            outerBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");

            popup.Child  = outerBorder;
            popup.IsOpen = true;
        }

        private void ApplyMerge(
            FilterTradeConfig trade, FilterRuleConfig anchor, MergePlan plan, bool destructive)
        {
            if (destructive)
            {
                anchor.Parameter         = plan.Parameter;
                anchor.MatchType         = plan.MatchType;
                anchor.Match             = plan.Keywords;
                anchor.BuiltInCategories = plan.Categories;

                var others = _fSelectedRuleIds.Where(id => id != anchor.Id).ToList();
                ClearMultiSelection();
                foreach (var id in others)
                    trade.Rules.RemoveAll(r => r.Id == id);
                _fActiveRuleId = anchor.Id;
            }
            else
            {
                var combined = FilterRuleConfig.NewBlank();
                combined.Name              = anchor.Name + LemoineStrings.T("globalSettings.filters.suffixes.combined");
                combined.Parameter         = plan.Parameter;
                combined.MatchType         = plan.MatchType;
                combined.Match             = plan.Keywords;
                combined.BuiltInCategories = plan.Categories;
                combined.Enabled           = anchor.Enabled;
                // Graphics come from the anchor so the combined rule looks like it.
                combined.CutColor     = anchor.CutColor;     combined.CutPattern  = anchor.CutPattern;
                combined.OverrideCut  = anchor.OverrideCut;
                combined.SurfColor    = anchor.SurfColor;    combined.SurfPattern = anchor.SurfPattern;
                combined.OverrideSurf = anchor.OverrideSurf;
                combined.LineColor    = anchor.LineColor;    combined.LinePattern = anchor.LinePattern;
                combined.LineWeight   = anchor.LineWeight;   combined.OverrideLine = anchor.OverrideLine;
                combined.Halftone     = anchor.Halftone;     combined.Transparency = anchor.Transparency;
                combined.Visible      = anchor.Visible;      combined.FilterOn     = anchor.FilterOn;

                trade.Rules.Add(combined);
                ClearMultiSelection();
                _fActiveRuleId = combined.Id;
            }

            FRefreshRuleList();
            FRefreshRuleEditor();
        }
    }
}
