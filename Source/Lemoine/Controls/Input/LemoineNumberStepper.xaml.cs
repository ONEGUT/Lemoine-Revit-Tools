using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Compact "− value +" integer stepper. Distinct from <see cref="LemoineNumberRange"/>
    /// which is a two-handle min/max control.
    ///
    /// Bindable Value; clamped to [Min, Max]. Step applies to both buttons.
    /// Raises <see cref="ValueChanged"/> after a user click that changes Value.
    /// </summary>
    public partial class LemoineNumberStepper : UserControl
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(int), typeof(LemoineNumberStepper),
                new FrameworkPropertyMetadata(0,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

        public static readonly DependencyProperty MinValueProperty =
            DependencyProperty.Register(nameof(MinValue), typeof(int), typeof(LemoineNumberStepper),
                new PropertyMetadata(0, OnBoundChanged));

        public static readonly DependencyProperty MaxValueProperty =
            DependencyProperty.Register(nameof(MaxValue), typeof(int), typeof(LemoineNumberStepper),
                new PropertyMetadata(100, OnBoundChanged));

        public static readonly DependencyProperty StepProperty =
            DependencyProperty.Register(nameof(Step), typeof(int), typeof(LemoineNumberStepper),
                new PropertyMetadata(1));

        public int Value     { get => (int)GetValue(ValueProperty);     set => SetValue(ValueProperty, value); }
        public int MinValue  { get => (int)GetValue(MinValueProperty);  set => SetValue(MinValueProperty, value); }
        public int MaxValue  { get => (int)GetValue(MaxValueProperty);  set => SetValue(MaxValueProperty, value); }
        public int Step      { get => (int)GetValue(StepProperty);      set => SetValue(StepProperty, value); }

        public event EventHandler<int>? ValueChanged;

        private TextBlock? _valueTb;
        private Button?    _minusBtn;
        private Button?    _plusBtn;

        public LemoineNumberStepper()
        {
            InitializeComponent();
            Loaded += (s, e) => { Build(); RefreshValue(); };
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LemoineNumberStepper s) s.RefreshValue();
        }

        private static void OnBoundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LemoineNumberStepper s) s.RefreshValue();
        }

        private void Build()
        {
            _outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _outer.SetResourceReference(Border.BackgroundProperty,  "LemoineSelectBg");

            _root.ColumnDefinitions.Clear();
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _root.ColumnDefinitions.Add(new ColumnDefinition());
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _minusBtn = MakeBtn("−");
            _minusBtn.Click += (s, e) => SetValueClamped(Value - Math.Max(1, Step));
            Grid.SetColumn(_minusBtn, 0);
            _root.Children.Add(_minusBtn);

            _valueTb = new TextBlock
            {
                Text = Value.ToString(),
                MinWidth = 22,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0),
            };
            _valueTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            _valueTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            _valueTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Grid.SetColumn(_valueTb, 1);
            _root.Children.Add(_valueTb);

            _plusBtn = MakeBtn("+");
            _plusBtn.Click += (s, e) => SetValueClamped(Value + Math.Max(1, Step));
            Grid.SetColumn(_plusBtn, 2);
            _root.Children.Add(_plusBtn);
        }

        private Button MakeBtn(string glyph)
        {
            var b = new Button
            {
                Content     = glyph,
                Width       = 18,
                Background  = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor      = Cursors.Hand,
                Padding     = new Thickness(0),
                FocusVisualStyle = null,
            };
            b.SetResourceReference(Control.ForegroundProperty,  "LemoineText");
            b.SetResourceReference(Control.FontFamilyProperty,  "LemoineMonoFont");
            b.SetResourceReference(Control.FontSizeProperty,    "LemoineFS_MD");
            b.Template = LemoineControlStyles.BuildFlatButtonTemplate();
            return b;
        }

        private void SetValueClamped(int v)
        {
            int min = MinValue, max = MaxValue;
            if (max < min) max = min;
            int clamped = Math.Max(min, Math.Min(max, v));
            if (clamped != Value)
            {
                Value = clamped;
                ValueChanged?.Invoke(this, clamped);
            }
        }

        private void RefreshValue()
        {
            if (_valueTb != null) _valueTb.Text = Value.ToString();
            if (_minusBtn != null) _minusBtn.IsEnabled = Value > MinValue;
            if (_plusBtn  != null) _plusBtn.IsEnabled  = Value < MaxValue;
        }
    }
}
