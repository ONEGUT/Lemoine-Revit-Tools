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
    public class SplitByReferencePlaneViewModel : ILemoineTool
    {
        public string Title    => "Split Elements by Reference Plane";
        public string RunLabel => "Split in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Select Categories",       required: true),
            new StepDefinition("S2", "Select Reference Planes", required: true),
            new StepDefinition("S3", "Review & Run",            required: false),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private List<string> _selectedCats     = new List<string>();
        private List<string> _selectedRefNames = new List<string>();
        private bool         _useActiveView    = false;

        private readonly Dictionary<string, ReferencePlane>          _refPlanesByName;
        private readonly IReadOnlyDictionary<string, int>            _elementCounts;
        private readonly ElementId?                                  _activeViewId;
        private readonly IReadOnlyList<ElementId>                    _preSelectedIds;
        private readonly IReadOnlyList<string>                       _preSelectedCats;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly SplitByReferencePlaneEventHandler _handler;
        private readonly ExternalEvent                     _event;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public SplitByReferencePlaneViewModel(
            SplitByReferencePlaneEventHandler    handler,
            ExternalEvent                        externalEvent,
            IEnumerable<ReferencePlane>          allRefPlanes,
            IReadOnlyDictionary<string, int>     elementCounts,
            ElementId?                           activeViewId,
            IReadOnlyList<ElementId>             preSelectedIds,
            IReadOnlyList<string>                preSelectedCats)
        {
            _handler         = handler;
            _event           = externalEvent;
            _elementCounts   = elementCounts;
            _activeViewId    = activeViewId;
            _preSelectedIds  = preSelectedIds;
            _preSelectedCats = preSelectedCats;

            _refPlanesByName = allRefPlanes
                .GroupBy(r => RefPlaneName(r))
                .ToDictionary(g => g.Key, g => g.First());

            if (_preSelectedIds.Count > 0)
                _selectedCats = new List<string>(_preSelectedCats);
        }

        private static string RefPlaneName(ReferencePlane rp) =>
            string.IsNullOrWhiteSpace(rp.Name)
                ? $"Ref Plane {rp.Id.IntegerValue}"
                : rp.Name;

        // ═════════════════════════════════════════════════════════════════════
        //  GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return BuildReview();
                default:   return null;
            }
        }

        private FrameworkElement BuildS1()
        {
            if (_preSelectedIds.Count > 0)
                return BuildS1_PreSelected();

            var outer = new StackPanel();

            // A — element count strip (reuses GridSplitCategories — same supported set)
            var parts = SplitElementsShared.GridSplitCategories
                .Select(c => $"{c.Label}: {(_elementCounts.TryGetValue(c.Label, out int n) ? n : 0)}");
            var countStrip = new TextBlock
            {
                Text         = string.Join("  ·  ", parts),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 6),
            };
            countStrip.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            countStrip.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            countStrip.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            outer.Children.Add(countStrip);

            // Category picker
            var groups = new Dictionary<string, List<string>>
            {
                { "Categories", SplitElementsShared.GridSplitCategories.Select(c => c.Label).ToList() }
            };
            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(groups);
            tabs.SelectionChanged += selected =>
            {
                _selectedCats = new List<string>(selected);
                OnValidationChanged();
            };
            outer.Children.Add(tabs);

            // B — active view filter
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
            if (_refPlanesByName.Count == 0)
            {
                var msg = new TextBlock
                {
                    Text         = "No reference planes found in the document.",
                    TextWrapping = TextWrapping.Wrap,
                };
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                return msg;
            }

            var groups = new Dictionary<string, List<string>>
            {
                { "Reference Planes", _refPlanesByName.Keys.OrderBy(n => n).ToList() }
            };

            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(groups);
            tabs.SelectionChanged += selected =>
            {
                _selectedRefNames = new List<string>(selected);
                OnValidationChanged();
            };
            return tabs;
        }

        private FrameworkElement BuildReview()
        {
            var outer = new StackPanel();
            var grid  = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddCard(grid, "Categories Selected",
                () => _selectedCats.Count == 0 ? "—" : string.Join(", ", _selectedCats),
                0, 0);
            AddCard(grid, "Reference Planes",
                () => _selectedRefNames.Count == 0 ? "—" : $"{_selectedRefNames.Count} plane(s)",
                0, 1);
            AddCard(grid, "Operation",
                () => "Split elements at each reference plane",
                1, 0);
            AddCard(grid, "Scope",
                () => _preSelectedIds.Count > 0 ? $"From selection ({_preSelectedIds.Count} elements)"
                    : _useActiveView ? "Active view only"
                    : "Entire document",
                1, 1);

            outer.Children.Add(grid);

            var note = new TextBlock
            {
                Text         = "Elements whose LocationCurve intersects a reference plane will be split at that intersection. " +
                               "The reference plane's own normal vector is used as the cutting direction.",
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 8, 0, 0),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            outer.Children.Add(note);

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
            card.SetResourceReference(Border.PaddingProperty,     "LemoineTh_CardPad");
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
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _preSelectedIds.Count > 0 || _selectedCats.Count > 0;
            if (stepId == "S2") return _selectedRefNames.Count > 0;
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
                return _selectedRefNames.Count == 0 ? "—" : $"{_selectedRefNames.Count} plane(s) selected";
            if (stepId == "S3")
                return "Ready to run";
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.PreSelectedIds = _preSelectedIds.Count > 0 ? new List<ElementId>(_preSelectedIds) : null;
            _handler.ActiveViewId   = (_useActiveView && _activeViewId != null) ? _activeViewId : null;

            if (_preSelectedIds.Count == 0)
            {
                var selectedBics = new HashSet<string>(_selectedCats);
                _handler.SelectedBics = SplitElementsShared.GridSplitCategories
                    .Where(c => selectedBics.Contains(c.Label))
                    .Select(c => c.Cat)
                    .ToList();
            }

            _handler.SelectedRefPlaneIds = _selectedRefNames
                .Where(n => _refPlanesByName.ContainsKey(n))
                .Select(n => _refPlanesByName[n].Id)
                .ToList();

            _handler.OnLog      = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;

            _event.Raise();
        }
    }
}
