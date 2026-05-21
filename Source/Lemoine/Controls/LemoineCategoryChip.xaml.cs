using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Pill button used for category chips in the legend palette.
    /// Optionally draggable. Active state inherits AccentColor as background.
    /// Optional accent dot (used for the "more ▾" overflow popup).
    /// </summary>
    public partial class LemoineCategoryChip : UserControl
    {
        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(LemoineCategoryChip),
                new PropertyMetadata("Chip", OnVisualChanged));

        public static readonly DependencyProperty ActiveProperty =
            DependencyProperty.Register(nameof(Active), typeof(bool), typeof(LemoineCategoryChip),
                new PropertyMetadata(false, OnVisualChanged));

        public static readonly DependencyProperty AccentColorProperty =
            DependencyProperty.Register(nameof(AccentColor), typeof(Color), typeof(LemoineCategoryChip),
                new PropertyMetadata(LemoineTheme.DarkMono.Accent.Color, OnVisualChanged));

        public static readonly DependencyProperty ShowChevProperty =
            DependencyProperty.Register(nameof(ShowChev), typeof(bool), typeof(LemoineCategoryChip),
                new PropertyMetadata(false, OnVisualChanged));

        public static readonly DependencyProperty ShowDotProperty =
            DependencyProperty.Register(nameof(ShowDot), typeof(bool), typeof(LemoineCategoryChip),
                new PropertyMetadata(false, OnVisualChanged));

        public static readonly DependencyProperty DraggableProperty =
            DependencyProperty.Register(nameof(Draggable), typeof(bool), typeof(LemoineCategoryChip),
                new PropertyMetadata(false));

        public string Label       { get => (string)GetValue(LabelProperty);       set => SetValue(LabelProperty, value); }
        public bool   Active      { get => (bool)GetValue(ActiveProperty);        set => SetValue(ActiveProperty, value); }
        public Color  AccentColor { get => (Color)GetValue(AccentColorProperty);  set => SetValue(AccentColorProperty, value); }
        public bool   ShowChev    { get => (bool)GetValue(ShowChevProperty);      set => SetValue(ShowChevProperty, value); }
        public bool   ShowDot     { get => (bool)GetValue(ShowDotProperty);       set => SetValue(ShowDotProperty, value); }
        public bool   Draggable   { get => (bool)GetValue(DraggableProperty);     set => SetValue(DraggableProperty, value); }

        public event EventHandler? Clicked;
        public event EventHandler<MouseEventArgs>? DragInitiated;

        private Point _dragStart;
        private bool  _mouseDown;

        public LemoineCategoryChip()
        {
            InitializeComponent();
            Loaded += (s, e) => Build();
            MouseLeftButtonDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseUp;
            MouseLeave += (s, e) => _mouseDown = false;
        }

        private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LemoineCategoryChip c && c.IsLoaded) c.Build();
        }

        private void Build()
        {
            // Border styling
            _outer.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            if (Active)
            {
                var bg = new SolidColorBrush(AccentColor); bg.Freeze();
                _outer.Background = bg;
            }
            else
            {
                _outer.SetResourceReference(Border.BackgroundProperty, "LemoineBg");
            }
            _outer.Cursor = Draggable ? Cursors.Hand : Cursors.Hand;

            _inner.Children.Clear();

            // Optional accent dot (used in overflow popup)
            if (ShowDot)
            {
                var dot = new Ellipse
                {
                    Width = 8, Height = 8,
                    Fill = new SolidColorBrush(AccentColor),
                    Stroke = null,
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                _inner.Children.Add(dot);
            }

            // Label
            var lbl = new TextBlock
            {
                Text = Label ?? "",
                VerticalAlignment = VerticalAlignment.Center,
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty,
                Active ? "LemoineBg" : "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _inner.Children.Add(lbl);

            // Chevron suffix for the "more ▾" overflow trigger
            if (ShowChev)
            {
                var chev = new TextBlock
                {
                    Text = " ▾",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, 0, 0, 0),
                };
                chev.SetResourceReference(TextBlock.ForegroundProperty,
                    Active ? "LemoineBg" : "LemoineTextSub");
                chev.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                chev.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                _inner.Children.Add(chev);
            }
        }

        // ── Mouse handling — click + drag-threshold ─────────────────────────
        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            _mouseDown = true;
            _dragStart = e.GetPosition(this);
        }
        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_mouseDown || !Draggable) return;
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _dragStart.X) > 6 || Math.Abs(pos.Y - _dragStart.Y) > 6)
            {
                _mouseDown = false;
                DragInitiated?.Invoke(this, e);
            }
        }
        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_mouseDown)
            {
                _mouseDown = false;
                Clicked?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
