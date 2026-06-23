using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;
using NavisApp = Autodesk.Navisworks.Api.Application;

namespace LemoineNavisworks.SearchSets
{
    // =========================================================================
    // DiscoverSearchSetsViewModel — Phase 2 v1.
    //
    // Step flow:
    //   S1  Scan scope     — which loaded models to scan for values
    //   S2  Discover by    — category + property + match mode (contains/equals)
    //   S3  Scan & review  — "Scan now" lists distinct values; toggle which to keep
    //   S4  Review & run   — optional name prefix; Run creates/updates search sets
    //
    // Search sets are created/UPDATED by display name (never duplicated) and are
    // document-wide in v1. Per-model scoping and folder nesting come later.
    // =========================================================================
    public class DiscoverSearchSetsViewModel : ILemoineTool
    {
        public string Title    => "Discover Search Sets";
        public string RunLabel => "Create / Update Sets →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Scan scope",    required: true),
            new StepDefinition("S2", "Discover by",   required: true),
            new StepDefinition("S3", "Scan & review", required: true),
            new StepDefinition("S4", "Review & run",  required: false),
        };

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── State ─────────────────────────────────────────────────────────────
        private readonly Dictionary<string, List<string>> _catProps;
        private readonly List<string> _modelNames = new List<string>();
        private readonly HashSet<string> _selModels = new HashSet<string>(StringComparer.Ordinal);

        private string _category = "";
        private string _property = "";
        private bool   _contains = true;
        private string _prefix   = "";

        private bool _scanned;
        private List<NavisSearchSets.ValueCount> _values = new List<NavisSearchSets.ValueCount>();
        private readonly HashSet<string> _included = new HashSet<string>(StringComparer.Ordinal);

        // Held control refs for cross-step updates.
        private LemoineSingleSelect? _propertySelect;
        private LemoineToggleSwitches? _valueToggles;
        private TextBlock? _scanStatus;

        public DiscoverSearchSetsViewModel()
        {
            var doc = NavisApp.ActiveDocument;

            foreach (var n in NavisSearchSets.ModelNames(doc))
            {
                _modelNames.Add(n);
                _selModels.Add(n);            // default: scan everything
            }

            _catProps = doc != null
                ? NavisSearchSets.DiscoverCategoryProperties(doc)
                : new Dictionary<string, List<string>>();

            // Sensible defaults: "Item" / "Name" mirrors most of the reference search sets.
            _category = _catProps.ContainsKey("Item") ? "Item"
                      : _catProps.Keys.FirstOrDefault() ?? "";
            _property = PropertiesFor(_category).FirstOrDefault(p => p == "Name")
                      ?? PropertiesFor(_category).FirstOrDefault() ?? "";
        }

        private List<string> PropertiesFor(string category) =>
            _catProps.TryGetValue(category, out var ps) ? ps : new List<string>();

        // ── Step content ────────────────────────────────────────────────────────
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildScopeStep();
                case "S2": return BuildDiscoverByStep();
                case "S3": return BuildScanStep();
                case "S4": return BuildRunStep();
            }
            return null;
        }

        private FrameworkElement BuildScopeStep()
        {
            if (_modelNames.Count == 0)
                return Hint("No models are loaded. Open an NWF/NWD with models, then reopen this tool.");

            var toggles = new LemoineToggleSwitches();
            toggles.SetItems(
                _modelNames.Select(n => new ToggleItem
                {
                    Id = n, Label = n, Desc = "Scan this model for values", DefaultOn = true
                }).ToList());
            toggles.StateChanged += state =>
            {
                _selModels.Clear();
                foreach (var kv in state) if (kv.Value) _selModels.Add(kv.Key);
                Changed();
            };
            return toggles;
        }

        private FrameworkElement BuildDiscoverByStep()
        {
            var panel = new StackPanel();

            if (_catProps.Count == 0)
            {
                panel.Children.Add(Hint("No property categories were found in the sampled items."));
                return panel;
            }

            var catSelect = new LemoineSingleSelect
            {
                Label        = "Property category",
                Items        = _catProps.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
                SelectedItem = _category,
            };
            _propertySelect = new LemoineSingleSelect
            {
                Label        = "Property",
                Items        = PropertiesFor(_category),
                SelectedItem = _property,
            };
            var matchSelect = new LemoineSingleSelect
            {
                Label        = "Match",
                Items        = new List<string> { "Contains", "Equals" },
                SelectedItem = _contains ? "Contains" : "Equals",
            };

            catSelect.SelectionChanged += v =>
            {
                _category = v ?? "";
                var props = PropertiesFor(_category);
                if (_propertySelect != null)
                {
                    _propertySelect.Items = props;
                    _property = props.FirstOrDefault(p => p == "Name") ?? props.FirstOrDefault() ?? "";
                    _propertySelect.SelectedItem = _property;
                }
                InvalidateScan();
                Changed();
            };
            _propertySelect.SelectionChanged += v => { _property = v ?? ""; InvalidateScan(); Changed(); };
            matchSelect.SelectionChanged    += v => { _contains = v != "Equals"; InvalidateScan(); };

            panel.Children.Add(catSelect);
            panel.Children.Add(Gap());
            panel.Children.Add(_propertySelect);
            panel.Children.Add(Gap());
            panel.Children.Add(matchSelect);
            return panel;
        }

        private FrameworkElement BuildScanStep()
        {
            var panel = new StackPanel();

            var scanBtn = new Button { Content = "Scan now", Margin = new Thickness(0, 0, 0, 8) };
            scanBtn.Template = LemoineControlStyles.BuildFlatButtonTemplate();
            scanBtn.SetResourceReference(Control.BackgroundProperty, "LemoineAccent");
            scanBtn.SetResourceReference(Control.ForegroundProperty, "LemoineBg");
            scanBtn.SetResourceReference(Control.FontFamilyProperty, "LemoineUiFont");
            scanBtn.SetResourceReference(Control.FontSizeProperty, "LemoineFS_MD");
            scanBtn.SetResourceReference(Control.MinHeightProperty, "LemoineH_BtnMin");
            scanBtn.SetResourceReference(Control.PaddingProperty, "LemoineTh_BtnPad");
            scanBtn.Click += (s, e) => RunScan();

            _scanStatus = new TextBlock { Text = "Not scanned yet.", TextWrapping = TextWrapping.Wrap };
            _scanStatus.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            _scanStatus.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _scanStatus.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");

            _valueToggles = new LemoineToggleSwitches { Margin = new Thickness(0, 8, 0, 0) };
            _valueToggles.StateChanged += state =>
            {
                _included.Clear();
                foreach (var kv in state) if (kv.Value) _included.Add(kv.Key);
                Changed();
            };

            panel.Children.Add(scanBtn);
            panel.Children.Add(_scanStatus);
            panel.Children.Add(_valueToggles);

            if (_scanned) PopulateValueToggles();   // re-entering the step keeps prior results
            return panel;
        }

        private FrameworkElement BuildRunStep()
        {
            var panel = new StackPanel();

            var lbl = new TextBlock { Text = "Optional name prefix (e.g. \"MECH_\")", Margin = new Thickness(0, 0, 0, 4) };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            lbl.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");

            var box = new TextBox { Text = _prefix };
            box.SetResourceReference(Control.BackgroundProperty, "LemoineSelectBg");
            box.SetResourceReference(Control.ForegroundProperty, "LemoineText");
            box.SetResourceReference(Control.FontFamilyProperty, "LemoineUiFont");
            box.SetResourceReference(Control.FontSizeProperty, "LemoineFS_MD");
            box.SetResourceReference(Control.PaddingProperty, "LemoineTh_InputPad");
            box.SetResourceReference(Control.MinHeightProperty, "LemoineH_Input");
            box.TextChanged += (s, e) => _prefix = box.Text ?? "";

            panel.Children.Add(lbl);
            panel.Children.Add(box);
            panel.Children.Add(Gap());
            panel.Children.Add(Hint("Run creates a search set per included value, updating any that already exist (matched by name)."));
            return panel;
        }

        // ── Scan ──────────────────────────────────────────────────────────────
        private void RunScan()
        {
            var doc = NavisApp.ActiveDocument;
            if (doc == null || string.IsNullOrEmpty(_category) || string.IsNullOrEmpty(_property))
            {
                if (_scanStatus != null) _scanStatus.Text = "Pick a category and property first.";
                return;
            }

            try
            {
                _values = NavisSearchSets.ScanDistinctValues(doc, _selModels, _category, _property);
                _scanned = true;
                PopulateValueToggles();
            }
            catch (Exception ex)
            {
                LemoineLog.Error("DiscoverSearchSets: scan", ex);
                if (_scanStatus != null) _scanStatus.Text = "Scan failed: " + ex.Message;
            }
            Changed();
        }

        private void PopulateValueToggles()
        {
            if (_scanStatus != null)
                _scanStatus.Text = $"Found {_values.Count} distinct value(s) of {_category} · {_property}.";

            _included.Clear();
            foreach (var v in _values) _included.Add(v.Value);   // default: include all

            _valueToggles?.SetItems(
                _values.Select(v => new ToggleItem
                {
                    Id = v.Value, Label = v.Value, Desc = v.Count + " items", DefaultOn = true
                }).ToList());
        }

        private void InvalidateScan()
        {
            _scanned = false;
            _values = new List<NavisSearchSets.ValueCount>();
            _included.Clear();
            if (_scanStatus != null) _scanStatus.Text = "Discovery target changed — scan again.";
            _valueToggles?.SetItems(new List<ToggleItem>());
        }

        // ── Validation / summary ─────────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selModels.Count > 0;
                case "S2": return !string.IsNullOrEmpty(_category) && !string.IsNullOrEmpty(_property);
                case "S3": return _scanned && _included.Count > 0;
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return $"{_selModels.Count}/{_modelNames.Count} model(s)";
                case "S2": return $"{_category} · {_property} ({(_contains ? "contains" : "equals")})";
                case "S3": return _scanned ? $"{_included.Count} of {_values.Count} value(s) selected" : "Not scanned";
                default:   return $"{_included.Count} set(s) to write";
            }
        }

        // ── Run ─────────────────────────────────────────────────────────────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            var doc = NavisApp.ActiveDocument;
            int created = 0, updated = 0, failed = 0;

            var targets = _values.Where(v => _included.Contains(v.Value)).ToList();
            if (doc == null || targets.Count == 0)
            {
                pushLog("Nothing to write.", "info");
                onComplete(0, 0, 0);
                return;
            }

            pushLog($"Writing {targets.Count} search set(s) — updating any that already exist…", "info");

            for (int i = 0; i < targets.Count; i++)
            {
                var v = targets[i];
                string name = (_prefix ?? "") + v.Value;

                var result = NavisSearchSets.CreateOrUpdateSearchSet(doc, name, _category, _property, v.Value, _contains);
                switch (result)
                {
                    case NavisSearchSets.WriteResult.Created: created++; pushLog($"Created “{name}”", "pass"); break;
                    case NavisSearchSets.WriteResult.Updated: updated++; pushLog($"Updated “{name}”", "pass"); break;
                    default:                                  failed++;  pushLog($"Failed “{name}” (see diagnostics log)", "fail"); break;
                }

                int pct = (int)((i + 1) * 100.0 / targets.Count);
                onProgress(pct, created + updated, failed, 0);
            }

            pushLog($"Done — {created} created, {updated} updated, {failed} failed.", failed == 0 ? "pass" : "info");
            onComplete(created + updated, failed, 0);
        }

        // ── Tiny UI helpers ───────────────────────────────────────────────────────
        private static FrameworkElement Gap() => new Border { Height = 8 };

        private static TextBlock Hint(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_MD");
            tb.FontStyle = FontStyles.Italic;
            return tb;
        }
    }
}
