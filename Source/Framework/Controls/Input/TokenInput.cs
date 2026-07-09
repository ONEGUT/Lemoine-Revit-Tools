using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LemoineTools.Framework.Naming;

namespace LemoineTools.Framework.Controls
{
    /// <summary>
    /// Code-behind-only UserControl that renders a themed TextBox with clickable token
    /// chip Buttons grouped underneath it. Clicking a chip inserts its token text at the
    /// current cursor position. This is the one naming-pattern editor shared by every
    /// tool that generates or rewrites names (the former separate Front/Center/End
    /// "NamingSlots" control is retired in favour of this).
    /// </summary>
    public class TokenInput : UserControl
    {
        // ── Internal refs ─────────────────────────────────────────────────────
        private readonly TextBox    _textBox;
        private readonly StackPanel _chipGroups;
        private readonly Button     _resetButton;
        private readonly StackPanel _root;
        private TextBlock?          _previewText;
        private readonly string     _defaultPattern;
        private Func<string, string>? _resolvePreview;

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

        /// <param name="tokens">The token vocabulary to offer, already filtered to what's
        /// valid for the caller's entity/context (see <see cref="NamingTokenRegistry.TokensFor"/>).</param>
        /// <param name="defaultPattern">The tool's default pattern — also doubles as the
        /// textbox tooltip and the target of the reset button.</param>
        public TokenInput(IReadOnlyList<TokenDefinition> tokens, string defaultPattern = "")
        {
            _defaultPattern = defaultPattern ?? "";

            // ── TextBox ───────────────────────────────────────────────────────
            _textBox = new TextBox
            {
                Text          = "",
                Padding       = new Thickness(8, 4, 8, 4),
                TextWrapping  = TextWrapping.NoWrap,
                AcceptsReturn = false,
            };
            _textBox.SetResourceReference(TextBox.MinHeightProperty,      "LemoineH_Input");
            _textBox.SetResourceReference(TextBox.FontFamilyProperty,     "LemoineMonoFont");
            _textBox.SetResourceReference(TextBox.FontSizeProperty,       "LemoineFS_MD");
            _textBox.SetResourceReference(TextBox.BackgroundProperty,     "LemoineSelectBg");
            _textBox.SetResourceReference(TextBox.ForegroundProperty,     "LemoineText");
            _textBox.SetResourceReference(TextBox.BorderBrushProperty,    "LemoineBorderMid");
            _textBox.SetResourceReference(TextBox.CaretBrushProperty,     "LemoineText");
            _textBox.SetResourceReference(TextBox.SelectionBrushProperty, "LemoineAccent");
            if (!string.IsNullOrEmpty(_defaultPattern)) _textBox.ToolTip = _defaultPattern;

            _textBox.TextChanged += (s, e) => OnTextChanged();

            // ── Reset button ─────────────────────────────────────────────────
            _resetButton = new Button
            {
                Content         = char.ConvertFromUtf32(0x21BA) + " " + AppStrings.T("naming.input.reset"),
                Cursor          = Cursors.Hand,
                Margin          = new Thickness(6, 0, 0, 0),
                Padding         = new Thickness(8, 0, 8, 0),
                BorderThickness = new Thickness(1),
                Template        = ControlStyles.BuildFlatButtonTemplate(),
                Visibility      = Visibility.Collapsed,
            };
            _resetButton.SetResourceReference(Button.MinHeightProperty,  "LemoineH_Input");
            _resetButton.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_SM");
            _resetButton.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            _resetButton.Background = Brushes.Transparent;
            _resetButton.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
            _resetButton.SetResourceReference(Button.ForegroundProperty,  "LemoineTextDim");
            _resetButton.Click += (s, e) => Text = _defaultPattern;

            var inputRow = new Grid();
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_textBox, 0);
            Grid.SetColumn(_resetButton, 1);
            inputRow.Children.Add(_textBox);
            inputRow.Children.Add(_resetButton);

            // ── Grouped chip rows ────────────────────────────────────────────
            _chipGroups = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin      = new Thickness(0, 6, 0, 0),
            };
            BuildChipGroups(tokens);

