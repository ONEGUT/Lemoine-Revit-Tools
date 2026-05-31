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
    /// Crash-isolation harness (per CLAUDE.md "Crashes & Large Ambiguous Issues — Build a
    /// Debugger First"). Each step LAZILY constructs one suspect WPF construct behind a
    /// "Build" button, because StepFlowWindow builds step content eagerly at construction —
    /// deferring to a Click is the only way to isolate a construct that crashes Revit on
    /// build. Open the harness, walk the steps, and click each "Build" button: the press
    /// that closes/hangs Revit names the culprit. (A managed exception would instead show
    /// in diagnostics.log; a hard crash leaves no log entry.)
    /// </summary>
    public sealed class CrashProbeViewModel : ILemoineTool
    {
        public string Title    => "Crash Probe";
        public string RunLabel => "Done";

        public StepDefinition[] Steps { get; } =
        {
            new StepDefinition("P1", "Color swatches × 48 (WireSwatchHover)", required: false),
            new StepDefinition("P2", "MultiSelectTabs 8 × 40 (tab/row hover)", required: false),
            new StepDefinition("P3", "Toggle switches × 6",                   required: false),
            new StepDefinition("P4", "Folder browser + text field + select",  required: false),
            new StepDefinition("P5", "Review summary (heavy)",                required: false),
            new StepDefinition("P6", "Autocomplete + tag chips",              required: false),
        };

        public bool   IsValid(string stepId)    => true;
        public string SummaryFor(string stepId) => "";

#pragma warning disable 67 // harness never re-validates
        public event EventHandler? ValidationChanged;
#pragma warning restore 67

        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "P1": return Probe("48 color swatches in a wrap panel", BuildSwatches);
                case "P2": return Probe("MultiSelectTabs with 8 groups × 40 items", BuildTabs);
                case "P3": return Probe("LemoineToggleSwitches with 6 toggles", BuildToggles);
                case "P4": return Probe("FolderBrowser + TextField + SingleSelect", BuildInputs);
                case "P5": return Probe("LemoineReviewSummary with 8 cards + chips + warning", BuildReview);
                case "P6": return Probe("SearchAutocomplete + TagChipInput", BuildSearchAndChips);
                default:   return new Grid();
            }
        }

        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
            => onComplete(0, 0, 0);

        // ── Probe scaffold: description + lazy "Build" button + host ─────────────
        private FrameworkElement Probe(string what, Func<FrameworkElement> build)
        {
            var sp = new StackPanel();

            var hint = new TextBlock
            {
                Text = "Click Build. If Revit closes or hangs the instant you click, THIS construct is the " +
                       "crash. If it builds fine, move to the next step. (Nothing builds until you click — " +
                       "so simply opening this window can't crash on these constructs.)",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 10),
            };
            hint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            hint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            sp.Children.Add(hint);

            var host = new Border { Margin = new Thickness(0, 10, 0, 0) };

            var btn = LemoineControlStyles.BuildButton($"▶ Build:  {what}", LemoineButtonVariant.Primary);
            btn.HorizontalAlignment = HorizontalAlignment.Left;
            btn.Click += (s, e) =>
            {
                btn.IsEnabled = false;
                host.Child = build(); // any crash here isolates the suspect
            };
            sp.Children.Add(btn);
            sp.Children.Add(host);
            return sp;
        }

        // ── Suspects ─────────────────────────────────────────────────────────────
        private FrameworkElement BuildSwatches()
        {
            var wrap = new WrapPanel();
            for (int i = 0; i < 48; i++)
            {
                var color = ColorFromHsv(i * (360.0 / 48.0), 0.6, 0.9);
                string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                wrap.Children.Add(LemoineColorPickerWindow.BuildColorPickerSwatch(() => hex, _ => { }, showHexLabel: false));
            }
            return wrap;
        }

        private FrameworkElement BuildTabs()
        {
            var tabs = new LemoineMultiSelectTabs { Height = 320 };
            var groups = new Dictionary<string, List<string>>();
            for (int g = 0; g < 8; g++)
            {
                var items = new List<string>();
                for (int i = 0; i < 40; i++) items.Add($"Group {g} · Item {i:00}");
                groups[$"Group {g}"] = items;
            }
            tabs.SetGroups(groups);
            return tabs;
        }

        private FrameworkElement BuildToggles()
        {
            var t = new LemoineToggleSwitches();
            var items = new List<ToggleItem>();
            for (int i = 0; i < 6; i++)
                items.Add(new ToggleItem { Id = $"t{i}", Label = $"Option {i}", Desc = $"Toggle option {i}.", DefaultOn = i % 2 == 0 });
            t.SetItems(items);
            return t;
        }

        private FrameworkElement BuildInputs()
        {
            var sp = new StackPanel();
            var folder = new LemoineFolderBrowser { Label = "Output folder" };
            folder.Margin = new Thickness(0, 0, 0, 10);
            sp.Children.Add(folder);
            var field = new LemoineTextField { Label = "Prefix", Placeholder = "Type…" };
            field.Margin = new Thickness(0, 0, 0, 10);
            sp.Children.Add(field);
            var sel = new LemoineSingleSelect { Label = "Mode" };
            sel.Items = new List<string> { "A", "B", "C" };
            sp.Children.Add(sel);
            return sp;
        }

        private FrameworkElement BuildReview()
        {
            var review = new LemoineReviewSummary();
            var items  = new List<(string, string)>();
            var values = new Dictionary<string, string>();
            for (int i = 0; i < 8; i++) { items.Add(($"k{i}", $"Field {i}")); values[$"k{i}"] = $"Value {i}"; }
            review.SetItems(items, values,
                new List<string> { "Chip A", "Chip B", "Chip C" },
                note: "A note beneath the summary.",
                warning: "⚠  A sample warning banner.");
            return review;
        }

        private FrameworkElement BuildSearchAndChips()
        {
            var sp = new StackPanel();
            var ac = new LemoineSearchAutocomplete { Placeholder = "Search…" };
            ac.Items = new List<string> { "Alpha", "Beta", "Gamma", "Delta" };
            ac.Margin = new Thickness(0, 0, 0, 10);
            sp.Children.Add(ac);
            var chips = new LemoineTagChipInput { Placeholder = "Add tags…", AllowFreeText = true };
            chips.ItemsSource = new List<string> { "One", "Two", "Three", "Four" };
            sp.Children.Add(chips);
            return sp;
        }

        // ── helper ───────────────────────────────────────────────────────────────
        private static Color ColorFromHsv(double h, double s, double v)
        {
            int hi = (int)(h / 60) % 6;
            double f = h / 60 - Math.Floor(h / 60);
            double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            double r, g, b;
            switch (hi)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
    }
}
