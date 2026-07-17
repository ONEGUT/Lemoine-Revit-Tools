using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using LemoineTools.Framework.Controls;
using LemoineTools.Tools.Dimensioning;

namespace LemoineTools.Framework
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
        private List<string>        _hostPhaseNames = new List<string>();   // host phases, sequence order
        private ClashPickEventHandler? _pickHandler;
        private ExternalEvent?         _pickEvent;

        // ── Live, editable buffer (deep copy of the saved library) ────────────
        private List<ClashDefinition> _defs = new List<ClashDefinition>();
        private string?               _activeId;
        private string                _snapshot = "";

        // Two-click delete: the first trash click arms this id (button turns red), the second
        // performs the delete. Leaving the row disarms it — a misclick can no longer destroy a
        // definition (the window auto-saves on close, so there is no undo after that).
        private string? _pendingDeleteId;

        private const double MmPerInch = 25.4;

        // ── Panels built in OnLoaded ──────────────────────────────────────────
        private StackPanel? _sidebarPanel;
        private readonly Dictionary<string, Border> _rowBorders = new Dictionary<string, Border>();

        public ClashDefinitionsWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;

            // Named handlers (not lambdas) so they can be detached in OnClosed — a leaked
            // subscription to this STA window after its dispatcher has shut down crashes/hangs
            // Revit on the next theme change.
            AppSettings.Instance.ThemeChanged  += OnThemeChanged;
            AppSettings.Instance.UiSizeChanged += OnUiSizeChanged;
        }

        private void OnThemeChanged(ThemePalette t)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppSettings.Instance.ApplyTo(Resources);
                Background = t.PageBg;
                _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            }));
        }

        private void OnUiSizeChanged(UiSize _)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppSettings.Instance.ApplyScaleTo(Resources);
                ControlStyles.InjectInto(Resources, scrollBarWidth: 8);
                if (_root != null)
                    _root.RowDefinitions[0].Height = new GridLength(AppSettings.Instance.ToolbarHeight);
            }));
        }

        /// <summary>Supplies Revit-queried data (called once before the window is shown).</summary>
        internal void SetContext(
            List<string> lineStyleNames, List<ClashDocInfo> docs, List<string> hostPhaseNames,
            ClashPickEventHandler? pickHandler, ExternalEvent? pickEvent)
        {
            _lineStyleNames = lineStyleNames ?? new List<string>();
            _docs           = docs ?? new List<ClashDocInfo>();
            _hostPhaseNames = hostPhaseNames ?? new List<string>();
            _pickHandler    = pickHandler;
            _pickEvent      = pickEvent;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            AppSettings.Instance.ApplyTo(Resources);
            ControlStyles.InjectInto(Resources, scrollBarWidth: 8);
            Background = AppSettings.Instance.ActiveTheme.PageBg;
            _root.SetResourceReference(Grid.BackgroundProperty, "LemoineBg");
            _outerBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            _outerBorder.CornerRadius = new CornerRadius(8);
            _root.RowDefinitions[0].Height = new GridLength(AppSettings.Instance.ToolbarHeight);
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
            AppSettings.Instance.ThemeChanged  -= OnThemeChanged;
            AppSettings.Instance.UiSizeChanged -= OnUiSizeChanged;

            // Callbacks parked on the static handler outlive this window — sever them so a
            // late pick can't marshal into this window's terminated dispatcher (and so the
            // editors/window aren't retained until the next pick).
            if (_pickHandler != null)
            {
                _pickHandler.OnPicked = null;
                _pickHandler.PushLog  = null;
            }

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

            var closeBtn = ControlStyles.BuildButton("×", ControlStyles.ButtonVariant.Ghost);
            closeBtn.SetResourceReference(Button.ForegroundProperty, "LemoineTextDim");
            closeBtn.Click += (s, ev) => Close();

            var rightPanel = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rightPanel.Children.Add(closeBtn);

            _toolbarBorder.Child = new TitleBar
            {
                Title        = AppStrings.T("clashDefinitions.window.title"),
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

            _sidebarPanel.Children.Add(ControlStyles.BuildAddPill(AppStrings.T("clashDefinitions.window.addPill"), AddDefinition));
        }

        private Border BuildDefRow(ClashDefinition def)
        {
            var nameTb = new TextBlock
            {
                Text              = string.IsNullOrWhiteSpace(def.Name) ? AppStrings.T("clashDefinitions.window.unnamed") : def.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                IsHitTestVisible  = false,
            };
            nameTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            nameTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            nameTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");

            var dupBtn = GlyphButton(char.ConvertFromUtf32(0xE8C8), AppStrings.T("clashDefinitions.window.dupTooltip"));   // Copy
            dupBtn.Click += (s, e) => DuplicateDefinition(def.Id);

            var delBtn = GlyphButton(char.ConvertFromUtf32(0xE74D), AppStrings.T("clashDefinitions.window.delTooltip"));   // Trash
            delBtn.Click += (s, e) =>
            {
                if (_pendingDeleteId == def.Id)
                {
                    _pendingDeleteId = null;
                    DeleteDefinition(def.Id);
                    return;
                }
                _pendingDeleteId = def.Id;
                ((TextBlock)delBtn.Content).SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
                delBtn.ToolTip = AppStrings.T("clashDefinitions.window.delConfirmTooltip");
            };

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
            row.MouseLeave += (s, e) =>
            {
                // Disarm a pending delete when the pointer leaves the row, so a stray click
                // minutes later can't complete it. Rebuild resets the button's style.
                if (_pendingDeleteId != def.Id) return;
                _pendingDeleteId = null;
                RefreshSidebar();
            };

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
                Template        = ControlStyles.BuildFlatButtonTemplate(),
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
            copy.Name = string.IsNullOrWhiteSpace(src.Name) ? AppStrings.T("clashDefinitions.window.copyDefault") : src.Name + AppStrings.T("clashDefinitions.window.copySuffix");
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
                AddDim(panel, AppStrings.T("clashDefinitions.window.noSelection"));
                SetEditorContent(panel);
                return;
            }

            // ── Name ──────────────────────────────────────────────────────────
            AddLabel(panel, AppStrings.T("clashDefinitions.labels.name"));
            var nameEdit = new InlineEdit { Text = def.Name, Margin = new Thickness(0, 0, 0, 10) };
            nameEdit.TextCommitted += (s, txt) =>
            {
                def.Name = string.IsNullOrWhiteSpace(txt) ? AppStrings.T("clashDefinitions.window.unnamed") : txt.Trim();
                RefreshSidebar();
            };
            panel.Children.Add(nameEdit);

            // ── Group 1 / Group 2 ─────────────────────────────────────────────
            var g1Editor = new ClashGroupEditor(def.Group1, _docs, _pickHandler, _pickEvent, null);
            panel.Children.Add(new SectionCard
            {
                Header      = AppStrings.T("clashDefinitions.labels.group1Header"),
                CardContent = g1Editor.Build(),
                Margin      = new Thickness(0, 0, 0, 14),
            });

            var g2Editor = new ClashGroupEditor(def.Group2, _docs, _pickHandler, _pickEvent, null);
            panel.Children.Add(new SectionCard
            {
                Header      = AppStrings.T("clashDefinitions.labels.group2Header"),
                CardContent = g2Editor.Build(),
                Margin      = new Thickness(0, 0, 0, 14),
            });

            // ── Marking settings ──────────────────────────────────────────────
            var marking = new StackPanel();

            // Edited in inches (imperial-first, matching the finder's oversize field);
            // stored in millimeters, unchanged on disk.
            AddStepperRow(marking, AppStrings.T("clashDefinitions.labels.tolerance"),
                AppStrings.T("clashDefinitions.labels.toleranceDesc"),
                def.ToleranceMm / MmPerInch, 0, 4, 0.125, 3, v => def.ToleranceMm = v * MmPerInch);

            AddStepperRow(marking, AppStrings.T("clashDefinitions.labels.maxClashes"),
                AppStrings.T("clashDefinitions.labels.maxClashesDesc"),
                def.MaxClashes, 1, 100000, 1, 0, v => def.MaxClashes = (int)v);

            // Persisted tokens ("Solid"/"Outline", "Edge"/"Centre") stay in code; the picker
            // shows externalized display labels mapped back to them (US spelling on screen).
            AddLabel(marking, AppStrings.T("clashDefinitions.labels.fillStyle"));
            string fillSolid = AppStrings.T("clashDefinitions.fill.solid"), fillOutline = AppStrings.T("clashDefinitions.fill.outline");
            var fillSelect = new SingleSelect
            {
                Items        = new[] { fillSolid, fillOutline },
                SelectedItem = def.FillStyle == "Outline" ? fillOutline : fillSolid,
            };
            fillSelect.SelectionChanged += val => { if (val != null) def.FillStyle = val == fillOutline ? "Outline" : "Solid"; };
            marking.Children.Add(fillSelect);

            AddDivider(marking);
            AddLabel(marking, AppStrings.T("clashDefinitions.labels.markerReference"));
            string refEdge = AppStrings.T("clashDefinitions.marker.edge"), refCenter = AppStrings.T("clashDefinitions.marker.center");
            var targetSelect = new SingleSelect
            {
                Items        = new[] { refEdge, refCenter },
                SelectedItem = def.DimTarget == "Centre" ? refCenter : refEdge,
            };
            targetSelect.SelectionChanged += val => { if (val != null) def.DimTarget = val == refCenter ? "Centre" : "Edge"; };
            marking.Children.Add(targetSelect);

            AddDivider(marking);
            AddLabel(marking, AppStrings.T("clashDefinitions.labels.phase"));
            string PhaseAll = AppStrings.T("clashDefinitions.phase.all"), PhaseMatch = AppStrings.T("clashDefinitions.phase.match"), PhaseSpecific = AppStrings.T("clashDefinitions.phase.specific");
            var phaseSelect = new SingleSelect
            {
                Items        = new[] { PhaseAll, PhaseMatch, PhaseSpecific },
                SelectedItem = def.PhaseMode == "MatchView" ? PhaseMatch
                             : def.PhaseMode == "Specific"  ? PhaseSpecific : PhaseAll,
            };
            marking.Children.Add(phaseSelect);
            AddDim(marking, AppStrings.T("clashDefinitions.labels.phaseDesc"));

            // Specific-phase picker — visible only in Specific mode. Shown default = the saved
            // name when valid, else the last (newest) host phase; written back only on user action.
            var specificSection = new StackPanel();
            string shownPhase = _hostPhaseNames.Contains(def.SpecificPhaseName)
                ? def.SpecificPhaseName
                : (_hostPhaseNames.Count > 0 ? _hostPhaseNames[_hostPhaseNames.Count - 1] : "");
            SingleSelect? specificSelect = null;
            if (_hostPhaseNames.Count > 0)
            {
                AddLabel(specificSection, AppStrings.T("clashDefinitions.labels.hostPhase"));
                specificSelect = new SingleSelect
                {
                    Items        = _hostPhaseNames.ToArray(),
                    SelectedItem = shownPhase,
                };
                specificSelect.SelectionChanged += val => { if (val != null) def.SpecificPhaseName = val; };
                specificSection.Children.Add(specificSelect);
            }
            else
            {
                AddDim(specificSection, AppStrings.T("clashDefinitions.labels.noHostPhases"));
            }
            specificSection.Visibility = def.PhaseMode == "Specific"
                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            marking.Children.Add(specificSection);

            phaseSelect.SelectionChanged += val =>
            {
                def.PhaseMode = val == PhaseMatch ? "MatchView" : val == PhaseSpecific ? "Specific" : "All";
                if (def.PhaseMode == "Specific" && specificSelect != null
                    && !_hostPhaseNames.Contains(def.SpecificPhaseName))
                    def.SpecificPhaseName = shownPhase;   // adopt the displayed default on entry
                specificSection.Visibility = def.PhaseMode == "Specific"
                    ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            };

            AddDivider(marking);
            AddLabel(marking, AppStrings.T("clashDefinitions.labels.crossLineStyle"));
            var lineItems = new List<string> { AppStrings.T("clashDefinitions.labels.lineDefault") };
            lineItems.AddRange(_lineStyleNames);
            var lineSelect = new SingleSelect
            {
                Items        = lineItems.ToArray(),
                SelectedItem = _lineStyleNames.Contains(def.CrossLineTypeName) ? def.CrossLineTypeName : AppStrings.T("clashDefinitions.labels.lineDefault"),
            };
            lineSelect.SelectionChanged += val =>
                def.CrossLineTypeName = (val == null || val == AppStrings.T("clashDefinitions.labels.lineDefault")) ? "" : val;
            marking.Children.Add(lineSelect);

            AddDivider(marking);
            AddLabel(marking, AppStrings.T("clashDefinitions.labels.fallbackColor"));
            var picker = new ColorPickerPanel
            {
                SelectedColor = BrushHelper.ColorFromHex(def.FallbackColorHex, ThemePalette.FallbackGrey),
            };
            picker.ColorChanged += (s, c) => def.FallbackColorHex = HexFromColor(c);
            marking.Children.Add(picker);

            panel.Children.Add(new SectionCard
            {
                Header      = AppStrings.T("clashDefinitions.labels.markingSettings"),
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
            ControlStyles.WireBubblingScroll(sv);
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

            var stepper = new InlineStepper
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
                DiagnosticsLog.Swallowed("ClashDefinitionsWindow.Serialize", ex);
                return Guid.NewGuid().ToString();   // treat as dirty → save
            }
        }
    }
}
