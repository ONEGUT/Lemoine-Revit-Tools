using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Preview
{
    internal sealed class PreviewMainWindow : Window
    {
        // ── State & tracking ──────────────────────────────────────────────────
        private readonly PreviewState _state = PreviewState.Load();

        private string   _activeTabId = "controls";
        private UIElement? _controlsContent;
        private UIElement? _demoContent;
        private UIElement? _iconsContent;

        private Border _contentHost = null!;

        // Theme bar handles — updated on theme/scale change
        private readonly List<(Border swatch, LemoineTheme theme)> _swatches  = new();
        private readonly List<(Border pill,   LemoineUiSize size)> _sizePills = new();
        private readonly Dictionary<string, Border> _tabPills = new();

        // The icon font must point to THIS assembly, not LemoineTools
        private static readonly FontFamily _iconFont = new FontFamily(
            new Uri("pack://application:,,,/LemoinePreview;component/"),
            "./Source/Resources/Fonts/IcoMoonFree.ttf#IcoMoon-Free");

        // ── Constructor ───────────────────────────────────────────────────────
        public PreviewMainWindow()
        {
            Width  = 1060;
            Height = 780;
            MinWidth  = 820;
            MinHeight = 580;
            WindowStyle = WindowStyle.None;
            ResizeMode  = ResizeMode.CanResizeWithGrip;
            AllowsTransparency = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Title = "Lemoine Preview";

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight        = 0,
                ResizeBorderThickness = new Thickness(4),
                GlassFrameThickness  = new Thickness(0),
                UseAeroCaptionButtons = false,
            });

            // Apply resources (theme + scale) then override icon font URI
            RefreshResources();

            // Inject WPF control styles (scrollbar, combobox, checkbox, datepicker)
            LemoineControlStyles.InjectInto(Resources, 8);

            // Re-apply on changes (theme/scale are persisted by LemoineSettings)
            LemoineSettings.Instance.ThemeChanged  += _ => RefreshResources();
            LemoineSettings.Instance.UiSizeChanged += _ => RefreshResources();

            BuildWindow();

            Loaded += (_, _) => ShowTab("controls");
        }

        private void RefreshResources()
        {
            LemoineSettings.Instance.ApplyTo(Resources);
            // Override the LemoineTools assembly URI with our own
            Resources["LemoineIconFont"] = _iconFont;
            UpdateSwatchHighlights();
            UpdateSizePillHighlights();
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

            // Close
            var closeBtn = LemoineControlStyles.BuildSmallButton("✕", LemoineButtonVariant.Ghost);
            closeBtn.VerticalAlignment = VerticalAlignment.Center;
            closeBtn.Click += (_, _) => Close();
            DockPanel.SetDock(closeBtn, Dock.Right);
            dp.Children.Add(closeBtn);

            // Minimise
            var minBtn = LemoineControlStyles.BuildSmallButton("─", LemoineButtonVariant.Ghost);
            minBtn.VerticalAlignment = VerticalAlignment.Center;
            minBtn.Click += (_, _) => WindowState = WindowState.Minimized;
            DockPanel.SetDock(minBtn, Dock.Right);
            dp.Children.Add(minBtn);

            // Title
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

            // ── Scale picker (right) ──────────────────────────────────────────
            var scaleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(scaleRow, Dock.Right);

            scaleRow.Children.Add(MakeBarLabel("Scale:"));

            foreach (var sz in new[] { LemoineUiSize.Small, LemoineUiSize.Medium, LemoineUiSize.Large })
            {
                var captured = sz;
                var label    = sz == LemoineUiSize.Small ? "S" : sz == LemoineUiSize.Medium ? "M" : "L";
                var pill     = MakePill(label);
                pill.Margin  = new Thickness(3, 0, 0, 0);
                pill.Cursor  = Cursors.Hand;
                pill.MouseLeftButtonUp += (_, _) => LemoineSettings.Instance.SetUiSize(captured);
                _sizePills.Add((pill, sz));
                scaleRow.Children.Add(pill);
            }
            dp.Children.Add(scaleRow);

            // ── Theme swatches (left) ─────────────────────────────────────────
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
            })
            {
                var tabId     = id;
                var tabPill   = BuildTabPill(label, icon);
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

            // Update tab pill highlights
            foreach (var (id, pill) in _tabPills)
            {
                var isActive = id == tabId;
                pill.BorderThickness = isActive ? new Thickness(0, 0, 0, 2) : new Thickness(0);
                if (isActive)
                    pill.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                else
                    pill.BorderBrush = null;

                var label = ((StackPanel)pill.Child).Children.OfType<TextBlock>().FirstOrDefault();
                if (label != null)
                    label.SetResourceReference(TextBlock.ForegroundProperty,
                        isActive ? "LemoineText" : "LemoineTextSub");
            }

            _contentHost.Child = tabId switch
            {
                "demo"   => GetDemoContent(),
                "icons"  => GetIconsContent(),
                _        => GetControlsContent(),
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
            var content = new StackPanel { Spacing = 8 };

            var normalRow = new StackPanel { Orientation = Orientation.Horizontal };
            normalRow.Spacing = 8;

            foreach (var (label, variant) in new[]
            {
                ("Ghost",   LemoineButtonVariant.Ghost),
                ("Primary", LemoineButtonVariant.Primary),
                ("Danger",  LemoineButtonVariant.Danger),
            })
            {
                var btn = LemoineControlStyles.BuildButton(label, variant);
                normalRow.Children.Add(btn);
            }
            content.Children.Add(MakeSubLabel("Normal"));
            content.Children.Add(normalRow);

            var smallRow = new StackPanel { Orientation = Orientation.Horizontal };
            smallRow.Spacing = 8;

            foreach (var (label, variant) in new[]
            {
                ("Ghost",   LemoineButtonVariant.Ghost),
                ("Primary", LemoineButtonVariant.Primary),
                ("Danger",  LemoineButtonVariant.Danger),
            })
            {
                var btn = LemoineControlStyles.BuildSmallButton(label, variant);
                smallRow.Children.Add(btn);
            }
            content.Children.Add(MakeSubLabel("Small"));
            content.Children.Add(smallRow);

            return MakeSection("Buttons", content);
        }

        // ── Inline Edit ───────────────────────────────────────────────────────
        private UIElement BuildInlineEditSection()
        {
            var content  = new StackPanel();
            var valueTb  = MakeValueLabel($"Committed: \"{_state.InlineText}\"");

            var edit = new LemoineInlineEdit
            {
                Text        = _state.InlineText,
                Placeholder = "Click to edit, press Enter to commit…",
            };
            edit.TextCommitted += newText =>
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

            var content = new StackPanel { Spacing = 12 };

            // Single select
            var ssValue = MakeValueLabel($"Selected: {_state.SingleSelect}");
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

            // Search autocomplete
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
            var content = new StackPanel { Spacing = 14 };

            // Multi-select tabs
            var groups = new Dictionary<string, List<string>>
            {
                ["Floor Levels"] = new List<string> { "Level 1", "Level 2", "Level 3", "Level 4", "Roof" },
                ["Below Grade"]  = new List<string> { "B1 Basement", "B2 Sub-Basement" },
                ["Mezzanines"]   = new List<string> { "Mezzanine A", "Mezzanine B" },
            };

            var mtValue = MakeValueLabel();
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

            // Tag chip input
            var availableTags = new List<string>
            {
                "Mechanical", "Plumbing", "Electrical", "Structural",
                "Civil", "Fire Protection", "Landscape", "Interiors", "Facade",
            };

            var chipValue = MakeValueLabel($"Tags: {_state.SelectedTags}");
            var chips = new LemoineTagChipInput
            {
                ItemsSource    = availableTags,
                SelectedItems  = _state.SelectedTagList,
                AllowFreeText  = true,
                Placeholder    = "Add discipline tags…",
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
            var content = new StackPanel { Spacing = 12 };

            // Number stepper
            var stepperValue = MakeValueLabel($"Value: {_state.StepperValue}");
            var stepper = new LemoineNumberStepper
            {
                Value    = _state.StepperValue,
                MinValue = 0,
                MaxValue = 20,
                Step     = 1,
            };
            stepper.ValueChanged += v =>
            {
                _state.StepperValue = v;
                _state.Save();
                stepperValue.Text = $"Value: {v}";
            };
            content.Children.Add(MakeSubLabel("Number Stepper"));
            content.Children.Add(stepper);
            content.Children.Add(stepperValue);

            // Number range
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
            var content   = new StackPanel { Spacing = 4 };
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
            var content = new StackPanel { Spacing = 4 };
            var toggleValue = MakeValueLabel();

            var items = new[]
            {
                new LemoineToggleSwitches.ToggleItem { Id = "show_dims",  Label = "Show dimensions",   Desc = "Display dimension annotations on selected elements", DefaultOn = true  },
                new LemoineToggleSwitches.ToggleItem { Id = "show_tags",  Label = "Show element tags",  Desc = "Render identity tags on all tagged elements",         DefaultOn = false },
                new LemoineToggleSwitches.ToggleItem { Id = "halftone",   Label = "Halftone linked",    Desc = "Apply halftone overrides to all linked models",       DefaultOn = true  },
                new LemoineToggleSwitches.ToggleItem { Id = "hide_grids", Label = "Hide grids in view", Desc = "Suppress grid lines in the current view",             DefaultOn = false },
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
            var content  = new StackPanel { Spacing = 4 };
            var fbValue  = MakeValueLabel(_state.FilePath.Length > 0 ? _state.FilePath : "No file selected");

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
            var content    = new StackPanel { Spacing = 14 };
            var colorValue = MakeValueLabel($"Selected: {_state.ColorHex}");

            var initial = BrushHelper.ColorFromHex(_state.ColorHex, Colors.CornflowerBlue);
            var picker  = new LemoineColorPickerPanel(initial);
            picker.LoadPersisted();
            picker.ColorChanged += c =>
            {
                _state.ColorHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                _state.Save();
                colorValue.Text = $"Selected: {_state.ColorHex}";
                picker.AddToRecent(c);
                picker.SavePersisted();
            };
            content.Children.Add(MakeSubLabel("Color Picker Panel"));
            content.Children.Add(picker);
            content.Children.Add(colorValue);

            // Swatch picker
            var swatchValue = MakeValueLabel($"Kind: {_state.SwatchKind}  Fill: {_state.SwatchFill}");
            var sp = new LemoineSwatchPicker
            {
                Kind          = _state.SwatchKind,
                Fill          = _state.SwatchFill,
                Title         = "Legend swatch style",
                AllowColorPick = false,
            };
            sp.SelectionChanged += e =>
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
            var content = new StackPanel { Spacing = 4 };
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
            var content = new StackPanel { Spacing = 12 };

            // Category chips
            content.Children.Add(MakeSubLabel("Category Chips"));
            var chipRow = new WrapPanel { Orientation = Orientation.Horizontal };
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
                chip.Clicked += () => chip.Active = !chip.Active;
                chipRow.Children.Add(chip);
            }
            content.Children.Add(chipRow);

            // Warn banner
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

            // ── Header ────────────────────────────────────────────────────────
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

            // ── Action area ───────────────────────────────────────────────────
            var actionArea = new Border { Padding = new Thickness(32, 24, 32, 32) };
            outer.Children.Add(actionArea);

            var actionStack = new StackPanel { Spacing = 12 };
            actionArea.Child = actionStack;

            // Info row
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

            var openBtn = LemoineControlStyles.BuildButton("Open Tool Window →", LemoineButtonVariant.Primary);
            openBtn.Margin              = new Thickness(0, 12, 0, 0);
            openBtn.HorizontalAlignment = HorizontalAlignment.Left;
            openBtn.Click += (_, _) =>
            {
                var tool   = new DemoTool(_state);
                var window = new StepFlowWindow(tool);
                window.Owner = this;
                // Apply current theme to the tool window
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

            // Group icons by category using the regions from the enum
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

                ("Misc",
                    new[] { LemoineIcon.Newspaper, LemoineIcon.Droplet, LemoineIcon.Hammer, LemoineIcon.Lab,
                            LemoineIcon.Briefcase, LemoineIcon.Road, LemoineIcon.Terminal, LemoineIcon.Sun,
                            LemoineIcon.Mail }),
            };

            foreach (var (categoryName, icons) in categories)
            {
                outer.Children.Add(MakeIconCategory(categoryName, icons));
            }

            return scroll;
        }

        private UIElement MakeIconCategory(string name, IEnumerable<LemoineIcon> icons)
        {
            var section = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

            var hdr = new TextBlock
            {
                Text       = name,
                FontWeight = FontWeights.SemiBold,
                Margin     = new Thickness(0, 0, 0, 8),
            };
            hdr.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            hdr.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            hdr.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            section.Children.Add(hdr);

            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };

            foreach (var icon in icons)
            {
                var card = new Border
                {
                    Width   = 80,
                    Height  = 68,
                    Margin  = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(6, 10, 6, 8),
                    CornerRadius = new CornerRadius(4),
                    Cursor  = Cursors.Hand,
                    ToolTip = icon.ToString(),
                };
                card.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");

                card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "LemoineSelectBg");
                card.MouseLeave += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");

                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

                var iconTb = LemoineIcons.Build(icon, "LemoineFS_LG");
                iconTb.HorizontalAlignment = HorizontalAlignment.Center;
                iconTb.Margin = new Thickness(0, 0, 0, 4);

                var nameTb = new TextBlock
                {
                    Text              = icon.ToString(),
                    TextWrapping      = TextWrapping.Wrap,
                    TextAlignment     = TextAlignment.Center,
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

            var hdr = new TextBlock
            {
                Text       = title,
                FontWeight = FontWeights.SemiBold,
                Margin     = new Thickness(0, 0, 0, 12),
            };
            hdr.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            hdr.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            hdr.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            stack.Children.Add(hdr);
            stack.Children.Add(content);
            return card;
        }

        private static TextBlock MakeSubLabel(string text)
        {
            var tb = new TextBlock
            {
                Text   = text,
                Margin = new Thickness(0, 0, 0, 4),
            };
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
            var tb = new TextBlock
            {
                Text       = text,
                FontWeight = FontWeights.Bold,
                Margin     = new Thickness(0, 0, 0, 16),
            };
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
