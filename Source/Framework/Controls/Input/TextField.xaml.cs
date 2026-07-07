using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace LemoineTools.Framework.Controls
{
    /// <summary>
    /// Themed single-line text input with a real watermark. The canonical control for
    /// free-text fields — replaces the raw <c>WpfTextBox</c> blocks scattered across
    /// tools so every plain text input shares one look and watermark behaviour.
    ///
    /// API:
    ///   Label       — optional caption above the field
    ///   Placeholder — watermark text shown when empty
    ///   Text        — current value (two-way via TextChanged)
    ///   event TextChanged(string)
    /// </summary>
    public partial class TextField : UserControl
    {
        public string Label
        {
            get => _label.Text;
            set
            {
                _label.Text = value;
                _label.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        public string Text
        {
            get => _textBox.Text;
            set => _textBox.Text = value ?? "";
        }

        public string Placeholder
        {
            get => _placeholder.Text;
            set
            {
                _placeholder.Text = value ?? string.Empty;
                UpdatePlaceholder();
            }
        }

        public event Action<string>? TextChanged;

        /// <summary>Accessible name announced by screen readers when the control receives focus.</summary>
        public string AccessibleName { set => AutomationProperties.SetName(this, value ?? string.Empty); }

        public TextField()
        {
            InitializeComponent();
            ApplyResourceReferences();
            _textBox.TextChanged += OnTextChanged;
        }

        private void ApplyResourceReferences()
        {
            _textBox.SetResourceReference(TextBox.BackgroundProperty,     "LemoineSelectBg");
            _textBox.SetResourceReference(TextBox.ForegroundProperty,     "LemoineText");
            _textBox.SetResourceReference(TextBox.BorderBrushProperty,    "LemoineBorderMid");
            _textBox.SetResourceReference(TextBox.CaretBrushProperty,     "LemoineText");
            _textBox.SetResourceReference(TextBox.FontFamilyProperty,     "LemoineUiFont");
            _textBox.SetResourceReference(TextBox.FontSizeProperty,       "LemoineFS_MD");
            _textBox.SetResourceReference(TextBox.SelectionBrushProperty, "LemoineAccent");

            _placeholder.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _placeholder.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            _label.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            _label.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _label.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            _label.FontStyle = FontStyles.Italic;
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            UpdatePlaceholder();
            TextChanged?.Invoke(_textBox.Text);
        }

        private void UpdatePlaceholder()
            => _placeholder.Visibility = string.IsNullOrEmpty(_textBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;
    }
}
