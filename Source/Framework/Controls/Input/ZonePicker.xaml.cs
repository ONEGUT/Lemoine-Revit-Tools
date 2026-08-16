using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LemoineTools.Framework.Zones;

using WpfVisibility = System.Windows.Visibility;

namespace LemoineTools.Framework.Controls
{
    // =========================================================================
    // ZonePicker — the one control every zone-consuming tool drops in instead of
    // hand-rolling a level list plus a scope-box list.
    //
    // The selectable unit is a CELL: one (Level, Area) pair. That is what tools
    // actually act on — "Area 1 on L03" — and it is why this is a tree rather
    // than two independent pickers, which would let you select a level and an
    // area that never coexist.
    //
    // Contract, deliberately identical to MultiSelectTabs / BrowserTreePicker:
    //
    //   • SelectionChanged fires ONCE at the end of SetLibrary. A ViewModel that
    //     mirrors selection into a field MUST subscribe BEFORE calling SetLibrary
    //     — that callback is the only thing that populates the mirror on init.
    //   • SingleSelect is set BEFORE SetLibrary.
    //   • Children nest under a parent expand caret; unticking a parent clears
    //     its children, so a selected cell can never sit under an unselected
    //     level.
    //   • Search is built in and unconditional — no call-site opt-in.
    // =========================================================================
    public partial class ZonePicker : UserControl
    {
        /// <summary>Which axis nests inside which.</summary>
        public enum TreeOrder
        {
            /// <summary>Building → Level → Area. The default: "what is on L03?" is one glance.</summary>
            LevelThenArea,
            /// <summary>Building → Area → Level. For a tower where an area runs many levels.</summary>
            AreaThenLevel,
        }

        /// <summary>One selected (Level, Area) pair.</summary>
        public sealed class Cell
        {
            public string LevelId { get; set; } = "";
            public string AreaId  { get; set; } = "";
            public string Key => KeyOf(LevelId, AreaId);
            public static string KeyOf(string? levelId, string? areaId)
                => (levelId ?? "") + "::" + (areaId ?? "");
        }

        private ZoneLibrary _library = new ZoneLibrary();
        private readonly HashSet<string> _selected = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _collapsed = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Cell> _cells = new Dictionary<string, Cell>(StringComparer.Ordinal);

        private string _query = "";
        private bool   _suppressEvents;

        private TextBlock _matchCount   = null!;
        private Button    _clearSearch  = null!;

        /// <summary>Set BEFORE <see cref="SetLibrary"/>.</summary>
        public bool SingleSelect { get; set; }

        /// <summary>Set BEFORE <see cref="SetLibrary"/>.</summary>
        public TreeOrder Order { get; set; } = TreeOrder.LevelThenArea;

        public IReadOnlyCollection<Cell> SelectedCells
            => _selected.Select(k => _cells.TryGetValue(k, out var c) ? c : null)
                        .Where(c => c != null).Select(c => c!).ToList();

        public event Action<IReadOnlyCollection<Cell>>? SelectionChanged;

        public ZonePicker()
        {
            InitializeComponent();

            _outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _outer.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            _summaryBar.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _summaryBar.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            _summaryText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _summaryText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            _searchHint.Text = AppStrings.T("zones.picker.searchHint");
            _searchHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _searchHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _searchBox.SetResourceReference(TextBox.FontSizeProperty,      "LemoineFS_SM");
            _searchBox.TextChanged += OnSearchChanged;

            _matchCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            _matchCount.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _matchCount.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _matchCount.Visibility = WpfVisibility.Collapsed;
            _searchActions.Children.Add(_matchCount);

            _clearSearch = MakeLinkButton(AppStrings.T("zones.picker.clear"), () => _searchBox.Text = "");
            _clearSearch.Visibility = WpfVisibility.Collapsed;
            _searchActions.Children.Add(_clearSearch);

            _summaryActions.Children.Add(MakeLinkButton(AppStrings.T("zones.picker.selectAll"), SelectAllVisible));
            _summaryActions.Children.Add(MakeLinkButton(AppStrings.T("zones.picker.clearAll"),  ClearAll));
        }

        /// <summary>
        /// Loads a library and rebuilds the tree. Fires <see cref="SelectionChanged"/> exactly
        /// once, at the end — subscribe first.
        /// </summary>
        public void SetLibrary(ZoneLibrary? library, IEnumerable<Cell>? initialSelection = null)
        {
            _library = library ?? new ZoneLibrary();

            _suppressEvents = true;
            try
            {
                _searchBox.Text = "";
                _query = "";
                _selected.Clear();
                _cells.Clear();

                foreach (var c in initialSelection ?? Enumerable.Empty<Cell>())
                    if (c != null) _selected.Add(c.Key);

                Rebuild();
            }
            finally { _suppressEvents = false; }

            Fire();
        }