            // ── Layout ────────────────────────────────────────────────────────
            _root = new StackPanel { Orientation = Orientation.Vertical };
            _root.Children.Add(_chipGroups);
            _root.Children.Add(inputRow);
            Content = _root;
        }

        /// <summary>
        /// Adds a live preview line under the input, re-rendered on every text change and once
        /// immediately. <paramref name="resolvePattern"/> should build a sample context and call
        /// <see cref="TokenResolver.Resolve"/> — the caller owns context construction since only
        /// it knows a representative sample element.
        /// </summary>
        public void SetPreview(Func<string, string> resolvePattern)
        {
            _resolvePreview = resolvePattern;

            if (_previewText == null)
            {
                _previewText = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                    Margin       = new Thickness(0, 6, 0, 0),
                };
                _previewText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                _previewText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                _previewText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                _root.Children.Add(_previewText);
            }

            RefreshPreview();
        }

        /// <summary>Re-renders the preview against the current text. Call this when the
        /// caller's own state changes (e.g. a different sample element becomes selected on
        /// another step) — <see cref="TextChanged"/> alone only covers edits to this box.</summary>
        public void RefreshPreview()
        {
            RefreshPreviewCore();
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private void OnTextChanged()
        {
            _resetButton.Visibility = Text.Trim() == _defaultPattern.Trim()
                ? Visibility.Collapsed
                : Visibility.Visible;
            RefreshPreviewCore();
            TextChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RefreshPreviewCore()
        {
            if (_previewText == null || _resolvePreview == null) return;
            string resolved = _resolvePreview(Text) ?? "";
            _previewText.Text = AppStrings.T("naming.input.previewLabel") + " " +
                (string.IsNullOrWhiteSpace(resolved) ? AppStrings.T("naming.input.previewEmpty") : resolved);
        }

        // Fixed group order: This item -> Source -> Project -> Date & Counter -> Your tokens.
        // A group is omitted entirely when it has no matching tokens, so a tool with no source
        // concept never shows an empty "Source" header.
        private void BuildChipGroups(IReadOnlyList<TokenDefinition> tokens)
        {
            var builtin = tokens.Where(t => t.Origin != TokenOrigin.UserParameter).ToList();
            var user    = tokens.Where(t => t.Origin == TokenOrigin.UserParameter).ToList();

            AddGroupIfAny(AppStrings.T("naming.input.groupTarget"),
                builtin.Where(t => t.Subject == TokenSubject.Target).ToList());
            AddGroupIfAny(AppStrings.T("naming.input.groupSource"),
                builtin.Where(t => t.Subject == TokenSubject.Source).ToList());
            AddGroupIfAny(AppStrings.T("naming.input.groupProject"),
                builtin.Where(t => t.Subject == TokenSubject.ProjectInfo).ToList());
            AddGroupIfAny(AppStrings.T("naming.input.groupDate"),
                builtin.Where(t => t.Subject == TokenSubject.Environment).ToList());
            AddGroupIfAny(AppStrings.T("naming.input.groupUser"), user);
        }

        private void AddGroupIfAny(string headerLabel, IReadOnlyList<TokenDefinition> items)
        {
            if (items.Count == 0) return;

            var header = new TextBlock
            {
                Text   = headerLabel.ToUpperInvariant(),
                Margin = new Thickness(0, _chipGroups.Children.Count == 0 ? 0 : 8, 0, 4),
            };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _chipGroups.Children.Add(header);

            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var def in items)
                row.Children.Add(BuildChipButton(def));
            _chipGroups.Children.Add(row);
        }

        private Button BuildChipButton(TokenDefinition def)
        {
            bool isUser = def.Origin == TokenOrigin.UserParameter;

            var btn = new Button
            {
                Content         = def.Label,
                Cursor          = Cursors.Hand,
                Margin          = new Thickness(0, 4, 4, 0),
                Padding         = new Thickness(8, 0, 8, 0),
                BorderThickness = new Thickness(1),
                Template        = ControlStyles.BuildFlatButtonTemplate(),
            };
            btn.SetResourceReference(Button.MinHeightProperty,  "LemoineH_BtnMin");
            btn.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_SM");
            btn.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            btn.Background = Brushes.Transparent;
            btn.SetResourceReference(Button.BackgroundProperty,  "LemoineRaised");
            btn.SetResourceReference(Button.BorderBrushProperty, isUser ? "LemoineAccent" : "LemoineBorder");
            btn.SetResourceReference(Button.ForegroundProperty,  isUser ? "LemoineAccent" : "LemoineText");

            string tooltip = def.Description ?? "";
            if (isUser && !string.IsNullOrEmpty(def.ParameterName))
                tooltip = (string.IsNullOrEmpty(tooltip) ? "" : tooltip + "\n") +
                          AppStrings.T("naming.input.parameterTooltip", def.ParameterName ?? "");
            if (!string.IsNullOrEmpty(tooltip)) btn.ToolTip = tooltip;

            btn.MouseEnter += (s, e) =>
            {
                btn.SetResourceReference(Button.BackgroundProperty,  "LemoineSelectBg");
                btn.SetResourceReference(Button.BorderBrushProperty, "LemoineAccent");
            };
            btn.MouseLeave += (s, e) =>
            {
                btn.SetResourceReference(Button.BackgroundProperty,  "LemoineRaised");
                btn.SetResourceReference(Button.BorderBrushProperty, isUser ? "LemoineAccent" : "LemoineBorder");
            };

            btn.Click += (s, e) => InsertAtCursor(def.Braced);
            return btn;
        }

        private void InsertAtCursor(string s)
        {
            int caret = _textBox.CaretIndex;
            _textBox.Text = _textBox.Text.Insert(caret, s);
            _textBox.CaretIndex = caret + s.Length;
            _textBox.Focus();
        }
    }
}
