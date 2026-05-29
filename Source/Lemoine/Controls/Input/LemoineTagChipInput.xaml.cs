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
        private WrapPanel? _chipPanel;
        private Popup?     _popup;
        private TextBox?   _searchBox;
        private ListBox?   _listBox;
        private UIElement? _addBtn;
        private Window?    _ownerWindow;   // hooked while the popup is open for outside-click dismissal

        public LemoineTagChipInput()
        {
            InitializeComponent();
            SelectedItems = new ObservableCollection<string>();
            Loaded   += (s, e) => Rebuild();
            Unloaded += (s, e) => ClosePopup(); // unhook owner-window handlers if torn down while open
        }

        // ── Build ──────────────────────────────────────────────────────────────

        private void Rebuild()
        {
            _root.Children.Clear();

            // Outer wrap panel holds chips + add button
            _chipPanel = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0),
            };

            if (SelectedItems != null)
            {
                foreach (var item in SelectedItems)
                    _chipPanel.Children.Add(BuildChip(item));
            }

            // Show placeholder when empty
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

            // Add (+) button — hidden when MaxItems is reached
            bool atMax = MaxItems > 0 && SelectedItems != null && SelectedItems.Count >= MaxItems;
            if (!atMax)
            {
                _addBtn = BuildAddButton();
                _chipPanel.Children.Add(_addBtn);
            }

            _root.Children.Add(_chipPanel);

            // Build popup (attached to root, shown on demand)
            _popup = BuildPopup();
            _root.Children.Add(_popup);
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

            var lbl = new TextBlock
            {
                Text              = label,
                VerticalAlignment = VerticalAlignment.Center,
            };
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
            removeBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                RemoveItem(captured);
            };
            removeBtn.MouseEnter += (s, e) =>
                removeBtn.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            removeBtn.MouseLeave += (s, e) =>
                removeBtn.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");

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
            btn.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                OpenPopup();
            };
            return btn;
        }

        // ── Popup ─────────────────────────────────────────────────────────────

        private Popup BuildPopup()
        {
            var popup = new Popup
            {
                PlacementTarget = this,
                Placement       = PlacementMode.Bottom,
                // StaysOpen MUST be true: StaysOpen=false installs a ComponentDispatcher
                // message hook that corrupts Revit's message loop (see CLAUDE.md). Outside-
                // click dismissal is handled manually in OpenPopup/ClosePopup instead.
                StaysOpen       = true,
                AllowsTransparency = true,
                PopupAnimation  = PopupAnimation.Fade,
            };

            var outerBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(0),
                MinWidth        = 200,
                MaxWidth        = 280,
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius  = 8,
                    Opacity     = 0.18,
                    ShadowDepth = 2,
                    Direction   = 270,
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
            _searchBox.SetResourceReference(TextBox.BackgroundProperty,   "LemoineBg");
            _searchBox.SetResourceReference(TextBox.ForegroundProperty,   "LemoineText");
            _searchBox.SetResourceReference(TextBox.BorderBrushProperty,  "LemoineBorder");
            _searchBox.SetResourceReference(TextBox.FontSizeProperty,     "LemoineFS_SM");
            _searchBox.SetResourceReference(TextBox.FontFamilyProperty,   "LemoineUiFont");
            // Guard: only refilter on real typing — a programmatic Text reset must not
            // rebuild the list out from under the popup. The open flow refreshes explicitly.
            _searchBox.TextChanged += (s, e) => { if (_searchBox.IsKeyboardFocusWithin) RefreshPopupList(); };
            _searchBox.PreviewKeyDown += SearchBox_KeyDown;
            stack.Children.Add(_searchBox);

            // Results list
            _listBox = new ListBox
            {
                Margin              = new Thickness(4, 0, 4, 8),
                MaxHeight           = 200,
                BorderThickness     = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            _listBox.SetResourceReference(ListBox.BackgroundProperty,    "LemoineSurface");
            _listBox.SetResourceReference(ListBox.ForegroundProperty,    "LemoineText");
            _listBox.SetResourceReference(ListBox.FontSizeProperty,      "LemoineFS_SM");
            _listBox.SetResourceReference(ListBox.FontFamilyProperty,    "LemoineUiFont");
            _listBox.MouseDoubleClick += (s, e) => ConfirmSelection();
            _listBox.PreviewKeyDown   += (s, e) =>
            {
                if (e.Key == Key.Enter) { ConfirmSelection(); e.Handled = true; }
                if (e.Key == Key.Escape) { ClosePopup(); e.Handled = true; }
            };

            // Themed list items — hover + selection highlight (shared style).
            _listBox.ItemContainerStyle = LemoineControlStyles.BuildListBoxItemStyle();

            stack.Children.Add(_listBox);

            // Free-text confirm row (shown only if AllowFreeText)
            if (AllowFreeText)
            {
                var freeRow = new Border
                {
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Padding         = new Thickness(8, 6, 8, 6),
                };
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

            // Manual outside-click dismissal (StaysOpen=true). Clicks inside the popup go to
            // the popup's own window, so a PreviewMouseDown on the owner window only fires for
            // clicks elsewhere in the app — exactly the "clicked outside" case. Deactivated
            // covers clicking another top-level window.
            _ownerWindow = Window.GetWindow(this);
            if (_ownerWindow != null)
            {
                _ownerWindow.PreviewMouseDown += OnOwnerPreviewMouseDown;
                _ownerWindow.Deactivated      += OnOwnerDeactivated;
            }
        }

        private void OnOwnerPreviewMouseDown(object sender, MouseButtonEventArgs e) => ClosePopup();
        private void OnOwnerDeactivated(object? sender, EventArgs e)                 => ClosePopup();

        private void ClosePopup()
        {
            if (_ownerWindow != null)
            {
                _ownerWindow.PreviewMouseDown -= OnOwnerPreviewMouseDown;
                _ownerWindow.Deactivated      -= OnOwnerDeactivated;
                _ownerWindow = null;
            }
            if (_popup != null) _popup.IsOpen = false;
        }

        private void RefreshPopupList()
        {
            if (_listBox == null || _searchBox == null) return;
            var filter  = _searchBox.Text.Trim();
            var current = SelectedItems ?? new ObservableCollection<string>();
            var source  = ItemsSource?.ToList() ?? new List<string>();

            var filtered = source
                .Where(x => !current.Contains(x))
                .Where(x => string.IsNullOrEmpty(filter) ||
                            x.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            _listBox.ItemsSource = filtered;
            if (filtered.Count > 0) _listBox.SelectedIndex = 0;
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && _listBox != null)
            {
                _listBox.Focus();
                if (_listBox.Items.Count > 0) _listBox.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (_listBox?.SelectedItem is string sel)
                {
                    AddItem(sel);
                }
                else if (AllowFreeText && !string.IsNullOrWhiteSpace(_searchBox?.Text))
                {
                    AddItem(_searchBox!.Text.Trim());
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ClosePopup();
                e.Handled = true;
            }
        }

        private void ConfirmSelection()
        {
            if (_listBox?.SelectedItem is string sel)
                AddItem(sel);
        }

        // ── Data mutations ────────────────────────────────────────────────────

        private void AddItem(string item)
        {
            if (string.IsNullOrWhiteSpace(item)) return;
            if (SelectedItems == null) SelectedItems = new ObservableCollection<string>();

            if (MaxItems == 1)
            {
                // Single-select: replace the current value
                SelectedItems.Clear();
                SelectedItems.Add(item);
            }
            else
            {
                if (!SelectedItems.Contains(item))
                    SelectedItems.Add(item);
            }

            ClosePopup();
            Changed?.Invoke(this, EventArgs.Empty);
            Rebuild();
        }

        private void RemoveItem(string item)
        {
            if (SelectedItems == null) return;
            SelectedItems.Remove(item);
            Changed?.Invoke(this, EventArgs.Empty);
            Rebuild();
        }
    }
}
