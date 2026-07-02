using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LemoineTools.Lemoine;

namespace LemoineTools.Lemoine.Controls
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
    public partial class LemoineNumberRange : UserControl
    {
        public string MinLabel { get; set; } = LemoineStrings.T("controls.inputs.numberRange.defaultMinLabel");
        public string MaxLabel { get; set; } = LemoineStrings.T("controls.inputs.numberRange.defaultMaxLabel");
        public string Unit     { get; set; } = "";
        public double  Step    { get; set; } = 1;
        public double? AbsMin  { get; set; }
        public double? AbsMax  { get; set; }

        private TextBox? _minBox;
        private TextBox? _maxBox;

        public event Action<double?, double?>? RangeChanged;

        /// <summary>Accessible name announced by screen readers when the control receives focus.</summary>
        public string AccessibleName { set => AutomationProperties.SetName(this, value ?? string.Empty); }

        public LemoineNumberRange()
        {
            InitializeComponent();
            Loaded += (s, e) => Build();
        }

        public void SetValues(double? min, double? max)
        {
            if (_minBox != null) _minBox.Text = min.HasValue ? min.Value.ToString() : "";
            if (_maxBox != null) _maxBox.Text = max.HasValue ? max.Value.ToString() : "";
        }

        private void Build()
        {
            _root.ColumnDefinitions.Clear();
            _root.ColumnDefinitions.Add(new ColumnDefinition());
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _root.ColumnDefinitions.Add(new ColumnDefinition());

            Grid.SetColumn(BuildSide(MinLabel, ref _minBox, isMin: true), 0);
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

        private void FireChange()
        {
            double? min = double.TryParse(_minBox?.Text, out var mn) ? (double?)mn : null;
            double? max = double.TryParse(_maxBox?.Text, out var mx) ? (double?)mx : null;
            RangeChanged?.Invoke(min, max);
        }
    }
}
