using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Debuggers
{
    /// <summary>
    /// Read-only display harness for <see cref="SourceIngestProbe"/>. Takes a fully
    /// pre-computed <see cref="ProbeReport"/> (gathered on Revit's main thread by the
    /// command) and renders one accordion step per section. This class is Revit-free —
    /// it runs on the StepFlowWindow STA thread and never touches the Revit API.
    /// </summary>
    public sealed class SourceIngestProbeViewModel : ILemoineTool
    {
        private readonly ProbeReport _report;

        public SourceIngestProbeViewModel(ProbeReport report)
        {
            _report = report ?? new ProbeReport { Headline = "(no report)" };
            Steps = _report.Sections
                .Select((s, i) => new StepDefinition("S" + i, $"{i + 1}. {s.Title}", required: false))
                .ToArray();
            if (Steps.Length == 0)
                Steps = new[] { new StepDefinition("S0", "No data", required: false) };
        }

        public string Title    => "Source-Ingest Probe";
        public string RunLabel => "Re-log summary →";

        public StepDefinition[] Steps { get; }

        public bool   IsValid(string stepId)    => true;
        public string SummaryFor(string stepId) => "";

#pragma warning disable 67 // interface event — the probe never re-validates
        public event EventHandler? ValidationChanged;
#pragma warning restore 67

        public FrameworkElement? GetStepContent(string stepId)
        {
            int idx = StepIndex(stepId);
            if (idx < 0 || idx >= _report.Sections.Count) return Mono(_report.Headline);

            var sp = new StackPanel();
            if (idx == 0)
            {
                var head = new TextBlock { Text = _report.Headline, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
                head.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                head.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
                head.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                sp.Children.Add(head);

                var copy = FlatButton("Copy full report to clipboard");
                copy.Click += (s, e) =>
                {
                    try { Clipboard.SetText(_report.ToText()); copy.Content = "Copied ✓"; }
                    catch (Exception ex) { LemoineLog.Swallowed("SourceIngestProbe: clipboard", ex); copy.Content = "Copy failed"; }
                };
                copy.Margin = new Thickness(0, 0, 0, 12);
                sp.Children.Add(copy);
            }

            sp.Children.Add(BuildSection(_report.Sections[idx]));
            return sp;
        }

        private static FrameworkElement BuildSection(ProbeSection sec)
        {
            var text = new TextBlock { Text = string.Join("\n", sec.Lines), TextWrapping = TextWrapping.NoWrap };
            text.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            text.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            text.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var inner = new StackPanel { Margin = new Thickness(10) };
            inner.Children.Add(text);

            var scroll = new ScrollViewer
            {
                MaxHeight                     = 420,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content                       = inner,
            };
            LemoineControlStyles.WireBubblingScroll(scroll);

            var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
            frame.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            frame.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            frame.Child = scroll;
            return frame;
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            pushLog(_report.Headline, "info");
            int n = _report.Sections.Count;
            for (int i = 0; i < n; i++)
            {
                pushLog("── " + _report.Sections[i].Title + " ──", "info");
                foreach (var line in _report.Sections[i].Lines)
                    if (!string.IsNullOrWhiteSpace(line)) pushLog(line, "info");
                onProgress(n == 0 ? 100 : (int)((i + 1) * 100.0 / n), i + 1, 0, 0);
            }
            onComplete(n, 0, 0);
        }

        // ── helpers ──────────────────────────────────────────────────────────────
        private int StepIndex(string stepId)
        {
            for (int i = 0; i < Steps.Length; i++) if (Steps[i].Id == stepId) return i;
            return -1;
        }

        private static FrameworkElement Mono(string s)
        {
            var tb = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }

        private static Button FlatButton(string label)
        {
            var btn = new Button
            {
                Content             = label,
                BorderThickness     = new Thickness(1),
                Cursor              = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                Template            = LemoineControlStyles.BuildFlatButtonTemplate(),
                Background          = Brushes.Transparent,
            };
            btn.SetResourceReference(Button.MinHeightProperty,  "LemoineH_BtnMin");
            btn.SetResourceReference(Button.PaddingProperty,    "LemoineTh_BtnPad");
            btn.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_MD");
            btn.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            btn.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
            btn.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
            LemoineMotion.WirePress(btn);
            return btn;
        }
    }
}
