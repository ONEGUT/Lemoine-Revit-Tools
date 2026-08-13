using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LemoineTools.Framework;

namespace LemoineTools.Framework.Controls
{
    public partial class MultiSelectTabs : UserControl
    {
        private const string SelectedGroupKey = "__selected__";
        private const string ResultsGroupKey  = "__results__";

        private Dictionary<string, List<string>> _groups          = new Dictionary<string, List<string>>();
        private List<string>                      _orderedGroupKeys = new List<string>();
        private readonly HashSet<string>          _selected        = new HashSet<string>();
        private string?                           _activeGroup;

        // Tab lookup by group key — the pinned Results tab appears and disappears with the
        // query, so badge refreshes address tabs by key rather than by list index.
        private readonly Dictionary<string, Border> _tabByKey = new Dictionary<string, Border>();

        // ── Search state ──────────────────────────────────────────────────────
        private string  _query            = "";
        private string? _preSearchGroup;          // tab to restore when the query is cleared
        private bool    _suppressSearchEvents;    // guards the reset inside SetGroups
        private TextBlock _matchCount    = null!;
        private Button    _clearSearchBtn = null!;

        public IReadOnlyCollection<string> SelectedItems => _selected;
        public event Action<IReadOnlyCollection<string>>? SelectionChanged;

        /// <summary>
        /// When true, only one item can be selected at a time across all groups: checking an
        /// item clears any prior selection and the per-group "All" row is hidden. Defaults to
        /// false (multi-select). Set before <see cref="SetGroups"/>.
        /// </summary>
        public bool SingleSelect { get; set; } = false;

        /// <summary>
        /// Optional parent → children nesting (same contract as TagChipInput.Hierarchy).
        /// Within a group's checklist, a child whose parent is in the same group is hidden from
        /// the flat level and rendered indented under the parent's expand caret. Parents stay
        /// individually checkable; the caret only toggles expansion. The "All" row and tab
        /// badges cover every item in the group, nested or not. Set before
        /// <see cref="SetGroups"/>; <see langword="null"/> (the default) keeps flat lists.
        /// While the built-in search box holds a query the nesting is set aside and matching
        /// items are listed flat, so a hit is never hidden behind a collapsed caret.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>>? Hierarchy { get; set; }

        /// <summary>
        /// Items shown greyed-out and un-checkable (e.g. a datum that already exists in the
        /// host, so copying it again isn't offered) — still listed, so the user sees WHY it's
        /// absent from the selection, rather than the item silently disappearing. Never
        /// included in the per-group "All" toggle or in <see cref="SelectionChanged"/> results.
        /// Set before <see cref="SetGroups"/>; <see langword="null"/> (the default) disables
        /// no items. Honoured on the flat list path, the search Results list, and any
        /// query-filtered group list (searching flattens); a <see cref="Hierarchy"/> list with
        /// no active query still renders its nested rows enabled.
        /// </summary>
        public IReadOnlyCollection<string>? DisabledItems { get; set; }

        private bool IsDisabled(string item) =>
            DisabledItems != null && DisabledItems.Contains(item);

        // Parents whose children are currently visible (reset on SetGroups).
        private readonly HashSet<string> _expandedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Accessible name announced by screen readers when the control receives focus.</summary>
        public string AccessibleName { set => AutomationProperties.SetName(this, value ?? string.Empty); }

        public MultiSelectTabs()
        {
            InitializeComponent();

            _outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _outer.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            _body.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
            _tabColumn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            _searchHint.Text = AppStrings.T("controls.pickers.multiSelectTabs.searchHint");
            _searchHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _searchHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _searchHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            _matchCount = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0),
                Visibility        = Visibility.Collapsed,
            };
            _matchCount.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            _matchCount.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _searchActions.Children.Add(_matchCount);

            _clearSearchBtn = ControlStyles.BuildSmallButton(AppStrings.T("controls.pickers.multiSelectTabs.clear"));
            _clearSearchBtn.Visibility = Visibility.Collapsed;
            _clearSearchBtn.Click += (s, e) => _searchBox.Text = "";  // TextChanged does the rest
            _searchActions.Children.Add(_clearSearchBtn);

            _searchBox.TextChanged += OnSearchTextChanged;

