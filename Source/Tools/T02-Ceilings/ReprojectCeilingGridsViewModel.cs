using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

using WpfGrid = System.Windows.Controls.Grid;

namespace LemoineTools.Tools.Ceilings
{
    public class ReprojectCeilingGridsViewModel : ILemoineTool, ILemoineReviewable
    {
        // ── Ceiling plan view entry passed in from Command ────────────────────
        public sealed class CeilingPlanViewEntry
        {
            public ElementId Id        { get; }
            public string    Name      { get; }
            public string    LevelName { get; }

            public CeilingPlanViewEntry(ElementId id, string name, string levelName)
            {
                Id        = id;
                Name      = name;
                LevelName = levelName;
            }
        }

        // ── ILemoineTool identity ─────────────────────────────────────────────
        public string Title    => "Reproject Ceiling Grids";
        public string RunLabel => "Run in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Ceiling Plans", required: true),
            new StepDefinition("S2", "Review & Run",         required: false),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private readonly List<CeilingPlanViewEntry>         _availableViews;
        private readonly Dictionary<string, ElementId>      _nameToId;
        private          List<ElementId>                    _selectedViewIds = new List<ElementId>();

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── ExternalEvent wiring ───────────────────────────────────────────
        private readonly CeilingGridEventHandler _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent _event;

        public ReprojectCeilingGridsViewModel(
            CeilingGridEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent,
            List<CeilingPlanViewEntry>? availableViews)
        {
            _handler        = handler;
            _event          = externalEvent;
            _availableViews = availableViews ?? new List<CeilingPlanViewEntry>();

            _nameToId = new Dictionary<string, ElementId>(StringComparer.Ordinal);
            foreach (var v in _availableViews)
                _nameToId[v.Name] = v.Id;
        }

        // ═════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildS1();
            if (stepId == "S2") return null; // framework renders review (ILemoineReviewable)
            return null;
        }

        private FrameworkElement BuildS1()
        {
            var outer = new StackPanel();

            if (_availableViews.Count == 0)
            {
                var none = new TextBlock
                {
                    Text         = "No ceiling plan views found in this project.",
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                };
                none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                outer.Children.Add(none);
                return outer;
            }

            // Group views by level
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var v in _availableViews.OrderBy(x => x.LevelName).ThenBy(x => x.Name))
            {
                string group = string.IsNullOrEmpty(v.LevelName) ? "Other" : v.LevelName;
                if (!groups.ContainsKey(group))
                    groups[group] = new List<string>();
                groups[group].Add(v.Name);
            }

            var tabs = new LemoineMultiSelectTabs();
            tabs.SelectionChanged += selected =>
            {
                _selectedViewIds = selected
                    .Where(name => _nameToId.ContainsKey(name))
                    .Select(name => _nameToId[name])
                    .ToList();
                OnValidationChanged();
            };
            tabs.SetGroups(groups, Enumerable.Empty<string>());
            outer.Children.Add(tabs);

            var warning = new TextBlock
            {
                Text         = "⚠  Original curves will be deleted and replaced — this cannot be undone individually.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 10, 0, 0),
            };
            warning.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            warning.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            warning.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            outer.Children.Add(warning);

            return outer;
        }

        // ─────────────────────────────────────────────────────────────────────
        private struct CardDef
        {
            public string       Label;
            public Func<string> Val;
            public int          Row;
            public int          Col;
            public CardDef(string label, Func<string> val, int row, int col)
            { Label = label; Val = val; Row = row; Col = col; }
        }

        // ── ILemoineReviewable (P3) — framework renders the review step ───────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("views",  "Views Selected"),
            ("target", "Target Ceilings"),
            ("op",     "Operation"),
            ("output", "Output"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["views"]  = _selectedViewIds.Count == 0 ? "None" : $"{_selectedViewIds.Count} ceiling plan(s)",
            ["target"] = "Visible per view",
            ["op"]     = "Delete → Reproject",
            ["output"] = "Model curves (updated Z)",
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => "For each selected ceiling plan, all ModelCurves visible in that " +
            "view are projected onto the current soffit geometry, then recreated at the updated ceiling " +
            "elevations. Curves with no ceiling overlap are skipped.";
        public string?        ReviewWarning => null;

        // ═════════════════════════════════════════════════════════════════════
        // IsValid
        // ═════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedViewIds.Count > 0;
            return true;
        }

        // ═════════════════════════════════════════════════════════════════════
        // SummaryFor
        // ═════════════════════════════════════════════════════════════════════
        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _selectedViewIds.Count == 0 ? "—"
                    : $"{_selectedViewIds.Count} ceiling plan(s)";
            if (stepId == "S2") return "Ready to run";
            return "—";
        }

        // ═════════════════════════════════════════════════════════════════════
        // Run
        // ═════════════════════════════════════════════════════════════════════
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.Mode           = CeilingGridEventHandler.ToolMode.Reproject;
            _handler.SelectedViewIds = new List<ElementId>(_selectedViewIds);
            _handler.BatchDwgFolder  = "";
            _handler.PushLog        = pushLog;
            _handler.OnProgress     = onProgress;
            _handler.OnComplete     = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
            _event.Raise();
        }
    }
}
