using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// WebView2 (IWebTool) version of Push Coordinates to Links - the first real tool migrated
    /// off WPF (Phase 3, wave 1). Same domain behaviour and the SAME ExternalEvent handler +
    /// PushCoordinatesData as <see cref="PushCoordinatesToLinksViewModel"/>; only the view layer
    /// differs (a spec instead of WPF panels). The WPF version is kept in parallel until this is
    /// verified on Windows (rule R25), after which the production command flips to this one.
    /// </summary>
    public class PushCoordinatesWebTool : IWebTool, IWebToolCleanup
    {
        private readonly PushCoordinatesToLinksRunHandler? _runHandler;
        private readonly ExternalEvent?                    _runEvent;
        private readonly PushCoordinatesData               _data;
        private readonly Dictionary<long, PushLinkSpec>    _linkSpecs = new Dictionary<long, PushLinkSpec>();

        private bool _movePbp    = true;
        private bool _moveSurvey = true;

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public PushCoordinatesWebTool(PushCoordinatesToLinksRunHandler? runHandler, ExternalEvent? runEvent, PushCoordinatesData? data)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _data       = data ?? new PushCoordinatesData();
            foreach (var l in _data.Links)
                _linkSpecs[l.LinkInstId] = new PushLinkSpec { LinkInstId = l.LinkInstId, LinkName = l.Name };
        }

        public string Title    => "Push Coordinates to Links";
        public string RunLabel => "Push in Revit →";

        public IReadOnlyList<WebStep> BuildSteps()
        {
            var links = new WebStep("links", "Links to Push").Add(
                WebInput.CheckList("links", $"Links ({_data.Links.Count} loaded)",
                    _data.Links.Select(l => (l.LinkInstId.ToString(), l.Name, _linkSpecs[l.LinkInstId].Selected))));

            var settings = new WebStep("settings", "Correction Settings")
                .Add(WebInput.Toggle("pbp",    "Project Base Point", _movePbp))
                .Add(WebInput.Toggle("survey", "Survey Point",       _moveSurvey));

            var run = new WebStep("run", "Review & Run", required: false).Add(
                WebInput.Review("review",
                    new[] { ("Links", LinksSummary()), ("Points", PointsSummary()) },
                    note: "Each selected link's own file is corrected and saved, then re-placed in the host using Shared Coordinates.",
                    warning: (!_movePbp && !_moveSurvey) ? "Pick at least one point to correct." : null));

            return new List<WebStep> { links, settings, run };
        }

        public void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "links":
                    if (value is IEnumerable ids && !(value is string))
                    {
                        var set = new HashSet<string>(ids.Cast<object?>()
                            .Select(o => o?.ToString()).Where(s => s != null).Select(s => s!));
                        foreach (var spec in _linkSpecs.Values)
                            spec.Selected = set.Contains(spec.LinkInstId.ToString());
                        Changed();
                    }
                    break;
                case "pbp":    if (value is bool p) { _movePbp    = p; Changed(); } break;
                case "survey": if (value is bool s) { _moveSurvey = s; Changed(); } break;
            }
        }

        public bool IsStepValid(string stepId)
        {
            switch (stepId)
            {
                case "links":    return _linkSpecs.Values.Any(s => s.Selected);
                case "settings": return _movePbp || _moveSurvey;
                default:         return true;
            }
        }

        public bool CanRun() => _linkSpecs.Values.Any(s => s.Selected) && (_movePbp || _moveSurvey);

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "links":    return LinksSummary() == "None" ? "-" : LinksSummary();
                case "settings": return PointsSummary();
                case "run":      return "Ready to run";
                default:         return "-";
            }
        }

        public bool IsStepVisible(string stepId) => true;

        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null)
            {
                pushLog("Run handler not registered.", "fail");
                onComplete(0, 1, 0);
                return;
            }

            _runHandler.MovePbp    = _movePbp;
            _runHandler.MoveSurvey = _moveSurvey;
            _runHandler.LinkSpecs  = _linkSpecs.Values.Select(s => new PushLinkSpec
            {
                LinkInstId = s.LinkInstId,
                LinkName   = s.LinkName,
                Selected   = s.Selected,
            }).ToList();
            _runHandler.PushLog    = pushLog;
            _runHandler.OnProgress = onProgress;
            _runHandler.OnComplete = onComplete;

            pushLog("Raising Revit ExternalEvent...", "info");
            _runEvent.Raise();
        }

        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog    = null;
            _runHandler.OnProgress = null;
            _runHandler.OnComplete = null;
        }

        private string LinksSummary()
        {
            int n = _linkSpecs.Values.Count(s => s.Selected);
            return n == 0 ? "None" : $"{n} link(s)";
        }

        private string PointsSummary()
        {
            if (_movePbp && _moveSurvey) return "Project Base + Survey";
            if (_movePbp)    return "Project Base Point";
            if (_moveSurvey) return "Survey Point";
            return "None";
        }
    }
}
