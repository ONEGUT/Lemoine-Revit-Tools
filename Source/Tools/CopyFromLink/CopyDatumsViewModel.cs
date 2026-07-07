using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

namespace LemoineTools.Tools.CopyFromLink
{
    /// <summary>
    /// Copy Datums — pulls grids and levels out of a chosen linked model into the host (link
    /// transform applied). A grid or level whose name already exists in the host is hidden from
    /// the list and skipped at run time, because Revit enforces unique names for both.
    /// </summary>
    public class CopyDatumsViewModel : IStepFlowTool, IReviewableTool, IToolCleanup
    {
        public string Title    => AppStrings.T("copy.datums.title");
        public string RunLabel => AppStrings.T("copy.datums.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("source", AppStrings.T("copy.datums.steps.source"), required: true),
            new StepDefinition("run",    AppStrings.T("copy.datums.steps.run"),        required: false),
        };

        private readonly CopyDatumsRunHandler? _runHandler;
        private readonly ExternalEvent?        _runEvent;
        private readonly List<CopyDatumLinkInfo> _links;
        private readonly Dictionary<string, long> _linkByName = new Dictionary<string, long>();

        private long _linkId;
        // Display name → element id, kept separate per kind. A level whose name collides with a
        // grid's name in the same link is suffixed " (Level)" in the tab list — the underlying
        // MultiSelectTabs tracks selection as one flat set of display strings across every
        // group tab, so two different elements can never safely share one display string.
        private readonly Dictionary<string, long> _gridDisplayToId  = new Dictionary<string, long>();
        private readonly Dictionary<string, long> _levelDisplayToId = new Dictionary<string, long>();
        private HashSet<long> _selectedGridIds  = new HashSet<long>();
        private HashSet<long> _selectedLevelIds = new HashSet<long>();

        private StackPanel? _datumContainer;

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

        public CopyDatumsViewModel(CopyDatumsRunHandler? runHandler, ExternalEvent? runEvent, List<CopyDatumLinkInfo>? links)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _links      = (links ?? new List<CopyDatumLinkInfo>())
                          .Where(l => l.Grids.Count > 0 || l.Levels.Count > 0).ToList();
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
                outer.Children.Add(Dim(AppStrings.T("copy.datums.labels.noLinks")));
                return outer;
            }

            outer.Children.Add(Label(AppStrings.T("copy.datums.labels.sourceLink")));
            var linkSelect = new SingleSelect
            {
                Items        = _links.Select(l => l.Name).ToList(),
                SelectedItem = _links.FirstOrDefault(l => l.LinkInstId == _linkId)?.Name ?? _links[0].Name,
            };
            linkSelect.SelectionChanged += name =>
            {
                if (name != null && _linkByName.TryGetValue(name, out var id)) _linkId = id;
                _selectedGridIds.Clear();
                _selectedLevelIds.Clear();
                RebuildDatums();
                Changed();
            };
            outer.Children.Add(linkSelect);

            Divider(outer);
            outer.Children.Add(Label(AppStrings.T("copy.datums.labels.datumsToCopy")));
            _datumContainer = new StackPanel();
            outer.Children.Add(_datumContainer);
            RebuildDatums();

            return outer;
        }

        private void RebuildDatums()
        {
            if (_datumContainer == null) return;
            _datumContainer.Children.Clear();

            var link = _links.FirstOrDefault(l => l.LinkInstId == _linkId);
            var grids  = link?.Grids  ?? new List<CopyDatumItem>();
            var levels = link?.Levels ?? new List<CopyDatumItem>();
            var copyableGrids  = grids.Where(g => !g.ExistsInHost).ToList();
            var copyableLevels = levels.Where(l => !l.ExistsInHost).ToList();
            int existing = (grids.Count - copyableGrids.Count) + (levels.Count - copyableLevels.Count);

            if (copyableGrids.Count == 0 && copyableLevels.Count == 0)
            {
                _datumContainer.Children.Add(Dim(existing > 0
                    ? AppStrings.T("copy.datums.labels.allExist", existing)
                    : AppStrings.T("copy.datums.labels.noDatums")));
                return;
            }

            _gridDisplayToId.Clear();
            _levelDisplayToId.Clear();

            var gridNames = new HashSet<string>(copyableGrids.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var g in copyableGrids) _gridDisplayToId[g.Name] = g.ElemId;
            foreach (var lvl in copyableLevels)
            {
                string display = gridNames.Contains(lvl.Name)
                    ? AppStrings.T("copy.datums.labels.levelNameCollision", lvl.Name)
                    : lvl.Name;
                _levelDisplayToId[display] = lvl.ElemId;
            }

            var tabs = new MultiSelectTabs();
            tabs.SelectionChanged += sel =>
            {
                _selectedGridIds  = new HashSet<long>(sel.Where(_gridDisplayToId.ContainsKey).Select(n => _gridDisplayToId[n]));
                _selectedLevelIds = new HashSet<long>(sel.Where(_levelDisplayToId.ContainsKey).Select(n => _levelDisplayToId[n]));
                Changed();
            };

            var groups = new Dictionary<string, List<string>>();
            var allDisplay = new List<string>();
            if (_gridDisplayToId.Count > 0)
            {
                var names = _gridDisplayToId.Keys.ToList();
                groups[AppStrings.T("copy.datums.labels.gridsTab")] = names;
                allDisplay.AddRange(names);
            }
            if (_levelDisplayToId.Count > 0)
            {
                var names = _levelDisplayToId.Keys.ToList();
                groups[AppStrings.T("copy.datums.labels.levelsTab")] = names;
                allDisplay.AddRange(names);
            }
            tabs.SetGroups(groups, allDisplay);  // default: copy all
            _datumContainer.Children.Add(tabs);

            if (existing > 0)
                _datumContainer.Children.Add(Dim(AppStrings.T("copy.datums.labels.someHidden", existing)));
        }

        // ── Review ──────────────────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("link", AppStrings.T("copy.datums.review.itemLink")), ("datums", AppStrings.T("copy.datums.review.itemDatums")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["link"]   = _links.FirstOrDefault(l => l.LinkInstId == _linkId)?.Name ?? "—",
            ["datums"] = _selectedGridIds.Count == 0 && _selectedLevelIds.Count == 0
                ? AppStrings.T("copy.datums.review.datumsNone")
                : AppStrings.T("copy.datums.review.datumsValue", _selectedGridIds.Count, _selectedLevelIds.Count),
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => null;
        public string?        ReviewWarning => null;

        public bool IsValid(string stepId) => stepId != "source" || _selectedGridIds.Count > 0 || _selectedLevelIds.Count > 0;

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "source":
                    int total = _selectedGridIds.Count + _selectedLevelIds.Count;
                    return total == 0 ? "—"
                        : AppStrings.T("copy.datums.summaries.source", (_links.FirstOrDefault(l => l.LinkInstId == _linkId)?.Name ?? "link"), total);
                case "run": return AppStrings.T("copy.datums.summaries.run");
                default:    return "—";
            }
        }

        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog(AppStrings.T("copy.datums.log.handlerMissing"), "fail"); onComplete(0, 1, 0); return; }

            _runHandler.LinkInstId   = _linkId;
            _runHandler.GridElemIds  = _selectedGridIds.ToList();
            _runHandler.LevelElemIds = _selectedLevelIds.ToList();
            _runHandler.PushLog      = pushLog;
            _runHandler.OnProgress   = onProgress;
            _runHandler.OnComplete   = onComplete;

            pushLog(AppStrings.T("copy.datums.log.raising"), "info");
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
