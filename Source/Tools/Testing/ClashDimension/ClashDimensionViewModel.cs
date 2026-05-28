using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.AutoFilters;

using WpfGrid       = System.Windows.Controls.Grid;
using WpfTextBox    = System.Windows.Controls.TextBox;
using WpfComboBox   = System.Windows.Controls.ComboBox;
using WpfVisibility = System.Windows.Visibility;

namespace LemoineTools.Tools.Testing
{
    public class ClashDimensionViewModel : ILemoineTool
    {
        public string Title    => "Clash Dimension";
        public string RunLabel => "Place Clash Annotations →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Views",              required: true),
            new StepDefinition("S2", "Group 1 — Source Filters",  required: true),
            new StepDefinition("S3", "Group 2 — Target Filters",  required: true),
            new StepDefinition("S4", "Grids & Slab References",   required: false),
            new StepDefinition("S5", "Settings & Review",         required: false),
        };

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── Filter group state ────────────────────────────────────────────────
        private readonly HashSet<string> _group1Keys = new HashSet<string>();
        private readonly HashSet<string> _group2Keys = new HashSet<string>();

        // displayKey → FilterRuleConfig
        private readonly Dictionary<string, FilterRuleConfig>  _displayKeyToRule    = new Dictionary<string, FilterRuleConfig>();
        // displayKey → persistKey ("{tradeId}::{ruleId}")
        private readonly Dictionary<string, string>            _displayKeyToPersist = new Dictionary<string, string>();
        // persistKey → displayKey
        private readonly Dictionary<string, string>            _persistKeyToDisplay = new Dictionary<string, string>();
        // trade label → list of display keys (for LemoineMultiSelectTabs)
        private readonly Dictionary<string, List<string>>      _filterGroups        = new Dictionary<string, List<string>>();

        // ── Grid / floor state ────────────────────────────────────────────────
        // Each display name maps to (linkInstId, elemId); linkInstId=0 means host doc.
        private readonly Dictionary<string, (long lnk, long id)> _gridDisplayToRef  = new Dictionary<string, (long, long)>();
        private readonly Dictionary<string, (long lnk, long id)> _floorDisplayToRef = new Dictionary<string, (long, long)>();
        private readonly HashSet<string> _selectedGridNames = new HashSet<string>();
        private string? _selectedFloorDisplay;   // single floor; null = "(None)"

        // ── View state ────────────────────────────────────────────────────────
        private List<string>                           _selectedViewNames = new List<string>();
        private readonly Dictionary<string, ElementId> _viewNameToId      = new Dictionary<string, ElementId>();

        // ── Settings ──────────────────────────────────────────────────────────
        private double _toleranceMm       = ClashDimensionSettings.Instance.ToleranceMm;
        private string _dimStyleName      = ClashDimensionSettings.Instance.DimStyleName;
        private double _dimLineOffsetMm   = ClashDimensionSettings.Instance.DimLineOffsetMm;
        private string _dimTarget         = ClashDimensionSettings.Instance.DimTarget;
        private string _fillStyle         = ClashDimensionSettings.Instance.FillStyle;
        private string _crossLineTypeName = ClashDimensionSettings.Instance.CrossLineTypeName;
        private bool   _clearPrevious     = ClashDimensionSettings.Instance.ClearPrevious;
        private int    _maxClashes        = ClashDimensionSettings.Instance.MaxClashes;
        private bool   _showAll           = ClashDimensionSettings.Instance.ShowAllDocuments;

        // ── Revit data ────────────────────────────────────────────────────────
        private readonly List<string>   _dimStyleNames;
        private readonly List<View>     _allViews;
        private readonly List<string>   _lineStyleNames;
        private readonly GridFloorData  _activeViewData;
        private readonly GridFloorData  _allData;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly ClashDimensionEventHandler? _handler;
        private readonly ExternalEvent?              _event;

        // ── Named struct (no anonymous tuple arrays) ──────────────────────────
        private struct CardDef
        {
            public string       Label;
            public Func<string> Value;
            public CardDef(string label, Func<string> value) { Label = label; Value = value; }
        }

        // ── Constructor ───────────────────────────────────────────────────────
        public ClashDimensionViewModel(
            ClashDimensionEventHandler? handler,
            ExternalEvent?              externalEvent,
            List<string>                dimStyleNames,
            List<View>                  allViews,
            GridFloorData               activeViewData,
            GridFloorData               allData,
            List<string>                lineStyleNames)
        {
            _handler        = handler;
            _event          = externalEvent;
            _dimStyleNames  = dimStyleNames;
            _allViews       = allViews;
            _activeViewData = activeViewData;
            _allData        = allData;
            _lineStyleNames = lineStyleNames;

            foreach (var v in _allViews)
                if (!_viewNameToId.ContainsKey(v.Name))
                    _viewNameToId[v.Name] = v.Id;

            // Build lookup dicts from allData (the full set)
            for (int i = 0; i < allData.GridNames.Count; i++)
                _gridDisplayToRef[allData.GridNames[i]] = (allData.GridLinkIds[i], allData.GridIds[i]);
            for (int i = 0; i < allData.FloorNames.Count; i++)
                _floorDisplayToRef[allData.FloorNames[i]] = (allData.FloorLinkIds[i], allData.FloorIds[i]);

            BuildFilterMappings();
            RestoreFromSettings();
        }

        // ── Build display/persist key maps from AutoFiltersSettings ───────────
        private void BuildFilterMappings()
        {
            var trades = AutoFiltersSettings.Instance.Trades;

            var nameCount = new Dictionary<string, int>();
            foreach (var trade in trades)
                foreach (var rule in trade.Rules)
                {
                    if (!nameCount.ContainsKey(rule.Name)) nameCount[rule.Name] = 0;
                    nameCount[rule.Name]++;
                }

            foreach (var trade in trades)
            {
                var groupItems = new List<string>();
                foreach (var rule in trade.Rules)
                {
                    string displayKey = nameCount.TryGetValue(rule.Name, out int cnt) && cnt > 1
                        ? $"{trade.Label} — {rule.Name}"
                        : rule.Name;
                    string persistKey = $"{trade.Id}::{rule.Id}";

                    _displayKeyToRule[displayKey]    = rule;
                    _displayKeyToPersist[displayKey] = persistKey;
                    _persistKeyToDisplay[persistKey] = displayKey;
                    groupItems.Add(displayKey);
                }
                if (groupItems.Count > 0)
                    _filterGroups[trade.Label] = groupItems;
            }
        }

        private void RestoreFromSettings()
        {
            var s = ClashDimensionSettings.Instance;

            // Filter selections
            foreach (var pk in s.Group1RuleKeys)
                if (_persistKeyToDisplay.TryGetValue(pk, out var dk) && dk != null) _group1Keys.Add(dk);
            foreach (var pk in s.Group2RuleKeys)
                if (_persistKeyToDisplay.TryGetValue(pk, out var dk) && dk != null) _group2Keys.Add(dk);

            // Build reverse lookup: (linkId, elemId) → display name
            var gridRefToDisplay  = new Dictionary<(long, long), string>();
            var floorRefToDisplay = new Dictionary<(long, long), string>();
            foreach (var kv in _gridDisplayToRef)
                gridRefToDisplay[(kv.Value.lnk, kv.Value.id)] = kv.Key;
            foreach (var kv in _floorDisplayToRef)
                floorRefToDisplay[(kv.Value.lnk, kv.Value.id)] = kv.Key;

            // Grids (multi-select)
            for (int i = 0; i < s.GridIds.Count; i++)
            {
                long lnk = (i < s.GridLinkIds.Count) ? s.GridLinkIds[i] : 0L;
                if (gridRefToDisplay.TryGetValue((lnk, s.GridIds[i]), out var display))
                    _selectedGridNames.Add(display);
            }

            // Floor (single-select) — restore from first saved entry
            if (s.FloorIds.Count > 0)
            {
                long lnk = (s.FloorLinkIds.Count > 0) ? s.FloorLinkIds[0] : 0L;
                if (floorRefToDisplay.TryGetValue((lnk, s.FloorIds[0]), out var display))
                    _selectedFloorDisplay = display;
            }
        }

        private bool HasGroupOverlap() =>
            _group1Keys.Count > 0 && _group2Keys.Count > 0 && _group1Keys.Overlaps(_group2Keys);

        // ═════════════════════════════════════════════════════════════════════
        //  GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return BuildS3();
                case "S4": return BuildS4();
                case "S5": return BuildS5();
                default:   return null;
            }
        }

        private static ScrollViewer WrapInScroll(FrameworkElement content, double maxHeight = 700)
        {
            return new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = maxHeight,
            };
        }

        // ── S1 — Select Views ─────────────────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            var outer = new StackPanel();
            LemoineMultiSelectTabs? tabs = null;

            void RebuildTabs()
            {
                if (tabs != null) outer.Children.Remove(tabs);
                tabs = new LemoineMultiSelectTabs();
                tabs.SetGroups(BuildViewGroups(), _selectedViewNames);
                tabs.SelectionChanged += selected =>
                {
                    _selectedViewNames = new List<string>(selected);
                    Fire();
                };
                outer.Children.Add(tabs);
            }

            RebuildTabs();
            return WrapInScroll(outer);
        }

        private Dictionary<string, List<string>> BuildViewGroups()
        {
            var groups = new Dictionary<string, List<string>>
            {
                ["Floor Plans"]             = new List<string>(),
                ["Reflected Ceiling Plans"] = new List<string>(),
            };

            foreach (var v in _allViews)
            {
                if (!_viewNameToId.ContainsKey(v.Name)) _viewNameToId[v.Name] = v.Id;
                if (v.ViewType == ViewType.CeilingPlan)
                    groups["Reflected Ceiling Plans"].Add(v.Name);
                else
                    groups["Floor Plans"].Add(v.Name);
            }

            foreach (var k in groups.Keys.Where(k => groups[k].Count == 0).ToList())
                groups.Remove(k);

            if (groups.Count == 0) groups["(No plan views)"] = new List<string>();
            return groups;
        }

        // ── S2 — Group 1 Filters ──────────────────────────────────────────────
        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel();

            if (_filterGroups.Count == 0)
            {
                var empty = new TextBlock { Text = "No filters configured. Set up Auto Filters first.", TextWrapping = TextWrapping.Wrap };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                empty.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                empty.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                outer.Children.Add(empty);
                return WrapInScroll(outer);
            }

            var note = new TextBlock
            {
                Text = "Select the filter rules whose clashing elements will be annotated (Group 1 — source).",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(note);

            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(new Dictionary<string, List<string>>(_filterGroups), _group1Keys);
            tabs.SelectionChanged += selected =>
            {
                _group1Keys.Clear();
                foreach (var s in selected) _group1Keys.Add(s);
                Fire();
            };
            outer.Children.Add(tabs);
            return WrapInScroll(outer);
        }

        // ── S3 — Group 2 Filters ──────────────────────────────────────────────
        private FrameworkElement BuildS3()
        {
            var outer = new StackPanel();

            if (_filterGroups.Count == 0)
            {
                var empty = new TextBlock { Text = "No filters configured. Set up Auto Filters first.", TextWrapping = TextWrapping.Wrap };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                empty.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                empty.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                outer.Children.Add(empty);
                return WrapInScroll(outer);
            }

            var note = new TextBlock
            {
                Text = "Select the filter rules to test against Group 1 for clashes (Group 2 — target).",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(note);

            var warnText = new TextBlock
            {
                Text = "⚠ Some rules appear in both groups — this may cause elements to clash against themselves.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = HasGroupOverlap() ? WpfVisibility.Visible : WpfVisibility.Collapsed,
            };
            warnText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            warnText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            warnText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(warnText);

            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(new Dictionary<string, List<string>>(_filterGroups), _group2Keys);
            tabs.SelectionChanged += selected =>
            {
                _group2Keys.Clear();
                foreach (var s in selected) _group2Keys.Add(s);
                warnText.Visibility = HasGroupOverlap() ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                Fire();
            };
            outer.Children.Add(tabs);
            return WrapInScroll(outer);
        }

        // ── S4 — Grids & Slab References ─────────────────────────────────────
        private FrameworkElement BuildS4()
        {
            var outer = new StackPanel();

            // ── Toggle: show all documents ────────────────────────────────────
            var toggleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var toggleCb  = new CheckBox { IsChecked = _showAll, VerticalAlignment = VerticalAlignment.Center };
            toggleCb.SetResourceReference(CheckBox.ForegroundProperty, "LemoineText");
            toggleCb.SetResourceReference(CheckBox.FontFamilyProperty, "LemoineUiFont");
            toggleCb.SetResourceReference(CheckBox.FontSizeProperty,   "LemoineFS_SM");
            var toggleLbl = new TextBlock
            {
                Text = "Show all documents (not just active view)",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };
            toggleLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            toggleLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            toggleLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            toggleRow.Children.Add(toggleCb);
            toggleRow.Children.Add(toggleLbl);
            outer.Children.Add(toggleRow);

            AddDivider(outer);

            // ── Grids label ───────────────────────────────────────────────────
            var gridsLbl = new TextBlock
            {
                Text = "GRIDS",
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 0, 0, 4),
            };
            gridsLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            gridsLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            gridsLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(gridsLbl);

            var gridContainer = new StackPanel();
            outer.Children.Add(gridContainer);

            AddDivider(outer);

            // ── Slab edges label ──────────────────────────────────────────────
            var floorsLbl = new TextBlock
            {
                Text = "SLAB EDGE (select one — all edges will be dimensioned)",
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 0, 0, 4),
            };
            floorsLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            floorsLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            floorsLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(floorsLbl);

            var floorContainer = new StackPanel();
            outer.Children.Add(floorContainer);

            // ── Rebuild closure: clears and repopulates both containers ───────
            Action rebuild = () =>
            {
                var data = _showAll ? _allData : _activeViewData;

                // Grid section
                gridContainer.Children.Clear();
                if (data.GridNames.Count > 0)
                {
                    var gridGroups = BuildSourceGroups(data.GridNames);
                    var gridTabs = new LemoineMultiSelectTabs();
                    gridTabs.SetGroups(gridGroups, new List<string>(_selectedGridNames));
                    gridTabs.SelectionChanged += selected =>
                    {
                        // Preserve selections from elements not in the current visible set
                        var visibleSet = new HashSet<string>(data.GridNames);
                        var hidden     = new List<string>(_selectedGridNames.Where(n => !visibleSet.Contains(n)));
                        _selectedGridNames.Clear();
                        foreach (var s in selected) _selectedGridNames.Add(s);
                        foreach (var s in hidden)   _selectedGridNames.Add(s);
                        Fire();
                    };
                    gridContainer.Children.Add(gridTabs);
                }
                else
                {
                    var noGrids = new TextBlock
                    {
                        Text = _showAll
                            ? "No grids found in host or linked documents."
                            : "No grids visible in the active view. Toggle 'Show all documents' to see all.",
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 8),
                    };
                    noGrids.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    noGrids.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    noGrids.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    gridContainer.Children.Add(noGrids);
                }

                // Floor section (single-select)
                floorContainer.Children.Clear();
                var floorItems = new List<string> { "(None)" };
                floorItems.AddRange(data.FloorNames);

                var floorSelect = new LemoineSingleSelect();
                floorSelect.Items = floorItems.ToArray();

                // Restore saved selection only if visible in current data
                string floorSel = (_selectedFloorDisplay != null && floorItems.Contains(_selectedFloorDisplay))
                    ? _selectedFloorDisplay
                    : "(None)";
                floorSelect.SelectedItem = floorSel;

                // Wire SelectionChanged AFTER setting Items and SelectedItem ⚠
                floorSelect.SelectionChanged += val =>
                {
                    _selectedFloorDisplay = (val == null || val == "(None)") ? null : val;
                    Fire();
                };

                if (data.FloorNames.Count == 0)
                {
                    floorSelect.IsEnabled = false;
                    var hint = new TextBlock
                    {
                        Text = _showAll
                            ? "No floors found in host or linked documents."
                            : "No floors visible in the active view. Toggle 'Show all documents' to see all.",
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 4, 0, 0),
                    };
                    hint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    hint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    hint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    floorContainer.Children.Add(floorSelect);
                    floorContainer.Children.Add(hint);
                }
                else
                {
                    floorContainer.Children.Add(floorSelect);
                }

                // ⚠ Clear keyboard focus after rebuild to prevent ComboBox autocomplete spurious events
                floorSelect.Dispatcher.BeginInvoke(
                    new Action(() => Keyboard.ClearFocus()),
                    DispatcherPriority.Input);
            };

            toggleCb.Checked   += (s, e) => { _showAll = true;  rebuild(); Fire(); };
            toggleCb.Unchecked += (s, e) => { _showAll = false; rebuild(); Fire(); };

            rebuild();
            return WrapInScroll(outer);
        }

        // Groups display names by source: "[LinkName]" prefix → grouped under that link name; others → "Host"
        private static Dictionary<string, List<string>> BuildSourceGroups(List<string> displayNames)
        {
            var groups = new Dictionary<string, List<string>>();
            foreach (var name in displayNames)
            {
                string groupKey;
                if (name.StartsWith("["))
                {
                    int end = name.IndexOf(']');
                    groupKey = end > 0 ? name.Substring(1, end - 1) : "Link";
                }
                else
                {
                    groupKey = "Host";
                }
                if (!groups.ContainsKey(groupKey)) groups[groupKey] = new List<string>();
                groups[groupKey].Add(name);
            }
            if (groups.Count == 0) groups["(None)"] = new List<string>();
            return groups;
        }

        // ── S5 — Settings & Review ────────────────────────────────────────────
        private FrameworkElement BuildS5()
        {
            var outer = new StackPanel();

            var settingsHdr = new TextBlock
            {
                Text = "ANNOTATION SETTINGS",
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 0, 0, 8),
            };
            settingsHdr.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            settingsHdr.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            settingsHdr.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(settingsHdr);

            AddSettingRow(outer, "Clash Tolerance (mm)",
                "Extra margin added to each side of the clash bounding box annotation.",
                _toleranceMm.ToString("F1"),
                val => { if (double.TryParse(val, out double d) && d >= 0) { _toleranceMm = d; Fire(); } });

            AddSettingRow(outer, "Dimension Line Offset (mm)",
                "Distance the dimension string sits away from the cross annotation centre.",
                _dimLineOffsetMm.ToString("F0"),
                val => { if (double.TryParse(val, out double d) && d >= 0) { _dimLineOffsetMm = d; Fire(); } });

            AddDivider(outer);
            AddLabel(outer, "Dimension Style");
            var styleItems = _dimStyleNames.Count > 0 ? _dimStyleNames.ToArray() : new[] { "(No dimension styles)" };
            var styleSelect = new LemoineSingleSelect
            {
                Items        = styleItems,
                SelectedItem = styleItems.Contains(_dimStyleName) ? _dimStyleName : styleItems[0],
            };
            styleSelect.SelectionChanged += val => { if (val != null) { _dimStyleName = val; Fire(); } };
            if (styleSelect.SelectedItem != null && _dimStyleNames.Contains(styleSelect.SelectedItem))
                _dimStyleName = styleSelect.SelectedItem;
            outer.Children.Add(styleSelect);

            AddDivider(outer);
            AddLabel(outer, "Dimension Reference");
            var targetSelect = new LemoineSingleSelect
            {
                Items        = new[] { "Edge", "Centre" },
                SelectedItem = _dimTarget,
            };
            targetSelect.SelectionChanged += val => { if (val != null) { _dimTarget = val; Fire(); } };
            outer.Children.Add(targetSelect);

            AddDivider(outer);
            AddLabel(outer, "Fill Style");
            var fillSelect = new LemoineSingleSelect
            {
                Items        = new[] { "Solid", "Outline" },
                SelectedItem = _fillStyle,
            };
            fillSelect.SelectionChanged += val => { if (val != null) { _fillStyle = val; Fire(); } };
            outer.Children.Add(fillSelect);

            AddDivider(outer);
            AddLabel(outer, "Cross Line Style");
            var lineItems = new List<string> { "(Default)" };
            lineItems.AddRange(_lineStyleNames);
            var lineSelect = new LemoineSingleSelect
            {
                Items        = lineItems.ToArray(),
                SelectedItem = _lineStyleNames.Contains(_crossLineTypeName)
                    ? _crossLineTypeName : "(Default)",
            };
            lineSelect.SelectionChanged += val =>
            {
                _crossLineTypeName = (val == null || val == "(Default)") ? "" : val;
                Fire();
            };
            outer.Children.Add(lineSelect);

            AddDivider(outer);
            var clearSp = new StackPanel { Orientation = Orientation.Horizontal };
            var clearCb = new CheckBox { IsChecked = _clearPrevious, Margin = new Thickness(0, 0, 0, 4) };
            clearCb.SetResourceReference(CheckBox.ForegroundProperty, "LemoineText");
            clearCb.SetResourceReference(CheckBox.FontFamilyProperty, "LemoineUiFont");
            clearCb.SetResourceReference(CheckBox.FontSizeProperty,   "LemoineFS_SM");
            var clearLbl = new TextBlock { Text = "Clear previous clash annotations before placing new ones", VerticalAlignment = VerticalAlignment.Center };
            clearLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            clearLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            clearLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            clearSp.Children.Add(clearCb);
            clearSp.Children.Add(clearLbl);
            outer.Children.Add(clearSp);
            clearCb.Checked   += (s, e) => { _clearPrevious = true;  Fire(); };
            clearCb.Unchecked += (s, e) => { _clearPrevious = false; Fire(); };

            AddDivider(outer);
            AddSettingRow(outer, "Max Clashes",
                "Stop detecting after this many clashes. Raise if clashes are being missed.",
                _maxClashes.ToString(),
                val => { if (int.TryParse(val, out int n) && n > 0) { _maxClashes = n; Fire(); } });

            // ── Review cards ──────────────────────────────────────────────────
            AddDivider(outer);
            var reviewHdr = new TextBlock
            {
                Text = "RUN SUMMARY",
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 0, 0, 8),
            };
            reviewHdr.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            reviewHdr.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            reviewHdr.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(reviewHdr);

            var cardGrid = new WpfGrid();
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition());
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition());

            var cardDefs = new CardDef[]
            {
                new CardDef("Views",          () => $"{_selectedViewNames.Count} selected"),
                new CardDef("Group 1 Rules",  () => $"{_group1Keys.Count} selected"),
                new CardDef("Group 2 Rules",  () => $"{_group2Keys.Count} selected"),
                new CardDef("Grids & Slabs",  () => $"{_selectedGridNames.Count} grid(s), floor: {_selectedFloorDisplay ?? "—"}"),
                new CardDef("Tolerance",      () => $"{_toleranceMm:F1} mm"),
                new CardDef("Dim Reference",  () => _dimTarget),
                new CardDef("Max Clashes",    () => _maxClashes.ToString()),
            };

            for (int i = 0; i < cardDefs.Length; i++)
            {
                int row = i / 2;
                int col = i % 2;
                if (cardGrid.RowDefinitions.Count <= row)
                    cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                AddReviewCard(cardGrid, cardDefs[i].Label, cardDefs[i].Value, row, col);
            }
            outer.Children.Add(cardGrid);

            return WrapInScroll(outer, maxHeight: 800);
        }

        // ── UI helpers ────────────────────────────────────────────────────────

        private void AddSettingRow(StackPanel parent, string label, string hint, string initial, Action<string> onChange)
        {
            var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 2) };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            parent.Children.Add(lbl);

            var box = new WpfTextBox
            {
                Text    = initial,
                Padding = new Thickness(6, 3, 6, 3),
                Margin  = new Thickness(0, 0, 0, 8),
                ToolTip = hint,
            };
            box.SetResourceReference(WpfTextBox.MinHeightProperty,   "LemoineH_Input");
            box.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            box.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            box.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorderMid");
            box.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineMonoFont");
            box.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_MD");
            box.TextChanged += (s, e) => onChange(box.Text);
            parent.Children.Add(box);
        }

        private static void AddLabel(StackPanel parent, string text)
        {
            var lbl = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4) };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            parent.Children.Add(lbl);
        }

        private void AddReviewCard(WpfGrid grid, string label, Func<string> valueFn, int row, int col)
        {
            var card = new Border
            {
                Margin          = new Thickness(col == 1 ? 4 : 0, row > 0 ? 4 : 0, 0, 0),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Padding         = new Thickness(10, 7, 10, 7),
            };
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var lbl = new TextBlock { Text = label.ToUpper(), Margin = new Thickness(0, 0, 0, 2) };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var val = new TextBlock { FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap };
            val.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            val.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            val.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            val.Text = valueFn();
            ValidationChanged += (s, e) => val.Text = valueFn();

            var sp = new StackPanel();
            sp.Children.Add(lbl);
            sp.Children.Add(val);
            card.Child = sp;
            WpfGrid.SetRow(card, row);
            WpfGrid.SetColumn(card, col);
            grid.Children.Add(card);
        }

        private static void AddDivider(StackPanel parent)
        {
            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            parent.Children.Add(sep);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedViewNames.Count > 0;
                case "S2": return _group1Keys.Count > 0;
                case "S3": return _group2Keys.Count > 0;
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedViewNames.Count == 0 ? "—"
                    : $"{_selectedViewNames.Count} view(s)";
                case "S2": return _group1Keys.Count == 0 ? "—"
                    : $"{_group1Keys.Count} rule(s)";
                case "S3": return _group2Keys.Count == 0 ? "—"
                    : $"{_group2Keys.Count} rule(s)"
                    + (HasGroupOverlap() ? " ⚠ overlap" : "");
                case "S4":
                    int total = _selectedGridNames.Count + (_selectedFloorDisplay != null ? 1 : 0);
                    if (total == 0) return "No references";
                    string floorPart = _selectedFloorDisplay ?? "—";
                    return $"{_selectedGridNames.Count} grid(s) · floor: {floorPart}";
                case "S5": return $"Tol {_toleranceMm:F0} mm · {_dimTarget} · {_fillStyle}";
                default:   return "—";
            }
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

            // Grid lists from multi-select
            var gridIds     = new List<long>();
            var gridLinkIds = new List<long>();
            foreach (var display in _selectedGridNames)
            {
                if (_gridDisplayToRef.TryGetValue(display, out var r))
                {
                    gridIds.Add(r.id);
                    gridLinkIds.Add(r.lnk);
                }
            }

            // Floor lists from single-select (at most one entry)
            var floorIds     = new List<long>();
            var floorLinkIds = new List<long>();
            if (_selectedFloorDisplay != null
                && _floorDisplayToRef.TryGetValue(_selectedFloorDisplay, out var fRef))
            {
                floorIds.Add(fRef.id);
                floorLinkIds.Add(fRef.lnk);
            }

            // Persist settings
            var s = ClashDimensionSettings.Instance;
            var g1Keys = new List<string>();
            foreach (var dk in _group1Keys)
                if (_displayKeyToPersist.TryGetValue(dk, out var pk) && pk != null) g1Keys.Add(pk);
            s.Group1RuleKeys = g1Keys;

            var g2Keys = new List<string>();
            foreach (var dk in _group2Keys)
                if (_displayKeyToPersist.TryGetValue(dk, out var pk) && pk != null) g2Keys.Add(pk);
            s.Group2RuleKeys    = g2Keys;
            s.GridIds           = gridIds;
            s.GridLinkIds       = gridLinkIds;
            s.FloorIds          = floorIds;
            s.FloorLinkIds      = floorLinkIds;
            s.ToleranceMm       = _toleranceMm;
            s.DimStyleName      = _dimStyleName;
            s.DimLineOffsetMm   = _dimLineOffsetMm;
            s.DimTarget         = _dimTarget;
            s.FillStyle         = _fillStyle;
            s.CrossLineTypeName = _crossLineTypeName;
            s.ClearPrevious     = _clearPrevious;
            s.MaxClashes        = _maxClashes;
            s.ShowAllDocuments  = _showAll;
            s.Save();

            // Wire handler
            _handler.ViewIds = _selectedViewNames
                .Where(n => _viewNameToId.ContainsKey(n))
                .Select(n => _viewNameToId[n])
                .ToList();
            _handler.Group1RuleKeys    = new List<string>(s.Group1RuleKeys);
            _handler.Group2RuleKeys    = new List<string>(s.Group2RuleKeys);
            _handler.GridIds           = gridIds;
            _handler.GridLinkIds       = gridLinkIds;
            _handler.FloorIds          = floorIds;
            _handler.FloorLinkIds      = floorLinkIds;
            _handler.ToleranceMm       = _toleranceMm;
            _handler.DimStyleName      = _dimStyleName;
            _handler.DimLineOffsetMm   = _dimLineOffsetMm;
            _handler.DimTarget         = _dimTarget;
            _handler.FillStyle         = _fillStyle;
            _handler.CrossLineTypeName = _crossLineTypeName;
            _handler.ClearPrevious     = _clearPrevious;
            _handler.MaxClashes        = _maxClashes;
            _handler.PushLog           = pushLog;
            _handler.OnProgress        = onProgress;
            _handler.OnComplete        = onComplete;

            _event.Raise();
        }
    }
}
