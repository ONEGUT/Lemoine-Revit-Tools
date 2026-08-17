using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LemoineTools.Framework;
using LemoineTools.Framework.Zones;

using WpfGrid       = System.Windows.Controls.Grid;
using WpfTextBox    = System.Windows.Controls.TextBox;
using WpfVisibility = System.Windows.Visibility;

namespace LemoineTools.Tools.Zones.Windows
{
    // =========================================================================
    // ZoneManagerWindow — the zone library editor.
    //
    // A bespoke Window rather than an IStepFlowTool because it is not a
    // run-once wizard: you come back to it and edit. Same family as
    // LegendSettingsWindow / FiltersSettingsWindow.
    //
    // Two constraints that bite bespoke windows specifically (CLAUDE.md):
    //
    //   • NO dispatcher safety net comes for free. StepFlowWindow installs
    //     Dispatcher.UnhandledException; this must install its own, or an
    //     unhandled throw on this window's STA thread hard-crashes Revit with
    //     no diagnostics.log entry at all.
    //   • IToolCleanup.OnWindowClosed is NEVER invoked for a bespoke window —
    //     only StepFlowWindow calls it. Persistence hangs off OnClosed directly.
    //
    // Everything Revit-side is captured on the main thread by the launching
    // command and handed in. This window never touches the Revit API.
    // =========================================================================
    public partial class ZoneManagerWindow : Window
    {
        // Tab tokens — logic identifiers, deliberately not externalized.
        private const string TabStructure = "Structure";
        private const string TabRecipes   = "Recipes";
        private const string TabLayouts   = "Layouts";

        private string _tab = TabStructure;
        private string _query = "";

        /// <summary>Selected record id in the current tab, "" for none.</summary>
        private string _selectedId = "";

        private readonly string _docTitle;
        private readonly List<ZoneTitleBlocks.TitleBlockType> _titleBlocks;
        private readonly List<string> _scopeBoxNames;
        private readonly List<string> _hostLevelNames;

        private ZoneLibrary Lib => ZoneSettings.Instance.Library;

        public ZoneManagerWindow(
            string? docTitle = null,
            List<ZoneTitleBlocks.TitleBlockType>? titleBlocks = null,
            List<string>? scopeBoxNames = null,
            List<string>? hostLevelNames = null)
        {
            _docTitle       = docTitle ?? "";
            _titleBlocks    = titleBlocks    ?? new List<ZoneTitleBlocks.TitleBlockType>();
            _scopeBoxNames  = scopeBoxNames  ?? new List<string>();
            _hostLevelNames = hostLevelNames ?? new List<string>();

            InitializeComponent();
            Loaded += OnLoaded;

            // Named handlers, never lambdas — a leaked subscription outliving this STA
            // thread's dispatcher crashes or hangs Revit on the next theme change.
            AppSettings.Instance.ThemeChanged  += OnThemeChanged;
            AppSettings.Instance.UiSizeChanged += OnUiSizeChanged;

            // Last-resort net for this window's own dispatcher. Without it a stray throw in
            // any handler is a silent hard crash, not a logged exception.
            Dispatcher.UnhandledException += OnDispatcherUnhandledException;
        }

        private void OnDispatcherUnhandledException(
            object? sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            DiagnosticsLog.Error("ZoneManagerWindow unhandled UI exception", e.Exception);
            e.Handled = true;
        }

