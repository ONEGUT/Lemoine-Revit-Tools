using System;
using System.Collections.Generic;
using System.Linq;
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
        /// <summary>file | multi | toggles | number | single | search | text | info</summary>
        public string Kind     { get; set; } = "info";
        public string Hint     { get; set; } = "";

        public Dictionary<string, List<string>>? Groups   { get; set; } // multi
        public List<ToggleItem>?                 Toggles  { get; set; } // toggles
        public List<string>?                     Items    { get; set; } // single / search

        public double NumValue { get; set; }
        public double NumMin   { get; set; }
        public double NumMax   { get; set; } = 100;
        public double NumStep  { get; set; } = 1;
        public int    NumDecimals { get; set; }
        public string NumUnit  { get; set; } = "";

        public string FileFilter      { get; set; } = "All files|*.*";
        public string FilePlaceholder { get; set; } = "Browse…";

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
        /// <summary>Lines the simulated run prints. Status ∈ {info, pass, fail, skip}.</summary>
        public (string Text, string Status)[] RunLog { get; set; } = Array.Empty<(string, string)>();
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
            // Seed text/number defaults so required-with-default steps start valid.
            foreach (var s in _spec.Steps)
            {
                if (s.Kind == "text" && !string.IsNullOrEmpty(s.TextDefault)) _value[s.Id] = s.TextDefault;
                if (s.Kind == "number") _number[s.Id] = s.NumValue;
            }
        }

        public string Title    => _spec.Title + "  —  Demo (no Revit changes)";
        public string RunLabel => _spec.RunLabel;

        public StepDefinition[] Steps =>
            _spec.Steps.Select(s => new StepDefinition(s.Id, s.Title, s.Required)).ToArray();

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        private OverviewDemoStep? Find(string id) => _spec.Steps.FirstOrDefault(s => s.Id == id);

        // ── Step content ──────────────────────────────────────────────────────
        public FrameworkElement? GetStepContent(string stepId)
        {
            var step = Find(stepId);
            if (step == null) return null;

            var stack = new StackPanel();
            if (!string.IsNullOrEmpty(step.Hint)) stack.Children.Add(MakeHint(step.Hint));

            switch (step.Kind)
            {
                case "file":    stack.Children.Add(BuildFile(step));    break;
                case "multi":   stack.Children.Add(BuildMulti(step));   break;
                case "toggles": stack.Children.Add(BuildToggles(step)); break;
                case "number":  stack.Children.Add(BuildNumber(step));  break;
                case "single":  stack.Children.Add(BuildSingle(step));  break;
                case "search":  stack.Children.Add(BuildSearch(step));  break;
                case "text":    stack.Children.Add(BuildText(step));    break;
                case "info":    if (!string.IsNullOrEmpty(step.Info)) stack.Children.Add(MakeHint(step.Info)); break;
            }
            return stack;
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
            sel.SelectionChanged += v => { _value[s.Id] = v ?? ""; Changed(); };
            return sel;
        }

        private FrameworkElement BuildSearch(OverviewDemoStep s)
        {
            var sa = new LemoineSearchAutocomplete
            {
                Items       = s.Items ?? new List<string>(),
                Placeholder = string.IsNullOrEmpty(s.TextPlaceholder) ? "Search…" : s.TextPlaceholder,
            };
            sa.SelectionChanged += v => { _value[s.Id] = v ?? ""; Changed(); };
            return sa;
        }

        private FrameworkElement BuildText(OverviewDemoStep s)
        {
            var edit = new LemoineInlineEdit
            {
                Text        = s.TextDefault,
                Placeholder = string.IsNullOrEmpty(s.TextPlaceholder) ? "Type a value…" : s.TextPlaceholder,
            };
            edit.TextCommitted += (_, t) => { _value[s.Id] = t ?? ""; Changed(); };
            return edit;
        }

        // ── Validation + summary ──────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            var step = Find(stepId);
            if (step == null || !step.Required) return true;

            switch (step.Kind)
            {
                case "multi":  return _multi.TryGetValue(stepId, out var m) && m.Count > 0;
                case "file":
                case "single":
                case "search":
                case "text":   return _value.TryGetValue(stepId, out var v) && !string.IsNullOrWhiteSpace(v);
                default:       return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            var step = Find(stepId);
            if (step == null) return "";
            if (!string.IsNullOrEmpty(step.Summary)) return step.Summary;

            switch (step.Kind)
            {
                case "multi":
                    return _multi.TryGetValue(stepId, out var m) && m.Count > 0
                        ? $"{m.Count} selected" : "None selected";
                case "file":
                    return _value.TryGetValue(stepId, out var f) && !string.IsNullOrEmpty(f)
                        ? System.IO.Path.GetFileName(f) : "No file";
                case "single":
                case "search":
                case "text":
                    return _value.TryGetValue(stepId, out var v) && !string.IsNullOrEmpty(v) ? v : "—";
                case "number":
                    return _number.TryGetValue(stepId, out var n)
                        ? $"{n}{(string.IsNullOrEmpty(step.NumUnit) ? "" : " " + step.NumUnit)}" : "—";
                default:
                    return "Ready";
            }
        }

        // ── Simulated run ─────────────────────────────────────────────────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            pushLog("Simulated run — no Revit document is opened or modified.", "info");

            int pass = 0, fail = 0, skip = 0;
            var log = _spec.RunLog;
            for (int i = 0; i < log.Length; i++)
            {
                var (text, status) = log[i];
                pushLog(text, status);
                if (status == "pass")      pass++;
                else if (status == "fail") fail++;
                else if (status == "skip") skip++;
                onProgress((i + 1) * 100 / Math.Max(1, log.Length), pass, fail, skip);
            }
            onComplete(pass, fail, skip);
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
