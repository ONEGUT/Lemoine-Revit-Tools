using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LemoineTools.Lemoine;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// View/sheet picker that mirrors the source document's Project Browser
    /// organization exactly — folder titles, nesting, ordering, and dependent
    /// views nested under their primary. Feed it the snapshot captured by
    /// <c>BrowserTreeCapture.Capture(doc)</c> on the Revit main thread via
    /// <see cref="SetTree"/>; roots with no eligible leaves are hidden, so a
    /// views-only tool simply never shows the Sheets root.
    /// Fires <see cref="SelectionChanged"/> once at the end of SetTree (same
    /// contract as <c>LemoineMultiSelectTabs.SetGroups</c>) — subscribe first.
    /// </summary>
    public partial class LemoineBrowserTreePicker : UserControl
    {
        // Wraps a pruned LemoineBrowserNode with render/selection bookkeeping.
        private sealed class Node
        {
            public LemoineBrowserNode Src = null!;
            public List<Node> Children = new List<Node>();
            public List<long> LeafIds  = new List<long>(); // eligible ids at/below this node
            public int  Depth;
            public bool Eligible;                          // this node itself is a pickable leaf
            public bool IsRoot => Depth == 0;
        }

        private readonly List<Node>    _roots    = new List<Node>();
        private readonly HashSet<long> _selected = new HashSet<long>();
        private readonly HashSet<Node> _expanded = new HashSet<Node>();
        private readonly Dictionary<long, bool> _isSheetById = new Dictionary<long, bool>();
        private string _filter = "";

        public IReadOnlyCollection<long> SelectedIds => _selected;
        public event Action<IReadOnlyCollection<long>>? SelectionChanged;

        /// <summary>
        /// When true, only one item can be selected at a time: checking a leaf clears any
        /// prior selection and folder checkboxes are hidden. Set before <see cref="SetTree"/>.
        /// </summary>
        public bool SingleSelect { get; set; } = false;

        /// <summary>Accessible name announced by screen readers when the control receives focus.</summary>
        public string AccessibleName { set => AutomationProperties.SetName(this, value ?? string.Empty); }

        public LemoineBrowserTreePicker()
        {
            InitializeComponent();

            _outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _outer.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            _footer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _footer.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");

            _filterHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _filterHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _filterHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _filterHint.Text = LemoineStrings.T("controls.pickers.browserTreePicker.filterHint");

            _filterBox.TextChanged += (s, e) =>
            {
                _filter = (_filterBox.Text ?? "").Trim();
                _filterHint.Visibility = _filter.Length == 0 && _filterBox.Text!.Length == 0
                    ? Visibility.Visible : Visibility.Collapsed;
                RebuildRows();
            };

            var expandBtn = LemoineControlStyles.BuildSmallButton(LemoineStrings.T("controls.pickers.browserTreePicker.expandAll"));
            expandBtn.Margin = new Thickness(0, 0, 4, 0);
            expandBtn.Click += (s, e) => { ExpandAll(); RebuildRows(); };
            _filterBtns.Children.Add(expandBtn);

            var collapseBtn = LemoineControlStyles.BuildSmallButton(LemoineStrings.T("controls.pickers.browserTreePicker.collapse"));
            collapseBtn.Click += (s, e) => { CollapseAll(); RebuildRows(); };
            _filterBtns.Children.Add(collapseBtn);

            LemoineControlStyles.WireBubblingScroll(_treeScroll);
            UpdateFooter();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Loads the captured browser tree, pruned to <paramref name="eligibleIds"/>
        /// (null = every captured leaf is pickable). Roots left without eligible
        /// leaves are hidden entirely.
        /// </summary>
        public void SetTree(LemoineBrowserTree? tree,
                            IEnumerable<long>? eligibleIds = null,
                            IEnumerable<long>? initialSelected = null)
        {
            _roots.Clear();
            _selected.Clear();
            _expanded.Clear();
            _isSheetById.Clear();

            var eligible = eligibleIds != null ? new HashSet<long>(eligibleIds) : null;
            if (tree != null)
            {
                foreach (var src in tree.Roots)
                {
                    var root = BuildNode(src, 0, eligible);
                    if (root != null)
                    {
                        _roots.Add(root);
                        _expanded.Add(root); // roots start expanded, folders collapsed
                    }
                }
            }

            if (initialSelected != null)
                foreach (var id in initialSelected)
                    if (_isSheetById.ContainsKey(id))
                        _selected.Add(id);

            _filterBox.Text = "";
            _filter = "";
            RebuildRows();
            UpdateFooter();
            // Notify subscribers of the post-setup selection state so ViewModels that
            // mirror selection into their own fields are always in sync after SetTree.
            SelectionChanged?.Invoke(SelectedIds);
        }

        // ── Tree building (prune to eligible leaves) ──────────────────────────

        private Node? BuildNode(LemoineBrowserNode src, int depth, HashSet<long>? eligible)
        {
            var node = new Node { Src = src, Depth = depth };
            if (src.IsLeaf && (eligible == null || eligible.Contains(src.Id!.Value)))
            {
                node.Eligible = true;
                node.LeafIds.Add(src.Id!.Value);
                _isSheetById[src.Id!.Value] = src.IsSheet;
            }
            foreach (var childSrc in src.Children)
            {
                var child = BuildNode(childSrc, depth + 1, eligible);
                if (child == null) continue;
                node.Children.Add(child);
                node.LeafIds.AddRange(child.LeafIds);
            }
            return node.LeafIds.Count > 0 ? node : null;
        }

        private void ExpandAll()
        {
            void Walk(Node n)
            {
                if (n.Children.Count > 0) _expanded.Add(n);
                foreach (var c in n.Children) Walk(c);
            }
            foreach (var r in _roots) Walk(r);
        }

        private void CollapseAll()
        {
            _expanded.Clear();
            foreach (var r in _roots) _expanded.Add(r); // roots always stay open
        }

        // ── Row rendering ─────────────────────────────────────────────────────

        private void RebuildRows()
        {
            _treeStack.Children.Clear();

            bool filtering = _filter.Length > 0;
            Dictionary<Node, bool>? matches = filtering ? new Dictionary<Node, bool>() : null;
            if (filtering)
                foreach (var r in _roots) ComputeMatches(r, matches!);

            foreach (var r in _roots)
                AddRows(r, filtering, matches, ancestorMatched: false);

            if (_treeStack.Children.Count == 0)
            {
                var none = new TextBlock
                {
                    Text         = filtering ? LemoineStrings.T("controls.pickers.browserTreePicker.noMatches") : LemoineStrings.T("controls.pickers.browserTreePicker.nothingToPick"),
                    FontStyle    = FontStyles.Italic,
                    Margin       = new Thickness(6, 6, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                };
                none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                _treeStack.Children.Add(none);
            }
        }

        // Post-order pass: does this node's own title match the filter (memoized subtree flag).
        private bool ComputeMatches(Node n, Dictionary<Node, bool> map)
        {
            bool any = TitleMatches(n);
            foreach (var c in n.Children)
                any |= ComputeMatches(c, map);
            map[n] = any;
            return any;
        }

        private bool TitleMatches(Node n) =>
            n.Src.Title.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;

        private void AddRows(Node n, bool filtering, Dictionary<Node, bool>? matches, bool ancestorMatched)
        {
            if (filtering && !ancestorMatched && !(matches != null && matches.TryGetValue(n, out var hit) && hit))
                return; // neither this subtree nor an ancestor matches

            _treeStack.Children.Add(BuildRow(n, filtering));

            // While filtering, every surviving branch is force-expanded so matches are visible.
            bool expanded = filtering || _expanded.Contains(n);
            if (n.Children.Count == 0 || !expanded) return;

            bool selfMatched = ancestorMatched || (filtering && TitleMatches(n));
            foreach (var c in n.Children)
                AddRows(c, filtering, matches, selfMatched);
        }

        private UIElement BuildRow(Node n, bool filtering)
        {
            string expandedGlyph  = char.ConvertFromUtf32(0x25BE); // ▾
            string collapsedGlyph = char.ConvertFromUtf32(0x25B8); // ▸

            bool hasChildren = n.Children.Count > 0;
            bool expanded    = filtering || _expanded.Contains(n);
            bool selected    = n.Eligible && _selected.Contains(n.Src.Id!.Value);

            var caret = new TextBlock
            {
                Width             = 16,
                TextAlignment     = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Text              = hasChildren ? (expanded ? expandedGlyph : collapsedGlyph) : "",
                Cursor            = hasChildren ? Cursors.Hand : Cursors.Arrow,
            };
            caret.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            caret.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            caret.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            caret.Background = Brushes.Transparent; // hit-testable across full 16px box
            if (hasChildren)
                caret.MouseLeftButtonDown += (s, e) => { e.Handled = true; ToggleExpand(n); };

            // Checkbox: leaves get a binary box; folders a tri-state summary box.
            // In single-select mode folders carry no checkbox at all.
            CheckBox? cb = null;
            FrameworkElement checkSlot;
            if (n.Eligible)
            {
                cb = new CheckBox
                {
                    IsChecked         = selected,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(0, 0, 6, 0),
                };
                cb.Checked   += (s, e) => SetLeaf(n, true);
                cb.Unchecked += (s, e) => SetLeaf(n, false);
                checkSlot = cb;
            }
            else if (!SingleSelect && n.LeafIds.Count > 0)
            {
                bool all  = n.LeafIds.All(id => _selected.Contains(id));
                bool some = !all && n.LeafIds.Any(id => _selected.Contains(id));
                cb = new CheckBox
                {
                    IsChecked         = some ? (bool?)null : all,
                    IsThreeState      = some,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(0, 0, 6, 0),
                };
                cb.Checked   += (s, e) => SetBranch(n, true);
                cb.Unchecked += (s, e) => SetBranch(n, false);
                checkSlot = cb;
            }
            else
            {
                checkSlot = new Border { Width = 19 }; // keep label column aligned
            }

            var label = new TextBlock
            {
                Text              = n.Src.Title,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                FontWeight        = n.IsRoot ? FontWeights.SemiBold
                                  : !n.Eligible ? FontWeights.Medium
                                  : FontWeights.Normal,
            };
            label.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            label.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            label.SetResourceReference(TextBlock.ForegroundProperty,
                n.Eligible && !selected ? "LemoineTextSub" : "LemoineText");

            var dock = new DockPanel { LastChildFill = true };

            // Collapsed folders show how many pickable items they hide.
            if (hasChildren && !expanded && n.LeafIds.Count > 0)
            {
                var count = new TextBlock
                {
                    Text              = $"({n.LeafIds.Count})",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(6, 0, 2, 0),
                };
                count.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                count.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
                count.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                DockPanel.SetDock(count, Dock.Right);
                dock.Children.Add(count);
            }

            dock.Children.Add(caret);
            dock.Children.Add(checkSlot);
            dock.Children.Add(label);

            var row = new Border
            {
                Child   = dock,
                Padding = new Thickness(4, 3, 6, 3),
                Margin  = new Thickness(n.Depth * 18, 0, 0, 1),
                Cursor  = Cursors.Hand,
            };
            row.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_MD");
            if (selected)
            {
                row.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
            }
            else
            {
                // Transparent (never null) so the whole row is hit-testable for click/hover.
                row.Background = Brushes.Transparent;
                LemoineMotion.WireHover(row, normalBgKey: null, hoverBgKey: "LemoineAccentDim");
            }

            row.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is CheckBox) return;
                if (e.OriginalSource is TextBlock t && t == caret) return;
                e.Handled = true;
                if (n.Eligible && cb != null) cb.IsChecked = !(cb.IsChecked == true);
                else if (hasChildren)        ToggleExpand(n);
            };

            // Right-click selects all dependents (descendant leaves) under this node,
            // excluding the node itself. Only offered when there is something to select
            // and we're in multi-select mode.
            int dependentCount = n.LeafIds.Count - (n.Eligible ? 1 : 0);
            if (!SingleSelect && dependentCount > 0)
            {
                row.ToolTip = LemoineStrings.T("controls.pickers.browserTreePicker.dependentsTooltip");
                row.MouseRightButtonDown += (s, e) =>
                {
                    e.Handled = true;
                    SelectDependents(n);
                };
            }
            return row;
        }

        private void ToggleExpand(Node n)
        {
            if (!_expanded.Remove(n)) _expanded.Add(n);
            RebuildRows();
        }

        // ── Selection plumbing ────────────────────────────────────────────────

        private void SetLeaf(Node n, bool on)
        {
            long id = n.Src.Id!.Value;
            if (on)
            {
                if (SingleSelect) _selected.Clear();
                _selected.Add(id);
            }
            else _selected.Remove(id);
            AfterSelectionChange();
        }

        private void SetBranch(Node n, bool on)
        {
            foreach (var id in n.LeafIds)
            {
                if (on) _selected.Add(id);
                else    _selected.Remove(id);
            }
            AfterSelectionChange();
        }

        // Right-click gesture: select every dependent (descendant leaf) under a node
        // while leaving the node itself untouched — a quick way to grab a parent
        // view's dependents without expanding and checking each one. Additive to the
        // current selection; no-op in single-select mode or when nothing changes.
        private void SelectDependents(Node n)
        {
            if (SingleSelect) return;
            bool any = false;
            foreach (var id in n.LeafIds)
            {
                if (n.Eligible && id == n.Src.Id!.Value) continue; // skip the node itself
                if (_selected.Add(id)) any = true;
            }
            if (any) AfterSelectionChange();
        }

        private void AfterSelectionChange()
        {
            RebuildRows();
            UpdateFooter();
            SelectionChanged?.Invoke(SelectedIds);
        }

        // ── Footer ────────────────────────────────────────────────────────────

        private void UpdateFooter()
        {
            _footerPanel.Children.Clear();

            int views  = _selected.Count(id => _isSheetById.TryGetValue(id, out var sheet) && !sheet);
            int sheets = _selected.Count - views;

            if (_selected.Count > 0)
            {
                var pill = new Border { Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
                pill.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
                pill.SetResourceReference(Border.BackgroundProperty,   "LemoineAccentDim");
                pill.SetResourceReference(Border.BorderBrushProperty,  "LemoineAccent");
                pill.BorderThickness = new Thickness(1);

                var pillText = new TextBlock { Text = LemoineStrings.T("controls.pickers.browserTreePicker.selectedPill", _selected.Count), FontWeight = FontWeights.Medium };
                pillText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                pillText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                pillText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                pill.Child = pillText;
                DockPanel.SetDock(pill, Dock.Left);
                _footerPanel.Children.Add(pill);

                string viewWord  = LemoineStrings.T("controls.pickers.browserTreePicker.viewWord");
                string sheetWord = LemoineStrings.T("controls.pickers.browserTreePicker.sheetWord");
                string breakdown =
                    views > 0 && sheets > 0 ? $"{views} {viewWord}{(views == 1 ? "" : "s")}  ·  {sheets} {sheetWord}{(sheets == 1 ? "" : "s")}"
                    : views > 0             ? $"{views} {viewWord}{(views == 1 ? "" : "s")}"
                                            : $"{sheets} {sheetWord}{(sheets == 1 ? "" : "s")}";
                var detail = new TextBlock
                {
                    Text              = breakdown,
                    Margin            = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                detail.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                detail.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
                detail.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                DockPanel.SetDock(detail, Dock.Left);
                _footerPanel.Children.Add(detail);

                var clear = new TextBlock
                {
                    Text              = LemoineStrings.T("controls.pickers.browserTreePicker.clear"),
                    Cursor            = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                clear.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                clear.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                clear.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                clear.Background = Brushes.Transparent;
                clear.MouseEnter += (s, e) => clear.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                clear.MouseLeave += (s, e) => clear.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                clear.MouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;
                    _selected.Clear();
                    AfterSelectionChange();
                };
                DockPanel.SetDock(clear, Dock.Right);
                _footerPanel.Children.Add(clear);
            }
            else
            {
                var none = new TextBlock
                {
                    Text              = LemoineStrings.T("controls.pickers.browserTreePicker.noneSelected"),
                    FontStyle         = FontStyles.Italic,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                DockPanel.SetDock(none, Dock.Left);
                _footerPanel.Children.Add(none);
            }
        }
    }
}
