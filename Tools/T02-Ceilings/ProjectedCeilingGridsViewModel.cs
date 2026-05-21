using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Ceilings
{
    // =========================================================================
    // ProjectedCeilingGridsViewModel
    //
    // THIS IS THE TEMPLATE for new Revit tool functions.
    //
    // To add a new tool:
    //   1. Copy this file to Tools/YourFeature/YourFeatureViewModel.cs
    //   2. Update Title, RunLabel, Steps
    //   3. Rewrite GetStepContent(), IsValid(), SummaryFor(), Run()
    //   4. Add a ribbon button in App.cs → new StepFlowWindow(new YourViewModel())
    //
    // You never touch StepFlowWindow, the controls, or themes.
    // =========================================================================
    public class ProjectedCeilingGridsViewModel : ILemoineTool
    {
        // ── ILemoineTool identity ─────────────────────────────────────────────
        public string Title    => "Projected Ceiling Grids";
        public string RunLabel => "Run in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "DWG Source File",  required: true),
            new StepDefinition("S2", "Review & Run",     required: false),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private string _dwgPath = "";

        // ── Validation change notification ─────────────────────────────────
        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── ExternalEvent wiring ───────────────────────────────────────────
        // The handler and event are created in App.cs at startup and stored
        // as static references — safe because only one instance of each tool
        // window can be open at a time.
        private readonly CeilingGridEventHandler _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent _event;

        public ProjectedCeilingGridsViewModel(
            CeilingGridEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent)
        {
            _handler = handler;
            _event   = externalEvent;
        }

        // ═════════════════════════════════════════════════════════════════════
        // GetStepContent — return the WPF control(s) for each step
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1")
            {
                var browser = new LemoineFileBrowser
                {
                    Label       = "Select the ceiling plan DWG to project onto ceiling soffit faces in the active view.",
                    Filter      = "AutoCAD DWG|*.dwg|All files|*.*",
                    DialogTitle = "Select Ceiling Plan DWG",
                    Recents     = new List<string>
                    {
                        @"C:\Projects\PROJ-001\CAD\Ceiling-Plan.dwg",
                        @"C:\Projects\PROJ-002\CAD\Ceiling-Plan.dwg",
                    },
                };
                browser.PathChanged += path =>
                {
                    _dwgPath = path ?? "";
                    OnValidationChanged();
                };
                return browser;
            }

            if (stepId == "S2")
                return BuildReviewPanel();

            return null;
        }

        private FrameworkElement BuildReviewPanel()
        {
            // A simple two-column grid of summary cards — mirrors the HTML Review step
            var outer = new StackPanel();

            var infoPanel = BuildInfoPanel();
            outer.Children.Add(infoPanel);

            var desc = new TextBlock
            {
                Text         = "The DWG will be imported into the active view at origin, all curves extracted, " +
                               "then the import deleted. Each curve is projected vertically onto matching ceiling " +
                               "soffit faces and recreated as a model curve at the correct elevation.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 8, 0, 0),
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            desc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(desc);

            return outer;
        }

        // Helper struct avoids the mixed-type implicit array inference error in net48 C#8
        private struct CardDef
        {
            public string       Label;
            public Func<string> Val;
            public int          Row;
            public int          Col;
            public CardDef(string label, Func<string> val, int row, int col)
            { Label = label; Val = val; Row = row; Col = col; }
        }

        private Grid BuildInfoPanel()
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var cards = new[]
            {
                new CardDef("DWG File",    () => string.IsNullOrEmpty(_dwgPath) ? "—" : System.IO.Path.GetFileName(_dwgPath), 0, 0),
                new CardDef("Target View", () => "Active view",        0, 1),
                new CardDef("Operation",   () => "Project → soffit",   1, 0),
                new CardDef("Output",      () => "Model curves",        1, 1),
            };

            foreach (var c in cards)
            {
                var card = new Border
                {
                    Margin          = new Thickness(c.Col == 0 ? 0 : 4, c.Row == 0 ? 0 : 4, 0, 0),
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(3),
                    Padding         = new Thickness(10, 7, 10, 7),
                };
                card.SetResourceReference(Border.BackgroundProperty,   "LemoineRaised");
                card.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");

                var lbl = new TextBlock
                {
                    Text = c.Label.ToUpper(),
                    Margin   = new Thickness(0, 0, 0, 2),
                };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty,  "LemoineUiFont");

                var capturedVal = c.Val;
                var valText = new TextBlock
                {
                    Text         = capturedVal(),
                    FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap,
                };
                valText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                valText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                valText.SetResourceReference(TextBlock.FontFamilyProperty,  "LemoineMonoFont");

                ValidationChanged += (s, e) => valText.Text = capturedVal();

                var sp = new StackPanel();
                sp.Children.Add(lbl);
                sp.Children.Add(valText);
                card.Child = sp;

                Grid.SetRow(card, c.Row);
                Grid.SetColumn(card, c.Col);
                grid.Children.Add(card);
            }

            return grid;
        }

        // ═════════════════════════════════════════════════════════════════════
        // IsValid — gate the Confirm button on required steps
        // ═════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return !string.IsNullOrWhiteSpace(_dwgPath);
            return true;
        }

        // ═════════════════════════════════════════════════════════════════════
        // SummaryFor — one-line collapsed summary per step
        // ═════════════════════════════════════════════════════════════════════
        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return string.IsNullOrEmpty(_dwgPath) ? "—"
                    : System.IO.Path.GetFileName(_dwgPath);
            if (stepId == "S2") return "Ready to run";
            return "—";
        }

        // ═════════════════════════════════════════════════════════════════════
        // Run — fires the ExternalEvent; Revit calls back via the callbacks
        // ═════════════════════════════════════════════════════════════════════
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            // Store parameters in the handler (main-thread access at Execute time)
            _handler.DwgPath    = _dwgPath;
            _handler.PushLog    = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;
            _handler.Mode       = CeilingGridEventHandler.ToolMode.Project;

            pushLog("Raising Revit ExternalEvent…", "info");
            _event.Raise();
        }
    }
}
