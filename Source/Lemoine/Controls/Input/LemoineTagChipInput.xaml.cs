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

        // ── Internal refs ─────────────────────────────────────────────────────
        private WrapPanel?   _chipPanel;
        private Popup?       _popup;
        private TextBox?     _searchBox;
        private StackPanel?  _rowStack;   // popup result rows (manual Borders, not a ListBox)
        private UIElement?   _addBtn;

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
            // detection is unreliable under Revit's WPF hosting).
            LemoineControlStyles.SetSelfContainedScroll(sv, true);
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
            return popup;
        }

        private void OpenPopup()
        {
            if (_popup == null || _searchBox == null) return;
            _searchBox.Text = "";
            RefreshPopupList();
            _popup.IsOpen = true;
            _searchBox.Focus();
        }

        private void ClosePopup()
        {
            if (_popup != null) _popup.IsOpen = false;
        }

        private void RefreshPopupList()
        {
            if (_rowStack == null || _searchBox == null) return;
            _rowStack.Children.Clear();

            var filter  = _searchBox.Text.Trim();
            var current = SelectedItems ?? new ObservableCollection<string>();
            var filtered = (ItemsSource?.ToList() ?? new List<string>())
                .Where(x => !current.Contains(x))
                .Where(x => string.IsNullOrEmpty(filter) ||
                            x.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            foreach (var item in filtered)
                _rowStack.Children.Add(BuildResultRow(item));

            if (filtered.Count == 0)
            {
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
