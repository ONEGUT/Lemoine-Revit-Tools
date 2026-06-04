using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.Clash;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// Library window for managing saved <see cref="ClashDefinition"/>s. Left = sidebar of
    /// definitions (select / duplicate / delete + add). Right = editor for the selected
    /// definition: Name, Group 1 / Group 2 (via <see cref="ClashGroupEditor"/>), and marking
    /// settings. Auto-saves on close (dirty check) — no Apply button, per the house pattern.
    /// </summary>
    public partial class ClashDefinitionsWindow : Window
    {
        // ── Context from the launching command (Revit main-thread data) ───────
        private List<string>        _lineStyleNames = new List<string>();
        private List<ClashDocInfo>  _docs           = new List<ClashDocInfo>();
        private ClashPickEventHandler? _pickHandler;
        private ExternalEvent?         _pickEvent;

        // ── Live, editable buffer (deep copy of the saved library) ────────────
        private List<ClashDefinition> _defs = new List<ClashDefinition>();
        private string?               _activeId;
        private string                _snapshot = "";

        // ── Panels built in OnLoaded ──────────────────────────────────────────
        private StackPanel? _sidebarPanel;
        private readonly Dictionary<string, Border> _rowBorders = new Dictionary<string, Border>();

        public ClashDefinitionsWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;

            LemoineSettings.Instance.ThemeChanged += t => Dispatcher.Invoke(() =>
            {
                LemoineSettings.Instance.ApplyTo(Resources);
                Background = t.PageBg;
                _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            });

            LemoineSettings.Instance.UiSizeChanged += _ => Dispatcher.Invoke(() =>
            {
                LemoineSettings.Instance.ApplyScaleTo(Resources);
                LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 8);
                if (_root != null)
                    _root.RowDefinitions[0].Height = new GridLength(LemoineSettings.Instance.ToolbarHeight);
            });
        }

        /// <summary>Supplies Revit-queried data (called once before the window is shown).</summary>
        internal void SetContext(
            List<string> lineStyleNames, List<ClashDocInfo> docs,
            ClashPickEventHandler? pickHandler, ExternalEvent? pickEvent)
        {
            _lineStyleNames = lineStyleNames ?? new List<string>();
            _docs           = docs ?? new List<ClashDocInfo>();
            _pickHandler    = pickHandler;
            _pickEvent      = pickEvent;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LemoineSettings.Instance.ApplyTo(Resources);
            LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 8);
            Background = LemoineSettings.Instance.ActiveTheme.PageBg;
            _root.SetResourceReference(Grid.BackgroundProperty, "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _outerBorder.CornerRadius = new CornerRadius(8);
            _root.RowDefinitions[0].Height = new GridLength(LemoineSettings.Instance.ToolbarHeight);
            _sidebarBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            _defs     = ClashDefinitionsSettings.DeepCopy(ClashDefinitionsSettings.Instance.Definitions);
            _snapshot = Serialize(_defs);
            _activeId = _defs.Count > 0 ? _defs[0].Id : null;

            BuildToolbar();
            BuildSidebar();
            RefreshSidebar();
            RefreshEditor();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (Serialize(_defs) != _snapshot)
            {
                ClashDefinitionsSettings.Instance.Definitions = _defs;
                ClashDefinitionsSettings.Instance.Save();
            }
            base.OnClosed(e);
        }

        // ── Toolbar ───────────────────────────────────────────────────────────
        private void BuildToolbar()
        {
            _toolbarBorder.BorderThickness = new Thickness(0);

            var closeBtn = LemoineControlStyles.BuildButton("×", LemoineControlStyles.LemoineButtonVariant.Ghost);
            closeBtn.SetResourceReference(Button.ForegroundProperty, "LemoineTextDim");
            closeBtn.Click += (s, ev) => Close();

            var rightPanel = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rightPanel.Children.Add(closeBtn);

            _toolbarBorder.Child = new LemoineTitleBar
            {
                Title        = "Clash Definitions",
                IconGlyph    = char.ConvertFromUtf32(0xE71C),   // Segoe MDL2: Filter
                RightContent = rightPanel,
            };
        }

        // ── Sidebar ───────────────────────────────────────────────────────────
        private void BuildSidebar()
        {
            _sidebarPanel = new StackPanel { Margin = new Thickness(8) };
            var sv = new ScrollViewer
            {
                Content                       = _sidebarPanel,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            _sidebarBorder.Child = sv;
        }

        private void RefreshSidebar()
        {
            if (_sidebarPanel == null) return;
            _sidebarPanel.Children.Clear();
            _rowBorders.Clear();

            foreach (var def in _defs)
                _sidebarPanel.Children.Add(BuildDefRow(def));

            _sidebarPanel.Children.Add(LemoineControlStyles.BuildAddPill("＋ Add definition", AddDefinition));
        }

        private Border BuildDefRow(ClashDefinition def)
        {
            var nameTb = new TextBlock
            {
                Text              = string.IsNullOrWhiteSpace(def.Name) ? "(unnamed)" : def.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                IsHitTestVisible  = false,
            };
            nameTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            nameTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            nameTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");

            var dupBtn = GlyphButton(char.ConvertFromUtf32(0xE8C8), "Duplicate");   // Copy
            dupBtn.Click += (s, e) => DuplicateDefinition(def.Id);

            var delBtn = GlyphButton(char.ConvertFromUtf32(0xE74D), "Delete");   // Trash
            delBtn.Click += (s, e) => DeleteDefinition(def.Id);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(nameTb, 0);
            Grid.SetColumn(dupBtn, 1);
            Grid.SetColumn(delBtn, 2);
            grid.Children.Add(nameTb);
            grid.Children.Add(dupBtn);
            grid.Children.Add(delBtn);

            var row = new Border
            {
                Padding         = new Thickness(10, 7, 8, 7),
                Margin          = new Thickness(0, 0, 0, 4),
                BorderThickness = new Thickness(1),
                Cursor          = Cursors.Hand,
                Child           = grid,
            };
            row.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Card");
            ApplyRowStyle(row, def.Id == _activeId);
            row.MouseLeftButtonUp += (s, e) => SelectDefinition(def.Id);

            _rowBorders[def.Id] = row;
            return row;
        }

        private void ApplyRowStyle(Border row, bool active)
        {
            if (active)
            {
                row.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
                row.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            }
            else
            {
                row.Background = Brushes.Transparent;
                row.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            }
        }

        private Button GlyphButton(string glyph, string tip)
        {
            var b = new Button
            {
                Content         = new TextBlock
                {
                    Text              = glyph,
                    FontFamily        = new FontFamily("Segoe MDL2 Assets"),
                    IsHitTestVisible  = false,
                },
                Cursor          = Cursors.Hand,
                Margin          = new Thickness(4, 0, 0, 0),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(6, 3, 6, 3),
                ToolTip         = tip,
                Template        = LemoineControlStyles.BuildFlatButtonTemplate(),
                Background      = Brushes.Transparent,
            };
            ((TextBlock)b.Content).SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            ((TextBlock)b.Content).SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            b.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
            return b;
        }

        // ── Sidebar actions ───────────────────────────────────────────────────
        private void AddDefinition()
        {
            var def = ClashDefinition.NewBlank();
            _defs.Add(def);
            _activeId = def.Id;
            RefreshSidebar();
            RefreshEditor();
        }

        private void DuplicateDefinition(string id)
        {
            var src = _defs.Find(d => d.Id == id);
            if (src == null) return;
            var copy = ClashDefinitionsSettings.DeepCopy(src);
            copy.Id   = "C" + Guid.NewGuid().ToString("N").Substring(0, 7);
            copy.Name = string.IsNullOrWhiteSpace(src.Name) ? "Definition (copy)" : src.Name + " (copy)";
            int idx = _defs.FindIndex(d => d.Id == id);
            _defs.Insert(idx + 1, copy);
            _activeId = copy.Id;
            RefreshSidebar();
            RefreshEditor();
        }

        private void DeleteDefinition(string id)
        {
            int idx = _defs.FindIndex(d => d.Id == id);
            if (idx < 0) return;
            _defs.RemoveAt(idx);
            if (_activeId == id)
                _activeId = _defs.Count > 0 ? _defs[Math.Min(idx, _defs.Count - 1)].Id : null;
            RefreshSidebar();
            RefreshEditor();
        }

        private void SelectDefinition(string id)
        {
            if (_activeId == id) return;
            _activeId = id;
            foreach (var kv in _rowBorders) ApplyRowStyle(kv.Value, kv.Key == _activeId);
            RefreshEditor();
        }

        // ── Editor ────────────────────────────────────────────────────────────
        private void RefreshEditor()
        {
            var panel = new StackPanel { Margin = new Thickness(16) };

            var def = _defs.Find(d => d.Id == _activeId);
            if (def == null)
            {
                AddDim(panel, "No definition selected. Use “＋ Add definition” on the left to create one.");
                SetEditorContent(panel);
                return;
            }

            // ── Name ──────────────────────────────────────────────────────────
            AddLabel(panel, "Name");
            var nameEdit = new LemoineInlineEdit { Text = def.Name, Margin = new Thickness(0, 0, 0, 10) };
            nameEdit.TextCommitted += (s, txt) =>
            {
                def.Name = string.IsNullOrWhiteSpace(txt) ? "(unnamed)" : txt.Trim();
                RefreshSidebar();
            };
            panel.Children.Add(nameEdit);

            // ── Group 1 / Group 2 ─────────────────────────────────────────────
            var g1Editor = new ClashGroupEditor(def.Group1, _docs, _pickHandler, _pickEvent, null);
            panel.Children.Add(new LemoineSectionCard
            {
                Header      = "Group 1 — Source",
                CardContent = g1Editor.Build(),
                Margin      = new Thickness(0, 0, 0, 14),
            });

            var g2Editor = new ClashGroupEditor(def.Group2, _docs, _pickHandler, _pickEvent, null);
            panel.Children.Add(new LemoineSectionCard
            {
                Header      = "Group 2 — Target",
                CardContent = g2Editor.Build(),
                Margin      = new Thickness(0, 0, 0, 14),
            });

            // ── Marking settings ──────────────────────────────────────────────
            var marking = new StackPanel();

            AddStepperRow(marking, "Clash Tolerance (mm)",
                "Extra margin added to each side of the clash bounding box marker.",
                def.ToleranceMm, 0, 100, 0.5, 1, v => def.ToleranceMm = v);

            AddStepperRow(marking, "Max Clashes",
                "Stop detecting after this many clashes. Raise if clashes are being missed.",
                def.MaxClashes, 1, 100000, 1, 0, v => def.MaxClashes = (int)v);

            AddLabel(marking, "Fill Style");
            var fillSelect = new LemoineSingleSelect { Items = new[] { "Solid", "Outline" }, SelectedItem = def.FillStyle };
            fillSelect.SelectionChanged += val => { if (val != null) def.FillStyle = val; };
            marking.Children.Add(fillSelect);

            AddDivider(marking);
            AddLabel(marking, "Marker Reference");
            var targetSelect = new LemoineSingleSelect { Items = new[] { "Edge", "Centre" }, SelectedItem = def.DimTarget };
            targetSelect.SelectionChanged += val => { if (val != null) def.DimTarget = val; };
            marking.Children.Add(targetSelect);

            AddDivider(marking);
            AddLabel(marking, "Cross Line Style");
            var lineItems = new List<string> { "(Default)" };
            lineItems.AddRange(_lineStyleNames);
            var lineSelect = new LemoineSingleSelect
            {
                Items        = lineItems.ToArray(),
                SelectedItem = _lineStyleNames.Contains(def.CrossLineTypeName) ? def.CrossLineTypeName : "(Default)",
            };
            lineSelect.SelectionChanged += val =>
                def.CrossLineTypeName = (val == null || val == "(Default)") ? "" : val;
            marking.Children.Add(lineSelect);

            AddDivider(marking);
            AddLabel(marking, "Fallback Colour (for clashes matching no Auto Filter rule)");
            var picker = new LemoineColorPickerPanel
            {
                SelectedColor = BrushHelper.ColorFromHex(def.FallbackColorHex, LemoineTheme.FallbackGrey),
            };
            picker.ColorChanged += (s, c) => def.FallbackColorHex = HexFromColor(c);
            marking.Children.Add(picker);

            panel.Children.Add(new LemoineSectionCard
            {
                Header      = "Marking Settings",
                CardContent = marking,
                Margin      = new Thickness(0, 0, 0, 14),
            });

            SetEditorContent(panel);
        }

        private void SetEditorContent(StackPanel panel)
        {
            var sv = new ScrollViewer
            {
                Content                       = panel,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            LemoineControlStyles.WireBubblingScroll(sv);
            _editorBorder.Child = sv;
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static string HexFromColor(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static void AddLabel(StackPanel parent, string text)
        {
            var lbl = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4) };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            parent.Children.Add(lbl);
        }

        private static void AddDim(StackPanel parent, string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            parent.Children.Add(tb);
        }

        private static void AddDivider(StackPanel parent)
        {
            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            parent.Children.Add(sep);
        }

        private static void AddStepperRow(StackPanel parent, string label, string hint,
            double value, double min, double max, double step, int decimals, Action<double> onChange)
        {
            var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 2) };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            parent.Children.Add(lbl);

            var stepper = new LemoineInlineStepper
            {
                Value               = value,
                MinValue            = min,
                MaxValue            = max,
                Step                = step,
                Decimals            = decimals,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin              = new Thickness(0, 0, 0, 8),
                ToolTip             = hint,
            };
            stepper.ValueChanged += (s, v) => onChange(v);
            parent.Children.Add(stepper);
        }

        private static string Serialize(List<ClashDefinition> defs)
        {
            if (defs == null) return "";
            try
            {
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(List<ClashDefinition>));
                using (var sw = new System.IO.StringWriter())
                {
                    xs.Serialize(sw, defs);
                    return sw.ToString();
                }
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("ClashDefinitionsWindow.Serialize", ex);
                return Guid.NewGuid().ToString();   // treat as dirty → save
            }
        }
    }
}
