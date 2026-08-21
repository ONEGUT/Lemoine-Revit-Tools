using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LemoineTools.Framework.Controls
{
    // =========================================================================
    // MultiSelectDropdown — a row-height picker that opens a checkbox list.
    //
    // Fills the gap between the two existing multi-pickers: MultiSelectTabs is a
    // full-height panel (too big to sit on a repeated row) and TagChipInput grows
    // taller with every pick (a row with six selections pushes the list around).
    // This one keeps a FIXED row height no matter how many items are selected —
    // the closed state shows a count badge plus the names, ellipsized — so a
    // column of these stays scannable.
    //
    // Closed:   [ 3 ] ARCH-L02, STR-L02, MEP-L02, +1        ▼
    // Open:     search · "All" row · one checkable row per item (+ optional
    //           right-aligned secondary text, e.g. a source filename)
    //
    // Popup safety follows the proven TagChipInput/SearchAutocomplete pattern:
    // StaysOpen = true (StaysOpen = false corrupts Revit's message loop — see
    // CLAUDE.md), dismissal via the search box losing focus outside the popup,
    // and a self-contained scroller plus a window-level wheel redirect so the
    // list scrolls both directions under Revit's WPF hosting.
    //
    // Host-agnostic: no Revit or Navisworks types, so it compiles into both
    // assemblies from the shared Source\Framework tree.
    // =========================================================================
    public sealed class MultiSelectDropdown : UserControl
    {
        // ── Public surface ────────────────────────────────────────────────────

        /// <summary>Every selectable item, in display order.</summary>
        public IReadOnlyList<string> ItemsSource
        {
            get => _items;
            set { _items = value ?? Array.Empty<string>(); RenderClosed(); }
        }

        /// <summary>Optional dim text shown right-aligned on a row (e.g. the source filename).</summary>
        public IReadOnlyDictionary<string, string>? SecondaryText { get; set; }

        /// <summary>Items currently ticked. Assigning replaces the set; mutate + call
        /// <see cref="Refresh"/> to change it in place.</summary>
        public ObservableCollection<string> SelectedItems
        {
            get => _selected;
            set { _selected = value ?? new ObservableCollection<string>(); RenderClosed(); }
        }

        /// <summary>Shown, dimmed and italic, when nothing is selected.</summary>
        public string Placeholder { get; set; } = "";

        /// <summary>Optional caption above the control. Empty hides the row entirely.</summary>
        public string Label
        {
            get => _label.Text;
            set
            {
                _label.Text = value ?? "";
                _label.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        /// <summary>Accessible name announced by screen readers.</summary>
        public string AccessibleName { set => AutomationProperties.SetName(this, value ?? string.Empty); }

        /// <summary>Raised whenever the selection changes — including via the "All" row.</summary>
        public event Action<IReadOnlyList<string>>? SelectionChanged;

        /// <summary>Re-reads <see cref="SelectedItems"/> and repaints the closed row.</summary>
        public void Refresh() => RenderClosed();

        // ── State ─────────────────────────────────────────────────────────────

        private IReadOnlyList<string>        _items    = Array.Empty<string>();
        private ObservableCollection<string> _selected = new ObservableCollection<string>();

        private readonly TextBlock   _label   = new TextBlock();
        private readonly Border      _badge   = new Border();
        private readonly TextBlock   _badgeTx = new TextBlock();
        private readonly TextBlock   _summary = new TextBlock();
        private readonly Border      _field   = new Border();
        private readonly TextBlock   _caret   = new TextBlock();

        private Popup?          _popup;
        private Border?         _popupRoot;
        private TextBox?        _searchBox;
        private StackPanel?     _rowStack;
        private ScrollViewer?   _rowScroll;
        private Window?         _wheelOwner;
        private bool            _suppress;   // guards re-entrant row rebuilds

        public MultiSelectDropdown()
        {
            var outer = new StackPanel();

            _label.Visibility = Visibility.Collapsed;
            _label.Margin     = new Thickness(0, 0, 0, 4);
            _label.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _label.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _label.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(_label);

            // ── Closed field: [badge] summary … caret ──────────────────────
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // badge
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });// summary
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // caret

            _badge.CornerRadius  = new CornerRadius(8);
            _badge.Padding       = new Thickness(6, 1, 6, 1);
            _badge.Margin        = new Thickness(0, 0, 6, 0);
            _badge.VerticalAlignment = VerticalAlignment.Center;
            _badge.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
            _badgeTx.FontWeight = FontWeights.SemiBold;
            _badgeTx.SetResourceReference(TextBlock.ForegroundProperty, "LemoineBg");
            _badgeTx.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _badgeTx.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _badge.Child = _badgeTx;
            Grid.SetColumn(_badge, 0);
            grid.Children.Add(_badge);

            _summary.TextTrimming      = TextTrimming.CharacterEllipsis;
            _summary.VerticalAlignment = VerticalAlignment.Center;
            _summary.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _summary.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            Grid.SetColumn(_summary, 1);
            grid.Children.Add(_summary);

            _caret.Text              = "▼";
            _caret.Margin            = new Thickness(6, 0, 0, 0);
            _caret.VerticalAlignment = VerticalAlignment.Center;
            _caret.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _caret.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _caret.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Grid.SetColumn(_caret, 2);
            grid.Children.Add(_caret);

            _field.BorderThickness = new Thickness(1);
            _field.Child           = grid;
            _field.SetResourceReference(Border.BackgroundProperty,   "LemoineSelectBg");
            _field.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorderMid");
            _field.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_SM");
            _field.SetResourceReference(Border.PaddingProperty,      "LemoineTh_InputPad");
            _field.SetResourceReference(Border.MinHeightProperty,    "LemoineH_Input");
            _field.Cursor = Cursors.Hand;
            // Opens only — never toggles closed. A click on _field while the popup is already
            // open (e.g. an imprecise click meant for the search box just below it) must not
            // silently close it out from under the user; dismissal is LostFocus-driven only.
            _field.MouseLeftButtonUp += (s, e) => { e.Handled = true; OpenPopup(); };
            outer.Children.Add(_field);

            // Parented deliberately: a Popup built loose sits outside the logical tree, so
            // SetResourceReference in its subtree never reaches the Window's injected
            // ResourceDictionary and every themed brush falls back silently. As a panel child it
            // takes no layout space but does inherit the resource scope.
            _popup = BuildPopup();
            outer.Children.Add(_popup);

            Content = outer;
            RenderClosed();
        }

        // ── Closed-state rendering ────────────────────────────────────────────

        private void RenderClosed()
        {
            int n = _selected.Count;
            _badge.Visibility  = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            _badgeTx.Text      = n.ToString();

            if (n == 0)
            {
                _summary.Text      = Placeholder;
                _summary.FontStyle = FontStyles.Italic;
                _summary.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                return;
            }

            _summary.FontStyle = FontStyles.Normal;
            _summary.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");

            // Name the first few and count the rest, so the row height never changes and a long
            // selection still tells you WHICH models are on it rather than only how many.
            const int shown = 3;
            var head = _selected.Take(shown).ToList();
            _summary.Text = n > shown
                ? string.Join(", ", head) + AppStrings.T("controls.pickers.multiSelectDropdown.more", n - shown)
                : string.Join(", ", head);
        }

        // ── Popup ─────────────────────────────────────────────────────────────

        private Popup BuildPopup()
        {
            var popup = new Popup
            {
                PlacementTarget    = _field,
                Placement          = PlacementMode.Bottom,
                StaysOpen          = true,   // StaysOpen=false corrupts Revit's message loop (CLAUDE.md)
                AllowsTransparency = true,
                PopupAnimation     = PopupAnimation.Fade,
            };

            var outerBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                MinWidth        = 220,
                Padding         = new Thickness(6),
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8, Opacity = 0.18, ShadowDepth = 2, Direction = 270,
                },
            };
            outerBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");

            var stack = new StackPanel();

            _searchBox = new TextBox
            {
                Margin          = new Thickness(0, 0, 0, 5),
                BorderThickness = new Thickness(1),
            };
            _searchBox.SetResourceReference(TextBox.BackgroundProperty,  "LemoineBg");
            _searchBox.SetResourceReference(TextBox.ForegroundProperty,  "LemoineText");
            _searchBox.SetResourceReference(TextBox.BorderBrushProperty, "LemoineBorderMid");
            _searchBox.SetResourceReference(TextBox.FontFamilyProperty,  "LemoineUiFont");
            _searchBox.SetResourceReference(TextBox.FontSizeProperty,    "LemoineFS_SM");
            _searchBox.SetResourceReference(TextBox.PaddingProperty,     "LemoineTh_InputPad");
            // Guarded exactly as the house ComboBox rule requires: without the focus check a
            // programmatic Text reset during a rebuild re-enters the filter.
            _searchBox.TextChanged += (s, e) => { if (_searchBox!.IsKeyboardFocusWithin) RefreshRows(); };
            // Dismiss when focus leaves for something OUTSIDE the popup. Rows are non-focusable,
            // so ticking one keeps the list open for multi-select; clicking away closes it.
            // Deferred so the click that moved focus settles first.
            _searchBox.LostFocus += (s, e) =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_searchBox != null && !_searchBox.IsKeyboardFocusWithin) ClosePopup();
                }), System.Windows.Threading.DispatcherPriority.Background);
            stack.Children.Add(_searchBox);

            var sv = new ScrollViewer
            {
                MaxHeight                     = 260,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            _rowStack  = new StackPanel();
            sv.Content = _rowStack;
            // Authoritative popup-scroller tag: the wheel drives this list and never leaks to the
            // page behind it (visual-tree popup detection is unreliable under Revit's hosting).
            ControlStyles.SetSelfContainedScroll(sv, true);
            _rowScroll = sv;
            stack.Children.Add(sv);

            outerBorder.Child = stack;
            popup.Child       = outerBorder;
            _popupRoot        = outerBorder;
            return popup;
        }

        private void OpenPopup()
        {
            if (_popup == null || _searchBox == null) return;
            if (_popup.IsOpen) { RefocusSearchBox(); return; }   // already open — don't wipe the query

            _suppress = true;
            _searchBox.Text = "";
            _suppress = false;
            RefreshRows();

            _popup.IsOpen = true;
            _caret.Text   = "▲";
            _field.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            RefocusSearchBox();

            // A Popup takes no Win32 activation, so WM_MOUSEWHEEL can be delivered to the MAIN
            // window instead of the popup's hwnd — the "scrolls down but not up" bug. Redirect at
            // the window level ONLY while open; detached in ClosePopup.
            if (_wheelOwner != null) _wheelOwner.PreviewMouseWheel -= OnOwnerPreviewMouseWheel;
            _wheelOwner = Window.GetWindow(this);
            if (_wheelOwner != null) _wheelOwner.PreviewMouseWheel += OnOwnerPreviewMouseWheel;
        }

        private void RefocusSearchBox()
        {
            var box = _searchBox;
            if (box == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (box.IsVisible) { box.Focus(); Keyboard.Focus(box); }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ClosePopup()
        {
            if (_wheelOwner != null)
            {
                _wheelOwner.PreviewMouseWheel -= OnOwnerPreviewMouseWheel;
                _wheelOwner = null;
            }
            if (_popup != null) _popup.IsOpen = false;
            _caret.Text = "▼";
            _field.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
        }

        private void OnOwnerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_popup?.IsOpen != true) return;
            ControlStyles.RedirectWheelToPopupScroller(e, _popupRoot, _rowScroll);
        }

        // ── Row list ──────────────────────────────────────────────────────────

        private void RefreshRows()
        {
            if (_rowStack == null || _searchBox == null || _suppress) return;
            _rowStack.Children.Clear();

            string q = (_searchBox.Text ?? "").Trim();
            var visible = string.IsNullOrEmpty(q)
                ? _items.ToList()
                : _items.Where(i => i.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (visible.Count == 0)
            {
                // Never a silently empty list — say why there is nothing to tick.
                var empty = new TextBlock
                {
                    Text         = _items.Count == 0
                                 ? AppStrings.T("controls.pickers.multiSelectDropdown.noItems")
                                 : AppStrings.T("controls.pickers.multiSelectDropdown.noMatches", q),
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(5, 6, 5, 6),
                    FontStyle    = FontStyles.Italic,
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                empty.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                empty.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                _rowStack.Children.Add(empty);
                return;
            }

            // "All" is scoped to what is VISIBLE, so it never silently ticks items the search has
            // filtered out — same contract as MultiSelectTabs' per-group All row.
            int selectedVisible = visible.Count(v => _selected.Contains(v));
            _rowStack.Children.Add(BuildAllRow(visible, selectedVisible));

            foreach (var item in visible)
                _rowStack.Children.Add(BuildRow(item, _selected.Contains(item)));
        }

        private FrameworkElement BuildAllRow(List<string> visible, int selectedVisible)
        {
            bool allOn = selectedVisible == visible.Count;
            var row = MakeRowShell(out Border box, out TextBlock check, out TextBlock text, out TextBlock tail);

            row.Margin          = new Thickness(0, 0, 0, 3);
            row.BorderThickness = new Thickness(0, 0, 0, 1);
            row.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            text.Text = string.IsNullOrEmpty((_searchBox?.Text ?? "").Trim())
                ? AppStrings.T("controls.pickers.multiSelectDropdown.all")
                : AppStrings.T("controls.pickers.multiSelectDropdown.allMatches", visible.Count);
            text.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tail.Text = AppStrings.T("controls.pickers.multiSelectDropdown.count", selectedVisible, visible.Count);
            tail.Visibility = Visibility.Visible;

            PaintCheck(box, check, allOn);

            row.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (allOn) foreach (var v in visible) _selected.Remove(v);
                else       foreach (var v in visible) if (!_selected.Contains(v)) _selected.Add(v);
                Commit();
            };
            return row;
        }

        private FrameworkElement BuildRow(string item, bool on)
        {
            var row = MakeRowShell(out Border box, out TextBlock check, out TextBlock text, out TextBlock tail);

            text.Text = item;
            text.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");

            if (SecondaryText != null && SecondaryText.TryGetValue(item, out var sub) && !string.IsNullOrWhiteSpace(sub))
            {
                tail.Text       = sub;
                tail.Visibility = Visibility.Visible;
            }

            PaintCheck(box, check, on);
            if (on) row.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");

            row.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (_selected.Contains(item)) _selected.Remove(item);
                else                          _selected.Add(item);
                Commit();
            };
            return row;
        }

        // A 3-column row: [check] name (ellipsizing) [secondary]. The star column is safe because
        // the scroller disables horizontal scrolling, so rows measure against a finite width.
        private Border MakeRowShell(out Border box, out TextBlock check, out TextBlock text, out TextBlock tail)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            check = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                FontWeight          = FontWeights.Bold,
            };
            check.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            check.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            box = new Border
            {
                Width           = 13,
                Height          = 13,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(2),
                Margin          = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child           = check,
            };
            Grid.SetColumn(box, 0);
            grid.Children.Add(box);

            text = new TextBlock
            {
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            text.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            text.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            tail = new TextBlock
            {
                Margin            = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility        = Visibility.Collapsed,
            };
            tail.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tail.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tail.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Grid.SetColumn(tail, 2);
            grid.Children.Add(tail);

            var row = new Border
            {
                Padding      = new Thickness(5, 4, 5, 4),
                CornerRadius = new CornerRadius(3),
                Child        = grid,
                Cursor       = Cursors.Hand,
                Focusable    = false,   // ticking must not steal focus from the search box
            };
            // Direct assignment, never SetResourceReference: "Transparent" is not a resource key
            // and a null Background makes the row hit-testable only on its glyphs.
            row.Background = Brushes.Transparent;
            return row;
        }

        private static void PaintCheck(Border box, TextBlock check, bool on)
        {
            if (on)
            {
                check.Text = "✓";
                box.SetResourceReference(Border.BackgroundProperty,  "LemoineAccent");
                box.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                check.SetResourceReference(TextBlock.ForegroundProperty, "LemoineBg");
            }
            else
            {
                check.Text = "";
                box.Background = Brushes.Transparent;
                box.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            }
        }

        private void Commit()
        {
            RenderClosed();
            RefreshRows();
            try { SelectionChanged?.Invoke(_selected.ToList()); }
            catch (Exception ex) { DiagnosticsLog.Error("MultiSelectDropdown.SelectionChanged", ex); }
        }
    }
}
