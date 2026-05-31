using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.Testing.LegendCreator;
using WpfGrid       = System.Windows.Controls.Grid;
using WpfPoint      = System.Windows.Point;
using WpfVisibility = System.Windows.Visibility;

namespace LemoineTools.Lemoine
{
    public partial class LegendSettingsWindow : Window
    {
        // ── Constructor parameters ─────────────────────────────────────────────
        private readonly List<(ElementId Id, string Name)> _textTypes;
        private readonly List<(ElementId Id, string Name)> _legendViews;

        // ── Active state ───────────────────────────────────────────────────────
        private int _activeIndex = 0;
        private readonly Dictionary<string, LemoineLegendBuilder> _builders =
            new Dictionary<string, LemoineLegendBuilder>();

        // ── Window-level preview overlay (grows from the floating Preview pill) ──
        private LemoineLegendPreview?   _previewControl;
        private WpfGrid?                _previewOverlayGrid;
        private bool                    _previewVisible;
        private LemoineLegendBuilder?   _activeBuilder;   // subscribed for live preview refresh

        // ── UI refs ────────────────────────────────────────────────────────────
        private Border?                 _statusChip;       // surface chip behind status text
        private TextBlock?              _statusText;
        private Border?                 _createPill;       // floating bottom-right Create/Update
        private TextBlock?              _createPillLabel;
        private bool                    _createBusy;       // guards re-entry while a run is in flight
        private Border?                 _previewPill;      // floating bottom-right Preview (above Create)
        private TextBlock?              _previewPillLabel;
        private StackPanel?             _tabStack;
        private ScrollViewer?           _tabScroll;   // auto-scroll to newly-added legend
        private LemoineLegendPalette?   _palette;
        private ContentControl?         _builderSlot;
        private ContentControl?         _sizingSlot;
        private ContentControl?         _textStylesSlot;
        private ContentControl?         _paletteSlot;
        private Border?                 _paletteCard;      // bottom card; inset to clear floating pills

