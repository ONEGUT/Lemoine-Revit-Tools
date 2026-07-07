using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.ModifyElements;

namespace LemoineTools.Tools.Debuggers
{
    /// <summary>
    /// Debug harness UI for the four split tools — see DebugSplitToolsEventHandler for the
    /// engine side. One step per tool, each self-contained: static "what to place / what cuts
    /// it" instructions, a "Run Test" button, and its own inline results log. No framework
    /// review step — every step documents its own run in place instead of funnelling to one
    /// final run. The framework still requires ILemoineTool.Run() (triggered by the last step's
    /// Confirm button); it simply re-runs the Cell test into the framework's own Output Log,
    /// for parity with how every other Lemoine tool's final step behaves.
    /// Debug-only tool: all display text is deliberately hardcoded (not LemoineStrings-routed).
    /// </summary>
    internal sealed class DebugSplitToolsViewModel : ILemoineTool, ILemoineToolCleanup
    {
        public string Title    => "Debug — Split Tools";
        public string RunLabel => "Re-run Cell Test (Output Log) →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Split by Level",           required: false),
            new StepDefinition("S2", "Split by Grid",             required: false),
            new StepDefinition("S3", "Split by Reference Plane",  required: false),
            new StepDefinition("S4", "Split by Cell",             required: false),
        };

        private readonly DebugSplitToolsEventHandler _handler;
        private readonly ExternalEvent                _event;
        private readonly Dictionary<string, StackPanel> _resultsPanels = new Dictionary<string, StackPanel>();

        public event EventHandler? ValidationChanged;

        public DebugSplitToolsViewModel(DebugSplitToolsEventHandler handler, ExternalEvent externalEvent)
        {
            _handler = handler;
            _event   = externalEvent;
        }

        public void OnWindowClosed()
        {
            _handler.OnLog      = null;
            _handler.OnComplete = null;
        }

        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildStep(stepId, SplitCutterKind.Level,
                    "Cutting boundary: every Level in the document.", DebugSplitMode.Level);
                case "S2": return BuildStep(stepId, SplitCutterKind.Grid,
                    "Cutting boundary: every straight Grid line in the document.", DebugSplitMode.Grid);
                case "S3": return BuildStep(stepId, SplitCutterKind.ReferencePlane,
                    "Cutting boundary: every Reference Plane in the document.", DebugSplitMode.ReferencePlane);
                case "S4": return BuildStep(stepId, SplitCutterKind.Cell,
                    "Cutting boundary: a 10' × 10' cell grid, from each element's own bounding-box origin.", DebugSplitMode.Cell);
                default:   return null;
            }
        }

        private FrameworkElement BuildStep(string stepId, SplitCutterKind cutter, string cutText, DebugSplitMode mode)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var outer = new StackPanel();

            var placeTb = new TextBlock
            {
                Text = "Place at least one of the following in the ACTIVE VIEW: " +
                       string.Join(", ", SplitTargets.For(cutter).Select(t => t.Label)) + ".",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 4),
            };
            placeTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            placeTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            placeTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(placeTb);

            var cutTb = new TextBlock
            {
                Text = cutText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
            };
            cutTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            cutTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            cutTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            outer.Children.Add(cutTb);

            var warnTb = new TextBlock
            {
                Text = "This performs a REAL split and commits it — the same code path a normal run " +
                       "uses. Use a scratch/test file, or Undo (Ctrl+Z) after each run." +
                       (stepId == "S4" ? " The Confirm button below re-runs this same test into the Output Log panel further down." : ""),
                TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 0, 0, 10),
            };
            warnTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            warnTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            warnTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(warnTb);

            var runBtn = BuildRunButton();
            outer.Children.Add(runBtn);

            var resultsBorder = new Border
            {
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(0, 8, 0, 0),
            };
            resultsBorder.SetResourceReference(Border.PaddingProperty,     "LemoineTh_CardPad");
            resultsBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            resultsBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var resultsPanel = new StackPanel();
            _resultsPanels[stepId] = resultsPanel;
            AppendResultLine(resultsPanel, "Not run yet.", "info");
            resultsBorder.Child = resultsPanel;
            outer.Children.Add(resultsBorder);

            runBtn.Click += (s, e) =>
            {
                resultsPanel.Children.Clear();
                AppendResultLine(resultsPanel, "Running…", "info");

                _handler.Mode       = mode;
                _handler.OnLog      = (msg, status) => SafeInvoke(dispatcher, () => AppendResultLine(resultsPanel, msg, status));
                _handler.OnComplete = null;
                _event.Raise();
            };

            return outer;
        }

        private static void SafeInvoke(Dispatcher dispatcher, Action action)
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            dispatcher.BeginInvoke(action);
        }

        private static void AppendResultLine(StackPanel panel, string msg, string status)
        {
            var tb = new TextBlock { Text = msg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            if      (status == "pass") tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineGreen");
            else if (status == "fail") tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            else                       tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            panel.Children.Add(tb);
        }

        private static Button BuildRunButton()
        {
            var btn = new Button
            {
                Content         = "Run Test",
                BorderThickness = new Thickness(1),
                Cursor          = Cursors.Hand,
                Template        = LemoineControlStyles.BuildFlatButtonTemplate(),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            btn.SetResourceReference(Button.MinHeightProperty,   "LemoineH_BtnMin");
            btn.SetResourceReference(Button.PaddingProperty,     "LemoineTh_BtnPad");
            btn.SetResourceReference(Button.FontSizeProperty,    "LemoineFS_MD");
            btn.SetResourceReference(Button.FontFamilyProperty,  "LemoineUiFont");
            btn.SetResourceReference(Button.BackgroundProperty,  "LemoineAccentDim");
            btn.SetResourceReference(Button.BorderBrushProperty, "LemoineAccent");
            btn.SetResourceReference(Button.ForegroundProperty,  "LemoineAccent");
            LemoineMotion.WirePress(btn);
            return btn;
        }

        // ═════════════════════════════════════════════════════════════════════
        public bool   IsValid(string stepId)    => true;
        public string SummaryFor(string stepId) => "Manual test — see results above";

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            pushLog("Re-running the Split by Cell test (same as step S4's own Run Test button).", "info");
            _handler.Mode       = DebugSplitMode.Cell;
            _handler.OnLog      = pushLog;
            _handler.OnComplete = () => onComplete(0, 0, 0);
            _event.Raise();
        }
    }
}
