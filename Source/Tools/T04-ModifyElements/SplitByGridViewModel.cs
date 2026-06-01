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
    public class SplitByGridViewModel : ILemoineTool, IStepAware, ILemoineReviewable
    {
        public string Title    => "Split Elements by Grid Lines";
        public string RunLabel => "Split in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Categories", required: true),
            new StepDefinition("S2", "Select Grids",      required: true),
            new StepDefinition("S3", "Review & Run",      required: false),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private List<string>                                    _selectedCats      = new List<string>();
        private List<string>                                    _selectedGridNames = new List<string>();
        private bool                                            _useActiveView     = false;
        private Action<string>?                                 _rebuildContent;

        private Dictionary<string, Autodesk.Revit.DB.Grid>           _gridsByName;
        private readonly Dictionary<string, List<string>>             _categoryGroups;
        private readonly int                                          _totalElements;
        private readonly ElementId?                                   _activeViewId;
        private readonly IReadOnlyList<ElementId>                     _preSelectedIds;
        private readonly IReadOnlyList<string>                        _preSelectedCats;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly SplitByGridEventHandler _handler;
        private readonly ExternalEvent           _event;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public SplitByGridViewModel(
            SplitByGridEventHandler                handler,
            ExternalEvent                          externalEvent,
            IEnumerable<Autodesk.Revit.DB.Grid>    allGrids,
            Dictionary<string, List<string>>       categoryGroups,
            int                                    totalElements,
            ElementId?                             activeViewId,
            IReadOnlyList<ElementId>               preSelectedIds,
            IReadOnlyList<string>                  preSelectedCats)
        {
            _handler         = handler;
            _event           = externalEvent;
            _categoryGroups  = categoryGroups;
            _totalElements   = totalElements;
            _activeViewId    = activeViewId;
            _preSelectedIds  = preSelectedIds;
            _preSelectedCats = preSelectedCats;

            _gridsByName = BuildGridMap(allGrids);

            if (_preSelectedIds.Count > 0)
                _selectedCats = new List<string>(_preSelectedCats);
        }

        // ── IStepAware ────────────────────────────────────────────────────────
        public void SetContentRefreshCallback(Action<string> rebuildStepContent)
            => _rebuildContent = rebuildStepContent;

        public void OnStepActivated(string stepId)
        {
            if (stepId != "S2") return;

            _handler.IsRefreshRequest = true;
            _handler.OnRefreshed = freshGrids =>
            {
                _gridsByName = BuildGridMap(freshGrids);
                _selectedGridNames = _selectedGridNames
                    .Where(n => _gridsByName.ContainsKey(n))
                    .ToList();
                _rebuildContent?.Invoke("S2");
            };
            _event.Raise();
        }

        private static Dictionary<string, Autodesk.Revit.DB.Grid> BuildGridMap(
            IEnumerable<Autodesk.Revit.DB.Grid> grids) =>
            grids
                .Where(g => g.Curve is Line)
                .GroupBy(g => g.Name)
                .ToDictionary(g => g.Key, g => g.First());

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
            if (_preSelectedIds.Count > 0)
                return BuildS1_PreSelected();

            var outer = new StackPanel();

            int totalCats  = _categoryGroups.Values.Sum(g => g.Count);
            var countStrip = new TextBlock
            {
                Text         = $"{totalCats} categories · {_totalElements} elements in document",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 6),
            };
            countStrip.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            countStrip.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            countStrip.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            outer.Children.Add(countStrip);

            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(_categoryGroups);
            tabs.SelectionChanged += selected =>
            {
                _selectedCats = new List<string>(selected);
                OnValidationChanged();
            };
            outer.Children.Add(tabs);

            if (_activeViewId != null)
            {
                var toggle = new LemoineToggleSwitches();
                toggle.SetItems(new List<ToggleItem>
                {
                    new ToggleItem
                    {
                        Id        = "activeView",
                        Label     = "Active view elements only",
                        Desc      = "When on, only elements visible in the current Revit view will be split.",
                        DefaultOn = false,
                    },
                });
                toggle.StateChanged += state =>
                {
                    _useActiveView = state.TryGetValue("activeView", out bool v) && v;
                    OnValidationChanged();
                };
                outer.Children.Add(toggle);
            }

            return outer;
        }

        private FrameworkElement BuildS1_PreSelected()
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
            };
            card.SetResourceReference(Border.PaddingProperty,     "LemoineTh_CardPad");
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var header = new TextBlock { Text = "FROM CURRENT SELECTION", Margin = new Thickness(0, 0, 0, 4) };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            int cnt  = _preSelectedIds.Count;
            int cats = _selectedCats.Count;
            var countLine = new TextBlock
            {
                Text         = $"{cnt} element{(cnt == 1 ? "" : "s")} across {cats} categor{(cats == 1 ? "y" : "ies")}",
                FontWeight   = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap,
            };
            countLine.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            countLine.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            countLine.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var catLine = new TextBlock
            {
                Text         = string.Join("  ·  ", _selectedCats),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 2, 0, 6),
            };
            catLine.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            catLine.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            catLine.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var note = new TextBlock
            {
                Text         = "Close and reopen the tool to use category selection instead.",
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
            };
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var sp = new StackPanel();
            sp.Children.Add(header);
            sp.Children.Add(countLine);
            sp.Children.Add(catLine);
            sp.Children.Add(note);
            card.Child = sp;
            return card;
        }

        private FrameworkElement BuildS2()
        {
            if (_gridsByName.Count == 0)
            {
                var msg = new TextBlock
                {
                    Text         = "No linear grids found in the document. Only linear (non-arc) grids can be used as split planes.",
                    TextWrapping = TextWrapping.Wrap,
                };
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                return msg;
            }

            var groups = new Dictionary<string, List<string>>
            {
                { "Grids", _gridsByName.Keys.OrderBy(n => n).ToList() }
            };

            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(groups);
            tabs.SelectionChanged += selected =>
            {
                _selectedGridNames = new List<string>(selected);
                OnValidationChanged();
            };
            return tabs;
        }



        // ═════════════════════════════════════════════════════════════════════
        //  IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════
        // ── ILemoineReviewable (P3) — framework renders the review step ───────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("cats",  "Categories Selected"),
            ("grids", "Grids Selected"),
            ("op",    "Operation"),
            ("scope", "Scope"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["cats"]  = _selectedCats.Count == 0 ? "—" : string.Join(", ", _selectedCats),
            ["grids"] = _selectedGridNames.Count == 0 ? "—" : $"{_selectedGridNames.Count} grid(s)",
            ["op"]    = "Split elements at each selected grid plane",
            ["scope"] = _preSelectedIds.Count > 0 ? $"From selection ({_preSelectedIds.Count} elements)"
                : _useActiveView ? "Active view only"
                : "Entire document",
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => "Elements whose LocationCurve intersects a selected grid plane will " +
            "be split at that intersection point. Elements with no linear curve are skipped. Only linear (non-arc) " +
            "grids are supported as split planes.";
        public string?        ReviewWarning => null;

        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _preSelectedIds.Count > 0 || _selectedCats.Count > 0;
            if (stepId == "S2") return _selectedGridNames.Count > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
            {
                if (_preSelectedIds.Count > 0) return $"From selection ({_preSelectedIds.Count} elements)";
                return _selectedCats.Count == 0 ? "—" : string.Join(", ", _selectedCats);
            }
            if (stepId == "S2")
                return _selectedGridNames.Count == 0 ? "—" : $"{_selectedGridNames.Count} grid(s) selected";
            if (stepId == "S3")
                return "Ready to run";
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.PreSelectedIds        = _preSelectedIds.Count > 0 ? new List<ElementId>(_preSelectedIds) : null;
            _handler.ActiveViewId          = (_useActiveView && _activeViewId != null) ? _activeViewId : null;
            _handler.SelectedCategoryNames = new List<string>(_selectedCats);

            _handler.SelectedGridIds = _selectedGridNames
                .Where(n => _gridsByName.ContainsKey(n))
                .Select(n => _gridsByName[n].Id)
                .ToList();

            _handler.OnLog      = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;

            _event.Raise();
        }
    }
}
