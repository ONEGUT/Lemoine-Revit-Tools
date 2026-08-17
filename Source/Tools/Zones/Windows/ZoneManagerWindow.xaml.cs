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
    // ONE TREE, not three tabs. Buildings own levels, levels own areas AND the
    // view definitions every one of those areas inherits, and each area may
    // override any view field. Sheet sets are a sibling root, because a set is
    // keyed by title-block size and spans every level and area — nesting it
    // under one parent would misrepresent what it covers.
    //
    // Creation is INLINE: every parent row carries a trailing + that makes the
    // right kind of child inside THAT parent. The old rail-footer buttons
    // guessed the parent with Buildings.FirstOrDefault(), so on a two-building
    // project a new level silently landed under the wrong one.
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
        // Node-kind tokens — logic identifiers, deliberately not externalized.
        private const string KindBuilding   = "building";
        private const string KindLevel      = "level";
        private const string KindArea       = "area";
        private const string KindViewDef    = "view";       // a level's view definition
        private const string KindAreaView   = "areaview";   // that view AS SEEN BY one area
        private const string KindSheetRoot  = "sheetroot";
        private const string KindSheetSet   = "sheetset";
        private const string KindSheetGroup = "group";

        private string _query = "";

        /// <summary>Selected node key ("kind:id" / "kind:parent/child"), "" for none.</summary>
        private string _selected = "";

        /// <summary>Expanded node keys. Buildings and the sheet root start open.</summary>
        private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.Ordinal);
        private bool _expansionSeeded;

        private readonly string _docTitle;
        private readonly List<ZoneTitleBlocks.TitleBlockType> _titleBlocks;
        private readonly List<ZoneScopeBoxSync.BoxInfo> _scopeBoxes;
        private readonly List<string> _hostLevelNames;

        /// <summary>Set once the Discover window has been asked to open, so re-entry reloads.</summary>
        private bool _discoverLaunched;

        private ZoneLibrary Lib => ZoneSettings.Instance.Library;

        public ZoneManagerWindow(
            string? docTitle = null,
            List<ZoneTitleBlocks.TitleBlockType>? titleBlocks = null,
            List<ZoneScopeBoxSync.BoxInfo>? scopeBoxes = null,
            List<string>? hostLevelNames = null)
        {
            _docTitle       = docTitle ?? "";
            _titleBlocks    = titleBlocks    ?? new List<ZoneTitleBlocks.TitleBlockType>();
            _scopeBoxes     = scopeBoxes     ?? new List<ZoneScopeBoxSync.BoxInfo>();
            _hostLevelNames = hostLevelNames ?? new List<string>();

            InitializeComponent();
            Loaded    += OnLoaded;
            Activated += OnWindowActivated;

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
                RebuildTree();
            };

            _statusText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _statusText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            // Dragging the toolbar moves the window — WindowStyle=None means no OS caption.
            _toolbarBorder.Background = Brushes.Transparent;
            _toolbarBorder.MouseLeftButtonDown += (s, ev) =>
            {
                if (ev.ClickCount == 1) { try { DragMove(); } catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneManagerWindow: DragMove", ex); } }
            };

            // A scope box picked in a previous session, or resized in the model since, is
            // reconciled before anything is drawn — so the extents field never reads
            // "unresolved" for a box that is sitting right there in the document.
            AdoptScopeBoxExtents(announce: true);

            BuildToolbarActions();
            BuildFooterActions();
            Rebuild();
        }

        /// <summary>
        /// Discover works on the same project library singleton this window edits, and its
        /// launcher re-reads that library from the document — so by the time focus comes back,
        /// ZoneSettings.Instance.Library may be a DIFFERENT object than the one this window
        /// last drew. Lib is a property, not a cached field, so simply rebuilding picks the new
        /// one up. (This window saves before launching Discover, so nothing on screen is lost
        /// to that re-read.) Same shape as Auto Filters' own post-Discover refresh.
        /// </summary>
        private void OnWindowActivated(object? sender, EventArgs e)
        {
            if (!_discoverLaunched) return;
            _discoverLaunched = false;

            try
            {
                // Newly discovered areas adopt scope boxes by name; resolve their extents now
                // rather than leaving them reading "unresolved" until something else touches them.
                AdoptScopeBoxExtents(announce: false);
                _selected = "";
                _expansionSeeded = false;
                Rebuild();
                FlashStatus(AppStrings.T("zones.manager.status.reloaded"));
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneManagerWindow: refresh after Discover", ex);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            AppSettings.Instance.ThemeChanged  -= OnThemeChanged;
            AppSettings.Instance.UiSizeChanged -= OnUiSizeChanged;
            Dispatcher.UnhandledException      -= OnDispatcherUnhandledException;
            Activated                          -= OnWindowActivated;

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

        // ── Scope box reconciliation ──────────────────────────────────────────

        /// <summary>
        /// Pulls every adopted area's extents from the scope box that carries its name.
        ///
        /// This RE-ADOPTS a box that was resized since adoption, which the underlying
        /// ZoneScopeBoxSync deliberately never did on its own: re-solving extents moves an
        /// ExtentsCentre anchor, and a moved anchor against an unchanged sheet coordinate
        /// shifts the drawing (SheetAnchorMath measured 5/8" on a 1/8" plot for a 10 ft
        /// change). That behaviour was chosen explicitly, so the movement is never silent —
        /// every re-adopt reports its drift and whether placements existed for that area.
        /// </summary>
        private void AdoptScopeBoxExtents(bool announce)
        {
            var byName = new Dictionary<string, ZoneScopeBoxSync.BoxInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in _scopeBoxes)
                if (!string.IsNullOrEmpty(b.Name) && b.HasBounds && !byName.ContainsKey(b.Name))
                    byName[b.Name] = b;

            int resolved = 0, moved = 0, missing = 0;

            foreach (var area in Lib.Areas)
            {
                if (area == null) continue;
                if (area.Definition != ZoneExtentMode.ScopeBox || string.IsNullOrEmpty(area.ScopeBoxName))
                    continue;

                if (!byName.TryGetValue(area.ScopeBoxName, out var box))
                {
                    missing++;
                    // The area is never deleted — the library outlives any one box.
                    DiagnosticsLog.Warn("ZoneManagerWindow",
                        $"Area '{area.Name}' adopts scope box '{area.ScopeBoxName}', which is not in this " +
                        "document. Its extents are left as they were.");
                    continue;
                }

                if (!area.HasExtents)
                {
                    ZoneScopeBoxSync.AdoptExtents(area, box);
                    resolved++;
                    continue;
                }

                double drift = Math.Max(
                    Math.Max(Math.Abs(box.MinX - area.MinX), Math.Abs(box.MaxX - area.MaxX)),
                    Math.Max(Math.Abs(box.MinY - area.MinY), Math.Abs(box.MaxY - area.MaxY)));

                // A hair of difference is model noise, not a resize.
                if (drift <= 1e-4) continue;

                bool hadPlacements = Lib.Placements != null &&
                    Lib.Placements.Any(p => p != null &&
                        string.Equals(p.AreaId, area.Id, StringComparison.Ordinal));

                ZoneScopeBoxSync.AdoptExtents(area, box);
                moved++;
                DiagnosticsLog.Warn("ZoneManagerWindow",
                    $"Area '{area.Name}': scope box '{area.ScopeBoxName}' was resized " +
                    $"(largest edge difference {drift:0.##}'). Extents re-adopted automatically" +
                    (hadPlacements
                        ? " — this area HAS stored sheet placements, so its views will move on the sheet."
                        : " — no sheet placements existed for it.") );
            }

            if (announce)
            {
                // A zero result is stated too: silence is indistinguishable from a collector
                // that read nothing at all.
                DiagnosticsLog.Info("ZoneManagerWindow",
                    $"Scope box reconcile: {resolved} first resolve(s), {moved} re-adopted after resize, " +
                    $"{missing} missing, across {Lib.Areas.Count} area(s) and {_scopeBoxes.Count} box(es).");
            }
        }

        // ── Chrome actions ────────────────────────────────────────────────────
        private void BuildToolbarActions()
        {
            _toolbarActions.Children.Clear();
            _toolbarActions.Children.Add(MakeButton(AppStrings.T("zones.manager.actions.discover"), OnDiscover));
            _toolbarActions.Children.Add(MakeButton(AppStrings.T("zones.manager.close"), Close, accent: false));
        }

        private void BuildFooterActions()
        {
            _footerActions.Children.Clear();
            _footerActions.Children.Add(MakeButton(AppStrings.T("zones.manager.actions.resolvePlacements"),
                                                   OnResolvePlacements, accent: false));
            _footerActions.Children.Add(MakeButton(AppStrings.T("zones.manager.close"), Close, accent: true));
        }

        /// <summary>
        /// Opens Zone Discover. The library is persisted FIRST so Discover reads what is on
        /// screen, then the request is marshalled to Revit's main thread — the window setup
        /// enumerates link instances and this thread has no document.
        /// </summary>
        private void OnDiscover()
        {
            try { ZoneSettings.Save(); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneManagerWindow: save before Discover", ex);
                FlashStatus(AppStrings.T("zones.manager.status.saveFailed"));
                return;
            }

            var evt = App.ZoneOpenDiscoverEvent;
            if (evt == null)
            {
                DiagnosticsLog.Warn("ZoneManagerWindow", "Discover unavailable: event handler not registered.");
                FlashStatus(AppStrings.T("zones.manager.status.discoverUnavailable"));
                return;
            }

            _discoverLaunched = true;
            evt.Raise();
            FlashStatus(AppStrings.T("zones.manager.status.openingDiscover"));
        }

        private void FlashStatus(string text)
        {
            _statusText.Text = text;
        }

        // ── Rebuild ───────────────────────────────────────────────────────────
        private void Rebuild()
        {
            RebuildTree();
            UpdateStatus();
        }

        private bool Matches(params string?[] fields)
        {
            if (string.IsNullOrEmpty(_query)) return true;
            foreach (var f in fields)
                if (!string.IsNullOrEmpty(f) && f!.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static string Key(string kind, string id) => kind + ":" + id;
        private static string Key(string kind, string parentId, string childId)
            => kind + ":" + parentId + "/" + childId;

        private bool IsExpanded(string key) => _expanded.Contains(key);

        private void Toggle(string key)
        {
            if (!_expanded.Remove(key)) _expanded.Add(key);
            RebuildTree();
        }

        // ── The tree ──────────────────────────────────────────────────────────
        private void RebuildTree()
        {
            _listStack.Children.Clear();
            _railActions.Children.Clear();

            if (!_expansionSeeded)
            {
                // Buildings and the sheet root open by default; deeper levels stay closed so a
                // large project does not open as one enormous list.
                foreach (var b in Lib.Buildings) _expanded.Add(Key(KindBuilding, b.Id));
                _expanded.Add(Key(KindSheetRoot, ""));
                _expansionSeeded = true;
            }

            bool searching = !string.IsNullOrEmpty(_query);
            int rows = 0;

            var buildings = Lib.Buildings.OrderBy(b => b.SortIndex)
                                         .ThenBy(b => b.Name, NaturalOrderComparer.OrdinalIgnoreCase)
                                         .ToList();

            foreach (var b in buildings)
                rows += AddBuildingNode(b, searching);

            // Levels and areas whose building was deleted would otherwise vanish from the UI
            // while still living in the library. They are shown under an explicit orphan header.
            rows += AddOrphans(searching);

            if (buildings.Count == 0 && rows == 0 && !searching)
                _listStack.Children.Add(MakeEmptyRow(AppStrings.T("zones.picker.emptyLibrary")));

            AddSheetRoot(searching);

            // Root-level creation: the only + with no parent row to hang from.
            _listStack.Children.Add(MakeAddRow(AppStrings.T("zones.manager.actions.addBuilding"),
                                               indent: 0, onAdd: AddBuilding));

            if (searching && rows == 0)
                _listStack.Children.Add(MakeEmptyRow(AppStrings.T("zones.manager.noMatches")));

            _railActions.Children.Add(MakeButton(AppStrings.T("zones.manager.actions.delete"), DeleteSelected));

            RebuildDetail();
        }

        private int AddBuildingNode(ZoneBuilding b, bool searching)
        {
            string key = Key(KindBuilding, b.Id);

            var levels = Lib.Levels.Where(l => (l.BuildingId ?? "") == b.Id)
                                   .OrderBy(l => l.SortIndex).ThenBy(l => l.ElevationFt).ToList();

            // While searching, a branch is shown when it or anything under it matches.
            bool self = Matches(b.Name, b.Code);
            var childRows = new List<UIElement>();
            int matched = 0;

            bool open = searching || IsExpanded(key);
            if (open)
            {
                foreach (var lv in levels)
                {
                    var built = BuildLevelNode(lv, searching, out int m);
                    matched += m;
                    if (m > 0 || !searching) childRows.AddRange(built);
                }
            }

            if (searching && !self && matched == 0) return 0;

            _listStack.Children.Add(MakeNodeRow(
                key, b.Name, AppStrings.T("zones.manager.node.building"),
                indent: 0, hasChildren: true, expanded: open,
                addLabel: AppStrings.T("zones.manager.actions.addLevel"),
                onAdd: () => AddLevel(b.Id)));

            foreach (var r in childRows) _listStack.Children.Add(r);

            if (open)
                _listStack.Children.Add(MakeAddRow(AppStrings.T("zones.manager.actions.addLevel"),
                                                   indent: 1, onAdd: () => AddLevel(b.Id)));

            return 1 + matched;
        }

        private List<UIElement> BuildLevelNode(ZoneLevel lv, bool searching, out int matched)
        {
            var rows = new List<UIElement>();
            string key = Key(KindLevel, lv.Id);
            bool self = Matches(lv.Name, lv.Code, lv.HostLevelName);
            matched = self ? 1 : 0;

            var areas = Lib.Areas
                .Where(a => Lib.AreaAppliesTo(a, lv))
                .OrderBy(a => a.SortIndex)
                .ThenBy(a => a.Name, NaturalOrderComparer.OrdinalIgnoreCase)
                .ToList();

            bool open = searching || IsExpanded(key);
            var child = new List<UIElement>();

            if (open)
            {
                // The level's OWN view definitions — the seed every area under it inherits.
                foreach (var v in (lv.ViewDefs ?? new List<ZoneViewDef>()).OrderBy(v => v.SortIndex))
                {
                    if (searching && !Matches(v.Name, v.Kind)) continue;
                    if (searching) matched++;
                    child.Add(MakeNodeRow(
                        Key(KindViewDef, lv.Id, v.Id), v.Name, v.Kind,
                        indent: 2, hasChildren: false, expanded: false));
                }
                child.Add(MakeAddRow(AppStrings.T("zones.manager.actions.addView"),
                                     indent: 2, onAdd: () => AddViewDef(lv)));

                foreach (var a in areas)
                {
                    var built = BuildAreaNode(lv, a, searching, out int m);
                    matched += m;
                    if (m > 0 || !searching) child.AddRange(built);
                }

                child.Add(MakeAddRow(AppStrings.T("zones.manager.actions.addArea"),
                                     indent: 2, onAdd: () => AddArea(lv)));
            }

            if (searching && matched == 0) return rows;

            rows.Add(MakeNodeRow(
                key, lv.Name, $"{lv.ElevationFt:0.##}'",
                indent: 1, hasChildren: true, expanded: open,
                addLabel: AppStrings.T("zones.manager.actions.addArea"),
                onAdd: () => AddArea(lv)));
            rows.AddRange(child);
            return rows;
        }

        private List<UIElement> BuildAreaNode(ZoneLevel lv, ZoneArea a, bool searching, out int matched)
        {
            var rows = new List<UIElement>();
            string key = Key(KindArea, a.Id);
            bool self = Matches(a.Name, a.Code, a.ScopeBoxName);
            matched = self ? 1 : 0;

            bool open = IsExpanded(key) && !searching;
            var child = new List<UIElement>();

            if (open)
            {
                // The same views as the level, seen through this area's overrides.
                foreach (var v in (lv.ViewDefs ?? new List<ZoneViewDef>()).OrderBy(v => v.SortIndex))
                {
                    var ov = Lib.OverrideFor(a, v.Id);
                    int n = ov?.OverriddenFields?.Count ?? 0;
                    string meta = n > 0
                        ? AppStrings.T("zones.manager.view.overriddenN", n)
                        : AppStrings.T("zones.manager.view.inherited");
                    child.Add(MakeNodeRow(
                        Key(KindAreaView, a.Id, v.Id), v.Name, meta,
                        indent: 3, hasChildren: false, expanded: false));
                }
                if ((lv.ViewDefs?.Count ?? 0) == 0)
                    child.Add(MakeEmptyRow(AppStrings.T("zones.manager.view.noneOnLevel"), indent: 3));
            }

            if (searching && matched == 0) return rows;

            rows.Add(MakeNodeRow(
                key, a.Name, a.HasExtents ? $"{a.WidthFt:0.#}' × {a.DepthFt:0.#}'" : a.ScopeBoxName,
                indent: 2, hasChildren: true, expanded: open));
            rows.AddRange(child);
            return rows;
        }

        /// <summary>
        /// Levels and areas whose building no longer exists. They are in the library, so they
        /// are shown — a record that is invisible but still saved is exactly the kind of thing
        /// that reads as data loss.
        /// </summary>
        private int AddOrphans(bool searching)
        {
            var ids = new HashSet<string>(Lib.Buildings.Select(b => b.Id), StringComparer.Ordinal);
            var orphanLevels = Lib.Levels
                .Where(l => string.IsNullOrEmpty(l.BuildingId) || !ids.Contains(l.BuildingId))
                .Where(l => Matches(l.Name, l.Code))
                .OrderBy(l => l.ElevationFt).ToList();

            if (orphanLevels.Count == 0) return 0;

            _listStack.Children.Add(MakeGroupHeader(AppStrings.T("zones.picker.unassigned")));
            foreach (var lv in orphanLevels)
                _listStack.Children.Add(MakeNodeRow(
                    Key(KindLevel, lv.Id), lv.Name, $"{lv.ElevationFt:0.##}'",
                    indent: 1, hasChildren: false, expanded: false));
            return orphanLevels.Count;
        }

        private void AddSheetRoot(bool searching)
        {
            string key = Key(KindSheetRoot, "");
            bool open = searching || IsExpanded(key);

            _listStack.Children.Add(MakeNodeRow(
                key, AppStrings.T("zones.manager.node.sheets"), Lib.SheetSets.Count.ToString(),
                indent: 0, hasChildren: true, expanded: open,
                addLabel: AppStrings.T("zones.manager.actions.addSheetSet"),
                onAdd: AddSheetSet));

            if (!open) return;

            foreach (var y in Lib.SheetSets.OrderBy(y => y.SortIndex)
                                           .ThenBy(y => y.Name, NaturalOrderComparer.OrdinalIgnoreCase))
            {
                if (searching && !Matches(y.Name, y.TitleBlockTypeName)) continue;

                string sKey = Key(KindSheetSet, y.Id);
                bool sOpen = searching || IsExpanded(sKey);

                _listStack.Children.Add(MakeNodeRow(
                    sKey, y.Name, y.TitleBlockTypeName,
                    indent: 1, hasChildren: true, expanded: sOpen,
                    addLabel: AppStrings.T("zones.manager.actions.addGroup"),
                    onAdd: () => AddGroup(y)));

                if (!sOpen) continue;

                foreach (var g in (y.Groups ?? new List<ZoneSheetGroup>()).OrderBy(g => g.SortIndex))
                {
                    string names = g.AreaIds != null && g.AreaIds.Count > 0
                        ? string.Join(" + ", g.AreaIds.Select(id => Lib.Area(id)?.Name
                                                                   ?? AppStrings.T("zones.manager.layout.missingArea")))
                        : AppStrings.T("zones.manager.layout.noAreas");
                    _listStack.Children.Add(MakeNodeRow(
                        Key(KindSheetGroup, y.Id, g.Id), names, g.Suffix,
                        indent: 2, hasChildren: false, expanded: false));
                }

                _listStack.Children.Add(MakeAddRow(AppStrings.T("zones.manager.actions.addGroup"),
                                                   indent: 2, onAdd: () => AddGroup(y)));
            }

            if (Lib.SheetSets.Count == 0)
                _listStack.Children.Add(MakeEmptyRow(AppStrings.T("zones.manager.layout.noSets"), indent: 1));

            _listStack.Children.Add(MakeAddRow(AppStrings.T("zones.manager.actions.addSheetSet"),
                                               indent: 1, onAdd: AddSheetSet));
        }

        // ── Detail pane ───────────────────────────────────────────────────────
        private void RebuildDetail()
        {
            _detailStack.Children.Clear();

            if (string.IsNullOrEmpty(_selected))
            {
                _detailStack.Children.Add(MakeEmptyRow(AppStrings.T("zones.manager.pickSomething")));
                return;
            }

            int colon = _selected.IndexOf(':');
            if (colon < 0) { _detailStack.Children.Add(MakeEmptyRow(AppStrings.T("zones.manager.pickSomething"))); return; }

            string kind = _selected.Substring(0, colon);
            string rest = _selected.Substring(colon + 1);
            string parent = rest, child = "";
            int slash = rest.IndexOf('/');
            if (slash >= 0) { parent = rest.Substring(0, slash); child = rest.Substring(slash + 1); }

            switch (kind)
            {
                case KindBuilding:
                {
                    // The case that was MISSING before: adding a building selected it, nothing
                    // rendered here, and the button read as broken.
                    var b = Lib.Building(parent);
                    if (b != null) { BuildBuildingDetail(b); return; }
                    break;
                }
                case KindLevel:
                {
                    var l = Lib.Level(parent);
                    if (l != null) { BuildLevelDetail(l); return; }
                    break;
                }
                case KindArea:
                {
                    var a = Lib.Area(parent);
                    if (a != null) { BuildAreaDetail(a); return; }
                    break;
                }
                case KindViewDef:
                {
                    var lv = Lib.Level(parent);
                    var v  = lv?.ViewDefs?.FirstOrDefault(x => x.Id == child);
                    if (lv != null && v != null) { BuildViewDefDetail(lv, v); return; }
                    break;
                }
                case KindAreaView:
                {
                    var a = Lib.Area(parent);
                    var v = Lib.LevelViewDef(child);
                    if (a != null && v != null) { BuildAreaViewDetail(a, v); return; }
                    break;
                }
                case KindSheetSet:
                case KindSheetGroup:
                {
                    var y = Lib.SheetSet(parent);
                    if (y != null) { BuildSheetSetDetail(y); return; }
                    break;
                }
            }

            _detailStack.Children.Add(MakeEmptyRow(AppStrings.T("zones.manager.gone")));
        }

        private void BuildBuildingDetail(ZoneBuilding b)
        {
            _detailStack.Children.Add(MakeHeading(b.Name));

            var card = MakeCard(AppStrings.T("zones.manager.node.building"));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.area.name"), b.Name,
                v => { b.Name = v; RebuildTree(); }));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.area.code"), b.Code, v => b.Code = v));
            card.Children.Add(MakeReadOnlyField(AppStrings.T("zones.manager.building.levels"),
                Lib.Levels.Count(l => (l.BuildingId ?? "") == b.Id).ToString()));
            _detailStack.Children.Add(WrapCard(card));
        }

        private void BuildLevelDetail(ZoneLevel l)
        {
            _detailStack.Children.Add(MakeHeading(l.Name));

            var card = MakeCard(AppStrings.T("zones.manager.node.level"));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.area.name"), l.Name,
                v => { l.Name = v; RebuildTree(); }));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.area.code"), l.Code, v => l.Code = v));

            var levelNames = new List<string> { "" };
            levelNames.AddRange(_hostLevelNames);
            card.Children.Add(MakeChoiceField(AppStrings.T("zones.manager.level.hostLevel"),
                levelNames.ToArray(), l.HostLevelName, v => l.HostLevelName = v));

            card.Children.Add(MakeNumberField(AppStrings.T("zones.manager.level.bandBase"),
                l.BandBaseOffsetFt, v => l.BandBaseOffsetFt = v));
            card.Children.Add(MakeNumberField(AppStrings.T("zones.manager.level.bandTop"),
                l.BandTopOffsetFt, v => l.BandTopOffsetFt = v));
            _detailStack.Children.Add(WrapCard(card));

            var views = MakeCard(AppStrings.T("zones.manager.level.views"));
            views.Children.Add(MakeNote(AppStrings.T("zones.manager.level.viewsNote")));
            foreach (var v in (l.ViewDefs ?? new List<ZoneViewDef>()).OrderBy(v => v.SortIndex))
                views.Children.Add(MakeReadOnlyField(v.Name, v.Kind));
            if ((l.ViewDefs?.Count ?? 0) == 0)
                views.Children.Add(MakeNote(AppStrings.T("zones.manager.level.noViews")));
            _detailStack.Children.Add(WrapCard(views));
        }

        private void BuildAreaDetail(ZoneArea a)
        {
            _detailStack.Children.Add(MakeHeading(a.Name));

            var card = MakeCard(AppStrings.T("zones.manager.area.section"));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.area.name"), a.Name, v => { a.Name = v; RebuildTree(); }));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.area.code"), a.Code, v => a.Code = v));
            card.Children.Add(MakeChoiceField(AppStrings.T("zones.manager.area.definition"),
                new[] { ZoneExtentMode.ScopeBox, ZoneExtentMode.Grids, ZoneExtentMode.RoomCluster, ZoneExtentMode.Manual },
                a.Definition, v => { a.Definition = v; RebuildDetail(); }));

            if (a.Definition == ZoneExtentMode.ScopeBox)
            {
                var names = new List<string> { "" };
                names.AddRange(_scopeBoxes.Where(b => !string.IsNullOrEmpty(b.Name))
                                          .Select(b => b.Name)
                                          .Distinct(StringComparer.OrdinalIgnoreCase)
                                          .OrderBy(n => n, NaturalOrderComparer.OrdinalIgnoreCase));

                card.Children.Add(MakeChoiceField(AppStrings.T("zones.manager.area.scopeBox"),
                    names.ToArray(), a.ScopeBoxName, v =>
                    {
                        a.ScopeBoxName = v;

                        // Picking the box RESOLVES the extents there and then. Only storing the
                        // name is what left this reading "not solved" no matter what you chose —
                        // and the bounds were being thrown away at capture, so nothing downstream
                        // could have fixed it either.
                        var box = _scopeBoxes.FirstOrDefault(
                            b => string.Equals(b.Name, v, StringComparison.OrdinalIgnoreCase) && b.HasBounds);
                        if (box != null) ZoneScopeBoxSync.AdoptExtents(a, box);
                        else if (!string.IsNullOrEmpty(v))
                            DiagnosticsLog.Warn("ZoneManagerWindow",
                                $"Scope box '{v}' has no readable bounds; area '{a.Name}' is left unresolved.");

                        RebuildTree();
                        RebuildDetail();
                    }));

                if (!string.IsNullOrEmpty(a.ScopeBoxName) &&
                    !_scopeBoxes.Any(b => string.Equals(b.Name, a.ScopeBoxName, StringComparison.OrdinalIgnoreCase)))
                    card.Children.Add(MakeWarn(AppStrings.T("zones.manager.area.boxMissing", a.ScopeBoxName)));
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

        /// <summary>A view definition as the LEVEL owns it — the seed every area inherits.</summary>
        private void BuildViewDefDetail(ZoneLevel level, ZoneViewDef r)
        {
            _detailStack.Children.Add(MakeHeading(r.Name));

            var card = MakeCard(AppStrings.T("zones.manager.viewDef.section"));
            card.Children.Add(MakeNote(AppStrings.T("zones.manager.viewDef.levelNote", level.Name)));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.viewDef.name"), r.Name, v => { r.Name = v; RebuildTree(); }));
            card.Children.Add(MakeChoiceField(AppStrings.T("zones.manager.viewDef.kind"),
                new[] { ZoneViewKind.FloorPlan, ZoneViewKind.CeilingPlan, ZoneViewKind.ThreeD, ZoneViewKind.Section, ZoneViewKind.AreaPlan },
                r.Kind, v => { r.Kind = v; RebuildDetail(); }));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.viewDef.familyType"), r.ViewFamilyTypeName, v => r.ViewFamilyTypeName = v));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.viewDef.template"),   r.ViewTemplateName,   v => r.ViewTemplateName   = v));
            card.Children.Add(MakeChoiceField(AppStrings.T("zones.manager.viewDef.scaleMode"),
                new[] { ZoneScaleMode.FitToTitleBlock, ZoneScaleMode.Fixed }, r.ScaleMode, v => { r.ScaleMode = v; RebuildDetail(); }));
            if (r.ScaleMode == ZoneScaleMode.Fixed)
                card.Children.Add(MakeScaleField(AppStrings.T("zones.manager.viewDef.scale"), r.Scale, v => r.Scale = v));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.viewDef.namePattern"), r.NamePattern, v => r.NamePattern = v));
            card.Children.Add(MakeNote(AppStrings.T("zones.manager.viewDef.patternNote")));
            _detailStack.Children.Add(WrapCard(card));

            if (ZoneViewKind.IsPlan(r.Kind) && r.ViewRange != null)
            {
                var vr = MakeCard(AppStrings.T("zones.manager.viewDef.viewRange"));
                vr.Children.Add(MakePlaneRow(AppStrings.T("zones.manager.viewDef.top"),    r.ViewRange.Top));
                vr.Children.Add(MakePlaneRow(AppStrings.T("zones.manager.viewDef.cut"),    r.ViewRange.CutPlane));
                vr.Children.Add(MakePlaneRow(AppStrings.T("zones.manager.viewDef.bottom"), r.ViewRange.Bottom));
                vr.Children.Add(MakePlaneRow(AppStrings.T("zones.manager.viewDef.depth"),  r.ViewRange.ViewDepth));
                _detailStack.Children.Add(WrapCard(vr));
            }
        }

        /// <summary>
        /// One level view def AS SEEN BY one area: every field shows the inherited value until
        /// it is explicitly overridden, per field. An area with nothing ticked stores nothing.
        /// </summary>
        private void BuildAreaViewDetail(ZoneArea area, ZoneViewDef baseDef)
        {
            _detailStack.Children.Add(MakeHeading($"{area.Name} · {baseDef.Name}"));

            var ov = Lib.OverrideFor(area, baseDef.Id);
            var effective = Lib.ResolveViewDef(null, area, baseDef);

            var card = MakeCard(AppStrings.T("zones.manager.view.overrideSection"));
            card.Children.Add(MakeNote(AppStrings.T("zones.manager.view.overrideNote")));

            card.Children.Add(OverridableChoice(area, baseDef, ZoneViewFields.Kind,
                AppStrings.T("zones.manager.viewDef.kind"),
                new[] { ZoneViewKind.FloorPlan, ZoneViewKind.CeilingPlan, ZoneViewKind.ThreeD, ZoneViewKind.Section, ZoneViewKind.AreaPlan },
                effective.Kind, (vals, v) => vals.Kind = v));

            card.Children.Add(OverridableText(area, baseDef, ZoneViewFields.ViewTemplateName,
                AppStrings.T("zones.manager.viewDef.template"),
                effective.ViewTemplateName, (vals, v) => vals.ViewTemplateName = v));

            card.Children.Add(OverridableText(area, baseDef, ZoneViewFields.ViewFamilyTypeName,
                AppStrings.T("zones.manager.viewDef.familyType"),
                effective.ViewFamilyTypeName, (vals, v) => vals.ViewFamilyTypeName = v));

            card.Children.Add(OverridableChoice(area, baseDef, ZoneViewFields.ScaleMode,
                AppStrings.T("zones.manager.viewDef.scaleMode"),
                new[] { ZoneScaleMode.FitToTitleBlock, ZoneScaleMode.Fixed },
                effective.ScaleMode, (vals, v) => vals.ScaleMode = v));

            card.Children.Add(OverridableScale(area, baseDef, ZoneViewFields.Scale,
                AppStrings.T("zones.manager.viewDef.scale"), effective.Scale,
                (vals, v) => vals.Scale = v));

            card.Children.Add(OverridableText(area, baseDef, ZoneViewFields.NamePattern,
                AppStrings.T("zones.manager.viewDef.namePattern"),
                effective.NamePattern, (vals, v) => vals.NamePattern = v));

            _detailStack.Children.Add(WrapCard(card));

            int n = ov?.OverriddenFields?.Count ?? 0;
            if (n > 0)
            {
                var reset = MakeCard(AppStrings.T("zones.manager.view.resetSection"));
                reset.Children.Add(MakeNote(AppStrings.T("zones.manager.view.overriddenN", n)));
                var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                row.Children.Add(MakeButton(AppStrings.T("zones.manager.view.resetAll"), () =>
                {
                    area.ViewOverrides?.RemoveAll(o => o != null && o.BaseId == baseDef.Id);
                    RebuildTree();
                    RebuildDetail();
                }));
                reset.Children.Add(row);
                _detailStack.Children.Add(WrapCard(reset));
            }
        }

        // ── Overridable field rows ────────────────────────────────────────────

        /// <summary>
        /// A field row with a leading override tick. Unticked, the control is disabled and shows
        /// the LEVEL's value; ticked, it edits this area's own. Unticking removes the field from
        /// the override rather than writing the inherited value back, so "inherited" keeps
        /// tracking the level rather than freezing today's value.
        /// </summary>
        private WpfGrid OverrideRow(ZoneArea area, ZoneViewDef baseDef, string field, string label,
                                    out bool overridden)
        {
            var ov = Lib.OverrideFor(area, baseDef.Id);
            overridden = ov?.Overrides(field) == true;

            var g = new WpfGrid { Margin = new Thickness(0, 0, 0, 7) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var tick = new CheckBox
            {
                IsChecked = overridden,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = AppStrings.T("zones.manager.view.overrideTip"),
            };
            tick.Click += (s, e) =>
            {
                try
                {
                    if (tick.IsChecked == true)
                    {
                        var rec = Lib.EnsureOverride(area, baseDef.Id);
                        if (!rec.OverriddenFields.Contains(field)) rec.OverriddenFields.Add(field);
                    }
                    else
                    {
                        var rec = Lib.OverrideFor(area, baseDef.Id);
                        rec?.OverriddenFields?.Remove(field);
                        // An override with nothing left in it is noise in the file.
                        if (rec != null && (rec.OverriddenFields?.Count ?? 0) == 0)
                            area.ViewOverrides?.Remove(rec);
                    }
                    RebuildTree();
                    RebuildDetail();
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error($"ZoneManagerWindow: toggle override '{field}'", ex);
                }
            };
            WpfGrid.SetColumn(tick, 0);
            g.Children.Add(tick);

            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, overridden ? "LemoineText" : "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            WpfGrid.SetColumn(t, 1);
            g.Children.Add(t);

            return g;
        }

        private UIElement OverridableText(ZoneArea area, ZoneViewDef baseDef, string field,
                                          string label, string value, Action<ZoneViewDef, string> set)
        {
            var g = OverrideRow(area, baseDef, field, label, out bool overridden);
            var box = new WpfTextBox
            {
                Text = value ?? "",
                VerticalContentAlignment = VerticalAlignment.Center,
                IsEnabled = overridden,
            };
            box.SetResourceReference(WpfTextBox.FontSizeProperty, "LemoineFS_SM");
            box.TextChanged += (s, e) =>
            {
                if (!overridden) return;
                try { set(Lib.EnsureOverride(area, baseDef.Id).Values, box.Text ?? ""); }
                catch (Exception ex) { DiagnosticsLog.Error($"ZoneManagerWindow: edit override '{field}'", ex); }
            };
            WpfGrid.SetColumn(box, 2);
            g.Children.Add(box);
            return g;
        }

        private UIElement OverridableChoice(ZoneArea area, ZoneViewDef baseDef, string field,
                                            string label, string[] options, string value,
                                            Action<ZoneViewDef, string> set)
        {
            var g = OverrideRow(area, baseDef, field, label, out bool overridden);
            var c = MakeCombo(options, value, v =>
            {
                if (!overridden) return;
                set(Lib.EnsureOverride(area, baseDef.Id).Values, v);
            });
            c.IsEnabled = overridden;
            WpfGrid.SetColumn(c, 2);
            g.Children.Add(c);
            return g;
        }

        private UIElement OverridableScale(ZoneArea area, ZoneViewDef baseDef, string field,
                                           string label, int value, Action<ZoneViewDef, int> set)
        {
            var g = OverrideRow(area, baseDef, field, label, out bool overridden);
            var options = ZoneScaleFit.DefaultLadder.Select(ZoneScaleFit.Label).ToArray();
            var c = MakeCombo(options, ZoneScaleFit.Label(value), v =>
            {
                if (!overridden) return;
                int match = ZoneScaleFit.DefaultLadder.FirstOrDefault(d => ZoneScaleFit.Label(d) == v);
                if (match > 0) set(Lib.EnsureOverride(area, baseDef.Id).Values, match);
            });
            c.IsEnabled = overridden;
            WpfGrid.SetColumn(c, 2);
            g.Children.Add(c);
            return g;
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

        /// <summary>A view scale chosen from the standard ladder — never a free-typed integer.</summary>
        private UIElement MakeScaleField(string label, int value, Action<int> onChange)
        {
            var g = FieldGrid(label);
            var options = ZoneScaleFit.DefaultLadder.Select(ZoneScaleFit.Label).ToArray();
            var c = MakeCombo(options, ZoneScaleFit.Label(value), v =>
            {
                int match = ZoneScaleFit.DefaultLadder.FirstOrDefault(d => ZoneScaleFit.Label(d) == v);
                if (match > 0) onChange(match);
            });
            WpfGrid.SetColumn(c, 1);
            g.Children.Add(c);
            return g;
        }

        private void BuildSheetSetDetail(ZoneSheetSet y)
        {
            _detailStack.Children.Add(MakeHeading(y.Name));

            var card = MakeCard(AppStrings.T("zones.manager.layout.section"));
            card.Children.Add(MakeTextField(AppStrings.T("zones.manager.layout.name"), y.Name, v => { y.Name = v; RebuildTree(); }));

            var tbNames = new List<string> { "" };
            tbNames.AddRange(_titleBlocks.Select(t => t.Name));
            card.Children.Add(MakeChoiceField(AppStrings.T("zones.manager.layout.titleBlock"),
                tbNames.ToArray(), y.TitleBlockTypeName, v =>
                {
                    y.TitleBlockTypeName = v;
                    var t = _titleBlocks.FirstOrDefault(x => x.Name == v);
                    if (t != null && t.HasSize) { y.SheetWidthFt = t.WidthFt; y.SheetHeightFt = t.HeightFt; }
                    RebuildTree();
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
            foreach (var g in (y.Groups ?? new List<ZoneSheetGroup>()).OrderBy(g => g.SortIndex).ToList())
                grp.Children.Add(BuildGroupCard(y, g));

            if ((y.Groups?.Count ?? 0) == 0) grp.Children.Add(MakeNote(AppStrings.T("zones.manager.layout.noGroups")));

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
            var b = new ZoneBuilding
            {
                Id = ZoneId.New(),
                Name = AppStrings.T("zones.manager.defaults.building"),
                SortIndex = Lib.Buildings.Count,
            };
            Lib.Buildings.Add(b);
            _expanded.Add(Key(KindBuilding, b.Id));
            _selected = Key(KindBuilding, b.Id);
            Rebuild();
        }

        /// <summary>Creates a level inside the building whose + was clicked — never a guessed one.</summary>
        private void AddLevel(string buildingId)
        {
            var l = new ZoneLevel
            {
                Id = ZoneId.New(),
                Name = AppStrings.T("zones.manager.defaults.level"),
                BuildingId = buildingId ?? "",
                SortIndex = Lib.Levels.Count,
            };
            Lib.Levels.Add(l);
            _expanded.Add(Key(KindBuilding, l.BuildingId));
            _expanded.Add(Key(KindLevel, l.Id));
            _selected = Key(KindLevel, l.Id);
            Rebuild();
        }

        /// <summary>
        /// Creates an area on the level whose + was clicked. It is scoped to that level
        /// explicitly (AppliesToLevelIds), so a new area never silently appears on every level
        /// of the building.
        /// </summary>
        private void AddArea(ZoneLevel level)
        {
            var a = new ZoneArea
            {
                Id = ZoneId.New(),
                Name = AppStrings.T("zones.manager.defaults.area"),
                BuildingId = level?.BuildingId ?? "",
                SortIndex = Lib.Areas.Count,
            };
            if (level != null) a.AppliesToLevelIds.Add(level.Id);
            Lib.Areas.Add(a);
            if (level != null) _expanded.Add(Key(KindLevel, level.Id));
            _expanded.Add(Key(KindArea, a.Id));
            _selected = Key(KindArea, a.Id);
            Rebuild();
        }

        /// <summary>Creates a view definition ON A LEVEL — every area under it inherits it.</summary>
        private void AddViewDef(ZoneLevel level)
        {
            if (level == null) return;
            if (level.ViewDefs == null) level.ViewDefs = new List<ZoneViewDef>();

            var v = new ZoneViewDef
            {
                Id = ZoneId.New(),
                Name = AppStrings.T("zones.manager.defaults.view"),
                SortIndex = level.ViewDefs.Count,
            };
            level.ViewDefs.Add(v);
            _expanded.Add(Key(KindLevel, level.Id));
            _selected = Key(KindViewDef, level.Id, v.Id);
            Rebuild();
        }

        private void AddSheetSet()
        {
            var y = new ZoneSheetSet
            {
                Id = ZoneId.New(),
                Name = AppStrings.T("zones.manager.defaults.sheetSet"),
                SortIndex = Lib.SheetSets.Count,
            };
            Lib.SheetSets.Add(y);
            _expanded.Add(Key(KindSheetRoot, ""));
            _expanded.Add(Key(KindSheetSet, y.Id));
            _selected = Key(KindSheetSet, y.Id);
            Rebuild();
        }

        private void AddGroup(ZoneSheetSet sheetSet)
        {
            if (sheetSet == null) return;
            if (sheetSet.Groups == null) sheetSet.Groups = new List<ZoneSheetGroup>();
            var g = new ZoneSheetGroup { Id = ZoneId.New(), SortIndex = sheetSet.Groups.Count };
            sheetSet.Groups.Add(g);
            _expanded.Add(Key(KindSheetRoot, ""));
            _expanded.Add(Key(KindSheetSet, sheetSet.Id));
            _selected = Key(KindSheetSet, sheetSet.Id);
            Rebuild();
        }

        /// <summary>
        /// Deletes whatever is selected, by KIND. The old version fired a RemoveAll at every
        /// collection with the raw id, which only worked because ids happened not to collide.
        /// </summary>
        private void DeleteSelected()
        {
            if (string.IsNullOrEmpty(_selected)) return;

            int colon = _selected.IndexOf(':');
            if (colon < 0) return;
            string kind = _selected.Substring(0, colon);
            string rest = _selected.Substring(colon + 1);
            string parent = rest, child = "";
            int slash = rest.IndexOf('/');
            if (slash >= 0) { parent = rest.Substring(0, slash); child = rest.Substring(slash + 1); }

            switch (kind)
            {
                case KindBuilding:
                    // Its levels and areas are NOT deleted with it — they surface under the
                    // orphan header instead, so a mis-click cannot take a floor's work with it.
                    Lib.Buildings.RemoveAll(b => b.Id == parent);
                    break;

                case KindLevel:
                    Lib.Levels.RemoveAll(l => l.Id == parent);
                    break;

                case KindArea:
                    Lib.Areas.RemoveAll(a => a.Id == parent);
                    // Placements keyed to a deleted area would silently reserve sheet space.
                    Lib.Placements?.RemoveAll(p => p != null && p.AreaId == parent);
                    foreach (var y in Lib.SheetSets)
                        foreach (var g in y.Groups ?? new List<ZoneSheetGroup>())
                            g.AreaIds?.Remove(parent);
                    break;

                case KindViewDef:
                {
                    var lv = Lib.Level(parent);
                    lv?.ViewDefs?.RemoveAll(v => v.Id == child);
                    // Every area's override of that def is meaningless once the def is gone.
                    foreach (var a in Lib.Areas)
                        a.ViewOverrides?.RemoveAll(o => o != null && o.BaseId == child);
                    break;
                }

                case KindAreaView:
                {
                    // Deleting an area's view means dropping its overrides — the view itself
                    // belongs to the level and is not this row's to remove.
                    var a = Lib.Area(parent);
                    a?.ViewOverrides?.RemoveAll(o => o != null && o.BaseId == child);
                    break;
                }

                case KindSheetSet:
                    Lib.SheetSets.RemoveAll(y => y.Id == parent);
                    break;

                case KindSheetGroup:
                {
                    var y = Lib.SheetSet(parent);
                    y?.Groups?.RemoveAll(g => g.Id == child);
                    Lib.Placements?.RemoveAll(p => p != null && p.GroupId == child);
                    break;
                }

                default:
                    return;
            }

            _selected = "";
            Rebuild();
        }

        /// <summary>
        /// Re-solves placements for every sheet set. Pure library maths — no Revit access, so it
        /// runs on this thread. Captured placements survive unless explicitly overwritten.
        /// </summary>
        private void OnResolvePlacements()
        {
            // Solving needs a document for the drawing area, which this window does not have.
            // Rather than pretend, the declared title-block size captured at launch is used and
            // the status line says the placements are from declared sizes.
            int solved = 0;
            foreach (var sheetSet in Lib.SheetSets)
            {
                if (sheetSet == null || string.IsNullOrEmpty(sheetSet.TitleBlockTypeName)) continue;
                var tb = _titleBlocks.FirstOrDefault(t => t.Name == sheetSet.TitleBlockTypeName);
                if (tb == null || !tb.HasSize) continue;

                sheetSet.SheetWidthFt  = tb.WidthFt;
                sheetSet.SheetHeightFt = tb.HeightFt;

                var area = ZoneGroupSolver.DrawingArea.FromSize(
                    tb.WidthFt, tb.HeightFt,
                    sheetSet.MarginLeftFt, sheetSet.MarginRightFt, sheetSet.MarginBottomFt, sheetSet.MarginTopFt);

                foreach (var g in sheetSet.Groups ?? new List<ZoneSheetGroup>())
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

                    var res = ZoneGroupSolver.Solve(inputs, area, sheetSet.Composition,
                                                    sheetSet.GapPaperFt, g.ScaleOverride);
                    string groupKey = inputs.Count > 1 ? g.Id : "";
                    foreach (var p in res.Items)
                    {
                        var existing = Lib.Placement(p.AreaId, sheetSet.TitleBlockTypeName, groupKey);
                        if (existing != null && existing.Source == ZonePlacementSource.Captured) continue;
                        Lib.SetPlacement(new ZoneSheetPlacement
                        {
                            AreaId = p.AreaId,
                            TitleBlockTypeName = sheetSet.TitleBlockTypeName,
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
            => _statusText.Text = AppStrings.T("zones.manager.statusLine",
                                               Lib.Areas.Count, Lib.SheetSets.Count, Lib.Placements.Count);

        // ── Group editing ─────────────────────────────────────────────────────

        /// <summary>
        /// Solves one group live, so the fit and matchline verdicts shown next to it are the
        /// real solver's answer rather than a guess.
        ///
        /// Uses the title block's DECLARED size: this window has no document, so it cannot
        /// measure a placed sheet. That is an estimate, and the sheet-set card says so.
        /// Returns null when the group cannot be solved at all (no title block, no extents).
        /// </summary>
        private ZoneGroupSolver.Result? SolveGroupPreview(ZoneSheetSet sheetSet, ZoneSheetGroup group)
        {
            if (sheetSet == null || group == null) return null;
            var tb = _titleBlocks.FirstOrDefault(t => t.Name == sheetSet.TitleBlockTypeName);
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
                sheetSet.MarginLeftFt, sheetSet.MarginRightFt, sheetSet.MarginBottomFt, sheetSet.MarginTopFt);

            return ZoneGroupSolver.Solve(inputs, area, sheetSet.Composition,
                                          sheetSet.GapPaperFt, group.ScaleOverride);
        }

        private UIElement BuildGroupCard(ZoneSheetSet sheetSet, ZoneSheetGroup group)
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

            var solved = SolveGroupPreview(sheetSet, group);
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
                sheetSet.Groups.Remove(group);
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
            // groups of the same sheet set without its placement key becoming ambiguous.
            var available = Lib.Areas
                .Where(a => a != null && Lib.GroupFor(sheetSet, a.Id) == null)
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
                    sheetSet.Composition == ZoneComposition.Continuous)
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
                Text = char.ConvertFromUtf32(0x2715),   // cross
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

        // ── Tree row builders ─────────────────────────────────────────────────
        private UIElement MakeGroupHeader(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(8, 8, 0, 2), FontWeight = FontWeights.SemiBold };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        private UIElement MakeEmptyRow(string text, int indent = 0)
        {
            var tb = new TextBlock
            {
                Text = text, Margin = new Thickness(10 + indent * 14, 6, 10, 6),
                TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        /// <summary>
        /// One tree row: caret | name | meta | +.
        ///
        /// The name label is deliberately left-aligned and shrink-to-text so it never covers the
        /// rest of the row; the leftover space stays row background and remains clickable.
        /// </summary>
        private UIElement MakeNodeRow(string key, string name, string meta, int indent,
                                      bool hasChildren, bool expanded,
                                      string? addLabel = null, Action? onAdd = null)
        {
            bool sel = _selected == key;

            var b = new Border
            {
                Padding = new Thickness(4 + indent * 14, 3, 6, 3),
                BorderThickness = new Thickness(sel ? 2 : 0, 0, 0, 0),
                Cursor = Cursors.Hand,
                // Without a background only the glyphs are hit-testable, so most of the row
                // would silently ignore clicks.
                Background = Brushes.Transparent,
            };
            if (sel)
            {
                b.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                b.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            }

            var g = new WpfGrid { Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // caret
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // meta
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // +

            if (hasChildren)
            {
                var caret = new TextBlock
                {
                    // Codepoints, never literal glyphs — the Edit tool cannot match those.
                    Text = expanded ? char.ConvertFromUtf32(0x25BE) : char.ConvertFromUtf32(0x25B8),
                    Width = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent,
                };
                caret.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                caret.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                caret.MouseLeftButtonUp += (s, e) => { Toggle(key); e.Handled = true; };
                WpfGrid.SetColumn(caret, 0);
                g.Children.Add(caret);
            }
            else
            {
                var spacer = new Border { Width = 14, Background = Brushes.Transparent };
                WpfGrid.SetColumn(spacer, 0);
                g.Children.Add(spacer);
            }

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
            WpfGrid.SetColumn(t, 1);
            g.Children.Add(t);

            if (!string.IsNullOrEmpty(meta))
            {
                var m = new TextBlock
                {
                    Text = meta, Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                m.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                m.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                WpfGrid.SetColumn(m, 2);
                g.Children.Add(m);
            }

            if (onAdd != null)
            {
                var plus = new TextBlock
                {
                    Text = "+",
                    Margin = new Thickness(8, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand,
                    ToolTip = addLabel,
                    Background = Brushes.Transparent,
                    FontWeight = FontWeights.Bold,
                };
                plus.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                plus.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                plus.MouseLeftButtonUp += (s, e) =>
                {
                    try { onAdd(); }
                    catch (Exception ex) { DiagnosticsLog.Error($"ZoneManagerWindow: add under '{key}'", ex); }
                    e.Handled = true;
                };
                WpfGrid.SetColumn(plus, 3);
                g.Children.Add(plus);
            }

            b.Child = g;
            b.MouseLeftButtonUp += (s, e) =>
            {
                _selected = key;
                RebuildTree();
            };
            return b;
        }

        /// <summary>An explicit "+ add X" row at the end of a parent's children.</summary>
        private UIElement MakeAddRow(string label, int indent, Action onAdd)
        {
            var b = new Border
            {
                Padding = new Thickness(18 + indent * 14, 3, 6, 3),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
            };
            var t = new TextBlock
            {
                Text = "+ " + label,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            b.Child = t;
            b.MouseLeftButtonUp += (s, e) =>
            {
                try { onAdd(); }
                catch (Exception ex) { DiagnosticsLog.Error($"ZoneManagerWindow: '{label}'", ex); }
                e.Handled = true;
            };
            return b;
        }

        // ── Small builders ────────────────────────────────────────────────────
        private TextBlock MakeHeading(string text)
        {
            var tb = new TextBlock
            {
                Text = text, Margin = new Thickness(0, 0, 0, 10),
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            };
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

            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
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
