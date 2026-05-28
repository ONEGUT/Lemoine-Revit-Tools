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

        private Dictionary<string, List<string>> _groups   = new Dictionary<string, List<string>>();
        private readonly HashSet<string>          _selected = new HashSet<string>();
        private string?                           _activeGroup;
        private readonly List<Border>             _tabBorders = new List<Border>();

        public IReadOnlyCollection<string> SelectedItems => _selected;
        public event Action<IReadOnlyCollection<string>>? SelectionChanged;

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
        }

        public void SetGroups(Dictionary<string, List<string>> groups,
                              IEnumerable<string>? initialSelected = null)
        {
            _groups = groups;
            _selected.Clear();
            if (initialSelected != null)
                foreach (var s in initialSelected) _selected.Add(s);

            _tabStack.Children.Clear();
            _tabBorders.Clear();

            // Pinned "Selected" tab at index 0
            var selectedTab = BuildTab(SelectedGroupKey);
            _tabBorders.Add(selectedTab);
            _tabStack.Children.Add(selectedTab);

            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(6, 0, 6, 0) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            _tabStack.Children.Add(sep);

            foreach (var group in groups.Keys)
            {
                var tab = BuildTab(group);
                _tabBorders.Add(tab);
                _tabStack.Children.Add(tab);
            }

            ActivateGroup(groups.Keys.FirstOrDefault());
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
            var keys = _groups.Keys.ToList();
            for (int i = 0; i < keys.Count && (i + 1) < _tabBorders.Count; i++)
                UpdateTabCounter(_tabBorders[i + 1], keys[i]);
        }

        private void ActivateGroup(string? groupName)
        {
            if (groupName == null) return;
            _activeGroup = groupName;

            SetTabStyle(_tabBorders[0], groupName == SelectedGroupKey);
            int idx = 1;
            foreach (var key in _groups.Keys)
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

            var divider = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 0, 0, 5) };
            divider.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            _checkStack.Children.Add(divider);

            foreach (var item in allItems)
            {
                var captured = item;
                _checkStack.Children.Add(BuildCheckItem(
                    item, _selected.Contains(item), false,
                    on =>
                    {
                        if (on) _selected.Add(captured);
                        else    _selected.Remove(captured);
                        SelectionChanged?.Invoke(SelectedItems);
                        RefreshAllCounters();
                        RefreshSelectAllRow(groupName, allItems);
                    }));
            }
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

        private UIElement BuildCheckItem(string text, bool isChecked, bool indeterminate,
            Action<bool> onToggle, bool bold = false)
        {
            var cb = new CheckBox
            {
                IsChecked    = indeterminate ? (bool?)null : isChecked,
                IsThreeState = indeterminate,
                VerticalAlignment = VerticalAlignment.Center,
                Margin       = new Thickness(0, 0, 6, 0),
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
