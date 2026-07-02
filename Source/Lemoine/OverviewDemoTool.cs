using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Lemoine
{
    // ─────────────────────────────────────────────────────────────────────────
    // Catalog-driven, Revit-free ILemoineTool used by the Tools Overview to give
    // each real tool a "dummy run": it declares the tool's real steps, builds the
    // genuine Lemoine input controls with representative sample data, validates,
    // and simulates a run (progress + log) with NO Revit API.
    //
    // Same idea as LemoinePreview.DemoTool, but data-driven from an OverviewDemoSpec
    // so one engine covers every tool. Opened in the real StepFlowWindow on its own
    // STA thread (see ToolsOverviewWindow.LaunchDemo).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>One simulated step: a real control kind seeded with sample data.</summary>
    public sealed class OverviewDemoStep
    {
        public string Id       { get; set; } = "";
        public string Title    { get; set; } = "";
        public bool   Required { get; set; }
        /// <summary>file | multi | toggles | number | single | search | text | info | composite</summary>
        public string Kind     { get; set; } = "info";
        public string Hint     { get; set; } = "";

        public Dictionary<string, List<string>>? Groups   { get; set; } // multi
        public List<ToggleItem>?                 Toggles  { get; set; } // toggles
        public List<string>?                     Items    { get; set; } // single / search

        /// <summary>Sub-controls stacked in one step (composite) — mirrors tools whose
        /// single step hosts several labelled inputs. Part ids must be spec-unique.</summary>
        public List<OverviewDemoStep>? Parts { get; set; }
        /// <summary>single: pre-select this item index (-1 = none), like ViewModels
        /// that default to the first grid/level.</summary>
        public int PreselectIndex { get; set; } = -1;

        public double NumValue { get; set; }
        public double NumMin   { get; set; }
        public double NumMax   { get; set; } = 100;
        public double NumStep  { get; set; } = 1;
        public int    NumDecimals { get; set; }
        public string NumUnit  { get; set; } = "";

        public string FileFilter      { get; set; } = "All files|*.*";
        public string FilePlaceholder { get; set; } = LemoineStrings.T("overviewDemo.browsePlaceholder");

        public string TextDefault     { get; set; } = "";
        public string TextPlaceholder { get; set; } = "";

        public string Info    { get; set; } = "";
        public string Summary { get; set; } = "";
    }

    /// <summary>The full demo definition for one tool.</summary>
    public sealed class OverviewDemoSpec
    {
        public string             Title    { get; set; } = "";
        public string             RunLabel { get; set; } = "Simulate Run →";
        public OverviewDemoStep[]  Steps   { get; set; } = Array.Empty<OverviewDemoStep>();
        /// <summary>Lines the simulated run prints. Status ∈ {info, pass, fail, skip}.
        /// Lines may carry {count:step}/{sel:step}/{first:step}/{value:step}/{file:step}
        /// tokens, expanded from the live demo selections at run time.</summary>
        public (string Text, string Status)[] RunLog { get; set; } = Array.Empty<(string, string)>();
        /// <summary>Title of the document the sample pools were captured from
        /// ("" = no document, canned data). Baked in at spec build so the demo
        /// window never reads the shared snapshot from its own thread.</summary>
        public string SampledFrom { get; set; } = "";
    }

    public sealed class OverviewDemoTool : ILemoineTool
    {
        private readonly OverviewDemoSpec _spec;

        // Live per-step state captured from the controls.
        private readonly Dictionary<string, IReadOnlyCollection<string>> _multi  = new Dictionary<string, IReadOnlyCollection<string>>();
        private readonly Dictionary<string, string>                      _value  = new Dictionary<string, string>();
        private readonly Dictionary<string, double>                      _number = new Dictionary<string, double>();

        public OverviewDemoTool(OverviewDemoSpec spec)
        {
            _spec = spec;
            // Seed defaults so required-with-default steps start valid.
            foreach (var s in AllSteps()) Seed(s);
        }

        private void Seed(OverviewDemoStep s)
        {
            if (s.Kind == "text" && !string.IsNullOrEmpty(s.TextDefault)) _value[s.Id] = s.TextDefault;
            if (s.Kind == "number") _number[s.Id] = s.NumValue;
            if (s.Kind == "single" && s.PreselectIndex >= 0 && s.Items != null && s.Items.Count > 0)
                _value[s.Id] = s.Items[Math.Min(s.PreselectIndex, s.Items.Count - 1)];
        }

        public string Title    => _spec.Title + LemoineStrings.T("overviewDemo.titleSuffix");
        public string RunLabel => _spec.RunLabel;

        public StepDefinition[] Steps =>
            _spec.Steps.Select(s => new StepDefinition(s.Id, s.Title, s.Required)).ToArray();

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>Top-level steps plus every composite part (state/token lookups).</summary>
        private IEnumerable<OverviewDemoStep> AllSteps()
        {
            foreach (var s in _spec.Steps)
            {
                yield return s;
                if (s.Parts == null) continue;
                foreach (var p in s.Parts) yield return p;
            }
        }

        private OverviewDemoStep? Find(string id) => AllSteps().FirstOrDefault(s => s.Id == id);

        // ── Step content ──────────────────────────────────────────────────────
        public FrameworkElement? GetStepContent(string stepId)
        {
            var step = Find(stepId);
            if (step == null) return null;

            var stack = new StackPanel();
            if (!string.IsNullOrEmpty(step.Hint)) stack.Children.Add(MakeHint(step.Hint));

            if (step.Kind == "composite")
            {
                foreach (var part in step.Parts ?? new List<OverviewDemoStep>())
                {
                    if (!string.IsNullOrEmpty(part.Title)) stack.Children.Add(MakeLabel(part.Title));
                    var ctl = BuildControl(part);
                    if (ctl != null) stack.Children.Add(ctl);
                }
                return stack;
            }

            var control = BuildControl(step);
            if (control != null) stack.Children.Add(control);
            return stack;
        }

        private FrameworkElement? BuildControl(OverviewDemoStep step)
        {
            switch (step.Kind)
            {
                case "file":    return BuildFile(step);
                case "multi":   return BuildMulti(step);
                case "toggles": return BuildToggles(step);
                case "number":  return BuildNumber(step);
                case "single":  return BuildSingle(step);
                case "search":  return BuildSearch(step);
                case "text":    return BuildText(step);
                case "info":    return string.IsNullOrEmpty(step.Info) ? null : MakeHint(step.Info);
                default:        return null;
            }
        }

        private FrameworkElement BuildFile(OverviewDemoStep s)
        {
            var fb = new LemoineFileBrowser
            {
                Label       = "",
                Placeholder = s.FilePlaceholder,
                Filter      = s.FileFilter,
            };
            fb.PathChanged += p => { _value[s.Id] = p ?? ""; Changed(); };
            return fb;
        }

        private FrameworkElement BuildMulti(OverviewDemoStep s)
        {
            var mt = new LemoineMultiSelectTabs();
            mt.SelectionChanged += sel => { _multi[s.Id] = sel; Changed(); };   // subscribe BEFORE SetGroups
            mt.SetGroups(s.Groups ?? new Dictionary<string, List<string>>());
            return mt;
        }

        private FrameworkElement BuildToggles(OverviewDemoStep s)
        {
            var tog = new LemoineToggleSwitches();
            tog.SetItems(s.Toggles ?? new List<ToggleItem>());
            return tog;
        }

        private FrameworkElement BuildNumber(OverviewDemoStep s)
        {
            var stepper = new LemoineInlineStepper
            {
                Value    = s.NumValue,
                MinValue = s.NumMin,
                MaxValue = s.NumMax,
                Step     = s.NumStep,
                Decimals = s.NumDecimals,
            };
            stepper.ValueChanged += (_, v) => { _number[s.Id] = v; };
            if (!string.IsNullOrEmpty(s.NumUnit))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(stepper);
                var unit = new TextBlock { Text = s.NumUnit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                unit.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
                unit.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                unit.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                row.Children.Add(unit);
                return row;
            }
            return stepper;
        }

        private FrameworkElement BuildSingle(OverviewDemoStep s)
        {
            var sel = new LemoineSingleSelect
            {
                Label = "",
                Items = s.Items ?? new List<string>(),
            };
            if (_value.TryGetValue(s.Id, out var pre) && !string.IsNullOrEmpty(pre))
                sel.SelectedItem = pre;   // seeded PreselectIndex default
            sel.SelectionChanged += v => { _value[s.Id] = v ?? ""; Changed(); };
            return sel;
        }

        private FrameworkElement BuildSearch(OverviewDemoStep s)
        {
            var sa = new LemoineSearchAutocomplete
            {
                Items       = s.Items ?? new List<string>(),
                Placeholder = string.IsNullOrEmpty(s.TextPlaceholder) ? LemoineStrings.T("overviewDemo.searchPlaceholder") : s.TextPlaceholder,
            };
            sa.SelectionChanged += v => { _value[s.Id] = v ?? ""; Changed(); };
            return sa;
        }

        private FrameworkElement BuildText(OverviewDemoStep s)
        {
            var edit = new LemoineInlineEdit
            {
                Text        = s.TextDefault,
                Placeholder = string.IsNullOrEmpty(s.TextPlaceholder) ? LemoineStrings.T("overviewDemo.textPlaceholder") : s.TextPlaceholder,
            };
            edit.TextCommitted += (_, t) => { _value[s.Id] = t ?? ""; Changed(); };
            return edit;
        }

        // ── Validation + summary ──────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            var step = Find(stepId);
            if (step == null) return true;
            if (step.Kind == "composite")
                return (step.Parts ?? new List<OverviewDemoStep>()).All(PartValid);
            return PartValid(step);
        }

        private bool PartValid(OverviewDemoStep step)
        {
            if (!step.Required) return true;
            switch (step.Kind)
            {
                case "multi":  return _multi.TryGetValue(step.Id, out var m) && m.Count > 0;
                case "file":
                case "single":
                case "search":
                case "text":   return _value.TryGetValue(step.Id, out var v) && !string.IsNullOrWhiteSpace(v);
                default:       return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            var step = Find(stepId);
            if (step == null) return "";
            if (!string.IsNullOrEmpty(step.Summary)) return step.Summary;

            if (step.Kind == "composite")
            {
                var parts = (step.Parts ?? new List<OverviewDemoStep>())
                    .Where(p => p.Kind != "toggles" && p.Kind != "info")
                    .Select(PartSummary).Where(s => !string.IsNullOrEmpty(s)).Take(3).ToList();
                return parts.Count > 0 ? string.Join(" · ", parts) : LemoineStrings.T("overviewDemo.ready");
            }
            return PartSummary(step);
        }

        private string PartSummary(OverviewDemoStep step)
        {
            switch (step.Kind)
            {
                case "multi":
                    return _multi.TryGetValue(step.Id, out var m) && m.Count > 0
                        ? $"{m.Count} selected" : LemoineStrings.T("overviewDemo.noneSelected");
                case "file":
                    return _value.TryGetValue(step.Id, out var f) && !string.IsNullOrEmpty(f)
                        ? System.IO.Path.GetFileName(f) : LemoineStrings.T("overviewDemo.noFile");
                case "single":
                case "search":
                case "text":
                    return _value.TryGetValue(step.Id, out var v) && !string.IsNullOrEmpty(v) ? v : "—";
                case "number":
                    return _number.TryGetValue(step.Id, out var n)
                        ? $"{n}{(string.IsNullOrEmpty(step.NumUnit) ? "" : " " + step.NumUnit)}" : "—";
                default:
                    return LemoineStrings.T("overviewDemo.ready");
            }
        }

        // ── Simulated run ─────────────────────────────────────────────────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            pushLog(LemoineStrings.T("overviewDemo.demoBanner"), "info");
            pushLog(string.IsNullOrEmpty(_spec.SampledFrom)
                ? LemoineStrings.T("overviewDemo.cannedData")
                : LemoineStrings.T("overviewDemo.sampledFrom", _spec.SampledFrom), "info");

            int pass = 0, fail = 0, skip = 0;
            var log = _spec.RunLog;
            for (int i = 0; i < log.Length; i++)
            {
                var (text, status) = log[i];
                pushLog(ExpandTokens(text), status);
                if (status == "pass")      pass++;
                else if (status == "fail") fail++;
                else if (status == "skip") skip++;
                onProgress((i + 1) * 100 / Math.Max(1, log.Length), pass, fail, skip);
            }
            onComplete(pass, fail, skip);
        }

        // ── Run-log tokens — echo the user's actual demo selections ──────────
        private static readonly Regex TokenRx =
            new Regex(@"\{(count|sel|first|value|file):([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

        private string ExpandTokens(string line) =>
            TokenRx.Replace(line, m => TokenValue(m.Groups[1].Value, m.Groups[2].Value));

        private string TokenValue(string kind, string id)
        {
            switch (kind)
            {
                case "count":
                    return (_multi.TryGetValue(id, out var c) ? c.Count : 0).ToString();
                case "sel":
                    if (_multi.TryGetValue(id, out var sel) && sel.Count > 0)
                    {
                        var names = sel.Take(3).ToList();
                        int extra = sel.Count - names.Count;
                        return string.Join(", ", names)
                            + (extra > 0 ? LemoineStrings.T("overviewDemo.moreSuffix", extra) : "");
                    }
                    return "—";
                case "first":
                    return _multi.TryGetValue(id, out var f) && f.Count > 0 ? f.First() : "—";
                case "value":
                    if (_value.TryGetValue(id, out var v) && !string.IsNullOrEmpty(v)) return v;
                    if (_number.TryGetValue(id, out var n)) return n.ToString("0.###");
                    return "—";
                case "file":
                    return _value.TryGetValue(id, out var p) && !string.IsNullOrEmpty(p)
                        ? System.IO.Path.GetFileName(p) : "—";
                default:
                    return "—";
            }
        }

        private static TextBlock MakeLabel(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 4) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock MakeHint(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }
    }
}
