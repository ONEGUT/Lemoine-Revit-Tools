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
    /// Temporary test harness for the P0 (interaction-bug) and P1 (motion) UI work.
    /// Opened from the reserved Developer-panel button. Each step shows a
    /// "what to look for" label followed by the live control that exercises the
    /// change, all running inside the real StepFlowWindow so step transitions,
    /// button press, and the log-tab hover are tested in their actual context.
    /// </summary>
    public sealed class MotionTestViewModel : ILemoineTool
    {
        public string Title    => "P0 / P1 Motion Test";
        public string RunLabel => "Simulate Run →";

        public StepDefinition[] Steps { get; } =
        {
            new StepDefinition("T1", "Press & Step Motion (P1)",   required: false),
            new StepDefinition("T2", "Hover Animation (P1)",        required: false),
            new StepDefinition("T3", "Dropdown + Code Guard (P1+P0)", required: false),
            new StepDefinition("T4", "Scroll Bubbling (P0)",        required: false),
            new StepDefinition("T6", "Input Controls (P2)",         required: false),
            new StepDefinition("T5", "Review & Log Tabs (P0)",      required: false),
        };

        public bool   IsValid(string stepId)    => true;
        public string SummaryFor(string stepId) => "✓ checked";

#pragma warning disable 67 // interface event — the harness never needs to re-validate
        public event EventHandler? ValidationChanged;
#pragma warning restore 67

        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "T1": return BuildT1();
                case "T2": return BuildT2();
                case "T3": return BuildT3();
                case "T4": return BuildT4();
                case "T6": return BuildT6();
                case "T5": return BuildT5();
                default:   return new Grid();
            }
        }

        // ── T1 — press + step fade-slide ────────────────────────────────────────
        private FrameworkElement BuildT1()
        {
            var demo = FlatButton("Press me", accent: true);
            return LookFor("P1 · BUTTON PRESS + STEP FADE",
                "This step's content faded UP into view as the step opened — navigate " +
                "Back/Confirm to see it again. Press the button below (and the Confirm/Back " +
                "buttons): each should dip to ~97% scale snappily on press, then spring back.",
                demo);
        }

        // ── T2 — animated hover ─────────────────────────────────────────────────
        private FrameworkElement BuildT2()
        {
            var row = new Border
            {
                Padding         = new Thickness(12),
                CornerRadius    = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Cursor          = Cursors.Hand,
                Background      = Brushes.Transparent,
            };
            row.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            var t = new TextBlock { Text = "Hover this row" };
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            row.Child = t;
            LemoineMotion.WireHover(row,
                normalBgKey: null, hoverBgKey: "LemoineAccentDim",
                normalBorderKey: "LemoineBorder", hoverBorderKey: "LemoineAccent");

            return LookFor("P1 · HOVER",
                "Hover the row below: its background and border should FADE in (~180ms), " +
                "not snap. Move on and off rapidly — it must never get stuck highlighted.",
                row);
        }

        // ── T3 — dropdown hover (P1) + programmatic-set guard (P0) ──────────────
        private FrameworkElement BuildT3()
        {
            var ac = new LemoineSearchAutocomplete { Placeholder = "Type to filter…" };
            ac.Items = new List<string> { "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta" };

            var setBtn = FlatButton("Set value in code →  \"Gamma\"", accent: false);
            setBtn.Margin = new Thickness(0, 8, 0, 0);
            setBtn.Click += (s, e) => ac.Value = "Gamma"; // programmatic — must NOT open dropdown

            var box = new StackPanel();
            box.Children.Add(ac);
            box.Children.Add(setBtn);

            return LookFor("P1 HOVER + P0 GUARD",
                "P1: click the field and hover the dropdown rows — the highlight should fade, " +
                "not snap. P0: click 'Set value in code' — the field fills with \"Gamma\" but " +
                "the dropdown must NOT pop open. Only real typing opens it.",
                box);
        }

        // ── T4 — nested scroll bubbling ─────────────────────────────────────────
        private FrameworkElement BuildT4()
        {
            var inner = new ScrollViewer
            {
                Height                        = 110,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            var list = new StackPanel();
            for (int i = 1; i <= 25; i++)
            {
                var tb = new TextBlock { Text = $"Inner list item {i}", Margin = new Thickness(8, 3, 8, 3) };
                tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                list.Children.Add(tb);
            }
            inner.Content = list;
            LemoineControlStyles.WireBubblingScroll(inner);

            var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
            frame.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            frame.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            frame.Child = inner;

            return LookFor("P0 · SCROLL BUBBLING",
                "Scroll the wheel inside the inner list. When it reaches the top or bottom, " +
                "continued scrolling should keep moving the OUTER window content instead of " +
                "stalling at the list edge.",
                frame);
        }

        // ── T6 — P2 input controls (folder browser, text field, read-only select) ─
        private FrameworkElement BuildT6()
        {
            var stack = new StackPanel();

            var folder = new LemoineFolderBrowser { Label = "Output folder", DialogTitle = "Pick a folder" };
            folder.Margin = new Thickness(0, 0, 0, 12);
            stack.Children.Add(folder);

            var field = new LemoineTextField { Label = "Prefix", Placeholder = "Type a value…" };
            field.Margin = new Thickness(0, 0, 0, 12);
            stack.Children.Add(field);

            var single = new LemoineSingleSelect { Label = "Mode" };
            single.Items = new List<string> { "Automatic", "Manual", "Hybrid" };
            stack.Children.Add(single);

            return LookFor("P2 · INPUT CONTROLS",
                "Folder browser: 'Browse…' opens a folder dialog and fills the path. " +
                "Text field: the watermark disappears as you type. Mode: this is a single-choice " +
                "picker — it should read as a dropdown (no caret / no editable text box) and only " +
                "let you pick from the list.",
                stack);
        }

        // ── T5 — review summary + log-tab hover (tabs registered by the command) ─
        private FrameworkElement BuildT5()
        {
            var review = new LemoineReviewSummary();
            review.SetItems(
                new List<(string, string)>
                {
                    ("press",  "Button press"),
                    ("hover",  "Row hover"),
                    ("guard",  "Code-set guard"),
                    ("scroll", "Scroll bubbling"),
                },
                new Dictionary<string, string>
                {
                    ["press"]  = "0.97 scale",
                    ["hover"]  = "fade 180ms",
                    ["guard"]  = "no popup",
                    ["scroll"] = "bubbles",
                });

            return LookFor("P0 · LOG-TAB HOVER",
                "Click 'Simulate Run →', then look below the run button: two tabs appear " +
                "('Output Log' and 'Notes'). Hover the INACTIVE tab — its label brightens, and " +
                "must return to DIM when you move away. It must not stay bright.",
                review);
        }

        /// <summary>Second log tab content, registered by the command so the tab bar (and its hover) appears.</summary>
        public FrameworkElement BuildNotesTab()
        {
            var sp = new StackPanel { Margin = new Thickness(10) };
            var tb = new TextBlock
            {
                Text = "This 'Notes' tab exists only so the log tab bar is shown. " +
                       "Hover the other tab to test the P0 hover-reset fix.",
                TextWrapping = TextWrapping.Wrap,
            };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            sp.Children.Add(tb);
            return sp;
        }

        // ── Run — quick simulated log so the run button + log populate ───────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            pushLog("Press feedback wired (P1)", "pass");
            onProgress(33, 1, 0, 0);
            pushLog("Hover animation wired (P1)", "pass");
            onProgress(66, 2, 0, 0);
            pushLog("Scroll bubbling + guards wired (P0)", "pass");
            onProgress(100, 3, 0, 0);
            onComplete(3, 0, 0);
        }

        // ── helpers ──────────────────────────────────────────────────────────────
        private static FrameworkElement LookFor(string tag, string body, FrameworkElement control)
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

            sp.Children.Add(control);
            return sp;
        }

        private static Button FlatButton(string label, bool accent)
        {
            var btn = new Button
            {
                Content             = label,
                BorderThickness     = new Thickness(1),
                Cursor              = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                Template            = LemoineControlStyles.BuildFlatButtonTemplate(),
            };
            btn.SetResourceReference(Button.MinHeightProperty,  "LemoineH_BtnMin");
            btn.SetResourceReference(Button.PaddingProperty,    "LemoineTh_BtnPad");
            btn.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_MD");
            btn.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            if (accent)
            {
                btn.SetResourceReference(Button.BackgroundProperty,  "LemoineAccentDim");
                btn.SetResourceReference(Button.BorderBrushProperty, "LemoineAccent");
                btn.SetResourceReference(Button.ForegroundProperty,  "LemoineAccent");
            }
            else
            {
                btn.Background = Brushes.Transparent;
                btn.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
                btn.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
            }
            LemoineMotion.WirePress(btn);
            return btn;
        }
    }
}
