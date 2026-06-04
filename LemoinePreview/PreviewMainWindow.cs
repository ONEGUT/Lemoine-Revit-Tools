using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.Ceilings;
using LemoineTools.Tools.LinkViews;
using LemoineTools.Tools.BulkExport;
using LemoineTools.Tools.Testing;
using LemoineTools.Tools.Testing.LegendCreator;

namespace LemoineTools.Preview
{
    internal sealed class PreviewMainWindow : Window
    {
        // ── State & tracking ──────────────────────────────────────────────────
        private readonly PreviewState _state = PreviewState.Load();

        private string    _activeTabId = "controls";
        private UIElement? _controlsContent;
        private UIElement? _demoContent;
        private UIElement? _iconsContent;
        private UIElement? _settingsContent;

        private DemoTool? _demoTool;

        private Border _contentHost = null!;

        // Theme bar swatches / size pills
        private readonly List<(Border swatch, LemoineTheme theme)> _swatches  = new List<(Border, LemoineTheme)>();
        private readonly List<(Border pill,   LemoineUiSize size)> _sizePills = new List<(Border, LemoineUiSize)>();
        private readonly Dictionary<string, Border>                _tabPills  = new Dictionary<string, Border>();

        // Settings tab — rail state + live-refresh rows
        private string  _settingsActiveTabId = "general";
        private readonly Dictionary<string, Border>              _settingsRailBtns  = new Dictionary<string, Border>();
        private readonly List<(Border row, LemoineTheme theme)>  _settingsThemeRows = new List<(Border, LemoineTheme)>();
        private readonly List<(Border row, LemoineUiSize size)>  _settingsSizeRows  = new List<(Border, LemoineUiSize)>();
        private Border? _settingsContentBorder;

        // The icon font must point to THIS assembly, not LemoineTools
        private static readonly FontFamily _iconFont = new FontFamily(
            new Uri("pack://application:,,,/LemoinePreview;component/"),
            "./Source/Resources/Fonts/IcoMoonFree.ttf#IcoMoon-Free");

        // ── Constructor ───────────────────────────────────────────────────────
        public PreviewMainWindow()
        {
            Width  = 1280;
            Height = 720;
            MinWidth  = 960;
            MinHeight = 540;
            WindowStyle = WindowStyle.None;
            ResizeMode  = ResizeMode.CanResizeWithGrip;
            AllowsTransparency = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Title = "Lemoine Preview";

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight         = 0,
                ResizeBorderThickness = new Thickness(4),
                GlassFrameThickness   = new Thickness(0),
                UseAeroCaptionButtons = false,
            });

            RefreshResources();
            LemoineControlStyles.InjectInto(Resources, 8);

            LemoineSettings.Instance.ThemeChanged  += _ => RefreshResources();
            LemoineSettings.Instance.UiSizeChanged += _ => RefreshResources();

            BuildWindow();

