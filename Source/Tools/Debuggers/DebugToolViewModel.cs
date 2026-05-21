using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Debuggers
{
    /// <summary>
    /// UI Debug tool — every Lemoine input control exercised in sequence.
    /// Uses a fake simulated run (no Revit API) so it works read-only.
    ///
    /// Steps:
    ///   D1  FileBrowser + SingleSelect + SearchAutocomplete
    ///   D2  MultiSelectTabs
    ///   D3  ToggleSwitches
    ///   D4  MatrixInput + NumberRange + DatePicker
    ///   D5  ReviewSummary + Review & Run (simulated)
    /// </summary>
    public class DebugToolViewModel : ILemoineTool, ILemoineToolSettings
    {
        public string Title    => "UI Debug";
        public string RunLabel => "Simulate Run →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("D1", "File, Select & Search",    required: true),
            new StepDefinition("D2", "Multi-Select Tabs",        required: true),
            new StepDefinition("D3", "Toggle Switches",          required: false),
            new StepDefinition("D4", "Matrix, Range & Date",     required: false),
            new StepDefinition("D5", "Review Summary & Run",     required: false),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private string _filePath   = "";
        private string _selected   = "";
        private string _search     = "";
        private IReadOnlyCollection<string> _multiSelected = new List<string>();
        private Dictionary<string, bool>    _toggles       = new Dictionary<string, bool>();
        private IReadOnlyDictionary<string, string> _matrix = new Dictionary<string, string>();
        private double? _rangeMin, _rangeMax;
        private string? _dateFrom, _dateTo;

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ═════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "D1") return BuildD1();
            if (stepId == "D2") return BuildD2();
            if (stepId == "D3") return BuildD3();
            if (stepId == "D4") return BuildD4();
            if (stepId == "D5") return BuildD5();
            return null;
        }

        // ── D1: FileBrowser + SingleSelect + SearchAutocomplete ────────────
        private FrameworkElement BuildD1()
        {
            var outer = new StackPanel();

            var hdr = SectionHdr("File Browser");
            outer.Children.Add(hdr);
            var fb = new LemoineFileBrowser
            {
                Label       = "Select any file to test the browser control.",
                Filter      = "All files|*.*",
                DialogTitle = "Debug — Select File",
                Recents     = new List<string> { @"C:\Debug\recent-a.txt", @"C:\Debug\recent-b.dwg" },
            };
            fb.PathChanged += p => { _filePath = p ?? ""; Fire(); };
            outer.Children.Add(fb);

            outer.Children.Add(Spacer());
            outer.Children.Add(SectionHdr("Single Select"));
            var sel = new LemoineSingleSelect
            {
                Label = "Select one option from the dropdown.",
                Items = new List<string> { "Option Alpha", "Option Beta", "Option Gamma", "Option Delta" },
            };
            sel.SelectionChanged += v => { _selected = v ?? ""; Fire(); };
            outer.Children.Add(sel);

            outer.Children.Add(Spacer());
            outer.Children.Add(SectionHdr("Search Autocomplete"));
            var sa = new LemoineSearchAutocomplete
            {
                Items       = new List<string> { "IFC Coordination Package", "For Construction (FC)", "For Information (FI)", "Tender Package", "As-Built Record Set", "Internal Review" },
                Placeholder = "Type to search…",
                MaxSuggestions = 4,
            };
            sa.SelectionChanged += v => { _search = v ?? ""; Fire(); };
            outer.Children.Add(sa);

            return outer;
        }

        // ── D2: MultiSelectTabs ─────────────────────────────────────────────
        private FrameworkElement BuildD2()
        {
            var outer = new StackPanel();
            outer.Children.Add(SectionHdr("Multi-Select Tabs"));

            var descTb = new TextBlock
            {
                Text = "Select items across multiple groups. Each group tab shows a badge count.",
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 0, 0, 8),
            };
            descTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            descTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            descTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(descTb);

            var mst = new LemoineMultiSelectTabs();
            mst.SetGroups(new Dictionary<string, List<string>>
            {
                { "Architecture", new List<string> { "A-001 Site Plan", "A-100 Ground Floor", "A-101 Level 1", "A-102 Level 2", "A-200 Elevations" } },
                { "Structure",    new List<string> { "S-001 Foundation", "S-100 Framing L1", "S-101 Framing L2" } },
                { "Mechanical",   new List<string> { "M-001 Legend", "M-100 Ground HVAC", "M-101 Level 1 HVAC" } },
                { "Electrical",   new List<string> { "E-001 Legend", "E-100 Ground Power", "E-101 Level 1 Power" } },
            });
            mst.SelectionChanged += sel => { _multiSelected = sel; Fire(); };
            outer.Children.Add(mst);

            return outer;
        }

        // ── D3: ToggleSwitches ──────────────────────────────────────────────
        private FrameworkElement BuildD3()
        {
            var outer = new StackPanel();
            outer.Children.Add(SectionHdr("Toggle Switches"));

            var tog = new LemoineToggleSwitches();
            tog.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "annotations",    Label = "Include Annotations",    Desc = "Dimensions, tags and keynotes",         DefaultOn = true  },
                new ToggleItem { Id = "links",          Label = "Show Linked Models",     Desc = "Display Revit link geometry",           DefaultOn = false },
                new ToggleItem { Id = "titleblock",     Label = "Apply Title Block",      Desc = "Attach sheet title block from template", DefaultOn = true  },
                new ToggleItem { Id = "color_override", Label = "Discipline Colour Overrides", Desc = "Apply standard clash colour coding",DefaultOn = false },
                new ToggleItem { Id = "crop",           Label = "Auto Crop to Level Bounds", Desc = "Crop view to slab extents",          DefaultOn = true  },
            });
            tog.StateChanged += state => { _toggles = new Dictionary<string, bool>(state); Fire(); };
            outer.Children.Add(tog);

            return outer;
        }

        // ── D4: MatrixInput + NumberRange + DatePicker ─────────────────────
        private FrameworkElement BuildD4()
        {
            var outer = new StackPanel();

            outer.Children.Add(SectionHdr("Matrix Input"));
            var mx = new LemoineMatrixInput();
            mx.SetMatrix(
                rows:     new List<string> { "Mechanical", "Electrical", "Plumbing" },
                cols:     new List<string> { "View Range", "Phase", "Clash Buffer (mm)" },
                defaults: new Dictionary<string, string>
                {
                    { "View Range", "Standard" },
                    { "Phase", "New Construction" },
                    { "Clash Buffer (mm)", "50" },
                }
            );
            mx.ValueChanged += v => { _matrix = v; Fire(); };
            outer.Children.Add(mx);

            outer.Children.Add(Spacer());
            outer.Children.Add(SectionHdr("Number Range"));
            var nr = new LemoineNumberRange
            {
                MinLabel = "Min Revision",
                MaxLabel = "Max Revision",
                Unit     = "",
                Step     = 1,
                AbsMin   = 0,
                AbsMax   = 99,
            };
            nr.RangeChanged += (min, max) => { _rangeMin = min; _rangeMax = max; };
            outer.Children.Add(nr);

            outer.Children.Add(Spacer());
            outer.Children.Add(SectionHdr("Date Picker — Single"));
            var dpSingle = new LemoineDatePicker
            {
                Label = "Select a single date.",
                Mode  = LemoineDatePicker.PickerMode.Single,
            };
            dpSingle.DateChanged += (from, to) => { _dateFrom = from; };
            outer.Children.Add(dpSingle);

            outer.Children.Add(Spacer());
            outer.Children.Add(SectionHdr("Date Picker — Range"));
            var dpRange = new LemoineDatePicker
            {
                Label = "Select a date range.",
                Mode  = LemoineDatePicker.PickerMode.Range,
            };
            dpRange.DateChanged += (from, to) => { _dateTo = to; };
            outer.Children.Add(dpRange);

            return outer;
        }

        // ── D5: ReviewSummary ──────────────────────────────────────────────
        private FrameworkElement BuildD5()
        {
            var outer = new StackPanel();
            outer.Children.Add(SectionHdr("Review Summary Control"));

            var rs = new LemoineReviewSummary();
            ValidationChanged += (s, e) => rs.SetItems(
                new List<(string id, string label)>
                {
                    ("file",    "File Path"),
                    ("select",  "Selection"),
                    ("search",  "Search Value"),
                    ("multi",   "Multi-Select Count"),
                    ("range",   "Number Range"),
                },
                new Dictionary<string, string>
                {
                    { "file",   string.IsNullOrEmpty(_filePath) ? "—" : System.IO.Path.GetFileName(_filePath) },
                    { "select", string.IsNullOrEmpty(_selected) ? "—" : _selected },
                    { "search", string.IsNullOrEmpty(_search)   ? "—" : _search },
                    { "multi",  $"{_multiSelected.Count} items selected" },
                    { "range",  (_rangeMin.HasValue || _rangeMax.HasValue) ? $"{_rangeMin ?? 0} – {_rangeMax ?? 0}" : "—" },
                },
                chips: new List<string> { "Debug", "All Controls", "No Revit API" }
            );
            // Initial population
            rs.SetItems(
                new List<(string id, string label)>
                {
                    ("file", "File Path"), ("select", "Selection"),
                    ("search", "Search Value"), ("multi", "Multi-Select Count"), ("range", "Number Range"),
                },
                new Dictionary<string, string>
                {
                    { "file", "—" }, { "select", "—" }, { "search", "—" },
                    { "multi", "0 items selected" }, { "range", "—" },
                },
                chips: new List<string> { "Debug", "All Controls", "No Revit API" }
            );
            outer.Children.Add(rs);

            outer.Children.Add(Spacer());

            var desc = new TextBlock
            {
                Text = "Clicking 'Simulate Run →' runs a fake progress loop with pass/fail/skip counts. No Revit API is called — this is purely a UI test.",
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
            };
            desc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            desc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            desc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(desc);

            return outer;
        }

        // ═════════════════════════════════════════════════════════════════════
        // IsValid
        // ═════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "D1") return !string.IsNullOrWhiteSpace(_filePath) || !string.IsNullOrWhiteSpace(_selected);
            if (stepId == "D2") return _multiSelected.Count > 0;
            return true;
        }

        // ═════════════════════════════════════════════════════════════════════
        // SummaryFor
        // ═════════════════════════════════════════════════════════════════════
        public string SummaryFor(string stepId)
        {
            if (stepId == "D1")
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(_filePath))  parts.Add(System.IO.Path.GetFileName(_filePath));
                if (!string.IsNullOrEmpty(_selected))  parts.Add(_selected);
                if (!string.IsNullOrEmpty(_search))    parts.Add(_search);
                return parts.Count > 0 ? string.Join(", ", parts) : "—";
            }
            if (stepId == "D2") return _multiSelected.Count > 0 ? $"{_multiSelected.Count} items" : "—";
            if (stepId == "D3")
            {
                int on = 0; foreach (var v in _toggles.Values) if (v) on++;
                return $"{on} enabled";
            }
            if (stepId == "D4") return (_rangeMin.HasValue || _rangeMax.HasValue) ? $"{_rangeMin ?? 0}–{_rangeMax ?? 0}" : "—";
            if (stepId == "D5") return "Ready";
            return "—";
        }

        // ═════════════════════════════════════════════════════════════════════
        // Run — simulated, no Revit API
        // ═════════════════════════════════════════════════════════════════════
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            pushLog("Starting simulated run — no Revit API is called.", "info");

            var items = new List<string>
            {
                "File browser value",
                "Single select value",
                "Search autocomplete value",
                "Multi-select group A",
                "Multi-select group B",
                "Toggle switches state",
                "Matrix row 1",
                "Matrix row 2",
                "Number range",
                "Date range",
            };

            int pass = 0, fail = 0, skip = 0;
            int total = items.Count;

            // Simulate async progress without threading (fires on idle)
            System.Windows.Threading.DispatcherTimer? timer = null;
            int idx = 0;
            timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            timer.Tick += (s, e) =>
            {
                if (idx >= total)
                {
                    timer.Stop();
                    pushLog($"Simulated run complete — {pass} pass, {fail} fail, {skip} skip.", "pass");
                    onComplete(pass, fail, skip);
                    return;
                }

                var item = items[idx];
                // Introduce a deliberate skip on idx 3 and fail on idx 7 for visual testing
                if (idx == 3)       { skip++; pushLog($"{item} — skipped (intentional)", "info"); }
                else if (idx == 7)  { fail++; pushLog($"{item} — failed (intentional test failure)", "fail"); }
                else                { pass++; pushLog($"{item} — OK", "pass"); }

                idx++;
                int pct = (int)(idx * 90.0 / total);
                onProgress(pct, pass, fail, skip);
            };
            timer.Start();
        }

        // DebugToolViewModel has no persistent settings.
        public LemoineToolSettingsSpec? GetSettingsSpec() => null;
        public void ApplySettings(string groupId, string settingId, object value) { }

        private static TextBlock SectionHdr(string text)
        {
            var tb = new TextBlock
            {
                Text = text, FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, 0, 0, 6),
            };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static FrameworkElement Spacer()
            => new System.Windows.Shapes.Rectangle { Height = 14 };
    }
}
