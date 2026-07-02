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
    /// Compare Grids Across Links — a read-only audit of grid lines across the host + selected
    /// links once they are aligned/share coordinates, flagging grids that are missing, offset,
    /// rotated, or present in only one file.
    /// </summary>
    public class CompareGridsViewModel : ILemoineTool, ILemoineReviewable, ILemoineToolCleanup
    {
        public string Title    => "Compare Grids Across Links";
        public string RunLabel => "Compare Grids →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("files", "Files & Tolerances", required: true),
            new StepDefinition("run",   "Review & Run",       required: false),
        };

        private readonly CompareGridsRunHandler? _runHandler;
        private readonly ExternalEvent?          _runEvent;
        private readonly CompareGridsData        _data;

        private readonly Dictionary<string, long> _fileByName = new Dictionary<string, long>();
        private HashSet<long> _selectedLinkIds = new HashSet<long>();
        private bool   _includeHost = true;
        private double _posTolInches = 0.0625;
        private double _angleTolDeg  = 0.10;

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public CompareGridsViewModel(CompareGridsRunHandler? runHandler, ExternalEvent? runEvent, CompareGridsData? data)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _data       = data ?? new CompareGridsData();
            // default: every loaded link selected
            _selectedLinkIds = new HashSet<long>(_data.Files.Where(f => !f.IsHost).Select(f => f.LinkInstId));
        }

        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog    = null;
            _runHandler.OnProgress = null;
            _runHandler.OnComplete = null;
        }

        public FrameworkElement? GetStepContent(string stepId)
            => stepId == "files" ? BuildFilesStep() : null;   // "run" rendered by ILemoineReviewable

        private FrameworkElement BuildFilesStep()
        {
            var outer = new StackPanel();

            var links = _data.Files.Where(f => !f.IsHost).ToList();
            if (links.Count == 0)
            {
                outer.Children.Add(Dim("No loaded links found — nothing to compare against the host."));
                return outer;
            }

            outer.Children.Add(Label("Links to include"));
            _fileByName.Clear();
            foreach (var f in links) _fileByName[f.Name] = f.LinkInstId;

            var tabs = new LemoineMultiSelectTabs();
            tabs.SelectionChanged += sel =>
            {
                _selectedLinkIds = new HashSet<long>(sel.Where(_fileByName.ContainsKey).Select(n => _fileByName[n]));
                Changed();
            };
            var all = links.Select(f => f.Name).ToList();
            tabs.SetGroups(new Dictionary<string, List<string>> { { "Links", all } }, all);
            outer.Children.Add(tabs);

            var hostToggle = new LemoineToggleSwitches { AccessibleName = "Include host" };
            hostToggle.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "host", Label = "Include host grids", DefaultOn = _includeHost },
            });
            hostToggle.StateChanged += st => { _includeHost = st.TryGetValue("host", out var h) && h; Changed(); };
            outer.Children.Add(hostToggle);

            Divider(outer);
            outer.Children.Add(Label("Tolerances"));
            outer.Children.Add(TolRow("Lateral offset (inches)", _posTolInches, 3, 0.0, 120.0, 0.0625,
                v => { _posTolInches = v; Changed(); }));
            outer.Children.Add(TolRow("Angle (degrees)", _angleTolDeg, 2, 0.0, 45.0, 0.05,
                v => { _angleTolDeg = v; Changed(); }));

            outer.Children.Add(Dim("Grids are compared in a common world frame. Meaningful once files share coordinates."));
            return outer;
        }

        private FrameworkElement TolRow(string label, double value, int decimals, double min, double max, double step, Action<double> onChange)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var tb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            DockPanel.SetDock(tb, Dock.Left);

            var stepper = new LemoineInlineStepper
            {
                Value = value, MinValue = min, MaxValue = max, Step = step, Decimals = decimals, ValueWidth = 52,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            stepper.ValueChanged += (s, v) => onChange(v);

            dp.Children.Add(tb);
            dp.Children.Add(stepper);
            return dp;
        }

        // ── Review ──────────────────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("files", "Files"), ("tol", "Tolerances"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["files"] = $"{(_includeHost ? "Host + " : "")}{_selectedLinkIds.Count} link(s)",
            ["tol"]   = $"{_posTolInches:0.###}\" · {_angleTolDeg:0.##}°",
        };

        public IList<string>? ReviewChips => null;
        public string?        ReviewNote    => "Read-only — reports grid differences, makes no changes.";
        public string?        ReviewWarning => null;

        public bool IsValid(string stepId)
        {
            if (stepId != "files") return true;
            int fileCount = _selectedLinkIds.Count + (_includeHost ? 1 : 0);
            return fileCount >= 2;
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "files":
                    return $"{(_includeHost ? "Host + " : "")}{_selectedLinkIds.Count} link(s) · {_posTolInches:0.###}\" / {_angleTolDeg:0.##}°";
                case "run": return "Ready to run";
                default:    return "—";
            }
        }

        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog("Run handler not registered.", "fail"); onComplete(0, 1, 0); return; }

            _runHandler.FileLinkInstIds = _selectedLinkIds.ToList();
            _runHandler.IncludeHost     = _includeHost;
            _runHandler.PosTolInches    = _posTolInches;
            _runHandler.AngleTolDegrees = _angleTolDeg;
            _runHandler.PushLog         = pushLog;
            _runHandler.OnProgress      = onProgress;
            _runHandler.OnComplete      = onComplete;

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
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) };
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
