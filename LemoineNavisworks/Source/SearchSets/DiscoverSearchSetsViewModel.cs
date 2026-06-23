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
    // DiscoverSearchSetsViewModel — Phase 2 (rework).
    //
    // Step flow:
    //   S1  Trade groups   — files auto-grouped by trade (level-split files
    //                        collapsed); editable per file.
    //   S2  Discover by    — pick a trade group, load its real (leaf/geometry)
    //                        properties, choose category·property + match mode.
    //   S3  Scan & review  — scan the group's files for distinct values; pick.
    //   S4  Review & run   — optional prefix; create/update search sets.
    //
    // Properties are discovered from GEOMETRY items and keyed on internal names
    // (stable across file types). Search sets are document-wide and updated by
    // name. Per-group file-scoping of the set definition is a later step.
    // =========================================================================
    public class DiscoverSearchSetsViewModel : ILemoineTool, IStepAware
    {
        public string Title    => "Discover Search Sets";
        public string RunLabel => "Create / Update Sets →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Trade groups",  required: true),
            new StepDefinition("S2", "Discover by",   required: true),
            new StepDefinition("S3", "Scan & review", required: true),
            new StepDefinition("S4", "Review & run",  required: false),
        };

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── State ─────────────────────────────────────────────────────────────
        private readonly List<string> _fileNames;
        private readonly TradeGrouping _grouping;

        private string _selGroup = "";
        private List<NavisSearchSets.PropertyRef> _props = new List<NavisSearchSets.PropertyRef>();
        private NavisSearchSets.PropertyRef? _selProp;
        private bool   _contains = true;
        private string _prefix   = "";

        private bool _scanned;
        private List<NavisSearchSets.ValueCount> _values = new List<NavisSearchSets.ValueCount>();
        private readonly HashSet<string> _included = new HashSet<string>(StringComparer.Ordinal);

        private bool _groupsDirty;
        private Action<string>? _rebuild;

        // Control refs held for cross-step / post-load updates.
        private TextBlock? _groupSummary;
        private LemoineSingleSelect? _groupSelect;
        private StackPanel? _propHost;
        private TextBlock? _scanStatus;
        private LemoineToggleSwitches? _valueToggles;

        public DiscoverSearchSetsViewModel()
        {
            var doc = NavisApp.ActiveDocument;
            _fileNames = NavisSearchSets.ModelNames(doc);
            _grouping  = TradeGrouping.Derive(_fileNames);
            _selGroup  = _grouping.Groups().FirstOrDefault() ?? "";
        }

        // ── IStepAware: refresh the group dropdown after S1 edits ────────────────
        public void SetContentRefreshCallback(Action<string> rebuild) => _rebuild = rebuild;

        public void OnStepActivated(string stepId)
        {
            if (stepId == "S2" && _groupsDirty)
            {
                _groupsDirty = false;
                _rebuild?.Invoke("S2");
            }
        }

        // ── Step content ────────────────────────────────────────────────────────
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildGroupsStep();
                case "S2": return BuildDiscoverByStep();
                case "S3": return BuildScanStep();
                case "S4": return BuildRunStep();
            }
            return null;
        }

        private FrameworkElement BuildGroupsStep()
        {
            if (_fileNames.Count == 0)
                return Hint("No models are loaded. Append files, then reopen this tool.");

            var panel = new StackPanel();
            _groupSummary = MakeSub(_grouping.Summary());
            _groupSummary.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(_groupSummary);

            for (int i = 0; i < _fileNames.Count; i++)
            {
                int idx = i;
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var name = new TextBlock
                {
                    Text = _fileNames[idx],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                name.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_MD");
                Grid.SetColumn(name, 0);

                var groupBox = MakeTextBox(_grouping.FileGroup[idx]);
                groupBox.Width = 170;
                groupBox.Margin = new Thickness(8, 0, 0, 0);
                groupBox.TextChanged += (s, e) =>
                {
                    _grouping.SetGroup(idx, groupBox.Text ?? "");
                    if (_groupSummary != null) _groupSummary.Text = _grouping.Summary();
                    _groupsDirty = true;
                    Changed();
                };
                Grid.SetColumn(groupBox, 1);

                row.Children.Add(name);
                row.Children.Add(groupBox);
                panel.Children.Add(row);
            }
            return panel;
        }

        private FrameworkElement BuildDiscoverByStep()
        {
            var panel = new StackPanel();
            var groups = _grouping.Groups();
            if (groups.Count == 0)
            {
                panel.Children.Add(Hint("No groups yet — assign trade groups in the previous step."));
                return panel;
            }

            string prior = _selGroup;

            _groupSelect = new LemoineSingleSelect { Label = "Trade group" };
            _groupSelect.SelectionChanged += g => { _selGroup = g ?? ""; ResetProps(); };

            var loadBtn = MakeFlatButton("Load properties for this group");
            loadBtn.Margin = new Thickness(0, 8, 0, 0);
            loadBtn.Click += (s, e) => LoadProperties();

            _propHost = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

            panel.Children.Add(_groupSelect);
            panel.Children.Add(loadBtn);
            panel.Children.Add(_propHost);

            _groupSelect.Items = groups;                       // cascades -> groups[0]
            if (groups.Contains(prior, StringComparer.OrdinalIgnoreCase))
                _groupSelect.SelectedItem = prior;
            return panel;
        }

        private FrameworkElement BuildScanStep()
        {
            var panel = new StackPanel();

            var scanBtn = MakeFlatButton("Scan now");
            scanBtn.Margin = new Thickness(0, 0, 0, 8);
            scanBtn.Click += (s, e) => RunScan();

            _scanStatus = MakeSub(_scanned ? "" : "Not scanned yet.");

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

            if (_scanned) PopulateValueToggles();
            return panel;
        }

        private FrameworkElement BuildRunStep()
        {
            var panel = new StackPanel();

            var lbl = MakeSub("Optional name prefix (e.g. \"MECH_\")");
            lbl.Margin = new Thickness(0, 0, 0, 4);

            var box = MakeTextBox(_prefix);
            box.TextChanged += (s, e) => _prefix = box.Text ?? "";

            panel.Children.Add(lbl);
            panel.Children.Add(box);
            panel.Children.Add(Gap());
            panel.Children.Add(Hint("Run creates a search set per included value, updating any that already exist (matched by name)."));
            return panel;
        }

        // ── Property loading (per group, behind a button) ────────────────────────
        private void LoadProperties()
        {
            if (_propHost == null) return;
            _propHost.Children.Clear();

            var doc = NavisApp.ActiveDocument;
            var indices = _grouping.FilesIn(_selGroup);
            if (doc == null || indices.Count == 0)
            {
                _propHost.Children.Add(Hint("No files in this group."));
                return;
            }

            var status = MakeSub("Scanning properties…");
            _propHost.Children.Add(status);

            try { _props = NavisSearchSets.DiscoverProperties(doc, indices); }
            catch (Exception ex)
            {
                LemoineLog.Error("DiscoverSearchSets: load properties", ex);
                status.Text = "Failed: " + ex.Message;
                return;
            }

            if (_props.Count == 0)
            {
                status.Text = "No properties found on geometry items in this group.";
                return;
            }
            status.Text = $"{_props.Count} propert(y/ies) across {indices.Count} file(s).";

            var categories = _props
                .Select(p => p.CategoryDisplay)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var catSel   = new LemoineSingleSelect { Label = "Category" };
            var propSel  = new LemoineSingleSelect { Label = "Property" };
            var matchSel = new LemoineSingleSelect { Label = "Match" };

            catSel.SelectionChanged += c =>
            {
                var props = _props
                    .Where(p => string.Equals(p.CategoryDisplay, c, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.PropertyDisplay)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                propSel.Items = props;                        // cascades -> prop handler
            };
            propSel.SelectionChanged += pn =>
            {
                _selProp = _props.FirstOrDefault(p =>
                    string.Equals(p.CategoryDisplay, catSel.SelectedItem, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.PropertyDisplay, pn, StringComparison.OrdinalIgnoreCase));
                InvalidateScan();
                Changed();
            };
            matchSel.SelectionChanged += v => { _contains = v != "Equals"; InvalidateScan(); };

            _propHost.Children.Add(Gap());
            _propHost.Children.Add(catSel);
            _propHost.Children.Add(Gap());
            _propHost.Children.Add(propSel);
            _propHost.Children.Add(Gap());
            _propHost.Children.Add(matchSel);

            matchSel.Items = new List<string> { "Contains", "Equals" };
            catSel.Items   = categories;                       // cascade fills propSel + _selProp
            Changed();
        }

        private void ResetProps()
        {
            _props   = new List<NavisSearchSets.PropertyRef>();
            _selProp = null;
            _propHost?.Children.Clear();
            InvalidateScan();
            Changed();
        }

        // ── Scan ─────────────────────────────────────────────────────────────────
        private void RunScan()
        {
            var doc = NavisApp.ActiveDocument;
            if (doc == null || _selProp == null)
            {
                if (_scanStatus != null) _scanStatus.Text = "Pick a group and property first.";
                return;
            }

            try
            {
                _values = NavisSearchSets.ScanDistinctValues(doc, _grouping.FilesIn(_selGroup), _selProp);
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
                _scanStatus.Text = $"Found {_values.Count} value(s) of {_selProp?.CategoryDisplay} · {_selProp?.PropertyDisplay}.";

            _included.Clear();
            foreach (var v in _values) _included.Add(v.Value);

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
            if (_scanStatus != null) _scanStatus.Text = "Selection changed — scan again.";
            _valueToggles?.SetItems(new List<ToggleItem>());
        }

        // ── Validation / summary ─────────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _fileNames.Count > 0;
                case "S2": return _selProp != null;
                case "S3": return _scanned && _included.Count > 0;
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _grouping.Summary();
                case "S2": return _selProp == null
                    ? "—"
                    : $"{_selGroup}: {_selProp.CategoryDisplay} · {_selProp.PropertyDisplay} ({(_contains ? "contains" : "equals")})";
                case "S3": return _scanned ? $"{_included.Count} of {_values.Count} value(s)" : "Not scanned";
                default:   return $"{_included.Count} set(s) to write";
            }
        }

        // ── Run ──────────────────────────────────────────────────────────────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            var doc = NavisApp.ActiveDocument;
            var targets = _values.Where(v => _included.Contains(v.Value)).ToList();

            if (doc == null || _selProp == null || targets.Count == 0)
            {
                pushLog("Nothing to write.", "info");
                onComplete(0, 0, 0);
                return;
            }

            pushLog($"Writing {targets.Count} search set(s) for “{_selGroup}” — updating existing by name…", "info");

            int created = 0, updated = 0, failed = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                var v = targets[i];
                string name = (_prefix ?? "") + v.Value;

                var result = NavisSearchSets.CreateOrUpdateSearchSet(doc, name, _selProp, v.Value, _contains);
                switch (result)
                {
                    case NavisSearchSets.WriteResult.Created: created++; pushLog($"Created “{name}”", "pass"); break;
                    case NavisSearchSets.WriteResult.Updated: updated++; pushLog($"Updated “{name}”", "pass"); break;
                    default:                                  failed++;  pushLog($"Failed “{name}” (see diagnostics log)", "fail"); break;
                }

                onProgress((int)((i + 1) * 100.0 / targets.Count), created + updated, failed, 0);
            }

            pushLog($"Done — {created} created, {updated} updated, {failed} failed.", failed == 0 ? "pass" : "info");
            onComplete(created + updated, failed, 0);
        }

        // ── Tiny UI helpers ───────────────────────────────────────────────────────
        private static Button MakeFlatButton(string text)
        {
            var b = new Button { Content = text };
            b.Template = LemoineControlStyles.BuildFlatButtonTemplate();
            b.SetResourceReference(Control.BackgroundProperty, "LemoineAccent");
            b.SetResourceReference(Control.ForegroundProperty, "LemoineBg");
            b.SetResourceReference(Control.FontFamilyProperty, "LemoineUiFont");
            b.SetResourceReference(Control.FontSizeProperty, "LemoineFS_MD");
            b.SetResourceReference(Control.MinHeightProperty, "LemoineH_BtnMin");
            b.SetResourceReference(Control.PaddingProperty, "LemoineTh_BtnPad");
            return b;
        }

        private static TextBox MakeTextBox(string initial)
        {
            var t = new TextBox { Text = initial ?? "" };
            t.SetResourceReference(Control.BackgroundProperty, "LemoineSelectBg");
            t.SetResourceReference(Control.ForegroundProperty, "LemoineText");
            t.SetResourceReference(Control.FontFamilyProperty, "LemoineUiFont");
            t.SetResourceReference(Control.FontSizeProperty, "LemoineFS_MD");
            t.SetResourceReference(Control.PaddingProperty, "LemoineTh_InputPad");
            t.SetResourceReference(Control.MinHeightProperty, "LemoineH_Input");
            return t;
        }

        private static TextBlock MakeSub(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            return tb;
        }

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