        // ── Search ────────────────────────────────────────────────────────────
        private void OnSearchChanged(object? sender, TextChangedEventArgs e)
        {
            if (_suppressEvents) return;
            _query = (_searchBox.Text ?? "").Trim();
            _searchHint.Visibility  = string.IsNullOrEmpty(_query) ? WpfVisibility.Visible   : WpfVisibility.Collapsed;
            _clearSearch.Visibility = string.IsNullOrEmpty(_query) ? WpfVisibility.Collapsed : WpfVisibility.Visible;
            _matchCount.Visibility  = string.IsNullOrEmpty(_query) ? WpfVisibility.Collapsed : WpfVisibility.Visible;
            Rebuild();
        }

        private bool Matches(params string?[] fields)
        {
            if (string.IsNullOrEmpty(_query)) return true;
            foreach (var f in fields)
                if (!string.IsNullOrEmpty(f) &&
                    f!.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ── Build ─────────────────────────────────────────────────────────────
        private void Rebuild()
        {
            _treeStack.Children.Clear();
            int visibleCells = 0;

            var buildings = _library.Buildings.OrderBy(b => b.SortIndex)
                                              .ThenBy(b => b.Name, NaturalOrderComparer.OrdinalIgnoreCase)
                                              .ToList();

            // A library with no buildings still has levels/areas worth showing, so an
            // unassigned bucket is rendered rather than the tree silently coming out empty.
            var groups = new List<(string Id, string Name)>();
            foreach (var b in buildings) groups.Add((b.Id, b.Name));
            bool anyUnassigned = _library.Levels.Any(l => _library.Building(l.BuildingId) == null) ||
                                 _library.Areas.Any(a => _library.Building(a.BuildingId) == null);
            if (anyUnassigned || groups.Count == 0)
                groups.Add(("", AppStrings.T("zones.picker.unassigned")));

            foreach (var g in groups)
            {
                var levels = _library.Levels
                    .Where(l => string.Equals(l.BuildingId ?? "", g.Id, StringComparison.Ordinal))
                    .OrderBy(l => l.SortIndex).ThenBy(l => l.ElevationFt).ToList();
                var areas = _library.Areas
                    .Where(a => string.Equals(a.BuildingId ?? "", g.Id, StringComparison.Ordinal))
                    .OrderBy(a => a.SortIndex)
                    .ThenBy(a => a.Name, NaturalOrderComparer.OrdinalIgnoreCase).ToList();

                if (levels.Count == 0 && areas.Count == 0) continue;

                var rows = new List<UIElement>();
                if (Order == TreeOrder.LevelThenArea)
                {
                    foreach (var lv in levels)
                    {
                        var kids = areas.Where(a => _library.AreaAppliesTo(a, lv)).ToList();
                        var cellRows = new List<UIElement>();
                        foreach (var a in kids)
                        {
                            if (!Matches(a.Name, a.Code, lv.Name, lv.Code)) continue;
                            cellRows.Add(MakeCellRow(lv, a, a.Name, 2));
                            visibleCells++;
                        }
                        if (cellRows.Count == 0) continue;
                        rows.Add(MakeParentRow(lv.Id, lv.Name, $"{cellRows.Count}", 1,
                                               cellRows, ParentCells(lv, kids)));
                        rows.AddRange(VisibleChildren(lv.Id, cellRows));
                    }
                }
                else
                {
                    foreach (var a in areas)
                    {
                        var kids = levels.Where(l => _library.AreaAppliesTo(a, l)).ToList();
                        var cellRows = new List<UIElement>();
                        foreach (var lv in kids)
                        {
                            if (!Matches(a.Name, a.Code, lv.Name, lv.Code)) continue;
                            cellRows.Add(MakeCellRow(lv, a, lv.Name, 2));
                            visibleCells++;
                        }
                        if (cellRows.Count == 0) continue;
                        rows.Add(MakeParentRow(a.Id, a.Name, $"{cellRows.Count}", 1,
                                               cellRows, kids.Select(l => Cell.KeyOf(l.Id, a.Id)).ToList()));
                        rows.AddRange(VisibleChildren(a.Id, cellRows));
                    }
                }

                if (rows.Count == 0) continue;

                _treeStack.Children.Add(MakeHeaderRow(g.Name));
                foreach (var r in rows) _treeStack.Children.Add(r);
            }

            if (_treeStack.Children.Count == 0)
                _treeStack.Children.Add(MakeEmptyRow(
                    string.IsNullOrEmpty(_query)
                        ? AppStrings.T("zones.picker.emptyLibrary")
                        : AppStrings.T("zones.picker.noMatches", _query)));

            if (!string.IsNullOrEmpty(_query))
                _matchCount.Text = AppStrings.T("zones.picker.matchCount", visibleCells);

            UpdateSummary();
        }

        private List<string> ParentCells(ZoneLevel lv, List<ZoneArea> areas)
            => areas.Select(a => Cell.KeyOf(lv.Id, a.Id)).ToList();

        private IEnumerable<UIElement> VisibleChildren(string parentId, List<UIElement> rows)
            => _collapsed.Contains(parentId) ? Enumerable.Empty<UIElement>() : rows;

        // ── Rows ──────────────────────────────────────────────────────────────
        private UIElement MakeHeaderRow(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                Margin = new Thickness(4, 6, 0, 2),
                FontWeight = FontWeights.SemiBold,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        private UIElement MakeEmptyRow(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                Margin = new Thickness(6, 10, 6, 10),
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        /// <summary>
        /// A parent row: expand caret + tri-state checkbox governing every cell beneath it.
        /// Unticking clears the children, so a selected cell can never sit under an
        /// unselected parent.
        /// </summary>
        private UIElement MakeParentRow(string parentId, string name, string badge, int indent,
                                        List<UIElement> childRows, List<string> childKeys)
        {
            var grid = new Grid { Margin = new Thickness(indent * 14, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // Without a background the row is only hit-testable on its glyphs.
            grid.Background = Brushes.Transparent;

            bool collapsed = _collapsed.Contains(parentId);
            var caret = new TextBlock
            {
                Text = collapsed ? "▸" : "▾",
                Width = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
            };
            caret.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            caret.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            caret.MouseLeftButtonUp += (s, e) =>
            {
                if (collapsed) _collapsed.Remove(parentId); else _collapsed.Add(parentId);
                Rebuild();
                e.Handled = true;
            };
            Grid.SetColumn(caret, 0);
            grid.Children.Add(caret);

            int sel = childKeys.Count(_selected.Contains);
            var cb = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                IsThreeState = false,
                // Indeterminate when some-but-not-all children are selected.
                IsChecked = sel == 0 ? false : (sel == childKeys.Count ? true : (bool?)null),
            };
            if (SingleSelect) cb.Visibility = WpfVisibility.Collapsed;
            cb.Click += (s, e) =>
            {
                bool turnOn = cb.IsChecked == true;
                foreach (var k in childKeys)
                {
                    if (turnOn) _selected.Add(k); else _selected.Remove(k);
                }
                Rebuild();
                Fire();
            };
            Grid.SetColumn(cb, 1);
            grid.Children.Add(cb);

            var label = new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            label.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Grid.SetColumn(label, 2);
            grid.Children.Add(label);

            var badgeTb = new TextBlock
            {
                Text = badge,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 4, 0),
            };
            badgeTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            badgeTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Grid.SetColumn(badgeTb, 3);
            grid.Children.Add(badgeTb);

            return grid;
        }

        private UIElement MakeCellRow(ZoneLevel level, ZoneArea area, string label, int indent)
        {
            string key = Cell.KeyOf(level.Id, area.Id);
            _cells[key] = new Cell { LevelId = level.Id, AreaId = area.Id };

            var grid = new Grid { Margin = new Thickness(indent * 14, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Background = Brushes.Transparent;

            var cb = new CheckBox
            {
                IsChecked = _selected.Contains(key),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            cb.Click += (s, e) =>
            {
                if (cb.IsChecked == true)
                {
                    // Single-select clears every prior pick across the whole tree.
                    if (SingleSelect) _selected.Clear();
                    _selected.Add(key);
                }
                else _selected.Remove(key);
                Rebuild();
                Fire();
            };
            Grid.SetColumn(cb, 0);
            grid.Children.Add(cb);

            var tb = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Grid.SetColumn(tb, 1);
            grid.Children.Add(tb);

            // A cell whose extents were never resolved cannot be placed, and saying so here is
            // cheaper than letting the run discover it later.
            string boxName = _library.ScopeBoxFor(area, level);
            bool resolved = _library.ExtentsFor(area, level, out _, out _, out _, out _);
            var note = new TextBlock
            {
                Text = resolved ? boxName : AppStrings.T("zones.picker.noExtents"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 4, 0),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, resolved ? "LemoineTextDim" : "LemoineRed");
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Grid.SetColumn(note, 2);
            grid.Children.Add(note);

            return grid;
        }

        // ── Bulk actions ──────────────────────────────────────────────────────
        private void SelectAllVisible()
        {
            if (SingleSelect) return;
            foreach (var kv in _cells) _selected.Add(kv.Key);
            Rebuild();
            Fire();
        }

        private void ClearAll()
        {
            _selected.Clear();
            Rebuild();
            Fire();
        }

        private void UpdateSummary()
            => _summaryText.Text = AppStrings.T("zones.picker.summary", _selected.Count, _cells.Count);

        private void Fire()
        {
            if (_suppressEvents) return;
            try { SelectionChanged?.Invoke(SelectedCells); }
            catch (Exception ex) { DiagnosticsLog.Error("ZonePicker: SelectionChanged subscriber", ex); }
        }

        private Button MakeLinkButton(string text, Action onClick)
        {
            var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 1, 6, 1) };
            b.SetResourceReference(Button.FontSizeProperty, "LemoineFS_SM");
            b.Click += (s, e) =>
            {
                try { onClick(); }
                catch (Exception ex) { DiagnosticsLog.Error($"ZonePicker: '{text}' action", ex); }
            };
            return b;
        }
    }
}