        // ── Constructor ────────────────────────────────────────────────────────
        public LegendSettingsWindow(
            List<(ElementId Id, string Name)>? textTypes   = null,
            List<(ElementId Id, string Name)>? legendViews = null)
        {
            _textTypes   = textTypes   ?? new List<(ElementId, string)>();
            _legendViews = legendViews ?? new List<(ElementId, string)>();

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
            _root.SetResourceReference(WpfGrid.BackgroundProperty, "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _outerBorder.CornerRadius = new CornerRadius(8);

            _palette = new LemoineLegendPalette();
            AutoFiltersSettings.Saved += OnFiltersSaved;

            UpdateRowHeights();
            BuildToolbar();
            BuildMainLayout();
            BuildPreviewLayer();
            BuildFloatingActions();
        }

        protected override void OnClosed(EventArgs e)
        {
            AutoFiltersSettings.Saved -= OnFiltersSaved;
            foreach (var b in _builders.Values)
            {
                AutoFiltersSettings.Saved -= b.OnFiltersSaved;
                b.Edited                  -= OnBuilderEdited;
            }
            base.OnClosed(e);
        }

        private void OnFiltersSaved() => _palette?.Refresh();

        private void UpdateRowHeights()
        {
            if (_root == null) return;
            _root.RowDefinitions[0].Height = new GridLength(LemoineSettings.Instance.ToolbarHeight);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Toolbar
        // ─────────────────────────────────────────────────────────────────────
        private void BuildToolbar()
        {
            _toolbarBorder.BorderThickness = new Thickness(0);

            var closeBtn = LemoineControlStyles.BuildButton(
                "×", LemoineControlStyles.LemoineButtonVariant.Ghost);
            closeBtn.Margin = new Thickness(8, 0, 0, 0);
            closeBtn.SetResourceReference(Button.ForegroundProperty, "LemoineTextDim");
            closeBtn.Click += (s, ev) => Close();

            var rightPanel = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rightPanel.Children.Add(closeBtn);

            _toolbarBorder.Child = new LemoineTitleBar
            {
                Title        = "Legend Creation",
                IconGlyph    = "⚙",
                RightContent = rightPanel,
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // Floating bottom-right action pills (status · Preview · Create/Update)
        // ─────────────────────────────────────────────────────────────────────
        private void BuildFloatingActions()
        {
            _statusChip = LemoineControlStyles.BuildStatusChip(out _statusText);
            _statusChip.HorizontalAlignment = HorizontalAlignment.Center;
            _statusChip.Margin              = new Thickness(0, 0, 0, 6);

            // Preview pill — sits directly above the Create pill
            _previewPill = LemoineControlStyles.BuildActionPill("Preview", primary: false, TogglePreviewFromPill);
            _previewPill.HorizontalAlignment = HorizontalAlignment.Center;
            _previewPill.Margin              = new Thickness(0, 0, 0, 8);
            _previewPill.ToolTip             = "Toggle a live preview of the legend";
            _previewPillLabel = _previewPill.Child as TextBlock;

            // Create / Update pill
            _createPill = LemoineControlStyles.BuildActionPill("Create Legend →", primary: true, HandleCreateUpdate);
            _createPill.HorizontalAlignment = HorizontalAlignment.Center;
            _createPill.ToolTip             = "Create or update the Legend view in Revit";
            _createPillLabel = _createPill.Child as TextBlock;

            var stack = new StackPanel
            {
                Orientation         = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Bottom,
            };
            stack.Children.Add(_statusChip);
            stack.Children.Add(_previewPill);
            stack.Children.Add(_createPill);

            // Pills float at the bottom of the left sidebar column, centered on it.
            // Kept in the ZIndex-50 slot (above the preview overlay) so the
            // Hide-Preview pill stays clickable while a preview is open.
            _floatingSlot.Content             = stack;
            _floatingSlot.HorizontalAlignment = HorizontalAlignment.Left;
            _floatingSlot.Width               = 180;   // matches the sidebar column width
            _floatingSlot.Margin              = new Thickness(0, 0, 0, 16);

            // Reserve space at the bottom of the sidebar tab list so the last
            // legend tab / Add Legend pill is never hidden behind the floating pills.
            _floatingSlot.SizeChanged += (s, e) => ApplyFloatingInset(e.NewSize.Height);

            // Reflect the Create/Update label for the active legend
            var entries = LegendCreatorSettings.Instance.Legends;
            if (_activeIndex >= 0 && _activeIndex < entries.Count)
                UpdateCreateUpdateButton(entries[_activeIndex]);
        }

        // ── Window-level preview overlay ────────────────────────────────────────
        private void BuildPreviewLayer()
        {
            _previewControl = new LemoineLegendPreview { Margin = new Thickness(8) };
            _previewOverlayGrid = new WpfGrid
            {
                Visibility            = WpfVisibility.Collapsed,
                // Grows from/to the bottom-left corner — where the Preview pill lives
                // (the pills now sit at the bottom of the left sidebar column).
                RenderTransformOrigin = new WpfPoint(0, 1),
                RenderTransform       = new ScaleTransform(0, 0),
            };
            _previewOverlayGrid.SetResourceReference(WpfGrid.BackgroundProperty, "LemoineSurface");
            _previewOverlayGrid.Children.Add(_previewControl);
            _previewLayer.Children.Add(_previewOverlayGrid);
        }

        private void TogglePreviewFromPill()
        {
            if (_previewVisible) ClosePreview(); else OpenPreview();
        }

        private void OpenPreview()
        {
            var builder = _builderSlot?.Content as LemoineLegendBuilder;
            if (builder == null || _previewControl == null || _previewOverlayGrid == null) return;
            _previewVisible = true;
            _previewControl.Update(builder.Layout, builder.Rows);
            _previewOverlayGrid.Visibility = WpfVisibility.Visible;
            var st   = (ScaleTransform)_previewOverlayGrid.RenderTransform;
            var open = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, open);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, open);
            if (_previewPillLabel != null) _previewPillLabel.Text = "Hide Preview";
        }

        private void ClosePreview()
        {
            _previewVisible = false;
            if (_previewPillLabel != null) _previewPillLabel.Text = "Preview";
            if (_previewOverlayGrid == null) return;
            var st    = (ScaleTransform)_previewOverlayGrid.RenderTransform;
            var close = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            close.Completed += (s, e) => { if (_previewOverlayGrid != null) _previewOverlayGrid.Visibility = WpfVisibility.Collapsed; };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, close);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, close);
        }

