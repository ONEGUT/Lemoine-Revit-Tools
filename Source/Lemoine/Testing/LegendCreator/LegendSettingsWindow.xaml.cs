using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.Testing.LegendCreator;
using WpfGrid = System.Windows.Controls.Grid;

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

        // ── UI refs ────────────────────────────────────────────────────────────
        private TextBlock?              _statusText;
        private Button?                 _createUpdateBtn;
        private StackPanel?             _tabStack;
        private LemoineLegendPalette?   _palette;
        private ContentControl?         _builderSlot;
        private ContentControl?         _sizingSlot;
        private ContentControl?         _textStylesSlot;
        private ContentControl?         _paletteSlot;

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
            BuildFooter();
            BuildMainLayout();
        }

        protected override void OnClosed(EventArgs e)
        {
            AutoFiltersSettings.Saved -= OnFiltersSaved;
            foreach (var b in _builders.Values)
                AutoFiltersSettings.Saved -= b.OnFiltersSaved;
            base.OnClosed(e);
        }

        private void OnFiltersSaved() => _palette?.Refresh();

        private void UpdateRowHeights()
        {
            if (_root == null) return;
            _root.RowDefinitions[0].Height = new GridLength(LemoineSettings.Instance.ToolbarHeight);
            _root.RowDefinitions[2].Height = new GridLength(LemoineSettings.Instance.FooterHeight);
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
                Title        = "Legend Creator",
                IconGlyph    = "⚙",
                RightContent = rightPanel,
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // Footer
        // ─────────────────────────────────────────────────────────────────────
        private void BuildFooter()
        {
            _footerBorder.SetResourceReference(Border.PaddingProperty,    "LemoineTh_FooterPad");
            _footerBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            _footerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            _statusText = new TextBlock
            {
                Text          = "",
                VerticalAlignment = VerticalAlignment.Center,
                FontStyle     = FontStyles.Italic,
            };
            _statusText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _statusText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineGreen");
            _statusText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var closeBtn = LemoineControlStyles.BuildButton(
                "Close", LemoineControlStyles.LemoineButtonVariant.Ghost);
            closeBtn.Click += (s, ev) => Close();

            var btnStack = new StackPanel { Orientation = Orientation.Horizontal };
            btnStack.Children.Add(closeBtn);

            var dp = new DockPanel
            {
                LastChildFill = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(btnStack, Dock.Right);
            dp.Children.Add(btnStack);
            dp.Children.Add(_statusText);

            _footerBorder.Child = dp;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Main 3-column layout
        // ─────────────────────────────────────────────────────────────────────
        private void BuildMainLayout()
        {
            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });

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

            // Right panel
            var rightBorder = new Border { BorderThickness = new Thickness(1, 0, 0, 0) };
            rightBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            rightBorder.Child = BuildRightPanel();
            WpfGrid.SetColumn(rightBorder, 2);
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

            // Add Legend button at very bottom
            var addBtn = LemoineControlStyles.BuildButton(
                "＋  Add Legend", LemoineControlStyles.LemoineButtonVariant.Ghost);
            addBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            addBtn.Margin = new Thickness(8, 6, 8, 8);
            addBtn.Click += (s, e) => AddLegend();
            DockPanel.SetDock(addBtn, Dock.Bottom);
            dp.Children.Add(addBtn);

            var addSep = new Border { Height = 1 };
            addSep.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
            DockPanel.SetDock(addSep, Dock.Bottom);
            dp.Children.Add(addSep);

            // Create/Update button above the separator
            _createUpdateBtn = LemoineControlStyles.BuildButton(
                "Create Legend →", LemoineControlStyles.LemoineButtonVariant.Primary);
            _createUpdateBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            _createUpdateBtn.Margin = new Thickness(8, 6, 8, 0);
            _createUpdateBtn.Click += (s, e) => HandleCreateUpdate();
            DockPanel.SetDock(_createUpdateBtn, Dock.Bottom);
            dp.Children.Add(_createUpdateBtn);

            // Tab list (fills remaining space)
            _tabStack = new StackPanel { Orientation = Orientation.Vertical };
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            scroll.Content = _tabStack;
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

            // Guard for ComboBoxes that may have been rebuilt
            Dispatcher.BeginInvoke(new Action(() => Keyboard.ClearFocus()),
                DispatcherPriority.Input);
        }

        private Border BuildTabItem(LegendEntry entry, int index)
        {
            bool isActive = index == _activeIndex;

            var tab = new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                Cursor  = isActive ? Cursors.Arrow : Cursors.Hand,
            };

            if (isActive)
            {
                tab.BorderThickness = new Thickness(3, 0, 0, 1);
                tab.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                tab.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
            }
            else
            {
                tab.BorderThickness = new Thickness(0, 0, 0, 1);
                tab.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                tab.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            }

            var nameDisplay = new TextBlock
            {
                Text             = entry.GetDisplayName(),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming     = TextTrimming.CharacterEllipsis,
            };
            nameDisplay.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            nameDisplay.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            nameDisplay.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tab.Child = nameDisplay;

            if (!isActive)
            {
                tab.MouseEnter += (s, e) =>
                    tab.SetResourceReference(Border.BackgroundProperty, "LemoineSelectBg");
                tab.MouseLeave += (s, e) =>
                    tab.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            }

            int capturedIndex = index;
            tab.MouseLeftButtonDown += (s, e) =>
            {
                if (capturedIndex != _activeIndex)
                {
                    ActivateTab(capturedIndex);
                }
                else if (e.ClickCount == 2)
                {
                    ShowRenamePopup(tab, entry, nameDisplay);
                    e.Handled = true;
                }
            };

            return tab;
        }

        private void ShowRenamePopup(UIElement anchor, LegendEntry entry, TextBlock nameDisplay)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin      = new Thickness(8),
                MinWidth    = 180,
            };

            var lbl = new TextBlock { Text = "Name", Margin = new Thickness(0, 0, 0, 2) };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            panel.Children.Add(lbl);

            var nameBox = new TextBox
            {
                Text            = entry.GetDisplayName(),
                Margin          = new Thickness(0, 0, 0, 8),
                Padding         = new Thickness(6, 4, 6, 4),
                BorderThickness = new Thickness(1),
            };
            nameBox.SetResourceReference(TextBox.ForegroundProperty,    "LemoineText");
            nameBox.SetResourceReference(TextBox.FontFamilyProperty,    "LemoineUiFont");
            nameBox.SetResourceReference(TextBox.FontSizeProperty,      "LemoineFS_MD");
            nameBox.SetResourceReference(TextBox.BorderBrushProperty,   "LemoineBorder");
            nameBox.SetResourceReference(TextBox.BackgroundProperty,    "LemoineSelectBg");
            nameBox.SetResourceReference(TextBox.CaretBrushProperty,    "LemoineText");
            panel.Children.Add(nameBox);

            var popup = new Popup
            {
                PlacementTarget    = anchor,
                Placement          = PlacementMode.Bottom,
                StaysOpen          = true,
                AllowsTransparency = false,
            };

            var saveBtn = LemoineControlStyles.BuildButton(
                "Save", LemoineControlStyles.LemoineButtonVariant.Primary);
            saveBtn.Click += (s, e) =>
            {
                CommitRename(entry, nameBox.Text.Trim(), nameDisplay);
                popup.IsOpen = false;
            };
            panel.Children.Add(saveBtn);

            nameBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Return) { CommitRename(entry, nameBox.Text.Trim(), nameDisplay); popup.IsOpen = false; e.Handled = true; }
                else if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; }
            };

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

            Dispatcher.BeginInvoke(new Action(() => { nameBox.Focus(); nameBox.SelectAll(); }),
                DispatcherPriority.Input);
        }

        private void CommitRename(LegendEntry entry, string newName, TextBlock display)
        {
            if (string.IsNullOrWhiteSpace(newName))
                newName = entry.Layout?.Title ?? "Untitled";
            entry.DisplayName = newName == (entry.Layout?.Title ?? "") ? null : newName;
            display.Text = entry.GetDisplayName();
            LegendCreatorSettings.Instance.Save();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Right panel
        // ─────────────────────────────────────────────────────────────────────
        private UIElement BuildRightPanel()
        {
            var dp = new DockPanel { LastChildFill = true };

            _sizingSlot = new ContentControl();
            DockPanel.SetDock(_sizingSlot, Dock.Top);
            dp.Children.Add(_sizingSlot);

            _textStylesSlot = new ContentControl();
            DockPanel.SetDock(_textStylesSlot, Dock.Top);
            dp.Children.Add(_textStylesSlot);

            // PALETTE header
            var palHeader = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding         = new Thickness(10, 6, 10, 6),
            };
            palHeader.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            var palHeaderTb = new TextBlock { Text = "PALETTE" };
            palHeaderTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            palHeaderTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            palHeaderTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            palHeader.Child = palHeaderTb;
            DockPanel.SetDock(palHeader, Dock.Top);
            dp.Children.Add(palHeader);

            _paletteSlot = new ContentControl();
            _paletteSlot.Content = _palette;
            dp.Children.Add(_paletteSlot);

            return dp;
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
                oldBuilder.BulkEditorChanged -= OnBulkEditorChanged;

            // Get/create builder and wire up
            var builder = GetOrCreateBuilder(entry);
            builder.BulkEditorChanged += OnBulkEditorChanged;

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
            if (_createUpdateBtn == null) return;
            _createUpdateBtn.Content   = entry.RevitViewId != -1 ? "Update Legend →" : "Create Legend →";
            _createUpdateBtn.IsEnabled = true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Sizing section
        // ─────────────────────────────────────────────────────────────────────
        private UIElement BuildSizingSection(LemoineLegendBuilder builder)
        {
            var layout = builder.Layout;

            var sp = new StackPanel { Margin = new Thickness(10, 10, 10, 0) };

            var header = new TextBlock { Text = "SIZING", Margin = new Thickness(0, 0, 0, 8) };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            sp.Children.Add(header);

            var grid = new WpfGrid { Margin = new Thickness(0, 0, 0, 10) };
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

            sp.Children.Add(grid);

            var sep = new Border { Height = 1, Margin = new Thickness(-10, 0, -10, 0) };
            sep.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
            sp.Children.Add(sep);

            return sp;
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
            var sp = new StackPanel { Margin = new Thickness(10, 10, 10, 0) };

            var header = new TextBlock { Text = "TEXT STYLES", Margin = new Thickness(0, 0, 0, 8) };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            sp.Children.Add(header);

            if (_textTypes.Count == 0)
            {
                var noTypes = new TextBlock
                {
                    Text         = "No TextNoteTypes found in project.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 0, 0, 8),
                };
                noTypes.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                noTypes.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                noTypes.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                sp.Children.Add(noTypes);
            }
            else
            {
                var grid = new WpfGrid { Margin = new Thickness(0, 0, 0, 10) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int r = 0; r < 4; r++)
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                AddTextTypeRow(grid, 0, "TITLE",        entry.TitleTypeId,       id => entry.TitleTypeId       = id);
                AddTextTypeRow(grid, 1, "SUBTITLE",     entry.SubtitleTypeId,    id => entry.SubtitleTypeId    = id);
                AddTextTypeRow(grid, 2, "GROUP HEADER", entry.GroupHeaderTypeId, id => entry.GroupHeaderTypeId = id);
                AddTextTypeRow(grid, 3, "LABEL",        entry.LabelTypeId,       id => entry.LabelTypeId       = id);

                sp.Children.Add(grid);
            }

            var sep = new Border { Height = 1, Margin = new Thickness(-10, 0, -10, 0) };
            sep.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
            sp.Children.Add(sep);

            return sp;
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
                    if (_createUpdateBtn != null) _createUpdateBtn.IsEnabled = true;
                }));
            };

            App.LegendCreatorHandler.OnComplete = (pass, fail, skip) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_createUpdateBtn != null) _createUpdateBtn.IsEnabled = true;
                    if (fail > 0)          FlashStatus($"Completed with {fail} error(s).");
                    else if (isUpdate)     FlashStatus("Legend updated.");
                }));
            };

            if (_createUpdateBtn != null)
            {
                _createUpdateBtn.IsEnabled = false;
                _createUpdateBtn.Content   = isUpdate ? "Updating…" : "Creating…";
            }

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
        }

        // ─────────────────────────────────────────────────────────────────────
        // Status flash
        // ─────────────────────────────────────────────────────────────────────
        private void FlashStatus(string msg)
        {
            if (_statusText == null) return;
            _statusText.Text = msg;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (s, e) => { _statusText.Text = ""; timer.Stop(); };
            timer.Start();
        }
    }
}
