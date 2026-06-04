using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Code-behind-only UserControl that renders a themed TextBox with a row of
    /// clickable token chip Buttons below it.  Clicking a chip inserts its token
    /// string at the current cursor position.
    ///
    /// Used by Bulk Export (filename pattern) and Create Sheets (sheet naming scheme).
    /// </summary>
    public class LemoineTokenInput : UserControl
    {
        // ── Internal refs ─────────────────────────────────────────────────────
        private readonly TextBox   _textBox;
        private readonly WrapPanel _chipPanel;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Current text value in the TextBox.</summary>
        public string Text
        {
            get => _textBox.Text;
            set => _textBox.Text = value ?? "";
        }

        /// <summary>Fires on every keystroke and on chip insert.</summary>
        public event EventHandler? TextChanged;

        // ── Construction ──────────────────────────────────────────────────────

        /// <summary>
        /// Creates a LemoineTokenInput with the given token set.
        /// </summary>
        /// <param name="tokens">List of (DisplayLabel, InsertedToken) pairs.</param>
        /// <param name="placeholder">Greyed hint text shown when the box is empty.</param>
        public LemoineTokenInput(IEnumerable<(string Label, string Token)> tokens,
                                  string placeholder = "")
        {
            // ── TextBox ───────────────────────────────────────────────────────
            _textBox = new TextBox
            {
                Text        = "",
                Padding     = new Thickness(8, 4, 8, 4),
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = false,
            };
            _textBox.SetResourceReference(TextBox.MinHeightProperty,    "LemoineH_Input");
            _textBox.SetResourceReference(TextBox.FontFamilyProperty,   "LemoineMonoFont");
            _textBox.SetResourceReference(TextBox.FontSizeProperty,     "LemoineFS_MD");
            _textBox.SetResourceReference(TextBox.BackgroundProperty,   "LemoineSelectBg");
            _textBox.SetResourceReference(TextBox.ForegroundProperty,   "LemoineText");
            _textBox.SetResourceReference(TextBox.BorderBrushProperty,  "LemoineBorderMid");
            _textBox.SetResourceReference(TextBox.CaretBrushProperty,   "LemoineText");
            _textBox.SetResourceReference(TextBox.SelectionBrushProperty, "LemoineAccent");

            if (!string.IsNullOrEmpty(placeholder))
            {
                // Watermark via Tag + style trigger is complex code-behind; use a simpler
                // overlay approach — show a TextBlock behind the TextBox and hide it on focus/text
                // For simplicity here, we set the actual text and clear on first edit.
                _textBox.Foreground = Brushes.Transparent;  // initially invisible placeholder
                var placed = false;
                _textBox.GotFocus += (s, e) =>
                {
                    if (!placed) return;
                    placed = false;
                    _textBox.Text = "";
                    _textBox.SetResourceReference(TextBox.ForegroundProperty, "LemoineText");
                };
                // Actually, use a simpler approach: just set Tag and use ToolTip for placeholder
                _textBox.SetResourceReference(TextBox.ForegroundProperty, "LemoineText");
                _textBox.ToolTip = placeholder;
            }

            _textBox.TextChanged += (s, e) => TextChanged?.Invoke(this, EventArgs.Empty);

            // ── Chip WrapPanel ────────────────────────────────────────────────
            _chipPanel = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 5, 0, 0),
            };

            foreach (var (label, token) in tokens)
            {
                var captured = token;
                var chip = BuildChipButton(label, captured);
                _chipPanel.Children.Add(chip);
            }

            // ── Layout ────────────────────────────────────────────────────────
            var stack = new StackPanel { Orientation = Orientation.Vertical };
            stack.Children.Add(_textBox);
            stack.Children.Add(_chipPanel);
            Content = stack;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private Button BuildChipButton(string label, string token)
        {
            var btn = new Button
            {
                Content         = label,
                Cursor          = Cursors.Hand,
                Margin          = new Thickness(0, 4, 4, 0),
                Padding         = new Thickness(8, 0, 8, 0),
                BorderThickness = new Thickness(1),
                Template        = LemoineControlStyles.BuildFlatButtonTemplate(),
            };
            btn.SetResourceReference(Button.MinHeightProperty,  "LemoineH_BtnMin");
            btn.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_SM");
            btn.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            btn.Background = Brushes.Transparent;
            btn.SetResourceReference(Button.BackgroundProperty,  "LemoineRaised");
            btn.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
            btn.SetResourceReference(Button.ForegroundProperty,  "LemoineText");

            btn.MouseEnter += (s, e) =>
            {
                btn.SetResourceReference(Button.BackgroundProperty,  "LemoineSelectBg");
                btn.SetResourceReference(Button.BorderBrushProperty, "LemoineAccent");
            };
            btn.MouseLeave += (s, e) =>
            {
                btn.SetResourceReference(Button.BackgroundProperty,  "LemoineRaised");
                btn.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
            };

            btn.Click += (s, e) => InsertAtCursor(token);
            return btn;
        }

        private void InsertAtCursor(string s)
        {
            int caret = _textBox.CaretIndex;
            _textBox.Text = _textBox.Text.Insert(caret, s);
            _textBox.CaretIndex = caret + s.Length;
            _textBox.Focus();
        }

        // ── Static helper ─────────────────────────────────────────────────────

        /// <summary>
        /// Resolves a token pattern against a dictionary of values.
        /// E.g. "{SheetNumber}-{SheetName}" + {"SheetNumber":"A101"} → "A101-Ground Floor"
        /// Any token not found in the dictionary is left unchanged.
        /// </summary>
        public static string Resolve(string pattern, Dictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(pattern)) return pattern ?? "";
            var result = pattern;
            foreach (var kvp in values)
                result = result.Replace("{" + kvp.Key + "}", kvp.Value ?? "");
            return result;
        }
    }
}
