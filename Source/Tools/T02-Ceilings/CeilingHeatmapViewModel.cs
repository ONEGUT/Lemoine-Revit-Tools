using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfTextBox  = System.Windows.Controls.TextBox;
using WpfGrid     = System.Windows.Controls.Grid;
using WpfPoint    = System.Windows.Point;
using WpfRectangle = System.Windows.Shapes.Rectangle;
using WpfComboBox  = System.Windows.Controls.ComboBox;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Lemoine.Templates;

namespace LemoineTools.Tools.Ceilings
{
    public class CeilingHeatmapViewModel : ILemoineTool, ILemoineReviewable
    {
        // ── Identity ─────────────────────────────────────────────────────────────
        public string Title    => "Ceiling Elevation Heatmap";
        public string RunLabel => "Apply Heatmap →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1",     "Select Ceiling Plan Views", required: true),
            new StepDefinition("S_RAMP", "Color Ramp",                required: false),
            new StepDefinition("S2",     "Run Options",               required: false),
            new StepDefinition("S3",     "Review & Run",              required: false),
        };

        // ── Color ramp state ─────────────────────────────────────────────────────
        private string _colorLow  = CeilingHeatmapSettings.Instance.ColorLow;
        private string _colorMid  = CeilingHeatmapSettings.Instance.ColorMid;
        private string _colorHigh = CeilingHeatmapSettings.Instance.ColorHigh;

        private static readonly LemoineTemplateStore<CeilingColorRamp> _rampStore =
            new LemoineTemplateStore<CeilingColorRamp>(
                toolId:      "CeilingHeatmapRamp",
                serialize:   (data, path) => data.SaveTo(path),
                deserialize: path => CeilingColorRamp.LoadFrom(path));

        // Live-update handles for S_RAMP step
        private WpfRectangle? _gradientRect;
        private LemoineSingleSelect?  _rampCombo;

        // ── Run options state ────────────────────────────────────────────────────
        private bool   _deleteExisting = true;
        private bool   _placeTags      = CeilingHeatmapSettings.Instance.PlaceTags;
        private bool   _includeLinks   = CeilingHeatmapSettings.Instance.IncludeLinks;
        private double _elevTolerance  = CeilingHeatmapSettings.Instance.ElevTolerance;

        // ── View selection state ─────────────────────────────────────────────────
        private List<string>                                _selectedViewNames = new List<string>();
        private readonly Dictionary<string, ElementId>     _viewNameToId;
        private readonly Dictionary<string, List<string>>  _viewsByLevel;

        // ── Debug wiring ─────────────────────────────────────────────────────────
        private static CeilingHeatmapDebugHandler _debugHandler;
        private static ExternalEvent              _debugEvent;

        public static void RegisterDebugEvent(
            CeilingHeatmapDebugHandler handler, ExternalEvent evt)
        {
            _debugHandler = handler;
            _debugEvent   = evt;
        }

        // ── Validation ───────────────────────────────────────────────────────────
        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── Revit wiring ─────────────────────────────────────────────────────────
        private readonly CeilingHeatmapEventHandler? _handler;
        private readonly ExternalEvent?              _event;

        public CeilingHeatmapViewModel(
            CeilingHeatmapEventHandler? handler,
            ExternalEvent?              externalEvent,
            Dictionary<string, List<(string Name, ElementId Id)>>? viewsByLevel = null)
        {
            _handler = handler;
            _event   = externalEvent;

            _viewNameToId = new Dictionary<string, ElementId>();
            _viewsByLevel = new Dictionary<string, List<string>>();

            if (viewsByLevel != null)
            {
                foreach (var kvp in viewsByLevel)
                {
                    var names = new List<string>();
                    foreach (var (n, id) in kvp.Value)
                    {
                        _viewNameToId[n] = id;
                        names.Add(n);
                    }
                    _viewsByLevel[kvp.Key] = names;
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═════════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1":     return BuildS1();
                case "S_RAMP": return BuildS_RAMP();
                case "S2":     return BuildS2();
                case "S3":     return null; // framework renders review (ILemoineReviewable)
                default:       return null;
            }
        }

        // ── S1: View selection ───────────────────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            if (_viewsByLevel.Count == 0)
            {
                var msg = new TextBlock
                {
                    Text         = "No reflected ceiling plan views found in this document.",
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                };
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                return msg;
            }

            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(_viewsByLevel);
            tabs.SelectionChanged += selected =>
            {
                _selectedViewNames = new List<string>(selected);
                OnValidationChanged();
            };
            return tabs;
        }

        // ── S_RAMP: Color Ramp step ──────────────────────────────────────────────
        private FrameworkElement BuildS_RAMP()
        {
            var outer = new StackPanel();

            // ── Section label: Saved Ramps ──────────────────────────────────────
            var savedLabel = new TextBlock { Text = "SAVED RAMPS", Margin = new Thickness(0, 0, 0, 6) };
            savedLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            savedLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            savedLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(savedLabel);

            // ── Load row: [ComboBox fills | Load | Delete] ───────────────────────
            var loadRow = new DockPanel
            {
                LastChildFill = true,
                Margin        = new Thickness(0, 0, 0, 6),
            };

            var deleteBtn = LemoineControlStyles.BuildSmallButton("Delete",
                LemoineControlStyles.LemoineButtonVariant.Ghost);
            deleteBtn.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(deleteBtn, Dock.Right);

            var loadBtn = LemoineControlStyles.BuildSmallButton("Load",
                LemoineControlStyles.LemoineButtonVariant.Ghost);
            DockPanel.SetDock(loadBtn, Dock.Right);

            _rampCombo = new LemoineSingleSelect();
            RefreshRampCombo();

            loadRow.Children.Add(deleteBtn);
            loadRow.Children.Add(loadBtn);
            loadRow.Children.Add(_rampCombo);
            outer.Children.Add(loadRow);

            // ── Save row: [NameBox fills | Save] ─────────────────────────────────
            var saveRow = new DockPanel
            {
                LastChildFill = true,
                Margin        = new Thickness(0, 0, 0, 14),
            };

            var saveBtn = LemoineControlStyles.BuildSmallButton("Save",
                LemoineControlStyles.LemoineButtonVariant.Primary);
            saveBtn.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(saveBtn, Dock.Right);

            var nameBox = new WpfTextBox { ToolTip = "Ramp name…" };
            nameBox.SetResourceReference(FrameworkElement.MinHeightProperty,             "LemoineH_Input");
            nameBox.SetResourceReference(System.Windows.Controls.Control.PaddingProperty,"LemoineTh_InputPad");
            nameBox.SetResourceReference(WpfTextBox.BackgroundProperty,   "LemoineSelectBg");
            nameBox.SetResourceReference(WpfTextBox.ForegroundProperty,   "LemoineText");
            nameBox.SetResourceReference(WpfTextBox.FontSizeProperty,     "LemoineFS_SM");
            nameBox.SetResourceReference(WpfTextBox.FontFamilyProperty,   "LemoineMonoFont");
            nameBox.SetResourceReference(WpfTextBox.BorderBrushProperty,  "LemoineBorderMid");

            saveRow.Children.Add(saveBtn);
            saveRow.Children.Add(nameBox);
            outer.Children.Add(saveRow);

            // ── Button wire-up ───────────────────────────────────────────────────
            saveBtn.Click += (s, e) =>
            {
                string name = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(name)) return;
                var ramp = new CeilingColorRamp { Low = _colorLow, Mid = _colorMid, High = _colorHigh };
                _rampStore.Save(name, ramp, out _);
                RefreshRampCombo();
                nameBox.Text = "";
            };

            loadBtn.Click += (s, e) =>
            {
                if (_rampCombo.SelectedItem is string name && !string.IsNullOrEmpty(name))
                {
                    var info = _rampStore.List().FirstOrDefault(t => t.Name == name);
                    if (info != null && _rampStore.Load(info, out var ramp, out _) && ramp != null)
                    {
                        _colorLow  = ramp.Low;
                        _colorMid  = ramp.Mid;
                        _colorHigh = ramp.High;
                        SaveColorsToSettings();
                        // Rebuild the step to reflect new colors
                        RebuildRampStep(outer);
                        OnValidationChanged();
                    }
                }
            };

            deleteBtn.Click += (s, e) =>
            {
                if (_rampCombo.SelectedItem is string name && !string.IsNullOrEmpty(name))
                {
                    var info = _rampStore.List().FirstOrDefault(t => t.Name == name);
                    if (info != null) { _rampStore.Delete(info, out _); RefreshRampCombo(); }
                }
            };

            // ── Color Stops section ──────────────────────────────────────────────
            var stopsLabel = new TextBlock { Text = "COLOR STOPS", Margin = new Thickness(0, 0, 0, 8) };
            stopsLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            stopsLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            stopsLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(stopsLabel);

            // 3-column chip grid (Low | gap | Mid | gap | High)
            var chipsGrid = new WpfGrid();
            chipsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            chipsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            chipsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            chipsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            chipsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            chipsGrid.Margin = new Thickness(0, 0, 0, 10);

            void AddChip(int col, string label, Func<string> getHex, Action<string> setHex)
            {
                var chip = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };

                var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                chip.Children.Add(lbl);

                var swatch = LemoineColorPickerWindow.BuildColorPickerSwatch(
                    getHex: getHex,
                    setHex: h =>
                    {
                        setHex(h);
                        SaveColorsToSettings();
                        UpdateGradientPreview();
                        OnValidationChanged();
                    });
                chip.Children.Add(swatch);

                WpfGrid.SetColumn(chip, col);
                chipsGrid.Children.Add(chip);
            }

            AddChip(0, "LOW",  () => _colorLow,  h => _colorLow  = h);
            AddChip(2, "MID",  () => _colorMid,  h => _colorMid  = h);
            AddChip(4, "HIGH", () => _colorHigh, h => _colorHigh = h);
            outer.Children.Add(chipsGrid);

            // ── Gradient preview ─────────────────────────────────────────────────
            var previewLabel = new TextBlock { Text = "RAMP PREVIEW", Margin = new Thickness(0, 0, 0, 6) };
            previewLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            previewLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            previewLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(previewLabel);

            var previewBorder = new Border
            {
                Height          = 28,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                ClipToBounds    = true,
            };
            previewBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            _gradientRect = new WpfRectangle();
            previewBorder.Child = _gradientRect;
            UpdateGradientPreview();
            outer.Children.Add(previewBorder);

            return outer;
        }

        private void RefreshRampCombo()
        {
            if (_rampCombo == null) return;
            var names = _rampStore.List().Select(t => t.Name).ToList();
            _rampCombo.Items = names; // LemoineSingleSelect auto-selects the first entry
        }

        private void UpdateGradientPreview()
        {
            if (_gradientRect == null) return;
            var brush = new LinearGradientBrush();
            brush.StartPoint = new WpfPoint(0, 0.5);
            brush.EndPoint   = new WpfPoint(1, 0.5);
            brush.GradientStops.Add(new GradientStop(ParseMediaColor(_colorLow,  0,   0, 255), 0.0));
            brush.GradientStops.Add(new GradientStop(ParseMediaColor(_colorMid,  0, 255,   0), 0.5));
            brush.GradientStops.Add(new GradientStop(ParseMediaColor(_colorHigh, 255,  0,   0), 1.0));
            _gradientRect.Fill = brush;
        }

        private void RebuildRampStep(StackPanel container)
        {
            // Null out handles so they get re-assigned when the step is rebuilt
            _gradientRect = null;
            _rampCombo    = null;
            // Rebuild by clearing and re-populating the container
            container.Children.Clear();
            var rebuilt = BuildS_RAMP();
            if (rebuilt is StackPanel sp)
                foreach (UIElement child in sp.Children)
                    container.Children.Add(child);
        }

        private void SaveColorsToSettings()
        {
            CeilingHeatmapSettings.Instance.ColorLow  = _colorLow;
            CeilingHeatmapSettings.Instance.ColorMid  = _colorMid;
            CeilingHeatmapSettings.Instance.ColorHigh = _colorHigh;
            CeilingHeatmapSettings.Instance.Save();
        }

        // ── S2: Run Options ──────────────────────────────────────────────────────
        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel();

            // ── Toggles: delete existing, include links, place tags ───────────────
            var tog = new LemoineToggleSwitches();
            tog.SetItems(new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id        = "delete",
                    Label     = "Delete existing heatmap filters",
                    Desc      = "Remove all \"Ceiling Heatmap\" view filters from the project before applying the new set.",
                    DefaultOn = _deleteExisting,
                },
                new ToggleItem
                {
                    Id        = "links",
                    Label     = "Include linked ceilings",
                    Desc      = "Scan Revit link instances visible in each selected view.",
                    DefaultOn = _includeLinks,
                },
                new ToggleItem
                {
                    Id        = "tags",
                    Label     = "Place ceiling tags",
                    Desc      = "Place a Ceiling Tag at the centroid of each ceiling after applying the heatmap. Ceilings that already carry a tag are skipped.",
                    DefaultOn = _placeTags,
                },
            });
            tog.StateChanged += state =>
            {
                _deleteExisting = state.TryGetValue("delete", out bool dv) && dv;
                _includeLinks   = state.TryGetValue("links",  out bool lv) && lv;
                _placeTags      = state.TryGetValue("tags",   out bool tv) && tv;
                OnValidationChanged();
            };
            outer.Children.Add(tog);

            // ── Elevation tolerance ───────────────────────────────────────────────
            outer.Children.Add(new FrameworkElement { Height = 14 });

            var tolLabel = new TextBlock { Text = "ELEVATION TOLERANCE", Margin = new Thickness(0, 0, 0, 6) };
            tolLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tolLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tolLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(tolLabel);

            var tolRow = new StackPanel { Orientation = Orientation.Horizontal };

            double displayInches = Math.Round(_elevTolerance * 12.0, 4);
            var tolBox = new WpfTextBox { Text = displayInches.ToString(), TextAlignment = TextAlignment.Right };
            tolBox.SetResourceReference(WpfTextBox.WidthProperty,      "LemoineW_NumInput");
            tolBox.SetResourceReference(WpfTextBox.MinHeightProperty,  "LemoineH_Input");
            tolBox.SetResourceReference(WpfTextBox.PaddingProperty,    "LemoineTh_InputPad");
            tolBox.SetResourceReference(WpfTextBox.BackgroundProperty, "LemoineSelectBg");
            tolBox.SetResourceReference(WpfTextBox.ForegroundProperty, "LemoineText");
            tolBox.SetResourceReference(WpfTextBox.FontFamilyProperty, "LemoineMonoFont");
            tolBox.SetResourceReference(WpfTextBox.FontSizeProperty,   "LemoineFS_MD");
            tolBox.SetResourceReference(WpfTextBox.BorderBrushProperty,"LemoineBorderMid");
            tolBox.LostFocus += (s, e) =>
            {
                if (double.TryParse(tolBox.Text, out double val))
                    _elevTolerance = val / 12.0;
            };
            tolRow.Children.Add(tolBox);

            var tolUnit = new TextBlock { Text = "  in", VerticalAlignment = VerticalAlignment.Center };
            tolUnit.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tolUnit.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tolUnit.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tolRow.Children.Add(tolUnit);

            outer.Children.Add(tolRow);

            var tolHint = new TextBlock
            {
                Text         = "Ceilings within this elevation range share one color filter bucket.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 4, 0, 0),
                FontStyle    = FontStyles.Italic,
            };
            tolHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tolHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tolHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(tolHint);

            return outer;
        }

        // ── S3: Review ───────────────────────────────────────────────────────────

        // ── ILemoineReviewable (P3) — framework renders the review step ───────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("views",  "Views Selected"),
            ("ramp",   "Color Ramp"),
            ("delete", "Delete Existing Filters"),
            ("links",  "Include Linked Ceilings"),
            ("tags",   "Place Ceiling Tags"),
            ("tol",    "Elevation Tolerance"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["views"]  = _selectedViewNames.Count == 0 ? "—"
                : $"{_selectedViewNames.Count} view{(_selectedViewNames.Count == 1 ? "" : "s")}",
            ["ramp"]   = $"{_colorLow} → {_colorMid} → {_colorHigh}",
            ["delete"] = _deleteExisting ? "Yes" : "No",
            ["links"]  = _includeLinks ? "Yes" : "No",
            ["tags"]   = _placeTags ? "Yes" : "No",
            ["tol"]    = $"{Math.Round(_elevTolerance * 12.0, 4)} in",
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => "Selected ceiling plan views will be scanned for \"Height Offset " +
            "From Level\" data. One view filter is created per distinct height offset bucket and applied to every " +
            "selected view as a solid surface color override.";
        public string?        ReviewWarning => null;

        // ═════════════════════════════════════════════════════════════════════════
        // IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedViewNames.Count > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _selectedViewNames.Count == 0 ? "—"
                    : $"{_selectedViewNames.Count} view{(_selectedViewNames.Count == 1 ? "" : "s")} selected";
            if (stepId == "S_RAMP")
                return $"{_colorLow} → {_colorMid} → {_colorHigh}";
            if (stepId == "S2")
            {
                var parts = new List<string>();
                if (_deleteExisting) parts.Add("Delete existing");
                if (_includeLinks)   parts.Add("Include links");
                if (_placeTags)      parts.Add("Place tags");
                parts.Add($"Tol {Math.Round(_elevTolerance * 12.0, 4)} in");
                return string.Join(" · ", parts);
            }
            if (stepId == "S3") return "Ready to run";
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            // Persist run options back to settings
            CeilingHeatmapSettings.Instance.PlaceTags     = _placeTags;
            CeilingHeatmapSettings.Instance.IncludeLinks  = _includeLinks;
            CeilingHeatmapSettings.Instance.ElevTolerance = _elevTolerance;
            SaveColorsToSettings();

            _handler!.SelectedViewIds = _selectedViewNames
                .Where(n => _viewNameToId.ContainsKey(n))
                .Select(n => _viewNameToId[n])
                .ToList();
            _handler.DeleteExisting  = _deleteExisting;
            _handler.PlaceTags       = _placeTags;
            _handler.IncludeLinks    = _includeLinks;
            _handler.ElevTolerance   = _elevTolerance;
            _handler.ColorLow        = ParseRevitColor(_colorLow,  0,   0, 255);
            _handler.ColorMid        = ParseRevitColor(_colorMid,  0, 255,   0);
            _handler.ColorHigh       = ParseRevitColor(_colorHigh, 255,  0,   0);
            _handler.PushLog         = pushLog;
            _handler.OnProgress      = onProgress;
            _handler.OnComplete      = onComplete;
            pushLog("Starting Ceiling Elevation Heatmap…", "info");
            _event!.Raise();
        }

        // ── Color helpers ─────────────────────────────────────────────────────────
        private static void ParseRGB(string hex, byte defR, byte defG, byte defB,
            out byte r, out byte g, out byte b)
        {
            if (TryParseHex(hex, out r, out g, out b)) return;
            r = defR; g = defG; b = defB;
        }

        private static bool TryParseHex(string hex, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            hex = hex.TrimStart('#');
            if (hex.Length == 6
                && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int v))
            {
                r = (byte)((v >> 16) & 0xFF);
                g = (byte)((v >>  8) & 0xFF);
                b = (byte)( v        & 0xFF);
                return true;
            }
            return false;
        }

        private static System.Windows.Media.Color ParseMediaColor(string hex, byte defR, byte defG, byte defB)
        {
            ParseRGB(hex, defR, defG, defB, out byte r, out byte g, out byte b);
            return System.Windows.Media.Color.FromRgb(r, g, b);
        }

        internal static Autodesk.Revit.DB.Color ParseRevitColor(string hex, byte defR, byte defG, byte defB)
        {
            ParseRGB(hex, defR, defG, defB, out byte r, out byte g, out byte b);
            return new Autodesk.Revit.DB.Color(r, g, b);
        }
    }
}
