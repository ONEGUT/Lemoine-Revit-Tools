using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using IOPath = System.IO.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Xml.Linq;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Reusable color-picker control matching the Lemoine design screenshot.
    ///
    /// Layout:
    ///   ┌──────────────────────────┐  ┌──────────────┐
    ///   │  SV gradient map         │  │ HEX [......] │
    ///   │                          │  │ R  G  B  A   │
    ///   │                          │  │ Project Colors│
    ///   │                          │  │ Recent Colors │
    ///   ├──────────────────────────┤  └──────────────┘
    ///   │ ● [==hue rainbow======]  │
    ///   │   [==alpha gradient===]  │
    ///   └──────────────────────────┘
    ///
    /// State is stored internally as HSV + alpha.
    /// <see cref="SelectedColor"/> is the dependency property the host binds to.
    /// </summary>
    public partial class LemoineColorPickerPanel : UserControl
    {
        // ── Config ───────────────────────────────────────────────────────────
        public static int MaxProjectColors { get; set; } = 6;
        private const  int MaxRecentColors  = 8;
        private const  int ProjectSwatchSize = 30;
        private const  int RecentSwatchSize  = 22;

        private static readonly string PersistPath =
            IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           "LemoineTools", "colorpicker.xml");

        // ── DPs ─────────────────────────────────────────────────────────────
        public static readonly DependencyProperty SelectedColorProperty =
            DependencyProperty.Register(nameof(SelectedColor), typeof(Color), typeof(LemoineColorPickerPanel),
                new FrameworkPropertyMetadata(Colors.Red,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

        public Color SelectedColor
        {
            get => (Color)GetValue(SelectedColorProperty);
            set => SetValue(SelectedColorProperty, value);
        }

        /// <summary>Raised after the user changes the color via any control.</summary>
        public event EventHandler<Color>? ColorChanged;

        // ── HSV + Alpha state ────────────────────────────────────────────────
        private double _h = 0;    // 0..360
        private double _s = 1.0;  // 0..1
        private double _v = 1.0;  // 0..1
        private double _a = 1.0;  // 0..1

        // ── Dimensions ───────────────────────────────────────────────────────
        private const int SvW     = 280;
        private const int SvH     = 220;
        private const int SliderH = 16;
        private const int PreviewD = 34; // diameter of preview circle

        // ── UI references ────────────────────────────────────────────────────
        private Image?   _svImg;
        private Canvas?  _svOverlay;
        private Ellipse? _svCursor;

        private Grid?    _hueHost;
        private Image?   _hueImg;
        private Canvas?  _hueOverlay;
        private Ellipse? _hueCursor;

        private Grid?    _alphaHost;
        private Image?   _alphaImg;
        private Canvas?  _alphaOverlay;
        private Ellipse? _alphaCursor;

        private Ellipse? _previewCircle;
        private TextBox? _hexBox;
        private TextBox? _rBox;
        private TextBox? _gBox;
        private TextBox? _bBox;
        private TextBox? _aBox;

        private WrapPanel? _projectSwatchRow;
        private WrapPanel? _recentSwatchRow;

        private bool _internalUpdate;

        // ── Persistence state ────────────────────────────────────────────────
        private List<Color> _projectColors = new List<Color>();
        private List<Color> _recentColors  = new List<Color>();

        public LemoineColorPickerPanel()
        {
            InitializeComponent();
            LoadPersisted();
            Loaded += (s, e) =>
            {
                Build();
                FromColor(SelectedColor);
                RefreshAll();
            };
        }

        private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LemoineColorPickerPanel p && !p._internalUpdate)
            {
                p.FromColor((Color)e.NewValue);
                p.RefreshAll();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Persistence
        // ─────────────────────────────────────────────────────────────────────
        private void LoadPersisted()
        {
            try
            {
                if (!File.Exists(PersistPath)) return;
                var doc = XDocument.Load(PersistPath);

                _projectColors = doc.Root?.Element("ProjectColors")?
                    .Elements("Color")
                    .Select(el => TryParseHexColor(el.Value))
                    .Where(c => c.HasValue)
                    .Select(c => c!.Value)
                    .Take(MaxProjectColors)
                    .ToList() ?? new List<Color>();

                _recentColors = doc.Root?.Element("RecentColors")?
                    .Elements("Color")
                    .Select(el => TryParseHexColor(el.Value))
                    .Where(c => c.HasValue)
                    .Select(c => c!.Value)
                    .Take(MaxRecentColors)
                    .ToList() ?? new List<Color>();
            }
            catch { /* silently ignore corrupt data */ }
        }

        private void SavePersisted()
        {
            try
            {
                Directory.CreateDirectory(IOPath.GetDirectoryName(PersistPath)!);
                var doc = new XDocument(
                    new XElement("LemoineColorPicker",
                        new XElement("ProjectColors",
                            _projectColors.Select(c => new XElement("Color", ColorToHex(c)))),
                        new XElement("RecentColors",
                            _recentColors.Select(c => new XElement("Color", ColorToHex(c))))));
                doc.Save(PersistPath);
            }
            catch { }
        }

        private static Color? TryParseHexColor(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            try { return (Color)ColorConverter.ConvertFromString(s); }
            catch { return null; }
        }

        private static string ColorToHex(Color c) =>
            $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>
        /// Add the given color to the Recent Colors list and persist.
        /// Call this when the user confirms a selection (Apply Color).
        /// </summary>
        public void AddToRecent(Color c)
        {
            _recentColors.RemoveAll(x => x == c);
            _recentColors.Insert(0, c);
            if (_recentColors.Count > MaxRecentColors)
                _recentColors.RemoveRange(MaxRecentColors, _recentColors.Count - MaxRecentColors);
            SavePersisted();
            RefreshRecentSwatches();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Build UI
        // ─────────────────────────────────────────────────────────────────────
        private void Build()
        {
            _root.Children.Clear();
            _root.RowDefinitions.Clear();
            _root.ColumnDefinitions.Clear();

            // Two-column layout: [left: SV+sliders] [gap] [right: inputs+swatches]
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // 0 – left
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });  // 1 – gap
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) }); // 2 – right
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            BuildLeftColumn();
            BuildRightColumn();
        }

        // ── Left column ───────────────────────────────────────────────────────
        private void BuildLeftColumn()
        {
            var left = new StackPanel { Width = SvW };
            Grid.SetColumn(left, 0);
            _root.Children.Add(left);

            // SV map
            var svHost = new Grid { Width = SvW, Height = SvH, ClipToBounds = true, Cursor = Cursors.Cross };
            _svImg = new Image { Width = SvW, Height = SvH, Stretch = Stretch.Fill, SnapsToDevicePixels = true };
            svHost.Children.Add(_svImg);
            _svOverlay = new Canvas { IsHitTestVisible = false };
            _svCursor = new Ellipse
            {
                Width = 14, Height = 14,
                Stroke = Brushes.White,
                StrokeThickness = 2,
            };
            _svOverlay.Children.Add(_svCursor);
            svHost.Children.Add(_svOverlay);

            var svBorder = new Border
            {
                Child = svHost,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
            };
            svBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            svHost.MouseLeftButtonDown += SvMouseDown;
            svHost.MouseMove           += SvMouseMove;
            svHost.MouseLeftButtonUp   += SvMouseUp;
            left.Children.Add(svBorder);

            left.Children.Add(new Border { Height = 10 });

            // Preview circle + Hue slider row
            var sliderRow = new Grid();
            sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // preview
            sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            sliderRow.ColumnDefinitions.Add(new ColumnDefinition());                            // hue slider

            _previewCircle = new Ellipse
            {
                Width  = PreviewD,
                Height = PreviewD,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(_previewCircle, 0);
            sliderRow.Children.Add(_previewCircle);

            var hueSlider = BuildHueSlider();
            hueSlider.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(hueSlider, 2);
            sliderRow.Children.Add(hueSlider);

            left.Children.Add(sliderRow);
            left.Children.Add(new Border { Height = 8 });

            // Alpha slider row (indented to align with hue slider)
            var alphaRow = new Grid();
            alphaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PreviewD + 10) });
            alphaRow.ColumnDefinitions.Add(new ColumnDefinition());

            var alphaSlider = BuildAlphaSlider();
            alphaSlider.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(alphaSlider, 1);
            alphaRow.Children.Add(alphaSlider);

            left.Children.Add(alphaRow);

            // Render bitmaps now that refs are assigned
            RenderSvBitmap();
            RenderHueBitmap();
            RenderAlphaBitmap();
        }

        private FrameworkElement BuildHueSlider()
        {
            _hueHost = new Grid { Height = SliderH, ClipToBounds = true, Cursor = Cursors.Hand };
            _hueImg  = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
            _hueHost.Children.Add(_hueImg);
            _hueOverlay = new Canvas { IsHitTestVisible = false };
            _hueCursor  = new Ellipse
            {
                Width  = SliderH + 4,
                Height = SliderH + 4,
                Stroke = Brushes.White,
                StrokeThickness = 2,
            };
            _hueOverlay.Children.Add(_hueCursor);
            _hueHost.Children.Add(_hueOverlay);
            _hueHost.MouseLeftButtonDown += HueMouseDown;
            _hueHost.MouseMove           += HueMouseMove;
            _hueHost.MouseLeftButtonUp   += HueMouseUp;

            var border = new Border
            {
                Child = _hueHost,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(SliderH / 2.0),
                ClipToBounds = true,
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            return border;
        }

        private FrameworkElement BuildAlphaSlider()
        {
            _alphaHost = new Grid { Height = SliderH, ClipToBounds = true, Cursor = Cursors.Hand };

            // Checkered underlay
            var checker = new Rectangle { Fill = MakeCheckerBrush() };
            _alphaHost.Children.Add(checker);

            // Color-to-transparent gradient overlay
            _alphaImg = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
            _alphaHost.Children.Add(_alphaImg);

            _alphaOverlay = new Canvas { IsHitTestVisible = false };
            _alphaCursor  = new Ellipse
            {
                Width  = SliderH + 4,
                Height = SliderH + 4,
                Stroke = Brushes.White,
                StrokeThickness = 2,
            };
            _alphaOverlay.Children.Add(_alphaCursor);
            _alphaHost.Children.Add(_alphaOverlay);

            _alphaHost.MouseLeftButtonDown += AlphaMouseDown;
            _alphaHost.MouseMove           += AlphaMouseMove;
            _alphaHost.MouseLeftButtonUp   += AlphaMouseUp;

            var border = new Border
            {
                Child = _alphaHost,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(SliderH / 2.0),
                ClipToBounds = true,
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            return border;
        }

        private static DrawingBrush MakeCheckerBrush()
        {
            var drawing = new DrawingGroup();
            var light = new SolidColorBrush(Colors.White);              light.Freeze();
            var dark  = new SolidColorBrush(Color.FromRgb(200,200,200)); dark.Freeze();
            drawing.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            drawing.Children.Add(new GeometryDrawing(dark,  null, new RectangleGeometry(new Rect(0, 0, 4, 4))));
            drawing.Children.Add(new GeometryDrawing(dark,  null, new RectangleGeometry(new Rect(4, 4, 4, 4))));
            drawing.Freeze();
            return new DrawingBrush
            {
                Drawing        = drawing,
                TileMode       = TileMode.Tile,
                Viewport       = new Rect(0, 0, 8, 8),
                ViewportUnits  = BrushMappingMode.Absolute,
                Stretch        = Stretch.None,
            };
        }

        // ── Right column ──────────────────────────────────────────────────────
        private void BuildRightColumn()
        {
            var right = new StackPanel();
            Grid.SetColumn(right, 2);
            _root.Children.Add(right);

            // ── HEX ──────────────────────────────────────────────────────────
            var hexLbl = MakeLabel("HEX");
            hexLbl.Margin = new Thickness(0, 0, 0, 5);
            right.Children.Add(hexLbl);

            // Rounded box: [# label][text input]
            var hexBox = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(0),
            };
            hexBox.SetResourceReference(Border.BackgroundProperty,  "LemoineSelectBg");
            hexBox.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var hexInner = new StackPanel { Orientation = Orientation.Horizontal };
            var hashLbl = MakeLabel("#");
            hashLbl.Margin = new Thickness(8, 0, 2, 0);
            hashLbl.VerticalAlignment = VerticalAlignment.Center;
            _hexBox = new TextBox
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0, 7, 8, 7),
                VerticalContentAlignment = VerticalAlignment.Center,
                MaxLength = 8,
            };
            _hexBox.SetResourceReference(TextBox.ForegroundProperty,  "LemoineText");
            _hexBox.SetResourceReference(TextBox.FontFamilyProperty,  "LemoineMonoFont");
            _hexBox.SetResourceReference(TextBox.FontSizeProperty,    "LemoineFS_SM");
            _hexBox.SetResourceReference(TextBox.CaretBrushProperty,  "LemoineText");
            _hexBox.LostFocus += (s, e) => CommitHex();
            _hexBox.KeyDown   += (s, e) => { if (e.Key == Key.Enter) { CommitHex(); e.Handled = true; } };
            hexInner.Children.Add(hashLbl);
            hexInner.Children.Add(_hexBox);
            hexBox.Child = hexInner;
            right.Children.Add(hexBox);

            right.Children.Add(new Border { Height = 12 });

            // ── RGBA inputs ───────────────────────────────────────────────────
            var rgbaGrid = new Grid();
            // 4 equal columns + 3 small gaps
            for (int i = 0; i < 4; i++)
            {
                rgbaGrid.ColumnDefinitions.Add(new ColumnDefinition());
                if (i < 3)
                    rgbaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            }
            rgbaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // labels
            rgbaGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            rgbaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // inputs

            string[] rgbaLabels = { "R", "G", "B", "A" };
            int[] labelCols = { 0, 2, 4, 6 };

            foreach (var (lbl, col) in rgbaLabels.Zip(labelCols, (lbl2, col2) => (lbl2, col2)))
            {
                var tb = MakeLabel(lbl);
                tb.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetColumn(tb, col);
                Grid.SetRow(tb, 0);
                rgbaGrid.Children.Add(tb);
            }

            TextBox[] boxes = { MakeNumBox(), MakeNumBox(), MakeNumBox(), MakeNumBox() };
            _rBox = boxes[0]; _gBox = boxes[1]; _bBox = boxes[2]; _aBox = boxes[3];

            for (int i = 0; i < boxes.Length; i++)
            {
                Grid.SetColumn(boxes[i], labelCols[i]);
                Grid.SetRow(boxes[i], 2);
                rgbaGrid.Children.Add(boxes[i]);
            }

            foreach (var b in boxes) { b.LostFocus += (s, e) => CommitRgba(); }
            foreach (var b in boxes) { b.KeyDown   += (s, e) => { if (e.Key == Key.Enter) { CommitRgba(); e.Handled = true; } }; }

            right.Children.Add(rgbaGrid);

            // ── Separator ─────────────────────────────────────────────────────
            var sep = new Border { Height = 1, Margin = new Thickness(0, 16, 0, 14) };
            sep.SetResourceReference(Border.BackgroundProperty, "LemoineBorderMid");
            right.Children.Add(sep);

            // ── Project Colors ────────────────────────────────────────────────
            var projLbl = MakeLabel("Project");
            projLbl.Margin = new Thickness(0, 0, 0, 6);
            right.Children.Add(projLbl);

            _projectSwatchRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };
            right.Children.Add(_projectSwatchRow);

            right.Children.Add(new Border { Height = 14 });

            // ── Recent Colors ─────────────────────────────────────────────────
            var recentHeader = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            recentHeader.ColumnDefinitions.Add(new ColumnDefinition());
            recentHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var recentLbl = MakeLabel("Recent");
            Grid.SetColumn(recentLbl, 0);
            recentHeader.Children.Add(recentLbl);

            var clearBtn = new TextBlock
            {
                Text = "Clear",
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
            };
            clearBtn.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            clearBtn.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            clearBtn.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            clearBtn.MouseLeftButtonUp += (s, e) => ClearRecentColors();
            Grid.SetColumn(clearBtn, 1);
            recentHeader.Children.Add(clearBtn);

            right.Children.Add(recentHeader);

            _recentSwatchRow = new WrapPanel();
            right.Children.Add(_recentSwatchRow);

            // Populate swatch rows
            RefreshProjectSwatches();
            RefreshRecentSwatches();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Bitmaps
        // ─────────────────────────────────────────────────────────────────────
        private void RenderSvBitmap()
        {
            const int w = SvW, h = SvH;
            var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            int stride = w * 4;
            byte[] data = new byte[stride * h];
            for (int y = 0; y < h; y++)
            {
                double v = 1.0 - (y / (double)(h - 1));
                for (int x = 0; x < w; x++)
                {
                    double s = x / (double)(w - 1);
                    var c = HsvToRgb(_h, s, v);
                    int i = y * stride + x * 4;
                    data[i + 0] = c.B;
                    data[i + 1] = c.G;
                    data[i + 2] = c.R;
                    data[i + 3] = 255;
                }
            }
            wb.WritePixels(new Int32Rect(0, 0, w, h), data, stride, 0);
            if (_svImg != null) _svImg.Source = wb;
        }

        private void RenderHueBitmap()
        {
            // 1-pixel-tall horizontal rainbow; Image will stretch to fill.
            const int w = 360, h = 1;
            var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            int stride = w * 4;
            byte[] data = new byte[stride * h];
            for (int x = 0; x < w; x++)
            {
                double hue = x * 360.0 / (w - 1);
                var c = HsvToRgb(hue, 1.0, 1.0);
                int i = x * 4;
                data[i + 0] = c.B;
                data[i + 1] = c.G;
                data[i + 2] = c.R;
                data[i + 3] = 255;
            }
            wb.WritePixels(new Int32Rect(0, 0, w, h), data, stride, 0);
            if (_hueImg != null) _hueImg.Source = wb;
        }

        private void RenderAlphaBitmap()
        {
            // Horizontal gradient: transparent current-hue-color → opaque.
            const int w = 256, h = 1;
            var rgb = HsvToRgb(_h, _s, _v);
            var wb  = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            int stride = w * 4;
            byte[] data = new byte[stride * h];
            for (int x = 0; x < w; x++)
            {
                double t = x / (double)(w - 1);
                int i = x * 4;
                data[i + 0] = rgb.B;
                data[i + 1] = rgb.G;
                data[i + 2] = rgb.R;
                data[i + 3] = (byte)Math.Round(t * 255);
            }
            wb.WritePixels(new Int32Rect(0, 0, w, h), data, stride, 0);
            if (_alphaImg != null) _alphaImg.Source = wb;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Mouse handling – SV map
        // ─────────────────────────────────────────────────────────────────────
        private bool _svDragging;

        private void SvMouseDown(object sender, MouseButtonEventArgs e)
        {
            _svDragging = true;
            ((UIElement)sender).CaptureMouse();
            UpdateSvFromPoint(e.GetPosition((IInputElement)sender));
        }
        private void SvMouseMove(object sender, MouseEventArgs e)
        {
            if (_svDragging) UpdateSvFromPoint(e.GetPosition((IInputElement)sender));
        }
        private void SvMouseUp(object sender, MouseButtonEventArgs e)
        {
            _svDragging = false;
            ((UIElement)sender).ReleaseMouseCapture();
        }
        private void UpdateSvFromPoint(Point p)
        {
            _s = Math.Max(0, Math.Min(1, p.X / (SvW - 1)));
            _v = Math.Max(0, Math.Min(1, 1.0 - p.Y / (SvH - 1)));
            RenderAlphaBitmap();
            PushOut();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Mouse handling – Hue slider
        // ─────────────────────────────────────────────────────────────────────
        private bool _hueDragging;

        private void HueMouseDown(object sender, MouseButtonEventArgs e)
        {
            _hueDragging = true;
            ((UIElement)sender).CaptureMouse();
            UpdateHueFromPoint(e.GetPosition((IInputElement)sender), ((FrameworkElement)sender).ActualWidth);
        }
        private void HueMouseMove(object sender, MouseEventArgs e)
        {
            if (_hueDragging)
                UpdateHueFromPoint(e.GetPosition((IInputElement)sender), ((FrameworkElement)sender).ActualWidth);
        }
        private void HueMouseUp(object sender, MouseButtonEventArgs e)
        {
            _hueDragging = false;
            ((UIElement)sender).ReleaseMouseCapture();
        }
        private void UpdateHueFromPoint(Point p, double width)
        {
            _h = Math.Max(0, Math.Min(360, p.X * 360.0 / Math.Max(1, width - 1)));
            RenderSvBitmap();
            RenderAlphaBitmap();
            PushOut();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Mouse handling – Alpha slider
        // ─────────────────────────────────────────────────────────────────────
        private bool _alphaDragging;

        private void AlphaMouseDown(object sender, MouseButtonEventArgs e)
        {
            _alphaDragging = true;
            ((UIElement)sender).CaptureMouse();
            UpdateAlphaFromPoint(e.GetPosition((IInputElement)sender), ((FrameworkElement)sender).ActualWidth);
        }
        private void AlphaMouseMove(object sender, MouseEventArgs e)
        {
            if (_alphaDragging)
                UpdateAlphaFromPoint(e.GetPosition((IInputElement)sender), ((FrameworkElement)sender).ActualWidth);
        }
        private void AlphaMouseUp(object sender, MouseButtonEventArgs e)
        {
            _alphaDragging = false;
            ((UIElement)sender).ReleaseMouseCapture();
        }
        private void UpdateAlphaFromPoint(Point p, double width)
        {
            _a = Math.Max(0, Math.Min(1, p.X / Math.Max(1, width - 1)));
            PushOut();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Commit text inputs
        // ─────────────────────────────────────────────────────────────────────
        private void CommitHex()
        {
            if (_hexBox == null) return;
            var text = _hexBox.Text?.Trim() ?? "";
            if (!text.StartsWith("#", StringComparison.Ordinal)) text = "#" + text;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(text);
                // Preserve alpha if user typed 6-char hex (no alpha in string)
                if (text.Length == 7) c = Color.FromArgb((byte)Math.Round(_a * 255), c.R, c.G, c.B);
                FromColor(c);
                PushOut();
            }
            catch { RefreshAll(); }
        }

        private void CommitRgba()
        {
            byte r = ParseByte(_rBox?.Text, SelectedColor.R);
            byte g = ParseByte(_gBox?.Text, SelectedColor.G);
            byte b = ParseByte(_bBox?.Text, SelectedColor.B);
            // A box is 0-100 percentage
            double aPct = 100.0;
            if (int.TryParse(_aBox?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aRaw))
                aPct = Math.Max(0, Math.Min(100, aRaw));
            _a = aPct / 100.0;
            FromColor(Color.FromArgb((byte)Math.Round(_a * 255), r, g, b));
            PushOut();
        }

        private static byte ParseByte(string? s, byte fallback)
        {
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return (byte)Math.Max(0, Math.Min(255, v));
            return fallback;
        }

        // ─────────────────────────────────────────────────────────────────────
        // State propagation
        // ─────────────────────────────────────────────────────────────────────
        private void FromColor(Color c)
        {
            _a = c.A / 255.0;
            RgbToHsv(c, out _h, out _s, out _v);
            RenderSvBitmap();
            RenderAlphaBitmap();
        }

        private void PushOut()
        {
            var rgb = HsvToRgb(_h, _s, _v);
            var c   = Color.FromArgb((byte)Math.Round(_a * 255), rgb.R, rgb.G, rgb.B);
            _internalUpdate = true;
            SelectedColor   = c;
            _internalUpdate = false;
            RefreshAll();
            ColorChanged?.Invoke(this, c);
        }

        private void RefreshAll()
        {
            var rgb = HsvToRgb(_h, _s, _v);
            var c   = Color.FromArgb((byte)Math.Round(_a * 255), rgb.R, rgb.G, rgb.B);

            // SV cursor (14×14, offset by half to centre on the point)
            if (_svCursor != null)
            {
                _svCursor.SetValue(Canvas.LeftProperty, _s * (SvW - 1) - 7.0);
                _svCursor.SetValue(Canvas.TopProperty,  (1.0 - _v) * (SvH - 1) - 7.0);
            }

            // Hue + Alpha cursors — need actual rendered width, defer to Render priority
            Dispatcher.BeginInvoke(new Action(PositionSliderCursors),
                System.Windows.Threading.DispatcherPriority.Render);

            // Preview circle
            if (_previewCircle != null)
            {
                var brush = new SolidColorBrush(c); brush.Freeze();
                _previewCircle.Fill = brush;
            }

            // HEX (6-char RRGGBB, # shown as static label)
            if (_hexBox != null && !_hexBox.IsFocused)
                _hexBox.Text = $"{c.R:X2}{c.G:X2}{c.B:X2}";

            // RGB
            if (_rBox != null && !_rBox.IsFocused) _rBox.Text = c.R.ToString(CultureInfo.InvariantCulture);
            if (_gBox != null && !_gBox.IsFocused) _gBox.Text = c.G.ToString(CultureInfo.InvariantCulture);
            if (_bBox != null && !_bBox.IsFocused) _bBox.Text = c.B.ToString(CultureInfo.InvariantCulture);
            // A as 0-100 percent
            if (_aBox != null && !_aBox.IsFocused)
                _aBox.Text = ((int)Math.Round(_a * 100)).ToString(CultureInfo.InvariantCulture);
        }

        private void PositionSliderCursors()
        {
            double halfCursor = (SliderH + 4) / 2.0;

            if (_hueHost != null && _hueOverlay != null && _hueCursor != null
                && _hueHost.ActualWidth > 0)
            {
                double hx = (_h / 360.0) * _hueHost.ActualWidth - halfCursor;
                _hueCursor.SetValue(Canvas.LeftProperty, hx);
                _hueCursor.SetValue(Canvas.TopProperty,  -(_hueCursor.Height - SliderH) / 2.0);
            }

            if (_alphaHost != null && _alphaOverlay != null && _alphaCursor != null
                && _alphaHost.ActualWidth > 0)
            {
                double ax = _a * _alphaHost.ActualWidth - halfCursor;
                _alphaCursor.SetValue(Canvas.LeftProperty, ax);
                _alphaCursor.SetValue(Canvas.TopProperty,  -(_alphaCursor.Height - SliderH) / 2.0);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Swatch management
        // ─────────────────────────────────────────────────────────────────────
        private void AddProjectColor()
        {
            if (_projectColors.Count >= MaxProjectColors) return;
            _projectColors.Add(SelectedColor);
            SavePersisted();
            RefreshProjectSwatches();
        }

        private void ClearRecentColors()
        {
            _recentColors.Clear();
            SavePersisted();
            RefreshRecentSwatches();
        }

        private void RefreshProjectSwatches()
        {
            if (_projectSwatchRow == null) return;
            _projectSwatchRow.Children.Clear();

            foreach (var c in _projectColors.ToList())
            {
                var swatch = MakeColorSwatch(c, ProjectSwatchSize);
                swatch.ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                var captured = c;
                swatch.MouseLeftButtonUp += (s, e) => { FromColor(captured); PushOut(); };

                var menu = new ContextMenu();
                var copyItem   = new MenuItem { Header = "Copy hex" };
                var removeItem = new MenuItem { Header = "Remove from project" };
                copyItem.Click   += (s2, e2) => Clipboard.SetText($"#{captured.R:X2}{captured.G:X2}{captured.B:X2}");
                removeItem.Click += (s2, e2) => { _projectColors.Remove(captured); SavePersisted(); RefreshProjectSwatches(); };
                menu.Items.Add(copyItem);
                menu.Items.Add(removeItem);
                swatch.ContextMenu = menu;

                _projectSwatchRow.Children.Add(swatch);
            }

            if (_projectColors.Count < MaxProjectColors)
                _projectSwatchRow.Children.Add(MakeAddSwatch());
        }

        private void RefreshRecentSwatches()
        {
            if (_recentSwatchRow == null) return;
            _recentSwatchRow.Children.Clear();

            foreach (var c in _recentColors)
            {
                var swatch = MakeColorSwatch(c, RecentSwatchSize);
                swatch.ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                var captured = c;
                swatch.MouseLeftButtonUp += (s, e) => { FromColor(captured); PushOut(); };
                _recentSwatchRow.Children.Add(swatch);
            }
        }

        private static Border MakeColorSwatch(Color c, int size = 30)
        {
            var b = new Border
            {
                Width  = size,
                Height = size,
                CornerRadius    = new CornerRadius(size <= 24 ? 4 : 6),
                Margin          = new Thickness(0, 0, 4, 4),
                Background      = new SolidColorBrush(c),
                BorderThickness = new Thickness(1),
                Cursor          = Cursors.Hand,
                SnapsToDevicePixels = true,
            };
            b.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            return b;
        }

        private Border MakeAddSwatch()
        {
            var b = new Border
            {
                Width  = ProjectSwatchSize,
                Height = ProjectSwatchSize,
                CornerRadius    = new CornerRadius(6),
                Margin          = new Thickness(0, 0, 4, 4),
                BorderThickness = new Thickness(1),
                Cursor          = Cursors.Hand,
                SnapsToDevicePixels = true,
            };
            b.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            b.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            var plus = new TextBlock
            {
                Text = "+",
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            plus.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            b.Child = plus;
            b.MouseLeftButtonUp += (s, e) => AddProjectColor();
            return b;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Color math (unchanged)
        // ─────────────────────────────────────────────────────────────────────
        internal static Color HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            s = Math.Max(0, Math.Min(1, s));
            v = Math.Max(0, Math.Min(1, v));
            double c  = v * s;
            double hp = h / 60.0;
            double x  = c * (1 - Math.Abs((hp % 2) - 1));
            double r1 = 0, g1 = 0, b1 = 0;
            if      (hp < 1) { r1 = c; g1 = x; }
            else if (hp < 2) { r1 = x; g1 = c; }
            else if (hp < 3) { g1 = c; b1 = x; }
            else if (hp < 4) { g1 = x; b1 = c; }
            else if (hp < 5) { r1 = x; b1 = c; }
            else             { r1 = c; b1 = x; }
            double m = v - c;
            return Color.FromRgb(
                (byte)Math.Round((r1 + m) * 255.0),
                (byte)Math.Round((g1 + m) * 255.0),
                (byte)Math.Round((b1 + m) * 255.0));
        }

        internal static void RgbToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d   = max - min;
            v = max;
            s = max == 0 ? 0 : d / max;
            if (d == 0) { h = 0; return; }
            if      (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else               h = 60 * (((r - g) / d) + 4);
            if (h < 0) h += 360;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Small helpers
        // ─────────────────────────────────────────────────────────────────────
        private static TextBlock MakeLabel(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        private static TextBox MakeNumBox()
        {
            var b = new TextBox
            {
                Padding  = new Thickness(4, 6, 4, 6),
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                MaxLength = 3,
            };
            b.SetResourceReference(TextBox.BackgroundProperty,  "LemoineSelectBg");
            b.SetResourceReference(TextBox.ForegroundProperty,  "LemoineText");
            b.SetResourceReference(TextBox.BorderBrushProperty, "LemoineBorder");
            b.SetResourceReference(TextBox.FontFamilyProperty,  "LemoineMonoFont");
            b.SetResourceReference(TextBox.FontSizeProperty,    "LemoineFS_SM");
            b.SetResourceReference(TextBox.CaretBrushProperty,  "LemoineText");
            return b;
        }
    }
}
