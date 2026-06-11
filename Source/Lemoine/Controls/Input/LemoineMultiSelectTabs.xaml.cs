using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LemoineTools.Lemoine.Controls
{
    public partial class LemoineMultiSelectTabs : UserControl
    {
        private const string SelectedGroupKey = "__selected__";

        private Dictionary<string, List<string>> _groups          = new Dictionary<string, List<string>>();
        private List<string>                      _orderedGroupKeys = new List<string>();
        private readonly HashSet<string>          _selected        = new HashSet<string>();
        private string?                           _activeGroup;
        private readonly List<Border>             _tabBorders      = new List<Border>();

        public IReadOnlyCollection<string> SelectedItems => _selected;
        public event Action<IReadOnlyCollection<string>>? SelectionChanged;

        /// <summary>
        /// When true, only one item can be selected at a time across all groups: checking an
        /// item clears any prior selection and the per-group "All" row is hidden. Defaults to
        /// false (multi-select). Set before <see cref="SetGroups"/>.
        /// </summary>
        public bool SingleSelect { get; set; } = false;

        /// <summary>
        /// Optional parent → children nesting (same contract as LemoineTagChipInput.Hierarchy).
        /// Within a group's checklist, a child whose parent is in the same group is hidden from
        /// the flat level and rendered indented under the parent's expand caret. Parents stay
        /// individually checkable; the caret only toggles expansion. The "All" row and tab
        /// badges cover every item in the group, nested or not. Set before
        /// <see cref="SetGroups"/>; <see langword="null"/> (the default) keeps flat lists.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>>? Hierarchy { get; set; }

        // Parents whose children are currently visible (reset on SetGroups).
        private readonly HashSet<string> _expandedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Accessible name announced by screen readers when the control receives focus.</summary>
        public string AccessibleName { set => AutomationProperties.SetName(this, value ?? string.Empty); }

        public LemoineMultiSelectTabs()
        {
            InitializeComponent();
            if (Content is Border outer)
            {
                outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                outer.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                if (outer.Child is Grid g && g.Children[0] is Border leftBorder)
                    leftBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            }
            // Both inner lists bubble the wheel to the page once they hit their scroll limit,
            // so hovering a tab/checkbox list doesn't trap page scrolling.
            LemoineControlStyles.WireBubblingScroll(_tabScroll);
            LemoineControlStyles.WireBubblingScroll(_checkScroll);
        }

        public void SetGroups(Dictionary<string, List<string>> groups,
                              IEnumerable<string>? initialSelected = null)
        {
            _groups = groups;
            _selected.Clear();
            _expandedParents.Clear();
            if (initialSelected != null)
                foreach (var s in initialSelected) _selected.Add(s);

            // Sort tabs alphabetically; "Other" always last.
            _orderedGroupKeys = groups.Keys
                .OrderBy(k => k == "Other" ? 1 : 0)
                .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _tabStack.Children.Clear();
            _tabBorders.Clear();

            // Pinned "Selected" tab at index 0
            var selectedTab = BuildTab(SelectedGroupKey);
            _tabBorders.Add(selectedTab);
            _tabStack.Children.Add(selectedTab);

            var sep = new Rectangle { Height = 1, Margin = new Thickness(6, 0, 6, 0) };
            sep.SetResourceReference(Rectangle.FillProperty, "LemoineBorder");
            _tabStack.Children.Add(sep);

            foreach (var group in _orderedGroupKeys)
            {
                var tab = BuildTab(group);
                _tabBorders.Add(tab);
                _tabStack.Children.Add(tab);
            }

            ActivateGroup(_orderedGroupKeys.FirstOrDefault());
            // Notify subscribers of the post-setup selection state so ViewModels that
            // mirror selection into their own fields are always in sync after SetGroups.
            SelectionChanged?.Invoke(SelectedItems);
        }

        private Border BuildTab(string groupName)
        {
            string displayName = groupName == SelectedGroupKey ? "Selected" : groupName;

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
            LemoineMotion.WireToggleHover(tab, () => groupName == _activeGroup);

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

            int selected;
            if (groupName == SelectedGroupKey)
            {
                selected       = _selected.Count;
                badgeText.Text = $"{selected}";
            }
            else
            {
                var items = _groups.TryGetValue(groupName, out var list) ? list : new List<string>();
                selected       = items.Count(i => _selected.Contains(i));
                badgeText.Text = $"{selected}/{items.Count}";
            }

            badgeBorder.Visibility = Visibility.Visible;

            if (selected > 0)
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
        }

        private void RefreshAllCounters()
        {
            UpdateTabCounter(_tabBorders[0], SelectedGroupKey);
            for (int i = 0; i < _orderedGroupKeys.Count && (i + 1) < _tabBorders.Count; i++)
                UpdateTabCounter(_tabBorders[i + 1], _orderedGroupKeys[i]);
        }

        private void ActivateGroup(string? groupName)
        {
            if (groupName == null) return;
            _activeGroup = groupName;

            SetTabStyle(_tabBorders[0], groupName == SelectedGroupKey);
            int idx = 1;
            foreach (var key in _orderedGroupKeys)
                SetTabStyle(_tabBorders[idx++], key == groupName);

            _checkStack.Children.Clear();

            if (groupName == SelectedGroupKey)
            {
                if (_selected.Count == 0)
                {
                    var none = new TextBlock
                    {
                        Text         = "No items selected.",
                        FontStyle    = FontStyles.Italic,
                        Margin       = new Thickness(4, 6, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                    };
                    none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    _checkStack.Children.Add(none);
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

            var allItems = _groups[groupName];

            // The per-group "All" row is meaningless when only one item may be selected.
            if (!SingleSelect)
            {
                bool allChecked  = allItems.Count > 0 && allItems.All(x => _selected.Contains(x));
                bool someChecked = allItems.Any(x => _selected.Contains(x)) && !allChecked;

                _checkStack.Children.Add(BuildCheckItem(
                    $"All {groupName}", allChecked, someChecked,
                    on =>
                    {
                        if (on) foreach (var it in allItems) _selected.Add(it);
                        else    foreach (var it in allItems) _selected.Remove(it);
                        SelectionChanged?.Invoke(SelectedItems);
                        RefreshAllCounters();
                        ActivateGroup(groupName);
                    },
                    bold: true));

                var divider = new Rectangle { Height = 1, Margin = new Thickness(0, 0, 0, 5) };
                divider.SetResourceReference(Rectangle.FillProperty, "LemoineBorder");
                _checkStack.Children.Add(divider);
            }

            if (Hierarchy != null)
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
                            else              RefreshSelectAllRow(groupName, allItems);
                        }));
                }
            }
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

        private void RefreshSelectAllRow(string groupName, List<string> items)
        {
            if (_checkStack.Children.Count == 0) return;
            bool allChecked  = items.Count > 0 && items.All(x => _selected.Contains(x));
            bool someChecked = items.Any(x => _selected.Contains(x)) && !allChecked;

            _checkStack.Children.RemoveAt(0);
            var newAll = BuildCheckItem(
                $"All {groupName}", allChecked, someChecked,
                on =>
                {
                    if (on) foreach (var it in items) _selected.Add(it);
                    else    foreach (var it in items) _selected.Remove(it);
                    SelectionChanged?.Invoke(SelectedItems);
                    RefreshAllCounters();
                    ActivateGroup(groupName);
                },
                bold: true);
            _checkStack.Children.Insert(0, newAll);
        }

        // A flat checkbox row. Used for the "All" row, Selected-tab items, and non-hierarchy groups.
        private UIElement BuildCheckItem(string text, bool isChecked, bool indeterminate,
            Action<bool> onToggle, bool bold = false)
        {
            var cb = new CheckBox
            {
                IsChecked         = indeterminate ? (bool?)null : isChecked,
                IsThreeState      = indeterminate,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0),
            };

            var lbl = new TextBlock
            {
                FontWeight        = bold ? FontWeights.Medium : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Text              = text,
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
            sp.Children.Add(cb);
            sp.Children.Add(lbl);

            LemoineMotion.WireHover(sp, normalBgKey: null, hoverBgKey: "LemoineAccentDim");

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

            LemoineMotion.WireHover(sp, normalBgKey: null, hoverBgKey: "LemoineAccentDim");

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
                Margin      = new Thickness(16, 0, 0, 5), // indent = caret column width
                Cursor      = Cursors.Hand,
            };
            sp.Children.Add(cb);
            sp.Children.Add(lbl);

            LemoineMotion.WireHover(sp, normalBgKey: null, hoverBgKey: "LemoineAccentDim");

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
