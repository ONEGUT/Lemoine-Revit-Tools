using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.CopyLinear
{
    /// <summary>
    /// Copy Grids — pulls grids out of a chosen linked model into the host (link transform applied).
    /// Grids whose name already exists in the host are hidden from the list and skipped at run time,
    /// because Revit enforces unique grid names.
    /// </summary>
    public class CopyGridsViewModel : ILemoineTool, ILemoineReviewable, ILemoineToolCleanup
    {
        public string Title    => "Copy Grids from Link";
        public string RunLabel => "Copy in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("source", "Source Link & Grids", required: true),
            new StepDefinition("run",    "Review & Run",        required: false),
        };

        private readonly CopyGridsRunHandler? _runHandler;
        private readonly ExternalEvent?       _runEvent;
        private readonly List<CopyGridLinkInfo> _links;
        private readonly Dictionary<string, long> _linkByName = new Dictionary<string, long>();

        private long _linkId;
        private readonly Dictionary<string, long> _gridDisplayToId = new Dictionary<string, long>();
        private HashSet<long> _selectedGridIds = new HashSet<long>();

        private StackPanel? _gridContainer;

        public event EventHandler? ValidationChanged;

        // Null the callbacks parked on the static handler so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog    = null;
            _runHandler.OnProgress = null;
            _runHandler.OnComplete = null;
        }

        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public CopyGridsViewModel(CopyGridsRunHandler? runHandler, ExternalEvent? runEvent, List<CopyGridLinkInfo>? links)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _links      = (links ?? new List<CopyGridLinkInfo>()).Where(l => l.Grids.Count > 0).ToList();
            foreach (var l in _links) _linkByName[l.Name] = l.LinkInstId;
            if (_links.Count > 0) _linkId = _links[0].LinkInstId;
        }

        public FrameworkElement? GetStepContent(string stepId)
            => stepId == "source" ? BuildSourceStep() : null;

        private FrameworkElement BuildSourceStep()
        {
            var outer = new StackPanel();
            if (_links.Count == 0)
            {
                outer.Children.Add(Dim("No loaded links contain grids."));
                return outer;
            }

            outer.Children.Add(Label("Source link"));
            var linkSelect = new LemoineSingleSelect
            {
                Items        = _links.Select(l => l.Name).ToList(),
                SelectedItem = _links.FirstOrDefault(l => l.LinkInstId == _linkId)?.Name ?? _links[0].Name,
            };
            linkSelect.SelectionChanged += name =>
            {
                if (name != null && _linkByName.TryGetValue(name, out var id)) _linkId = id;
                _selectedGridIds.Clear();
                RebuildGrids();
                Changed();
            };
            outer.Children.Add(linkSelect);

            Divider(outer);
            outer.Children.Add(Label("Grids to copy"));
            _gridContainer = new StackPanel();
            outer.Children.Add(_gridContainer);
            RebuildGrids();

            return outer;
        }

        private void RebuildGrids()
        {
            if (_gridContainer == null) return;
            _gridContainer.Children.Clear();

            var link = _links.FirstOrDefault(l => l.LinkInstId == _linkId);
            var grids = link?.Grids ?? new List<CopyGridItem>();
            var copyable = grids.Where(g => !g.ExistsInHost).ToList();
            int existing = grids.Count - copyable.Count;

            if (copyable.Count == 0)
            {
                _gridContainer.Children.Add(Dim(existing > 0
                    ? $"All {existing} grid(s) in this link already exist in the host."
                    : "This link has no grids."));
                return;
            }

            _gridDisplayToId.Clear();
            foreach (var g in copyable) _gridDisplayToId[g.Name] = g.ElemId;

            var tabs = new LemoineMultiSelectTabs();
            tabs.SelectionChanged += sel =>
            {
                _selectedGridIds = new HashSet<long>(
                    sel.Where(_gridDisplayToId.ContainsKey).Select(n => _gridDisplayToId[n]));
                Changed();
            };
            var all = copyable.Select(g => g.Name).ToList();
            tabs.SetGroups(new Dictionary<string, List<string>> { { "Grids", all } }, all);  // default: copy all
            _gridContainer.Children.Add(tabs);

            if (existing > 0)
                _gridContainer.Children.Add(Dim($"{existing} grid(s) already exist in the host and were hidden."));
        }

        // ── Review ──────────────────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("link", "Source Link"), ("grids", "Grids"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["link"]  = _links.FirstOrDefault(l => l.LinkInstId == _linkId)?.Name ?? "—",
            ["grids"] = _selectedGridIds.Count == 0 ? "None" : $"{_selectedGridIds.Count} grid(s)",
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => null;
        public string?        ReviewWarning => null;

        public bool IsValid(string stepId) => stepId != "source" || _selectedGridIds.Count > 0;

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "source":
                    return _selectedGridIds.Count == 0 ? "—"
                        : $"{(_links.FirstOrDefault(l => l.LinkInstId == _linkId)?.Name ?? "link")} · {_selectedGridIds.Count} grid(s)";
                case "run": return "Ready to run";
                default:    return "—";
            }
        }

        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog("Run handler not registered.", "fail"); onComplete(0, 1, 0); return; }

            _runHandler.LinkInstId  = _linkId;
            _runHandler.GridElemIds = _selectedGridIds.ToList();
            _runHandler.PushLog     = pushLog;
            _runHandler.OnProgress  = onProgress;
            _runHandler.OnComplete  = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
            _runEvent.Raise();
        }

        // ── helpers ──────────────────────────────────────────────────────────────
        private static TextBlock Label(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock Dim(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static void Divider(StackPanel parent)
        {
            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            parent.Children.Add(sep);
        }
    }
}
