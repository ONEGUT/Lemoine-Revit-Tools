using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.CopyFromLink
{
    /// <summary>Web port of <see cref="CopyDatumsViewModel"/> — copy grids/levels out of a
    /// linked model. Same handler, display-name maps (level-vs-grid name collision suffix,
    /// existing-in-host suffix + disabled), and AppStrings keys. Switching the source link
    /// clears the selection and rebuilds the datum tabs via IWebStepRefresh.</summary>
    public class CopyDatumsWebTool : WebToolBase, IWebToolCleanup, IWebStepRefresh
    {
        private readonly CopyDatumsRunHandler? _runHandler;
        private readonly ExternalEvent?        _runEvent;
        private readonly List<CopyDatumLinkInfo> _links;
        private readonly Dictionary<string, long> _linkByName = new Dictionary<string, long>();

        private long _linkId;
        // Display name → element id, kept separate per kind (see the WPF twin for why display
        // strings must be unique across both group tabs).
        private readonly Dictionary<string, long> _gridDisplayToId  = new Dictionary<string, long>();
        private readonly Dictionary<string, long> _levelDisplayToId = new Dictionary<string, long>();
        private HashSet<long> _selectedGridIds  = new HashSet<long>();
        private HashSet<long> _selectedLevelIds = new HashSet<long>();

        public event Action<string>? StepInputsChanged;

        public CopyDatumsWebTool(CopyDatumsRunHandler? runHandler, ExternalEvent? runEvent, List<CopyDatumLinkInfo>? links)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _links      = (links ?? new List<CopyDatumLinkInfo>())
                          .Where(l => l.Grids.Count > 0 || l.Levels.Count > 0).ToList();
            foreach (var l in _links) _linkByName[l.Name] = l.LinkInstId;
            if (_links.Count > 0) _linkId = _links[0].LinkInstId;
            RebuildDisplayMaps(selectAllCopyable: true);
        }

        public override string Title    => AppStrings.T("copy.datums.title");
        public override string RunLabel => AppStrings.T("copy.datums.runLabel");

        private CopyDatumLinkInfo? CurrentLink => _links.FirstOrDefault(l => l.LinkInstId == _linkId);

        // Rebuilds the display-name → id maps for the current link (WPF RebuildDatums). The
        // WPF SetGroups default selected every copyable datum; selectAllCopyable mirrors that.
        private HashSet<string> RebuildDisplayMaps(bool selectAllCopyable)
        {
            _gridDisplayToId.Clear();
            _levelDisplayToId.Clear();
            var disabled = new HashSet<string>(StringComparer.Ordinal);

            var grids  = CurrentLink?.Grids  ?? new List<CopyDatumItem>();
            var levels = CurrentLink?.Levels ?? new List<CopyDatumItem>();

            var gridNames = new HashSet<string>(grids.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var g in grids)
            {
                string display = g.ExistsInHost
                    ? AppStrings.T("copy.datums.labels.existingSuffix", g.Name)
                    : g.Name;
                _gridDisplayToId[display] = g.ElemId;
                if (g.ExistsInHost) disabled.Add(display);
            }
            foreach (var lvl in levels)
            {
                string baseName = gridNames.Contains(lvl.Name)
                    ? AppStrings.T("copy.datums.labels.levelNameCollision", lvl.Name)
                    : lvl.Name;
                string display = lvl.ExistsInHost
                    ? AppStrings.T("copy.datums.labels.existingSuffix", baseName)
                    : baseName;
                _levelDisplayToId[display] = lvl.ElemId;
                if (lvl.ExistsInHost) disabled.Add(display);
            }

            if (selectAllCopyable)
            {
                _selectedGridIds = new HashSet<long>(
                    _gridDisplayToId.Where(kv => !disabled.Contains(kv.Key)).Select(kv => kv.Value));
                _selectedLevelIds = new HashSet<long>(
                    _levelDisplayToId.Where(kv => !disabled.Contains(kv.Key)).Select(kv => kv.Value));
            }
            return disabled;
        }

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var source = new WebStep("source", AppStrings.T("copy.datums.steps.source"));
            if (_links.Count == 0)
            {
                source.Add(WebInput.Hint("noLinks", AppStrings.T("copy.datums.labels.noLinks")));
            }
            else
            {
                string current = CurrentLink?.Name ?? _links[0].Name;
                source.Add(WebInput.SingleSelect("link", AppStrings.T("copy.datums.labels.sourceLink"),
                    current, _links.Select(l => new WebOption(l.Name, l.Name))));

                var disabled = RebuildDisplayMaps(selectAllCopyable: false);
                var grids  = CurrentLink?.Grids  ?? new List<CopyDatumItem>();
                var levels = CurrentLink?.Levels ?? new List<CopyDatumItem>();
                if (grids.Count == 0 && levels.Count == 0)
                {
                    source.Add(WebInput.Hint("noDatums", AppStrings.T("copy.datums.labels.noDatums")));
                }
                else
                {
                    var groups = new Dictionary<string, List<string>>();
                    if (_gridDisplayToId.Count > 0)
                        groups[AppStrings.T("copy.datums.labels.gridsTab")] = _gridDisplayToId.Keys.ToList();
                    if (_levelDisplayToId.Count > 0)
                        groups[AppStrings.T("copy.datums.labels.levelsTab")] = _levelDisplayToId.Keys.ToList();

                    var selected = _gridDisplayToId.Where(kv => _selectedGridIds.Contains(kv.Value)).Select(kv => kv.Key)
                        .Concat(_levelDisplayToId.Where(kv => _selectedLevelIds.Contains(kv.Value)).Select(kv => kv.Key))
                        .ToList();

                    source.Add(WebInput.MultiSelectTabs("datums", AppStrings.T("copy.datums.labels.datumsToCopy"),
                        groups, selected, disabledItems: disabled));

                    int existing = grids.Count(g => g.ExistsInHost) + levels.Count(l => l.ExistsInHost);
                    if (existing > 0)
                        source.Add(WebInput.Hint("existingCount", AppStrings.T("copy.datums.labels.existingCount", existing)));
                }
            }

            var run = new WebStep("run", AppStrings.T("copy.datums.steps.run"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("copy.datums.review.itemLink"),
                     CurrentLink?.Name ?? "-"),
                    (AppStrings.T("copy.datums.review.itemDatums"),
                     _selectedGridIds.Count == 0 && _selectedLevelIds.Count == 0
                        ? AppStrings.T("copy.datums.review.datumsNone")
                        : AppStrings.T("copy.datums.review.datumsValue", _selectedGridIds.Count, _selectedLevelIds.Count)),
                }));

            return new List<WebStep> { source, run };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "link":
                {
                    var name = AsString(value);
                    if (_linkByName.TryGetValue(name, out var id) && id != _linkId)
                    {
                        _linkId = id;
                        RebuildDisplayMaps(selectAllCopyable: true);
                        StepInputsChanged?.Invoke("source");
                        Fire();
                    }
                    break;
                }
                case "datums":
                {
                    var sel = StrList(value);
                    _selectedGridIds  = new HashSet<long>(sel.Where(_gridDisplayToId.ContainsKey).Select(n => _gridDisplayToId[n]));
                    _selectedLevelIds = new HashSet<long>(sel.Where(_levelDisplayToId.ContainsKey).Select(n => _levelDisplayToId[n]));
                    Fire();
                    break;
                }
            }
        }

        public override bool IsStepValid(string stepId) =>
            stepId != "source" || _selectedGridIds.Count > 0 || _selectedLevelIds.Count > 0;

        public override bool CanRun() => _selectedGridIds.Count > 0 || _selectedLevelIds.Count > 0;

        public override string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "source":
                    int total = _selectedGridIds.Count + _selectedLevelIds.Count;
                    return total == 0 ? "-"
                        : AppStrings.T("copy.datums.summaries.source", CurrentLink?.Name ?? "link", total);
                case "run": return AppStrings.T("copy.datums.summaries.run");
                default:    return "-";
            }
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null)
            {
                pushLog(AppStrings.T("copy.datums.log.handlerMissing"), "fail");
                onComplete(0, 1, 0);
                return;
            }

            _runHandler.LinkInstId   = _linkId;
            _runHandler.GridElemIds  = _selectedGridIds.ToList();
            _runHandler.LevelElemIds = _selectedLevelIds.ToList();
            _runHandler.PushLog      = pushLog;
            _runHandler.OnProgress   = onProgress;
            _runHandler.OnComplete   = onComplete;

            pushLog(AppStrings.T("copy.datums.log.raising"), "info");
            _runEvent.Raise();
        }

        public void OnWindowClosed()
        {
            if (_runHandler == null) return;
            _runHandler.PushLog    = null;
            _runHandler.OnProgress = null;
            _runHandler.OnComplete = null;
        }
    }
}
