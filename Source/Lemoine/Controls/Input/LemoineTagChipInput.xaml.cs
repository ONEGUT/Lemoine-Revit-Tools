using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// A wrapping row of removable tag chips with a '+' button that opens a
    /// searchable popup to add items. Supports free-text entry and MaxItems cap.
    ///
    /// Properties
    ///   ItemsSource   — full list of available options shown in the popup
    ///   SelectedItems — the currently active chips (two-way, raises Changed)
    ///   MaxItems      — 0 = unlimited; 1 = single-select (replaces instead of adds)
    ///   AllowFreeText — if true, typed text not in ItemsSource is accepted
    ///   Placeholder   — ghost text shown when SelectedItems is empty
    ///
    /// The popup uses the same proven pattern as LemoineSearchAutocomplete: StaysOpen=true
    /// (StaysOpen=false corrupts Revit's message loop — see CLAUDE.md), manually-built
    /// NON-focusable Border rows whose MouseLeftButtonDown adds the tag (so the search box
    /// keeps keyboard focus and the popup stays open for multi-add), and LostFocus-based
    /// dismissal (no owner-window hook, which previously swallowed the click).
    /// </summary>
    public partial class LemoineTagChipInput : UserControl
    {
        // ── Dependency Properties ─────────────────────────────────────────────

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable<string>),
                typeof(LemoineTagChipInput),
                new PropertyMetadata(null, (d, e) => ((LemoineTagChipInput)d).Rebuild()));

        public static readonly DependencyProperty SelectedItemsProperty =
            DependencyProperty.Register(nameof(SelectedItems), typeof(ObservableCollection<string>),
                typeof(LemoineTagChipInput),
                new PropertyMetadata(null, (d, e) => ((LemoineTagChipInput)d).Rebuild()));

        public static readonly DependencyProperty MaxItemsProperty =
            DependencyProperty.Register(nameof(MaxItems), typeof(int),
                typeof(LemoineTagChipInput), new PropertyMetadata(0));

        public static readonly DependencyProperty AllowFreeTextProperty =
            DependencyProperty.Register(nameof(AllowFreeText), typeof(bool),
                typeof(LemoineTagChipInput), new PropertyMetadata(false));

        public static readonly DependencyProperty PlaceholderProperty =
            DependencyProperty.Register(nameof(Placeholder), typeof(string),
                typeof(LemoineTagChipInput), new PropertyMetadata(""));

        public IEnumerable<string>            ItemsSource   { get => (IEnumerable<string>)GetValue(ItemsSourceProperty);   set => SetValue(ItemsSourceProperty,   value); }
        public ObservableCollection<string>   SelectedItems { get => (ObservableCollection<string>)GetValue(SelectedItemsProperty); set => SetValue(SelectedItemsProperty, value); }
        public int                            MaxItems      { get => (int)GetValue(MaxItemsProperty);    set => SetValue(MaxItemsProperty,    value); }
        public bool                           AllowFreeText { get => (bool)GetValue(AllowFreeTextProperty); set => SetValue(AllowFreeTextProperty, value); }
        public string                         Placeholder   { get => (string)GetValue(PlaceholderProperty);  set => SetValue(PlaceholderProperty,   value); }

        /// <summary>Raised whenever SelectedItems changes (add or remove).</summary>
        public event EventHandler? Changed;

        /// <summary>
        /// Optional parent→children grouping. When set (and the search box is empty) the popup
        /// renders a one-level tree: each parent that owns children shows a caret that expands to
        /// reveal its child rows; children are hidden from the flat top level and only appear
        /// under their parent. Parents remain individually selectable. Keys and values are the
        /// same display strings used in <see cref="ItemsSource"/>; a child not present in
        /// ItemsSource is dropped. <see langword="null"/> (the default) keeps the flat list.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>>? Hierarchy { get; set; }

        // Parents the user has expanded in the popup (display names). Reset each open.
        private readonly HashSet<string> _expandedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Cached union of every child across Hierarchy, so a top-level pass can exclude them.
        private HashSet<string>? _childSet;

        // ── Internal refs ─────────────────────────────────────────────────────
        private WrapPanel?    _chipPanel;
        private Popup?        _popup;
        private TextBox?      _searchBox;
        private StackPanel?   _rowStack;       // popup result rows (manual Borders, not a ListBox)
        private UIElement?    _addBtn;
        private ScrollViewer? _resultsScroll;  // the popup's scrollable results list
        private FrameworkElement? _popupRoot;   // popup content root, for cursor hit-testing
        private Window?       _wheelOwner;       // window we attached the wheel-redirect to (while open)

        public LemoineTagChipInput()
        {
            InitializeComponent();
            SelectedItems = new ObservableCollection<string>();
            Loaded += (s, e) => Rebuild();
        }

        // ── Build ──────────────────────────────────────────────────────────────

        private void Rebuild()
        {
            _root.Children.Clear();

            _chipPanel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0) };
            FillChips();
            _root.Children.Add(_chipPanel);

            _popup = BuildPopup();
            _root.Children.Add(_popup);
        }

        /// <summary>Refreshes only the chip row (chips + placeholder + add button) in place,
        /// without recreating the popup — so adding a chip never disturbs an open popup.</summary>
        private void FillChips()
        {
            if (_chipPanel == null) { Rebuild(); return; }
            _chipPanel.Children.Clear();

            if (SelectedItems != null)
                foreach (var item in SelectedItems)
                    _chipPanel.Children.Add(BuildChip(item));

            if (SelectedItems == null || SelectedItems.Count == 0)
            {
                var ph = new TextBlock
                {
                    Text              = Placeholder,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(2, 2, 4, 2),
                    Opacity           = 0.45,
                };
                ph.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                ph.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                ph.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                _chipPanel.Children.Add(ph);
            }

            bool atMax = MaxItems > 0 && SelectedItems != null && SelectedItems.Count >= MaxItems;
            if (!atMax)
            {
                _addBtn = BuildAddButton();
                _chipPanel.Children.Add(_addBtn);
            }
        }

        // ── Chip ──────────────────────────────────────────────────────────────

        private UIElement BuildChip(string label)
        {
            var border = new Border
            {
                CornerRadius    = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(6, 2, 4, 2),
                Margin          = new Thickness(0, 1, 4, 1),
                Cursor          = Cursors.Arrow,
            };
            border.SetResourceReference(Border.BackgroundProperty,   "LemoineRaised");
            border.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var removeBtn = new TextBlock
            {
                Text              = "×",
                Margin            = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor            = Cursors.Hand,
            };
            removeBtn.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            removeBtn.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            removeBtn.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            string captured = label;
            removeBtn.MouseLeftButtonDown += (s, e) => { e.Handled = true; RemoveItem(captured); };
            removeBtn.MouseEnter += (s, e) => removeBtn.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            removeBtn.MouseLeave += (s, e) => removeBtn.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");

            row.Children.Add(lbl);
            row.Children.Add(removeBtn);
            border.Child = row;
            return border;
        }

        // ── Add button ────────────────────────────────────────────────────────

        private UIElement BuildAddButton()
        {
            var btn = new Border
            {
                CornerRadius      = new CornerRadius(4),
                BorderThickness   = new Thickness(1),
                Padding           = new Thickness(7, 2, 7, 2),
                Margin            = new Thickness(0, 1, 0, 1),
                Cursor            = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            btn.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            btn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var lbl = new TextBlock
            {
                Text                = "+",
                TextAlignment       = TextAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible    = false,
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            btn.Child = lbl;

            btn.MouseEnter += (s, e) =>
            {
                btn.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                btn.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            };
            btn.MouseLeave += (s, e) =>
            {
                btn.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                btn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            };
            btn.MouseLeftButtonUp += (s, e) => { e.Handled = true; OpenPopup(); };
            return btn;
        }

        // ── Popup ─────────────────────────────────────────────────────────────

        private Popup BuildPopup()
        {
            var popup = new Popup
            {
                PlacementTarget    = this,
                Placement          = PlacementMode.Bottom,
                StaysOpen          = true,   // StaysOpen=false corrupts Revit's message loop (CLAUDE.md)
                AllowsTransparency = true,
                PopupAnimation     = PopupAnimation.Fade,
            };

            var outerBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                MinWidth        = 200,
                MaxWidth        = 280,
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8, Opacity = 0.18, ShadowDepth = 2, Direction = 270,
                },
            };
            outerBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var stack = new StackPanel();

            // Search box
            _searchBox = new TextBox
            {
                Margin          = new Thickness(8, 8, 8, 4),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(6, 4, 6, 4),
            };
            _searchBox.SetResourceReference(TextBox.BackgroundProperty,  "LemoineBg");
            _searchBox.SetResourceReference(TextBox.ForegroundProperty,  "LemoineText");
            _searchBox.SetResourceReference(TextBox.BorderBrushProperty, "LemoineBorder");
            _searchBox.SetResourceReference(TextBox.FontSizeProperty,    "LemoineFS_SM");
            _searchBox.SetResourceReference(TextBox.FontFamilyProperty,  "LemoineUiFont");
            _searchBox.TextChanged    += (s, e) => { if (_searchBox.IsKeyboardFocusWithin) RefreshPopupList(); };
            _searchBox.PreviewKeyDown += SearchBox_KeyDown;
            // Dismiss when the search box loses focus to something OUTSIDE the popup. Clicking a
            // result row doesn't steal focus (rows are non-focusable), so the popup stays open
            // for multi-add; clicking elsewhere closes it. Deferred so the click settles first.
            _searchBox.LostFocus += (s, e) =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_searchBox != null && !_searchBox.IsKeyboardFocusWithin) ClosePopup();
                }), System.Windows.Threading.DispatcherPriority.Background);
            stack.Children.Add(_searchBox);

            // Results — manual rows in a scroller (bubbles wheel at limits)
            var sv = new ScrollViewer
            {
                Margin                        = new Thickness(4, 0, 4, 8),
                MaxHeight                     = 200,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            _rowStack = new StackPanel();
            sv.Content = _rowStack;
            // Authoritatively mark this scroller self-contained: the wheel scrolls the list
            // directly and never leaks to the page scroller behind the popup (tree-based popup
            // detection is unreliable under Revit's WPF hosting). This handles the case where the
            // wheel IS delivered to the popup's own hwnd; the window-level redirect (see
            // OpenPopup) handles the case where Windows routes WM_MOUSEWHEEL to the main window.
            LemoineControlStyles.SetSelfContainedScroll(sv, true);
            _resultsScroll = sv;
            stack.Children.Add(sv);

            if (AllowFreeText)
            {
                var freeRow = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(8, 6, 8, 6) };
                freeRow.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                var freeHint = new TextBlock { Text = "Press Enter to add typed value" };
                freeHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                freeHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                freeHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                freeRow.Child = freeHint;
                stack.Children.Add(freeRow);
            }

            outerBorder.Child = stack;
            popup.Child       = outerBorder;
            _popupRoot        = outerBorder;
            return popup;
        }

        private void OpenPopup()
        {
            if (_popup == null || _searchBox == null) return;
            _searchBox.Text = "";
            _expandedParents.Clear();   // start collapsed each time the picker opens
            RefreshPopupList();
            _popup.IsOpen = true;
            _searchBox.Focus();

            // A Popup doesn't take Win32 activation, so depending on focus state and the OS
            // "scroll inactive windows under the cursor" setting, WM_MOUSEWHEEL can be delivered
            // to the MAIN window rather than the popup's hwnd. When that happens the popup's
            // self-contained scroller never sees the wheel and the page's (asymmetric) scrolling
            // takes over — the "scrolls down but not up" bug. Attach a window-level redirect ONLY
            // while the popup is open: if the cursor is over the popup, drive the results scroller
            // directly (symmetric by construction) and consume the event. Detached in ClosePopup.
            if (_wheelOwner != null) // already attached (re-entrant open) — avoid double-subscribe
                _wheelOwner.PreviewMouseWheel -= OnOwnerPreviewMouseWheel;
            _wheelOwner = Window.GetWindow(this);
            if (_wheelOwner != null)
                _wheelOwner.PreviewMouseWheel += OnOwnerPreviewMouseWheel;
        }

        private void ClosePopup()
        {
            if (_wheelOwner != null)
            {
                _wheelOwner.PreviewMouseWheel -= OnOwnerPreviewMouseWheel;
                _wheelOwner = null;
            }
            if (_popup != null) _popup.IsOpen = false;
        }

        // Window-level fallback wheel handler — live only while the popup is open. Redirects a
        // wheel delivered to the main window into the popup's results scroller when the cursor is
        // over the popup, so the list scrolls both directions regardless of where Windows routed
        // the WM_MOUSEWHEEL. Uses the shared redirect helper (same path as ComboBox dropdowns).
        private void OnOwnerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_popup?.IsOpen != true) return;
            LemoineControlStyles.RedirectWheelToPopupScroller(e, _popupRoot, _resultsScroll);
        }

        private void RefreshPopupList()
        {
            if (_rowStack == null || _searchBox == null) return;
            _rowStack.Children.Clear();

            var filter  = _searchBox.Text.Trim();
            var current = SelectedItems ?? new ObservableCollection<string>();

            // Tree mode: hierarchy supplied AND not currently searching. While searching we fall
            // through to the flat list over ALL items so nested children are still findable.
            if (Hierarchy != null && Hierarchy.Count > 0 && string.IsNullOrEmpty(filter))
            {
                BuildTreeRows(current);
                if (_rowStack.Children.Count == 0) AddNoMatchesRow();
                return;
            }

            var filtered = (ItemsSource?.ToList() ?? new List<string>())
                .Where(x => !current.Contains(x))
                .Where(x => string.IsNullOrEmpty(filter) ||
                            x.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            foreach (var item in filtered)
                _rowStack.Children.Add(BuildResultRow(item));

            if (filtered.Count == 0)
                AddNoMatchesRow();
        }

        private void AddNoMatchesRow()
        {
            if (_rowStack == null) return;
            var none = new TextBlock
            {
                Text   = AllowFreeText ? "No matches — press Enter to add" : "No matches",
                Margin = new Thickness(8, 6, 8, 6),
            };
            none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _rowStack.Children.Add(none);
        }

        // Union of all children across the hierarchy — top-level rows exclude these so a child
        // appears only beneath its parent's caret.
        private HashSet<string> GetChildSet()
        {
            if (_childSet != null) return _childSet;
            _childSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Hierarchy != null)
                foreach (var kids in Hierarchy.Values)
                    if (kids != null)
                        foreach (var k in kids) _childSet.Add(k);
            return _childSet;
        }

        // Renders the one-level tree: top-level rows (categories that are not a child of any
        // parent), each parent with children carrying a caret that expands indented child rows.
        private void BuildTreeRows(ICollection<string> current)
        {
            if (_rowStack == null) return;

            var childSet = GetChildSet();
            var items    = ItemsSource?.ToList() ?? new List<string>();
            var known    = new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                if (childSet.Contains(item)) continue; // shown only under its parent

                bool hasChildren = Hierarchy!.TryGetValue(item, out var kids)
                                   && kids != null && kids.Count > 0;
                bool selected = current.Contains(item);

                // Leaf top-level item that's already selected → hide (flat-list behaviour).
                // A parent with children is always shown so its children stay reachable.
                if (selected && !hasChildren) continue;

                _rowStack.Children.Add(BuildParentRow(item, hasChildren, selected));

                if (hasChildren && _expandedParents.Contains(item))
                {
                    foreach (var kid in kids!)
                    {
                        if (!known.Contains(kid)) continue;     // unknown/invalid child — drop
                        if (current.Contains(kid)) continue;    // already selected
                        _rowStack.Children.Add(BuildChildRow(kid));
                    }
                }
            }
        }

        // A single clickable, NON-focusable row (so clicking it doesn't pull keyboard focus
        // off the search box — which keeps the popup open and registers the click reliably).
        private Border BuildResultRow(string item)
        {
            var row = new Border
            {
                Padding   = new Thickness(8, 4, 8, 4),
                Cursor    = Cursors.Hand,
                Focusable = false,
                Tag       = item,
                Background = Brushes.Transparent, // full-width hit area
            };
            var tb = new TextBlock { Text = item };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            row.Child = tb;

            LemoineMotion.WireHover(row, normalBgKey: null, hoverBgKey: "LemoineAccentDim");

            string captured = item;
            row.MouseLeftButtonDown += (s, e) => { e.Handled = true; AddItem(captured); };
            return row;
        }

        // A top-level row in tree mode. When it owns children it carries a caret on the left
        // (its own hit target — toggles expansion without selecting); the label area selects the
        // category itself. An already-selected parent shows a dim ✓ and its label becomes a
        // no-op for re-add, but the caret still reaches its children.
        private Border BuildParentRow(string item, bool hasChildren, bool selected)
        {
            var row = new Border
            {
                Padding    = new Thickness(6, 4, 8, 4),
                Cursor     = Cursors.Hand,
                Focusable  = false,
                Tag        = item,
                Background = Brushes.Transparent,
            };

            var grid = new DockPanel { LastChildFill = true };

            // Caret (or a fixed-width spacer for alignment when there are no children).
            string expanded  = char.ConvertFromUtf32(0x25BE); // ▾
            string collapsed = char.ConvertFromUtf32(0x25B8); // ▸
            var caret = new TextBlock
            {
                Width               = 16,
                TextAlignment       = TextAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Text                = hasChildren ? (_expandedParents.Contains(item) ? expanded : collapsed) : "",
                Cursor              = hasChildren ? Cursors.Hand : Cursors.Arrow,
            };
            caret.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            caret.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            caret.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            caret.Background = Brushes.Transparent; // hit-testable across its whole box
            if (hasChildren)
            {
                string capCaret = item;
                caret.MouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;                       // toggle only — never selects
                    if (!_expandedParents.Remove(capCaret)) _expandedParents.Add(capCaret);
                    RefreshPopupList();
                };
            }
            DockPanel.SetDock(caret, Dock.Left);
            grid.Children.Add(caret);

            if (selected)
            {
                var check = new TextBlock
                {
                    Text              = char.ConvertFromUtf32(0x2713), // ✓
                    Margin            = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                check.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                check.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                check.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                DockPanel.SetDock(check, Dock.Right);
                grid.Children.Add(check);
            }

            var lbl = new TextBlock { Text = item, VerticalAlignment = VerticalAlignment.Center };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, selected ? "LemoineTextDim" : "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            grid.Children.Add(lbl);

            row.Child = grid;
            LemoineMotion.WireHover(row, normalBgKey: null, hoverBgKey: "LemoineAccentDim");

            // Clicking the label area selects the parent category (no-op if already selected —
            // AddItem dedups). The caret handler above sets e.Handled, so caret clicks never reach here.
            string captured = item;
            row.MouseLeftButtonDown += (s, e) => { e.Handled = true; AddItem(captured); };
            return row;
        }

        // An indented, selectable child row shown under an expanded parent.
        private Border BuildChildRow(string item)
        {
            var row = new Border
            {
                Padding    = new Thickness(30, 4, 8, 4),  // indent under the parent's caret + label
                Cursor     = Cursors.Hand,
                Focusable  = false,
                Tag        = item,
                Background = Brushes.Transparent,
            };
            var tb = new TextBlock { Text = item };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            row.Child = tb;

            LemoineMotion.WireHover(row, normalBgKey: null, hoverBgKey: "LemoineAccentDim");

            string captured = item;
            row.MouseLeftButtonDown += (s, e) => { e.Handled = true; AddItem(captured); };
            return row;
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var first = _rowStack?.Children.OfType<Border>().FirstOrDefault(b => b.Tag is string);
                if (first?.Tag is string sel)
                    AddItem(sel);
                else if (AllowFreeText && !string.IsNullOrWhiteSpace(_searchBox?.Text))
                    AddItem(_searchBox!.Text.Trim());
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ClosePopup();
                e.Handled = true;
            }
        }

        // ── Data mutations ────────────────────────────────────────────────────

        private void AddItem(string item)
        {
            if (string.IsNullOrWhiteSpace(item)) return;
            if (SelectedItems == null) SelectedItems = new ObservableCollection<string>();

            bool single = MaxItems == 1;
            if (single)
            {
                SelectedItems.Clear();
                SelectedItems.Add(item);
            }
            else if (!SelectedItems.Contains(item))
            {
                SelectedItems.Add(item);
            }

            FillChips();                       // chip appears immediately (popup untouched)
            Changed?.Invoke(this, EventArgs.Empty);

            bool atMax = MaxItems > 0 && SelectedItems.Count >= MaxItems;
            if (single || atMax)
            {
                ClosePopup();
            }
            else
            {
                // Keep the popup open for multi-add: drop the added item from the list and
                // restore focus to the search box so the user can keep picking/typing.
                _searchBox!.Text = "";
                RefreshPopupList();
                _searchBox.Focus();
            }
        }

        private void RemoveItem(string item)
        {
            if (SelectedItems == null) return;
            SelectedItems.Remove(item);
            FillChips();
            Changed?.Invoke(this, EventArgs.Empty);
            if (_popup != null && _popup.IsOpen) RefreshPopupList(); // re-show the freed item
        }
    }
}
