using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LemoineTools.Framework.Controls
{
    /// <summary>
    /// Search input with live-filtered dropdown list.
    /// Mirrors JS SearchAutocomplete exactly.
    ///
    /// API:
    ///   Items          — full list of searchable strings
    ///   Placeholder    — watermark text
    ///   MaxSuggestions — cap on visible results (default 8)
    ///   Value          — currently selected string
    ///   event SelectionChanged(string)
    /// </summary>
    public partial class SearchAutocomplete : UserControl
    {
        private IList<string> _items           = new List<string>();
        private int           _maxSuggestions  = 8;
        private string        _value           = "";

        // Icon show/hide state
        private bool _iconVisible = true;
        private static readonly Thickness _padWithIcon = new Thickness(26, 6, 10, 6);
        private static readonly Thickness _padNoIcon   = new Thickness(8,  6, 10, 6);
        // Frozen so this shared static is thread-safe across each window's STA thread
        // (a non-frozen Freezable crashes the second window with a cross-thread access).
        private static readonly CubicEase _iconEase    = FreezeEase(new CubicEase { EasingMode = EasingMode.EaseOut });

        private static CubicEase FreezeEase(CubicEase e) { e.Freeze(); return e; }

        public string Value
        {
            get => _value;
            set { _value = value ?? ""; _textBox.Text = _value; }
        }

        public IList<string> Items
        {
            get => _items;
            set { _items = value ?? new List<string>(); }
        }

        public int MaxSuggestions
        {
            get => _maxSuggestions;
            set => _maxSuggestions = value;
        }

        public string Placeholder
        {
            get => _placeholder.Text;
            set
            {
                _placeholder.Text = value ?? string.Empty;
                _placeholder.Visibility = string.IsNullOrEmpty(_textBox.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public event Action<string>? SelectionChanged;

        /// <summary>Accessible name announced by screen readers when the control receives focus.</summary>
        public string AccessibleName { set => AutomationProperties.SetName(this, value ?? string.Empty); }

        public SearchAutocomplete()
        {
            InitializeComponent();
            ApplyTheme();
            AttachEvents();
            DrawIcon();
        }

        private void ApplyTheme()
        {
            _textBox.SetResourceReference(TextBox.BackgroundProperty,   "LemoineSelectBg");
            _textBox.SetResourceReference(TextBox.FontSizeProperty,    "LemoineFS_MD");
            _textBox.SetResourceReference(TextBox.ForegroundProperty,   "LemoineText");
            _textBox.SetResourceReference(TextBox.BorderBrushProperty,  "LemoineBorderMid");
            _textBox.SetResourceReference(TextBox.FontFamilyProperty,   "LemoineUiFont");
            _textBox.SetResourceReference(TextBox.CaretBrushProperty,   "LemoineText");

            _dropdownBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            _dropdownBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");

            _placeholder.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _placeholder.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
        }

        private void DrawIcon()
        {
            var path = new Path
            {
                Data    = Geometry.Parse("M11.742 10.344a6.5 6.5 0 1 0-1.397 1.398h-.001c.03.04.062.078.098.115l3.85 3.85a1 1 0 0 0 1.415-1.414l-3.85-3.85a1.007 1.007 0 0 0-.115-.099zm-5.242 1.656a5.5 5.5 0 1 1 0-11 5.5 5.5 0 0 1 0 11z"),
                Width   = 12,
                Height  = 12,
                Stretch = Stretch.Uniform,
            };
            path.SetResourceReference(Path.FillProperty, "LemoineTextDim");
            _iconCanvas.Children.Add(path);
        }

        private void AttachEvents()
        {
            _textBox.TextChanged += OnTextChanged;
            _textBox.GotFocus    += (s, e) =>
            {
                // Hide the icon and shift text left the moment the field is focused —
                // not on first keystroke — so the caret sits flush at the field's edge.
                UpdateIconState(showIcon: false);
                ShowDropdown();
            };
            _textBox.LostFocus   += (s, e) =>
            {
                // Restore the icon only when the field is left empty.
                UpdateIconState(showIcon: string.IsNullOrEmpty(_textBox.Text));
                // Delay so clicks on dropdown items register
                Dispatcher.BeginInvoke(new Action(() => _popup.IsOpen = false),
                    System.Windows.Threading.DispatcherPriority.Background);
            };

            _textBox.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) _popup.IsOpen = false;
                if (e.Key == Key.Down && _dropStack.Children.Count > 0)
                {
                    (_dropStack.Children[0] as UIElement)?.Focus();
                    e.Handled = true;
                }
            };
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            _value = _textBox.Text;
            _placeholder.Visibility = string.IsNullOrEmpty(_value)
                ? Visibility.Visible : Visibility.Collapsed;
            SelectionChanged?.Invoke(_value);
            // Only auto-open the dropdown for real typing — a programmatic text change
            // (e.g. panel rebuild / SetValue) must not pop the popup. Guard on keyboard focus.
            if (_textBox.IsKeyboardFocusWithin) ShowDropdown();
            // A programmatic SetValue on an unfocused field should still hide the icon when
            // it fills text in; focus changes are handled by the Got/LostFocus handlers.
            if (!string.IsNullOrEmpty(_value)) UpdateIconState(showIcon: false);
        }

        // Fades the search icon out (and slides the text padding left to fill the gap) when the
        // field is focused or holds text, and reverses both when it returns to an empty rest state.
        private void UpdateIconState(bool showIcon)
        {
            var dur = new Duration(TimeSpan.FromMilliseconds(AppSettings.Instance.AnimFast));

            if (!showIcon && _iconVisible)
            {
                _iconVisible = false;
                var fade = new DoubleAnimation(0, dur) { EasingFunction = _iconEase };
                // Guard: only collapse if we haven't been asked to restore in the meantime.
                fade.Completed += (s, e) => { if (!_iconVisible) _iconCanvas.Visibility = Visibility.Collapsed; };
                _iconCanvas.BeginAnimation(UIElement.OpacityProperty, fade);
                _textBox.BeginAnimation(Control.PaddingProperty,
                    new ThicknessAnimation(_padNoIcon, dur) { EasingFunction = _iconEase });
            }
            else if (showIcon && !_iconVisible)
            {
                _iconVisible = true;
                _iconCanvas.Visibility = Visibility.Visible;
                _iconCanvas.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(1, dur) { EasingFunction = _iconEase });
                _textBox.BeginAnimation(Control.PaddingProperty,
                    new ThicknessAnimation(_padWithIcon, dur) { EasingFunction = _iconEase });
            }
        }

        private void ShowDropdown()
        {
            var query    = _textBox.Text ?? "";
            var filtered = string.IsNullOrEmpty(query)
                ? _items.Take(_maxSuggestions).ToList()
                : _items.Where(i => i.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Take(_maxSuggestions).ToList();

            _dropStack.Children.Clear();

            if (filtered.Count == 0) { _popup.IsOpen = false; return; }

            foreach (var item in filtered)
            {
                var captured = item;
                var row = new Border
                {
                    Cursor    = Cursors.Hand,
                    Focusable = true,
                };
                if (item == _value)
                    row.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
                row.SetResourceReference(Border.PaddingProperty, "LemoineTh_CardPad");
                KeyboardNavigation.SetIsTabStop(row, false); // arrow-key nav only, not in tab order
                AutomationProperties.SetName(row, item);

                var txt = new TextBlock { Text = item };
                txt.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                txt.SetResourceReference(TextBlock.ForegroundProperty,
                    item == _value ? "LemoineAccent" : "LemoineText");
                txt.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                row.Child = txt;

                // Animated hover — the selected row keeps its accent rest state.
                MotionEffects.WireHover(row,
                    normalBgKey: captured == _value ? "LemoineAccentDim" : null,
                    hoverBgKey:  "LemoineAccentDim");
                row.GotFocus += (s, e) =>
                    row.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
                row.LostFocus += (s, e) =>
                {
                    if (captured == _value)
                        row.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
                    else
                        row.Background = Brushes.Transparent;
                };
                row.MouseLeftButtonDown += (s, e) => Select(captured);

                // Keyboard: Up/Down navigate, Enter selects, Escape closes
                row.KeyDown += (s, e) =>
                {
                    var items = _dropStack.Children.OfType<UIElement>().ToList();
                    int idx   = items.IndexOf(row);
                    switch (e.Key)
                    {
                        case Key.Enter:
                            Select(captured);
                            e.Handled = true;
                            break;
                        case Key.Escape:
                            _popup.IsOpen = false;
                            _textBox.Focus();
                            e.Handled = true;
                            break;
                        case Key.Down:
                            if (idx < items.Count - 1) items[idx + 1].Focus();
                            e.Handled = true;
                            break;
                        case Key.Up:
                            if (idx > 0) items[idx - 1].Focus();
                            else _textBox.Focus();
                            e.Handled = true;
                            break;
                    }
                };

                _dropStack.Children.Add(row);
            }

            _popup.Width    = _textBox.ActualWidth > 0 ? _textBox.ActualWidth : 200;
            _popup.IsOpen   = true;
        }

        private void Select(string item)
        {
            _value        = item;
            _textBox.Text = item;
            _popup.IsOpen = false;
            SelectionChanged?.Invoke(item);
        }
    }
}
