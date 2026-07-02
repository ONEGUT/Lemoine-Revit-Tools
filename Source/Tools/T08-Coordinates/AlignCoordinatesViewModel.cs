using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Coordinates
{
    /// <summary>
    /// Align Coordinates — move the host Survey Point and/or Project Base Point to a picked
    /// grid intersection + level, then rotate/translate every selected link so its same-named
    /// grid intersection coincides, and publish the host's shared coordinates to those links.
    /// </summary>
    public class AlignCoordinatesViewModel : ILemoineTool, ILemoineReviewable, ILemoineToolCleanup, IStepAware
    {
        public string Title    => "Align Coordinates";
        public string RunLabel => "Align in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("host",  "Host Reference",  required: true),
            new StepDefinition("links", "Links to Align",  required: false),
            new StepDefinition("run",   "Review & Run",    required: false),
        };

        private readonly AlignCoordinatesRunHandler? _runHandler;
        private readonly ExternalEvent?              _runEvent;
        private readonly AlignCoordinatesData        _data;

        // selection state
        private string? _grid1;
        private string? _grid2;
        private string? _level;
        private bool _moveSurvey = true;
        private bool _movePbp    = true;
        private bool _rotate     = true;
        private bool _publish    = true;
        private readonly Dictionary<string, long> _linkByName = new Dictionary<string, long>();
        private HashSet<long> _selectedLinkIds = new HashSet<long>();

        private StackPanel? _linksContainer;
        private Action<string>? _refresh;

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public AlignCoordinatesViewModel(AlignCoordinatesRunHandler? runHandler, ExternalEvent? runEvent, AlignCoordinatesData? data)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _data       = data ?? new AlignCoordinatesData();

            if (_data.HostGridNames.Count >= 1) _grid1 = _data.HostGridNames[0];
            if (_data.HostGridNames.Count >= 2) _grid2 = _data.HostGridNames[1];
            if (_data.HostLevelNames.Count >= 1) _level = _data.HostLevelNames[0];
        }

        // ── IStepAware: rebuild the link list when the step opens (matches the chosen grids) ──
        public void SetContentRefreshCallback(Action<string> rebuild) => _refresh = rebuild;
        public void OnStepActivated(string stepId)
        {
            if (stepId == "links") RebuildLinks();
        }

        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog    = null;
            _runHandler.OnProgress = null;
            _runHandler.OnComplete = null;
        }

        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "host":  return BuildHostStep();
                case "links": return BuildLinksStep();
                default:      return null;   // "run" is rendered by ILemoineReviewable
            }
        }

        // ── Step 1: host reference ────────────────────────────────────────────────
        private FrameworkElement BuildHostStep()
        {
            var outer = new StackPanel();
            if (_data.HostGridNames.Count < 2)
            {
                outer.Children.Add(Dim("This document needs at least two grids to define an intersection."));
                return outer;
            }

            outer.Children.Add(Label("Grid 1"));
            outer.Children.Add(GridSelect(_grid1, v => { _grid1 = v; Changed(); }));

            outer.Children.Add(Label("Grid 2"));
            outer.Children.Add(GridSelect(_grid2, v => { _grid2 = v; Changed(); }));

            outer.Children.Add(Label("Level"));
            var levels = new LemoineSingleSelect
            {
                Items        = _data.HostLevelNames,
                SelectedItem = _level ?? _data.HostLevelNames.FirstOrDefault(),
                AccessibleName = "Level",
            };
            levels.SelectionChanged += v => { _level = v; Changed(); };
            outer.Children.Add(levels);

            Divider(outer);
            outer.Children.Add(Label("Move which host point(s)"));
            var toggles = new LemoineToggleSwitches { AccessibleName = "Host points to move" };
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "survey", Label = "Survey Point",       DefaultOn = _moveSurvey },
                new ToggleItem { Id = "pbp",    Label = "Project Base Point", DefaultOn = _movePbp },
            });
            toggles.StateChanged += st =>
            {
                _moveSurvey = st.TryGetValue("survey", out var s) && s;
                _movePbp    = st.TryGetValue("pbp",    out var p) && p;
                Changed();
            };
            outer.Children.Add(toggles);

            outer.Children.Add(Dim("Moves the chosen point(s) to the grid intersection at the level's elevation."));
            return outer;
        }

        private LemoineSingleSelect GridSelect(string? selected, Action<string?> onChange)
        {
            var sel = new LemoineSingleSelect
            {
                Items          = _data.HostGridNames,
                SelectedItem   = selected ?? _data.HostGridNames.FirstOrDefault(),
                AccessibleName = "Grid",
            };
            sel.SelectionChanged += onChange;
            return sel;
        }

        // ── Step 2: links ─────────────────────────────────────────────────────────
        private FrameworkElement BuildLinksStep()
        {
            var outer = new StackPanel();
            _linksContainer = new StackPanel();
            outer.Children.Add(_linksContainer);

            Divider(outer);
            var toggles = new LemoineToggleSwitches { AccessibleName = "Alignment options" };
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "rotate",  Label = "Rotate to align orientation",
                    Desc = "Turn each link so its grid lines run parallel to the host's.", DefaultOn = _rotate },
                new ToggleItem { Id = "publish", Label = "Publish shared coordinates",
                    Desc = "Write the host coordinate system into each aligned link (saved with the host).", DefaultOn = _publish },
            });
            toggles.StateChanged += st =>
            {
                _rotate  = st.TryGetValue("rotate",  out var r) && r;
                _publish = st.TryGetValue("publish", out var p) && p;
                Changed();
            };
            outer.Children.Add(toggles);

            RebuildLinks();
            return outer;
        }

        private void RebuildLinks()
        {
            if (_linksContainer == null) return;
            _linksContainer.Children.Clear();
            _linkByName.Clear();

            var matched = _data.Links
                .Where(l => _grid1 != null && _grid2 != null
                            && l.GridNames.Contains(_grid1) && l.GridNames.Contains(_grid2))
                .ToList();

            int unmatched = _data.Links.Count - matched.Count;

            if (matched.Count == 0)
            {
                _linksContainer.Children.Add(Dim(_data.Links.Count == 0
                    ? "No loaded links contain grids."
                    : $"No loaded link contains both grid '{_grid1}' and '{_grid2}'."));
                _selectedLinkIds.Clear();
                return;
            }

            _linksContainer.Children.Add(Label("Links containing both grids"));
            foreach (var l in matched) _linkByName[l.Name] = l.LinkInstId;

            var tabs = new LemoineMultiSelectTabs();
            tabs.SelectionChanged += sel =>
            {
                _selectedLinkIds = new HashSet<long>(
                    sel.Where(_linkByName.ContainsKey).Select(n => _linkByName[n]));
                Changed();
            };
            var all = matched.Select(l => l.Name).ToList();
            tabs.SetGroups(new Dictionary<string, List<string>> { { "Links", all } }, all);  // default: all
            _linksContainer.Children.Add(tabs);

            if (unmatched > 0)
                _linksContainer.Children.Add(Dim($"{unmatched} link(s) are missing one of the grids and were hidden."));
        }

        // ── Review ──────────────────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("ref", "Host Reference"), ("points", "Host Points"), ("links", "Links"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["ref"]    = (_grid1 != null && _grid2 != null) ? $"{_grid1} × {_grid2} @ {_level ?? "—"}" : "—",
            ["points"] = PointsSummary(),
            ["links"]  = _selectedLinkIds.Count == 0 ? "None" : $"{_selectedLinkIds.Count} link(s)",
        };

        public IList<string>? ReviewChips => new[]
        {
            _rotate  ? "rotate ✓"  : "rotate ✗",
            _publish ? "publish ✓" : "publish ✗",
        };
        public string? ReviewNote    => "Links are repositioned, then host shared coordinates are published to them.";
        public string? ReviewWarning => (_grid1 != null && _grid1 == _grid2)
            ? "Grid 1 and Grid 2 are the same — pick two different grids." : null;

        private string PointsSummary()
        {
            if (_moveSurvey && _movePbp) return "Survey + Project Base";
            if (_moveSurvey) return "Survey Point";
            if (_movePbp)    return "Project Base Point";
            return "None";
        }

        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "host":
                    return _grid1 != null && _grid2 != null && _grid1 != _grid2
                           && !string.IsNullOrEmpty(_level) && (_moveSurvey || _movePbp);
                case "links":
                    return _selectedLinkIds.Count > 0;
                default:
                    return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "host":
                    return (_grid1 != null && _grid2 != null)
                        ? $"{_grid1} × {_grid2} @ {_level ?? "—"} · {PointsSummary()}" : "—";
                case "links":
                    return _selectedLinkIds.Count == 0 ? "—"
                        : $"{_selectedLinkIds.Count} link(s) · rotate {(_rotate ? "✓" : "✗")} · publish {(_publish ? "✓" : "✗")}";
                case "run": return "Ready to run";
                default:    return "—";
            }
        }

        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog("Run handler not registered.", "fail"); onComplete(0, 1, 0); return; }

            _runHandler.Grid1Name   = _grid1 ?? "";
            _runHandler.Grid2Name   = _grid2 ?? "";
            _runHandler.LevelName   = _level ?? "";
            _runHandler.MoveSurvey  = _moveSurvey;
            _runHandler.MovePbp     = _movePbp;
            _runHandler.Rotate      = _rotate;
            _runHandler.Publish     = _publish;
            _runHandler.LinkInstIds = _selectedLinkIds.ToList();
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
