using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LemoineTools.Framework;

namespace LemoineTools.Framework.Controls
{
    /// <summary>
    /// Min / Max numeric input pair.
    /// Mirrors JS NumberRange exactly.
    ///
    /// API:
    ///   MinLabel, MaxLabel, Unit   — display strings
    ///   Step, AbsMin, AbsMax       — numeric constraints (double, nullable)
    ///   SetValues(min, max)        — pre-populate
    ///   event RangeChanged(min, max) — fires on any change (double?, double?)
    /// </summary>
    public partial class NumberRange : UserControl
    {
        public string MinLabel { get; set; } = AppStrings.T("controls.inputs.numberRange.defaultMinLabel");
        public string MaxLabel { get; set; } = AppStrings.T("controls.inputs.numberRange.defaultMaxLabel");
        public string Unit     { get; set; } = "";
        public double  Step    { get; set; } = 1;
        public double? AbsMin  { get; set; }
        public double? AbsMax  { get; set; }

        private TextBox? _minBox;
        private TextBox? _maxBox;

        // Values requested via SetValues before Build() ran (Loaded hasn't fired yet, so the
        // text boxes don't exist). Applied once Build() creates them — otherwise the fields
        // render blank while the owner silently holds these values.
        private bool    _built;
        private double? _pendingMin;
        private double? _pendingMax;

        public event Action<double?, double?>? RangeChanged;

        /// <summary>Accessible name announced by screen readers when the control receives focus.</summary>
        public string AccessibleName { set => AutomationProperties.SetName(this, value ?? string.Empty); }

        public NumberRange()
        {
            InitializeComponent();
            Loaded += (s, e) => Build();
        }

        public void SetValues(double? min, double? max)
        {
            _pendingMin = min;
            _pendingMax = max;
            if (_built) ApplyPending();
        }

        private void ApplyPending()
        {
            if (_minBox != null) _minBox.Text = _pendingMin.HasValue ? _pendingMin.Value.ToString(CultureInfo.InvariantCulture) : "";
            if (_maxBox != null) _maxBox.Text = _pendingMax.HasValue ? _pendingMax.Value.ToString(CultureInfo.InvariantCulture) : "";
        }

        private void Build()
        {
            _root.ColumnDefinitions.Clear();
            _root.ColumnDefinitions.Add(new ColumnDefinition());
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _root.ColumnDefinitions.Add(new ColumnDefinition());

            _root.Children.Add(CreateChild(BuildSide(MinLabel, ref _minBox, isMin: true), 0));

            var dash = new TextBlock
            {
                Text = "–",

                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(10, 0, 10, 7),
            };
            dash.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _root.Children.Add(CreateChild(dash, 1));

            _root.Children.Add(CreateChild(BuildSide(MaxLabel, ref _maxBox, isMin: false), 2));

            _built = true;
            ApplyPending();   // apply any values set before the control was laid out (NR-1)
        }

        private UIElement CreateChild(UIElement el, int col)
        {
            Grid.SetColumn(el, col);
            return el;
        }

        private StackPanel BuildSide(string label, ref TextBox? box, bool isMin)
        {
            var sp = new StackPanel();

            var lbl = new TextBlock
            {
                Text   = label.ToUpper(),
                
                Margin = new Thickness(0, 0, 0, 4),
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            sp.Children.Add(lbl);

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            var tb = new TextBox
            {
                
                Padding  = new Thickness(7, 5, 7, 5),
                BorderThickness = new Thickness(1),
                TextAlignment = TextAlignment.Right,
                MinWidth = 70,
            };
            tb.SetResourceReference(TextBox.FontSizeProperty,  "LemoineFS_MD");
            tb.SetResourceReference(TextBox.BackgroundProperty,   "LemoineSelectBg");
            tb.SetResourceReference(TextBox.ForegroundProperty,   "LemoineText");
            tb.SetResourceReference(TextBox.BorderBrushProperty,  "LemoineBorderMid");
            tb.SetResourceReference(TextBox.FontFamilyProperty,   "LemoineMonoFont");
            tb.SetResourceReference(TextBox.CaretBrushProperty,   "LemoineText");

            box = tb;
            tb.TextChanged += (s, e) => FireChange();
            // On leaving the field, snap the visible text to the clamped value so what the user
            // sees always matches what the tool receives (no silent out-of-range or stale value).
            tb.LostFocus += (s, e) => SnapBox(tb);

            row.Children.Add(tb);

            if (!string.IsNullOrEmpty(Unit))
            {
                var unitText = new TextBlock
                {
                    Text  = Unit,
                    
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 0, 0),
                };
                unitText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                row.Children.Add(unitText);
            }

            sp.Children.Add(row);
            return sp;
        }

        // Parse a box invariant-first (decimal '.'), then the active culture, so "10.5" is
        // accepted on a comma-decimal locale instead of silently failing and keeping the old value.
        private static double? ParseBox(TextBox? box)
        {
            string? t = box?.Text;
            if (string.IsNullOrWhiteSpace(t)) return null;
            if (double.TryParse(t, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var v)) return v;
            if (double.TryParse(t, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture,  out v))      return v;
            return null;
        }

        // Clamp to the declared [AbsMin, AbsMax] so a typo (e.g. 0.01 ft) can't drive the owner
        // into a runaway range — AbsMin/AbsMax were previously declared but never enforced.
        private double Clamp(double v)
        {
            if (AbsMin.HasValue && v < AbsMin.Value) v = AbsMin.Value;
            if (AbsMax.HasValue && v > AbsMax.Value) v = AbsMax.Value;
            return v;
        }

        private void SnapBox(TextBox tb)
        {
            var v = ParseBox(tb);
            if (!v.HasValue) return;
            string snapped = Clamp(v.Value).ToString(CultureInfo.InvariantCulture);
            if (tb.Text != snapped) tb.Text = snapped;   // re-fires TextChanged → emits the clamped value
        }

        private void FireChange()
        {
            double? min = ParseBox(_minBox);
            double? max = ParseBox(_maxBox);
            RangeChanged?.Invoke(
                min.HasValue ? (double?)Clamp(min.Value) : null,
                max.HasValue ? (double?)Clamp(max.Value) : null);
        }
    }
}