        // Refreshes the live preview from the active builder when it's showing.
        private void OnBuilderEdited()
        {
            if (_previewVisible && _previewControl != null && _activeBuilder != null)
                _previewControl.Update(_activeBuilder.Layout, _activeBuilder.Rows);
        }

        // Pushes a bottom inset onto the sidebar tab list so its last item (the
        // Add Legend pill) clears the floating Preview / Create pills below it.
        private void ApplyFloatingInset(double pillsHeight)
        {
            if (_tabScroll == null) return;
            double inset = Math.Max(0, pillsHeight) + 24;
            _tabScroll.Margin = new Thickness(0, 0, 0, inset);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Main 3-column layout
        // ─────────────────────────────────────────────────────────────────────
        private void BuildMainLayout()
        {
            // Four-column split mirroring the Auto Filters window:
            // [sidebar 180] [canvas * (min150)] [splitter 5] [right editor 280 (min280)]
            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280), MinWidth = 280 });

            // Sidebar
            var sidebarBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0) };
            sidebarBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            sidebarBorder.Child = BuildSidebar();
            WpfGrid.SetColumn(sidebarBorder, 0);
            grid.Children.Add(sidebarBorder);

            // Center builder slot
            _builderSlot = new ContentControl();
            WpfGrid.SetColumn(_builderSlot, 1);
            grid.Children.Add(_builderSlot);

            // Splitter between canvas and right editor
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
            WpfGrid.SetColumn(splitter, 2);
            grid.Children.Add(splitter);

            // Right panel
            var rightBorder = new Border { BorderThickness = new Thickness(1, 0, 0, 0) };
            rightBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            rightBorder.Child = BuildRightPanel();
            WpfGrid.SetColumn(rightBorder, 3);
            grid.Children.Add(rightBorder);

            _contentBorder.Child = grid;

            ActivateTab(0);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Sidebar
        // ─────────────────────────────────────────────────────────────────────
        private UIElement BuildSidebar()
        {
            var dp = new DockPanel { LastChildFill = true };

            // Templates pill at top
            var templatesPill = BuildTemplatesPill();
            DockPanel.SetDock(templatesPill, Dock.Top);
            dp.Children.Add(templatesPill);

            // "LEGENDS" section header
            var legendsLabel = new TextBlock
            {
                Text   = "LEGENDS",
                Margin = new Thickness(10, 12, 10, 6),
            };
            legendsLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            legendsLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            legendsLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            DockPanel.SetDock(legendsLabel, Dock.Top);
            dp.Children.Add(legendsLabel);

            // "＋ Add Legend" now floats as the last item inside the tab list
            // (AppendAddLegendPill, called from RebuildTabStack) — no sticky bar.
            // Create/Update moved to the floating bottom-right pill.

            // Tab list (fills remaining space)
            _tabStack = new StackPanel { Orientation = Orientation.Vertical };
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            scroll.Content = _tabStack;
            _tabScroll = scroll;
            dp.Children.Add(scroll);

            return dp;
        }

        private Border BuildTemplatesPill()
        {
            var pill = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding         = new Thickness(10),
                Cursor          = Cursors.Hand,
            };
            pill.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            pill.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");

            var pillText = new TextBlock { Text = "TEMPLATES ˅" };
            pillText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            pillText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            pillText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            pill.Child = pillText;

            pill.MouseEnter += (s, e) =>
                pill.SetResourceReference(Border.BackgroundProperty, "LemoineSelectBg");
            pill.MouseLeave += (s, e) =>
                pill.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");

            pill.MouseLeftButtonUp += (s, e) =>
            {
                var entries = LegendCreatorSettings.Instance.Legends;
                if (_activeIndex >= 0 && _activeIndex < entries.Count)
                    GetOrCreateBuilder(entries[_activeIndex]).ShowTemplatesPopup(pill);
            };

            return pill;
        }

        private void RebuildTabStack()
        {
            if (_tabStack == null) return;
            _tabStack.Children.Clear();

            var entries = LegendCreatorSettings.Instance.Legends;
            for (int i = 0; i < entries.Count; i++)
                _tabStack.Children.Add(BuildTabItem(entries[i], i));

            // "＋ Add Legend" floats as the last item in the list
            _tabStack.Children.Add(
                LemoineControlStyles.BuildAddPill("＋  Add Legend", () => AddLegend()));

            // Guard for ComboBoxes that may have been rebuilt
            Dispatcher.BeginInvoke(new Action(() => Keyboard.ClearFocus()),
                DispatcherPriority.Input);
        }

        private Border BuildTabItem(LegendEntry entry, int index)
        {
            bool isActive = index == _activeIndex;

            // Structured to mirror the Auto Filters trade tabs: a left accent bar
            // marks the active item; an inline ✎ opens the editor. (Legends have no
            // single representative colour, so the filters' colour dot is omitted.)
            var tab = new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                Margin  = new Thickness(0, 0, 0, 2),
                Cursor  = isActive ? Cursors.Arrow : Cursors.Hand,
            };

            if (isActive)
            {
                tab.BorderThickness = new Thickness(3, 0, 0, 0);
                tab.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                tab.SetResourceReference(Border.BackgroundProperty,  "LemoineSelectBg");
            }
            else
            {
                tab.BorderThickness = new Thickness(3, 0, 0, 0);
                tab.BorderBrush     = Brushes.Transparent;
            }

            // [ label ] .......... [ ✎ ]
            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameDisplay = new TextBlock
            {
                Text              = entry.GetDisplayName(),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            nameDisplay.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            nameDisplay.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            nameDisplay.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            WpfGrid.SetColumn(nameDisplay, 0);
            grid.Children.Add(nameDisplay);

            // Edit pencil (✎) — opens the Title / Subtitle editor moved here from
            // the builder's old top layout bar.
            var editBtn = new TextBlock
            {
                Text              = "✎",
                VerticalAlignment = VerticalAlignment.Center,
                Cursor            = Cursors.Hand,
                Margin            = new Thickness(6, 0, 0, 0),
            };
            editBtn.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            editBtn.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            editBtn.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            WpfGrid.SetColumn(editBtn, 1);
            grid.Children.Add(editBtn);

            // Handle Down (not Up) and mark it handled so the click never bubbles to
            // the tab's activate handler — otherwise activating rebuilds the tab list
            // and detaches the popup's anchor before it opens.
            editBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                ShowLegendEditPopup(tab, entry, nameDisplay);
            };

            tab.Child = grid;

            int capturedIndex = index;
            tab.MouseLeftButtonDown += (s, e) =>
            {
                if (capturedIndex != _activeIndex)
                    ActivateTab(capturedIndex);
                else if (e.ClickCount == 2)
                {
                    ShowLegendEditPopup(tab, entry, nameDisplay);
                    e.Handled = true;
                }
            };

            return tab;
        }

        // Title / Subtitle editor — moved here from the builder's old top layout bar
        // and anchored to the legend tab's ✎ pencil.
        private void ShowLegendEditPopup(UIElement anchor, LegendEntry entry, TextBlock nameDisplay)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin      = new Thickness(8),
                MinWidth    = 200,
            };

            panel.Children.Add(MakeEditLabel("Title"));
            var titleBox = MakeEditBox(entry.Layout?.Title ?? "");
            titleBox.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(titleBox);

            panel.Children.Add(MakeEditLabel("Subtitle"));
            var subBox = MakeEditBox(entry.Layout?.Subtitle ?? "");
            subBox.Margin = new Thickness(0, 0, 0, 10);
            panel.Children.Add(subBox);

            var popup = new Popup
            {
                PlacementTarget    = anchor,
                Placement          = PlacementMode.Bottom,
                StaysOpen          = true,   // StaysOpen=false crashes Revit (see CLAUDE.md)
                AllowsTransparency = false,
            };

            void Commit()
            {
                ApplyLegendTitle(entry, titleBox.Text.Trim(), subBox.Text.Trim(), nameDisplay);
                popup.IsOpen = false;
            }

            var saveBtn = LemoineControlStyles.BuildButton(
                "Save", LemoineControlStyles.LemoineButtonVariant.Primary);
            saveBtn.Click += (s, e) => Commit();
            panel.Children.Add(saveBtn);

            void OnKey(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Return)      { Commit();            e.Handled = true; }
                else if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; }
            }
            titleBox.KeyDown += OnKey;
            subBox.KeyDown   += OnKey;

            var outerBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Child           = panel,
            };
            outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            outerBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");

            popup.Child  = outerBorder;
            popup.IsOpen = true;

            Dispatcher.BeginInvoke(new Action(() => { titleBox.Focus(); titleBox.SelectAll(); }),
                DispatcherPriority.Input);
        }

        private static TextBlock MakeEditLabel(string text)
        {
            var lbl = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 2) };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return lbl;
        }

        private static TextBox MakeEditBox(string text)
        {
            var box = new TextBox
            {
                Text            = text,
                Padding         = new Thickness(6, 4, 6, 4),
                BorderThickness = new Thickness(1),
            };
            box.SetResourceReference(TextBox.ForegroundProperty,  "LemoineText");
            box.SetResourceReference(TextBox.FontFamilyProperty,  "LemoineUiFont");
            box.SetResourceReference(TextBox.FontSizeProperty,    "LemoineFS_MD");
            box.SetResourceReference(TextBox.BorderBrushProperty, "LemoineBorder");
            box.SetResourceReference(TextBox.BackgroundProperty,  "LemoineSelectBg");
            box.SetResourceReference(TextBox.CaretBrushProperty,  "LemoineText");
            return box;
        }

        // Applies an edited Title/Subtitle to both the persisted entry and the live
        // builder buffer, then refreshes the tab label and (if open) the preview.
        private void ApplyLegendTitle(LegendEntry entry, string title, string subtitle, TextBlock nameDisplay)
        {
            if (entry.Layout == null) entry.Layout = new LegendLayoutConfig();
            entry.Layout.Title    = title;
            entry.Layout.Subtitle = subtitle;
            entry.DisplayName     = null;   // the title is now the single source of the tab name

            var builder = GetOrCreateBuilder(entry);
            builder.Layout.Title    = title;
            builder.Layout.Subtitle = subtitle;

            nameDisplay.Text = entry.GetDisplayName();
            LegendCreatorSettings.Instance.Save();
            builder.NotifyLayoutChanged();   // rebuilds the canvas and raises Edited → refreshes preview
        }

        // ─────────────────────────────────────────────────────────────────────
        // Right panel
        // ─────────────────────────────────────────────────────────────────────
        private UIElement BuildRightPanel()
        {
            // [cards scroll (capped)] over [palette fills] — see the design decision:
            // SIZING + TEXT STYLES are scrollable rounded cards on top; the palette
            // (drag source / bulk-block editor) fills and keeps its own scroll.
            var grid = new WpfGrid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 160 });

            _sizingSlot     = new ContentControl();
            _textStylesSlot = new ContentControl();

            var cardsStack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            cardsStack.Children.Add(_sizingSlot);
            cardsStack.Children.Add(_textStylesSlot);

            var cardsScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content                       = cardsStack,
            };
            LemoineControlStyles.WireBubblingScroll(cardsScroll);
            // Cap the cards region to ~half the column so the palette always claims space.
            grid.SizeChanged += (s, e) => cardsScroll.MaxHeight = Math.Max(120, e.NewSize.Height * 0.5);
            WpfGrid.SetRow(cardsScroll, 0);
            grid.Children.Add(cardsScroll);

            // PALETTE card (the control renders its own "PALETTE" header internally).
            _paletteCard = new Border
            {
                Margin          = new Thickness(10, 0, 10, 10),
                BorderThickness = new Thickness(1),
            };
            _paletteCard.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Card");
            _paletteCard.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
            _paletteCard.SetResourceReference(Border.BackgroundProperty,   "LemoineBg");
            _paletteSlot = new ContentControl { Content = _palette };
            _paletteCard.Child = _paletteSlot;
            WpfGrid.SetRow(_paletteCard, 1);
            grid.Children.Add(_paletteCard);

            return grid;
        }

        // Section label + rounded card wrapper shared by the right-panel sections.
        private UIElement WrapCard(string title, UIElement body)
        {
            var outer = new StackPanel();

            var lbl = new TextBlock { Text = title, Margin = new Thickness(12, 0, 12, 4) };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            outer.Children.Add(lbl);

            var card = new Border
            {
                Margin          = new Thickness(10, 0, 10, 10),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(10, 8, 10, 8),
                Child           = body,
            };
            card.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Card");
            card.SetResourceReference(Border.BackgroundProperty,   "LemoineBg");
            card.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
            outer.Children.Add(card);

            return outer;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ActivateTab
        // ─────────────────────────────────────────────────────────────────────
        private void ActivateTab(int index)
        {
            var entries = LegendCreatorSettings.Instance.Legends;
            if (entries.Count == 0) return;
            index = Math.Max(0, Math.Min(index, entries.Count - 1));
            _activeIndex = index;

            var entry = entries[index];

            // Detach old builder events
            if (_builderSlot?.Content is LemoineLegendBuilder oldBuilder)
            {
                oldBuilder.BulkEditorChanged -= OnBulkEditorChanged;
                oldBuilder.Edited            -= OnBuilderEdited;
            }

            // Get/create builder and wire up
            var builder = GetOrCreateBuilder(entry);
            builder.BulkEditorChanged += OnBulkEditorChanged;
            builder.Edited            += OnBuilderEdited;
            _activeBuilder            = builder;

            if (_builderSlot != null)
                _builderSlot.Content = builder;

            // Reset palette slot
            if (_paletteSlot != null)
                _paletteSlot.Content = _palette;

            // Rebuild right-panel sections for this entry
            if (_sizingSlot != null)
                _sizingSlot.Content = BuildSizingSection(builder);
            if (_textStylesSlot != null)
                _textStylesSlot.Content = BuildTextStylesSection(entry);

            UpdateCreateUpdateButton(entry);
            // Preview is a single window-level overlay; reflect its state and, if
            // it's showing, repoint it at the newly-active legend.
            if (_previewPillLabel != null)
                _previewPillLabel.Text = _previewVisible ? "Hide Preview" : "Preview";
            if (_previewVisible && _previewControl != null)
                _previewControl.Update(builder.Layout, builder.Rows);
            RebuildTabStack();
        }

        private LemoineLegendBuilder GetOrCreateBuilder(LegendEntry entry)
        {
            if (_builders.TryGetValue(entry.Id, out var existing)) return existing;
            var builder = new LemoineLegendBuilder();
            builder.LoadFrom(entry);
            _builders[entry.Id] = builder;
            return builder;
        }

        private void OnBulkEditorChanged(UIElement? element)
        {
            if (_paletteSlot == null) return;
            if (element != null) _paletteSlot.Content = element;
            else                 _paletteSlot.Content = _palette;
        }

        private void UpdateCreateUpdateButton(LegendEntry entry)
        {
            _createBusy = false;
            if (_createPill != null) _createPill.Opacity = 1.0;
            if (_createPillLabel == null) return;
            _createPillLabel.Text = entry.RevitViewId != -1 ? "Update Legend →" : "Create Legend →";
        }

        // Toggles the floating Create pill between idle and in-flight (busy) states.
        private void SetCreateBusy(bool busy, string? busyLabel)
        {
            _createBusy = busy;
            if (_createPill != null) _createPill.Opacity = busy ? 0.6 : 1.0;
            if (_createPillLabel == null) return;
            if (busy && busyLabel != null)
            {
                _createPillLabel.Text = busyLabel;
                return;
            }
            var entries = LegendCreatorSettings.Instance.Legends;
            if (_activeIndex >= 0 && _activeIndex < entries.Count)
                _createPillLabel.Text =
                    entries[_activeIndex].RevitViewId != -1 ? "Update Legend →" : "Create Legend →";
        }

        // ─────────────────────────────────────────────────────────────────────
        // Sizing section
        // ─────────────────────────────────────────────────────────────────────
        private UIElement BuildSizingSection(LemoineLegendBuilder builder)
        {
            var layout = builder.Layout;

            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < 5; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddSizingRow(grid, 0, "Scale 1:", layout.ViewScale.ToString(),
                val => { if (int.TryParse(val, out int n) && n > 0) { layout.ViewScale = n; builder.NotifyLayoutChanged(); } });
            AddSizingRow(grid, 1, "Swatch W", layout.SwatchW.ToString("F3"),
                val => { if (double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) && d > 0) { layout.SwatchW = d; builder.NotifyLayoutChanged(); } });
            AddSizingRow(grid, 2, "Swatch H", layout.SwatchH.ToString("F3"),
                val => { if (double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) && d > 0) { layout.SwatchH = d; builder.NotifyLayoutChanged(); } });
            AddSizingRow(grid, 3, "Font pt", layout.FontPt.ToString(),
                val => { if (int.TryParse(val, out int n) && n > 0) { layout.FontPt = n; builder.NotifyLayoutChanged(); } });
            AddSizingRow(grid, 4, "Gap", layout.Gap.ToString("F3"),
                val => { if (double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) && d >= 0) { layout.Gap = d; builder.NotifyLayoutChanged(); } });

            return WrapCard("SIZING", grid);
        }

        private void AddSizingRow(WpfGrid grid, int row, string label, string value, Action<string> onCommit)
        {
            var lbl = new TextBlock
            {
                Text              = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, row == 0 ? 0 : 5, 0, 0),
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var tb = new TextBox
            {
                Text            = value,
                Padding         = new Thickness(4, 3, 4, 3),
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(0, row == 0 ? 0 : 5, 0, 0),
            };
            tb.SetResourceReference(TextBox.ForegroundProperty,    "LemoineText");
            tb.SetResourceReference(TextBox.FontFamilyProperty,    "LemoineMonoFont");
            tb.SetResourceReference(TextBox.FontSizeProperty,      "LemoineFS_SM");
            tb.SetResourceReference(TextBox.BorderBrushProperty,   "LemoineBorder");
            tb.SetResourceReference(TextBox.BackgroundProperty,    "LemoineSelectBg");
            tb.SetResourceReference(TextBox.CaretBrushProperty,    "LemoineText");

            tb.LostFocus += (s, e) => onCommit(tb.Text.Trim());
            tb.KeyDown   += (s, e) =>
            {
                if (e.Key == Key.Return) { onCommit(tb.Text.Trim()); Keyboard.ClearFocus(); e.Handled = true; }
            };

            WpfGrid.SetRow(lbl, row); WpfGrid.SetColumn(lbl, 0);
            WpfGrid.SetRow(tb,  row); WpfGrid.SetColumn(tb,  2);
            grid.Children.Add(lbl);
            grid.Children.Add(tb);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Text Styles section
        // ─────────────────────────────────────────────────────────────────────
        private UIElement BuildTextStylesSection(LegendEntry entry)
        {
            if (_textTypes.Count == 0)
            {
                var noTypes = new TextBlock
                {
                    Text         = "No TextNoteTypes found in project.",
                    TextWrapping = TextWrapping.Wrap,
                };
                noTypes.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                noTypes.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                noTypes.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                return WrapCard("TEXT STYLES", noTypes);
            }

            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < 4; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddTextTypeRow(grid, 0, "TITLE",        entry.TitleTypeId,       id => entry.TitleTypeId       = id);
            AddTextTypeRow(grid, 1, "SUBTITLE",     entry.SubtitleTypeId,    id => entry.SubtitleTypeId    = id);
            AddTextTypeRow(grid, 2, "GROUP HEADER", entry.GroupHeaderTypeId, id => entry.GroupHeaderTypeId = id);
            AddTextTypeRow(grid, 3, "LABEL",        entry.LabelTypeId,       id => entry.LabelTypeId       = id);

            return WrapCard("TEXT STYLES", grid);
        }

        private void AddTextTypeRow(WpfGrid grid, int row, string label, long storedId, Action<long> onChanged)
        {
            var lbl = new TextBlock
            {
                Text              = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, row == 0 ? 0 : 6, 0, 0),
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var combo = new ComboBox
            {
                IsEditable = false,
                Margin     = new Thickness(0, row == 0 ? 0 : 6, 0, 0),
            };
            combo.SetResourceReference(FrameworkElement.HeightProperty, "LemoineH_Input");
            combo.SetResourceReference(ComboBox.FontFamilyProperty,     "LemoineMonoFont");
            combo.SetResourceReference(ComboBox.FontSizeProperty,       "LemoineFS_SM");

            foreach (var (_, name) in _textTypes)
                combo.Items.Add(name);

            int initIdx = 0;
            if (storedId != -1)
            {
                for (int i = 0; i < _textTypes.Count; i++)
                    if (_textTypes[i].Id.Value == storedId) { initIdx = i; break; }
            }
            combo.SelectedIndex = initIdx;

            combo.SelectionChanged += (s, e) =>
            {
                int idx = combo.SelectedIndex;
                long newId = idx >= 0 && idx < _textTypes.Count ? _textTypes[idx].Id.Value : -1L;
                onChanged(newId);
            };

            WpfGrid.SetRow(lbl,   row); WpfGrid.SetColumn(lbl,   0);
            WpfGrid.SetRow(combo, row); WpfGrid.SetColumn(combo, 2);
            grid.Children.Add(lbl);
            grid.Children.Add(combo);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Create / Update
        // ─────────────────────────────────────────────────────────────────────
        private void HandleCreateUpdate()
        {
            if (_createBusy) return; // guard re-entry while a run is in flight

            if (App.LegendCreatorHandler == null || App.LegendCreatorEvent == null)
            {
                FlashStatus("Legend creator not initialised.");
                return;
            }

            var entries = LegendCreatorSettings.Instance.Legends;
            if (_activeIndex < 0 || _activeIndex >= entries.Count) return;
            var entry   = entries[_activeIndex];

            // Flush editing buffer into entry
            var builder = GetOrCreateBuilder(entry);
            entry.Layout = builder.Layout;
            entry.Rows   = builder.Rows;

            bool isUpdate = entry.RevitViewId != -1;

            App.LegendCreatorHandler.Layout     = entry.Layout;
            App.LegendCreatorHandler.Rows        = entry.Rows;
            App.LegendCreatorHandler.UpdateMode  = isUpdate;
            App.LegendCreatorHandler.TargetLegendId    = isUpdate   ? new ElementId(entry.RevitViewId)       : (ElementId?)null;
            App.LegendCreatorHandler.TemplateLegendId  = null;
            App.LegendCreatorHandler.TitleTypeId       = entry.TitleTypeId       != -1 ? new ElementId(entry.TitleTypeId)       : (ElementId?)null;
            App.LegendCreatorHandler.SubtitleTypeId    = entry.SubtitleTypeId    != -1 ? new ElementId(entry.SubtitleTypeId)    : (ElementId?)null;
            App.LegendCreatorHandler.GroupHeaderTypeId = entry.GroupHeaderTypeId != -1 ? new ElementId(entry.GroupHeaderTypeId) : (ElementId?)null;
            App.LegendCreatorHandler.LabelTypeId       = entry.LabelTypeId       != -1 ? new ElementId(entry.LabelTypeId)       : (ElementId?)null;
            App.LegendCreatorHandler.PushLog           = null;
            App.LegendCreatorHandler.OnProgress        = null;

            int capturedIndex = _activeIndex;

            App.LegendCreatorHandler.OnLegendCreated = viewId =>
            {
                long idValue = viewId.Value;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var list = LegendCreatorSettings.Instance.Legends;
                    if (capturedIndex < list.Count)
                    {
                        list[capturedIndex].RevitViewId = idValue;
                        LegendCreatorSettings.Instance.Save();
                        UpdateCreateUpdateButton(list[capturedIndex]);
                    }
                    FlashStatus("Legend created.");
                    SetCreateBusy(false, null);
                }));
            };

            App.LegendCreatorHandler.OnComplete = (pass, fail, skip) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SetCreateBusy(false, null);
                    if (fail > 0)          FlashStatus($"Completed with {fail} error(s).");
                    else if (isUpdate)     FlashStatus("Legend updated.");
                }));
            };

            SetCreateBusy(true, isUpdate ? "Updating…" : "Creating…");

            LegendCreatorSettings.Instance.Save();
            App.LegendCreatorEvent.Raise();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Add Legend
        // ─────────────────────────────────────────────────────────────────────
        private void AddLegend()
        {
            var entry = new LegendEntry
            {
                Id     = LegendIdGen.New("legend"),
                Layout = new LegendLayoutConfig { Title = "New Legend" },
            };
            LegendCreatorSettings.Instance.Legends.Add(entry);
            LegendCreatorSettings.Instance.Save();
            ActivateTab(LegendCreatorSettings.Instance.Legends.Count - 1);
            Dispatcher.BeginInvoke(new Action(() => _tabScroll?.ScrollToBottom()),
                DispatcherPriority.Background);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Status flash
        // ─────────────────────────────────────────────────────────────────────
        private void FlashStatus(string msg)
        {
            if (_statusText == null) return;
            _statusText.Text = msg;
            if (_statusChip != null) _statusChip.Visibility = WpfVisibility.Visible;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (s, e) =>
            {
                _statusText.Text = "";
                if (_statusChip != null) _statusChip.Visibility = WpfVisibility.Collapsed;
                timer.Stop();
            };
            timer.Start();
        }
    }
}
