using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Click / double-click to rename. Renders as a TextBlock by default; on
    /// activation swaps to a TextBox. Commits on Enter or LostFocus; reverts on Escape.
    ///
    /// Matches the Filters-tab rename convention — double-click to start.
    /// </summary>
    public partial class LemoineInlineEdit : UserControl
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(LemoineInlineEdit),
                new FrameworkPropertyMetadata("",
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

        public static readonly DependencyProperty BoldProperty =
            DependencyProperty.Register(nameof(Bold), typeof(bool), typeof(LemoineInlineEdit),
                new PropertyMetadata(false, OnVisualChanged));

        public static readonly DependencyProperty PlaceholderProperty =
            DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(LemoineInlineEdit),
                new PropertyMetadata("(empty)", OnVisualChanged));

        public static readonly DependencyProperty FontSizeKeyProperty =
            DependencyProperty.Register(nameof(FontSizeKey), typeof(string), typeof(LemoineInlineEdit),
                new PropertyMetadata("LemoineFS_MD", OnVisualChanged));

        public static readonly DependencyProperty UppercaseProperty =
            DependencyProperty.Register(nameof(Uppercase), typeof(bool), typeof(LemoineInlineEdit),
                new PropertyMetadata(false, OnVisualChanged));

        public string Text         { get => (string)GetValue(TextProperty);         set => SetValue(TextProperty, value); }
        public bool   Bold         { get => (bool)GetValue(BoldProperty);           set => SetValue(BoldProperty, value); }
        public string Placeholder  { get => (string)GetValue(PlaceholderProperty);  set => SetValue(PlaceholderProperty, value); }
        public string FontSizeKey  { get => (string)GetValue(FontSizeKeyProperty);  set => SetValue(FontSizeKeyProperty, value); }
        public bool   Uppercase    { get => (bool)GetValue(UppercaseProperty);      set => SetValue(UppercaseProperty, value); }

        /// <summary>Raised after Text changes via a successful user edit.</summary>
        public event EventHandler<string>? TextCommitted;

        private TextBlock? _display;
        private TextBox?   _editor;
        private bool _editing;

        public LemoineInlineEdit()
        {
            InitializeComponent();
            Loaded += (s, e) => RebuildDisplay();
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LemoineInlineEdit ed) ed.RebuildDisplay();
        }
        private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LemoineInlineEdit ed) ed.RebuildDisplay();
        }

        private void RebuildDisplay()
        {
            if (_editing) return;
            _root.Children.Clear();

            _display = new TextBlock
            {
                Text = DisplayText(),
                FontWeight = Bold ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor = Cursors.IBeam,
            };
            _display.SetResourceReference(TextBlock.ForegroundProperty,
                string.IsNullOrEmpty(Text) ? "LemoineTextDim" : "LemoineText");
            _display.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _display.SetResourceReference(TextBlock.FontSizeProperty, FontSizeKey);

            if (string.IsNullOrEmpty(Text))
                _display.FontStyle = FontStyles.Italic;

            _display.MouseLeftButtonDown += DisplayMouseDown;
            _root.Children.Add(_display);
        }

        private string DisplayText()
        {
            if (string.IsNullOrEmpty(Text)) return Placeholder ?? "";
            return Uppercase ? (Text ?? "").ToUpperInvariant() : (Text ?? "");
        }

        private void DisplayMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount >= 2)
            {
                e.Handled = true;
                BeginEdit();
            }
        }

        /// <summary>Programmatic activation (e.g. F2-style flow from parent).</summary>
        public void BeginEdit()
        {
            if (_editing) return;
            _editing = true;
            _root.Children.Clear();

            _editor = new TextBox
            {
                Text = Text ?? "",
                BorderThickness = new Thickness(0, 0, 0, 1),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontWeight = Bold ? FontWeights.SemiBold : FontWeights.Normal,
            };
            _editor.SetResourceReference(TextBox.ForegroundProperty,  "LemoineText");
            _editor.SetResourceReference(TextBox.BorderBrushProperty, "LemoineAccent");
            _editor.SetResourceReference(TextBox.FontFamilyProperty,  "LemoineUiFont");
            _editor.SetResourceReference(TextBox.FontSizeProperty,    FontSizeKey);
            _editor.SetResourceReference(TextBox.CaretBrushProperty,  "LemoineText");

            bool committed = false;
            void Commit(bool accept)
            {
                if (committed) return; committed = true;
                _editing = false;
                if (accept)
                {
                    string newText = _editor.Text?.Trim() ?? "";
                    if (Uppercase) newText = newText.ToUpperInvariant();
                    if (newText != Text)
                    {
                        Text = newText;
                        TextCommitted?.Invoke(this, newText);
                    }
                }
                RebuildDisplay();
            }

            _editor.LostFocus += (s, e) => Commit(true);
            _editor.KeyDown   += (s, e) =>
            {
                if (e.Key == Key.Enter)  { e.Handled = true; Commit(true); }
                if (e.Key == Key.Escape) { e.Handled = true; Commit(false); }
            };

            _root.Children.Add(_editor);
            _editor.Focus();
            _editor.SelectAll();
        }
    }
}
