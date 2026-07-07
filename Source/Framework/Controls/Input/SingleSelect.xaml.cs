using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace LemoineTools.Framework.Controls
{
    public partial class SingleSelect : UserControl
    {
        public string Label
        {
            get => _label.Text;
            set { _label.Text = value; _label.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public string? SelectedItem
        {
            get => _combo.SelectedItem as string;
            set
            {
                if (_combo.Items.Contains(value))
                    _combo.SelectedItem = value;
            }
        }

        public IList<string> Items
        {
            set
            {
                _combo.Items.Clear();
                if (value == null) return;
                foreach (var item in value) _combo.Items.Add(item);
                if (_combo.Items.Count > 0) _combo.SelectedIndex = 0;
            }
        }

        public event Action<string?>? SelectionChanged;

        /// <summary>Accessible name announced by screen readers when the control receives focus.</summary>
        public string AccessibleName { set => AutomationProperties.SetName(this, value ?? string.Empty); }

        public SingleSelect()
        {
            InitializeComponent();
            ApplyResourceReferences();
        }

        private void ApplyResourceReferences()
        {
            // Read-only template: a single-choice picker, not an editable text combo.
            _combo.Style = ControlStyles.BuildReadOnlyComboBoxStyle();
            // Don't let a closed combo eat the wheel (it would change value + trap page scroll).
            ControlStyles.WireComboWheelBubbling(_combo);
            _combo.SetResourceReference(ComboBox.BackgroundProperty,   "LemoineSelectBg");
            _combo.SetResourceReference(ComboBox.ForegroundProperty,   "LemoineText");
            _combo.SetResourceReference(ComboBox.BorderBrushProperty,  "LemoineBorderMid");
            _combo.SetResourceReference(ComboBox.FontFamilyProperty,   "LemoineUiFont");
            _combo.SetResourceReference(ComboBox.FontSizeProperty,     "LemoineFS_MD");
            _combo.SetResourceReference(ComboBox.MinHeightProperty,    "LemoineH_Input");

            _label.SetResourceReference(TextBlock.ForegroundProperty,  "LemoineTextSub");
            _label.SetResourceReference(TextBlock.FontFamilyProperty,  "LemoineUiFont");
            _label.SetResourceReference(TextBlock.FontSizeProperty,    "LemoineFS_SM");
        }

        private void Combo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectionChanged?.Invoke(_combo.SelectedItem as string);
        }
    }
}
