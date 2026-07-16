using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
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

        private bool _movePbp        = true;
        private bool _moveSurvey     = true;
        private bool _publishReplace = false;   // opt-in - shared-coordinate workflows only

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        private static string T(string key, params object[] args) => AppStrings.T("setup.pushCoordinates." + key, args);

        public PushCoordinatesWebTool(PushCoordinatesToLinksRunHandler? runHandler, ExternalEvent? runEvent, PushCoordinatesData? data)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _data       = data ?? new PushCoordinatesData();
            foreach (var l in _data.Links)
                _linkSpecs[l.LinkInstId] = new PushLinkSpec { LinkInstId = l.LinkInstId, LinkName = l.Name };
        }

        public string Title    => T("title");
        public string RunLabel => T("runLabel");

        public IReadOnlyList<WebStep> BuildSteps()
        {
            var links = new WebStep("links", T("steps.links")).Add(
                WebInput.CheckList("links", T("labels.linksCount", _data.Links.Count),
                    _data.Links.Select(l => (l.LinkInstId.ToString(), l.Name, _linkSpecs[l.LinkInstId].Selected))));

            // Shared-coordinate publish + instance re-place - opt-in (off by default). Publishing
            // republishes the host's coordinates to the link, then deletes/recreates the instance
            // (dropping any dependent dimensions/tags/overrides); most non-shared-coordinate
            // projects leave it off. Toggle has no description slot, so the caveat rides on a Hint.
            var settings = new WebStep("settings", T("steps.settings"))
                .Add(WebInput.Toggle("pbp",    T("labels.projectBasePoint"), _movePbp))
                .Add(WebInput.Toggle("survey", T("labels.surveyPoint"),       _moveSurvey))
                .Add(WebInput.Hint("savedHint", T("labels.savedHint")))
                .Add(WebInput.Toggle("publish", T("labels.publishLabel"), _publishReplace))
                .Add(WebInput.Hint("publishDesc", T("labels.publishDesc")));

            var run = new WebStep("run", T("steps.run"), required: false).Add(
                WebInput.Review("review",
                    new[] { (T("review.itemPoints"), PointsSummary()), (T("review.itemShared"), SharedSummary()) },
                    note: T("review.note"),
                    warning: (!_movePbp && !_moveSurvey) ? T("review.warnNoPoints") : null));

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
                case "pbp":     if (value is bool p)   { _movePbp        = p;   Changed(); } break;
                case "survey":  if (value is bool s)   { _moveSurvey     = s;   Changed(); } break;
                case "publish": if (value is bool pub) { _publishReplace = pub; Changed(); } break;
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
                case "links":    return LinksSummary();
                case "settings": return T("summaries.settings", PointsSummary(), _publishReplace ? "✓" : "✗");
                case "run":      return T("summaries.run");
                default:         return "—";
            }
        }

        public bool IsStepVisible(string stepId) => true;

        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null)
            {
                pushLog(T("log.handlerMissing"), "fail");
                onComplete(0, 1, 0);
                return;
            }

            _runHandler.MovePbp        = _movePbp;
            _runHandler.MoveSurvey     = _moveSurvey;
            _runHandler.PublishReplace = _publishReplace;
            _runHandler.LinkSpecs  = _linkSpecs.Values.Select(s => new PushLinkSpec
            {
                LinkInstId = s.LinkInstId,
                LinkName   = s.LinkName,
                Selected   = s.Selected,
            }).ToList();
            _runHandler.PushLog    = pushLog;
            _runHandler.OnProgress = onProgress;
            _runHandler.OnComplete = onComplete;

            pushLog(T("log.starting"), "info");
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
            return n == 0 ? "—" : T("review.linksValue", n);
        }

        private string PointsSummary()
        {
            if (_movePbp && _moveSurvey) return T("review.pointsBoth");
            if (_movePbp)    return T("review.pointsPbp");
            if (_moveSurvey) return T("review.pointsSurvey");
            return T("review.pointsNone");
        }

        private string SharedSummary() =>
            _publishReplace ? T("review.sharedOn") : T("review.sharedOff");
    }
}
