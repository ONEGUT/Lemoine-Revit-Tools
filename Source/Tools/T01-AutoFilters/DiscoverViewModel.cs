using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.AutoFilters
{
    // =========================================================================
    // DiscoverViewModel — ILemoineTool, 5-step accordion
    //
    // Step flow:
    //   S1  Select which loaded Revit links to scan (one trade per link)
    //   S2  Choose Revit categories via LemoineMultiSelectTabs
    //   S3  Configure scan: mode (Whole Category | Per Value) + parameter per row
    //         → "Discover Rules" button fires DiscoverEventHandler (MainScan)
    //   S4  Review discovered rules: include/exclude, rename, pick colours
    //   S5  Summary card + Confirm triggers DiscoverEventHandler (Commit)
    // =========================================================================

    public class DiscoverViewModel : ILemoineTool
    {
        // ── Nested data classes ───────────────────────────────────────────────

        public class LinkEntry
        {
            public ElementId Id         { get; }
            public string    Label      { get; }
            public string    TradeName  { get; set; }
            public bool      IsSelected { get; set; }

            public LinkEntry(ElementId id, string label)
            {
                Id        = id;
                Label     = label;
                // Default trade name: link title (truncated to 20 chars if long)
                TradeName = label.Length > 20 ? label.Substring(0, 20).TrimEnd() : label;
            }
        }

        public class ScanConfigRow
        {
            public string   CategoryLabel   { get; }
            public string   OstCategory     { get; }
            public string   Mode            { get; set; } = "PerValue";
            public string   Parameter       { get; set; }
            public string[] AvailableParams { get; }

            /// <summary>True when Parameter is in the known-safe set for Revit API resolution.</summary>
            public bool IsKnownSafe => DiscoverEventHandler.KnownSafeParams.Contains(Parameter);

            public ScanConfigRow(string label, string ostCategory)
            {
                CategoryLabel   = label;
                OstCategory     = ostCategory;
                AvailableParams = AutoFiltersSettings.GetParametersFor(ostCategory);
                // Pick a safe default where possible
                Parameter = AvailableParams.FirstOrDefault(
                    p => DiscoverEventHandler.KnownSafeParams.Contains(p))
                    ?? (AvailableParams.Length > 0 ? AvailableParams[0] : "Type Name");
            }
        }

        public class DiscoveredRuleRow
        {
            public bool   IsIncluded       { get; set; } = true;
            public string RuleName         { get; set; } = "";
            public string HexColor         { get; set; } = "#888888";
            public string TradeName        { get; set; } = "";
            public int    ElementCount     { get; set; }
            public bool   IsWholeCategory  { get; set; }
            public string ParameterValue   { get; set; } = "";
            public string Parameter        { get; set; } = "Type Name";
            public List<string> BuiltInCategories { get; set; } = new List<string>();
        }

        // ── ILemoineTool identity ─────────────────────────────────────────────

        public string Title    => "Discover Rules";
        public string RunLabel => "Commit Rules →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Links",      required: true),
            new StepDefinition("S2", "Select Categories", required: true),
            new StepDefinition("S3", "Configure Scan",    required: true),
            new StepDefinition("S4", "Review Rules",      required: true),
            new StepDefinition("S5", "Confirm & Commit",  required: false),
        };

        public event EventHandler? ValidationChanged;
        private void RaiseValidation() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── State ─────────────────────────────────────────────────────────────

        private readonly DiscoverEventHandler               _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent    _event;
        private readonly List<LinkEntry>                    _links;

        private IReadOnlyCollection<string> _selectedCategories = Array.Empty<string>();
        private readonly List<ScanConfigRow>     _scanConfigRows  = new List<ScanConfigRow>();
        private readonly List<DiscoveredRuleRow> _discoveredRules = new List<DiscoveredRuleRow>();

        private bool       _scanComplete;
        private bool       _isScanning;
        private Dispatcher? _wpfDispatcher;  // captured on WPF STA thread

        // Mutable UI panels — built once by GetStepContent, updated dynamically
        private StackPanel?          _s3Panel;
        private TextBlock?           _s3ScanStatus;
        private Button?              _s3ScanBtn;
        private StackPanel?          _s4Panel;
        private LemoineReviewSummary? _s5Review;

        // ── Constructor ───────────────────────────────────────────────────────

        public DiscoverViewModel(
            DiscoverEventHandler             handler,
            Autodesk.Revit.UI.ExternalEvent  externalEvent,
            List<LinkEntry>                  links)
        {
            _handler = handler  ?? throw new ArgumentNullException(nameof(handler));
            _event   = externalEvent ?? throw new ArgumentNullException(nameof(externalEvent));
            _links   = links    ?? new List<LinkEntry>();
        }

        // ═════════════════════════════════════════════════════════════════════
        // GetStepContent — called once per step at window-open time
        // ═════════════════════════════════════════════════════════════════════

        public FrameworkElement? GetStepContent(string stepId)
        {
            // Capture the WPF STA dispatcher the first time any step is built.
            // GetStepContent is called from StepFlowWindow.BuildStepAccordion()
            // which runs on the WPF STA thread.
            if (_wpfDispatcher == null)
                _wpfDispatcher = Dispatcher.CurrentDispatcher;

            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return BuildS3();
                case "S4": return BuildS4();
                case "S5": return BuildS5();
                default:   return null;
            }
        }

        // ── S1 — Select Links ─────────────────────────────────────────────────

        private FrameworkElement BuildS1()
        {
            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, // ⚠ constrain horizontal
                MaxHeight = 320,
            };

            var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };

            var hdr = MakeNote("Select which loaded Revit links to scan. Each link produces one trade.");
            sp.Children.Add(hdr);

            if (_links.Count == 0)
            {
                sp.Children.Add(new LemoineWarnBanner(
                    "⚠  No loaded Revit links found in the active document."));
            }
            else
            {
                foreach (var link in _links)
                    sp.Children.Add(BuildLinkRow(link));
            }

            sv.Content = sp;
            return sv;
        }

        private FrameworkElement BuildLinkRow(LinkEntry link)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Margin          = new Thickness(0, 0, 0, 4),
                Padding         = new Thickness(8, 6, 8, 6),
            };
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");

            // Grid inside ScrollViewer (HorizontalScrollBarVisibility=Disabled) → width constrained ✓
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // checkbox
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // label
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // "Trade:"
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });                // trade name

            var cb = new CheckBox
            {
                IsChecked         = link.IsSelected,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 8, 0),
            };
            cb.Checked   += (s, e) => { link.IsSelected = true;  RaiseValidation(); };
            cb.Unchecked += (s, e) => { link.IsSelected = false; RaiseValidation(); };

            var lbl = new TextBlock
            {
                Text              = link.Label,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var tradeLbl = new TextBlock
            {
                Text              = "Trade:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 0, 6, 0),
            };
            tradeLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tradeLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tradeLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var tradeBox = new TextBox
            {
                Text              = link.TradeName,
                Padding           = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center,
                BorderThickness   = new Thickness(1),
            };
            tradeBox.SetResourceReference(TextBox.BackgroundProperty,  "LemoineSelectBg");
            tradeBox.SetResourceReference(TextBox.ForegroundProperty,  "LemoineText");
            tradeBox.SetResourceReference(TextBox.BorderBrushProperty, "LemoineBorder");
            tradeBox.SetResourceReference(TextBox.FontSizeProperty,    "LemoineFS_SM");
            tradeBox.SetResourceReference(TextBox.FontFamilyProperty,  "LemoineUiFont");
            tradeBox.TextChanged += (s, e) => { link.TradeName = tradeBox.Text; RaiseValidation(); };

            Grid.SetColumn(cb,       0);
            Grid.SetColumn(lbl,      1);
            Grid.SetColumn(tradeLbl, 2);
            Grid.SetColumn(tradeBox, 3);
            g.Children.Add(cb);
            g.Children.Add(lbl);
            g.Children.Add(tradeLbl);
            g.Children.Add(tradeBox);

            card.Child = g;
            return card;
        }

        // ── S2 — Select Categories ────────────────────────────────────────────

        private FrameworkElement BuildS2()
        {
            var sp = new StackPanel();
            sp.Children.Add(MakeNote(
                "Select the Revit categories you want to scan. " +
                "Each category can be configured independently in Step 3."));

            var tabs = new LemoineMultiSelectTabs { Height = 320 };
            tabs.SetGroups(new Dictionary<string, List<string>>
            {
                ["MEP / HVAC"]    = new List<string> { "Ducts","Duct Fittings","Duct Accessories","Duct Insulation","Duct Linings","Air Terminals","Flex Ducts","Mechanical Equipment","Fabrication Ductwork" },
                ["Piping"]        = new List<string> { "Pipes","Pipe Fittings","Pipe Accessories","Pipe Insulation","Pipe Linings","Flex Pipes","Plumbing Fixtures","Sprinklers","Fabrication Pipework","Fabrication Hangers","Fabrication Containment" },
                ["Electrical"]    = new List<string> { "Cable Trays","Cable Tray Fittings","Conduits","Conduit Fittings","Electrical Equipment","Electrical Fixtures","Lighting Fixtures","Lighting Devices","Communication Devices","Fire Alarm Devices","Security Devices","Data Devices","Telephone Devices","Nurse Call Devices" },
                ["Structural"]    = new List<string> { "Structural Framing","Structural Columns","Structural Foundations","Structural Trusses","Rebar" },
                ["Architectural"] = new List<string> { "Walls","Floors","Roofs","Ceilings","Doors","Windows","Generic Models","Rooms","Spaces" },
            });
            tabs.SelectionChanged += cats =>
            {
                _selectedCategories = cats;
                // Rebuild S3 rows whenever selection changes
                _wpfDispatcher?.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    RebuildS3Panel();
                    _scanComplete = false;
                    RaiseValidation();
                }));
            };
            sp.Children.Add(tabs);
            return sp;
        }

        // ── S3 — Configure Scan ───────────────────────────────────────────────

        private FrameworkElement BuildS3()
        {
            _s3Panel = new StackPanel();
            _s3Panel.Children.Add(MakeNote(
                "Select categories in Step 2, then configure the scan mode and parameter for each row here."));
            return _s3Panel;
        }

        private void RebuildS3Panel()
        {
            if (_s3Panel == null) return;
            _s3Panel.Children.Clear();
            _scanComplete = false;

            _scanConfigRows.Clear();
            foreach (var catLabel in _selectedCategories)
                if (AutoFiltersSettings.KnownCategoryMap.TryGetValue(catLabel, out var ost))
                    _scanConfigRows.Add(new ScanConfigRow(catLabel, ost));

            if (_scanConfigRows.Count == 0)
            {
                _s3Panel.Children.Add(MakeNote("No categories selected. Choose categories in Step 2."));
                return;
            }

            _s3Panel.Children.Add(MakeNote(
                "Configure each category: scan by individual parameter values, or treat the whole category as one rule."));

            // Table header
            _s3Panel.Children.Add(BuildS3Header());

            // One config row per selected category
            foreach (var row in _scanConfigRows)
                _s3Panel.Children.Add(BuildS3ConfigRow(row));

            // "Discover Rules →" button
            var btnRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _s3ScanBtn = LemoineControlStyles.BuildButton(
                "Discover Rules →",
                LemoineControlStyles.LemoineButtonVariant.Primary);
            _s3ScanBtn.HorizontalAlignment = HorizontalAlignment.Right;
            _s3ScanBtn.Click += OnScanButtonClick;
            Grid.SetColumn(_s3ScanBtn, 1);
            btnRow.Children.Add(_s3ScanBtn);
            _s3Panel.Children.Add(btnRow);

            // Status line (hidden until first scan)
            _s3ScanStatus = new TextBlock
            {
                Text       = "",
                Margin     = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            _s3ScanStatus.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _s3ScanStatus.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            _s3ScanStatus.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            _s3Panel.Children.Add(_s3ScanStatus);
        }

        private FrameworkElement BuildS3Header()
        {
            var border = new Border
            {
                Margin          = new Thickness(0, 0, 0, 2),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding         = new Thickness(4, 2, 4, 4),
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // label
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });                   // mode
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });                   // parameter
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });                    // info

            void H(string text, int col)
            {
                var tb = new TextBlock { Text = text };
                tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                Grid.SetColumn(tb, col);
                g.Children.Add(tb);
            }
            H("Category", 0);
            H("Mode",      1);
            H("Parameter", 2);
            border.Child = g;
            return border;
        }

        private FrameworkElement BuildS3ConfigRow(ScanConfigRow row)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Margin          = new Thickness(0, 0, 0, 3),
                Padding         = new Thickness(6, 5, 6, 5),
            };
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // label
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });                   // mode
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });                   // param
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });                    // info

            var catLbl = new TextBlock
            {
                Text              = row.CategoryLabel,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            catLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            catLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            catLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            Grid.SetColumn(catLbl, 0);
            g.Children.Add(catLbl);

            var modeCombo = new ComboBox
            {
                ItemsSource   = new[] { "Per Value", "Whole Category" },
                SelectedIndex = 0,
                Margin        = new Thickness(4, 0, 4, 0),
            };
            modeCombo.SetResourceReference(ComboBox.BackgroundProperty,  "LemoineSelectBg");
            modeCombo.SetResourceReference(ComboBox.ForegroundProperty,  "LemoineText");
            modeCombo.SetResourceReference(ComboBox.FontSizeProperty,    "LemoineFS_SM");
            modeCombo.SetResourceReference(ComboBox.FontFamilyProperty,  "LemoineUiFont");
            modeCombo.SetResourceReference(ComboBox.BorderBrushProperty, "LemoineBorder");
            Grid.SetColumn(modeCombo, 1);
            g.Children.Add(modeCombo);

            var paramCombo = new ComboBox
            {
                ItemsSource   = row.AvailableParams,
                SelectedIndex = 0,
                Margin        = new Thickness(0, 0, 4, 0),
            };
            paramCombo.SetResourceReference(ComboBox.BackgroundProperty,  "LemoineSelectBg");
            paramCombo.SetResourceReference(ComboBox.ForegroundProperty,  "LemoineText");
            paramCombo.SetResourceReference(ComboBox.FontSizeProperty,    "LemoineFS_SM");
            paramCombo.SetResourceReference(ComboBox.FontFamilyProperty,  "LemoineUiFont");
            paramCombo.SetResourceReference(ComboBox.BorderBrushProperty, "LemoineBorder");
            Grid.SetColumn(paramCombo, 2);
            g.Children.Add(paramCombo);

            // ⓘ indicator — shown when param is not in the known-safe set (Gap 2 mitigation)
            var infoTb = new TextBlock
            {
                Text              = row.IsKnownSafe ? "" : "ⓘ",
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment     = TextAlignment.Center,
                Margin            = new Thickness(2, 0, 0, 0),
            };
            if (!row.IsKnownSafe)
                infoTb.ToolTip = "This parameter may not resolve in all documents.\n" +
                                 "Known-safe: System Classification, Type Name, Family Name, " +
                                 "Fabrication Service, Structural Material.";
            infoTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            infoTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            infoTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            Grid.SetColumn(infoTb, 3);
            g.Children.Add(infoTb);

            // Wire events
            modeCombo.SelectionChanged += (s, e) =>
            {
                var sel = modeCombo.SelectedItem as string ?? "Per Value";
                row.Mode = (sel == "Whole Category") ? "WholeCategory" : "PerValue";
                paramCombo.Visibility = (row.Mode == "WholeCategory")
                    ? Visibility.Collapsed : Visibility.Visible;
                _scanComplete = false;
                RaiseValidation();
            };
            paramCombo.SelectionChanged += (s, e) =>
            {
                if (paramCombo.SelectedItem is string param)
                {
                    row.Parameter = param;
                    infoTb.Text   = row.IsKnownSafe ? "" : "ⓘ";
                    infoTb.ToolTip = row.IsKnownSafe ? null
                        : (object)"This parameter may not resolve in all documents.\n" +
                          "Known-safe: System Classification, Type Name, Family Name, " +
                          "Fabrication Service, Structural Material.";
                }
                _scanComplete = false;
                RaiseValidation();
            };

            card.Child = g;
            return card;
        }

        // ── S3 scan trigger ───────────────────────────────────────────────────

        private void OnScanButtonClick(object sender, RoutedEventArgs e)
        {
            if (_isScanning) return;

            var selectedLinks = _links.Where(l => l.IsSelected).ToList();
            if (selectedLinks.Count == 0 || _scanConfigRows.Count == 0) return;

            _isScanning = true;
            if (_s3ScanBtn    != null) _s3ScanBtn.IsEnabled = false;
            if (_s3ScanStatus != null)
            {
                _s3ScanStatus.Text       = "● Scanning…";
                _s3ScanStatus.Visibility = Visibility.Visible;
                _s3ScanStatus.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            }

            // Build scan specs: each selected link × each config row
            var specs = new List<ScanSpec>();
            foreach (var link in selectedLinks)
                foreach (var row in _scanConfigRows)
                    specs.Add(new ScanSpec
                    {
                        OstCategory = row.OstCategory,
                        Mode        = row.Mode,
                        Parameter   = row.Parameter,
                        LinkId      = link.Id.Value,
                        TradeName   = link.TradeName,
                    });

            _handler.Mode      = DiscoverMode.MainScan;
            _handler.ScanSpecs = specs;
            _handler.PushLog   = null;     // log not wired for intermediate scan
            _handler.OnProgress = null;
            // OnComplete dispatches back to WPF thread via _wpfDispatcher
            _handler.OnComplete = OnScanComplete;
            _event.Raise();
        }

        /// <summary>
        /// Called by DiscoverEventHandler.Execute() on Revit's main thread.
        /// Dispatches UI updates back to the WPF STA thread.
        /// </summary>
        private void OnScanComplete(int pass, int fail, int skip)
        {
            // ScanResults is written before OnComplete fires — safe to read here.
            var results = _handler.ScanResults;

            _wpfDispatcher?.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                _isScanning   = false;
                _scanComplete = results.Count > 0;

                if (_s3ScanBtn != null)
                    _s3ScanBtn.IsEnabled = true;

                if (_s3ScanStatus != null)
                {
                    if (_scanComplete)
                    {
                        _s3ScanStatus.Text = $"● {results.Count} rule(s) discovered.";
                        _s3ScanStatus.SetResourceReference(TextBlock.ForegroundProperty, "LemoineGreen");
                    }
                    else
                    {
                        _s3ScanStatus.Text = "● No rules found. Check link selection and categories.";
                        _s3ScanStatus.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
                    }
                    _s3ScanStatus.Visibility = Visibility.Visible;
                }

                PopulateS4(results);
                UpdateS5Summary();
                RaiseValidation();
            }));
        }

        // ── S4 — Review Rules ─────────────────────────────────────────────────

        private FrameworkElement BuildS4()
        {
            _s4Panel = new StackPanel();
            _s4Panel.Children.Add(MakeNote("Run the scan in Step 3 to see discovered rules here."));
            return _s4Panel;
        }

        private void PopulateS4(List<ScanResult> results)
        {
            if (_s4Panel == null) return;
            _s4Panel.Children.Clear();
            _discoveredRules.Clear();

            // Convert ScanResults → DiscoveredRuleRows
            foreach (var r in results)
            {
                _discoveredRules.Add(new DiscoveredRuleRow
                {
                    IsIncluded       = true,
                    RuleName         = r.IsWholeCategory
                        ? (AutoFiltersSettings.KnownCategoryMap
                               .FirstOrDefault(kvp => kvp.Value == r.OstCategory).Key
                           ?? r.OstCategory)
                        : r.ParameterValue,
                    HexColor         = r.HexColor,
                    TradeName        = r.TradeName,
                    ElementCount     = r.ElementCount,
                    IsWholeCategory  = r.IsWholeCategory,
                    ParameterValue   = r.ParameterValue,
                    Parameter        = r.Parameter,
                    BuiltInCategories = new List<string> { r.OstCategory },
                });
            }

            if (_discoveredRules.Count == 0)
            {
                _s4Panel.Children.Add(new LemoineWarnBanner(
                    "⚠  No rules discovered. Try a different scan mode or check your link selection."));
                return;
            }

            _s4Panel.Children.Add(MakeNote(
                $"{_discoveredRules.Count} rule(s) discovered. " +
                "Check to include, click the colour swatch to change colour, double-click the name to rename."));

            // Table header
            _s4Panel.Children.Add(BuildS4Header());

            // Scrollable rule rows
            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, // ⚠ constrain
                MaxHeight = 320,
            };
            var rowStack = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            foreach (var rule in _discoveredRules)
                rowStack.Children.Add(BuildS4RuleRow(rule));
            sv.Content = rowStack;
            _s4Panel.Children.Add(sv);
        }

        private FrameworkElement BuildS4Header()
        {
            var border = new Border
            {
                Margin          = new Thickness(0, 0, 0, 2),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding         = new Thickness(4, 2, 4, 4),
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });                    // check
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });                    // swatch
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // name
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });                   // trade
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });                    // count

            void H(string text, int col)
            {
                var tb = new TextBlock { Text = text };
                tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                Grid.SetColumn(tb, col);
                g.Children.Add(tb);
            }
            H("",        0);
            H("",        1);
            H("Rule Name", 2);
            H("Trade",     3);
            H("Count",     4);
            border.Child = g;
            return border;
        }

        private FrameworkElement BuildS4RuleRow(DiscoveredRuleRow rule)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Margin          = new Thickness(0, 0, 0, 2),
                Padding         = new Thickness(4, 4, 4, 4),
            };
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");

            // Grid inside ScrollViewer (HorizontalScrollBarVisibility=Disabled) → width constrained ✓
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });

            // Include checkbox
            var cb = new CheckBox
            {
                IsChecked         = rule.IsIncluded,
                VerticalAlignment = VerticalAlignment.Center,
            };
            cb.Checked   += (s, e) => { rule.IsIncluded = true;  UpdateS5Summary(); RaiseValidation(); };
            cb.Unchecked += (s, e) => { rule.IsIncluded = false; UpdateS5Summary(); RaiseValidation(); };
            Grid.SetColumn(cb, 0);
            g.Children.Add(cb);

            // Color swatch — uses shared factory (handles open-picker, update hex, update UI)
            var swatchPanel = LemoineColorPickerWindow.BuildColorPickerSwatch(
                getHex: () => rule.HexColor,
                setHex: hex => { rule.HexColor = hex; },
                showHexLabel: false);
            swatchPanel.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(swatchPanel, 1);
            g.Children.Add(swatchPanel);

            // Rule name — editable inline
            var nameEdit = new LemoineInlineEdit
            {
                Text              = rule.RuleName,
                FontSizeKey       = "LemoineFS_SM",
                Margin            = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            nameEdit.TextCommitted += (s, name) => rule.RuleName = name;
            Grid.SetColumn(nameEdit, 2);
            g.Children.Add(nameEdit);

            // Trade name
            var tradeTb = new TextBlock
            {
                Text              = rule.TradeName,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            tradeTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tradeTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tradeTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            Grid.SetColumn(tradeTb, 3);
            g.Children.Add(tradeTb);

            // Element count
            var countTb = new TextBlock
            {
                Text              = rule.ElementCount.ToString(),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment     = TextAlignment.Right,
            };
            countTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            countTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            countTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            Grid.SetColumn(countTb, 4);
            g.Children.Add(countTb);

            card.Child = g;
            return card;
        }

        // ── S5 — Confirm & Commit ─────────────────────────────────────────────

        private FrameworkElement BuildS5()
        {
            var sp = new StackPanel();

            _s5Review = new LemoineReviewSummary { Margin = new Thickness(0, 0, 0, 12) };
            sp.Children.Add(_s5Review);

            // ⚠  Gap 4 mitigation: static note about existing Revit filter elements
            sp.Children.Add(new LemoineWarnBanner(
                "⚠  Existing Revit filter elements are not updated automatically. " +
                "After committing, run 'Auto Filters' to apply updated rules to your views.",
                bottomMargin: 0));

            return sp;
        }

        private void UpdateS5Summary()
        {
            if (_s5Review == null) return;
            var included = _discoveredRules.Where(r => r.IsIncluded).ToList();
            int tradeCount = included.Select(r => r.TradeName)
                                     .Distinct(StringComparer.OrdinalIgnoreCase).Count();

            var items = new List<(string id, string label)>
            {
                ("total",   "Total Rules"),
                ("include", "To Commit"),
                ("skip",    "To Skip"),
                ("trades",  "Trades Affected"),
            };
            var values = new Dictionary<string, string>
            {
                ["total"]   = _discoveredRules.Count.ToString(),
                ["include"] = included.Count.ToString(),
                ["skip"]    = (_discoveredRules.Count - included.Count).ToString(),
                ["trades"]  = tradeCount.ToString(),
            };
            var chips = included.Select(r => r.RuleName).Distinct().Take(24).ToList();
            _s5Review.SetItems(items, values, chips);
        }

        // ═════════════════════════════════════════════════════════════════════
        // IsValid, SummaryFor, Run
        // ═════════════════════════════════════════════════════════════════════

        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1":
                    return _links.Any(l => l.IsSelected)
                        && _links.Where(l => l.IsSelected)
                                 .All(l => !string.IsNullOrWhiteSpace(l.TradeName));
                case "S2":
                    return _selectedCategories.Count > 0;
                case "S3":
                    return _scanComplete && _discoveredRules.Count > 0;
                case "S4":
                    return _discoveredRules.Any(r => r.IsIncluded);
                case "S5":
                    return true;
                default:
                    return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1":
                {
                    var sel = _links.Where(l => l.IsSelected).ToList();
                    if (sel.Count == 0) return "No links selected";
                    return sel.Count == 1
                        ? sel[0].Label
                        : $"{sel.Count} link(s): {string.Join(", ", sel.Select(l => l.TradeName))}";
                }
                case "S2":
                {
                    int c = _selectedCategories.Count;
                    return c == 0 ? "No categories selected"
                        : $"{c} categor{(c == 1 ? "y" : "ies")} selected";
                }
                case "S3":
                    return _scanComplete
                        ? $"{_discoveredRules.Count} rule(s) discovered"
                        : "Not yet scanned";
                case "S4":
                {
                    int inc = _discoveredRules.Count(r => r.IsIncluded);
                    return $"{inc} of {_discoveredRules.Count} rule(s) selected";
                }
                case "S5":
                    return "";
                default:
                    return "";
            }
        }

        /// <summary>
        /// Called by StepFlowWindow when the user confirms the final step.
        /// Wires the Commit event and raises it. The callbacks are already
        /// wrapped in Dispatcher.BeginInvoke by StepFlowWindow.StartRun().
        /// </summary>
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            UpdateS5Summary();

            var specs = _discoveredRules
                .Where(r => r.IsIncluded)
                .Select(r => new CommitRuleSpec
                {
                    TradeName         = r.TradeName,
                    RuleName          = r.RuleName,
                    HexColor          = r.HexColor,
                    IsWholeCategory   = r.IsWholeCategory,
                    ParameterValue    = r.ParameterValue,
                    Parameter         = r.Parameter,
                    BuiltInCategories = r.BuiltInCategories,
                }).ToList();

            _handler.Mode        = DiscoverMode.Commit;
            _handler.CommitSpecs = specs;
            _handler.PushLog     = pushLog;
            _handler.OnProgress  = onProgress;
            _handler.OnComplete  = onComplete;
            _event.Raise();
        }

        // ── UI helpers ────────────────────────────────────────────────────────

        private static TextBlock MakeNote(string text)
        {
            var tb = new TextBlock
            {
                Text         = text,
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 0, 0, 10),
            };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }
    }
}
