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
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;
using LemoineTools.Framework.Templates;

namespace LemoineTools.Tools.Ceilings
{
    public class CeilingHeatmapViewModel : IStepFlowTool, IStepAware, IReviewableTool, IRunResult, IToolCleanup
    {
        // IStepAware: framework callback that rebuilds a step's content widget in place.
        private Action<string>? _rebuildContent;

        // Run strip: "tags" during the run, full filter/tag/failure breakdown on completion.
        public string? ResultNoun => "tags";
        private System.Collections.Generic.IReadOnlyList<LemoineTools.Framework.ResultChip>? _resultChips;
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Framework.ResultChip>? ResultChips => _resultChips;

        // Null the callbacks parked on the static handler so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog       = null;
            _handler.OnProgress    = null;
            _handler.OnComplete    = null;
            _handler.OnResultChips = null;
        }

        // ── Identity ─────────────────────────────────────────────────────────────
        public string Title    => AppStrings.T("ceilings.heatmap.title");
        public string RunLabel => AppStrings.T("ceilings.heatmap.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1",     AppStrings.T("ceilings.heatmap.steps.S1"), required: true),
            new StepDefinition("S_RAMP", AppStrings.T("ceilings.heatmap.steps.S_RAMP"),                required: false),
            new StepDefinition("S2",     AppStrings.T("ceilings.heatmap.steps.S2"),               required: false),
            new StepDefinition("S3",     AppStrings.T("ceilings.heatmap.steps.S3"),              required: false),
        };

        // ── Color ramp state ─────────────────────────────────────────────────────
        private string _colorLow  = CeilingHeatmapSettings.Instance.ColorLow;
        private string _colorMid  = CeilingHeatmapSettings.Instance.ColorMid;
        private string _colorHigh = CeilingHeatmapSettings.Instance.ColorHigh;

        private static readonly TemplateStore<CeilingColorRamp> _rampStore =
            new TemplateStore<CeilingColorRamp>(
                toolId:      "CeilingHeatmapRamp",
                serialize:   (data, path) => data.SaveTo(path),
                deserialize: path => CeilingColorRamp.LoadFrom(path));

        // Live-update handles for S_RAMP step
        private WpfRectangle? _gradientRect;
        private SingleSelect?  _rampCombo;

        // ── Run options state ────────────────────────────────────────────────────
        private bool   _deleteExisting = true;
        private bool   _placeTags      = CeilingHeatmapSettings.Instance.PlaceTags;
        private double _elevTolerance  = CeilingHeatmapSettings.Instance.ElevTolerance;

        // ── View selection state ─────────────────────────────────────────────────
        private List<long>                  _selectedViewIds = new List<long>();
        private readonly List<long>         _allViewIds      = new List<long>();
        private readonly BrowserTree _browserTree;

        // ── S1 mode: existing RCP views vs. generate one per level ──────────────
        private const string ModeExisting = "Existing";
        private const string ModeGenerate = "Generate";
        private string _viewMode = ModeExisting;

        public sealed class HeatmapLevelEntry
        {
            public ElementId Id          { get; set; } = ElementId.InvalidElementId;
            public string    Name        { get; set; } = "";
            public double    ElevationFt { get; set; }
        }

        public sealed class HeatmapTemplateEntry
        {
            public ElementId Id   { get; set; } = ElementId.InvalidElementId;
            public string    Name { get; set; } = "";
        }

        private readonly List<HeatmapLevelEntry>    _levels;
        private readonly List<HeatmapTemplateEntry> _rcpTemplates;
        private List<ElementId> _selectedGenerateLevelIds = new List<ElementId>();
        private string          _generateSuffix           = "_Heatmap";
        private ElementId       _generateTemplateId       = ElementId.InvalidElementId;
        private Dictionary<string, ElementId> _levelKeyToId = new Dictionary<string, ElementId>(StringComparer.Ordinal);

        // ── Debug wiring ─────────────────────────────────────────────────────────
        private static CeilingHeatmapDebugHandler? _debugHandler;
        private static ExternalEvent?              _debugEvent;

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
            Dictionary<string, List<(string Name, ElementId Id)>>? viewsByLevel = null,
            BrowserTree?         browserTree = null,
            List<HeatmapLevelEntry>?    levels = null,
            List<HeatmapTemplateEntry>? rcpTemplates = null)
        {
            _handler     = handler;
            _event       = externalEvent;
            _browserTree = browserTree ?? new BrowserTree();
            _levels       = levels       ?? new List<HeatmapLevelEntry>();
            _rcpTemplates = rcpTemplates ?? new List<HeatmapTemplateEntry>();

            if (viewsByLevel != null)
                foreach (var kvp in viewsByLevel)
                    foreach (var (_, id) in kvp.Value)
                        _allViewIds.Add(id.Value);
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
                case "S3":     return null; // framework renders review (IReviewableTool)
                default:       return null;
            }
        }

        // ── S1: View selection ───────────────────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            var outer = new StackPanel();

            string existingLabel = AppStrings.T("ceilings.heatmap.labels.modeExisting");
            string generateLabel = AppStrings.T("ceilings.heatmap.labels.modeGenerate");
            var modeSelect = new SingleSelect
            {
                Width = 300,
                HorizontalAlignment = HorizontalAlignment.Left,
                Items = new List<string> { existingLabel, generateLabel },
                SelectedItem = _viewMode == ModeGenerate ? generateLabel : existingLabel,
            };

            var body = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

            void RebuildBody()
            {
                body.Children.Clear();
                body.Children.Add(_viewMode == ModeGenerate ? BuildGenerateBody() : BuildExistingBody());
            }

            modeSelect.SelectionChanged += v =>
            {
                _viewMode = v == generateLabel ? ModeGenerate : ModeExisting;
                RebuildBody();
                OnValidationChanged();
            };

            outer.Children.Add(modeSelect);
            outer.Children.Add(body);
            RebuildBody();
            return outer;
        }

        private FrameworkElement BuildExistingBody()
        {
            if (_allViewIds.Count == 0)
            {
                var msg = new TextBlock
                {
                    Text         = AppStrings.T("ceilings.heatmap.labels.noViews"),
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                };
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                return msg;
            }

            var picker = new BrowserTreePicker
            {
                Height         = 300,
                AccessibleName = AppStrings.T("ceilings.heatmap.labels.pickerName"),
            };
            // Subscribe BEFORE SetTree — its end-of-setup SelectionChanged seeds the mirror list.
            picker.SelectionChanged += ids =>
            {
                _selectedViewIds = ids.ToList();
                OnValidationChanged();
            };
            picker.SetTree(_browserTree, _allViewIds, _selectedViewIds.ToList());
            return picker;
        }

        private FrameworkElement BuildGenerateBody()
        {
            var outer = new StackPanel();

            if (_levels.Count == 0)
            {
                outer.Children.Add(new TextBlock
                {
                    Text = AppStrings.T("ceilings.heatmap.labels.noLevels"),
                    TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic,
                });
                return outer;
            }

            _levelKeyToId.Clear();
            var groups = new Dictionary<string, List<string>>
            {
                ["Levels"] = _levels.OrderBy(l => l.ElevationFt).Select(l =>
                {
                    string key = AppStrings.T("ceilings.heatmap.labels.levelRow", l.Name, l.ElevationFt);
                    _levelKeyToId[key] = l.Id;
                    return key;
                }).ToList(),
            };
            var selectedIdSet = new HashSet<long>(_selectedGenerateLevelIds.Select(id => id.Value));
            var preSelected = _levelKeyToId.Where(kv => selectedIdSet.Contains(kv.Value.Value)).Select(kv => kv.Key).ToList();

            var levelTabs = new MultiSelectTabs();
            levelTabs.SelectionChanged += selected =>
            {
                _selectedGenerateLevelIds = selected
                    .Where(k => _levelKeyToId.ContainsKey(k))
                    .Select(k => _levelKeyToId[k])
                    .ToList();
                OnValidationChanged();
            };
            // First build: nothing selected yet, so default to all levels. SetGroups fires
            // SelectionChanged once at the end of setup (subscribed above), which syncs
            // _selectedGenerateLevelIds to whatever list is passed here — including on
            // later rebuilds, where the user's own selection is preserved via preSelected.
            levelTabs.SetGroups(groups, preSelected.Count > 0 ? preSelected : _levelKeyToId.Keys.ToList());
            outer.Children.Add(levelTabs);

            outer.Children.Add(new FrameworkElement { Height = 10 });

            var suffixLabel = new TextBlock { Text = AppStrings.T("ceilings.heatmap.labels.generateSuffix"), Margin = new Thickness(0, 0, 0, 4) };
            suffixLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            suffixLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            suffixLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(suffixLabel);

            var suffixBox = new WpfTextBox { Text = _generateSuffix, Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
            suffixBox.SetResourceReference(FrameworkElement.HeightProperty,                "LemoineH_Input");
            suffixBox.SetResourceReference(System.Windows.Controls.Control.PaddingProperty,"LemoineTh_InputPad");
            suffixBox.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            suffixBox.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            suffixBox.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_SM");
            suffixBox.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineMonoFont");
            suffixBox.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorderMid");
            suffixBox.TextChanged += (s, e) => { _generateSuffix = suffixBox.Text; OnValidationChanged(); };
            outer.Children.Add(suffixBox);

            outer.Children.Add(new FrameworkElement { Height = 10 });

            var tmplLabel = new TextBlock { Text = AppStrings.T("ceilings.heatmap.labels.generateTemplate"), Margin = new Thickness(0, 0, 0, 4) };
            tmplLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tmplLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tmplLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(tmplLabel);

            string noneLabel = AppStrings.T("ceilings.heatmap.labels.generateTemplateNone");
            var tmplNames = new List<string> { noneLabel }.Concat(_rcpTemplates.Select(t => t.Name)).ToList();
            string curTmplName = _rcpTemplates.FirstOrDefault(t => t.Id.Value == _generateTemplateId.Value)?.Name ?? noneLabel;
            var tmplSelect = new SingleSelect { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Items = tmplNames, SelectedItem = curTmplName };
            tmplSelect.SelectionChanged += name =>
            {
                var entry = _rcpTemplates.FirstOrDefault(t => t.Name == name);
                _generateTemplateId = entry?.Id ?? ElementId.InvalidElementId;
            };
            outer.Children.Add(tmplSelect);

            return outer;
        }

        // ── S_RAMP: Color Ramp step ──────────────────────────────────────────────
        private FrameworkElement BuildS_RAMP()
        {
            var outer = new StackPanel();

            // ── Section label: Saved Ramps ──────────────────────────────────────
            var savedLabel = new TextBlock { Text = AppStrings.T("ceilings.heatmap.labels.savedRamps"), Margin = new Thickness(0, 0, 0, 6) };
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

            var deleteBtn = ControlStyles.BuildSmallButton(AppStrings.T("ceilings.heatmap.labels.delete"),
                ControlStyles.ButtonVariant.Ghost);
            deleteBtn.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(deleteBtn, Dock.Right);

            var loadBtn = ControlStyles.BuildSmallButton(AppStrings.T("ceilings.heatmap.labels.load"),
                ControlStyles.ButtonVariant.Ghost);
            DockPanel.SetDock(loadBtn, Dock.Right);

            _rampCombo = new SingleSelect();
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

            var saveBtn = ControlStyles.BuildSmallButton(AppStrings.T("ceilings.heatmap.labels.save"),
                ControlStyles.ButtonVariant.Primary);
            saveBtn.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(saveBtn, Dock.Right);

            var nameBox = new WpfTextBox { ToolTip = AppStrings.T("ceilings.heatmap.labels.rampNameTip") };
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
                        // Rebuild the step through the framework so the swapped-in content is a
                        // real child of the step's host (StepFlowWindow.RefreshStepContent). The
                        // previous hand-rolled rebuild re-parented children that still belonged to
                        // a throwaway panel — Children.Add then threw and hard-crashed Revit.
                        _rebuildContent?.Invoke("S_RAMP");
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
            var stopsLabel = new TextBlock { Text = AppStrings.T("ceilings.heatmap.labels.colorStops"), Margin = new Thickness(0, 0, 0, 8) };
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

                var swatch = ColorPickerWindow.BuildColorPickerSwatch(
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

            AddChip(0, AppStrings.T("ceilings.heatmap.labels.chipLow"),  () => _colorLow,  h => _colorLow  = h);
            AddChip(2, AppStrings.T("ceilings.heatmap.labels.chipMid"),  () => _colorMid,  h => _colorMid  = h);
            AddChip(4, AppStrings.T("ceilings.heatmap.labels.chipHigh"), () => _colorHigh, h => _colorHigh = h);
            outer.Children.Add(chipsGrid);

            // ── Gradient preview ─────────────────────────────────────────────────
            var previewLabel = new TextBlock { Text = AppStrings.T("ceilings.heatmap.labels.rampPreview"), Margin = new Thickness(0, 0, 0, 6) };
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
            _rampCombo.Items = names; // SingleSelect auto-selects the first entry
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

        // ── IStepAware ────────────────────────────────────────────────────────────
        // Store the framework's in-place content-refresh callback (set by StepFlowWindow).
        public void SetContentRefreshCallback(Action<string> rebuildStepContent)
            => _rebuildContent = rebuildStepContent;

        // The ramp step depends only on its own state, so no rebuild is needed on entry;
        // each BuildS_RAMP reassigns _gradientRect / _rampCombo, so a framework rebuild is
        // self-consistent.
        public void OnStepActivated(string stepId) { }

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
            var tog = new ToggleSwitches();
            tog.SetItems(new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id        = "delete",
                    Label     = AppStrings.T("ceilings.heatmap.labels.toggleDeleteLabel"),
                    Desc      = AppStrings.T("ceilings.heatmap.labels.toggleDeleteDesc"),
                    DefaultOn = _deleteExisting,
                },
                new ToggleItem
                {
                    Id        = "tags",
                    Label     = AppStrings.T("ceilings.heatmap.labels.toggleTagsLabel"),
                    Desc      = AppStrings.T("ceilings.heatmap.labels.toggleTagsDesc"),
                    DefaultOn = _placeTags,
                },
            });
            tog.StateChanged += state =>
            {
                _deleteExisting = state.TryGetValue("delete", out bool dv) && dv;
                _placeTags      = state.TryGetValue("tags",   out bool tv) && tv;
                OnValidationChanged();
            };
            outer.Children.Add(tog);

            // ── Elevation tolerance ───────────────────────────────────────────────
            outer.Children.Add(new FrameworkElement { Height = 14 });

            var tolLabel = new TextBlock { Text = AppStrings.T("ceilings.heatmap.labels.elevTol"), Margin = new Thickness(0, 0, 0, 6) };
            tolLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tolLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tolLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(tolLabel);

            var tolRow = new StackPanel { Orientation = Orientation.Horizontal };

            double displayInches = Math.Round(_elevTolerance * 12.0, 2);
            var tolStepper = new InlineStepper
            {
                Value             = displayInches,
                MinValue          = 0,
                MaxValue          = 24,
                Step              = 0.25,
                Decimals          = 2,
                ValueWidth        = 56,
                VerticalAlignment = VerticalAlignment.Center,
            };
            tolStepper.ValueChanged += (s, v) => _elevTolerance = v / 12.0;
            tolRow.Children.Add(tolStepper);

            var tolUnit = new TextBlock { Text = AppStrings.T("ceilings.heatmap.labels.inUnit"), VerticalAlignment = VerticalAlignment.Center };
            tolUnit.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tolUnit.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tolUnit.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tolRow.Children.Add(tolUnit);

            outer.Children.Add(tolRow);

            var tolHint = new TextBlock
            {
                Text         = AppStrings.T("ceilings.heatmap.labels.tolHint"),
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

        // ── IReviewableTool (P3) — framework renders the review step ───────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("views",  AppStrings.T("ceilings.heatmap.review.itemViews")),
            ("ramp",   AppStrings.T("ceilings.heatmap.review.itemRamp")),
            ("delete", AppStrings.T("ceilings.heatmap.review.itemDelete")),
            ("tags",   AppStrings.T("ceilings.heatmap.review.itemTags")),
            ("tol",    AppStrings.T("ceilings.heatmap.review.itemTol")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["views"]  = _viewMode == ModeGenerate
                ? (_selectedGenerateLevelIds.Count == 0 ? "—"
                    : AppStrings.T("ceilings.heatmap.review.generateValue", _selectedGenerateLevelIds.Count))
                : (_selectedViewIds.Count == 0 ? "—"
                    : AppStrings.T("ceilings.heatmap.review.viewsValue", _selectedViewIds.Count)),
            ["ramp"]   = AppStrings.T("ceilings.heatmap.labels.rampDisplay", _colorLow, _colorMid, _colorHigh),
            ["delete"] = _deleteExisting ? AppStrings.T("ceilings.heatmap.review.yes") : AppStrings.T("ceilings.heatmap.review.no"),
            ["tags"]   = _placeTags ? AppStrings.T("ceilings.heatmap.review.yes") : AppStrings.T("ceilings.heatmap.review.no"),
            ["tol"]    = AppStrings.T("ceilings.heatmap.review.tolValue", Math.Round(_elevTolerance * 12.0, 4)),
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => AppStrings.T("ceilings.heatmap.review.note");
        public string?        ReviewWarning => null;

        // ═════════════════════════════════════════════════════════════════════════
        // IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1")
                return _viewMode == ModeGenerate
                    ? _selectedGenerateLevelIds.Count > 0
                    : _selectedViewIds.Count > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _viewMode == ModeGenerate
                    ? (_selectedGenerateLevelIds.Count == 0 ? "—"
                        : AppStrings.T("ceilings.heatmap.summaries.levelsSelected", _selectedGenerateLevelIds.Count))
                    : (_selectedViewIds.Count == 0 ? "—"
                        : AppStrings.T("ceilings.heatmap.summaries.viewsSelected", _selectedViewIds.Count));
            if (stepId == "S_RAMP")
                return AppStrings.T("ceilings.heatmap.labels.rampDisplay", _colorLow, _colorMid, _colorHigh);
            if (stepId == "S2")
            {
                var parts = new List<string>();
                if (_deleteExisting) parts.Add(AppStrings.T("ceilings.heatmap.summaries.s2Delete"));
                if (_placeTags)      parts.Add(AppStrings.T("ceilings.heatmap.summaries.s2Tags"));
                parts.Add(AppStrings.T("ceilings.heatmap.summaries.s2Tol", Math.Round(_elevTolerance * 12.0, 4)));
                return string.Join(" · ", parts);
            }
            if (stepId == "S3") return AppStrings.T("ceilings.heatmap.summaries.S3");
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _resultChips = null;   // clear any breakdown from a previous run

            // Persist run options back to settings
            CeilingHeatmapSettings.Instance.PlaceTags     = _placeTags;
            CeilingHeatmapSettings.Instance.ElevTolerance = _elevTolerance;
            SaveColorsToSettings();

            if (_viewMode == ModeGenerate)
            {
                _handler!.SelectedViewIds     = new List<ElementId>();
                _handler.GenerateForLevelIds  = new List<ElementId>(_selectedGenerateLevelIds);
                _handler.GenerateSuffix       = string.IsNullOrWhiteSpace(_generateSuffix) ? "_Heatmap" : _generateSuffix;
                _handler.GenerateTemplateId   = _generateTemplateId;
            }
            else
            {
                _handler!.SelectedViewIds    = _selectedViewIds.Select(id => new ElementId(id)).ToList();
                _handler.GenerateForLevelIds = new List<ElementId>();
            }
            _handler.DeleteExisting  = _deleteExisting;
            _handler.PlaceTags       = _placeTags;
            _handler.ElevTolerance   = _elevTolerance;
            _handler.ColorLow        = ParseRevitColor(_colorLow,  0,   0, 255);
            _handler.ColorMid        = ParseRevitColor(_colorMid,  0, 255,   0);
            _handler.ColorHigh       = ParseRevitColor(_colorHigh, 255,  0,   0);
            _handler.PushLog         = pushLog;
            _handler.OnProgress      = onProgress;
            _handler.OnComplete      = onComplete;
            _handler.OnResultChips   = chips => _resultChips = chips;
            pushLog(AppStrings.T("ceilings.heatmap.log.starting"), "info");
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
