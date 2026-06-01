using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.AutoFilters;

using WpfGrid       = System.Windows.Controls.Grid;
using WpfButton     = System.Windows.Controls.Button;
using WpfTextBox    = System.Windows.Controls.TextBox;
using WpfComboBox   = System.Windows.Controls.ComboBox;
using WpfVisibility = System.Windows.Visibility;

namespace LemoineTools.Tools.Testing
{
    public class ClashDimensionViewModel : ILemoineTool, ILemoineReviewable
    {
        public string Title    => "Clash Dimension";
        public string RunLabel => "Place Clash Annotations →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Views",              required: true),
            new StepDefinition("S2", "Group 1 — Source",          required: true),
            new StepDefinition("S3", "Group 2 — Target",          required: true),
            new StepDefinition("S4", "Grids & Slab References",   required: false),
            new StepDefinition("S5", "Settings",                  required: false),
            new StepDefinition("S6", "Review & Run",              required: false),
        };

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── Per-group definition state ────────────────────────────────────────
        private sealed class GroupState
        {
            public string Mode = "Rules";   // "Rules" | "Categories" | "Elements"
            public readonly HashSet<string>            RuleKeys      = new HashSet<string>();   // display keys
            public readonly HashSet<string>            Categories    = new HashSet<string>();   // display names
            public readonly List<(long lnk, long id)>  ElemRefs      = new List<(long, long)>();
            public readonly HashSet<long>              SourceLinkIds = new HashSet<long>();      // 0 = host
        }
        private readonly GroupState _g1 = new GroupState();
        private readonly GroupState _g2 = new GroupState();

        // ── Filter rule mappings ──────────────────────────────────────────────
        private readonly Dictionary<string, FilterRuleConfig>  _displayKeyToRule    = new Dictionary<string, FilterRuleConfig>();
        private readonly Dictionary<string, string>            _displayKeyToPersist = new Dictionary<string, string>();
        private readonly Dictionary<string, string>            _persistKeyToDisplay = new Dictionary<string, string>();
        private readonly Dictionary<string, List<string>>      _filterGroups        = new Dictionary<string, List<string>>();

        // ── Category mappings ─────────────────────────────────────────────────
        private readonly Dictionary<string, List<string>> _categoryGroups      = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, string>       _categoryDisplayToOst = new Dictionary<string, string>();
        private readonly Dictionary<string, string>       _ostToCategoryDisplay = new Dictionary<string, string>();

        // ── Source documents ──────────────────────────────────────────────────
        private readonly List<string>               _docNames          = new List<string>();
        private readonly Dictionary<string, long>   _docDisplayToLinkId = new Dictionary<string, long>();

        // ── Grid / floor state (S4) ───────────────────────────────────────────
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
        private readonly ClashPickEventHandler?      _pickHandler;
        private readonly ExternalEvent?              _pickEvent;

        // Mode display ↔ internal
        private static readonly string[] ModeDisplayItems = { "Filter Rules", "Categories", "Select Elements" };
        private static string ModeToDisplay(string m) =>
            m == "Categories" ? "Categories" : m == "Elements" ? "Select Elements" : "Filter Rules";
        private static string DisplayToMode(string? d) =>
            d == "Categories" ? "Categories" : d == "Select Elements" ? "Elements" : "Rules";

        // ── Named struct (no anonymous tuple arrays) ──────────────────────────

        // ── Constructor ───────────────────────────────────────────────────────
        public ClashDimensionViewModel(
            ClashDimensionEventHandler? handler,
            ExternalEvent?              externalEvent,
            ClashPickEventHandler?      pickHandler,
            ExternalEvent?              pickEvent,
            List<string>                dimStyleNames,
            List<View>                  allViews,
            GridFloorData               activeViewData,
            GridFloorData               allData,
            List<ClashDocInfo>          docs,
            List<string>                lineStyleNames)
        {
            _handler        = handler;
            _event          = externalEvent;
            _pickHandler    = pickHandler;
            _pickEvent      = pickEvent;
            _dimStyleNames  = dimStyleNames;
            _allViews       = allViews;
            _activeViewData = activeViewData;
            _allData        = allData;
            _lineStyleNames = lineStyleNames;

            foreach (var v in _allViews)
                if (!_viewNameToId.ContainsKey(v.Name))
                    _viewNameToId[v.Name] = v.Id;

            foreach (var d in docs)
            {
                if (_docDisplayToLinkId.ContainsKey(d.Name)) continue;
                _docNames.Add(d.Name);
                _docDisplayToLinkId[d.Name] = d.LinkInstId;
            }

            for (int i = 0; i < allData.GridNames.Count; i++)
                _gridDisplayToRef[allData.GridNames[i]] = (allData.GridLinkIds[i], allData.GridIds[i]);
            for (int i = 0; i < allData.FloorNames.Count; i++)
                _floorDisplayToRef[allData.FloorNames[i]] = (allData.FloorLinkIds[i], allData.FloorIds[i]);

            BuildFilterMappings();
            BuildCategoryMappings();
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

        private void BuildCategoryMappings()
        {
            foreach (var kv in AutoFiltersSettings.KnownCategoryMap)
            {
                string display = kv.Key;
                string ost     = kv.Value;
                string disc    = DisciplineOf(ost);

                if (!_categoryGroups.ContainsKey(disc)) _categoryGroups[disc] = new List<string>();
                _categoryGroups[disc].Add(display);
                _categoryDisplayToOst[display] = ost;
                _ostToCategoryDisplay[ost]     = display;
            }
            foreach (var k in _categoryGroups.Keys.ToList())
                _categoryGroups[k].Sort(StringComparer.OrdinalIgnoreCase);
        }

        private static string DisciplineOf(string ost)
        {
            bool C(params string[] needles) => needles.Any(n => ost.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            if (C("Duct", "MechanicalEquipment", "FlexDuct"))                                      return "Mechanical";
            if (C("Pipe", "Plumbing", "Sprinkler", "FlexPipe", "FabricationPipe",
                  "FabricationHangers", "FabricationContainment"))                                  return "Piping";
            if (C("Cable", "Conduit", "Electrical", "Lighting", "Communication",
                  "FireAlarm", "Security", "Data", "Telephone", "NurseCall"))                       return "Electrical";
            if (C("Structural", "Rebar", "Reinforcement", "Fabric"))                                return "Structural";
            return "Architectural";
        }

        private void RestoreFromSettings()
        {
            var s = ClashDimensionSettings.Instance;

            RestoreGroup(_g1, s.Group1Mode, s.Group1RuleKeys, s.Group1Categories,
                         s.Group1ElemIds, s.Group1ElemLinkIds, s.Group1SourceLinkIds);
            RestoreGroup(_g2, s.Group2Mode, s.Group2RuleKeys, s.Group2Categories,
                         s.Group2ElemIds, s.Group2ElemLinkIds, s.Group2SourceLinkIds);

            // Build reverse lookup for grids/floors: (linkId, elemId) → display name
            var gridRefToDisplay  = new Dictionary<(long, long), string>();
            var floorRefToDisplay = new Dictionary<(long, long), string>();
            foreach (var kv in _gridDisplayToRef)
                gridRefToDisplay[(kv.Value.lnk, kv.Value.id)] = kv.Key;
            foreach (var kv in _floorDisplayToRef)
                floorRefToDisplay[(kv.Value.lnk, kv.Value.id)] = kv.Key;

            for (int i = 0; i < s.GridIds.Count; i++)
            {
                long lnk = (i < s.GridLinkIds.Count) ? s.GridLinkIds[i] : 0L;
                if (gridRefToDisplay.TryGetValue((lnk, s.GridIds[i]), out var display))
                    _selectedGridNames.Add(display);
            }
            if (s.FloorIds.Count > 0)
            {
                long lnk = (s.FloorLinkIds.Count > 0) ? s.FloorLinkIds[0] : 0L;
                if (floorRefToDisplay.TryGetValue((lnk, s.FloorIds[0]), out var display))
                    _selectedFloorDisplay = display;
            }
        }

        private void RestoreGroup(GroupState g, string mode,
            List<string> ruleKeys, List<string> categories,
            List<long> elemIds, List<long> elemLinkIds, List<long> sourceLinkIds)
        {
            g.Mode = (mode == "Categories" || mode == "Elements") ? mode : "Rules";

            foreach (var pk in ruleKeys)
                if (_persistKeyToDisplay.TryGetValue(pk, out var dk) && dk != null) g.RuleKeys.Add(dk);

            foreach (var ost in categories)
                if (_ostToCategoryDisplay.TryGetValue(ost, out var disp) && disp != null) g.Categories.Add(disp);

            for (int i = 0; i < elemIds.Count; i++)
            {
                long lnk = (i < elemLinkIds.Count) ? elemLinkIds[i] : 0L;
                g.ElemRefs.Add((lnk, elemIds[i]));
            }

            foreach (var id in sourceLinkIds) g.SourceLinkIds.Add(id);
        }

        private bool BothRulesMode() => _g1.Mode == "Rules" && _g2.Mode == "Rules";
        private bool HasGroupOverlap() =>
            BothRulesMode() && _g1.RuleKeys.Count > 0 && _g2.RuleKeys.Count > 0 && _g1.RuleKeys.Overlaps(_g2.RuleKeys);

        private static bool GroupValid(GroupState g)
        {
            switch (g.Mode)
            {
                case "Categories": return g.Categories.Count > 0;
                case "Elements":   return g.ElemRefs.Count > 0;
                default:           return g.RuleKeys.Count > 0;
            }
        }

        private static string GroupSummary(GroupState g)
        {
            switch (g.Mode)
            {
                case "Categories": return g.Categories.Count == 0 ? "—" : $"{g.Categories.Count} category(ies)";
                case "Elements":   return g.ElemRefs.Count   == 0 ? "—" : $"{g.ElemRefs.Count} element(s)";
                default:           return g.RuleKeys.Count    == 0 ? "—" : $"{g.RuleKeys.Count} rule(s)";
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildGroupStep(1);
                case "S3": return BuildGroupStep(2);
                case "S4": return BuildS4();
                case "S5": return BuildS5();
                default:   return null;
            }
        }

        private static ScrollViewer WrapInScroll(FrameworkElement content, double maxHeight = 700)
        {
            var sv = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = maxHeight,
            };
            LemoineControlStyles.WireBubblingScroll(sv); // bubble wheel to the page at scroll limits
            return sv;
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

        // ── S2 / S3 — Group definition (shared) ───────────────────────────────
        private FrameworkElement BuildGroupStep(int group)
        {
            var g = group == 1 ? _g1 : _g2;
            var outer = new StackPanel();

            var note = new TextBlock
            {
                Text = group == 1
                    ? "Define Group 1 — the SOURCE elements whose clashes get annotated (colour comes from this group)."
                    : "Define Group 2 — the TARGET elements tested against Group 1 for clashes.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(note);

            // Overlap warning (Group 2 only)
            TextBlock? warnText = null;
            if (group == 2)
            {
                warnText = new TextBlock
                {
                    Text = "⚠ Some rules appear in both groups — elements may clash against themselves.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8),
                    Visibility = HasGroupOverlap() ? WpfVisibility.Visible : WpfVisibility.Collapsed,
                };
                warnText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                warnText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                warnText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                outer.Children.Add(warnText);
                var capturedWarn = warnText;
                ValidationChanged += (s, e) =>
                    capturedWarn.Visibility = HasGroupOverlap() ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            }

            // ── Body container (rebuilt on mode change) ───────────────────────
            var body = new StackPanel();

            Action rebuildBody = () =>
            {
                body.Children.Clear();
                switch (g.Mode)
                {
                    case "Categories": BuildCategoryBody(body, g); break;
                    case "Elements":   BuildElementsBody(body, g); break;
                    default:           BuildRulesBody(body, g, warnText); break;
                }
                body.Dispatcher.BeginInvoke(new Action(() => Keyboard.ClearFocus()), DispatcherPriority.Input);
            };

            // ── Mode selector ─────────────────────────────────────────────────
            AddLabel(outer, "Group definition mode");
            var modeSelect = new LemoineSingleSelect();
            modeSelect.Items        = ModeDisplayItems;
            modeSelect.SelectedItem = ModeToDisplay(g.Mode);
            modeSelect.SelectionChanged += val =>
            {
                g.Mode = DisplayToMode(val);
                rebuildBody();
                Fire();
            };
            outer.Children.Add(modeSelect);

            AddDivider(outer);

            // ── Source documents ──────────────────────────────────────────────
            AddLabel(outer, "Source documents (which models this group scans)");
            var docGroups = new Dictionary<string, List<string>> { ["Documents"] = new List<string>(_docNames) };
            var initialDocs = (g.SourceLinkIds.Count == 0)
                ? new List<string>(_docNames)   // none stored → all selected
                : _docNames.Where(n => g.SourceLinkIds.Contains(_docDisplayToLinkId[n])).ToList();
            var srcTabs = new LemoineMultiSelectTabs();
            srcTabs.SetGroups(docGroups, initialDocs);
            srcTabs.SelectionChanged += selected =>
            {
                g.SourceLinkIds.Clear();
                foreach (var n in selected)
                    if (_docDisplayToLinkId.TryGetValue(n, out var lid)) g.SourceLinkIds.Add(lid);
                Fire();
            };
            outer.Children.Add(srcTabs);

            AddDivider(outer);
            outer.Children.Add(body);

            rebuildBody();
            return WrapInScroll(outer);
        }

        private void BuildRulesBody(StackPanel body, GroupState g, TextBlock? warnText)
        {
            if (_filterGroups.Count == 0)
            {
                AddDim(body, "No AutoFilters rules configured. Switch to Categories or Select Elements, or set up Auto Filters first.");
                return;
            }
            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(new Dictionary<string, List<string>>(_filterGroups), g.RuleKeys);
            tabs.SelectionChanged += selected =>
            {
                g.RuleKeys.Clear();
                foreach (var s in selected) g.RuleKeys.Add(s);
                if (warnText != null)
                    warnText.Visibility = HasGroupOverlap() ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                Fire();
            };
            body.Children.Add(tabs);
        }

        private void BuildCategoryBody(StackPanel body, GroupState g)
        {
            if (_categoryGroups.Count == 0)
            {
                AddDim(body, "No categories available.");
                return;
            }
            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(new Dictionary<string, List<string>>(_categoryGroups), g.Categories);
            tabs.SelectionChanged += selected =>
            {
                g.Categories.Clear();
                foreach (var s in selected) g.Categories.Add(s);
                Fire();
            };
            body.Children.Add(tabs);
        }

        private void BuildElementsBody(StackPanel body, GroupState g)
        {
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

            var pickHost  = MakeButton("＋ Pick host elements");
            var pickLinks = MakeButton("＋ Pick linked elements");
            var clearBtn  = MakeButton("Clear");
            clearBtn.Margin = new Thickness(0);

            btnRow.Children.Add(pickHost);
            btnRow.Children.Add(pickLinks);
            btnRow.Children.Add(clearBtn);
            body.Children.Add(btnRow);

            var count = new TextBlock { TextWrapping = TextWrapping.Wrap };
            count.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            count.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            count.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            body.Children.Add(count);

            Action refresh = () => count.Text = g.ElemRefs.Count == 0
                ? "No elements picked yet. Use the buttons above to pick in the model."
                : $"{g.ElemRefs.Count} element(s) picked.";
            refresh();

            pickHost.Click  += (s, e) => StartPick(g, false, (WpfButton)s!, refresh);
            pickLinks.Click += (s, e) => StartPick(g, true,  (WpfButton)s!, refresh);
            clearBtn.Click  += (s, e) => { g.ElemRefs.Clear(); refresh(); Fire(); };
        }

        private void StartPick(GroupState g, bool inLinks, WpfButton sourceBtn, Action refresh)
        {
            if (_pickHandler == null || _pickEvent == null) return;
            var disp = sourceBtn.Dispatcher;   // window's STA dispatcher

            _pickHandler.InLinks = inLinks;
            _pickHandler.OnPicked = picks =>
            {
                // Runs on Revit's main thread — marshal back to the UI thread.
                disp.BeginInvoke(new Action(() =>
                {
                    foreach (var p in picks)
                        if (!g.ElemRefs.Any(e => e.lnk == p.linkId && e.id == p.elemId))
                            g.ElemRefs.Add((p.linkId, p.elemId));
                    refresh();
                    Fire();
                }));
            };
            _pickEvent.Raise();
        }

        // ── S4 — Grids & Slab References ─────────────────────────────────────
        private FrameworkElement BuildS4()
        {
            var outer = new StackPanel();

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

            var gridsLbl = new TextBlock { Text = "GRIDS", FontStyle = FontStyles.Italic, Margin = new Thickness(0, 0, 0, 4) };
            gridsLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            gridsLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            gridsLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(gridsLbl);

            var gridContainer = new StackPanel();
            outer.Children.Add(gridContainer);

            AddDivider(outer);

            var floorsLbl = new TextBlock
            {
                Text = "SLAB EDGE (select one — all edges will be dimensioned)",
                FontStyle = FontStyles.Italic, Margin = new Thickness(0, 0, 0, 4),
            };
            floorsLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            floorsLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            floorsLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(floorsLbl);

            var floorContainer = new StackPanel();
            outer.Children.Add(floorContainer);

            Action rebuild = () =>
            {
                var data = _showAll ? _allData : _activeViewData;

                gridContainer.Children.Clear();
                if (data.GridNames.Count > 0)
                {
                    var gridTabs = new LemoineMultiSelectTabs();
                    gridTabs.SetGroups(BuildSourceGroups(data.GridNames), new List<string>(_selectedGridNames));
                    gridTabs.SelectionChanged += selected =>
                    {
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
                    AddDim(gridContainer, _showAll
                        ? "No grids found in host or linked documents."
                        : "No grids visible in the active view. Toggle 'Show all documents' to see all.");
                }

                floorContainer.Children.Clear();
                var floorItems = new List<string> { "(None)" };
                floorItems.AddRange(data.FloorNames);

                var floorSelect = new LemoineSingleSelect();
                floorSelect.Items = floorItems.ToArray();
                floorSelect.SelectedItem = (_selectedFloorDisplay != null && floorItems.Contains(_selectedFloorDisplay))
                    ? _selectedFloorDisplay : "(None)";
                floorSelect.SelectionChanged += val =>
                {
                    _selectedFloorDisplay = (val == null || val == "(None)") ? null : val;
                    Fire();
                };

                floorContainer.Children.Add(floorSelect);
                if (data.FloorNames.Count == 0)
                {
                    floorSelect.IsEnabled = false;
                    AddDim(floorContainer, _showAll
                        ? "No floors found in host or linked documents."
                        : "No floors visible in the active view. Toggle 'Show all documents' to see all.");
                }

                floorSelect.Dispatcher.BeginInvoke(new Action(() => Keyboard.ClearFocus()), DispatcherPriority.Input);
            };

            toggleCb.Checked   += (s, e) => { _showAll = true;  rebuild(); Fire(); };
            toggleCb.Unchecked += (s, e) => { _showAll = false; rebuild(); Fire(); };

            rebuild();
            return WrapInScroll(outer);
        }

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
                else groupKey = "Host";
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

            var settingsHdr = new TextBlock { Text = "ANNOTATION SETTINGS", FontStyle = FontStyles.Italic, Margin = new Thickness(0, 0, 0, 8) };
            settingsHdr.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            settingsHdr.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            settingsHdr.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(settingsHdr);

            AddSettingRow(outer, "Clash Tolerance (mm)",
                "Extra margin added to each side of the clash bounding box annotation.",
                _toleranceMm, 0, 100, 0.5, 1,
                d => { _toleranceMm = d; Fire(); });

            AddSettingRow(outer, "Dimension Line Offset (mm)",
                "Distance the dimension string sits away from the cross annotation centre.",
                _dimLineOffsetMm, 0, 1000, 1, 0,
                d => { _dimLineOffsetMm = d; Fire(); });

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
            var targetSelect = new LemoineSingleSelect { Items = new[] { "Edge", "Centre" }, SelectedItem = _dimTarget };
            targetSelect.SelectionChanged += val => { if (val != null) { _dimTarget = val; Fire(); } };
            outer.Children.Add(targetSelect);

            AddDivider(outer);
            AddLabel(outer, "Fill Style");
            var fillSelect = new LemoineSingleSelect { Items = new[] { "Solid", "Outline" }, SelectedItem = _fillStyle };
            fillSelect.SelectionChanged += val => { if (val != null) { _fillStyle = val; Fire(); } };
            outer.Children.Add(fillSelect);

            AddDivider(outer);
            AddLabel(outer, "Cross Line Style");
            var lineItems = new List<string> { "(Default)" };
            lineItems.AddRange(_lineStyleNames);
            var lineSelect = new LemoineSingleSelect
            {
                Items        = lineItems.ToArray(),
                SelectedItem = _lineStyleNames.Contains(_crossLineTypeName) ? _crossLineTypeName : "(Default)",
            };
            lineSelect.SelectionChanged += val =>
            {
                _crossLineTypeName = (val == null || val == "(Default)") ? "" : val;
                Fire();
            };
            outer.Children.Add(lineSelect);

            AddDivider(outer);
            var clearToggle = new LemoineToggleSwitches();
            clearToggle.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "clear", Label = "Clear previous clash annotations before placing new ones", DefaultOn = _clearPrevious },
            });
            clearToggle.StateChanged += state => { state.TryGetValue("clear", out _clearPrevious); Fire(); };
            outer.Children.Add(clearToggle);

            AddDivider(outer);
            AddSettingRow(outer, "Max Clashes",
                "Stop detecting after this many clashes. Raise if clashes are being missed.",
                _maxClashes, 1, 100000, 1, 0,
                d => { _maxClashes = (int)d; Fire(); });

            return WrapInScroll(outer, maxHeight: 800);
        }

        // ── UI helpers ────────────────────────────────────────────────────────

        private WpfButton MakeButton(string label)
        {
            var b = new WpfButton
            {
                Content         = label,
                Cursor          = Cursors.Hand,
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(0, 0, 8, 0),
                Template        = LemoineControlStyles.BuildFlatButtonTemplate(),
            };
            b.SetResourceReference(WpfButton.MinHeightProperty,  "LemoineH_BtnMin");
            b.SetResourceReference(WpfButton.PaddingProperty,    "LemoineTh_BtnPad");
            b.SetResourceReference(WpfButton.FontSizeProperty,   "LemoineFS_MD");
            b.SetResourceReference(WpfButton.FontFamilyProperty, "LemoineUiFont");
            b.Background = Brushes.Transparent;
            b.SetResourceReference(WpfButton.BorderBrushProperty, "LemoineBorder");
            b.SetResourceReference(WpfButton.ForegroundProperty,  "LemoineText");
            return b;
        }

        private static void AddDim(StackPanel parent, string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            parent.Children.Add(tb);
        }

        // Numeric overload — a typeable inline stepper instead of a free-text box.
        private void AddSettingRow(StackPanel parent, string label, string hint,
                                   double value, double min, double max, double step, int decimals, Action<double> onChange)
        {
            var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 2) };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            parent.Children.Add(lbl);

            var stepper = new LemoineInlineStepper
            {
                Value               = value,
                MinValue            = min,
                MaxValue            = max,
                Step                = step,
                Decimals            = decimals,
                ValueWidth          = 56,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin              = new Thickness(0, 0, 0, 8),
                ToolTip             = hint,
            };
            stepper.ValueChanged += (s, v) => onChange(v);
            parent.Children.Add(stepper);
        }

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


        private static void AddDivider(StackPanel parent)
        {
            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            parent.Children.Add(sep);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════
        // ── ILemoineReviewable (P3) — framework renders the final review step ─
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("views", "Views"),
            ("g1",    "Group 1"),
            ("g2",    "Group 2"),
            ("grids", "Grids & Slabs"),
            ("tol",   "Tolerance"),
            ("ref",   "Dim Reference"),
            ("max",   "Max Clashes"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["views"] = $"{_selectedViewNames.Count} selected",
            ["g1"]    = $"{ModeToDisplay(_g1.Mode)} · {GroupSummary(_g1)}",
            ["g2"]    = $"{ModeToDisplay(_g2.Mode)} · {GroupSummary(_g2)}",
            ["grids"] = $"{_selectedGridNames.Count} grid(s), floor: {_selectedFloorDisplay ?? "—"}",
            ["tol"]   = $"{_toleranceMm:F1} mm",
            ["ref"]   = _dimTarget,
            ["max"]   = _maxClashes.ToString(),
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => null;
        public string?        ReviewWarning => null;

        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedViewNames.Count > 0;
                case "S2": return GroupValid(_g1);
                case "S3": return GroupValid(_g2);
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedViewNames.Count == 0 ? "—" : $"{_selectedViewNames.Count} view(s)";
                case "S2": return $"{ModeToDisplay(_g1.Mode)} · {GroupSummary(_g1)}";
                case "S3": return $"{ModeToDisplay(_g2.Mode)} · {GroupSummary(_g2)}"
                                + (HasGroupOverlap() ? " ⚠ overlap" : "");
                case "S4":
                    int total = _selectedGridNames.Count + (_selectedFloorDisplay != null ? 1 : 0);
                    if (total == 0) return "No references";
                    return $"{_selectedGridNames.Count} grid(s) · floor: {_selectedFloorDisplay ?? "—"}";
                case "S5": return $"Tol {_toleranceMm:F0} mm · {_dimTarget} · {_fillStyle}";
                default:   return "—";
            }
        }

        private ClashGroupSpec BuildSpec(GroupState g)
        {
            var spec = new ClashGroupSpec { Mode = g.Mode };
            foreach (var dk in g.RuleKeys)
                if (_displayKeyToPersist.TryGetValue(dk, out var pk) && pk != null) spec.RuleKeys.Add(pk);
            foreach (var disp in g.Categories)
                if (_categoryDisplayToOst.TryGetValue(disp, out var ost) && ost != null) spec.Categories.Add(ost);
            foreach (var r in g.ElemRefs) { spec.ElemIds.Add(r.id); spec.ElemLinkIds.Add(r.lnk); }
            foreach (var id in g.SourceLinkIds) spec.SourceLinkIds.Add(id);
            return spec;
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

            var spec1 = BuildSpec(_g1);
            var spec2 = BuildSpec(_g2);

            // Grid lists from multi-select
            var gridIds     = new List<long>();
            var gridLinkIds = new List<long>();
            foreach (var display in _selectedGridNames)
                if (_gridDisplayToRef.TryGetValue(display, out var r)) { gridIds.Add(r.id); gridLinkIds.Add(r.lnk); }

            // Floor lists from single-select (at most one)
            var floorIds     = new List<long>();
            var floorLinkIds = new List<long>();
            if (_selectedFloorDisplay != null && _floorDisplayToRef.TryGetValue(_selectedFloorDisplay, out var fRef))
            { floorIds.Add(fRef.id); floorLinkIds.Add(fRef.lnk); }

            // Persist settings
            var s = ClashDimensionSettings.Instance;
            s.Group1Mode          = _g1.Mode;
            s.Group2Mode          = _g2.Mode;
            s.Group1RuleKeys      = new List<string>(spec1.RuleKeys);
            s.Group2RuleKeys      = new List<string>(spec2.RuleKeys);
            s.Group1Categories    = new List<string>(spec1.Categories);
            s.Group2Categories    = new List<string>(spec2.Categories);
            s.Group1ElemIds       = new List<long>(spec1.ElemIds);
            s.Group1ElemLinkIds   = new List<long>(spec1.ElemLinkIds);
            s.Group2ElemIds       = new List<long>(spec2.ElemIds);
            s.Group2ElemLinkIds   = new List<long>(spec2.ElemLinkIds);
            s.Group1SourceLinkIds = new List<long>(spec1.SourceLinkIds);
            s.Group2SourceLinkIds = new List<long>(spec2.SourceLinkIds);
            s.GridIds             = gridIds;
            s.GridLinkIds         = gridLinkIds;
            s.FloorIds            = floorIds;
            s.FloorLinkIds        = floorLinkIds;
            s.ToleranceMm         = _toleranceMm;
            s.DimStyleName        = _dimStyleName;
            s.DimLineOffsetMm     = _dimLineOffsetMm;
            s.DimTarget           = _dimTarget;
            s.FillStyle           = _fillStyle;
            s.CrossLineTypeName   = _crossLineTypeName;
            s.ClearPrevious       = _clearPrevious;
            s.MaxClashes          = _maxClashes;
            s.ShowAllDocuments    = _showAll;
            s.Save();

            // Wire handler
            _handler.ViewIds = _selectedViewNames
                .Where(n => _viewNameToId.ContainsKey(n))
                .Select(n => _viewNameToId[n])
                .ToList();
            _handler.Group1Spec        = spec1;
            _handler.Group2Spec        = spec2;
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