        private void OnThemeChanged(ThemePalette t)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppSettings.Instance.ApplyTo(Resources);
                Background = t.PageBg;
                ApplyChrome();
                Rebuild();
            }));
        }

        private void OnUiSizeChanged(UiSize _)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppSettings.Instance.ApplyScaleTo(Resources);
                ControlStyles.InjectInto(Resources, scrollBarWidth: 8);
                Rebuild();
            }));
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            AppSettings.Instance.ApplyTo(Resources);
            ControlStyles.InjectInto(Resources, scrollBarWidth: 8);
            Background = AppSettings.Instance.ActiveTheme.PageBg;
            ApplyChrome();

            _titleText.Text = AppStrings.T("zones.manager.title");
            _titleText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            _titleText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
            _titleText.FontWeight = FontWeights.SemiBold;

            _docText.Text = _docTitle;
            _docText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _docText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            _searchHint.Text = AppStrings.T("zones.picker.searchHint");
            _searchHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _searchHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _searchBox.SetResourceReference(WpfTextBox.FontSizeProperty,   "LemoineFS_SM");
            _searchBox.TextChanged += (s, ev) =>
            {
                _query = (_searchBox.Text ?? "").Trim();
                _searchHint.Visibility = string.IsNullOrEmpty(_query) ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                RebuildList();
            };

            _statusText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _statusText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            // Dragging the toolbar moves the window — WindowStyle=None means no OS caption.
            _toolbarBorder.Background = Brushes.Transparent;
            _toolbarBorder.MouseLeftButtonDown += (s, ev) =>
            {
                if (ev.ClickCount == 1) { try { DragMove(); } catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneManagerWindow: DragMove", ex); } }
            };

            BuildToolbarActions();
            BuildFooterActions();
            Rebuild();
        }

        protected override void OnClosed(EventArgs e)
        {
            AppSettings.Instance.ThemeChanged  -= OnThemeChanged;
            AppSettings.Instance.UiSizeChanged -= OnUiSizeChanged;
            Dispatcher.UnhandledException      -= OnDispatcherUnhandledException;

            // The zone library belongs to the PROJECT. IToolCleanup is never called for a
            // bespoke window, so the save hangs off OnClosed. Guarded, because an unhandled
            // throw on this STA thread would take Revit with it.
            try { ZoneSettings.Save(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneManagerWindow: save project zone library", ex); }

            base.OnClosed(e);
        }

        private void ApplyChrome()
        {
            _root.SetResourceReference(WpfGrid.BackgroundProperty,    "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _toolbarBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _footerBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            _footerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _railBorder.SetResourceReference(Border.BackgroundProperty,    "LemoineSurface");
            _railBorder.SetResourceReference(Border.BorderBrushProperty,   "LemoineBorder");
            _listBorder.SetResourceReference(Border.BackgroundProperty,    "LemoineBg");
            _listBorder.SetResourceReference(Border.BorderBrushProperty,   "LemoineBorder");
        }

        // ── Chrome actions ────────────────────────────────────────────────────
        private void BuildToolbarActions()
        {
            _toolbarActions.Children.Clear();
            _toolbarActions.Children.Add(MakeButton(AppStrings.T("zones.manager.close"), Close, accent: false));
        }

        private void BuildFooterActions()
        {
            _footerActions.Children.Clear();
            _footerActions.Children.Add(MakeButton(AppStrings.T("zones.manager.actions.resolvePlacements"),
                                                   OnResolvePlacements, accent: false));
            _footerActions.Children.Add(MakeButton(AppStrings.T("zones.manager.close"), Close, accent: true));
        }

        // ── Rebuild ───────────────────────────────────────────────────────────
        private void Rebuild()
        {
            BuildTabs();
            RebuildList();
            UpdateStatus();
        }

        private void BuildTabs()
        {
            _tabStrip.Children.Clear();
            AddTab(TabStructure, AppStrings.T("zones.manager.tabs.structure"), Lib.Areas.Count + Lib.Levels.Count);
            AddTab(TabRecipes,   AppStrings.T("zones.manager.tabs.recipes"),   Lib.Recipes.Count);
            AddTab(TabLayouts,   AppStrings.T("zones.manager.tabs.layouts"),   Lib.Layouts.Count);
        }

        private void AddTab(string token, string label, int count)
        {
            var b = new Border
            {
                // LemoineRadius_Card (10) — tabs and pills share the add-button rounding.
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(11, 5, 11, 5),
                Margin = new Thickness(0, 0, 4, 0),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                // Without a background the border is only hit-testable on its edge.
                Background = Brushes.Transparent,
            };
            bool on = _tab == token;
            b.SetResourceReference(Border.BorderBrushProperty, on ? "LemoineAccent" : "LemoineBorder");
            if (on) b.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");

            var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Background = Brushes.Transparent };
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, on ? "LemoineText" : "LemoineTextSub");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            if (on) t.FontWeight = FontWeights.SemiBold;
            sp.Children.Add(t);

            var badge = new TextBlock { Text = count.ToString(), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            badge.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            badge.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            sp.Children.Add(badge);

            b.Child = sp;
            b.MouseLeftButtonUp += (s, e) =>
            {
                _tab = token;
                _selectedId = "";
                Rebuild();
            };
            _tabStrip.Children.Add(b);
        }

        private bool Matches(params string?[] fields)
        {
            if (string.IsNullOrEmpty(_query)) return true;
            foreach (var f in fields)
                if (!string.IsNullOrEmpty(f) && f!.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private void RebuildList()
        {
            _listStack.Children.Clear();
            _railActions.Children.Clear();

            switch (_tab)
            {
                case TabRecipes: BuildRecipeList(); break;
                case TabLayouts: BuildLayoutList(); break;
                default:         BuildStructureList(); break;
            }

            RebuildDetail();
        }

        // ── Structure tab ─────────────────────────────────────────────────────
        private void BuildStructureList()
        {
            var buildings = Lib.Buildings.OrderBy(b => b.SortIndex)
                                         .ThenBy(b => b.Name, NaturalOrderComparer.OrdinalIgnoreCase).ToList();
            var groups = buildings.Select(b => (b.Id, b.Name)).ToList();
            if (groups.Count == 0) groups.Add(("", AppStrings.T("zones.picker.unassigned")));

            int shown = 0;
            foreach (var g in groups)
            {
                var levels = Lib.Levels.Where(l => (l.BuildingId ?? "") == g.Id)
                                       .OrderBy(l => l.SortIndex).ThenBy(l => l.ElevationFt).ToList();
                var areas  = Lib.Areas.Where(a => (a.BuildingId ?? "") == g.Id)
                                      .OrderBy(a => a.SortIndex)
                                      .ThenBy(a => a.Name, NaturalOrderComparer.OrdinalIgnoreCase).ToList();

                var rows = new List<UIElement>();
                foreach (var lv in levels.Where(l => Matches(l.Name, l.Code)))
                    rows.Add(MakeListRow(lv.Id, lv.Name, $"{lv.ElevationFt:0.##}'", indent: 1, kind: "level"));
                foreach (var a in areas.Where(x => Matches(x.Name, x.Code, x.ScopeBoxName)))
                    rows.Add(MakeListRow(a.Id, a.Name, a.ScopeBoxName, indent: 1, kind: "area"));

                if (rows.Count == 0) continue;
                _listStack.Children.Add(MakeGroupHeader(g.Name));
                foreach (var r in rows) { _listStack.Children.Add(r); shown++; }
            }

            if (shown == 0) _listStack.Children.Add(MakeEmptyRow(AppStrings.T("zones.picker.emptyLibrary")));

            _railActions.Children.Add(MakeButton(AppStrings.T("zones.manager.actions.addBuilding"), () => AddBuilding()));
            _railActions.Children.Add(MakeButton(AppStrings.T("zones.manager.actions.addLevel"),    () => AddLevel()));
            _railActions.Children.Add(MakeButton(AppStrings.T("zones.manager.actions.addArea"),     () => AddArea()));
        }

        private void BuildRecipeList()
        {
            var recipes = Lib.Recipes.OrderBy(r => r.SortIndex)
                                     .ThenBy(r => r.Name, NaturalOrderComparer.OrdinalIgnoreCase)
                                     .Where(r => Matches(r.Name, r.Kind)).ToList();
            foreach (var r in recipes)
                _listStack.Children.Add(MakeListRow(r.Id, r.Name, r.Kind, indent: 0, kind: "recipe"));
            if (recipes.Count == 0) _listStack.Children.Add(MakeEmptyRow(AppStrings.T("zones.picker.emptyLibrary")));

            _railActions.Children.Add(MakeButton(AppStrings.T("zones.manager.actions.addRecipe"), () => AddRecipe()));
            _railActions.Children.Add(MakeButton(AppStrings.T("zones.manager.actions.delete"),    () => DeleteSelected()));
        }

        private void BuildLayoutList()
        {
            var layouts = Lib.Layouts.OrderBy(y => y.SortIndex)
                                     .ThenBy(y => y.Name, NaturalOrderComparer.OrdinalIgnoreCase)
                                     .Where(y => Matches(y.Name, y.TitleBlockTypeName)).ToList();
            foreach (var y in layouts)
            {
                _listStack.Children.Add(MakeListRow(y.Id, y.Name, $"{y.Groups.Count} grp", indent: 0, kind: "layout"));
                foreach (var g in y.Groups.OrderBy(g => g.SortIndex))
                {
                    string names = string.Join(" + ", g.AreaIds.Select(id => Lib.Area(id)?.Name ?? "?"));
                    _listStack.Children.Add(MakeListRow(y.Id + "/" + g.Id, names, g.Suffix, indent: 1, kind: "group"));
                }
            }
            if (layouts.Count == 0) _listStack.Children.Add(MakeEmptyRow(AppStrings.T("zones.picker.emptyLibrary")));

            _railActions.Children.Add(MakeButton(AppStrings.T("zones.manager.actions.addLayout"), () => AddLayout()));
            _railActions.Children.Add(MakeButton(AppStrings.T("zones.manager.actions.delete"),    () => DeleteSelected()));
        }

        // ── Detail pane ───────────────────────────────────────────────────────
        private void RebuildDetail()
        {
            _detailStack.Children.Clear();

            var area = Lib.Area(_selectedId);
            if (area != null) { BuildAreaDetail(area); return; }

            var level = Lib.Level(_selectedId);
            if (level != null) { BuildLevelDetail(level); return; }

            var recipe = Lib.Recipe(_selectedId);
            if (recipe != null) { BuildRecipeDetail(recipe); return; }

            var layout = Lib.Layout(_selectedId);
            if (layout != null) { BuildLayoutDetail(layout); return; }

            _detailStack.Children.Add(MakeEmptyRow(AppStrings.T("zones.picker.emptyLibrary")));
        }

        private void BuildAreaDetail(ZoneArea a)
        {
            _detailStack.Children.Add(MakeHeading(a.Name, AppStrings.T("zones.manager.area.section")));

            var card = MakeCard(AppStrings.T("zones.manager.area.section"));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.area.name"), a.Name, v => { a.Name = v; RebuildList(); }));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.area.code"), a.Code, v => a.Code = v));
            card.Children.Add(MakeChoiceField(AppStrings.T("zones.manager.area.definition"),
                new[] { ZoneExtentMode.ScopeBox, ZoneExtentMode.Grids, ZoneExtentMode.RoomCluster, ZoneExtentMode.Manual },
                a.Definition, v => { a.Definition = v; RebuildDetail(); }));

            if (a.Definition == ZoneExtentMode.ScopeBox)
            {
                var names = new List<string> { "" };
                names.AddRange(_scopeBoxNames);
                card.Children.Add(MakeChoiceField(AppStrings.T("zones.manager.area.scopeBox"),
                    names.ToArray(), a.ScopeBoxName, v => { a.ScopeBoxName = v; RebuildList(); }));
            }

            card.Children.Add(MakeReadOnlyField(AppStrings.T("zones.manager.area.extents"),
                a.HasExtents
                    ? AppStrings.T("zones.manager.area.extentsValue", $"{a.WidthFt:0.##}", $"{a.DepthFt:0.##}")
                    : AppStrings.T("zones.manager.area.extentsUnresolved")));

            card.Children.Add(MakeChoiceField(AppStrings.T("zones.manager.area.anchorMode"),
                new[] { ZoneAnchorMode.ExtentsCentre, ZoneAnchorMode.GridIntersection, ZoneAnchorMode.Manual },
                a.AnchorMode, v => a.AnchorMode = v));
            card.Children.Add(MakeNote(AppStrings.T("zones.manager.area.anchorNote")));

            _detailStack.Children.Add(WrapCard(card));
        }

        private void BuildLevelDetail(ZoneLevel l)
        {
            _detailStack.Children.Add(MakeHeading(l.Name, "Level"));
            var card = MakeCard("Level");
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.area.name"), l.Name, v => { l.Name = v; RebuildList(); }));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.area.code"), l.Code, v => l.Code = v));

            var levelNames = new List<string> { "" };
            levelNames.AddRange(_hostLevelNames);
            card.Children.Add(MakeChoiceField("Host level", levelNames.ToArray(), l.HostLevelName,
                v => l.HostLevelName = v));

            card.Children.Add(MakeNumberField("Band base (ft)", l.BandBaseOffsetFt, v => l.BandBaseOffsetFt = v));
            card.Children.Add(MakeNumberField("Band top (ft)",  l.BandTopOffsetFt,  v => l.BandTopOffsetFt  = v));
            _detailStack.Children.Add(WrapCard(card));
        }

        private void BuildRecipeDetail(ZoneViewRecipe r)
        {
            _detailStack.Children.Add(MakeHeading(r.Name, AppStrings.T("zones.manager.recipe.section")));

            var card = MakeCard(AppStrings.T("zones.manager.recipe.section"));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.recipe.name"), r.Name, v => { r.Name = v; RebuildList(); }));
            card.Children.Add(MakeChoiceField(AppStrings.T("zones.manager.recipe.kind"),
                new[] { ZoneViewKind.FloorPlan, ZoneViewKind.CeilingPlan, ZoneViewKind.ThreeD, ZoneViewKind.Section, ZoneViewKind.AreaPlan },
                r.Kind, v => { r.Kind = v; RebuildDetail(); }));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.recipe.familyType"), r.ViewFamilyTypeName, v => r.ViewFamilyTypeName = v));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.recipe.template"),   r.ViewTemplateName,   v => r.ViewTemplateName   = v));
            card.Children.Add(MakeChoiceField(AppStrings.T("zones.manager.recipe.scaleMode"),
                new[] { ZoneScaleMode.FitToTitleBlock, ZoneScaleMode.Fixed }, r.ScaleMode, v => { r.ScaleMode = v; RebuildDetail(); }));
            if (r.ScaleMode == ZoneScaleMode.Fixed)
                card.Children.Add(MakeReadOnlyField(AppStrings.T("zones.manager.recipe.scale"), ZoneScaleFit.Label(r.Scale)));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.recipe.namePattern"), r.NamePattern, v => r.NamePattern = v));
            card.Children.Add(MakeNote(AppStrings.T("zones.manager.recipe.patternNote")));
            _detailStack.Children.Add(WrapCard(card));

            if (ZoneViewKind.IsPlan(r.Kind) && r.ViewRange != null)
            {
                var vr = MakeCard(AppStrings.T("zones.manager.recipe.viewRange"));
                vr.Children.Add(MakePlaneRow(AppStrings.T("zones.manager.recipe.top"),    r.ViewRange.Top));
                vr.Children.Add(MakePlaneRow(AppStrings.T("zones.manager.recipe.cut"),    r.ViewRange.CutPlane));
                vr.Children.Add(MakePlaneRow(AppStrings.T("zones.manager.recipe.bottom"), r.ViewRange.Bottom));
                vr.Children.Add(MakePlaneRow(AppStrings.T("zones.manager.recipe.depth"),  r.ViewRange.ViewDepth));
                _detailStack.Children.Add(WrapCard(vr));
            }
        }

        private UIElement MakePlaneRow(string label, ZoneViewRangePlane plane)
        {
            var g = FieldGrid(label);
            var refs = new List<string> { ZoneLevelRef.Current, ZoneLevelRef.Above, ZoneLevelRef.Below, ZoneLevelRef.Unlimited };
            refs.AddRange(_hostLevelNames);
            var combo = MakeCombo(refs.ToArray(), plane.LevelRef, v => plane.LevelRef = v);
            WpfGrid.SetColumn(combo, 1);
            g.Children.Add(combo);

            var num = MakeNumberBox(plane.OffsetFt, v => plane.OffsetFt = v);
            num.Width = 90;
            num.Margin = new Thickness(8, 0, 0, 0);
            WpfGrid.SetColumn(num, 2);
            g.Children.Add(num);
            return g;
        }

        private void BuildLayoutDetail(ZoneSheetLayout y)
        {
            _detailStack.Children.Add(MakeHeading(y.Name, AppStrings.T("zones.manager.layout.section")));

            var card = MakeCard(AppStrings.T("zones.manager.layout.section"));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.layout.name"), y.Name, v => { y.Name = v; RebuildList(); }));

            var tbNames = new List<string> { "" };
            tbNames.AddRange(_titleBlocks.Select(t => t.Name));
            card.Children.Add(MakeChoiceField(AppStrings.T("zones.manager.layout.titleBlock"),
                tbNames.ToArray(), y.TitleBlockTypeName, v =>
                {
                    y.TitleBlockTypeName = v;
                    var t = _titleBlocks.FirstOrDefault(x => x.Name == v);
                    if (t != null && t.HasSize) { y.SheetWidthFt = t.WidthFt; y.SheetHeightFt = t.HeightFt; }
                    RebuildDetail();
                }));

            card.Children.Add(MakeReadOnlyField(AppStrings.T("zones.manager.layout.drawingArea"),
                y.SheetWidthFt > 0
                    ? AppStrings.T("zones.manager.layout.drawingAreaDeclared",
                        $"{(y.SheetWidthFt  - y.MarginLeftFt   - y.MarginRightFt) * 12:0.#}",
                        $"{(y.SheetHeightFt - y.MarginBottomFt - y.MarginTopFt)   * 12:0.#}")
                    : AppStrings.T("zones.manager.layout.drawingAreaUnknown")));
            _detailStack.Children.Add(WrapCard(card));

            var comp = MakeCard(AppStrings.T("zones.manager.layout.composition"));
            comp.Children.Add(MakeChoiceField(AppStrings.T("zones.manager.layout.composition"),
                new[] { ZoneComposition.Continuous, ZoneComposition.Packed }, y.Composition,
                v => { y.Composition = v; RebuildDetail(); }));
            comp.Children.Add(MakeNote(AppStrings.T("zones.manager.layout.compositionNote")));
            _detailStack.Children.Add(WrapCard(comp));

            var grp = MakeCard(AppStrings.T("zones.manager.layout.groups"));
            foreach (var g in y.Groups.OrderBy(g => g.SortIndex).ToList())
                grp.Children.Add(BuildGroupCard(y, g));

            if (y.Groups.Count == 0) grp.Children.Add(MakeNote(AppStrings.T("zones.manager.layout.noGroups")));

            var addRow = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0),
            };
            addRow.Children.Add(MakeButton(AppStrings.T("zones.manager.actions.addGroup"),
                                           () => AddGroup(y), accent: true));
            grp.Children.Add(addRow);

            // Areas with no group at this sheet size will simply never be generated for it.
            // That is silent by nature, so it is stated rather than left to be discovered.
            var loose = Lib.Areas
                .Where(a => a != null && Lib.GroupFor(y, a.Id) == null)
                .Select(a => a.Name)
                .ToList();
            if (loose.Count > 0)
                grp.Children.Add(MakeWarn(AppStrings.T("zones.manager.layout.unassigned",
                                                       loose.Count, string.Join(", ", loose))));

            _detailStack.Children.Add(WrapCard(grp));
        }

        // ── Mutations ─────────────────────────────────────────────────────────
        private void AddBuilding()
        {
            var b = new ZoneBuilding { Id = ZoneId.New(), Name = "Building", SortIndex = Lib.Buildings.Count };
            Lib.Buildings.Add(b);
            _selectedId = b.Id;
            Rebuild();
        }

        private void AddLevel()
        {
            var l = new ZoneLevel
            {
                Id = ZoneId.New(),
                Name = "Level",
                BuildingId = Lib.Buildings.FirstOrDefault()?.Id ?? "",
                SortIndex = Lib.Levels.Count,
            };
            Lib.Levels.Add(l);
            _selectedId = l.Id;
            Rebuild();
        }

        private void AddArea()
        {
            var a = new ZoneArea
            {
                Id = ZoneId.New(),
                Name = "Area",
                BuildingId = Lib.Buildings.FirstOrDefault()?.Id ?? "",
                SortIndex = Lib.Areas.Count,
            };
            Lib.Areas.Add(a);
            _selectedId = a.Id;
            Rebuild();
        }

        private void AddRecipe()
        {
            var r = new ZoneViewRecipe { Id = ZoneId.New(), Name = "Floor Plan", SortIndex = Lib.Recipes.Count };
            Lib.Recipes.Add(r);
            _selectedId = r.Id;
            Rebuild();
        }

        private void AddLayout()
        {
            var y = new ZoneSheetLayout { Id = ZoneId.New(), Name = "Sheet size", SortIndex = Lib.Layouts.Count };
            Lib.Layouts.Add(y);
            _selectedId = y.Id;
            Rebuild();
        }

        private void DeleteSelected()
        {
            if (string.IsNullOrEmpty(_selectedId)) return;
            Lib.Recipes.RemoveAll(r => r.Id == _selectedId);
            Lib.Layouts.RemoveAll(y => y.Id == _selectedId);
            Lib.Areas.RemoveAll(a => a.Id == _selectedId);
            Lib.Levels.RemoveAll(l => l.Id == _selectedId);
            Lib.Buildings.RemoveAll(b => b.Id == _selectedId);
            _selectedId = "";
            Rebuild();
        }

        /// <summary>
        /// Re-solves placements for every layout. Pure library maths — no Revit access, so it
        /// runs on this thread. Captured placements survive unless explicitly overwritten.
        /// </summary>
        private void OnResolvePlacements()
        {
            // Solving needs a document for the drawing area, which this window does not have.
            // Rather than pretend, the declared title-block size captured at launch is used and
            // the status line says the placements are from declared sizes.
            int solved = 0;
            foreach (var layout in Lib.Layouts)
            {
                if (layout == null || string.IsNullOrEmpty(layout.TitleBlockTypeName)) continue;
                var tb = _titleBlocks.FirstOrDefault(t => t.Name == layout.TitleBlockTypeName);
                if (tb == null || !tb.HasSize) continue;

                layout.SheetWidthFt  = tb.WidthFt;
                layout.SheetHeightFt = tb.HeightFt;

                var area = ZoneGroupSolver.DrawingArea.FromSize(
                    tb.WidthFt, tb.HeightFt,
                    layout.MarginLeftFt, layout.MarginRightFt, layout.MarginBottomFt, layout.MarginTopFt);

                foreach (var g in layout.Groups ?? new List<ZoneSheetGroup>())
                {
                    var inputs = new List<ZoneGroupSolver.AreaInput>();
                    foreach (var id in g.AreaIds ?? new List<string>())
                    {
                        var a = Lib.Area(id);
                        if (a == null || !a.HasExtents) continue;
                        inputs.Add(new ZoneGroupSolver.AreaInput
                        {
                            AreaId = a.Id, Label = a.Name,
                            MinX = a.MinX, MinY = a.MinY, MaxX = a.MaxX, MaxY = a.MaxY,
                            AnchorX = a.HasAnchor ? a.AnchorX : (a.MinX + a.MaxX) / 2.0,
                            AnchorY = a.HasAnchor ? a.AnchorY : (a.MinY + a.MaxY) / 2.0,
                        });
                    }
                    if (inputs.Count == 0) continue;

                    var res = ZoneGroupSolver.Solve(inputs, area, layout.Composition,
                                                    layout.GapPaperFt, g.ScaleOverride);
                    string groupKey = inputs.Count > 1 ? g.Id : "";
                    foreach (var p in res.Items)
                    {
                        var existing = Lib.Placement(p.AreaId, layout.TitleBlockTypeName, groupKey);
                        if (existing != null && existing.Source == ZonePlacementSource.Captured) continue;
                        Lib.SetPlacement(new ZoneSheetPlacement
                        {
                            AreaId = p.AreaId,
                            TitleBlockTypeName = layout.TitleBlockTypeName,
                            GroupId = groupKey,
                            SheetWidthFt = tb.WidthFt, SheetHeightFt = tb.HeightFt,
                            AnchorWorldX = p.AnchorWorldX, AnchorWorldY = p.AnchorWorldY,
                            AnchorSheetX = p.AnchorSheetX, AnchorSheetY = p.AnchorSheetY,
                            Scale = res.Scale,
                            Source = ZonePlacementSource.Solved,
                        });
                        solved++;
                    }
                }
            }
            DiagnosticsLog.Info("ZoneManagerWindow", $"Re-solved {solved} placement(s) from declared title block sizes.");
            Rebuild();
        }

        private void UpdateStatus()
            => _statusText.Text = AppStrings.T("zones.manager.status",
                                               Lib.Areas.Count, Lib.Layouts.Count, Lib.Placements.Count);

        // ── Group editing ─────────────────────────────────────────────────────

        /// <summary>
        /// Solves one group live, so the fit and matchline verdicts shown next to it are the
        /// real solver's answer rather than a guess.
        ///
        /// Uses the title block's DECLARED size: this window has no document, so it cannot
        /// measure a placed sheet. That is an estimate, and the layout card says so.
        /// Returns null when the group cannot be solved at all (no title block, no extents).
        /// </summary>
        private ZoneGroupSolver.Result? SolveGroupPreview(ZoneSheetLayout layout, ZoneSheetGroup group)
        {
            if (layout == null || group == null) return null;
            var tb = _titleBlocks.FirstOrDefault(t => t.Name == layout.TitleBlockTypeName);
            if (tb == null || !tb.HasSize) return null;

            var inputs = new List<ZoneGroupSolver.AreaInput>();
            foreach (var id in group.AreaIds ?? new List<string>())
            {
                var a = Lib.Area(id);
                if (a == null || !a.HasExtents) continue;
                inputs.Add(new ZoneGroupSolver.AreaInput
                {
                    AreaId = a.Id, Label = a.Name,
                    MinX = a.MinX, MinY = a.MinY, MaxX = a.MaxX, MaxY = a.MaxY,
                    AnchorX = a.HasAnchor ? a.AnchorX : (a.MinX + a.MaxX) / 2.0,
                    AnchorY = a.HasAnchor ? a.AnchorY : (a.MinY + a.MaxY) / 2.0,
                });
            }
            if (inputs.Count == 0) return null;

            var area = ZoneGroupSolver.DrawingArea.FromSize(
                tb.WidthFt, tb.HeightFt,
                layout.MarginLeftFt, layout.MarginRightFt, layout.MarginBottomFt, layout.MarginTopFt);

            return ZoneGroupSolver.Solve(inputs, area, layout.Composition,
                                          layout.GapPaperFt, group.ScaleOverride);
        }

        private UIElement BuildGroupCard(ZoneSheetLayout layout, ZoneSheetGroup group)
        {
            var body = new StackPanel();

            // ── Row 1: suffix · scale · verdict · delete ──────────────────────
            var row1 = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 7),
            };

            row1.Children.Add(SmallLabel(AppStrings.T("zones.manager.layout.suffix")));

            var suffix = new WpfTextBox
            {
                Text = group.Suffix ?? "",
                Width = 56,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 12, 0),
            };
            suffix.SetResourceReference(WpfTextBox.FontSizeProperty, "LemoineFS_SM");
            suffix.TextChanged += (s, e) => group.Suffix = suffix.Text ?? "";
            row1.Children.Add(suffix);

            row1.Children.Add(SmallLabel(AppStrings.T("zones.manager.layout.scale")));

            // "Auto" plus the standard ladder. Stored as an int, 0 meaning solve it.
            var scaleOptions = new List<string> { AppStrings.T("zones.manager.layout.scaleAuto") };
            scaleOptions.AddRange(ZoneScaleFit.DefaultLadder.Select(ZoneScaleFit.Label));
            string currentScale = group.ScaleOverride > 0
                ? ZoneScaleFit.Label(group.ScaleOverride)
                : scaleOptions[0];

            var scaleCombo = MakeCombo(scaleOptions.ToArray(), currentScale, v =>
            {
                if (string.Equals(v, scaleOptions[0], StringComparison.Ordinal))
                {
                    group.ScaleOverride = 0;
                }
                else
                {
                    int match = ZoneScaleFit.DefaultLadder.FirstOrDefault(d => ZoneScaleFit.Label(d) == v);
                    group.ScaleOverride = match;
                }
                RebuildDetail();
            });
            scaleCombo.Width = 150;
            scaleCombo.Margin = new Thickness(6, 0, 12, 0);
            row1.Children.Add(scaleCombo);

            var solved = SolveGroupPreview(layout, group);
            if (solved != null)
            {
                row1.Children.Add(Chip(ZoneScaleFit.Label(solved.Scale), "LemoineTextDim", "LemoineBorder"));
                row1.Children.Add(solved.Fits
                    ? Chip(AppStrings.T("zones.manager.layout.groupFits",
                                        $"{Math.Min(solved.SlackXFt, solved.SlackYFt) * 12:0.#}"),
                           "LemoineGreen", "LemoineGreen")
                    : Chip(AppStrings.T("zones.manager.layout.groupOverflows"), "LemoineRed", "LemoineRed"));
            }
            else
            {
                row1.Children.Add(Chip(AppStrings.T("zones.manager.layout.cannotSolve"),
                                       "LemoineTextDim", "LemoineBorder"));
            }

            var del = MakeButton(AppStrings.T("zones.manager.actions.deleteGroup"), () =>
            {
                layout.Groups.Remove(group);
                // Placements keyed to this group are meaningless without it, so they go too —
                // leaving them would silently reserve sheet positions for a group that is gone.
                Lib.Placements.RemoveAll(p => string.Equals(p.GroupId, group.Id, StringComparison.Ordinal));
                Rebuild();
            });
            del.Margin = new Thickness(12, 0, 0, 0);
            row1.Children.Add(del);

            body.Children.Add(row1);

            // ── Row 2: the areas on this sheet ────────────────────────────────
            var row2 = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4),
            };
            row2.Children.Add(SmallLabel(AppStrings.T("zones.manager.layout.areas")));

            var chips = new WrapPanel { Margin = new Thickness(6, 0, 0, 0) };
            foreach (var id in (group.AreaIds ?? new List<string>()).ToList())
            {
                var a = Lib.Area(id);
                string label = a?.Name ?? AppStrings.T("zones.manager.layout.missingArea");
                chips.Children.Add(AreaChip(label, () =>
                {
                    group.AreaIds.Remove(id);
                    Lib.Placements.RemoveAll(p =>
                        string.Equals(p.AreaId, id, StringComparison.Ordinal) &&
                        string.Equals(p.GroupId, group.Id, StringComparison.Ordinal));
                    RebuildDetail();
                }));
            }

            // Only areas not already on this sheet are offered — an area cannot be in two
            // groups of the same layout without its placement key becoming ambiguous.
            var available = Lib.Areas
                .Where(a => a != null && Lib.GroupFor(layout, a.Id) == null)
                .OrderBy(a => a.Name, NaturalOrderComparer.OrdinalIgnoreCase)
                .ToList();

            if (available.Count > 0)
            {
                var addOptions = new List<string> { AppStrings.T("zones.manager.layout.addArea") };
                addOptions.AddRange(available.Select(a => a.Name));

                var addCombo = MakeCombo(addOptions.ToArray(), addOptions[0], v =>
                {
                    if (string.Equals(v, addOptions[0], StringComparison.Ordinal)) return;
                    var pick = available.FirstOrDefault(a => a.Name == v);
                    if (pick == null) return;
                    if (group.AreaIds == null) group.AreaIds = new List<string>();
                    group.AreaIds.Add(pick.Id);
                    RebuildDetail();
                });
                addCombo.MinWidth = 120;
                addCombo.Margin = new Thickness(4, 2, 0, 2);
                chips.Children.Add(addCombo);
            }

            row2.Children.Add(chips);
            body.Children.Add(row2);

            // ── Verdict detail: overlaps and overflow, in words ───────────────
            if (solved != null)
            {
                foreach (var o in solved.Overlaps)
                    body.Children.Add(MakeWarn(AppStrings.T("zones.manager.layout.overlapWarn",
                        o.LabelA, o.LabelB,
                        $"{o.OverlapWidthFt * 12:0.#}", $"{o.OverlapHeightFt * 12:0.#}")));

                if (!solved.Fits)
                    body.Children.Add(MakeWarn(AppStrings.T("zones.manager.layout.overflowWarn",
                        $"{Math.Max(0, -solved.SlackXFt) * 12:0.#}",
                        $"{Math.Max(0, -solved.SlackYFt) * 12:0.#}",
                        ZoneScaleFit.Label(solved.Scale))));

                // Continuous composition guarantees matchline continuity when nothing overlaps,
                // and that guarantee is the reason to use it — so it is stated, not assumed.
                if (solved.Overlaps.Count == 0 && solved.Items.Count > 1 &&
                    layout.Composition == ZoneComposition.Continuous)
                    body.Children.Add(MakeOk(AppStrings.T("zones.manager.layout.matchlineOk")));
            }

            var card = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(11, 9, 11, 9),
                Margin = new Thickness(0, 0, 0, 7),
                Child = body,
            };
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            return card;
        }

        private void AddGroup(ZoneSheetLayout layout)
        {
            if (layout.Groups == null) layout.Groups = new List<ZoneSheetGroup>();
            layout.Groups.Add(new ZoneSheetGroup
            {
                Id = ZoneId.New(),
                SortIndex = layout.Groups.Count,
            });
            Rebuild();
        }

        private TextBlock SmallLabel(string text)
        {
            var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }

        private Border Chip(string text, string fgKey, string borderKey)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
            };
            b.SetResourceReference(Border.BorderBrushProperty, borderKey);
            var t = new TextBlock { Text = text, Background = Brushes.Transparent };
            t.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            b.Child = t;
            return b;
        }

        /// <summary>An area chip with an inline remove affordance.</summary>
        private Border AreaChip(string label, Action onRemove)
        {
            var sp = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Background = Brushes.Transparent,
            };

            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            sp.Children.Add(t);

            var x = new TextBlock
            {
                // Codepoint, not a literal glyph or a \uXXXX escape — those break the Edit
                // tool's exact-match (CLAUDE.md), so future edits to this file would fail.
                Text = char.ConvertFromUtf32(0x2715),   // ✕
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                // A null background leaves only the glyph hit-testable; this makes the whole
                // little box clickable.
                Background = Brushes.Transparent,
            };
            x.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            x.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            x.MouseLeftButtonUp += (s, e) =>
            {
                try { onRemove(); }
                catch (Exception ex) { DiagnosticsLog.Error("ZoneManagerWindow: remove area from group", ex); }
                e.Handled = true;
            };
            sp.Children.Add(x);

            var b = new Border
            {
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 3, 6, 3),
                Margin = new Thickness(0, 2, 5, 2),
                Child = sp,
            };
            b.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
            b.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            return b;
        }

        private UIElement MakeWarn(string text)
        {
            var t = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0),
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }

        private UIElement MakeOk(string text)
        {
            var t = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0),
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineGreen");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }

        // ── Small builders ────────────────────────────────────────────────────
        private UIElement MakeGroupHeader(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(8, 6, 0, 2), FontWeight = FontWeights.SemiBold };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        private UIElement MakeEmptyRow(string text)
        {
            var tb = new TextBlock
            {
                Text = text, Margin = new Thickness(10, 10, 10, 10),
                TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        private UIElement MakeListRow(string id, string name, string meta, int indent, string kind)
        {
            bool sel = _selectedId == id;
            var b = new Border
            {
                Padding = new Thickness(8 + indent * 12, 4, 8, 4),
                BorderThickness = new Thickness(sel ? 2 : 0, 0, 0, 0),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
            };
            if (sel)
            {
                b.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                b.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            }

            var g = new WpfGrid { Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var t = new TextBlock
            {
                Text = name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            WpfGrid.SetColumn(t, 0);
            g.Children.Add(t);

            if (!string.IsNullOrEmpty(meta))
            {
                var m = new TextBlock { Text = meta, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                m.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                m.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                WpfGrid.SetColumn(m, 1);
                g.Children.Add(m);
            }

            b.Child = g;
            b.MouseLeftButtonUp += (s, e) =>
            {
                // A group row addresses its parent layout — groups are edited on the layout.
                _selectedId = id.Contains("/") ? id.Substring(0, id.IndexOf('/')) : id;
                RebuildList();
            };
            return b;
        }

        private TextBlock MakeHeading(string text, string kind)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 10), FontWeight = FontWeights.SemiBold };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
            return tb;
        }

        private StackPanel MakeCard(string title)
        {
            var sp = new StackPanel();
            var h = new TextBlock { Text = title.ToUpperInvariant(), Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold };
            h.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            h.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            sp.Children.Add(h);
            return sp;
        }

        private UIElement WrapCard(StackPanel content)
        {
            var b = new Border
            {
                // LemoineRadius_Card (10) — same rounding as tabs and pills.
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(13, 10, 13, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = content,
            };
            b.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            b.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            return b;
        }

        private WpfGrid FieldGrid(string label)
        {
            var g = new WpfGrid { Margin = new Thickness(0, 0, 0, 7) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            WpfGrid.SetColumn(t, 0);
            g.Children.Add(t);
            return g;
        }

        private UIElement MakeTextField(string label, string value, Action<string> onChange)
        {
            var g = FieldGrid(label);
            var box = new WpfTextBox { Text = value ?? "", VerticalContentAlignment = VerticalAlignment.Center };
            box.SetResourceReference(WpfTextBox.FontSizeProperty, "LemoineFS_SM");
            box.TextChanged += (s, e) =>
            {
                try { onChange(box.Text ?? ""); }
                catch (Exception ex) { DiagnosticsLog.Error($"ZoneManagerWindow: edit '{label}'", ex); }
            };
            WpfGrid.SetColumn(box, 1);
            g.Children.Add(box);
            return g;
        }

        private WpfTextBox MakeNumberBox(double value, Action<double> onChange)
        {
            var box = new WpfTextBox { Text = value.ToString("0.###"), VerticalContentAlignment = VerticalAlignment.Center };
            box.SetResourceReference(WpfTextBox.FontSizeProperty, "LemoineFS_SM");
            box.TextChanged += (s, e) =>
            {
                // A half-typed number ("-", "1.") is normal while editing, so a parse failure is
                // ignored rather than logged — only a real value is committed.
                if (double.TryParse(box.Text, out double v))
                {
                    try { onChange(v); }
                    catch (Exception ex) { DiagnosticsLog.Error("ZoneManagerWindow: numeric edit", ex); }
                }
            };
            return box;
        }

        private UIElement MakeNumberField(string label, double value, Action<double> onChange)
        {
            var g = FieldGrid(label);
            var box = MakeNumberBox(value, onChange);
            WpfGrid.SetColumn(box, 1);
            g.Children.Add(box);
            return g;
        }

        private UIElement MakeReadOnlyField(string label, string value)
        {
            var g = FieldGrid(label);
            var t = new TextBlock { Text = value ?? "", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            WpfGrid.SetColumn(t, 1);
            g.Children.Add(t);
            return g;
        }

        private ComboBox MakeCombo(string[] options, string current, Action<string> onChange)
        {
            var c = new ComboBox { VerticalContentAlignment = VerticalAlignment.Center };
            c.SetResourceReference(ComboBox.FontSizeProperty, "LemoineFS_SM");
            foreach (var o in options) c.Items.Add(o);
            c.SelectedItem = options.Contains(current ?? "") ? current : (options.Length > 0 ? options[0] : null);
            c.SelectionChanged += (s, e) =>
            {
                try { onChange(c.SelectedItem as string ?? ""); }
                catch (Exception ex) { DiagnosticsLog.Error("ZoneManagerWindow: choice change", ex); }
            };
            return c;
        }

        private UIElement MakeChoiceField(string label, string[] options, string current, Action<string> onChange)
        {
            var g = FieldGrid(label);
            var c = MakeCombo(options, current, onChange);
            WpfGrid.SetColumn(c, 1);
            g.Children.Add(c);
            return g;
        }

        private UIElement MakeNote(string text)
        {
            var t = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 0),
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }

        private Border MakeButton(string text, Action onClick, bool accent = false)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
            };
            b.SetResourceReference(Border.BorderBrushProperty, accent ? "LemoineAccent" : "LemoineBorder");
            b.SetResourceReference(Border.BackgroundProperty,  accent ? "LemoineAccent" : "LemoineRaised");

            var t = new TextBlock { Text = text, Background = Brushes.Transparent };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            if (accent) t.FontWeight = FontWeights.SemiBold;
            b.Child = t;

            b.MouseLeftButtonUp += (s, e) =>
            {
                try { onClick(); }
                catch (Exception ex) { DiagnosticsLog.Error($"ZoneManagerWindow: '{text}' action", ex); }
            };
            return b;
        }
    }
}
