using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace LemoineTools.Lemoine.Controls
{
    public partial class LemoineFileBrowser : UserControl
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

        public string Path
        {
            get => _pathBox.Text;
            set => _pathBox.Text = value ?? "";
        }

        public string? Placeholder { get; set; }

        public string Filter      { get; set; } = "All files|*.*";
        public string DialogTitle { get; set; } = "Select File";

        // Recents property kept for API compatibility — silently ignored
        public System.Collections.Generic.IList<string> Recents { set { } }

        public event Action<string>? PathChanged;

        /// <summary>Accessible name announced by screen readers when the control receives focus.</summary>
        public string AccessibleName { set => AutomationProperties.SetName(this, value ?? string.Empty); }

        public LemoineFileBrowser()
        {
            InitializeComponent();
            ApplyResourceReferences();
        }

        private void ApplyResourceReferences()
        {
            _pathBox.SetResourceReference(TextBox.BackgroundProperty,     "LemoineSelectBg");
            _pathBox.SetResourceReference(TextBox.ForegroundProperty,     "LemoineText");
            _pathBox.SetResourceReference(TextBox.BorderBrushProperty,    "LemoineBorderMid");
            _pathBox.SetResourceReference(TextBox.CaretBrushProperty,     "LemoineText");
            _pathBox.SetResourceReference(TextBox.FontFamilyProperty,     "LemoineMonoFont");
            _pathBox.SetResourceReference(TextBox.FontSizeProperty,       "LemoineFS_MD");
            _pathBox.SetResourceReference(TextBox.SelectionBrushProperty, "LemoineAccent");

            _browseBtn.SetResourceReference(Button.BackgroundProperty,    "LemoineRaised");
            _browseBtn.SetResourceReference(Button.BorderBrushProperty,   "LemoineBorder");
            _browseBtn.SetResourceReference(Button.ForegroundProperty,    "LemoineText");
            _browseBtn.SetResourceReference(Button.FontFamilyProperty,    "LemoineUiFont");
            _browseBtn.SetResourceReference(Button.FontSizeProperty,      "LemoineFS_MD");
            _browseBtn.SetResourceReference(Button.MinHeightProperty,     "LemoineH_BtnMin");
            _browseBtn.Template = BuildFlatButtonTemplate();

            _label.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            _label.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _label.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            _label.FontStyle = FontStyles.Italic;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog { Title = DialogTitle, Filter = Filter, CheckFileExists = true };
            if (ofd.ShowDialog() == true)
            {
                _pathBox.Text = ofd.FileName;
                PathChanged?.Invoke(ofd.FileName);
            }
        }

        private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            PathChanged?.Invoke(_pathBox.Text);
        }

        private static ControlTemplate BuildFlatButtonTemplate()
            => LemoineControlStyles.BuildFlatButtonTemplate();
    }
}
