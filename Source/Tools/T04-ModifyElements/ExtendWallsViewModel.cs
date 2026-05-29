using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

using WpfGrid = System.Windows.Controls.Grid;

namespace LemoineTools.Tools.ModifyElements
{
    public class ExtendWallsViewModel : ILemoineTool, ILemoineReviewable
    {
        public string Title    => "Extend Walls to Level";
        public string RunLabel => "Extend in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Base Levels", required: true),
            new StepDefinition("S2", "Options",            required: false),
            new StepDefinition("S3", "Review & Run",       required: false),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private List<string>                       _selectedLevelNames  = new List<string>();
        private readonly Dictionary<string, Level> _levelsByName;
        private double                             _assumedCeilingFt    = 9.0;
        private bool                               _activeViewOnly      = true;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly ExtendWallsEventHandler _handler;
        private readonly ExternalEvent           _event;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public ExtendWallsViewModel(
            ExtendWallsEventHandler handler,
            ExternalEvent           externalEvent,
            IEnumerable<Level>      processableLevels)
        {
            _handler      = handler;
            _event        = externalEvent;
            _levelsByName = processableLevels
                .OrderBy(l => l.Elevation)
                .GroupBy(l => l.Name)
                .ToDictionary(g => g.Key, g => g.First());
        }

        // ═════════════════════════════════════════════════════════════════════
        //  GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return null; // framework renders review (ILemoineReviewable)
                default:   return null;
            }
        }

        private FrameworkElement BuildS1()
        {
            if (_levelsByName.Count == 0)
            {
                var msg = new TextBlock
                {
                    Text         = "No processable levels found. At least two levels are required: a base level and a level above it.",
                    TextWrapping = TextWrapping.Wrap,
                };
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                return msg;
            }

            var groups = new Dictionary<string, List<string>>
            {
                { "Levels", _levelsByName.Keys.ToList() }
            };

            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(groups);
            tabs.SelectionChanged += selected =>
            {
                _selectedLevelNames = new List<string>(selected);
                OnValidationChanged();
            };
            return tabs;
        }

        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel();

            // Assumed ceiling height — labeled single-value input via LemoineNumberRange
            // Using MinLabel for the value, MaxLabel deliberately left empty
            var heightRange = new LemoineNumberRange
            {
                MinLabel = "Assumed ceiling height (ft)",
                MaxLabel = "",
                AbsMin   = 1.0,
                AbsMax   = 100.0,
                Step     = 0.5,
            };
            heightRange.SetValues(_assumedCeilingFt, null);
            heightRange.RangeChanged += (min, max) =>
            {
                if (min.HasValue && min.Value > 0) _assumedCeilingFt = min.Value;
                OnValidationChanged();
            };
            outer.Children.Add(heightRange);

            var toggle = new LemoineToggleSwitches();
            toggle.SetItems(new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id        = "viewOnly",
                    Label     = "Active view only",
                    Desc      = "When on, only walls visible in the active view are scanned. " +
                                "When off, all walls in the document are scanned.",
                    DefaultOn = true,
                },
            });
            toggle.StateChanged += state =>
            {
                _activeViewOnly = !state.TryGetValue("viewOnly", out bool v) || v;
                OnValidationChanged();
            };
            outer.Children.Add(toggle);

            return outer;
        }

        private void AddCard(WpfGrid grid, string label, Func<string> val, int row, int col)
        {
            var card = new Border
            {
                Margin          = new Thickness(col == 0 ? 0 : 4, row == 0 ? 0 : 4, 0, 0),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
            };
            card.SetResourceReference(Border.PaddingProperty,    "LemoineTh_CardPad");
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var lbl = new TextBlock { Text = label.ToUpper(), Margin = new Thickness(0, 0, 0, 2) };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var valText = new TextBlock { FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap };
            valText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            valText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            valText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            valText.Text = val();
            ValidationChanged += (s, e) => valText.Text = val();

            var sp = new StackPanel();
            sp.Children.Add(lbl);
            sp.Children.Add(valText);
            card.Child = sp;
            WpfGrid.SetRow(card, row);
            WpfGrid.SetColumn(card, col);
            grid.Children.Add(card);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════
        // ── ILemoineReviewable (P3) — framework renders the review step ───────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("levels",  "Base Levels"),
            ("ceiling", "Assumed Ceiling Height"),
            ("scope",   "Scope"),
            ("target",  "Target"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["levels"]  = _selectedLevelNames.Count == 0 ? "—" : $"{_selectedLevelNames.Count} level(s)",
            ["ceiling"] = $"{_assumedCeilingFt:F2} ft (used when no Ceiling element found)",
            ["scope"]   = _activeViewOnly ? "Active view" : "Entire document",
            ["target"]  = "Walls above ceiling — set Top Constraint to next level",
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => "Walls whose top elevation exceeds the ceiling threshold at their " +
            "base level will have their Top Constraint set to the next level up, with Top Offset zeroed. Curtain " +
            "walls and walls without a base constraint are skipped.";
        public string?        ReviewWarning => null;

        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedLevelNames.Count > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _selectedLevelNames.Count == 0 ? "—"
                    : $"{_selectedLevelNames.Count} level(s) selected";
            if (stepId == "S2")
                return $"Ceiling: {_assumedCeilingFt:F2} ft — Scope: {(_activeViewOnly ? "active view" : "document")}";
            if (stepId == "S3")
                return "Ready to run";
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.SelectedLevelIds  = _selectedLevelNames
                .Where(n => _levelsByName.ContainsKey(n))
                .Select(n => _levelsByName[n].Id)
                .ToList();
            _handler.AssumedCeilingFt  = _assumedCeilingFt;
            _handler.ActiveViewOnly    = _activeViewOnly;
            _handler.OnLog             = pushLog;
            _handler.OnProgress        = onProgress;
            _handler.OnComplete        = onComplete;

            _event.Raise();
        }
    }
}