            Loaded += (_, _) => ShowTab("controls");
        }

        private DemoTool GetDemoTool() => _demoTool ??= new DemoTool(_state);

        private void RefreshResources()
        {
            LemoineSettings.Instance.ApplyTo(Resources);
            Resources["LemoineIconFont"] = _iconFont;
            UpdateSwatchHighlights();
            UpdateSizePillHighlights();
            RefreshSettingsRows();
        }

        // ── Window structure ──────────────────────────────────────────────────
        private void BuildWindow()
        {
            var outer = new Border();
            outer.SetResourceReference(Border.BackgroundProperty, "LemoineBg");

            var outerBorder = new Border { BorderThickness = new Thickness(1) };
            outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            outerBorder.Child = outer;

            var dock = new DockPanel();
            outer.Child = dock;

            var header   = BuildHeader();
            var themeBar = BuildThemeBar();
            var tabBar   = BuildTabBar();

            DockPanel.SetDock(header,   Dock.Top);
            DockPanel.SetDock(themeBar, Dock.Top);
            DockPanel.SetDock(tabBar,   Dock.Top);

            dock.Children.Add(header);
            dock.Children.Add(themeBar);
            dock.Children.Add(tabBar);

            _contentHost = new Border();
            _contentHost.SetResourceReference(Border.BackgroundProperty, "LemoinePageBg");
            dock.Children.Add(_contentHost);

            Content = outerBorder;
        }

        // ── Header ────────────────────────────────────────────────────────────
        private FrameworkElement BuildHeader()
        {
            var header = new Border { Height = 38 };
            header.SetResourceReference(Border.BackgroundProperty, "LemoineSurface");

            var dp = new DockPanel { Margin = new Thickness(14, 0, 6, 0), LastChildFill = true };
            header.Child = dp;

            header.MouseLeftButtonDown += (_, e) => { if (e.ClickCount >= 1) DragMove(); };

            var closeBtn = LemoineControlStyles.BuildSmallButton("✕", LemoineControlStyles.LemoineButtonVariant.Ghost);
            closeBtn.VerticalAlignment = VerticalAlignment.Center;
            closeBtn.Click += (_, _) => Close();
            DockPanel.SetDock(closeBtn, Dock.Right);
            dp.Children.Add(closeBtn);

            var minBtn = LemoineControlStyles.BuildSmallButton("─", LemoineControlStyles.LemoineButtonVariant.Ghost);
            minBtn.VerticalAlignment = VerticalAlignment.Center;
            minBtn.Click += (_, _) => WindowState = WindowState.Minimized;
            DockPanel.SetDock(minBtn, Dock.Right);
            dp.Children.Add(minBtn);

            var icon = LemoineIcons.Build(LemoineIcon.PaintFormat, "LemoineFS_SM");
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 6, 0);

            var title = new TextBlock
            {
                Text              = "Lemoine Preview",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight        = FontWeights.SemiBold,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            title.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            title.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titleRow.Children.Add(icon);
            titleRow.Children.Add(title);
            dp.Children.Add(titleRow);

            return header;
        }

        // ── Theme + scale bar ─────────────────────────────────────────────────
        private FrameworkElement BuildThemeBar()
        {
            var bar = new Border { Padding = new Thickness(12, 5, 12, 5) };
            bar.SetResourceReference(Border.BackgroundProperty, "LemoineSurface");

            var sep = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
            sep.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            sep.Child = bar;

            var dp = new DockPanel();
            bar.Child = dp;

            var scaleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(scaleRow, Dock.Right);
            scaleRow.Children.Add(MakeBarLabel("Scale:"));

            foreach (var sz in new[] { LemoineUiSize.Small, LemoineUiSize.Medium, LemoineUiSize.Large, LemoineUiSize.ExtraLarge })
            {
                var captured = sz;
                var label    = sz == LemoineUiSize.Small       ? "S"  :
                               sz == LemoineUiSize.Medium      ? "M"  :
                               sz == LemoineUiSize.Large       ? "L"  : "XL";
                var pill     = MakePill(label);
                pill.Margin  = new Thickness(3, 0, 0, 0);
                pill.Cursor  = Cursors.Hand;
                pill.MouseLeftButtonUp += (_, _) => LemoineSettings.Instance.SetUiSize(captured);
                _sizePills.Add((pill, sz));
                scaleRow.Children.Add(pill);
            }
            dp.Children.Add(scaleRow);

            var themeRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            themeRow.Children.Add(MakeBarLabel("Theme:"));

            foreach (var theme in LemoineTheme.All)
            {
                var t      = theme;
                var swatch = new Border
                {
                    Width        = 16,
                    Height       = 16,
                    Background   = t.Swatch,
                    CornerRadius = new CornerRadius(3),
                    Margin       = new Thickness(3, 0, 0, 0),
                    Cursor       = Cursors.Hand,
                    ToolTip      = t.Name,
                };
                swatch.MouseLeftButtonUp += (_, _) => LemoineSettings.Instance.SetTheme(t);
                _swatches.Add((swatch, t));
                themeRow.Children.Add(swatch);
            }
            dp.Children.Add(themeRow);

            UpdateSwatchHighlights();
            UpdateSizePillHighlights();

            return sep;
        }

        private void UpdateSwatchHighlights()
        {
            var active = LemoineSettings.Instance.ActiveTheme;
            foreach (var (sw, th) in _swatches)
            {
                sw.BorderThickness = th == active ? new Thickness(2) : new Thickness(0);
                sw.BorderBrush     = th == active ? Brushes.White    : null;
            }
        }

        private void UpdateSizePillHighlights()
        {
            var active = LemoineSettings.Instance.UiSize;
            foreach (var (pill, sz) in _sizePills)
            {
                if (sz == active)
                    pill.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
                else
                    pill.Background = Brushes.Transparent;
            }
        }

        // ── Tab bar ───────────────────────────────────────────────────────────
        private FrameworkElement BuildTabBar()
        {
            var bar = new Border { Padding = new Thickness(12, 0, 12, 0) };
            bar.SetResourceReference(Border.BackgroundProperty, "LemoineSurface");

            var sep = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
            sep.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            sep.Child = bar;

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            bar.Child = row;

            foreach (var (id, label, icon) in new[]
            {
                ("controls", "Controls Gallery", LemoineIcon.Menu3),
                ("demo",     "Demo Tool",        LemoineIcon.Play),
                ("icons",    "Icons",            LemoineIcon.StarFull),
                ("settings", "Settings",         LemoineIcon.Cog),
            })
            {
                var tabId   = id;
                var tabPill = BuildTabPill(label, icon);
                tabPill.MouseLeftButtonUp += (_, _) => ShowTab(tabId);
                _tabPills[id] = tabPill;
                row.Children.Add(tabPill);
            }

            return sep;
        }

        private Border BuildTabPill(string label, LemoineIcon icon)
        {
            var pill = new Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                Cursor  = Cursors.Hand,
            };

            var iconTb = LemoineIcons.Build(icon, "LemoineFS_SM");
            iconTb.VerticalAlignment = VerticalAlignment.Center;
            iconTb.Margin = new Thickness(0, 0, 5, 0);

            var labelTb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            labelTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            labelTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            labelTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            labelTb.Text = label;

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(iconTb);
            row.Children.Add(labelTb);
            pill.Child = row;

            return pill;
        }

        private void ShowTab(string tabId)
        {
            _activeTabId = tabId;

            foreach (var pair in _tabPills)
            {
                var isActive = pair.Key == tabId;
                pair.Value.BorderThickness = isActive ? new Thickness(0, 0, 0, 2) : new Thickness(0);
                if (isActive)
                    pair.Value.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                else
                    pair.Value.BorderBrush = null;

                var labelTb = ((StackPanel)pair.Value.Child).Children.OfType<TextBlock>().FirstOrDefault();
                if (labelTb != null)
                    labelTb.SetResourceReference(TextBlock.ForegroundProperty,
                        isActive ? "LemoineText" : "LemoineTextSub");
            }

            _contentHost.Child = tabId switch
            {
                "demo"     => GetDemoContent(),
                "icons"    => GetIconsContent(),
                "settings" => GetSettingsContent(),
                _          => GetControlsContent(),
            };
        }

        // ══════════════════════════════════════════════════════════════════════
        // CONTROLS GALLERY TAB
        // ══════════════════════════════════════════════════════════════════════
        private UIElement GetControlsContent() => _controlsContent ??= BuildControlsPanel();

        private UIElement BuildControlsPanel()
        {
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            };

            var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
            scroll.Content = stack;

            stack.Children.Add(MakeGalleryHeader("Controls Gallery"));
            stack.Children.Add(BuildButtonsSection());
            stack.Children.Add(BuildInlineEditSection());
            stack.Children.Add(BuildSelectionsSection());
            stack.Children.Add(BuildMultiAndTagsSection());
            stack.Children.Add(BuildNumbersSection());
            stack.Children.Add(BuildDateSection());
            stack.Children.Add(BuildTogglesSection());
            stack.Children.Add(BuildFileBrowserSection());
            stack.Children.Add(BuildColorSection());
            stack.Children.Add(BuildMatrixSection());
            stack.Children.Add(BuildChipsAndBannerSection());

            return scroll;
        }

        // ── Buttons ───────────────────────────────────────────────────────────
        private UIElement BuildButtonsSection()
        {
            var content = new StackPanel();

            var normalRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            foreach (var (label, variant) in new[]
            {
                ("Ghost",   LemoineControlStyles.LemoineButtonVariant.Ghost),
                ("Primary", LemoineControlStyles.LemoineButtonVariant.Primary),
                ("Danger",  LemoineControlStyles.LemoineButtonVariant.Danger),
            })
            {
                var btn = LemoineControlStyles.BuildButton(label, variant);
                btn.Margin = new Thickness(0, 0, 8, 0);
                normalRow.Children.Add(btn);
            }
            content.Children.Add(MakeSubLabel("Normal"));
            content.Children.Add(normalRow);

            var smallRow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (label, variant) in new[]
            {
                ("Ghost",   LemoineControlStyles.LemoineButtonVariant.Ghost),
                ("Primary", LemoineControlStyles.LemoineButtonVariant.Primary),
                ("Danger",  LemoineControlStyles.LemoineButtonVariant.Danger),
            })
            {
                var btn = LemoineControlStyles.BuildSmallButton(label, variant);
                btn.Margin = new Thickness(0, 0, 8, 0);
                smallRow.Children.Add(btn);
            }
            content.Children.Add(MakeSubLabel("Small"));
            content.Children.Add(smallRow);

            return MakeSection("Buttons", content);
        }

        // ── Inline Edit ───────────────────────────────────────────────────────
        private UIElement BuildInlineEditSection()
        {
            var content = new StackPanel();
            var valueTb = MakeValueLabel($"Committed: \"{_state.InlineText}\"");

            var edit = new LemoineInlineEdit
            {
                Text        = _state.InlineText,
                Placeholder = "Click to edit, press Enter to commit…",
            };
            edit.TextCommitted += (_, newText) =>
            {
                _state.InlineText = newText;
                _state.Save();
                valueTb.Text = $"Committed: \"{newText}\"";
            };

            content.Children.Add(edit);
            content.Children.Add(valueTb);
            return MakeSection("Inline Edit  —  LemoineInlineEdit", content);
        }

        // ── Single Select + Search ────────────────────────────────────────────
        private UIElement BuildSelectionsSection()
        {
            var disciplines = new List<string>
                { "Mechanical", "Plumbing", "Electrical", "Structural", "Civil", "Landscape", "Interiors" };

            var content = new StackPanel();

            var ssValue = MakeValueLabel($"Selected: {_state.SingleSelect}");
            ssValue.Margin = new Thickness(0, 6, 0, 12);
            var ss = new LemoineSingleSelect
            {
                Label        = "Discipline",
                Items        = disciplines,
                SelectedItem = _state.SingleSelect,
            };
            ss.SelectionChanged += val =>
            {
                if (val == null) return;
                _state.SingleSelect = val;
                _state.Save();
                ssValue.Text = $"Selected: {val}";
            };
            content.Children.Add(MakeSubLabel("Single Select"));
            content.Children.Add(ss);
            content.Children.Add(ssValue);

            var rooms = new List<string>
            {
                "Room 101 — Reception", "Room 102 — Office A", "Room 103 — Office B",
                "Room 201 — Conference", "Room 202 — Break Room", "Room 301 — Server Room",
                "Corridor A", "Corridor B", "Stairwell 1", "Stairwell 2",
            };

            var saValue = MakeValueLabel(_state.SearchValue.Length > 0 ? $"Selected: {_state.SearchValue}" : "Nothing selected");
            var sa = new LemoineSearchAutocomplete
            {
                Items       = rooms,
                Placeholder = "Search rooms…",
                Value       = _state.SearchValue,
            };
            sa.SelectionChanged += val =>
            {
                _state.SearchValue = val;
                _state.Save();
                saValue.Text = $"Selected: {val}";
            };
            content.Children.Add(MakeSubLabel("Search Autocomplete"));
            content.Children.Add(sa);
            content.Children.Add(saValue);

            return MakeSection("Selections", content);
        }

        // ── Multi-Select + Tag Input ──────────────────────────────────────────
        private UIElement BuildMultiAndTagsSection()
        {
            var content = new StackPanel();

            var groups = new Dictionary<string, List<string>>
            {
                ["Floor Levels"] = new List<string> { "Level 1", "Level 2", "Level 3", "Level 4", "Roof" },
                ["Below Grade"]  = new List<string> { "B1 Basement", "B2 Sub-Basement" },
                ["Mezzanines"]   = new List<string> { "Mezzanine A", "Mezzanine B" },
            };

            var mtValue = MakeValueLabel();
            mtValue.Margin = new Thickness(0, 6, 0, 14);
            var mt = new LemoineMultiSelectTabs();
            mt.SetGroups(groups, _state.SelectedMultiList);
            mt.SelectionChanged += sel =>
            {
                _state.SelectedMulti = string.Join(",", sel);
                _state.Save();
                mtValue.Text = $"{sel.Count} selected: {string.Join(", ", sel.Take(3))}{(sel.Count > 3 ? "…" : "")}";
            };
            mtValue.Text = $"{_state.SelectedMultiList.Count} selected";

            content.Children.Add(MakeSubLabel("Multi-Select Tabs"));
            content.Children.Add(mt);
            content.Children.Add(mtValue);

            var availableTags = new List<string>
            {
                "Mechanical", "Plumbing", "Electrical", "Structural",
                "Civil", "Fire Protection", "Landscape", "Interiors", "Facade",
            };

            var chipValue = MakeValueLabel($"Tags: {_state.SelectedTags}");
            var chips = new LemoineTagChipInput
            {
                ItemsSource   = availableTags,
                SelectedItems = new ObservableCollection<string>(_state.SelectedTagList),
                AllowFreeText = true,
                Placeholder   = "Add discipline tags…",
            };
            chips.Changed += (_, _) =>
            {
                var selected = chips.SelectedItems?.ToList() ?? new List<string>();
                _state.SelectedTags = string.Join(",", selected);
                _state.Save();
                chipValue.Text = $"Tags: {string.Join(", ", selected)}";
            };

            content.Children.Add(MakeSubLabel("Tag Chip Input"));
            content.Children.Add(chips);
            content.Children.Add(chipValue);

            return MakeSection("Multi-Select + Tags", content);
        }

        // ── Numbers ───────────────────────────────────────────────────────────
        private UIElement BuildNumbersSection()
        {
            var content = new StackPanel();

            var stepperValue = MakeValueLabel($"Value: {_state.StepperValue}");
            stepperValue.Margin = new Thickness(0, 6, 0, 12);
            var stepper = new LemoineNumberStepper
            {
                Value    = _state.StepperValue,
                MinValue = 0,
                MaxValue = 20,
                Step     = 1,
            };
            stepper.ValueChanged += (_, v) =>
            {
                _state.StepperValue = v;
                _state.Save();
                stepperValue.Text = $"Value: {v}";
            };
            content.Children.Add(MakeSubLabel("Number Stepper"));
            content.Children.Add(stepper);
            content.Children.Add(stepperValue);

            var rangeValue = MakeValueLabel($"Range: {_state.RangeMin} – {_state.RangeMax}");
            var range = new LemoineNumberRange
            {
                MinLabel = "Min area",
                MaxLabel = "Max area",
                Unit     = "m²",
                Step     = 10,
                AbsMin   = 0,
                AbsMax   = 2000,
            };
            range.SetValues(_state.RangeMin, _state.RangeMax);
            range.RangeChanged += (min, max) =>
            {
                _state.RangeMin = min ?? 0;
                _state.RangeMax = max ?? 2000;
                _state.Save();
                rangeValue.Text = $"Range: {min} – {max} m²";
            };
            content.Children.Add(MakeSubLabel("Number Range"));
            content.Children.Add(range);
            content.Children.Add(rangeValue);

            return MakeSection("Numbers", content);
        }

        // ── Date Picker ───────────────────────────────────────────────────────
        private UIElement BuildDateSection()
        {
            var content   = new StackPanel();
            var dateValue = MakeValueLabel(_state.DateFrom.Length > 0
                ? $"From: {_state.DateFrom}  To: {_state.DateTo}"
                : "No dates set");

            var picker = new LemoineDatePicker
            {
                Mode  = LemoineDatePicker.PickerMode.Range,
                Label = "Project period",
            };
            picker.SetDates(_state.DateFrom, _state.DateTo);
            picker.DateChanged += (from, to) =>
            {
                _state.DateFrom = from ?? "";
                _state.DateTo   = to  ?? "";
                _state.Save();
                dateValue.Text = $"From: {from}  To: {to}";
            };

            content.Children.Add(picker);
            content.Children.Add(dateValue);
            return MakeSection("Date Picker  —  Range mode", content);
        }

        // ── Toggle Switches ───────────────────────────────────────────────────
        private UIElement BuildTogglesSection()
        {
            var content     = new StackPanel();
            var toggleValue = MakeValueLabel();

            var items = new List<ToggleItem>
            {
                new ToggleItem { Id = "show_dims",  Label = "Show dimensions",   Desc = "Display dimension annotations on selected elements", DefaultOn = true  },
                new ToggleItem { Id = "show_tags",  Label = "Show element tags",  Desc = "Render identity tags on all tagged elements",         DefaultOn = false },
                new ToggleItem { Id = "halftone",   Label = "Halftone linked",    Desc = "Apply halftone overrides to all linked models",       DefaultOn = true  },
                new ToggleItem { Id = "hide_grids", Label = "Hide grids in view", Desc = "Suppress grid lines in the current view",             DefaultOn = false },
            };

            var toggles = new LemoineToggleSwitches();
            toggles.SetItems(items, _state.ToggleStateDict);
            toggles.StateChanged += s =>
            {
                _state.ToggleState = PreviewState.FormatToggles(s);
                _state.Save();
                var on = s.Where(kv => kv.Value).Select(kv => kv.Key);
                toggleValue.Text = $"On: {string.Join(", ", on)}";
            };

            var initial = _state.ToggleStateDict;
            toggleValue.Text = "On: " + string.Join(", ", initial.Where(kv => kv.Value).Select(kv => kv.Key));

            content.Children.Add(toggles);
            content.Children.Add(toggleValue);
            return MakeSection("Toggle Switches", content);
        }

        // ── File Browser ──────────────────────────────────────────────────────
        private UIElement BuildFileBrowserSection()
        {
            var content = new StackPanel();
            var fbValue = MakeValueLabel(_state.FilePath.Length > 0 ? _state.FilePath : "No file selected");

            var fb = new LemoineFileBrowser
            {
                Label       = "Linked CAD/BIM file",
                Placeholder = "Browse for a file…",
                Filter      = "All files|*.*|DWG|*.dwg|IFC|*.ifc|NWC|*.nwc",
                Path        = _state.FilePath,
            };
            fb.PathChanged += path =>
            {
                _state.FilePath = path;
                _state.Save();
                fbValue.Text = path.Length > 0 ? path : "No file selected";
            };

            content.Children.Add(fb);
            content.Children.Add(fbValue);
            return MakeSection("File Browser", content);
        }

        // ── Color Section ─────────────────────────────────────────────────────
        private UIElement BuildColorSection()
        {
            var content    = new StackPanel();
            var colorValue = MakeValueLabel($"Selected: {_state.ColorHex}");
            colorValue.Margin = new Thickness(0, 6, 0, 14);

            var initial = BrushHelper.ColorFromHex(_state.ColorHex, Colors.CornflowerBlue);
            var picker  = new LemoineColorPickerPanel();
            picker.SelectedColor = initial;
            picker.ColorChanged += (_, c) =>
            {
                _state.ColorHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                _state.Save();
                colorValue.Text = $"Selected: {_state.ColorHex}";
                picker.AddToRecent(c);
            };
            content.Children.Add(MakeSubLabel("Color Picker Panel"));
            content.Children.Add(picker);
            content.Children.Add(colorValue);

            var swatchValue = MakeValueLabel($"Kind: {_state.SwatchKind}  Fill: {_state.SwatchFill}");
            var sp = new LemoineSwatchPicker
            {
                Kind           = _state.SwatchKind,
                Fill           = _state.SwatchFill,
                Title          = "Legend swatch style",
                AllowColorPick = false,
            };
            sp.SelectionChanged += (_, e) =>
            {
                _state.SwatchKind = e.Kind;
                _state.SwatchFill = e.Fill;
                _state.Save();
                swatchValue.Text = $"Kind: {e.Kind}  Fill: {e.Fill}";
            };
            content.Children.Add(MakeSubLabel("Swatch Picker"));
            content.Children.Add(sp);
            content.Children.Add(swatchValue);

            return MakeSection("Color Controls", content);
        }

        // ── Matrix Input ──────────────────────────────────────────────────────
        private UIElement BuildMatrixSection()
        {
            var content = new StackPanel();
            var mxValue = MakeValueLabel();

            var rows = new List<string> { "Foundation", "Framing", "Enclosure" };
            var cols = new List<string> { "Design", "Construction", "Commissioning" };

            var mx = new LemoineMatrixInput();
            mx.SetMatrix(rows, cols, _state.MatrixDict);
            mx.ValueChanged += d =>
            {
                _state.MatrixValues = PreviewState.FormatMatrix(d);
                _state.Save();
                mxValue.Text = $"{d.Count(kv => !string.IsNullOrEmpty(kv.Value))} cells filled";
            };

            content.Children.Add(mx);
            content.Children.Add(mxValue);
            return MakeSection("Matrix Input", content);
        }

        // ── Chips + Warn Banner ───────────────────────────────────────────────
        private UIElement BuildChipsAndBannerSection()
        {
            var content = new StackPanel();

            content.Children.Add(MakeSubLabel("Category Chips"));
            var chipRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            chipRow.HorizontalAlignment = HorizontalAlignment.Left;

            foreach (var (label, active) in new[]
            {
                ("All Elements", true), ("Walls", false), ("Floors", true),
                ("Ceilings", false),    ("Roofs", false),
            })
            {
                var chip = new LemoineCategoryChip
                {
                    Label    = label,
                    Active   = active,
                    ShowChev = false,
                    Margin   = new Thickness(0, 0, 6, 6),
                };
                chip.Clicked += (_, _) => chip.Active = !chip.Active;
                chipRow.Children.Add(chip);
            }
            content.Children.Add(chipRow);

            content.Children.Add(MakeSubLabel("Warn Banner"));
            var banner = new LemoineWarnBanner(
                "This is a preview environment — no Revit document is open. " +
                "Controls are wired to PreviewState.xml in %AppData%\\LemoineTools\\.",
                bottomMargin: 0);
            content.Children.Add(banner);

            return MakeSection("Chips + Banners", content);
        }

        // ══════════════════════════════════════════════════════════════════════
        // DEMO TOOL TAB
        // ══════════════════════════════════════════════════════════════════════
        private UIElement GetDemoContent() => _demoContent ??= BuildDemoPanel();

        private UIElement BuildDemoPanel()
        {
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            };

            var outer = new StackPanel
            {
                Margin              = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Top,
            };
            scroll.Content = outer;

            var header = new Border { Padding = new Thickness(32, 28, 32, 24) };
            header.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            outer.Children.Add(header);

            var hdrStack = new StackPanel();
            header.Child = hdrStack;

            var titleIcon = LemoineIcons.Build(LemoineIcon.Office, "LemoineFS_XL");
            titleIcon.Margin = new Thickness(0, 0, 0, 8);

            var titleTb = new TextBlock
            {
                Text       = "Room Tagger",
                FontWeight = FontWeights.Bold,
                Margin     = new Thickness(0, 0, 0, 6),
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            titleTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XL");
            titleTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var descTb = new TextBlock
            {
                Text         = "Demo implementation of ILemoineTool + ILemoineToolSettings.\n" +
                               "All step content, validation, and settings are live.\n" +
                               "The run simulates progress — no Revit API is involved.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth     = 560,
            };
            descTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            descTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            descTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            hdrStack.Children.Add(titleIcon);
            hdrStack.Children.Add(titleTb);
            hdrStack.Children.Add(descTb);

            var actionArea = new Border { Padding = new Thickness(32, 24, 32, 32) };
            outer.Children.Add(actionArea);

            var actionStack = new StackPanel();
            actionArea.Child = actionStack;

            var infoItems = new[]
            {
                (LemoineIcon.List,      "3 steps: file → levels → tag options"),
                (LemoineIcon.Checkmark, "Validation: levels required, file optional"),
                (LemoineIcon.Info,      "Settings panel exposed via ILemoineToolSettings"),
                (LemoineIcon.Play,      "Run simulates progress + log entries"),
            };

            foreach (var (icon, text) in infoItems)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                var ic  = LemoineIcons.Build(icon, "LemoineFS_SM", "LemoineAccent");
                ic.Margin = new Thickness(0, 1, 8, 0);
                var tb  = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
                tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
                tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                row.Children.Add(ic);
                row.Children.Add(tb);
                actionStack.Children.Add(row);
            }

            var openBtn = LemoineControlStyles.BuildButton("Open Tool Window →", LemoineControlStyles.LemoineButtonVariant.Primary);
            openBtn.Margin              = new Thickness(0, 12, 0, 0);
            openBtn.HorizontalAlignment = HorizontalAlignment.Left;
            openBtn.Click += (_, _) =>
            {
                var tool   = GetDemoTool();
                var window = new StepFlowWindow(tool);
                window.Owner = this;
                LemoineSettings.Instance.ApplyTo(window.Resources);
                window.Resources["LemoineIconFont"] = _iconFont;
                LemoineControlStyles.InjectInto(window.Resources, 8);
                window.ShowDialog();
            };
            actionStack.Children.Add(openBtn);

            var hint = new TextBlock
            {
                Text         = "Step values and settings are saved to PreviewState.xml — they persist across window opens.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 6, 0, 0),
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            hint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            hint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            actionStack.Children.Add(hint);

            return scroll;
        }

        // ══════════════════════════════════════════════════════════════════════
        // ICONS TAB
        // ══════════════════════════════════════════════════════════════════════
        private UIElement GetIconsContent() => _iconsContent ??= BuildIconsPanel();

        private UIElement BuildIconsPanel()
        {
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            };

            var outer = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
            scroll.Content = outer;

            outer.Children.Add(MakeGalleryHeader("Icons  —  LemoineIcons"));

            var subTb = new TextBlock
            {
                Text         = "IcoMoon Free (0xe900–0xeaeb). Use LemoineIcons.Build(LemoineIcon.X) to render any icon in your tools.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 20),
            };
            subTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            subTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            subTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(subTb);

            var categories = new[]
            {
                ("Navigation / Layout",
                    new[] { LemoineIcon.Home, LemoineIcon.Office, LemoineIcon.Menu, LemoineIcon.Menu2,
                            LemoineIcon.Menu3, LemoineIcon.Tree, LemoineIcon.List, LemoineIcon.ListNumbered,
                            LemoineIcon.Table, LemoineIcon.Table2 }),
                ("Editing / Tools",
                    new[] { LemoineIcon.Pencil, LemoineIcon.Pencil2, LemoineIcon.Pen, LemoineIcon.Eyedropper,
                            LemoineIcon.PaintFormat, LemoineIcon.Scissors, LemoineIcon.Crop,
                            LemoineIcon.MakeGroup, LemoineIcon.Ungroup, LemoineIcon.Filter }),
                ("Files / Media",
                    new[] { LemoineIcon.Image, LemoineIcon.Images, LemoineIcon.Camera, LemoineIcon.Attachment,
                            LemoineIcon.Clipboard, LemoineIcon.FilePdf, LemoineIcon.FileWord, LemoineIcon.FileExcel }),
                ("Cloud / Network",
                    new[] { LemoineIcon.Cloud, LemoineIcon.CloudDownload, LemoineIcon.CloudUpload,
                            LemoineIcon.CloudCheck, LemoineIcon.Download, LemoineIcon.Upload,
                            LemoineIcon.Link, LemoineIcon.Share, LemoineIcon.Share2, LemoineIcon.NewTab }),
                ("Status / Feedback",
                    new[] { LemoineIcon.Eye, LemoineIcon.EyeBlocked, LemoineIcon.Warning, LemoineIcon.Notification,
                            LemoineIcon.Question, LemoineIcon.Info, LemoineIcon.Cancel, LemoineIcon.Blocked,
                            LemoineIcon.Cross, LemoineIcon.Checkmark, LemoineIcon.Checkmark2, LemoineIcon.Flag,
                            LemoineIcon.Bookmark, LemoineIcon.Star, LemoineIcon.StarHalf, LemoineIcon.StarFull }),
                ("Actions",
                    new[] { LemoineIcon.Plus, LemoineIcon.Minus, LemoineIcon.Enter, LemoineIcon.Exit,
                            LemoineIcon.Bin, LemoineIcon.Bin2, LemoineIcon.Target, LemoineIcon.Shield,
                            LemoineIcon.Power, LemoineIcon.SpellCheck }),
                ("Playback / Process",
                    new[] { LemoineIcon.Play, LemoineIcon.Pause, LemoineIcon.Stop, LemoineIcon.Previous,
                            LemoineIcon.Next, LemoineIcon.Loop, LemoineIcon.Shuffle }),
                ("Sort / Arrows",
                    new[] { LemoineIcon.SortAlphaAsc, LemoineIcon.SortAlphaDesc, LemoineIcon.SortAmountAsc,
                            LemoineIcon.SortAmountDesc, LemoineIcon.ArrowUp, LemoineIcon.ArrowRight,
                            LemoineIcon.ArrowDown, LemoineIcon.ArrowLeft, LemoineIcon.ArrowUp2,
                            LemoineIcon.ArrowRight2, LemoineIcon.ArrowDown2, LemoineIcon.ArrowLeft2,
                            LemoineIcon.CircleUp, LemoineIcon.CircleRight, LemoineIcon.CircleDown, LemoineIcon.CircleLeft }),
                ("Form Controls",
                    new[] { LemoineIcon.CheckboxChecked, LemoineIcon.CheckboxUnchecked,
                            LemoineIcon.RadioChecked, LemoineIcon.RadioUnchecked }),
                ("Text",
                    new[] { LemoineIcon.Bold, LemoineIcon.Italic, LemoineIcon.Underline,
                            LemoineIcon.IndentIncrease, LemoineIcon.IndentDecrease }),
                ("Tools / Settings",
                    new[] { LemoineIcon.Wrench, LemoineIcon.Equalizer, LemoineIcon.Cog, LemoineIcon.Cogs }),
                ("Misc",
                    new[] { LemoineIcon.Newspaper, LemoineIcon.Droplet, LemoineIcon.Hammer, LemoineIcon.Lab,
                            LemoineIcon.Briefcase, LemoineIcon.Road, LemoineIcon.Terminal, LemoineIcon.Sun,
                            LemoineIcon.Mail }),
            };

            foreach (var (categoryName, icons) in categories)
                outer.Children.Add(MakeIconCategory(categoryName, icons));

            return scroll;
        }

        private UIElement MakeIconCategory(string name, IEnumerable<LemoineIcon> icons)
        {
            var section = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

            var hdr = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
            hdr.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            hdr.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            hdr.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            section.Children.Add(hdr);

            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };

            foreach (var icon in icons)
            {
                var card = new Border
                {
                    Width        = 80,
                    Height       = 68,
                    Margin       = new Thickness(0, 0, 6, 6),
                    Padding      = new Thickness(6, 10, 6, 8),
                    CornerRadius = new CornerRadius(4),
                    Cursor       = Cursors.Hand,
                    ToolTip      = icon.ToString(),
                };
                card.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
                card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "LemoineSelectBg");
                card.MouseLeave += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");

                var stack   = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                var iconTb  = LemoineIcons.Build(icon, "LemoineFS_LG");
                iconTb.HorizontalAlignment = HorizontalAlignment.Center;
                iconTb.Margin = new Thickness(0, 0, 0, 4);
                var nameTb = new TextBlock
                {
                    Text                = icon.ToString(),
                    TextWrapping        = TextWrapping.Wrap,
                    TextAlignment       = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                nameTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                nameTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
                nameTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                stack.Children.Add(iconTb);
                stack.Children.Add(nameTb);
                card.Child = stack;
                wrap.Children.Add(card);
            }

            section.Children.Add(wrap);
            return section;
        }

        // ══════════════════════════════════════════════════════════════════════
        // SETTINGS TAB  —  mirrors GlobalSettingsWindow layout exactly
        // ══════════════════════════════════════════════════════════════════════
        private UIElement GetSettingsContent() => _settingsContent ??= BuildSettingsPanel();

        private UIElement BuildSettingsPanel()
        {
            // Vertical layout: pill nav row on top, content below
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Top: pill navigation row
            var pillNavBorder = new Border { Padding = new Thickness(12, 6, 12, 6) };
            Grid.SetRow(pillNavBorder, 0);
            grid.Children.Add(pillNavBorder);

            // Bottom: content host
            _settingsContentBorder = new Border();
            Grid.SetRow(_settingsContentBorder, 1);
            grid.Children.Add(_settingsContentBorder);

            // Nav defs — mirrors GlobalSettingsWindow._navDefs
            var navDefs = new[]
            {
                ("general", "General"),
                ("filters", "Filters / Color"),
                ("t03",     "Ceiling Heatmap"),
                ("t04",     "Link Views"),
                ("t08",     "Legend Creator"),
                ("tx",      "Bulk Export"),
                ("tz",      "Create Sheets"),
            };
            const int pillNavVisible = 6;

            var pillPanel = new StackPanel { Orientation = Orientation.Horizontal };
            _settingsRailBtns.Clear();

            int vis = Math.Min(pillNavVisible, navDefs.Length);
            for (int i = 0; i < vis; i++)
            {
                var (id, label) = navDefs[i];
                var pill = BuildSettingsNavPill(id, label);
                _settingsRailBtns[id] = pill;
                pillPanel.Children.Add(pill);
            }

            if (navDefs.Length > vis)
            {
                int overflow = navDefs.Length - vis;
                var morePill = BuildSettingsMorePill($"More +{overflow} ▾", navDefs, pillNavVisible);
                pillPanel.Children.Add(morePill);
            }

            pillNavBorder.Child = pillPanel;

            SwitchSettingsTab(_settingsActiveTabId);
            return grid;
        }

        private Border BuildSettingsNavPill(string tabId, string label)
        {
            bool active = tabId == _settingsActiveTabId;

            var pill = new Border
            {
                Cursor          = Cursors.Hand,
                CornerRadius    = new CornerRadius(999),
                BorderThickness = new Thickness(1),
                Background      = Brushes.Transparent,
                Margin          = new Thickness(0, 0, 6, 0),
            };
            pill.SetResourceReference(Border.PaddingProperty, "LemoineTh_NavPillPad");
            if (active) pill.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            else        pill.BorderBrush = Brushes.Transparent;

            var lbl = new TextBlock
            {
                Text       = label,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, active ? "LemoineAccent" : "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            pill.Child = lbl;

            pill.MouseLeftButtonDown += (_, _) => SwitchSettingsTab(tabId);
            pill.MouseEnter += (_, _) =>
            {
                if (tabId != _settingsActiveTabId)
                    pill.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            };
            pill.MouseLeave += (_, _) =>
            {
                if (tabId != _settingsActiveTabId)
                    pill.BorderBrush = Brushes.Transparent;
            };
            return pill;
        }

        private Border BuildSettingsMorePill(string label,
            (string Id, string Label)[] navDefs, int pillNavVisible)
        {
            var pill = new Border
            {
                Cursor          = Cursors.Hand,
                CornerRadius    = new CornerRadius(999),
                BorderThickness = new Thickness(1),
                Background      = Brushes.Transparent,
                Margin          = new Thickness(0, 0, 6, 0),
            };
            pill.SetResourceReference(Border.PaddingProperty,     "LemoineTh_NavPillPad");
            pill.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var lbl = new TextBlock { Text = label };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            pill.Child = lbl;

            pill.MouseLeftButtonDown += (_, _) =>
            {
                var popup = new System.Windows.Controls.Primitives.Popup
                {
                    PlacementTarget    = pill,
                    Placement          = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                    StaysOpen          = false,
                    AllowsTransparency = false,
                };
                var outer = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(8),
                    Padding         = new Thickness(4),
                    Margin          = new Thickness(0, 2, 0, 0),
                };
                outer.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
                outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

                var stack = new StackPanel();
                for (int i = pillNavVisible; i < navDefs.Length; i++)
                {
                    var (tabId, tabLabel) = navDefs[i];
                    var captId = tabId;
                    var item = new Border
                    {
                        Cursor       = Cursors.Hand,
                        CornerRadius = new CornerRadius(4),
                        Padding      = new Thickness(12, 6, 12, 6),
                        Background   = Brushes.Transparent,
                    };
                    var itemLbl = new TextBlock { Text = tabLabel };
                    itemLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                    itemLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    itemLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    item.Child = itemLbl;
                    item.MouseLeftButtonDown += (s2, e2) => { popup.IsOpen = false; SwitchSettingsTab(captId); };
                    item.MouseEnter += (s2, e2) => item.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
                    item.MouseLeave += (s2, e2) => item.Background = Brushes.Transparent;
                    stack.Children.Add(item);
                }
                outer.Child = stack;
                popup.Child = outer;
                popup.IsOpen = true;
            };
            return pill;
        }

        private void SwitchSettingsTab(string tabId)
        {
            _settingsActiveTabId = tabId;

            foreach (var pair in _settingsRailBtns)
            {
                bool active = pair.Key == tabId;
                var pill = pair.Value;
                if (active) pill.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                else        pill.BorderBrush = Brushes.Transparent;

                if (pill.Child is TextBlock lbl)
                {
                    lbl.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                    lbl.SetResourceReference(TextBlock.ForegroundProperty,
                        active ? "LemoineAccent" : "LemoineText");
                }
            }

            UIElement content;
            switch (tabId)
            {
                case "general":  content = BuildGeneralContent();                                              break;
                case "filters":  content = BuildFiltersPlaceholder();                                         break;
                case "t03":      content = BuildSpecContent(BuildCeilingHeatmapProxy(), "Ceiling Heatmap");   break;
                case "t04":      content = BuildSpecContent(BuildLinkViewsProxy(),      "Link Views");        break;
                case "t08":      content = LegendCreatorTabContent.BuildContent(this);                        break;
                case "tx":       content = BuildSpecContent(BuildBulkExportProxy(),    "Bulk Export");      break;
                case "tz":       content = BuildSpecContent(BuildCreateSheetsProxy(),   "Create Sheets");     break;
                default:         content = BuildGeneralContent();                                             break;
            }

            if (_settingsContentBorder != null)
                _settingsContentBorder.Child = content;
        }

        // ── General tab (mirrors GlobalSettingsWindow.BuildGeneralContent) ────
        private UIElement BuildGeneralContent()
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };

            panel.Children.Add(GContentHeader("General"));

            panel.Children.Add(GSubLabel("Colour theme"));
            _settingsThemeRows.Clear();
            foreach (var t in LemoineTheme.All)
            {
                var row = BuildSettingsThemeRow(t);
                _settingsThemeRows.Add((row, t));
                panel.Children.Add(row);
            }

            panel.Children.Add(GHSep(12));

            panel.Children.Add(GSubLabel("UI size"));
            _settingsSizeRows.Clear();
            foreach (LemoineUiSize sz in Enum.GetValues(typeof(LemoineUiSize)))
            {
                var row = BuildSettingsSizeRow(sz);
                _settingsSizeRows.Add((row, sz));
                panel.Children.Add(row);
            }

            scroll.Content = panel;
            return scroll;
        }

        private Border BuildSettingsThemeRow(LemoineTheme theme)
        {
            bool active = theme == LemoineSettings.Instance.ActiveTheme;
            var row = GToggleRowShell(active);

            var swatch = new Border
            {
                Width             = 18,
                Height            = 18,
                CornerRadius      = new CornerRadius(9),
                Background        = theme.Swatch,
                BorderThickness   = new Thickness(2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 10, 0),
            };
            swatch.SetResourceReference(Border.BorderBrushProperty,
                active ? "LemoineAccent" : "LemoineBorderMid");

            var pill = GPill(active);

            var name = new TextBlock
            {
                Text              = theme.Name,
                FontWeight        = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
            name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var dp = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(swatch, Dock.Left);
            DockPanel.SetDock(pill,   Dock.Right);
            dp.Children.Add(swatch);
            dp.Children.Add(pill);
            dp.Children.Add(name);
            row.Child = dp;

            row.MouseLeftButtonDown += (_, _) =>
            {
                LemoineSettings.Instance.SetTheme(theme);
                RefreshSettingsThemeRows();
            };
            return row;
        }

        private Border BuildSettingsSizeRow(LemoineUiSize size)
        {
            bool active = size == LemoineSettings.Instance.UiSize;
            var row  = GToggleRowShell(active);
            var pill = GPill(active);

            var name = new TextBlock { Text = size.ToString(), FontWeight = FontWeights.Medium };
            name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
            name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var desc = new TextBlock
            {
                Text      = size == LemoineUiSize.Small       ? "Small — Compact"        :
                            size == LemoineUiSize.Large       ? "Large — Comfortable"    :
                            size == LemoineUiSize.ExtraLarge  ? "Extra Large — Spacious" : "Medium — Default",
                FontStyle = FontStyles.Italic,
                Margin    = new Thickness(0, 2, 0, 0),
            };
            desc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            desc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            desc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var inner = new StackPanel();
            inner.Children.Add(name);
            inner.Children.Add(desc);

            var dp = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(pill, Dock.Right);
            dp.Children.Add(pill);
            dp.Children.Add(inner);
            row.Child = dp;

            row.MouseLeftButtonDown += (_, _) =>
            {
                LemoineSettings.Instance.SetUiSize(size);
                RefreshSettingsSizeRows();
            };
            return row;
        }

        private void RefreshSettingsRows()
        {
            RefreshSettingsThemeRows();
            RefreshSettingsSizeRows();
        }

        private void RefreshSettingsThemeRows()
        {
            foreach (var (row, theme) in _settingsThemeRows)
            {
                bool active = theme == LemoineSettings.Instance.ActiveTheme;
                if (active) row.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
                else        row.Background = Brushes.Transparent;
                row.SetResourceReference(Border.BorderBrushProperty,
                    active ? "LemoineAccent" : "LemoineBorder");

                if (row.Child is DockPanel dp)
                {
                    if (dp.Children[0] is Border swatch)
                        swatch.SetResourceReference(Border.BorderBrushProperty,
                            active ? "LemoineAccent" : "LemoineBorderMid");
                    if (dp.Children[1] is Border pill)
                        GUpdatePill(pill, active);
                }
            }
        }

        private void RefreshSettingsSizeRows()
        {
            foreach (var (row, size) in _settingsSizeRows)
            {
                bool active = size == LemoineSettings.Instance.UiSize;
                if (active) row.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
                else        row.Background = Brushes.Transparent;
                row.SetResourceReference(Border.BorderBrushProperty,
                    active ? "LemoineAccent" : "LemoineBorder");

                if (row.Child is DockPanel dp && dp.Children[0] is Border pill)
                    GUpdatePill(pill, active);
            }
        }

        // ── Room Tagger settings tab ──────────────────────────────────────────
        // ── Shared spec renderer (mirrors GlobalSettingsWindow.CeilingHeatmap.cs) ─
        private UIElement BuildSpecContent(ILemoineToolSettings? vm, string tabLabel)
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            panel.Children.Add(GContentHeader(tabLabel));

            var spec = vm?.GetSettingsSpec();
            if (spec?.Groups == null || spec.Groups.Count == 0)
            {
                panel.Children.Add(GNoSettings());
                scroll.Content = panel;
                return scroll;
            }

            foreach (var group in spec.Groups)
            {
                if (!string.IsNullOrEmpty(group.Title))
                    panel.Children.Add(GSubLabel(group.Title));

                if (!string.IsNullOrEmpty(group.Hint))
                {
                    var hint = new TextBlock { Text = group.Hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
                    hint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    hint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    hint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                    panel.Children.Add(hint);
                }

                foreach (var setting in group.Settings)
                {
                    if (!string.IsNullOrEmpty(setting.Label))
                    {
                        var lbl = new TextBlock { Text = setting.Label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
                        lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                        lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                        lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                        panel.Children.Add(lbl);
                    }

                    if (!string.IsNullOrEmpty(setting.Hint))
                    {
                        var sh = new TextBlock { Text = setting.Hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
                        sh.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                        sh.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                        sh.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                        panel.Children.Add(sh);
                    }

                    var ctrl = BuildSettingControl(setting, group.Id, vm!);
                    if (ctrl != null) panel.Children.Add(ctrl);
                    panel.Children.Add(new Rectangle { Height = 10 });
                }

                panel.Children.Add(GHSep(8));
            }

            scroll.Content = panel;
            return scroll;
        }

        private UIElement BuildFiltersPlaceholder()
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            panel.Children.Add(GContentHeader("Filters / Color"));

            var info = new TextBlock
            {
                Text         = "The Filters / Color tab hosts a full drag-and-drop trade → rule editor " +
                               "that is tightly coupled to the live Revit document (category IDs, " +
                               "fill-pattern names, etc.). It is not renderable in the standalone preview.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 12),
                FontStyle    = FontStyles.Italic,
            };
            info.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            info.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            info.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            panel.Children.Add(info);

            scroll.Content = panel;
            return scroll;
        }

        // ── Proxy builders: replicate each ViewModel's GetSettingsSpec + ApplySettings ─

        private ILemoineToolSettings BuildCeilingHeatmapProxy()
        {
            var s = CeilingHeatmapSettings.Instance;
            return new SpecProxy(
                new LemoineToolSettingsSpec
                {
                    Id = "T03", Label = "Ceiling Heatmap", Icon = "03",
                    Description = "Color ramp, elevation tolerance, and tag settings.",
                    Groups = new List<LemoineSettingsGroup>
                    {
                        new LemoineSettingsGroup
                        {
                            Id = "G1", Title = "Color Ramp", OpenByDefault = true,
                            Hint = "Three-stop gradient from lowest to highest ceiling elevation offset.",
                            Settings = new List<LemoineSettingDef>
                            {
                                new LemoineSettingDef { Id="colorLow",  Kind="color", Label="Low color",  Default=s.ColorLow  },
                                new LemoineSettingDef { Id="colorMid",  Kind="color", Label="Mid color",  Default=s.ColorMid  },
                                new LemoineSettingDef { Id="colorHigh", Kind="color", Label="High color", Default=s.ColorHigh },
                            }
                        },
                        new LemoineSettingsGroup
                        {
                            Id = "G2", Title = "Detection",
                            Settings = new List<LemoineSettingDef>
                            {
                                new LemoineSettingDef { Id="elevTolerance", Kind="number", Label="Elevation tolerance",
                                    Hint="Ceilings within this tolerance (inches) share one filter bucket.",
                                    Options=new NumberOpts { Unit="in", Min=0.01, Max=12.0, Step=0.01 },
                                    Default=Math.Round(s.ElevTolerance * 12.0, 4) },
                                new LemoineSettingDef { Id="includeLinks", Kind="toggle", Label="Include linked ceilings",
                                    Hint="Scan Revit link instances visible in each selected view.", Default=s.IncludeLinks },
                                new LemoineSettingDef { Id="placeTags", Kind="toggle", Label="Place ceiling tags",
                                    Hint="Place a Ceiling Tag at the centroid of each ceiling.", Default=s.PlaceTags },
                            }
                        },
                    }
                },
                (groupId, sid, val) =>
                {
                    if (groupId == "G1")
                    {
                        if (sid == "colorLow")  s.ColorLow  = val as string ?? s.ColorLow;
                        if (sid == "colorMid")  s.ColorMid  = val as string ?? s.ColorMid;
                        if (sid == "colorHigh") s.ColorHigh = val as string ?? s.ColorHigh;
                    }
                    else if (groupId == "G2")
                    {
                        if (sid == "elevTolerance" && val is double tol) s.ElevTolerance = tol / 12.0;
                        if (sid == "includeLinks"  && val is bool links) s.IncludeLinks  = links;
                        if (sid == "placeTags"     && val is bool tags)  s.PlaceTags     = tags;
                    }
                    s.Save();
                });
        }

        private ILemoineToolSettings BuildLinkViewsProxy()
        {
            var s = LinkViewsLevelSettings.Instance;
            return new SpecProxy(
                new LemoineToolSettingsSpec
                {
                    Id = "T04a", Label = "Link Views — Level", Icon = "04",
                    Description = "Clustering and view geometry settings.",
                    Groups = new List<LemoineSettingsGroup>
                    {
                        new LemoineSettingsGroup
                        {
                            Id = "G1", Title = "View Geometry", OpenByDefault = true,
                            Settings = new List<LemoineSettingDef>
                            {
                                new LemoineSettingDef { Id="bufferXY", Kind="number", Label="XY buffer",
                                    Hint="Margin added around each cluster crop box (feet).",
                                    Options=new NumberOpts { Unit="ft", Min=0, Max=200, Step=1 }, Default=s.BufferXY },
                                new LemoineSettingDef { Id="clusterThreshold", Kind="number", Label="Cluster threshold",
                                    Hint="Maximum gap for union-find cluster merging (feet).",
                                    Options=new NumberOpts { Unit="ft", Min=0, Max=500, Step=1 }, Default=s.ClusterThreshold },
                                new LemoineSettingDef { Id="cutOffset", Kind="number", Label="Cut plane offset",
                                    Hint="Height above level elevation for the cut plane (feet).",
                                    Options=new NumberOpts { Unit="ft", Min=0, Max=30, Step=0.5 }, Default=s.CutOffset },
                            }
                        }
                    }
                },
                (groupId, sid, val) =>
                {
                    if (groupId == "G1")
                    {
                        if (sid == "bufferXY"         && val is double bxy) s.BufferXY         = bxy;
                        if (sid == "clusterThreshold" && val is double ct)  s.ClusterThreshold = ct;
                        if (sid == "cutOffset"        && val is double co)  s.CutOffset        = co;
                    }
                    s.Save();
                });
        }

        private ILemoineToolSettings BuildBulkExportProxy()
        {
            var s = BulkExportSettings.Instance;
            return new SpecProxy(
                new LemoineToolSettingsSpec
                {
                    Id = "tx", Label = "Bulk Export", Icon = "Tx",
                    Description = "Export sheets and views to PDF and DWG with parametric filenames.",
                    Groups = new List<LemoineSettingsGroup>
                    {
                        new LemoineSettingsGroup
                        {
                            Id = "G1", Title = "Output", OpenByDefault = true,
                            Settings = new List<LemoineSettingDef>
                            {
                                new LemoineSettingDef { Id="outdir", Kind="file", Label="Default output folder",
                                    Options=new FileOpts { Placeholder=@"C:\Projects\Exports\" }, Default=s.OutputFolder },
                                new LemoineSettingDef { Id="splitformat", Kind="toggle", Label="Split output by file format",
                                    Hint="Creates PDF\\, DWG\\ subfolders automatically.", Default=s.SplitByFormat },
                            }
                        },
                        new LemoineSettingsGroup
                        {
                            Id = "G2", Title = "Filename",
                            Settings = new List<LemoineSettingDef>
                            {
                                new LemoineSettingDef { Id="pattern", Kind="text", Label="Default filename pattern",
                                    Hint="Tokens: {SheetNumber} {SheetName} {Revision} {IssueDate} {ProjectNumber} {Year} {Month} {Day}",
                                    Options=new TextOpts { Mono=true, Placeholder="{SheetNumber}-{SheetName}" },
                                    Default=s.FilenamePattern },
                            }
                        },
                        new LemoineSettingsGroup
                        {
                            Id = "G3", Title = "Default Formats",
                            Settings = new List<LemoineSettingDef>
                            {
                                new LemoineSettingDef { Id="defpdf", Kind="toggle", Label="PDF on by default", Default=s.ExportPdf },
                                new LemoineSettingDef { Id="defdwg", Kind="toggle", Label="DWG on by default", Default=s.ExportDwg },
                            }
                        },
                        new LemoineSettingsGroup
                        {
                            Id = "G4", Title = "PDF Options",
                            Settings = new List<LemoineSettingDef>
                            {
                                new LemoineSettingDef { Id="combinepdf", Kind="toggle", Label="Combine into single PDF by default", Default=s.CombinePdf },
                                new LemoineSettingDef { Id="placement", Kind="single", Label="Paper placement",
                                    Options=new SingleSelectOpts { Items=new List<string> { "Center", "Offset from Corner" } },
                                    Default=s.PdfPaperPlacement },
                                new LemoineSettingDef { Id="hiddenlines", Kind="single", Label="Hidden line views",
                                    Options=new SingleSelectOpts { Items=new List<string> { "Vector Processing", "Raster Processing" } },
                                    Default=s.HiddenLinesVector ? "Vector Processing" : "Raster Processing" },
                            }
                        },
                        new LemoineSettingsGroup
                        {
                            Id = "G5", Title = "DWG Options",
                            Settings = new List<LemoineSettingDef>
                            {
                                new LemoineSettingDef { Id="dwgsetup", Kind="text", Label="Default DWG export setup name",
                                    Hint="Must match a setup created in Revit via File → Export → DWG.",
                                    Options=new TextOpts { Placeholder="Standard DWG" }, Default=s.DwgExportSetupName },
                            }
                        },
                    }
                },
                (groupId, sid, val) =>
                {
                    switch (sid)
                    {
                        case "outdir":      s.OutputFolder      = val as string ?? "";             break;
                        case "splitformat": s.SplitByFormat     = val is bool b1 && b1;            break;
                        case "pattern":     s.FilenamePattern   = val as string ?? "";             break;
                        case "defpdf":      s.ExportPdf         = val is bool b2 && b2;            break;
                        case "defdwg":      s.ExportDwg         = val is bool b3 && b3;            break;
                        case "combinepdf":  s.CombinePdf        = val is bool b4 && b4;            break;
                        case "placement":   s.PdfPaperPlacement = val as string ?? "Center";       break;
                        case "hiddenlines": s.HiddenLinesVector = val as string == "Vector Processing"; break;
                        case "dwgsetup":    s.DwgExportSetupName = val as string ?? "";            break;
                    }
                    s.Save();
                });
        }

        private ILemoineToolSettings BuildCreateSheetsProxy()
        {
            var s = CreateSheetsSettings.Instance;
            return new SpecProxy(
                new LemoineToolSettingsSpec
                {
                    Id = "tz", Label = "Create Sheets", Icon = "",
                    Description = "Title block, naming scheme, and starting number defaults.",
                    Groups = new List<LemoineSettingsGroup>
                    {
                        new LemoineSettingsGroup
                        {
                            Id = "G1", Title = "Create Sheets Defaults", OpenByDefault = true,
                            Settings = new List<LemoineSettingDef>
                            {
                                new LemoineSettingDef { Id="titleblock", Kind="text", Label="Default Title Block",
                                    Hint="Pre-selected title block family when opening the tool.",
                                    Options=new TextOpts { Placeholder="Family : Type" }, Default=s.DefaultTitleblockName },
                                new LemoineSettingDef { Id="namingScheme", Kind="text", Label="Default Naming Scheme",
                                    Hint="Token pattern used to name sheets, e.g. {LevelName}.",
                                    Options=new TextOpts { Placeholder="{LevelName}", Mono=true }, Default=s.DefaultNamingScheme },
                                new LemoineSettingDef { Id="startingNumber", Kind="number", Label="Default Starting Number",
                                    Hint="Sheet numbering begins at this value.",
                                    Options=new NumberOpts { Min=1, Max=9999, Step=1 }, Default=(double)s.DefaultStartingNumber },
                            }
                        }
                    }
                },
                (groupId, sid, val) =>
                {
                    switch (sid)
                    {
                        case "titleblock":    s.DefaultTitleblockName = val?.ToString() ?? ""; break;
                        case "namingScheme":  s.DefaultNamingScheme   = val?.ToString() ?? ""; break;
                        case "startingNumber":
                            if (int.TryParse(val?.ToString(), out int n) && n >= 1) s.DefaultStartingNumber = n;
                            break;
                    }
                    s.Save();
                });
        }

        // ── SpecProxy: thin ILemoineToolSettings wrapper around a pre-built spec ─
        private sealed class SpecProxy : ILemoineToolSettings
        {
            private readonly LemoineToolSettingsSpec       _spec;
            private readonly Action<string, string, object>? _apply;
            public SpecProxy(LemoineToolSettingsSpec spec, Action<string, string, object>? apply = null)
            { _spec = spec; _apply = apply; }
            public LemoineToolSettingsSpec? GetSettingsSpec() => _spec;
            public void ApplySettings(string groupId, string settingId, object value) => _apply?.Invoke(groupId, settingId, value);
        }

        // ── Shared helpers for spec rendering ────────────────────────────────────
        private static UIElement GNoSettings()
        {
            var tb = new TextBlock { Text = "No settings for this tool.", FontStyle = FontStyles.Italic, Margin = new Thickness(0, 2, 0, 2) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        // ── General tab shared helpers (mirror GlobalSettingsWindow.General.cs) ─
        private static TextBlock GContentHeader(string text)
        {
            var tb = new TextBlock { Text = text, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 18) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XL");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock GSubLabel(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 8) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }

        private static UIElement GHSep(double margin)
        {
            var r = new Rectangle { Height = 1, Margin = new Thickness(0, margin, 0, margin) };
            r.SetResourceReference(Rectangle.FillProperty, "LemoineBorder");
            return r;
        }

        private static Border GToggleRowShell(bool active)
        {
            var b = new Border
            {
                Padding         = new Thickness(12, 10, 12, 10),
                Margin          = new Thickness(0, 0, 0, 4),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Cursor          = Cursors.Hand,
            };
            if (active) b.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            else        b.Background = Brushes.Transparent;
            b.SetResourceReference(Border.BorderBrushProperty, active ? "LemoineAccent" : "LemoineBorder");
            return b;
        }

        private static Border GPill(bool active)
        {
            var b = new Border
            {
                Width             = 8,
                Height            = 8,
                CornerRadius      = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                BorderThickness   = new Thickness(1),
            };
            if (active) b.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
            else        b.Background = Brushes.Transparent;
            b.SetResourceReference(Border.BorderBrushProperty, active ? "LemoineAccent" : "LemoineBorderMid");
            return b;
        }

        private static void GUpdatePill(Border pill, bool active)
        {
            if (active) pill.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
            else        pill.Background = Brushes.Transparent;
            pill.SetResourceReference(Border.BorderBrushProperty, active ? "LemoineAccent" : "LemoineBorderMid");
        }

        // ── Setting control factory (shared by Room Tagger tab) ───────────────
        private UIElement? BuildSettingControl(LemoineSettingDef setting, string groupId, ILemoineToolSettings ts)
        {
            var sid = setting.Id;

            switch (setting.Kind)
            {
                case "toggle":
                {
                    bool on  = setting.Default is bool b && b;
                    var tog  = new LemoineToggleSwitches();
                    tog.SetItems(new List<ToggleItem>
                    {
                        new ToggleItem { Id = sid, Label = setting.Label ?? sid, Desc = setting.Hint ?? "", DefaultOn = on },
                    });
                    tog.StateChanged += state =>
                    {
                        if (state.TryGetValue(sid, out bool v))
                            ts.ApplySettings(groupId, sid, v);
                    };
                    return tog;
                }

                case "number":
                {
                    var opts = setting.Options as NumberOpts;
                    var row  = new StackPanel { Orientation = Orientation.Horizontal };
                    var tb   = new TextBox
                    {
                        Width         = 90,
                        Text          = setting.Default?.ToString() ?? "",
                        TextAlignment = TextAlignment.Right,
                    };
                    tb.SetResourceReference(TextBox.HeightProperty,     "LemoineH_Input");
                    tb.SetResourceReference(TextBox.PaddingProperty,    "LemoineTh_InputPad");
                    tb.SetResourceReference(TextBox.BackgroundProperty, "LemoineSelectBg");
                    tb.SetResourceReference(TextBox.ForegroundProperty, "LemoineText");
                    tb.SetResourceReference(TextBox.FontFamilyProperty, "LemoineMonoFont");
                    tb.SetResourceReference(TextBox.FontSizeProperty,   "LemoineFS_MD");
                    tb.LostFocus += (_, _) =>
                    {
                        if (double.TryParse(tb.Text, out double v))
                        {
                            if (opts != null) v = Math.Max(opts.Min, Math.Min(opts.Max, v));
                            tb.Text = v.ToString();
                            ts.ApplySettings(groupId, sid, v);
                        }
                    };
                    row.Children.Add(tb);
                    if (!string.IsNullOrEmpty(opts?.Unit))
                    {
                        var unitTb = new TextBlock
                        {
                            Text              = "  " + opts.Unit,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        unitTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                        unitTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                        unitTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                        row.Children.Add(unitTb);
                    }
                    return row;
                }

                case "search":
                {
                    var opts = setting.Options as SearchOpts;
                    var sa   = new LemoineSearchAutocomplete
                    {
                        Items       = opts?.Items ?? new List<string>(),
                        Placeholder = "Search…",
                    };
                    if (setting.Default is string def) sa.Value = def;
                    sa.SelectionChanged += v => ts.ApplySettings(groupId, sid, v);
                    return sa;
                }

                case "single":
                {
                    var opts = setting.Options as SingleSelectOpts;
                    var sel  = new LemoineSingleSelect
                    {
                        Items = opts?.Items ?? new List<string>(),
                    };
                    if (setting.Default is string def) sel.SelectedItem = def;
                    sel.SelectionChanged += v => ts.ApplySettings(groupId, sid, v);
                    return sel;
                }

                case "color":
                {
                    string currentHex = setting.Default as string ?? "#569cd6";
                    return LemoineColorPickerWindow.BuildColorPickerSwatch(
                        getHex: () => currentHex,
                        setHex: h =>
                        {
                            currentHex = h;
                            ts.ApplySettings(groupId, sid, h);
                        });
                }

                case "text":
                {
                    var opts = setting.Options as TextOpts;
                    var tb   = new TextBox
                    {
                        Width = 260,
                        Text  = setting.Default?.ToString() ?? "",
                    };
                    if (!string.IsNullOrEmpty(opts?.Placeholder))
                        tb.Tag = opts.Placeholder;
                    tb.SetResourceReference(TextBox.HeightProperty,     "LemoineH_Input");
                    tb.SetResourceReference(TextBox.PaddingProperty,    "LemoineTh_InputPad");
                    tb.SetResourceReference(TextBox.BackgroundProperty, "LemoineSelectBg");
                    tb.SetResourceReference(TextBox.ForegroundProperty, "LemoineText");
                    tb.SetResourceReference(TextBox.FontSizeProperty,   "LemoineFS_MD");
                    tb.SetResourceReference(TextBox.FontFamilyProperty, (opts?.Mono == true) ? "LemoineMonoFont" : "LemoineUiFont");
                    tb.LostFocus += (_, _) => ts.ApplySettings(groupId, sid, tb.Text);
                    return tb;
                }

                case "file":
                {
                    var opts = setting.Options as FileOpts;
                    var row  = new StackPanel { Orientation = Orientation.Horizontal };

                    var tb = new TextBox
                    {
                        Width = 200,
                        Text  = setting.Default?.ToString() ?? "",
                    };
                    tb.SetResourceReference(TextBox.HeightProperty,     "LemoineH_Input");
                    tb.SetResourceReference(TextBox.PaddingProperty,    "LemoineTh_InputPad");
                    tb.SetResourceReference(TextBox.BackgroundProperty, "LemoineSelectBg");
                    tb.SetResourceReference(TextBox.ForegroundProperty, "LemoineText");
                    tb.SetResourceReference(TextBox.FontSizeProperty,   "LemoineFS_SM");
                    tb.SetResourceReference(TextBox.FontFamilyProperty, "LemoineMonoFont");
                    tb.LostFocus += (_, _) => ts.ApplySettings(groupId, sid, tb.Text);

                    var browse = LemoineControlStyles.BuildSmallButton("…", LemoineControlStyles.LemoineButtonVariant.Ghost);
                    browse.Margin = new Thickness(4, 0, 0, 0);
                    browse.Click += (_, _) =>
                    {
                        var dlg = new System.Windows.Forms.FolderBrowserDialog
                        {
                            Description         = opts?.Placeholder ?? "Select folder",
                            SelectedPath        = tb.Text,
                            ShowNewFolderButton = true,
                        };
                        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        {
                            tb.Text = dlg.SelectedPath;
                            ts.ApplySettings(groupId, sid, tb.Text);
                        }
                    };
                    row.Children.Add(tb);
                    row.Children.Add(browse);
                    return row;
                }

                case "info":
                {
                    var opts = setting.Options as InfoOpts;
                    var tb   = new TextBlock
                    {
                        Text         = opts?.Text ?? "",
                        TextWrapping = TextWrapping.Wrap,
                        FontStyle    = FontStyles.Italic,
                    };
                    tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                    return tb;
                }

                default:
                    return null;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // SHARED HELPERS
        // ══════════════════════════════════════════════════════════════════════
        private static Border MakeSection(string title, UIElement content)
        {
            var card = new Border
            {
                Margin       = new Thickness(0, 0, 0, 14),
                Padding      = new Thickness(16, 14, 16, 16),
                CornerRadius = new CornerRadius(6),
            };
            card.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");

            var stack = new StackPanel();
            card.Child = stack;

            var hdr = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
            hdr.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            hdr.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            hdr.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            stack.Children.Add(hdr);
            stack.Children.Add(content);
            return card;
        }

        private static TextBlock MakeSubLabel(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock MakeValueLabel(string initial = "")
        {
            var tb = new TextBlock
            {
                Text         = initial,
                Margin       = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }

        private static TextBlock MakeGalleryHeader(string text)
        {
            var tb = new TextBlock { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 16) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock MakeBarLabel(string text)
        {
            var tb = new TextBlock
            {
                Text              = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(12, 0, 4, 0),
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static Border MakePill(string label)
        {
            var tb = new TextBlock
            {
                Text              = label,
                VerticalAlignment = VerticalAlignment.Center,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XS");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            return new Border
            {
                Child        = tb,
                Padding      = new Thickness(7, 2, 7, 2),
                CornerRadius = new CornerRadius(3),
                Background   = Brushes.Transparent,
            };
        }
    }
}
