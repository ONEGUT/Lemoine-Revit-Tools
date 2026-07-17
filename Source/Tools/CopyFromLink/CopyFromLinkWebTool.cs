using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Tools.CopyFromLink
{
    /// <summary>Web port of <see cref="CopyFromLinkViewModel"/> — copy picked "Family: Type"
    /// entries out of a link into the host. Same scan/run handlers, settings, and AppStrings
    /// keys. The type scan runs on Source-step Confirm and rebuilds the Types step when the
    /// result lands (mirrors the WPF activation-driven scan).</summary>
    public class CopyFromLinkWebTool : WebToolBase, IWebToolCleanup, IWebConfirmable, IWebStepRefresh
    {
        private readonly CopyFromLinkScanHandler? _scanHandler;
        private readonly ExternalEvent?           _scanEvent;
        private readonly CopyFromLinkRunHandler?  _runHandler;
        private readonly ExternalEvent?           _runEvent;
        private readonly List<CopyFromLinkLinkInfo> _links;

        private readonly CopyFromLinkSpec _spec = new CopyFromLinkSpec();
        private readonly Dictionary<string, CopyFromLinkLinkInfo> _linkByName = new Dictionary<string, CopyFromLinkLinkInfo>();
        private HashSet<string> _selectedTypeKeys = new HashSet<string>(StringComparer.Ordinal);

        private bool _deletePrevious = CopyFromLinkSettings.Instance.DeletePrevious;
        private bool _onlyChanged    = CopyFromLinkSettings.Instance.OnlyChanged;
        private bool _deleteOrphans  = CopyFromLinkSettings.Instance.DeleteOrphans;

        private bool _scanning, _scanDone;
        private string? _scanError;
        private CopyFromLinkScanResult _scan = new CopyFromLinkScanResult();

        public event Action<string>? StepInputsChanged;

        public CopyFromLinkWebTool(
            CopyFromLinkScanHandler? scanHandler, ExternalEvent? scanEvent,
            CopyFromLinkRunHandler?  runHandler,  ExternalEvent?  runEvent,
            List<CopyFromLinkLinkInfo>? links)
        {
            _scanHandler = scanHandler; _scanEvent = scanEvent;
            _runHandler  = runHandler;  _runEvent  = runEvent;
            _links       = links ?? new List<CopyFromLinkLinkInfo>();

            foreach (var l in _links) _linkByName[l.Name] = l;
            if (_links.Count > 0) { _spec.LinkInstId = _links[0].LinkInstId; _spec.LinkInstUid = _links[0].LinkInstUid; }
        }

        public override string Title    => AppStrings.T("copy.fromLink.title");
        public override string RunLabel => AppStrings.T("copy.fromLink.runLabel");

        // ── Scan orchestration ────────────────────────────────────────────────

        public string? ConfirmLabelFor(string stepId) => null;

        public void OnStepConfirm(string stepId)
        {
            if (stepId != "source") return;
            if (_spec.Categories.Count == 0) { _scanDone = false; StepInputsChanged?.Invoke("types"); return; }
            if (!_scanDone && !_scanning) TriggerScan();
        }

        private void ResetScan()
        {
            _scanDone = false;
            _scanning = false;
            _scanError = null;
            _scan = new CopyFromLinkScanResult();
            _selectedTypeKeys.Clear();
        }

        private void TriggerScan()
        {
            if (_scanHandler == null || _scanEvent == null) return;
            _scanning = true; _scanDone = false; _scanError = null;
            StepInputsChanged?.Invoke("types");

            _scanHandler.ScanRequested = true;
            _scanHandler.Spec = new CopyFromLinkSpec
            {
                LinkInstId         = _spec.LinkInstId,
                LinkInstUid        = _spec.LinkInstUid,
                Categories         = new List<string>(_spec.Categories),
                ExcludedWorksetIds = new List<int>(_spec.ExcludedWorksetIds),
            };
            _scanHandler.OnScanned = result =>
            {
                _scanning = false; _scanDone = true; _scan = result ?? new CopyFromLinkScanResult();
                StepInputsChanged?.Invoke("types");
                Fire();
            };
            _scanHandler.OnError = err =>
            {
                _scanning = false; _scanError = err;
                StepInputsChanged?.Invoke("types");
                Fire();
            };
            _scanEvent.Raise();
        }

        // ── Steps ─────────────────────────────────────────────────────────────

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var source = new WebStep("source", AppStrings.T("copy.fromLink.steps.source"));
            BuildSourceInputs(source);

            var types = new WebStep("types", AppStrings.T("copy.fromLink.steps.types"))
                .Add(WebInput.Hint("familiesHelp", AppStrings.T("copy.fromLink.labels.familiesHelp")));
            BuildTypesInputs(types);

            var changes = new WebStep("changes", AppStrings.T("copy.fromLink.steps.changes"), required: false)
                .Add(WebInput.Hint("changesNote",
                    "By default each copy is a finished element — once created it is detached "
                    + "from this tool and a later run never deletes it. Turn on delete-previous to keep outputs "
                    + "stamped so a re-run on the same source elements replaces them instead of adding duplicates."))
                .Add(WebInput.Toggle("prev", AppStrings.T("copy.fromLink.labels.prevLabel"), _deletePrevious))
                .Add(WebInput.Hint("prevDesc", AppStrings.T("copy.fromLink.labels.prevDesc")));
            if (_deletePrevious)
            {
                changes.Add(WebInput.Toggle("only", AppStrings.T("copy.fromLink.labels.onlyLabel"), _onlyChanged));
                changes.Add(WebInput.Hint("onlyDesc", AppStrings.T("copy.fromLink.labels.onlyDesc")));
                changes.Add(WebInput.Toggle("orph", AppStrings.T("copy.fromLink.labels.orphLabel"), _deleteOrphans));
                changes.Add(WebInput.Hint("orphDesc", AppStrings.T("copy.fromLink.labels.orphDesc")));
            }

            var run = new WebStep("run", AppStrings.T("copy.fromLink.steps.run"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("copy.fromLink.review.itemLink"),
                     _links.FirstOrDefault(l => l.LinkInstId == _spec.LinkInstId)?.Name ?? "-"),
                    (AppStrings.T("copy.fromLink.review.itemCats"),
                     _spec.Categories.Count == 0 ? "-" : AppStrings.T("copy.fromLink.review.catsValue", _spec.Categories.Count)),
                    (AppStrings.T("copy.fromLink.review.itemTypes"),
                     _selectedTypeKeys.Count == 0 ? AppStrings.T("copy.fromLink.review.typesNone") : AppStrings.T("copy.fromLink.review.typesValue", _selectedTypeKeys.Count)),
                    (AppStrings.T("copy.fromLink.review.itemChanges"), ChangesSummary()),
                },
                warning: _selectedTypeKeys.Count == 0 ? AppStrings.T("copy.fromLink.review.warnNoTypes") : null));

            return new List<WebStep> { source, types, changes, run };
        }

        private void BuildSourceInputs(WebStep source)
        {
            if (_links.Count == 0)
            {
                source.Add(WebInput.Hint("noLinks", AppStrings.T("copy.fromLink.labels.noLinks")));
                return;
            }

            string current = _links.FirstOrDefault(l => l.LinkInstId == _spec.LinkInstId)?.Name ?? _links[0].Name;
            source.Add(WebInput.SingleSelect("link", AppStrings.T("copy.fromLink.labels.sourceLink"),
                current, _links.Select(l => new WebOption(l.Name, l.Name))));

            var catGroups = BuildCategoryGroups();
            var allCatDisplayNames = new HashSet<string>(catGroups.Values.SelectMany(v => v), StringComparer.Ordinal);
            var selectedCatLabels = AutoFiltersSettings.KnownCategoryMap
                .Where(kv => _spec.Categories.Contains(kv.Value) && allCatDisplayNames.Contains(kv.Key))
                .Select(kv => kv.Key).ToList();
            source.Add(WebInput.MultiSelectTabs("cats", AppStrings.T("copy.fromLink.labels.catsToCopy"),
                catGroups, selectedCatLabels));

            var link = _links.FirstOrDefault(l => l.LinkInstId == _spec.LinkInstId);
            var ws   = link?.Worksets ?? new List<CopyFromLinkWorksetInfo>();
            if (ws.Count == 0)
            {
                source.Add(WebInput.Hint("noWorksets", AppStrings.T("copy.fromLink.labels.noWorksets")));
            }
            else
            {
                var excluded = new HashSet<int>(_spec.ExcludedWorksetIds);
                var included = ws.Where(w => !excluded.Contains(w.Id)).Select(w => w.Name).ToList();
                source.Add(WebInput.MultiSelectTabs("worksets", AppStrings.T("copy.fromLink.labels.worksets"),
                    new Dictionary<string, List<string>> { ["Worksets"] = ws.Select(w => w.Name).ToList() },
                    included));
            }
        }

        private void BuildTypesInputs(WebStep types)
        {
            if (_scanning)          { types.Add(WebInput.Hint("scanning", AppStrings.T("copy.fromLink.labels.scanning"))); return; }
            if (_scanError != null) { types.Add(WebInput.Warn("scanError", AppStrings.T("copy.fromLink.labels.scanError", _scanError))); return; }
            if (!_scanDone)
            {
                types.Add(WebInput.Hint("notScanned", _spec.Categories.Count == 0
                    ? AppStrings.T("copy.fromLink.labels.pickCatFirst")
                    : AppStrings.T("copy.fromLink.labels.confirmToScan")));
                return;
            }
            if (_scan.TypesByCategory.Count == 0)
            {
                types.Add(WebInput.Hint("noTypes", AppStrings.T("copy.fromLink.labels.noTypes", _scan.ElementCount)));
                return;
            }

            var allKeys = new HashSet<string>(_scan.TypesByCategory.Values.SelectMany(v => v), StringComparer.Ordinal);
            if (_selectedTypeKeys.Count == 0) _selectedTypeKeys = new HashSet<string>(allKeys, StringComparer.Ordinal);
            else _selectedTypeKeys.IntersectWith(allKeys);
            if (_selectedTypeKeys.Count == 0) _selectedTypeKeys = new HashSet<string>(allKeys, StringComparer.Ordinal);

            types.Add(WebInput.Hint("typesSummary",
                AppStrings.T("copy.fromLink.labels.typesSummary", _scan.ElementCount, allKeys.Count, _scan.TypesByCategory.Count)));

            var groups = _scan.TypesByCategory.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
            types.Add(WebInput.MultiSelectTabs("typeKeys", AppStrings.T("copy.fromLink.labels.families"),
                groups, _selectedTypeKeys.ToList()));
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "link":
                {
                    var name = AsString(value);
                    if (_linkByName.TryGetValue(name, out var info) && info.LinkInstId != _spec.LinkInstId)
                    {
                        _spec.LinkInstId  = info.LinkInstId;
                        _spec.LinkInstUid = info.LinkInstUid;
                        _spec.ExcludedWorksetIds.Clear();
                        ResetScan();
                        StepInputsChanged?.Invoke("source");   // worksets are per-link
                        StepInputsChanged?.Invoke("types");
                        Fire();
                    }
                    return;
                }
                case "cats":
                {
                    var map = AutoFiltersSettings.KnownCategoryMap;
                    _spec.Categories = StrList(value)
                        .Select(s => map.TryGetValue(s, out var o) ? o : null)
                        .Where(o => o != null).Cast<string>().ToList();
                    ResetScan();
                    Fire();
                    return;
                }
                case "worksets":
                {
                    var link = _links.FirstOrDefault(l => l.LinkInstId == _spec.LinkInstId);
                    var ws   = link?.Worksets ?? new List<CopyFromLinkWorksetInfo>();
                    var byName = ws.ToDictionary(w => w.Name, w => w.Id);
                    var includedIds = new HashSet<int>(StrList(value).Where(byName.ContainsKey).Select(n => byName[n]));
                    _spec.ExcludedWorksetIds = ws.Where(w => !includedIds.Contains(w.Id)).Select(w => w.Id).ToList();
                    ResetScan();
                    Fire();
                    return;
                }
                case "typeKeys":
                    _selectedTypeKeys = new HashSet<string>(StrList(value), StringComparer.Ordinal);
                    Fire();
                    return;
                case "prev":
                    _deletePrevious = AsBool(value, _deletePrevious);
                    StepInputsChanged?.Invoke("changes");
                    Fire();
                    return;
                case "only": _onlyChanged   = AsBool(value, _onlyChanged);   Fire(); return;
                case "orph": _deleteOrphans = AsBool(value, _deleteOrphans); Fire(); return;
            }
        }

        private string ChangesSummary() => !_deletePrevious
            ? AppStrings.T("copy.fromLink.review.changesKeep")
            : _onlyChanged ? AppStrings.T("copy.fromLink.review.changesOnlyChanged") : AppStrings.T("copy.fromLink.review.changesReCopyAll");

        public override bool IsStepValid(string stepId)
        {
            switch (stepId)
            {
                case "source": return _spec.Categories.Count > 0;
                case "types":  return _selectedTypeKeys.Count > 0;
                default:       return true;
            }
        }

        public override bool CanRun() => _spec.Categories.Count > 0 && _selectedTypeKeys.Count > 0;

        public override string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "source":
                    return _spec.Categories.Count == 0 ? "-"
                        : AppStrings.T("copy.fromLink.summaries.source",
                            (_links.FirstOrDefault(l => l.LinkInstId == _spec.LinkInstId)?.Name ?? "link"), _spec.Categories.Count);
                case "types":
                    return _selectedTypeKeys.Count == 0 ? "-" : AppStrings.T("copy.fromLink.summaries.typeCount", _selectedTypeKeys.Count);
                case "changes":
                    return ChangesSummary();
                case "run": return AppStrings.T("copy.fromLink.summaries.run");
                default:    return "-";
            }
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog(AppStrings.T("copy.fromLink.log.handlerMissing"), "fail"); onComplete(0, 1, 0); return; }

            var s = CopyFromLinkSettings.Instance;
            s.DeletePrevious = _deletePrevious;
            s.OnlyChanged    = _onlyChanged;
            s.DeleteOrphans  = _deleteOrphans;
            s.Save();

            _spec.SelectedTypeKeys = _selectedTypeKeys.ToList();
            _runHandler.Spec           = _spec;
            _runHandler.DeletePrevious = _deletePrevious;
            _runHandler.OnlyChanged    = _deletePrevious && _onlyChanged;
            _runHandler.DeleteOrphans  = _deletePrevious && _deleteOrphans;
            _runHandler.PushLog    = pushLog;
            _runHandler.OnProgress = onProgress;
            _runHandler.OnComplete = onComplete;

            pushLog(AppStrings.T("copy.fromLink.log.raising"), "info");
            _runEvent.Raise();
        }

        private static Dictionary<string, List<string>> BuildCategoryGroups()
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var kv in AutoFiltersSettings.KnownCategoryMap)
            {
                string disc = DisciplineOf(kv.Value);
                if (!groups.ContainsKey(disc)) groups[disc] = new List<string>();
                groups[disc].Add(kv.Key);
            }
            foreach (var k in groups.Keys.ToList())
                groups[k].Sort(StringComparer.OrdinalIgnoreCase);
            return groups;
        }

        private static string DisciplineOf(string ost)
        {
            bool C(params string[] needles) => needles.Any(n => ost.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            if (C("Duct", "MechanicalEquipment", "FlexDuct"))                                      return "Mechanical";
            if (C("Pipe", "Plumbing", "Sprinkler", "FlexPipe", "FabricationPipe",
                  "FabricationHangers", "FabricationContainment"))                                  return "Piping";
            if (C("Cable", "Conduit", "Electrical", "Lighting", "Communication",
                  "FireAlarm", "Security", "Data", "Telephone", "NurseCall"))                       return "Electrical";
            if (C("Structural", "Rebar", "Reinforcement", "Fabric"))                                return "Structural";
            return "Architectural / Other";
        }

        public void OnWindowClosed()
        {
            if (_scanHandler != null) { _scanHandler.OnScanned = null; _scanHandler.OnError = null; }
            if (_runHandler != null) { _runHandler.PushLog = null; _runHandler.OnProgress = null; _runHandler.OnComplete = null; }
        }
    }
}
