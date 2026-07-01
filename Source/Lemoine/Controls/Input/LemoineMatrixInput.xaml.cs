using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using LemoineTools.Lemoine;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Grid of text inputs (rows × cols).
    /// Mirrors JS MatrixInput exactly.
    ///
    /// Value key format: "rowName__colName"
    /// Defaults: Dictionary keyed by column name.
    ///
    /// API:
    ///   SetMatrix(rows, cols, defaults)   — configure the grid
    ///   Values                            — current Dictionary of rowKey__colKey → string
    ///   event ValueChanged                — fires on every cell edit
    /// </summary>
    public partial class LemoineMatrixInput : UserControl
    {
        private List<string>              _rows     = new List<string>();
        private List<string>              _cols     = new List<string>();
        private Dictionary<string, string> _defaults = new Dictionary<string, string>();
        private Dictionary<string, string> _values   = new Dictionary<string, string>();
        private Dictionary<string, TextBox> _cells   = new Dictionary<string, TextBox>();

        public IReadOnlyDictionary<string, string> Values => _values;
        public event Action<IReadOnlyDictionary<string, string>>? ValueChanged;

        /// <summary>Accessible name announced by screen readers when the control receives focus.</summary>
        public string AccessibleName { set => AutomationProperties.SetName(this, value ?? string.Empty); }

        public LemoineMatrixInput() => InitializeComponent();

        public void SetMatrix(
            IList<string> rows,
            IList<string> cols,
            Dictionary<string, string>? defaults = null,
            Dictionary<string, string>? initial  = null)
        {
            _rows     = new List<string>(rows);
            _cols     = new List<string>(cols);
            _defaults = defaults ?? new Dictionary<string, string>();
            _values   = initial  != null ? new Dictionary<string, string>(initial)
                                         : new Dictionary<string, string>();
            _cells.Clear();
            Rebuild();
        }

        private void Rebuild()
        {
            _root.Children.Clear();
            _root.RowDefinitions.Clear();
            _root.ColumnDefinitions.Clear();

            // Column definitions: row-label + one per data column
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            foreach (var _ in _cols)
                _root.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 90 });

            // Header row
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowHdr = BuildHeaderCell(LemoineStrings.T("controls.inputs.matrixInput.rowHeader"), 0, 0);
            Grid.SetRow(rowHdr, 0); Grid.SetColumn(rowHdr, 0);
            _root.Children.Add(rowHdr);

            for (int c = 0; c < _cols.Count; c++)
            {
                var hdr = BuildHeaderCell(_cols[c].ToUpper(), 0, c + 1);
                Grid.SetRow(hdr, 0); Grid.SetColumn(hdr, c + 1);
                _root.Children.Add(hdr);
            }

            // Data rows
            for (int r = 0; r < _rows.Count; r++)
            {
                _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var row = _rows[r];
                bool alt = r % 2 != 0;

                // Row label cell
                var labelCell = new Border
                {
                    Padding = new Thickness(8, 5, 8, 5),
                    Background = alt ? new SolidColorBrush(Color.FromArgb(10, 128, 128, 128)) : Brushes.Transparent,
                };
                labelCell.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                labelCell.BorderThickness = new Thickness(0, 0, 0, 1);
                var labelText = new TextBlock
                {
                    Text       = row,
                    FontSize   = 11,
                    FontWeight = FontWeights.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                labelText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
                labelText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                labelCell.Child = labelText;
                Grid.SetRow(labelCell, r + 1); Grid.SetColumn(labelCell, 0);
                _root.Children.Add(labelCell);

                // Input cells
                for (int c = 0; c < _cols.Count; c++)
                {
                    var col = _cols[c];
                    var key = $"{row}__{col}";
                    if (!_values.ContainsKey(key))
                        _values[key] = _defaults.TryGetValue(col, out var def) ? def : "";

                    var cell = new Border
                    {
                        Padding    = new Thickness(3, 3, 3, 3),
                        Background = alt ? new SolidColorBrush(Color.FromArgb(10, 128, 128, 128)) : Brushes.Transparent,
                    };
                    cell.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                    cell.BorderThickness = new Thickness(0, 0, 0, 1);

                    var tb = new TextBox
                    {
                        Text            = _values[key],
                        FontSize        = 10,
                        Padding         = new Thickness(5, 4, 5, 4),
                        BorderThickness = new Thickness(1),
                        VerticalContentAlignment = VerticalAlignment.Center,
                    };
                    tb.SetResourceReference(TextBox.BackgroundProperty,   "LemoineSelectBg");
                    tb.SetResourceReference(TextBox.ForegroundProperty,   "LemoineText");
                    tb.SetResourceReference(TextBox.BorderBrushProperty,  "LemoineBorderMid");
                    tb.SetResourceReference(TextBox.FontFamilyProperty,   "LemoineMonoFont");
                    tb.SetResourceReference(TextBox.CaretBrushProperty,   "LemoineText");

                    var capturedKey = key;
                    tb.TextChanged += (s, e) =>
                    {
                        _values[capturedKey] = tb.Text;
                        ValueChanged?.Invoke(Values);
                    };

                    _cells[key] = tb;
                    cell.Child  = tb;
                    Grid.SetRow(cell, r + 1); Grid.SetColumn(cell, c + 1);
                    _root.Children.Add(cell);
                }
            }
        }

        private Border BuildHeaderCell(string text, int row, int col)
        {
            var border = new Border { Padding = new Thickness(6, 5, 6, 5) };
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            border.BorderThickness = new Thickness(0, 0, 0, 1);

            var tb = new TextBlock
            {
                Text          = text,
                FontSize      = 9.5,
                FontWeight    = FontWeights.Medium,
                TextTrimming  = TextTrimming.CharacterEllipsis,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            border.Child = tb;
            return border;
        }
    }
}
