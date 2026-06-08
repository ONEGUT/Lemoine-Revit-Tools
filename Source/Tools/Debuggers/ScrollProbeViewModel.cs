using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Debuggers
{
    /// <summary>
    /// Scroll-mechanics probe (per CLAUDE.md "Build a Debugger First" for large, ambiguous UI
    /// problems). Each step isolates ONE scroll construct that has been reported as misbehaving and
    /// pairs it with a LIVE readout: every wheel notch over the step prints the ScrollViewer under
    /// the cursor and its metrics (offset / extent / viewport / scrollable / CanContentScroll +
    /// delta). This turns "the dropdown scrolls wrong" into observed numbers — which scroller is
    /// being driven, in what units, and how far per notch — so the fix targets the real construct
    /// instead of being guessed from reading code.
    ///
    /// Opened from the reserved Developer-panel button (DebugToolCommand). Revit-free.
    /// </summary>
    public sealed class ScrollProbeViewModel : ILemoineTool
    {
        public string Title    => "Scroll Probe";
        public string RunLabel => "Done";

        public StepDefinition[] Steps { get; } =
        {
            new StepDefinition("S1", "Page scroller (baseline)",        required: false),
            new StepDefinition("S2", "ComboBox dropdown (fill-type)",   required: false),
            new StepDefinition("S3", "MultiSelectTabs (discover menu)", required: false),
            new StepDefinition("S4", "Tag-chip popup",                  required: false),
            new StepDefinition("S5", "Nested scrollers",                required: false),
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
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return BuildS3();
                case "S4": return BuildS4();
                case "S5": return BuildS5();
                default:   return new Grid();
            }
        }

        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
            => onComplete(0, 0, 0);

        // ── S1 — plain page scroller, no special wiring (baseline) ───────────────
        private FrameworkElement BuildS1()
        {
            var list = TextList("Page item", 40);
            return Step("PAGE SCROLLER",
                "Baseline. Scroll the wheel over this list — it should move smoothly a few lines " +
                "per notch, both up and down. The readout shows which scroller is driven and its units.",
                list);
        }

        // ── S2 — ComboBox dropdown (the reported fill-type / discover combos) ─────
        private FrameworkElement BuildS2()
        {
            var combo = new ComboBox
            {
                IsEditable          = true,
                IsTextSearchEnabled = false,
                StaysOpenOnEdit     = true,
                MaxDropDownHeight   = 200,
                Width               = 220,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            for (int i = 1; i <= 40; i++) combo.Items.Add($"Fill pattern {i}");
            combo.SelectedIndex = 0;

            return Step("COMBOBOX DROPDOWN",
                "Open the dropdown and scroll. It should move a few items per notch, both ways — " +
                "NOT jump top-to-bottom and NOT scroll the page behind it. Read the live numbers: " +
                "if CanContentScroll=True the units are ITEMS, if False they are PIXELS.",
                combo);
        }

        // ── S3 — MultiSelectTabs (the discover categories "menu") ────────────────
        private FrameworkElement BuildS3()
        {
            var groups = new Dictionary<string, List<string>>();
            for (int g = 1; g <= 4; g++)
            {
                var items = new List<string>();
                for (int i = 1; i <= 30; i++) items.Add($"G{g} item {i}");
                groups[$"Group {g}"] = items;
            }
            var tabs = new LemoineMultiSelectTabs { MaxHeight = 200 };
            tabs.SetGroups(groups);

            return Step("MULTISELECTTABS",
                "This is the discover-rules category menu. Scroll over the LEFT tab column and the " +
                "RIGHT checkbox list independently — each should scroll itself, and at its limit the " +
                "page should take over. Watch the readout to confirm the inner scroller is driven.",
                tabs);
        }

        // ── S4 — tag-chip popup ──────────────────────────────────────────────────
        private FrameworkElement BuildS4()
        {
            var chips = new LemoineTagChipInput { Placeholder = "Add tags…", AllowFreeText = true };
            var items = new List<string>();
            for (int i = 1; i <= 40; i++) items.Add($"Tag {i}");
            chips.ItemsSource = items;

            return Step("TAG-CHIP POPUP",
                "Click '+', then scroll the popup result list. It should scroll both directions and " +
                "never move the page behind it. (Readout updates when the wheel reaches the window.)",
                chips);
        }

        // ── S5 — nested scrollers (inner list inside the page scroller) ──────────
        private FrameworkElement BuildS5()
        {
            var inner = new ScrollViewer
            {
                Height                        = 130,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            inner.Content = TextListPanel("Inner item", 25);

            var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
            frame.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            frame.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            frame.Child = inner;

            return Step("NESTED SCROLLERS",
                "Scroll inside the inner list. While it can still move it should scroll itself; once " +
                "it hits its top/bottom the OUTER page content should take over (no stall at the edge).",
                frame);
        }

        // ── Step scaffold: tag + instructions + construct + LIVE wheel readout ────
        private static FrameworkElement Step(string tag, string body, FrameworkElement construct)
        {
            var sp = new StackPanel();

            var hdr = new TextBlock { Text = tag, Margin = new Thickness(0, 0, 0, 4) };
            hdr.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            hdr.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            hdr.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            sp.Children.Add(hdr);

            var txt = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            txt.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            txt.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            txt.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            sp.Children.Add(txt);

            sp.Children.Add(construct);

            // Live readout
            var readout = new TextBlock
            {
                Text         = "(scroll over the construct to read its scroller metrics)",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 12, 0, 0),
            };
            readout.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            readout.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            readout.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            sp.Children.Add(readout);

            // handledEventsToo:true so we observe the wheel even after a scroll handler marks it
            // handled. This reports the scroller under the cursor at the moment of the notch.
            sp.AddHandler(UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler((s, e) => readout.Text = DescribeScrollerUnderMouse(e.Delta)),
                handledEventsToo: true);

            return sp;
        }

        private static string DescribeScrollerUnderMouse(int delta)
        {
            var sv = ScrollerUnderMouse();
            if (sv == null)
                return $"notch Δ={delta}: no ScrollViewer under cursor " +
                       "(a ComboBox dropdown delivered to its own popup hwnd won't report here).";
            return $"notch Δ={delta} | CanContentScroll={sv.CanContentScroll} | " +
                   $"offset={sv.VerticalOffset:0.##} / scrollable={sv.ScrollableHeight:0.##} " +
                   $"| extent={sv.ExtentHeight:0.##} viewport={sv.ViewportHeight:0.##}";
        }

        private static ScrollViewer? ScrollerUnderMouse()
        {
            var d = Mouse.DirectlyOver as DependencyObject;
            while (d != null)
            {
                if (d is ScrollViewer sv) return sv;
                d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                        ? VisualTreeHelper.GetParent(d)
                        : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        // ── small content helpers ───────────────────────────────────────────────
        private static ScrollViewer TextList(string label, int count)
        {
            var sv = new ScrollViewer
            {
                Height                        = 200,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content                       = TextListPanel(label, count),
            };
            return sv;
        }

        private static StackPanel TextListPanel(string label, int count)
        {
            var list = new StackPanel();
            for (int i = 1; i <= count; i++)
            {
                var tb = new TextBlock { Text = $"{label} {i}", Margin = new Thickness(8, 3, 8, 3) };
                tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                list.Children.Add(tb);
            }
            return list;
        }
    }
}
