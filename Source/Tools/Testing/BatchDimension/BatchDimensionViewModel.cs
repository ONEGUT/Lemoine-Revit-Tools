using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

using WpfGrid       = System.Windows.Controls.Grid;
using WpfTextBox    = System.Windows.Controls.TextBox;
using WpfComboBox   = System.Windows.Controls.ComboBox;
using WpfVisibility = System.Windows.Visibility;

namespace LemoineTools.Tools.Testing
{
    /// <summary>
    /// View model for the Batch Dimension tool (Ty).
    /// 3-step wizard: Select Views → Categories &amp; Settings → Review &amp; Run.
    /// </summary>
    public class BatchDimensionViewModel : ILemoineTool, ILemoineToolSettings, ILemoineReviewable
    {
        public string Title    => "Batch Dimension";
        public string RunLabel => "Place Dimensions in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Views",          required: true),
            new StepDefinition("S2", "Categories & Settings", required: true),
            new StepDefinition("S3", "Review & Run",          required: false),
        };

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── Category catalog ──────────────────────────────────────────────────
        private static readonly (string Name, string OST)[] SupportedCategories =
        {
            ("Walls",                "OST_Walls"),
            ("Doors",                "OST_Doors"),
            ("Windows",              "OST_Windows"),
            ("Grids",                "OST_Grids"),
            ("Structural Columns",   "OST_StructuralColumns"),
            ("Structural Framing",   "OST_StructuralFraming"),
        };

        private static readonly Dictionary<string, string[]> ReferencePlaneOptions =
            new Dictionary<string, string[]>
            {
                ["Walls"]              = new[] { "Face of Core Exterior", "Face of Core Interior", "Center of Wall", "Face of Finish Exterior", "Face of Finish Interior" },
                ["Doors"]             = new[] { "Center", "Left Face", "Right Face" },
                ["Windows"]           = new[] { "Center", "Left Face", "Right Face" },
                ["Grids"]             = new[] { "Grid Line" },
                ["Structural Columns"] = new[] { "Center of Column" },
                ["Structural Framing"] = new[] { "Face", "Center" },
            };

        private static readonly string[] TieConditionOptions =
            { "Tie to Nearest Grid", "None" };

        // ── State ─────────────────────────────────────────────────────────────
        private List<string>         _selectedViewNames = new List<string>();
        private Dictionary<string, ElementId> _viewNameToId = new Dictionary<string, ElementId>();
        private List<DimCategoryConfig> _categories;
        private string               _dimStyleName = BatchDimensionSettings.Instance.DefaultDimStyleName;

        // ── Data from Revit ───────────────────────────────────────────────────
        private readonly List<string> _dimStyleNames;
        private readonly List<View>   _allViews;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly BatchDimensionEventHandler? _handler;
        private readonly ExternalEvent?              _event;

        // ── Constructor ───────────────────────────────────────────────────────

        public BatchDimensionViewModel(
            BatchDimensionEventHandler? handler,
            ExternalEvent?              externalEvent,
            List<string>?               dimStyleNames,
            List<View>?                 allViews)
        {
            _handler       = handler;
            _event         = externalEvent;
            _dimStyleNames = dimStyleNames ?? new List<string>();
            _allViews      = allViews      ?? new List<View>();

            foreach (var v in _allViews)
            {
                if (!_viewNameToId.ContainsKey(v.Name))
                    _viewNameToId[v.Name] = v.Id;
            }

            // Start with one default category row (Walls)
            var s = BatchDimensionSettings.Instance;
            _categories = new List<DimCategoryConfig>
            {
                new DimCategoryConfig
                {
                    CategoryName  = "Walls",
                    BuiltInCatOST = "OST_Walls",
                    ReferencePlane = s.DefaultReferencePlane,
                    TieCondition   = s.DefaultTieCondition,
                    Offset         = s.DefaultOffset,
                }
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        //  GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return null; // framework renders review (ILemoineReviewable)
                default:   return null;
            }
        }

        // ── Step 1 — Select Views ─────────────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            var outer = new StackPanel();

            // Show-all checkbox
            var showAllCb = new CheckBox
            {
                Content   = "Show all non-template views",
                IsChecked = false,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin    = new Thickness(0, 0, 0, 6),
                Tag       = false,
            };
            showAllCb.SetResourceReference(CheckBox.ForegroundProperty,  "LemoineText");
            showAllCb.SetResourceReference(CheckBox.FontFamilyProperty,  "LemoineUiFont");
            showAllCb.SetResourceReference(CheckBox.FontSizeProperty,    "LemoineFS_SM");
            outer.Children.Add(showAllCb);

            // Multi-select
            LemoineMultiSelectTabs? tabs = null;

            void RebuildTabs(bool showAll)
            {
                if (tabs != null) outer.Children.Remove(tabs);
                tabs = new LemoineMultiSelectTabs();
                tabs.SetGroups(BuildViewGroups(showAll));
                tabs.SelectionChanged += selected =>
                {
                    _selectedViewNames = new List<string>(selected);
                    Fire();
                };
                outer.Children.Add(tabs);
            }

            showAllCb.Checked   += (s, e) => { showAllCb.Tag = true;  RebuildTabs(true);  Fire(); };
            showAllCb.Unchecked += (s, e) => { showAllCb.Tag = false; RebuildTabs(false); Fire(); };

            RebuildTabs(false);
            return outer;
        }

        private Dictionary<string, List<string>> BuildViewGroups(bool showAll)
        {
            var groups = new Dictionary<string, List<string>>
            {
                ["Floor Plans"]             = new List<string>(),
                ["Reflected Ceiling Plans"] = new List<string>(),
                ["Sections"]                = new List<string>(),
                ["Elevations"]              = new List<string>(),
                ["Drafting Views"]          = new List<string>(),
            };

            foreach (var v in _allViews)
            {
                string key = v.Name;
                if (!_viewNameToId.ContainsKey(key)) _viewNameToId[key] = v.Id;

                string group;
                if (v is ViewPlan vp)
                    group = vp.ViewType == ViewType.CeilingPlan ? "Reflected Ceiling Plans" : "Floor Plans";
                else if (v is ViewSection)
                    group = v.ViewType == ViewType.Elevation ? "Elevations" : "Sections";
                else if (v.ViewType == ViewType.DraftingView)
                    group = "Drafting Views";
                else if (showAll)
                    group = v.ViewType.ToString();
                else
                    continue;

                if (!groups.ContainsKey(group)) groups[group] = new List<string>();
                groups[group].Add(key);
            }

            // Remove empty groups
            foreach (var emptyKey in groups.Keys.Where(k => groups[k].Count == 0).ToList())
                groups.Remove(emptyKey);

            if (groups.Count == 0)
                groups["(No views)"] = new List<string>();

            return groups;
        }

        // ── Step 2 — Categories & Settings ───────────────────────────────────
        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel();
            outer.Tag  = "categoryOuter";

            // Header bar
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

            var hdrLabel = new TextBlock { Text = "CATEGORIES", VerticalAlignment = VerticalAlignment.Center };
            hdrLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            hdrLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            hdrLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            hdrLabel.FontStyle = FontStyles.Italic;
            DockPanel.SetDock(hdrLabel, Dock.Left);

            var addBtn = LemoineControlStyles.BuildButton("+ Add Category");
            addBtn.HorizontalAlignment = HorizontalAlignment.Right;
            DockPanel.SetDock(addBtn, Dock.Right);

            header.Children.Add(addBtn);
            header.Children.Add(hdrLabel);
            outer.Children.Add(header);

            // Category rows container
            var rowsPanel = new StackPanel { Tag = "rowsPanel" };
            outer.Children.Add(rowsPanel);

            // Populate initial rows
            foreach (var cat in _categories)
                rowsPanel.Children.Add(BuildCategoryRow(cat, rowsPanel));

            addBtn.Click += (s, e) =>
            {
                var newCat = new DimCategoryConfig
                {
                    CategoryName   = "Walls",
                    BuiltInCatOST  = "OST_Walls",
                    ReferencePlane = BatchDimensionSettings.Instance.DefaultReferencePlane,
                    TieCondition   = BatchDimensionSettings.Instance.DefaultTieCondition,
                    Offset         = BatchDimensionSettings.Instance.DefaultOffset,
                };
                _categories.Add(newCat);
                rowsPanel.Children.Add(BuildCategoryRow(newCat, rowsPanel));
                RefreshRemoveButtons(rowsPanel);
                Fire();
            };

            // Dimension style section
            AddDivider(outer);
            var dimLbl = new TextBlock
            {
                Text         = "DIMENSION STYLE",
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            };
            dimLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            dimLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            dimLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(dimLbl);

            var styleItems = _dimStyleNames.Count > 0
                ? _dimStyleNames.ToArray()
                : new[] { "(No dimension styles found)" };
            int styleIdx = styleItems.Contains(_dimStyleName)
                ? Array.IndexOf(styleItems, _dimStyleName)
                : 0;
            string? initialStyle = (styleIdx >= 0 && styleIdx < styleItems.Length)
                ? styleItems[styleIdx]
                : null;

            var styleCombo = new LemoineSingleSelect
            {
                Items        = styleItems,
                SelectedItem = initialStyle,
            };
            styleCombo.SelectionChanged += val =>
            {
                if (val == null) return;
                _dimStyleName = val;
                Fire();
            };
            if (initialStyle != null)
                _dimStyleName = initialStyle;
            outer.Children.Add(styleCombo);

            return outer;
        }

        private Border BuildCategoryRow(DimCategoryConfig cat, StackPanel rowsPanel)
        {
            var row = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                Margin          = new Thickness(0, 0, 0, 8),
                Padding         = new Thickness(0, 0, 0, 8),
                Tag             = cat,
            };
            row.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var inner = new StackPanel();
            row.Child = inner;

            // Row header — category ComboBox + remove button
            var headerDp = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

            var catCombo = new WpfComboBox
            {
                ItemsSource       = SupportedCategories.Select(c => c.Name).ToArray(),
                SelectedItem      = cat.CategoryName,
                IsEditable        = false,
                MaxDropDownHeight = 200,
            };
            catCombo.SetResourceReference(WpfComboBox.BackgroundProperty, "LemoineSelectBg");
            catCombo.SetResourceReference(WpfComboBox.ForegroundProperty, "LemoineText");
            catCombo.SetResourceReference(WpfComboBox.FontFamilyProperty, "LemoineUiFont");
            catCombo.SetResourceReference(WpfComboBox.FontSizeProperty,   "LemoineFS_MD");
            LemoineControlStyles.WireComboWheelBubbling(catCombo); // don't eat page scroll when closed

            var removeBtn = LemoineControlStyles.BuildButton("×");
            removeBtn.Margin = new Thickness(6, 0, 0, 0);
            removeBtn.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
            removeBtn.Tag = "removeBtn";

            DockPanel.SetDock(removeBtn, Dock.Right);
            headerDp.Children.Add(removeBtn);
            headerDp.Children.Add(catCombo);
            inner.Children.Add(headerDp);

            // Settings grid: Reference Plane | Tie Condition | Offset
            var settingsPanel = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            inner.Children.Add(settingsPanel);

            // Reference plane options depend on category
            string[] GetRefPlanes(string catName) =>
                ReferencePlaneOptions.TryGetValue(catName, out var opts) ? opts : new[] { "Face", "Center" };

            string[] currentRefPlanes = GetRefPlanes(cat.CategoryName);
            int refIdx = Array.IndexOf(currentRefPlanes, cat.ReferencePlane);
            if (refIdx < 0) refIdx = 0;

            var refLbl = new TextBlock { Text = "Reference Plane", Margin = new Thickness(0, 0, 0, 2) };
            refLbl.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            refLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            refLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            settingsPanel.Children.Add(refLbl);

            var refCombo = new WpfComboBox
            {
                ItemsSource       = currentRefPlanes,
                SelectedIndex     = Math.Max(0, refIdx),
                IsEditable        = false,
                MaxDropDownHeight = 200,
                Margin            = new Thickness(0, 0, 0, 6),
            };
            refCombo.SetResourceReference(WpfComboBox.BackgroundProperty, "LemoineSelectBg");
            refCombo.SetResourceReference(WpfComboBox.ForegroundProperty, "LemoineText");
            refCombo.SetResourceReference(WpfComboBox.FontFamilyProperty, "LemoineUiFont");
            refCombo.SetResourceReference(WpfComboBox.FontSizeProperty,   "LemoineFS_MD");
            refCombo.SelectionChanged += (s, e) =>
            {
                if (refCombo.SelectedItem is string val) { cat.ReferencePlane = val; Fire(); }
            };
            LemoineControlStyles.WireComboWheelBubbling(refCombo);
            settingsPanel.Children.Add(refCombo);

            // Tie condition
            int tieIdx = Array.IndexOf(TieConditionOptions, cat.TieCondition);
            if (tieIdx < 0) tieIdx = 0;

            var tieLbl = new TextBlock { Text = "Tie Condition", Margin = new Thickness(0, 0, 0, 2) };
            tieLbl.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            tieLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tieLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            settingsPanel.Children.Add(tieLbl);

            var tieCombo = new WpfComboBox
            {
                ItemsSource       = TieConditionOptions,
                SelectedIndex     = tieIdx,
                IsEditable        = false,
                MaxDropDownHeight = 200,
                Margin            = new Thickness(0, 0, 0, 6),
            };
            tieCombo.SetResourceReference(WpfComboBox.BackgroundProperty, "LemoineSelectBg");
            tieCombo.SetResourceReference(WpfComboBox.ForegroundProperty, "LemoineText");
            tieCombo.SetResourceReference(WpfComboBox.FontFamilyProperty, "LemoineUiFont");
            tieCombo.SetResourceReference(WpfComboBox.FontSizeProperty,   "LemoineFS_MD");
            tieCombo.SelectionChanged += (s, e) =>
            {
                if (tieCombo.SelectedItem is string val) { cat.TieCondition = val; Fire(); }
            };
            LemoineControlStyles.WireComboWheelBubbling(tieCombo);
            settingsPanel.Children.Add(tieCombo);

            // Offset
            var offLbl = new TextBlock { Text = "String Offset (mm)", Margin = new Thickness(0, 0, 0, 2) };
            offLbl.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            offLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            offLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            settingsPanel.Children.Add(offLbl);

            var offsetStepper = new LemoineInlineStepper
            {
                Value               = cat.Offset,
                MinValue            = 0,
                MaxValue            = 1000,
                Step                = 1,
                Decimals            = 0,
                ValueWidth          = 56,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin              = new Thickness(0, 0, 0, 4),
            };
            offsetStepper.ValueChanged += (s, v) => { cat.Offset = v; Fire(); };
            settingsPanel.Children.Add(offsetStepper);

            // Wire category change → update ref plane options
            catCombo.SelectionChanged += (s, e) =>
            {
                if (!(catCombo.SelectedItem is string newName)) return;
                var entry = SupportedCategories.FirstOrDefault(c => c.Name == newName);
                cat.CategoryName  = newName;
                cat.BuiltInCatOST = entry.OST ?? "OST_Walls";

                var newOpts = GetRefPlanes(newName);
                refCombo.ItemsSource   = newOpts;
                refCombo.SelectedIndex = 0;
                cat.ReferencePlane     = newOpts[0];
                Fire();
            };

            // Remove button handler
            removeBtn.Click += (s, e) =>
            {
                _categories.Remove(cat);
                rowsPanel.Children.Remove(row);
                RefreshRemoveButtons(rowsPanel);
                Fire();
            };

            return row;
        }

        private static void RefreshRemoveButtons(StackPanel panel)
        {
            // Hide remove button on the last remaining row
            bool onlyOne = panel.Children.Count == 1;
            foreach (UIElement child in panel.Children)
            {
                if (child is Border border && border.Child is StackPanel sp)
                {
                    foreach (UIElement spChild in sp.Children)
                    {
                        if (spChild is DockPanel dp)
                        {
                            foreach (UIElement dpChild in dp.Children)
                            {
                                if (dpChild is Button btn && (string?)btn.Tag == "removeBtn")
                                    btn.Visibility = onlyOne ? WpfVisibility.Collapsed : WpfVisibility.Visible;
                            }
                        }
                    }
                }
            }
        }

        // ── Step 3 — Review & Run ─────────────────────────────────────────────
                // ── ILemoineReviewable (P3) — framework renders the review step ───
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("views", "Views Selected"),
            ("cats",  "Categories"),
            ("style", "Dimension Style"),
            ("est",   "Estimated Strings"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["views"] = $"{_selectedViewNames.Count}",
            ["cats"]  = _categories.Count == 0 ? "—" : string.Join(", ", _categories.Select(c => c.CategoryName)),
            ["style"] = string.IsNullOrEmpty(_dimStyleName) ? "—" : _dimStyleName,
            ["est"]   = $"~{_selectedViewNames.Count * _categories.Count * 2} (approximate)",
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => "Dimension strings will be placed on each selected view for each " +
            "configured category. The estimated string count is approximate.";
        public string?        ReviewWarning => null;


        private static void AddDivider(System.Windows.Controls.Panel parent)
        {
            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            parent.Children.Add(sep);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedViewNames.Count > 0;
                case "S2": return _categories.Count > 0 && !string.IsNullOrEmpty(_dimStyleName);
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedViewNames.Count == 0 ? "—"
                    : $"{_selectedViewNames.Count} view(s) selected";
                case "S2": return _categories.Count == 0 ? "No categories"
                    : $"{string.Join(", ", _categories.Select(c => c.CategoryName))} — {_dimStyleName}";
                case "S3": return "Ready to run";
                default:   return "—";
            }
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

            // Persist defaults
            var s = BatchDimensionSettings.Instance;
            s.DefaultDimStyleName   = _dimStyleName;
            if (_categories.Count > 0)
            {
                s.DefaultReferencePlane = _categories[0].ReferencePlane;
                s.DefaultTieCondition   = _categories[0].TieCondition;
                s.DefaultOffset         = _categories[0].Offset;
            }
            s.Save();

            _handler.ViewIds      = _selectedViewNames
                .Where(n => _viewNameToId.ContainsKey(n))
                .Select(n => _viewNameToId[n])
                .ToList();
            _handler.Categories   = new List<DimCategoryConfig>(_categories);
            _handler.DimStyleName = _dimStyleName;
            _handler.PushLog      = pushLog;
            _handler.OnProgress   = onProgress;
            _handler.OnComplete   = onComplete;

            _event.Raise();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ILemoineToolSettings
        // ═════════════════════════════════════════════════════════════════════
        public LemoineToolSettingsSpec? GetSettingsSpec()
        {
            var s = BatchDimensionSettings.Instance;
            return new LemoineToolSettingsSpec
            {
                Id          = "ty",
                Label       = "Batch Dimension",
                Icon        = "Ty",
                Description = "Place dimension strings across multiple views by element category.",
                Groups      = new List<LemoineSettingsGroup>
                {
                    new LemoineSettingsGroup
                    {
                        Id = "G1", Title = "Defaults", OpenByDefault = true,
                        Settings = new List<LemoineSettingDef>
                        {
                            new LemoineSettingDef { Id = "dimstyle", Kind = "text", Label = "Default dimension style name",
                                Options = new TextOpts { Placeholder = "Linear Dimension Style" }, Default = s.DefaultDimStyleName },
                            new LemoineSettingDef { Id = "refplane", Kind = "single", Label = "Default reference plane (Walls)",
                                Options = new SingleSelectOpts { Items = new List<string>
                                    { "Face of Core Exterior", "Face of Core Interior", "Center of Wall",
                                      "Face of Finish Exterior", "Face of Finish Interior" } },
                                Default = s.DefaultReferencePlane },
                            new LemoineSettingDef { Id = "tiecond", Kind = "single", Label = "Default tie condition",
                                Options = new SingleSelectOpts { Items = new List<string>
                                    { "Tie to Nearest Grid", "None" } },
                                Default = s.DefaultTieCondition },
                            new LemoineSettingDef { Id = "offset", Kind = "number", Label = "Default string offset",
                                Options = new NumberOpts { Unit = "mm", Min = 0, Max = 500, Step = 1 },
                                Default = s.DefaultOffset },
                        }
                    },
                }
            };
        }

        public void ApplySettings(string groupId, string settingId, object value)
        {
            var s = BatchDimensionSettings.Instance;
            switch (settingId)
            {
                case "dimstyle": s.DefaultDimStyleName   = value as string ?? "";    break;
                case "refplane": s.DefaultReferencePlane = value as string ?? "";    break;
                case "tiecond":  s.DefaultTieCondition   = value as string ?? "";    break;
                case "offset":   if (value is double dv) s.DefaultOffset = dv;      break;
            }
            s.Save();
        }
    }
}
