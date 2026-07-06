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
    /// Push Coordinates to Links — commits an already-aligned link's position into the link FILE
    /// itself: opens it (detached, if workshared — the live central model is never touched or
    /// synced), moves its own Project Base Point and/or Survey Point to match, saves, then
    /// re-publishes coordinates and re-places the link instance so it self-corrects going forward.
    /// This is the companion to Align Coordinates, which only repositions the host's copy.
    /// </summary>
    public class PushCoordinatesToLinksViewModel : ILemoineTool, ILemoineReviewable, ILemoineToolCleanup
    {
        public string Title    => "Push Coordinates to Links";
        public string RunLabel => "Push in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("links",    "Links to Push",       required: true),
            new StepDefinition("settings", "Correction Settings",  required: true),
            new StepDefinition("run",      "Review & Run",         required: false),
        };

        private readonly PushCoordinatesToLinksRunHandler? _runHandler;
        private readonly ExternalEvent?                    _runEvent;
        private readonly PushCoordinatesData               _data;

        private readonly Dictionary<long, PushLinkSpec> _linkSpecs = new Dictionary<long, PushLinkSpec>();

        private bool _movePbp    = true;
        private bool _moveSurvey = true;
        private string _subfolder = "Coordinated Links";

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public PushCoordinatesToLinksViewModel(PushCoordinatesToLinksRunHandler? runHandler, ExternalEvent? runEvent, PushCoordinatesData? data)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _data       = data ?? new PushCoordinatesData();

            foreach (var l in _data.Links)
                _linkSpecs[l.LinkInstId] = new PushLinkSpec { LinkInstId = l.LinkInstId, LinkName = l.Name };
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
                case "links":    return BuildLinksStep();
                case "settings": return BuildSettingsStep();
                default:         return null;   // "run" is rendered by ILemoineReviewable
            }
        }

        // ── Step 1: links ────────────────────────────────────────────────────────
        private FrameworkElement BuildLinksStep()
        {
            var outer = new StackPanel();

            outer.Children.Add(Label($"Links ({_data.Links.Count} loaded)"));

            if (_data.Links.Count == 0)
            {
                outer.Children.Add(Dim("No loaded links found."));
                return outer;
            }

            var listBorder = new Border { BorderThickness = new Thickness(1) };
            listBorder.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorderMid");
            listBorder.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_MD");

            var list = new StackPanel();
            foreach (var info in _data.Links) list.Children.Add(BuildLinkRow(info));
            listBorder.Child = list;

            outer.Children.Add(listBorder);
            outer.Children.Add(Dim("Every loaded link is listed — uncheck a link to leave it out of this run."));

            return outer;
        }

        private FrameworkElement BuildLinkRow(PushLinkInfo info)
        {
            var spec = _linkSpecs[info.LinkInstId];

            var row = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(10, 8, 10, 8) };
            row.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var rowStack = new StackPanel { Orientation = Orientation.Horizontal };
            row.Child = rowStack;

            var cb = new CheckBox { IsChecked = spec.Selected, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            cb.Checked   += (s, e) => { spec.Selected = true;  Changed(); };
            cb.Unchecked += (s, e) => { spec.Selected = false; Changed(); };
            rowStack.Children.Add(cb);

            var name = new TextBlock { Text = info.Name, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            rowStack.Children.Add(name);

            return row;
        }

        // ── Step 2: correction settings ─────────────────────────────────────────
        private FrameworkElement BuildSettingsStep()
        {
            var outer = new StackPanel();

            outer.Children.Add(Label("Correct which point(s) in each link"));
            var toggles = new LemoineToggleSwitches { AccessibleName = "Points to correct" };
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "pbp",    Label = "Project Base Point", DefaultOn = _movePbp },
                new ToggleItem { Id = "survey", Label = "Survey Point",       DefaultOn = _moveSurvey },
            });
            toggles.StateChanged += st =>
            {
                _movePbp    = st.TryGetValue("pbp",    out var p) && p;
                _moveSurvey = st.TryGetValue("survey", out var s) && s;
                Changed();
            };
            outer.Children.Add(toggles);

            Divider(outer);

            var field = new LemoineTextField
            {
                Label = "Workshared-link subfolder name",
                Text  = _subfolder,
            };
            var preview = Dim(PreviewPath());
            field.TextChanged += t => { _subfolder = t ?? ""; preview.Text = PreviewPath(); Changed(); };
            outer.Children.Add(field);
            outer.Children.Add(preview);

            outer.Children.Add(Dim("A workshared link source is opened detached and saved as a new file in this subfolder — the live central model is never opened with worksharing enabled and never synced. A non-workshared source is corrected and saved in place."));

            return outer;
        }

        private string PreviewPath()
        {
            string sub = string.IsNullOrWhiteSpace(_subfolder) ? "Coordinated Links" : _subfolder.Trim();
            if (string.IsNullOrEmpty(_data.HostFolder)) return $"Will save workshared copies to: <host folder>\\{sub}\\";
            return $"Will save workshared copies to: {System.IO.Path.Combine(_data.HostFolder, sub)}\\";
        }

        // ── Review ──────────────────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("links", "Links"), ("points", "Points"), ("dest", "Workshared Copies"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["links"]  = LinksSummary(),
            ["points"] = PointsSummary(),
            ["dest"]   = string.IsNullOrWhiteSpace(_subfolder) ? "Coordinated Links" : _subfolder.Trim(),
        };

        public IList<string>? ReviewChips => null;
        public string? ReviewNote =>
            "Each selected link's own file is corrected and saved, then re-placed in the host using Shared Coordinates.";
        public string? ReviewWarning =>
            (!_movePbp && !_moveSurvey) ? "Pick at least one point to correct." : null;

        private string LinksSummary()
        {
            var selected = _linkSpecs.Values.Where(s => s.Selected).ToList();
            return selected.Count == 0 ? "None" : $"{selected.Count} link(s)";
        }

        private string PointsSummary()
        {
            if (_movePbp && _moveSurvey) return "Project Base + Survey";
            if (_movePbp)    return "Project Base Point";
            if (_moveSurvey) return "Survey Point";
            return "None";
        }

        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "links":    return _linkSpecs.Values.Any(s => s.Selected);
                case "settings": return _movePbp || _moveSurvey;
                default:         return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "links":    return LinksSummary() == "None" ? "—" : LinksSummary();
                case "settings": return $"{PointsSummary()} · subfolder \"{(string.IsNullOrWhiteSpace(_subfolder) ? "Coordinated Links" : _subfolder.Trim())}\"";
                case "run":      return "Ready to run";
                default:         return "—";
            }
        }

        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog("Run handler not registered.", "fail"); onComplete(0, 1, 0); return; }

            _runHandler.MovePbp       = _movePbp;
            _runHandler.MoveSurvey    = _moveSurvey;
            _runHandler.SubfolderName = string.IsNullOrWhiteSpace(_subfolder) ? "Coordinated Links" : _subfolder.Trim();
            _runHandler.HostFolder    = _data.HostFolder;
            _runHandler.LinkSpecs     = _linkSpecs.Values.Select(s => new PushLinkSpec
            {
                LinkInstId = s.LinkInstId,
                LinkName   = s.LinkName,
                Selected   = s.Selected,
            }).ToList();
            _runHandler.PushLog    = pushLog;
            _runHandler.OnProgress = onProgress;
            _runHandler.OnComplete = onComplete;

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
