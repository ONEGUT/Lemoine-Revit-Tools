using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Single date or date-range picker.
    /// Mirrors JS DatePicker exactly.
    ///
    /// Single mode:  DateChanged fires with (from: string, to: null)
    /// Range  mode:  DateChanged fires with (from: string, to: string)
    ///
    /// Dates are ISO 8601 strings "YYYY-MM-DD" or null when not set.
    ///
    /// API:
    ///   Mode  = "single" | "range"
    ///   Label = optional label text above the control
    ///   SetDates(from, to)      — pre-populate
    ///   event DateChanged(from, to)
    /// </summary>
    public partial class LemoineDatePicker : UserControl
    {
        public enum PickerMode { Single, Range }

        private PickerMode  _mode  = PickerMode.Single;
        private string?     _label = null;

        private DatePicker? _singlePicker;
        private DatePicker? _fromPicker;
        private DatePicker? _toPicker;

        public event Action<string?, string?>? DateChanged;  // (from, to)

        /// <summary>Accessible name announced by screen readers when the control receives focus.</summary>
        public string AccessibleName { set => AutomationProperties.SetName(this, value ?? string.Empty); }

        public LemoineDatePicker() => InitializeComponent();

        public string Label
        {
            get => _label ?? string.Empty;
            set { _label = value; Rebuild(); }
        }

        public PickerMode Mode
        {
            get => _mode;
            set { _mode = value; Rebuild(); }
        }

        public void SetDates(string from, string? to = null)
        {
            if (_mode == PickerMode.Single && _singlePicker != null)
                _singlePicker.SelectedDate = TryParse(from);
            else if (_mode == PickerMode.Range)
            {
                if (_fromPicker != null) _fromPicker.SelectedDate = TryParse(from);
                if (_toPicker   != null) _toPicker.SelectedDate   = TryParse(to);
            }
        }

        private static DateTime? TryParse(string? s)
            => DateTime.TryParse(s, out var d) ? (DateTime?)d : null;

        private static string? ToIso(DateTime? d)
            => d.HasValue ? d.Value.ToString("yyyy-MM-dd") : null;

        private void Rebuild()
        {
            _root.Children.Clear();

            // Optional label
            if (!string.IsNullOrEmpty(_label))
            {
                var lbl = new TextBlock
                {
                    Text        = _label,
                    FontSize    = 11,
                    TextWrapping= TextWrapping.Wrap,
                    Margin      = new Thickness(0, 0, 0, 6),
                };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty,  "LemoineUiFont");
                _root.Children.Add(lbl);
            }

            if (_mode == PickerMode.Single)
            {
                _singlePicker = BuildPicker();
                _singlePicker.SelectedDateChanged += (s, e) =>
                    DateChanged?.Invoke(ToIso(_singlePicker.SelectedDate), null);
                _root.Children.Add(_singlePicker);
            }
            else
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());

                // From
                var fromStack = new StackPanel();
                var fromHdr   = MiniLabel("FROM");
                _fromPicker   = BuildPicker();
                _fromPicker.SelectedDateChanged += (s, e) =>
                    DateChanged?.Invoke(ToIso(_fromPicker.SelectedDate), ToIso(_toPicker?.SelectedDate));
                fromStack.Children.Add(fromHdr);
                fromStack.Children.Add(_fromPicker);
                Grid.SetColumn(fromStack, 0);

                // Arrow
                var arrow = new TextBlock
                {
                    Text = "→",
                    
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(8, 0, 8, 6),
                };
                arrow.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                Grid.SetColumn(arrow, 1);

                // To
                var toStack = new StackPanel();
                var toHdr   = MiniLabel("TO");
                _toPicker   = BuildPicker();
                _toPicker.SelectedDateChanged += (s, e) =>
                    DateChanged?.Invoke(ToIso(_fromPicker?.SelectedDate), ToIso(_toPicker.SelectedDate));
                toStack.Children.Add(toHdr);
                toStack.Children.Add(_toPicker);
                Grid.SetColumn(toStack, 2);

                row.Children.Add(fromStack);
                row.Children.Add(arrow);
                row.Children.Add(toStack);
                _root.Children.Add(row);
            }
        }

        private DatePicker BuildPicker()
        {
            var dp = new DatePicker { };
            dp.SetResourceReference(DatePicker.FontSizeProperty,  "LemoineFS_MD");
            dp.SetResourceReference(DatePicker.HeightProperty,   "LemoineH_Input");
            dp.SetResourceReference(DatePicker.BackgroundProperty,  "LemoineSelectBg");
            dp.SetResourceReference(DatePicker.ForegroundProperty,  "LemoineText");
            dp.SetResourceReference(DatePicker.BorderBrushProperty, "LemoineBorderMid");
            dp.SetResourceReference(DatePicker.FontFamilyProperty,  "LemoineMonoFont");
            return dp;
        }

        private TextBlock MiniLabel(string text)
        {
            var tb = new TextBlock
            {
                Text   = text,
                
                Margin = new Thickness(0, 0, 0, 3),
            };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }
    }
}