            // Both inner lists bubble the wheel to the page once they hit their scroll limit,
            // so hovering a tab/checkbox list doesn't trap page scrolling.
            ControlStyles.WireBubblingScroll(_tabScroll);
            ControlStyles.WireBubblingScroll(_checkScroll);
        }

        // ── Search ────────────────────────────────────────────────────────────

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressSearchEvents) return;

            string newQuery = (_searchBox.Text ?? "").Trim();
            if (string.Equals(newQuery, _query, StringComparison.Ordinal))
            {
                UpdateSearchChrome();
                return;
            }

            bool wasSearching = _query.Length  > 0;
            bool nowSearching = newQuery.Length > 0;
            _query = newQuery;

            string? target;
            if (nowSearching && !wasSearching)
            {
                // Entering search: remember where the user was, then jump to Results so a
                // match in a tab they aren't looking at is visible immediately.
                _preSearchGroup = _activeGroup;
                target          = ResultsGroupKey;
            }
            else if (!nowSearching && wasSearching)
            {
                // Leaving search: Results collapses, so never leave it active.
                target = _preSearchGroup != null && _preSearchGroup != ResultsGroupKey
                    ? _preSearchGroup
                    : _orderedGroupKeys.FirstOrDefault() ?? SelectedGroupKey;
                _preSearchGroup = null;
            }
            else
            {
                // Refining the query — stay on whichever tab is open (Results or a group).
                target = _activeGroup;
            }

            UpdateSearchChrome();
            ActivateGroup(target);
        }

        private bool IsSearching => _query.Length > 0;

        private bool Matches(string item) =>
            !IsSearching || (item != null && item.IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0);

        private List<string> MatchingItems(string groupName) =>
            _groups.TryGetValue(groupName, out var items) && items != null
                ? items.Where(Matches).ToList()
                : new List<string>();

        /// <summary>
        /// Every matching item across all groups, paired with the group it is shown under, in
        /// tab order. An item listed in more than one group yields a single row (checking it
        /// selects the same string either way), attributed to the first group that carries it.
        /// </summary>
        private List<KeyValuePair<string, string>> MatchingItemsWithGroup()
        {
            var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<KeyValuePair<string, string>>();
            foreach (var group in _orderedGroupKeys)
            {
                if (!_groups.TryGetValue(group, out var items) || items == null) continue;
                foreach (var item in items)
                {
                    if (!Matches(item) || !seen.Add(item)) continue;
                    result.Add(new KeyValuePair<string, string>(item, group));
                }
            }
            return result;
        }

        // Placeholder, match count, Clear button and the pinned Results tab all track the query.
        private void UpdateSearchChrome()
        {
            _searchHint.Visibility = (_searchBox.Text ?? "").Length == 0
                ? Visibility.Visible : Visibility.Collapsed;

            _matchCount.Visibility     = IsSearching ? Visibility.Visible : Visibility.Collapsed;
            _clearSearchBtn.Visibility = IsSearching ? Visibility.Visible : Visibility.Collapsed;

            if (IsSearching)
            {
                int total = MatchingItemsWithGroup().Count;
                _matchCount.Text = total == 1
                    ? AppStrings.T("controls.pickers.multiSelectTabs.matchCountOne")
                    : AppStrings.T("controls.pickers.multiSelectTabs.matchCount", total);
                _matchCount.SetResourceReference(TextBlock.ForegroundProperty,
                    total > 0 ? "LemoineAccent" : "LemoineTextDim");
            }

            if (_tabByKey.TryGetValue(ResultsGroupKey, out var resultsTab))
                resultsTab.Visibility = IsSearching ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetGroups(Dictionary<string, List<string>> groups,
                              IEnumerable<string>? initialSelected = null)
        {
            _groups = groups;
            _selected.Clear();
            _expandedParents.Clear();
            if (initialSelected != null)
                foreach (var s in initialSelected) _selected.Add(s);

            // A fresh item set invalidates any active query. Suppressed so the reset can't
            // re-enter ActivateGroup while the tab list is half-rebuilt.
            _suppressSearchEvents = true;
            _searchBox.Text       = "";
            _suppressSearchEvents = false;
            _query                = "";
            _preSearchGroup       = null;

            // Sort tabs alphabetically; "Other" always last.
            _orderedGroupKeys = groups.Keys
                .OrderBy(k => k == "Other" ? 1 : 0)
                .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _tabStack.Children.Clear();
            _tabByKey.Clear();

            // Pinned "Results" tab — hidden until there is a query to have results for.
            var resultsTab = BuildTab(ResultsGroupKey);
            resultsTab.Visibility = Visibility.Collapsed;
            _tabStack.Children.Add(resultsTab);

            // Pinned "Selected" tab
            var selectedTab = BuildTab(SelectedGroupKey);
            _tabStack.Children.Add(selectedTab);

            var sep = new Rectangle { Height = 1, Margin = new Thickness(6, 0, 6, 0) };
            sep.SetResourceReference(Rectangle.FillProperty, "LemoineBorder");
            _tabStack.Children.Add(sep);

            foreach (var group in _orderedGroupKeys)
            {
                var tab = BuildTab(group);
                _tabStack.Children.Add(tab);
            }

            UpdateSearchChrome();
            ActivateGroup(_orderedGroupKeys.FirstOrDefault());
            // Notify subscribers of the post-setup selection state so ViewModels that
            // mirror selection into their own fields are always in sync after SetGroups.
            SelectionChanged?.Invoke(SelectedItems);
        }

        private Border BuildTab(string groupName)
        {
            string displayName =
                groupName == SelectedGroupKey ? AppStrings.T("controls.pickers.multiSelectTabs.selectedTabLabel") :
                groupName == ResultsGroupKey  ? AppStrings.T("controls.pickers.multiSelectTabs.resultsTabLabel")  :
                groupName;

            var badgeBorder = new Border
            {
                Padding    = new Thickness(5, 1, 5, 1),
                Margin     = new Thickness(4, 0, 0, 0),
                Visibility = Visibility.Visible,
                Tag        = "badge",
            };
            badgeBorder.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
            badgeBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            badgeBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var badgeText = new TextBlock { FontWeight = FontWeights.Medium, Tag = "badgeText" };
            badgeText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            badgeText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            badgeBorder.Child = badgeText;

            var label = new TextBlock
            {
                Text              = displayName,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            label.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var dp = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(badgeBorder, Dock.Right);
            dp.Children.Add(badgeBorder);
            dp.Children.Add(label);

            var tab = new Border
            {
                Padding         = new Thickness(8, 7, 8, 7),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Cursor          = Cursors.Hand,
                Child           = dp,
                Tag             = new object[] { label, badgeBorder, badgeText, groupName },
            };
            tab.MouseLeftButtonDown += (s, e) => ActivateGroup(groupName);
            MotionEffects.WireToggleHover(tab, () => groupName == _activeGroup);

            _tabByKey[groupName] = tab;
            SetTabStyle(tab, false);
            UpdateTabCounter(tab, groupName);
            return tab;
        }

        private void SetTabStyle(Border tab, bool active)
        {
            if (active) tab.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
            else        tab.Background = Brushes.Transparent;
            if (active) tab.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            else        tab.BorderBrush = Brushes.Transparent;
            if (tab.Tag is object[] arr && arr[0] is TextBlock lbl)
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
        }

        private void UpdateTabCounter(Border tab, string groupName)
        {
            if (!(tab.Tag is object[] arr)) return;
            var badgeBorder = arr[1] as Border;
            var badgeText   = arr[2] as TextBlock;
            if (badgeBorder == null || badgeText == null) return;

            // "highlighted" drives the accent badge: selection count normally, match count
            // while a query is active (a group with no hits reads as an empty badge, dimmed).
            int  highlighted;
            bool dimLabel = false;

            if (groupName == ResultsGroupKey)
            {
                highlighted    = MatchingItemsWithGroup().Count;
                badgeText.Text = $"{highlighted}";
            }
            else if (groupName == SelectedGroupKey)
            {
                // The Selected tab is the review list for current picks — never filtered,
                // so its badge stays the total selected count even while searching.
                highlighted    = _selected.Count;
                badgeText.Text = $"{highlighted}";
            }
            else if (IsSearching)
            {
                highlighted    = MatchingItems(groupName).Count;
                badgeText.Text = $"{highlighted}";
                dimLabel       = highlighted == 0;
            }
            else
            {
                var items = _groups.TryGetValue(groupName, out var list) ? list : new List<string>();
                highlighted    = items.Count(i => _selected.Contains(i));
                badgeText.Text = $"{highlighted}/{items.Count}";
            }

            badgeBorder.Visibility = Visibility.Visible;

            if (highlighted > 0)
            {
                badgeBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                badgeBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                badgeText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            }
            else
            {
                badgeBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                badgeBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                badgeText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            }

            // A zero-match tab stays listed (so the user sees it has nothing, rather than the
            // tab list silently shrinking) but is dimmed to point at the tabs that do have hits.
            if (arr[0] is TextBlock label)
                label.SetResourceReference(TextBlock.ForegroundProperty,
                    dimLabel ? "LemoineTextDim" : "LemoineText");
        }

        private void RefreshAllCounters()
        {
            if (_tabByKey.TryGetValue(ResultsGroupKey, out var resultsTab))
                UpdateTabCounter(resultsTab, ResultsGroupKey);
            if (_tabByKey.TryGetValue(SelectedGroupKey, out var selectedTab))
                UpdateTabCounter(selectedTab, SelectedGroupKey);
            foreach (var key in _orderedGroupKeys)
                if (_tabByKey.TryGetValue(key, out var tab))
                    UpdateTabCounter(tab, key);
        }

        private void ActivateGroup(string? groupName)
        {
            if (groupName == null) return;
            _activeGroup = groupName;

            foreach (var kv in _tabByKey)
                SetTabStyle(kv.Value, kv.Key == groupName);
            // Badges (and the zero-match dimming) are recomputed after SetTabStyle, which
            // resets every tab label to the normal foreground.
            RefreshAllCounters();

            _checkStack.Children.Clear();

            if (groupName == ResultsGroupKey)
            {
                BuildResultsList();
                return;
            }

            if (groupName == SelectedGroupKey)
            {
                if (_selected.Count == 0)
                {
                    _checkStack.Children.Add(BuildMessageRow(
                        AppStrings.T("controls.pickers.multiSelectTabs.noItemsSelected")));
                    return;
                }
                foreach (var item in _selected.ToList())
                {
                    var captured = item;
                    _checkStack.Children.Add(BuildCheckItem(
                        item, true, false,
                        on =>
                        {
                            if (on) _selected.Add(captured);
                            else    _selected.Remove(captured);
                            SelectionChanged?.Invoke(SelectedItems);
                            RefreshAllCounters();
                            ActivateGroup(SelectedGroupKey);
                        }));
                }
                return;
            }

            // Unreachable by construction (every group tab is built from a _groups key), so an
            // empty checklist here means the tab list and the item map have drifted apart —
            // report it rather than rendering a silently empty group.
            if (!_groups.TryGetValue(groupName, out var groupItems) || groupItems == null)
            {
                DiagnosticsLog.Warn("MultiSelectTabs: activate group",
                    $"No item list for active group '{groupName}' — checklist left empty.");
                return;
            }

            // While searching, a group tab lists only its own matches — and lists them flat,
            // so a match can never be hidden behind a collapsed parent caret.
            var allItems = IsSearching ? MatchingItems(groupName) : groupItems;

            if (IsSearching && allItems.Count == 0)
            {
                _checkStack.Children.Add(BuildMessageRow(
                    AppStrings.T("controls.pickers.multiSelectTabs.noMatches", _query)));
                return;
            }

            // The per-group "All" row is meaningless when only one item may be selected.
            if (!SingleSelect)
            {
                _checkStack.Children.Add(BuildAllRow(
                    AllRowLabel(groupName, allItems.Count), allItems, groupName));

                var divider = new Rectangle { Height = 1, Margin = new Thickness(0, 0, 0, 5) };
                divider.SetResourceReference(Rectangle.FillProperty, "LemoineBorder");
                _checkStack.Children.Add(divider);
            }

            if (Hierarchy != null && !IsSearching)
            {
                // Build the set of children that have their parent present in THIS group,
                // so they render indented under the caret instead of at the top level.
                var groupChildSet = BuildGroupChildSet(allItems);

                foreach (var item in allItems)
                {
                    if (groupChildSet.Contains(item)) continue; // rendered under its parent

                    var captured = item;
                    Hierarchy.TryGetValue(item, out var rawKids);
                    // Only count children that are actually present in this group's item list.
                    var activeKids = rawKids != null
                        ? rawKids.Where(k => allItems.Contains(k)).ToList()
                        : new List<string>();
                    bool hasChildren  = activeKids.Count > 0;
                    bool expanded     = _expandedParents.Contains(item);
                    bool isChecked    = _selected.Contains(item);
                    // Indeterminate: parent itself unselected but at least one child is.
                    bool indeterminate = !isChecked && hasChildren
                                        && activeKids.Any(k => _selected.Contains(k));

                    _checkStack.Children.Add(BuildParentCheckItem(
                        item, hasChildren, expanded, isChecked, indeterminate,
                        on =>
                        {
                            if (on) { if (SingleSelect) _selected.Clear(); _selected.Add(captured); }
                            else    _selected.Remove(captured);
                            SelectionChanged?.Invoke(SelectedItems);
                            RefreshAllCounters();
                            ActivateGroup(groupName);
                        },
                        () =>
                        {
                            if (!_expandedParents.Remove(captured)) _expandedParents.Add(captured);
                            ActivateGroup(groupName);
                        }));

                    if (hasChildren && expanded)
                    {
                        foreach (var kid in activeKids)
                        {
                            var capKid = kid;
                            _checkStack.Children.Add(BuildIndentedCheckItem(
                                kid, _selected.Contains(kid),
                                on =>
                                {
                                    if (on) { if (SingleSelect) _selected.Clear(); _selected.Add(capKid); }
                                    else    _selected.Remove(capKid);
                                    SelectionChanged?.Invoke(SelectedItems);
                                    RefreshAllCounters();
                                    ActivateGroup(groupName);
                                }));
                        }
                    }
                }
            }
            else
            {
                foreach (var item in allItems)
                {
                    var captured = item;
                    bool disabled = IsDisabled(item);
                    _checkStack.Children.Add(BuildCheckItem(
                        item, _selected.Contains(item), false,
                        on =>
                        {
                            if (on)
                            {
                                if (SingleSelect) _selected.Clear();
                                _selected.Add(captured);
                            }
                            else _selected.Remove(captured);
                            SelectionChanged?.Invoke(SelectedItems);
                            RefreshAllCounters();
                            if (SingleSelect) ActivateGroup(groupName);
                            else              RefreshAllRow(AllRowLabel(groupName, allItems.Count),
                                                            allItems, groupName);
                        },
                        disabled: disabled,
                        highlight: true));
                }
            }
        }

        // The cross-group match list shown on the pinned "Results" tab: every hit from every
        // group, in tab order, each row tagged with the group it lives in.
        private void BuildResultsList()
        {
            var hits = MatchingItemsWithGroup();
            if (hits.Count == 0)
            {
                _checkStack.Children.Add(BuildMessageRow(
                    AppStrings.T("controls.pickers.multiSelectTabs.noMatches", _query)));
                return;
            }

            var hitItems = hits.Select(h => h.Key).ToList();

            if (!SingleSelect)
            {
                _checkStack.Children.Add(BuildAllRow(
                    AppStrings.T("controls.pickers.multiSelectTabs.allMatches", hitItems.Count),
                    hitItems, ResultsGroupKey));

                var divider = new Rectangle { Height = 1, Margin = new Thickness(0, 0, 0, 5) };
                divider.SetResourceReference(Rectangle.FillProperty, "LemoineBorder");
                _checkStack.Children.Add(divider);
            }

            foreach (var hit in hits)
            {
                var captured  = hit.Key;
                bool disabled = IsDisabled(captured);
                _checkStack.Children.Add(BuildCheckItem(
                    captured, _selected.Contains(captured), false,
                    on =>
                    {
                        if (on)
                        {
                            if (SingleSelect) _selected.Clear();
                            _selected.Add(captured);
                        }
                        else _selected.Remove(captured);
                        SelectionChanged?.Invoke(SelectedItems);
                        RefreshAllCounters();
                        if (SingleSelect) ActivateGroup(ResultsGroupKey);
                        else              RefreshAllRow(
                            AppStrings.T("controls.pickers.multiSelectTabs.allMatches", hitItems.Count),
                            hitItems, ResultsGroupKey);
                    },
                    disabled:   disabled,
                    highlight:  true,
                    groupLabel: hit.Value));
            }
        }

        // "All <group>" normally; "All N matches in <group>" while a query narrows the list.
        private string AllRowLabel(string groupName, int visibleCount) =>
            IsSearching
                ? AppStrings.T("controls.pickers.multiSelectTabs.allMatchesInGroup", visibleCount, groupName)
                : AppStrings.T("controls.pickers.multiSelectTabs.allGroupRow", groupName);

        // Bulk toggle over exactly the rows currently visible below it — disabled items are
        // excluded from both the all-checked/some-checked math and what the toggle writes.
        private UIElement BuildAllRow(string labelText, List<string> items, string rebuildGroup)
        {
            var toggleable   = items.Where(x => !IsDisabled(x)).ToList();
            bool allChecked  = toggleable.Count > 0 && toggleable.All(x => _selected.Contains(x));
            bool someChecked = toggleable.Any(x => _selected.Contains(x)) && !allChecked;

            return BuildCheckItem(labelText, allChecked, someChecked,
                on =>
                {
                    if (on) foreach (var it in toggleable) _selected.Add(it);
                    else    foreach (var it in toggleable) _selected.Remove(it);
                    SelectionChanged?.Invoke(SelectedItems);
                    RefreshAllCounters();
                    ActivateGroup(rebuildGroup);
                },
                bold: true);
        }

        // An italic dim message filling the checklist area (nothing selected / no matches).
        private UIElement BuildMessageRow(string text)
        {
            var tb = new TextBlock
            {
                Text         = text,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(4, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        // Returns the set of items in the given group whose parent is ALSO in that group.
        // These are rendered indented under the parent's caret, not at the top level.
        private HashSet<string> BuildGroupChildSet(List<string> groupItems)
        {
            var result   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Hierarchy == null) return result;
            var groupSet = new HashSet<string>(groupItems, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Hierarchy)
            {
                if (!groupSet.Contains(kv.Key) || kv.Value == null) continue;
                foreach (var kid in kv.Value)
                    if (groupSet.Contains(kid)) result.Add(kid);
            }
            return result;
        }

        // Swaps just the "All" row (always child 0) so checking one item re-syncs the bulk
        // toggle without rebuilding the whole list — a rebuild would reset the scroll position
        // out from under the click.
        private void RefreshAllRow(string labelText, List<string> items, string rebuildGroup)
        {
            if (_checkStack.Children.Count == 0) return;
            _checkStack.Children.RemoveAt(0);
            _checkStack.Children.Insert(0, BuildAllRow(labelText, items, rebuildGroup));
        }

        // A flat checkbox row. Used for the "All" row, Selected-tab items, and non-hierarchy groups.
        // A disabled row (see DisabledItems) is shown dimmed, its checkbox non-interactive, and
        // never responds to a row click — it exists only to show the user why the item is absent
        // from the selection.
        private UIElement BuildCheckItem(string text, bool isChecked, bool indeterminate,
            Action<bool> onToggle, bool bold = false, bool disabled = false,
            bool highlight = false, string? groupLabel = null)
        {
            var cb = new CheckBox
            {
                IsChecked         = indeterminate ? (bool?)null : isChecked,
                IsThreeState      = indeterminate,
                IsEnabled         = !disabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0),
            };

            var lbl = new TextBlock
            {
                FontWeight        = bold ? FontWeights.Medium : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, disabled ? "LemoineTextDim" : "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            SetLabelText(lbl, text, highlight);

            Panel row;
            if (groupLabel == null)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(cb);
                sp.Children.Add(lbl);
                row = sp;
            }
            else
            {
                // Which group a Results row came from, pinned right. The label takes a "*"
                // column so a long name ellipsizes instead of shoving the tag off the row.
                // ⚠ "*" width is safe here: _checkScroll disables horizontal scrolling, so the
                //   row is measured against a finite viewport width (only HEIGHT is infinite
                //   inside the vertical StackPanel).
                lbl.TextTrimming = TextTrimming.CharacterEllipsis;

                var tag = new TextBlock
                {
                    Text              = groupLabel,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(8, 0, 2, 0),
                };
                tag.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                tag.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                tag.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(cb,  0);
                Grid.SetColumn(lbl, 1);
                Grid.SetColumn(tag, 2);
                g.Children.Add(cb);
                g.Children.Add(lbl);
                g.Children.Add(tag);
                row = g;
            }

            row.Margin = new Thickness(0, 0, 0, 5);
            row.Cursor = disabled ? Cursors.Arrow : Cursors.Hand;

            if (disabled)
            {
                // Read-only row — no hover/click wiring. Still needs a hit-testable background
                // so it doesn't fall through to whatever sits behind the list.
                row.Background = Brushes.Transparent;
                return row;
            }

            MotionEffects.WireHover(row, normalBgKey: null, hoverBgKey: "LemoineAccentDim");

            cb.Checked   += (s, e) => onToggle(true);
            cb.Unchecked += (s, e) => onToggle(false);

            row.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is CheckBox) return;
                cb.IsChecked = !(cb.IsChecked == true);
                e.Handled = true;
            };
            return row;
        }

        /// <summary>
        /// Writes a row label, tinting the span that matched the active query so the reason a
        /// row is listed is visible at a glance. Plain text when there is no query, no hit in
        /// this label, or highlighting is off (the "All …" rows, whose wording is chrome rather
        /// than item text).
        /// </summary>
        private void SetLabelText(TextBlock lbl, string text, bool highlight)
        {
            text = text ?? string.Empty;
            int at = highlight && IsSearching
                ? text.IndexOf(_query, StringComparison.OrdinalIgnoreCase)
                : -1;

            if (at < 0) { lbl.Text = text; return; }

            lbl.Inlines.Clear();
            if (at > 0) lbl.Inlines.Add(new Run(text.Substring(0, at)));

            var hit = new Run(text.Substring(at, _query.Length)) { FontWeight = FontWeights.Bold };
            hit.SetResourceReference(Run.BackgroundProperty, "LemoineAccentDim");
            lbl.Inlines.Add(hit);

            int end = at + _query.Length;
            if (end < text.Length) lbl.Inlines.Add(new Run(text.Substring(end)));
        }

        // A top-level row in hierarchy mode. Left 16 px is either a clickable caret (when this
        // item has children in the group) or a blank spacer (leaf — keeps column aligned with
        // parents). The checkbox+label to the right toggles the item itself independently of
        // any children; the indeterminate state signals that some-but-not-all children are checked.
        private UIElement BuildParentCheckItem(string item, bool hasChildren, bool expanded,
            bool isChecked, bool indeterminate, Action<bool> onToggle, Action onCaretToggle)
        {
            string expandedGlyph  = char.ConvertFromUtf32(0x25BE); // ▾
            string collapsedGlyph = char.ConvertFromUtf32(0x25B8); // ▸

            var caret = new TextBlock
            {
                Width             = 16,
                TextAlignment     = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Text              = hasChildren ? (expanded ? expandedGlyph : collapsedGlyph) : "",
                Cursor            = hasChildren ? Cursors.Hand : Cursors.Arrow,
            };
            caret.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            caret.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            caret.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            caret.Background = Brushes.Transparent; // hit-testable across full 16px box
            if (hasChildren)
                caret.MouseLeftButtonDown += (s, e) => { e.Handled = true; onCaretToggle(); };

            var cb = new CheckBox
            {
                IsChecked         = indeterminate ? (bool?)null : isChecked,
                IsThreeState      = indeterminate,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0),
            };

            var lbl = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Text              = item,
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 5),
                Cursor      = Cursors.Hand,
            };
            sp.Children.Add(caret);
            sp.Children.Add(cb);
            sp.Children.Add(lbl);

            MotionEffects.WireHover(sp, normalBgKey: null, hoverBgKey: "LemoineAccentDim");

            cb.Checked   += (s, e) => onToggle(true);
            cb.Unchecked += (s, e) => onToggle(false);

            sp.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is CheckBox || e.OriginalSource is TextBlock t && t == caret) return;
                cb.IsChecked = !(cb.IsChecked == true);
                e.Handled = true;
            };
            return sp;
        }

        // An indented child row in hierarchy mode (16 px left margin = caret column width).
        private UIElement BuildIndentedCheckItem(string item, bool isChecked, Action<bool> onToggle)
        {
            var cb = new CheckBox
            {
                IsChecked         = isChecked,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0),
            };

            var lbl = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Text              = item,
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(32, 0, 0, 5), // indent past parent caret (16) + checkbox (~16)
                Cursor      = Cursors.Hand,
            };
            sp.Children.Add(cb);
            sp.Children.Add(lbl);

            MotionEffects.WireHover(sp, normalBgKey: null, hoverBgKey: "LemoineAccentDim");

            cb.Checked   += (s, e) => onToggle(true);
            cb.Unchecked += (s, e) => onToggle(false);

            sp.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is CheckBox) return;
                cb.IsChecked = !(cb.IsChecked == true);
                e.Handled = true;
            };
            return sp;
        }
    }
}
