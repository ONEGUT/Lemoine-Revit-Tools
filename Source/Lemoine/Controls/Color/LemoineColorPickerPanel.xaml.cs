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
    public partial class LemoineColorPickerPanel : UserControl
    {
        // ── Config ────────────────────────────────────────────────────────────
        public static int MaxProjectColors { get; set; } = 12; // kept for public compat
        private const int SetSlotCount      = 12;   // 6 cols × 2 rows
        private const int MaxRecentColors   = 8;
        private const int ProjectSwatchSize = 30;
        private const int RecentSwatchSize  = 22;
        private const int RightColWidth     = 204;  // 6 × (30px swatch + 4px margin)

        private static readonly string PersistPath =
            IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           "LemoineTools", "colorpicker.xml");

        // ── ColorSet model ────────────────────────────────────────────────────
        private class ColorSet
        {
            public string       Name;
            public List<Color?> Colors;

            public ColorSet(string name)
            {
                Name   = name;
                Colors = Enumerable.Repeat<Color?>(null, SetSlotCount).ToList();
            }
        }

        // ── DPs ───────────────────────────────────────────────────────────────
        public static readonly DependencyProperty SelectedColorProperty =
            DependencyProperty.Register(nameof(SelectedColor), typeof(Color), typeof(LemoineColorPickerPanel),
                new FrameworkPropertyMetadata(Colors.Red,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

        public Color SelectedColor
        {
            get => (Color)GetValue(SelectedColorProperty);
            set => SetValue(SelectedColorProperty, value);
        }

        public event EventHandler<Color>? ColorChanged;

        // ── HSV + Alpha state ─────────────────────────────────────────────────
        private double _h = 0;
        private double _s = 1.0;
        private double _v = 1.0;
        private double _a = 1.0;

        // ── Dimensions ────────────────────────────────────────────────────────
        private const int SvW      = 280;
        private const int SvH      = 220;
        private const int SliderH  = 16;
        private const int PreviewD = 34;

        // ── UI refs ───────────────────────────────────────────────────────────
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
        private TextBox? _rBox, _gBox, _bBox, _aBox;
        private WrapPanel?  _recentSwatchRow;
        private Grid?       _projectGrid;
        private Border?     _dropdownBtn;
        private TextBlock?  _dropdownBtnLabel;
        private Border?     _setDropdownPanel;
        private StackPanel? _setDropdownStack;
        private TextBox?    _newSetNameBox;
        private bool        _internalUpdate;

        // ── Data state ────────────────────────────────────────────────────────
        private List<ColorSet> _colorSets    = new List<ColorSet>();
        private int            _activeSetIdx = 0;
        private List<Color>    _recentColors = new List<Color>();

        // ─────────────────────────────────────────────────────────────────────
        // Constructor
        // ─────────────────────────────────────────────────────────────────────
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
                var doc  = XDocument.Load(PersistPath);
                var root = doc.Root;
                if (root == null) return;

                var setsEl = root.Element("ColorSets");
                if (setsEl != null)
                {
                    int activeIdx = 0;
                    int.TryParse(setsEl.Attribute("activeIndex")?.Value, out activeIdx);

                    foreach (var setEl in setsEl.Elements("Set"))
                    {
                        var name  = setEl.Attribute("name")?.Value ?? "Set";
                        var cs    = new ColorSet(name);
                        var slots = setEl.Elements("Slot").ToList();
                        for (int i = 0; i < Math.Min(slots.Count, SetSlotCount); i++)
                            cs.Colors[i] = string.IsNullOrEmpty(slots[i].Value)
                                ? (Color?)null
                                : TryParseHexColor(slots[i].Value);
                        _colorSets.Add(cs);
                    }

                    _activeSetIdx = Math.Max(0, Math.Min(activeIdx, _colorSets.Count - 1));
                }

                _recentColors = root.Element("RecentColors")?
                    .Elements("Color")
                    .Select(el => TryParseHexColor(el.Value))
                    .Where(c => c.HasValue)
                    .Select(c => c!.Value)
                    .Take(MaxRecentColors)
                    .ToList() ?? new List<Color>();
            }
            catch { }

            if (_colorSets.Count == 0)
                _colorSets.Add(new ColorSet("Set 1"));
        }

        private void SavePersisted()
        {
            try
            {
                Directory.CreateDirectory(IOPath.GetDirectoryName(PersistPath)!);
                var doc = new XDocument(
                    new XElement("LemoineColorPicker",
                        new XElement("ColorSets",
                            new XAttribute("activeIndex", _activeSetIdx),
                            _colorSets.Select(cs =>
                                new XElement("Set",
                                    new XAttribute("name", cs.Name),
                                    cs.Colors.Select(c =>
                                        new XElement("Slot", c.HasValue ? ColorToHex(c.Value) : ""))))),
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

        // ─────────────────────────────────────────────────────────────────────
        // Recent
        // ─────────────────────────────────────────────────────────────────────
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
        // Build
        // ─────────────────────────────────────────────────────────────────────
        private void Build()
        {
            _root.Children.Clear();
            _root.RowDefinitions.Clear();
            _root.ColumnDefinitions.Clear();

            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                // 0 – left
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });              // 1 – gap
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RightColWidth) });   // 2 – right (204px)
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 0 – main
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 1 – recent

            BuildLeftColumn();
            BuildRightColumn();
            BuildRecentRow();
        }

        // ── Left column ───────────────────────────────────────────────────────
        private void BuildLeftColumn()
        {
            var left = new StackPanel { Width = SvW };
            Grid.SetColumn(left, 0);
            Grid.SetRow(left, 0);
            _root.Children.Add(left);

            // SV map
            var svHost = new Grid { Width = SvW, Height = SvH, ClipToBounds = true, Cursor = Cursors.Cross };
            _svImg     = new Image { Width = SvW, Height = SvH, Stretch = Stretch.Fill, SnapsToDevicePixels = true };
            svHost.Children.Add(_svImg);
            _svOverlay = new Canvas { IsHitTestVisible = false };
            _svCursor  = new Ellipse { Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2 };
            _svOverlay.Children.Add(_svCursor);
            svHost.Children.Add(_svOverlay);

            var svBorder = new Border
            {
                Child           = svHost,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                ClipToBounds    = true,
            };
            svBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            svHost.MouseLeftButtonDown += SvMouseDown;
            svHost.MouseMove           += SvMouseMove;
            svHost.MouseLeftButtonUp   += SvMouseUp;
            left.Children.Add(svBorder);

            left.Children.Add(new Border { Height = 10 });

            // Preview + Hue slider row
            var sliderRow = new Grid();
            sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            sliderRow.ColumnDefinitions.Add(new ColumnDefinition());

            _previewCircle = new Ellipse
            {
                Width             = PreviewD,
                Height            = PreviewD,
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

            // Alpha slider row
            var alphaRow = new Grid();
            alphaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PreviewD + 10) });
            alphaRow.ColumnDefinitions.Add(new ColumnDefinition());

            var alphaSlider = BuildAlphaSlider();
            alphaSlider.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(alphaSlider, 1);
            alphaRow.Children.Add(alphaSlider);

            left.Children.Add(alphaRow);

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
                Width           = SliderH + 4,
                Height          = SliderH + 4,
                Stroke          = Brushes.White,
                StrokeThickness = 2,
            };
            _hueOverlay.Children.Add(_hueCursor);
            _hueHost.Children.Add(_hueOverlay);
            _hueHost.MouseLeftButtonDown += HueMouseDown;
            _hueHost.MouseMove           += HueMouseMove;
            _hueHost.MouseLeftButtonUp   += HueMouseUp;

            var border = new Border
            {
                Child           = _hueHost,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(SliderH / 2.0),
                ClipToBounds    = true,
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            return border;
        }

        private FrameworkElement BuildAlphaSlider()
        {
            _alphaHost = new Grid { Height = SliderH, ClipToBounds = true, Cursor = Cursors.Hand };

            var checker = new Rectangle { Fill = MakeCheckerBrush() };
            _alphaHost.Children.Add(checker);

            _alphaImg = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
            _alphaHost.Children.Add(_alphaImg);

            _alphaOverlay = new Canvas { IsHitTestVisible = false };
            _alphaCursor  = new Ellipse
            {
                Width           = SliderH + 4,
                Height          = SliderH + 4,
                Stroke          = Brushes.White,
                StrokeThickness = 2,
            };
            _alphaOverlay.Children.Add(_alphaCursor);
            _alphaHost.Children.Add(_alphaOverlay);

            _alphaHost.MouseLeftButtonDown += AlphaMouseDown;
            _alphaHost.MouseMove           += AlphaMouseMove;
            _alphaHost.MouseLeftButtonUp   += AlphaMouseUp;

            var border = new Border
            {
                Child           = _alphaHost,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(SliderH / 2.0),
                ClipToBounds    = true,
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            return border;
        }

        private static DrawingBrush MakeCheckerBrush()
        {
            var drawing = new DrawingGroup();
            var light = new SolidColorBrush(Colors.White);               light.Freeze();
            var dark  = new SolidColorBrush(Color.FromRgb(200, 200, 200)); dark.Freeze();
            drawing.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            drawing.Children.Add(new GeometryDrawing(dark,  null, new RectangleGeometry(new Rect(0, 0, 4, 4))));
            drawing.Children.Add(new GeometryDrawing(dark,  null, new RectangleGeometry(new Rect(4, 4, 4, 4))));
            drawing.Freeze();
            return new DrawingBrush
            {
                Drawing       = drawing,
                TileMode      = TileMode.Tile,
                Viewport      = new Rect(0, 0, 8, 8),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch       = Stretch.None,
            };
        }

        // ── Right column ──────────────────────────────────────────────────────
        private void BuildRightColumn()
        {
            var right = new StackPanel();
            Grid.SetColumn(right, 2);
            Grid.SetRow(right, 0);
            _root.Children.Add(right);

            // HEX
            var hexLbl = MakeLabel("HEX");
            hexLbl.Margin = new Thickness(0, 0, 0, 5);
            right.Children.Add(hexLbl);

            var hexBox = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(0),
            };
            hexBox.SetResourceReference(Border.BackgroundProperty,  "LemoineSelectBg");
            hexBox.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var hexInner = new StackPanel { Orientation = Orientation.Horizontal };
            var hashLbl  = MakeLabel("#");
            hashLbl.Margin            = new Thickness(8, 0, 2, 0);
            hashLbl.VerticalAlignment = VerticalAlignment.Center;
            _hexBox = new TextBox
            {
                BorderThickness          = new Thickness(0),
                Background               = Brushes.Transparent,
                Padding                  = new Thickness(0, 7, 8, 7),
                VerticalContentAlignment = VerticalAlignment.Center,
                MaxLength                = 8,
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

            // RGBA — * columns are safe: right StackPanel is inside 204px fixed Grid column
            var rgbaGrid = new Grid();
            for (int i = 0; i < 4; i++)
            {
                rgbaGrid.ColumnDefinitions.Add(new ColumnDefinition()); // ← * col, constrained by 204px column
                if (i < 3)
                    rgbaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            }
            rgbaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rgbaGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            rgbaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            string[] rgbaLabels = { "R", "G", "B", "A" };
            int[]    labelCols  = { 0, 2, 4, 6 };

            for (int i = 0; i < rgbaLabels.Length; i++)
            {
                var tb = MakeLabel(rgbaLabels[i]);
                tb.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetColumn(tb, labelCols[i]);
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

            foreach (var b in boxes) b.LostFocus += (s, e) => CommitRgba();
            foreach (var b in boxes) b.KeyDown   += (s, e) => { if (e.Key == Key.Enter) { CommitRgba(); e.Handled = true; } };

            right.Children.Add(rgbaGrid);

            // Separator
            var sep = new Border { Height = 1, Margin = new Thickness(0, 16, 0, 14) };
            sep.SetResourceReference(Border.BackgroundProperty, "LemoineBorderMid");
            right.Children.Add(sep);

            // Set-selector dropdown
            right.Children.Add(BuildSetDropdownButton());
            right.Children.Add(BuildSetDropdownPanel());
            right.Children.Add(new Border { Height = 6 });

            // 6-column × 2-row project swatch grid
            _projectGrid = new Grid();
            for (int col = 0; col < 6; col++)
                _projectGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int row = 0; row < 2; row++)
                _projectGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.Children.Add(_projectGrid);
            RefreshProjectGrid();
        }

        // ── Recent row — full width below both columns ────────────────────────
        private void BuildRecentRow()
        {
            var recentContainer = new StackPanel();
            Grid.SetRow(recentContainer, 1);
            Grid.SetColumn(recentContainer, 0);
            Grid.SetColumnSpan(recentContainer, 3); // spans left + gap + right
            _root.Children.Add(recentContainer);

            var sep = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 0) };
            sep.SetResourceReference(Border.BackgroundProperty, "LemoineBorderMid");
            recentContainer.Children.Add(sep);

            recentContainer.Children.Add(new Border { Height = 10 });

            var recentHeader = new Grid();
            recentHeader.ColumnDefinitions.Add(new ColumnDefinition());
            recentHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var recentLbl = MakeLabel("Recent");
            Grid.SetColumn(recentLbl, 0);
            recentHeader.Children.Add(recentLbl);

            var clearBtn = new TextBlock
            {
                Text              = "Clear",
                VerticalAlignment = VerticalAlignment.Center,
                Cursor            = Cursors.Hand,
            };
            clearBtn.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            clearBtn.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            clearBtn.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            clearBtn.MouseLeftButtonUp += (s, e) => ClearRecentColors();
            Grid.SetColumn(clearBtn, 1);
            recentHeader.Children.Add(clearBtn);

            recentContainer.Children.Add(recentHeader);
            recentContainer.Children.Add(new Border { Height = 8 });

            _recentSwatchRow = new WrapPanel();
            recentContainer.Children.Add(_recentSwatchRow);

            RefreshRecentSwatches();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Set dropdown
        // ─────────────────────────────────────────────────────────────────────
        private FrameworkElement BuildSetDropdownButton()
        {
            var btn = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(8, 5, 8, 5),
                Cursor          = Cursors.Hand,
            };
            btn.SetResourceReference(Border.BackgroundProperty,  "LemoineSelectBg");
            btn.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var inner = new Grid();
            inner.ColumnDefinitions.Add(new ColumnDefinition());
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _dropdownBtnLabel = MakeLabel(_colorSets[_activeSetIdx].Name);
            Grid.SetColumn(_dropdownBtnLabel, 0);
            inner.Children.Add(_dropdownBtnLabel);

            var chevron = new TextBlock
            {
                Text              = "▾",
                Margin            = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            chevron.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Grid.SetColumn(chevron, 1);
            inner.Children.Add(chevron);

            btn.Child    = inner;
            _dropdownBtn = btn;
            btn.MouseLeftButtonUp += (s, e) => OnDropdownButtonClick();
            return btn;
        }

        private void OnDropdownButtonClick()
        {
            if (_setDropdownPanel == null) return;
            if (_setDropdownPanel.Visibility == Visibility.Visible)
            {
                CollapseDropdown();
            }
            else
            {
                RefreshDropdownContent();
                _setDropdownPanel.Visibility = Visibility.Visible;
            }
        }

        private FrameworkElement BuildSetDropdownPanel()
        {
            _setDropdownStack = new StackPanel { MinWidth = RightColWidth };

            var panelBorder = new Border
            {
                Child           = _setDropdownStack,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(4),
                Margin          = new Thickness(0, 2, 0, 0),
                Visibility      = Visibility.Collapsed,
            };
            panelBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineBg");
            panelBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            _setDropdownPanel = panelBorder;
            return panelBorder;
        }

        private void CollapseDropdown()
        {
            if (_setDropdownPanel != null)
                _setDropdownPanel.Visibility = Visibility.Collapsed;
        }

        private void RefreshDropdownContent()
        {
            if (_setDropdownStack == null) return;
            _setDropdownStack.Children.Clear();

            int setIndex = 0;
            foreach (var cs in _colorSets)
            {
                var capturedIdx = setIndex;

                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var nameTb = MakeLabel(cs.Name);
                nameTb.Margin = new Thickness(4, 4, 4, 4);
                nameTb.Cursor = Cursors.Hand;
                nameTb.MouseLeftButtonUp += (s, e) =>
                {
                    SwitchToSet(capturedIdx);
                };
                Grid.SetColumn(nameTb, 0);
                row.Children.Add(nameTb);

                if (_colorSets.Count > 1)
                {
                    var trashBtn = new TextBlock
                    {
                        Text              = "×",
                        Margin            = new Thickness(0, 0, 4, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Cursor            = Cursors.Hand,
                    };
                    trashBtn.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
                    trashBtn.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    trashBtn.MouseLeftButtonUp += (s, e) =>
                    {
                        DeleteSet(capturedIdx);
                        e.Handled = true;
                    };
                    Grid.SetColumn(trashBtn, 1);
                    row.Children.Add(trashBtn);
                }

                var rowBorder = new Border { Child = row, CornerRadius = new CornerRadius(4), Padding = new Thickness(0) };
                if (setIndex == _activeSetIdx)
                    rowBorder.SetResourceReference(Border.BackgroundProperty, "LemoineSelectBg");
                else
                    rowBorder.Background = Brushes.Transparent;

                _setDropdownStack.Children.Add(rowBorder);
                setIndex++;
            }

            var sep2 = new Border { Height = 1, Margin = new Thickness(2, 4, 2, 4) };
            sep2.SetResourceReference(Border.BackgroundProperty, "LemoineBorderMid");
            _setDropdownStack.Children.Add(sep2);

            BuildNewSetRow();
        }

        private void BuildNewSetRow()
        {
            if (_setDropdownStack == null) return;

            var addLabel = new TextBlock
            {
                Text   = "+ New Set",
                Cursor = Cursors.Hand,
                Margin = new Thickness(8, 4, 8, 4),
            };
            addLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            addLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            addLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            var entryRow = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(4, 2, 4, 2) };
            entryRow.ColumnDefinitions.Add(new ColumnDefinition());
            entryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _newSetNameBox = new TextBox
            {
                Padding         = new Thickness(6, 4, 6, 4),
                BorderThickness = new Thickness(1),
                MaxLength       = 32,
            };
            _newSetNameBox.SetResourceReference(TextBox.BackgroundProperty,  "LemoineSelectBg");
            _newSetNameBox.SetResourceReference(TextBox.ForegroundProperty,  "LemoineText");
            _newSetNameBox.SetResourceReference(TextBox.BorderBrushProperty, "LemoineBorder");
            _newSetNameBox.SetResourceReference(TextBox.FontSizeProperty,    "LemoineFS_SM");
            _newSetNameBox.SetResourceReference(TextBox.FontFamilyProperty,  "LemoineMonoFont");
            _newSetNameBox.SetResourceReference(TextBox.CaretBrushProperty,  "LemoineText");
            Grid.SetColumn(_newSetNameBox, 0);
            entryRow.Children.Add(_newSetNameBox);

            var confirmBorder = new Border
            {
                Padding      = new Thickness(8, 4, 8, 4),
                CornerRadius = new CornerRadius(4),
                Margin       = new Thickness(4, 0, 0, 0),
                Cursor       = Cursors.Hand,
            };
            confirmBorder.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
            var confirmTb = new TextBlock { Text = "Add" };
            confirmTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            confirmTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            confirmTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            confirmBorder.Child = confirmTb;
            Grid.SetColumn(confirmBorder, 1);
            entryRow.Children.Add(confirmBorder);

            Action confirmCreate = () =>
            {
                var name = _newSetNameBox?.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(name)) name = $"Set {_colorSets.Count + 1}";
                CreateNewSet(name);
            };

            addLabel.MouseLeftButtonUp += (s, e) =>
            {
                addLabel.Visibility  = Visibility.Collapsed;
                entryRow.Visibility  = Visibility.Visible;
                _newSetNameBox?.Focus();
            };

            confirmBorder.MouseLeftButtonUp += (s, e) => confirmCreate();
            _newSetNameBox.KeyDown += (s, e) =>
            {
                if      (e.Key == Key.Enter)  { confirmCreate(); e.Handled = true; }
                else if (e.Key == Key.Escape) { CollapseDropdown(); e.Handled = true; }
            };

            var container = new StackPanel();
            container.Children.Add(addLabel);
            container.Children.Add(entryRow);
            _setDropdownStack.Children.Add(container);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Set management
        // ─────────────────────────────────────────────────────────────────────
        private void SwitchToSet(int idx)
        {
            _activeSetIdx = Math.Max(0, Math.Min(idx, _colorSets.Count - 1));
            CollapseDropdown();
            SavePersisted();
            RefreshProjectGrid();
            RefreshDropdownLabel();
        }

        private void CreateNewSet(string name)
        {
            _colorSets.Add(new ColorSet(name));
            _activeSetIdx = _colorSets.Count - 1;
            CollapseDropdown();
            SavePersisted();
            RefreshProjectGrid();
            RefreshDropdownLabel();
        }

        private void DeleteSet(int idx)
        {
            if (_colorSets.Count <= 1) return;
            _colorSets.RemoveAt(idx);
            _activeSetIdx = Math.Max(0, Math.Min(_activeSetIdx, _colorSets.Count - 1));
            SavePersisted();
            RefreshProjectGrid();
            RefreshDropdownLabel();
            RefreshDropdownContent();
        }

        private void RefreshDropdownLabel()
        {
            if (_dropdownBtnLabel != null && _colorSets.Count > 0)
                _dropdownBtnLabel.Text = _colorSets[_activeSetIdx].Name;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Project swatch grid (6 × 2)
        // ─────────────────────────────────────────────────────────────────────
        private void RefreshProjectGrid()
        {
            if (_projectGrid == null) return;
            _projectGrid.Children.Clear();

            var activeSet = _colorSets.Count > 0 ? _colorSets[_activeSetIdx] : null;

            for (int i = 0; i < SetSlotCount; i++)
            {
                var slotIdx = i;
                var color   = (activeSet != null && activeSet.Colors.Count > i) ? activeSet.Colors[i] : null;

                Border swatch;
                if (color.HasValue)
                {
                    var c  = color.Value;
                    swatch = MakeColorSwatch(c, ProjectSwatchSize);
                    swatch.ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2} · Right-click to remove";

                    // Left-click: load this color into the picker
                    swatch.MouseLeftButtonUp += (s, e) => { FromColor(c); PushOut(); };

                    // Right-click: context menu to remove the slot (ContextMenu is Revit-safe)
                    var removeItem = new MenuItem { Header = "Remove" };
                    removeItem.Click += (ms, me) =>
                    {
                        if (activeSet != null && activeSet.Colors.Count > slotIdx)
                            activeSet.Colors[slotIdx] = null;
                        SavePersisted();
                        RefreshProjectGrid();
                    };
                    swatch.ContextMenu = new ContextMenu { Items = { removeItem } };
                }
                else
                {
                    swatch         = MakeEmptySwatch();
                    swatch.ToolTip = "Click to save current color here";

                    // Left-click: fill this slot with the current color immediately
                    swatch.MouseLeftButtonUp += (s, e) =>
                    {
                        if (activeSet != null)
                        {
                            while (activeSet.Colors.Count <= slotIdx) activeSet.Colors.Add(null);
                            activeSet.Colors[slotIdx] = SelectedColor;
                        }
                        SavePersisted();
                        RefreshProjectGrid();
                    };
                }

                Grid.SetRow(swatch, i / 6);
                Grid.SetColumn(swatch, i % 6);
                _projectGrid.Children.Add(swatch);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Recent swatches
        // ─────────────────────────────────────────────────────────────────────
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

        private void ClearRecentColors()
        {
            _recentColors.Clear();
            SavePersisted();
            RefreshRecentSwatches();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Bitmaps
        // ─────────────────────────────────────────────────────────────────────
        private void RenderSvBitmap()
        {
            const int w = SvW, h = SvH;
            var wb     = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            int stride = w * 4;
            byte[] data = new byte[stride * h];
            for (int y = 0; y < h; y++)
            {
                double v = 1.0 - (y / (double)(h - 1));
                for (int x = 0; x < w; x++)
                {
                    double s = x / (double)(w - 1);
                    var c    = HsvToRgb(_h, s, v);
                    int i    = y * stride + x * 4;
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
            const int w = 360, h = 1;
            var wb     = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            int stride = w * 4;
            byte[] data = new byte[stride * h];
            for (int x = 0; x < w; x++)
            {
                double hue = x * 360.0 / (w - 1);
                var c      = HsvToRgb(hue, 1.0, 1.0);
                int i      = x * 4;
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
            const int w = 256, h = 1;
            var rgb    = HsvToRgb(_h, _s, _v);
            var wb     = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            int stride = w * 4;
            byte[] data = new byte[stride * h];
            for (int x = 0; x < w; x++)
            {
                double t = x / (double)(w - 1);
                int i    = x * 4;
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
                if (text.Length == 7) c = Color.FromArgb((byte)Math.Round(_a * 255), c.R, c.G, c.B);
                FromColor(c);
                PushOut();
            }
            catch { RefreshAll(); }
        }

        private void CommitRgba()
        {
            byte r     = ParseByte(_rBox?.Text, SelectedColor.R);
            byte g     = ParseByte(_gBox?.Text, SelectedColor.G);
            byte b     = ParseByte(_bBox?.Text, SelectedColor.B);
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

            if (_svCursor != null)
            {
                _svCursor.SetValue(Canvas.LeftProperty, _s * (SvW - 1) - 7.0);
                _svCursor.SetValue(Canvas.TopProperty,  (1.0 - _v) * (SvH - 1) - 7.0);
            }

            Dispatcher.BeginInvoke(new Action(PositionSliderCursors),
                System.Windows.Threading.DispatcherPriority.Render);

            if (_previewCircle != null)
            {
                var brush = new SolidColorBrush(c); brush.Freeze();
                _previewCircle.Fill = brush;
            }

            if (_hexBox != null && !_hexBox.IsFocused)
                _hexBox.Text = $"{c.R:X2}{c.G:X2}{c.B:X2}";

            if (_rBox != null && !_rBox.IsFocused) _rBox.Text = c.R.ToString(CultureInfo.InvariantCulture);
            if (_gBox != null && !_gBox.IsFocused) _gBox.Text = c.G.ToString(CultureInfo.InvariantCulture);
            if (_bBox != null && !_bBox.IsFocused) _bBox.Text = c.B.ToString(CultureInfo.InvariantCulture);
            if (_aBox != null && !_aBox.IsFocused)
                _aBox.Text = ((int)Math.Round(_a * 100)).ToString(CultureInfo.InvariantCulture);
        }

        private void PositionSliderCursors()
        {
            double halfCursor = (SliderH + 4) / 2.0;

            if (_hueHost != null && _hueOverlay != null && _hueCursor != null && _hueHost.ActualWidth > 0)
            {
                double hx = (_h / 360.0) * _hueHost.ActualWidth - halfCursor;
                _hueCursor.SetValue(Canvas.LeftProperty, hx);
                _hueCursor.SetValue(Canvas.TopProperty,  -(_hueCursor.Height - SliderH) / 2.0);
            }

            if (_alphaHost != null && _alphaOverlay != null && _alphaCursor != null && _alphaHost.ActualWidth > 0)
            {
                double ax = _a * _alphaHost.ActualWidth - halfCursor;
                _alphaCursor.SetValue(Canvas.LeftProperty, ax);
                _alphaCursor.SetValue(Canvas.TopProperty,  -(_alphaCursor.Height - SliderH) / 2.0);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Color math
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
            double r   = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
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
        // Helpers
        // ─────────────────────────────────────────────────────────────────────
        private static TextBlock MakeLabel(string text)
        {
            var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        private static TextBox MakeNumBox()
        {
            var b = new TextBox
            {
                Padding                  = new Thickness(4, 6, 4, 6),
                BorderThickness          = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
                TextAlignment            = TextAlignment.Center,
                MaxLength                = 3,
            };
            b.SetResourceReference(TextBox.BackgroundProperty,  "LemoineSelectBg");
            b.SetResourceReference(TextBox.ForegroundProperty,  "LemoineText");
            b.SetResourceReference(TextBox.BorderBrushProperty, "LemoineBorder");
            b.SetResourceReference(TextBox.FontFamilyProperty,  "LemoineMonoFont");
            b.SetResourceReference(TextBox.FontSizeProperty,    "LemoineFS_SM");
            b.SetResourceReference(TextBox.CaretBrushProperty,  "LemoineText");
            return b;
        }

        private static Border MakeColorSwatch(Color c, int size = 30)
        {
            var b = new Border
            {
                Width               = size,
                Height              = size,
                CornerRadius        = new CornerRadius(size <= 24 ? 4 : 6),
                Margin              = new Thickness(0, 0, 4, 4),
                Background          = new SolidColorBrush(c),
                BorderThickness     = new Thickness(1),
                Cursor              = Cursors.Hand,
                SnapsToDevicePixels = true,
            };
            b.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            return b;
        }

        private Border MakeEmptySwatch()
        {
            var b = new Border
            {
                Width               = ProjectSwatchSize,
                Height              = ProjectSwatchSize,
                CornerRadius        = new CornerRadius(6),
                Margin              = new Thickness(0, 0, 4, 4),
                BorderThickness     = new Thickness(1),
                Cursor              = Cursors.Hand,
                SnapsToDevicePixels = true,
            };
            b.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            b.SetResourceReference(Border.BorderBrushProperty, "LemoineBorderMid");
            var plus = new TextBlock
            {
                Text                = "+",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            plus.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            plus.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            b.Child = plus;
            return b;
        }
    }
}
