using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using WinForms = System.Windows.Forms;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Folder-path picker — the directory twin of <see cref="LemoineFileBrowser"/>.
    /// Replaces the hand-rolled TextBox + FolderBrowserDialog blocks scattered across
    /// tools (BatchExport, MakeCeilingGrids, Preview) with one themed control.
    ///
    /// API mirrors LemoineFileBrowser:
    ///   Label        — optional caption above the field
    ///   Path         — selected folder path (two-way via PathChanged)
    ///   DialogTitle  — text shown at the top of the folder dialog
    ///   event PathChanged(string)
    /// </summary>
    public partial class LemoineFolderBrowser : UserControl
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

        public string DialogTitle { get; set; } = "Select Folder";

        public event Action<string>? PathChanged;

        /// <summary>Accessible name announced by screen readers when the control receives focus.</summary>
        public string AccessibleName { set => AutomationProperties.SetName(this, value ?? string.Empty); }

        public LemoineFolderBrowser()
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
            _browseBtn.Template = LemoineControlStyles.BuildFlatButtonTemplate();

            _label.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            _label.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _label.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            _label.FontStyle = FontStyles.Italic;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new WinForms.FolderBrowserDialog
            {
                Description         = DialogTitle,
                SelectedPath        = _pathBox.Text,
                ShowNewFolderButton = true,
            })
            {
                if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                {
                    _pathBox.Text = dlg.SelectedPath;
                    PathChanged?.Invoke(dlg.SelectedPath);
                }
            }
        }

        private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            PathChanged?.Invoke(_pathBox.Text);
        }
    }
}
