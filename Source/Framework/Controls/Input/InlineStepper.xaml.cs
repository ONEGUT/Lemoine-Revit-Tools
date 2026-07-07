using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LemoineTools.Framework.Controls
{
    /// <summary>
    /// Compact "− [value] +" inline stepper with a TYPEABLE centre field — the house
    /// numeric input. Double-based; set <see cref="Decimals"/>=0 for integer behaviour.
    /// Value is clamped to [<see cref="MinValue"/>, <see cref="MaxValue"/>] and rounded to
    /// <see cref="Decimals"/>; the buttons step by <see cref="Step"/> and the centre field
    /// accepts typed input, committing on Enter or focus-loss (Esc reverts).
    /// Raises <see cref="ValueChanged"/> whenever Value changes through user interaction.
    /// </summary>
    public partial class InlineStepper : UserControl
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(InlineStepper),
                new FrameworkPropertyMetadata(0.0,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

        public static readonly DependencyProperty MinValueProperty =
            DependencyProperty.Register(nameof(MinValue), typeof(double), typeof(InlineStepper),
                new PropertyMetadata(0.0, OnBoundChanged));

        public static readonly DependencyProperty MaxValueProperty =
            DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(InlineStepper),
                new PropertyMetadata(100.0, OnBoundChanged));

        public static readonly DependencyProperty StepProperty =
            DependencyProperty.Register(nameof(Step), typeof(double), typeof(InlineStepper),
                new PropertyMetadata(1.0));

        public static readonly DependencyProperty DecimalsProperty =
            DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(InlineStepper),
                new PropertyMetadata(0, OnBoundChanged));

        public double Value    { get => (double)GetValue(ValueProperty);    set => SetValue(ValueProperty, value); }
        public double MinValue { get => (double)GetValue(MinValueProperty); set => SetValue(MinValueProperty, value); }
        public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }
        public double Step     { get => (double)GetValue(StepProperty);     set => SetValue(StepProperty, value); }
        public int    Decimals { get => (int)GetValue(DecimalsProperty);    set => SetValue(DecimalsProperty, value); }

        /// <summary>Width of the centre value field in px (set before the control loads). Default 40.</summary>
        public double ValueWidth { get; set; } = 40;

        /// <summary>Raised after Value changes through a button press or a committed edit.</summary>
        public event EventHandler<double>? ValueChanged;

        private TextBox? _valueBox;

        public InlineStepper()
        {
            InitializeComponent();
            Loaded += (s, e) => { if (_valueBox == null) Build(); RefreshValue(); };
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is InlineStepper s) s.RefreshValue();
        }
        private static void OnBoundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is InlineStepper s) s.RefreshValue();
        }

        private void Build()
        {
            _outer.SetResourceReference(FrameworkElement.HeightProperty, "LemoineH_Input");
            _outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var minus = MakeBtn("−");
            minus.MouseLeftButtonUp += (s, e) => { Bump(-Step); e.Handled = true; };

            _valueBox = new TextBox
            {
                Width                    = ValueWidth,
                TextAlignment            = TextAlignment.Center,
                VerticalAlignment        = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderThickness          = new Thickness(0),
                Background                = Brushes.Transparent,
                Padding                   = new Thickness(0),
            };
            _valueBox.SetResourceReference(TextBox.ForegroundProperty, "LemoineText");
            _valueBox.SetResourceReference(TextBox.FontFamilyProperty, "LemoineMonoFont");
            _valueBox.SetResourceReference(TextBox.FontSizeProperty,   "LemoineFS_SM");
            _valueBox.SetResourceReference(TextBox.CaretBrushProperty, "LemoineText");
            _valueBox.LostFocus += (s, e) => CommitText();
            _valueBox.KeyDown   += (s, e) =>
            {
                if (e.Key == Key.Enter)  { CommitText();  Keyboard.ClearFocus(); e.Handled = true; }
                if (e.Key == Key.Escape) { RefreshValue(); Keyboard.ClearFocus(); e.Handled = true; }
            };

            var plus = MakeBtn("+");
            plus.MouseLeftButtonUp += (s, e) => { Bump(+Step); e.Handled = true; };

            _root.Children.Add(minus);
            _root.Children.Add(MakeSep());
            _root.Children.Add(_valueBox);
            _root.Children.Add(MakeSep());
            _root.Children.Add(plus);
        }

        private Border MakeBtn(string glyph)
        {
            var b = new Border
            {
                Padding           = new Thickness(7, 0, 7, 0),
                Cursor            = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            b.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            var tb = new TextBlock
            {
                Text              = glyph,
                TextAlignment     = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible  = false,
            };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            b.Child = tb;
            b.MouseEnter += (s, e) => b.SetResourceReference(Border.BackgroundProperty, "LemoineAccentDim");
            b.MouseLeave += (s, e) => b.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            return b;
        }

        private Border MakeSep()
        {
            var sep = new Border { Width = 1, VerticalAlignment = VerticalAlignment.Stretch };
            sep.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
            return sep;
        }

        private void Bump(double delta) => CommitValue(Value + delta);

        private void CommitText()
        {
            if (_valueBox == null) return;
            string t = _valueBox.Text.Trim();
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) ||
                double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                CommitValue(v);
            else
                RefreshValue(); // revert unparseable input to the last good value
        }

        private void CommitValue(double v)
        {
            double min = MinValue, max = MaxValue;
            if (max < min) max = min;
            double clamped = Math.Round(Math.Max(min, Math.Min(max, v)), Math.Max(0, Decimals));
            bool changed = Math.Abs(clamped - Value) > 1e-9;
            Value = clamped;     // OnValueChanged → RefreshValue
            RefreshValue();
            if (changed) ValueChanged?.Invoke(this, clamped);
        }

        private void RefreshValue()
        {
            if (_valueBox == null) return;
            if (_valueBox.IsKeyboardFocusWithin) return; // never overwrite while the user types
            _valueBox.Text = Value.ToString("F" + Math.Max(0, Decimals), CultureInfo.CurrentCulture);
        }
    }
}
