using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.Ceilings
{
    /// <summary>Web port of <see cref="MakeCeilingGridsViewModel"/> — export ceiling-grid DWGs
    /// per view. Same phase-1 scan + run handlers and AppStrings keys. The WPF re-scan-on-
    /// filter-step-activation becomes a scan on docs-step Confirm (IWebConfirmable) with the
    /// filter step rebuilt via IWebStepRefresh when the async type list lands.</summary>
    public class MakeCeilingGridsWebTool : WebToolBase, IWebToolCleanup, IWebConfirmable, IWebStepRefresh
    {
        private readonly MakeCeilingGridsPhase1Handler? _phase1Handler;
        private readonly Autodesk.Revit.UI.ExternalEvent? _phase1Event;
        private readonly MakeCeilingGridsRunHandler?    _runHandler;
        private readonly Autodesk.Revit.UI.ExternalEvent? _runEvent;

        private readonly List<MakeCeilingGridsViewModel.DocEntry> _availableDocs;
        private readonly Dictionary<string, MakeCeilingGridsViewModel.DocEntry> _docByLabel;

        private List<string>           _selectedDocLabels;
        private List<CeilingTypeEntry> _ceilingTypes = new List<CeilingTypeEntry>();

        // Exclusion is name-based ("Family|Type") so unchecking a type hides it across
        // every model — consistent with the name-based hide filter in the run handler.
        private HashSet<string> _excludedTypeKeys = new HashSet<string>(StringComparer.Ordinal);
        private HashSet<string> _allTypeKeys      = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _displayToKey = new Dictionary<string, string>(StringComparer.Ordinal);

        private bool _scanning = false;
        private bool _scanDone = false;
        private string? _scanError;

        private string _outputFolder             = MakeCeilingGridsSettings.Instance.OutputFolder;
        private bool   _useCeilingGridsSubfolder = MakeCeilingGridsSettings.Instance.UseCeilingGridsSubfolder;

        public event Action<string>? StepInputsChanged;

        public MakeCeilingGridsWebTool(
            MakeCeilingGridsPhase1Handler? phase1Handler, Autodesk.Revit.UI.ExternalEvent? phase1Event,
            MakeCeilingGridsRunHandler?    runHandler,    Autodesk.Revit.UI.ExternalEvent? runEvent,
            List<MakeCeilingGridsViewModel.DocEntry>?     availableDocs)
        {
            _phase1Handler = phase1Handler;
            _phase1Event   = phase1Event;
            _runHandler    = runHandler;
            _runEvent      = runEvent;
            _availableDocs = availableDocs ?? new List<MakeCeilingGridsViewModel.DocEntry>();

            _docByLabel = new Dictionary<string, MakeCeilingGridsViewModel.DocEntry>(StringComparer.Ordinal);
            foreach (var d in _availableDocs) _docByLabel[d.Label] = d;

            _selectedDocLabels = _availableDocs.Select(d => d.Label).ToList();
        }

        public override string Title    => AppStrings.T("ceilings.makeGrids.title");
        public override string RunLabel => AppStrings.T("ceilings.makeGrids.runLabel");

        // ── Scan orchestration ────────────────────────────────────────────────

        public string? ConfirmLabelFor(string stepId) => null;

        // Mirrors WPF OnStepActivated("filter"): confirming the docs step scans ceiling types
        // for the current document selection when the scan is stale.
        public void OnStepConfirm(string stepId)
        {
            if (stepId != "docs") return;
            if (_scanDone || _scanning) return;
            TriggerPhase1();
        }

        private void TriggerPhase1()
        {
            if (_phase1Handler == null || _phase1Event == null) return;
            _scanning  = true;
            _scanError = null;

            bool includeHost = false;
            var  linkInstIds = new List<ElementId>();
            foreach (var label in _selectedDocLabels)
            {
                if (!_docByLabel.TryGetValue(label, out var entry)) continue;
                if (entry.IsHost) includeHost = true;
                else              linkInstIds.Add(entry.LinkInstId);
            }

            _phase1Handler.IncludeHost = includeHost;
            _phase1Handler.LinkInstIds = linkInstIds;

            // Callbacks land on the Revit thread; the window's StepInputsChanged handler
            // marshals to the UI dispatcher itself (SafeUi).
            _phase1Handler.OnTypesLoaded = results =>
            {
                _scanning     = false;
                _scanDone     = true;
                _ceilingTypes = results ?? new List<CeilingTypeEntry>();
                StepInputsChanged?.Invoke("filter");
                Fire();
            };
            _phase1Handler.OnError = err =>
            {
                _scanning  = false;
                _scanError = err;
                StepInputsChanged?.Invoke("filter");
                Fire();
            };

            _phase1Event.Raise();
        }

        // ── Steps ─────────────────────────────────────────────────────────────

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var docs = new WebStep("docs", AppStrings.T("ceilings.makeGrids.steps.docs"));
            if (_availableDocs.Count == 0)
                docs.Add(WebInput.Hint("noDocs", AppStrings.T("ceilings.makeGrids.labels.noDocs")));
            else
                docs.Add(WebInput.MultiSelectTabs("docs", "",
                    new Dictionary<string, List<string>> { ["Documents"] = _availableDocs.Select(d => d.Label).ToList() },
                    _selectedDocLabels));

            var filter = new WebStep("filter", AppStrings.T("ceilings.makeGrids.steps.filter"), required: false);
            BuildFilterInputs(filter);

            var export = new WebStep("export", AppStrings.T("ceilings.makeGrids.steps.export"))
                .Add(WebInput.FolderBrowser("folder", AppStrings.T("ceilings.makeGrids.labels.outputFolder"), _outputFolder))
                .Add(WebInput.Toggle("subfolder", AppStrings.T("ceilings.makeGrids.labels.subfolderLabel"), _useCeilingGridsSubfolder))
                .Add(WebInput.Hint("subfolderDesc", AppStrings.T("ceilings.makeGrids.labels.subfolderDesc")));

            int total    = DistinctTypeCount();
            int excluded = _excludedTypeKeys.Count;
            var run = new WebStep("run", AppStrings.T("ceilings.makeGrids.steps.run"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("ceilings.makeGrids.review.itemTypes"),
                     total == 0 ? AppStrings.T("ceilings.makeGrids.review.typesPending")
                                : AppStrings.T("ceilings.makeGrids.review.typesIncluded", total - excluded, total)),
                    (AppStrings.T("ceilings.makeGrids.review.itemFolder"),
                     string.IsNullOrEmpty(_outputFolder) ? "-"
                        : (_outputFolder.Length > 40 ? "…" + _outputFolder.Substring(_outputFolder.Length - 37) : _outputFolder)),
                    (AppStrings.T("ceilings.makeGrids.review.itemSubfolder"),
                     _useCeilingGridsSubfolder ? AppStrings.T("ceilings.makeGrids.review.subfolderOn") : AppStrings.T("ceilings.makeGrids.review.subfolderOff")),
                    (AppStrings.T("ceilings.makeGrids.review.itemDwg"), AppStrings.T("ceilings.makeGrids.review.dwg")),
                    (AppStrings.T("ceilings.makeGrids.review.itemDocs"),
                     _selectedDocLabels.Count == 0 ? AppStrings.T("ceilings.makeGrids.review.docsNone")
                        : string.Join(", ", _selectedDocLabels.Take(2)) + (_selectedDocLabels.Count > 2 ? AppStrings.T("ceilings.makeGrids.review.docsMore", _selectedDocLabels.Count - 2) : "")),
                }));

            return new List<WebStep> { docs, filter, export, run };
        }

        private void BuildFilterInputs(WebStep filter)
        {
            if (_scanError != null)
            {
                filter.Add(WebInput.Warn("scanError", AppStrings.T("ceilings.makeGrids.labels.scanError", _scanError)));
                return;
            }
            if (!_scanDone)
            {
                filter.Add(WebInput.Hint("scanning", AppStrings.T("ceilings.makeGrids.labels.scanning")));
                return;
            }
            if (_ceilingTypes.Count == 0)
            {
                filter.Add(WebInput.Hint("noTypes", AppStrings.T("ceilings.makeGrids.labels.noTypes")));
                return;
            }

            _displayToKey.Clear();
            _allTypeKeys.Clear();

            // One tab per source model, scan order preserved; items are "{Family} — {Type}"
            // rows shared by display string across tabs (name-based, like the run handler).
            var groups     = new Dictionary<string, List<string>>();
            var groupOrder = new List<string>();
            foreach (var t in _ceilingTypes
                .OrderBy(t => t.FamilyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.TypeName,   StringComparer.OrdinalIgnoreCase))
            {
                string display = AppStrings.T("ceilings.makeGrids.labels.typeDisplay", t.FamilyName, t.TypeName);
                string key     = $"{t.FamilyName}|{t.TypeName}";

                _displayToKey[display] = key;
                _allTypeKeys.Add(key);

                if (!groups.TryGetValue(t.Source, out var list))
                {
                    list = new List<string>();
                    groups[t.Source] = list;
                    groupOrder.Add(t.Source);
                }
                if (!list.Contains(display)) list.Add(display);
            }

            _excludedTypeKeys.IntersectWith(_allTypeKeys);

            var orderedGroups = new Dictionary<string, List<string>>();
            foreach (var src in groupOrder) orderedGroups[src] = groups[src];

            var initialSelected = _displayToKey
                .Where(kv => !_excludedTypeKeys.Contains(kv.Value))
                .Select(kv => kv.Key)
                .ToList();

            filter.Add(WebInput.MultiSelectTabs("types",
                AppStrings.T("ceilings.makeGrids.labels.typesByModel"), orderedGroups, initialSelected));
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "docs":
                    _selectedDocLabels = StrList(value);
                    // Scan is stale for the new document set (WPF cleared it the same way).
                    _scanDone = false;
                    _scanning = false;
                    _ceilingTypes.Clear();
                    _excludedTypeKeys.Clear();
                    Fire();
                    break;
                case "types":
                {
                    var includedKeys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var d in StrList(value))
                        if (_displayToKey.TryGetValue(d, out var k)) includedKeys.Add(k);
                    _excludedTypeKeys = new HashSet<string>(
                        _allTypeKeys.Where(k => !includedKeys.Contains(k)), StringComparer.Ordinal);
                    Fire();
                    break;
                }
                case "folder":    _outputFolder = AsString(value); Fire(); break;
                case "subfolder": _useCeilingGridsSubfolder = AsBool(value, _useCeilingGridsSubfolder); break;
            }
        }

        public override bool IsStepValid(string stepId)
        {
            if (stepId == "docs")   return _selectedDocLabels.Count > 0;
            if (stepId == "export") return !string.IsNullOrWhiteSpace(_outputFolder);
            return true;
        }

        public override bool CanRun() =>
            _selectedDocLabels.Count > 0 && !string.IsNullOrWhiteSpace(_outputFolder);

        public override string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "docs":
                    return _selectedDocLabels.Count == 0 ? "-"
                        : AppStrings.T("ceilings.makeGrids.summaries.docsCount", _selectedDocLabels.Count);
                case "filter":
                    if (!_scanDone) return AppStrings.T("ceilings.makeGrids.summaries.scanPending");
                    int total    = DistinctTypeCount();
                    int excluded = _excludedTypeKeys.Count;
                    return excluded == 0 ? AppStrings.T("ceilings.makeGrids.summaries.allIncluded", total)
                        : AppStrings.T("ceilings.makeGrids.summaries.someIncluded", total - excluded, total);
                case "export":
                    return string.IsNullOrEmpty(_outputFolder) ? "-"
                        : System.IO.Path.GetFileName(_outputFolder.TrimEnd('\\', '/'));
                case "run":
                    return AppStrings.T("ceilings.makeGrids.summaries.run");
                default:
                    return "-";
            }
        }

        private int DistinctTypeCount()
            => _ceilingTypes
                .Select(t => $"{t.FamilyName}|{t.TypeName}")
                .Distinct(StringComparer.Ordinal)
                .Count();

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null)
            {
                pushLog(AppStrings.T("ceilings.makeGrids.log.runHandlerMissing"), "fail");
                onComplete(0, 1, 0);
                return;
            }

            var excludedTypeNames = _ceilingTypes
                .Select(t => (t.FamilyName, t.TypeName))
                .Distinct()
                .Where(p => _excludedTypeKeys.Contains($"{p.FamilyName}|{p.TypeName}"))
                .ToList();

            bool includeHost = false;
            var  linkInstIds = new List<ElementId>();
            foreach (var label in _selectedDocLabels)
            {
                if (!_docByLabel.TryGetValue(label, out var entry)) continue;
                if (entry.IsHost) includeHost = true;
                else              linkInstIds.Add(entry.LinkInstId);
            }

            _runHandler.IncludeHost              = includeHost;
            _runHandler.LinkInstIds              = linkInstIds;
            _runHandler.ExcludedTypeNames        = excludedTypeNames;
            _runHandler.OutputFolder             = _outputFolder;
            _runHandler.UseCeilingGridsSubfolder = _useCeilingGridsSubfolder;
            _runHandler.PushLog                  = pushLog;
            _runHandler.OnProgress               = onProgress;
            _runHandler.OnComplete               = onComplete;

            pushLog(AppStrings.T("ceilings.makeGrids.log.raising"), "info");
            _runEvent.Raise();
        }

        public void OnWindowClosed()
        {
            if (_phase1Handler != null)
            {
                _phase1Handler.OnTypesLoaded = null;
                _phase1Handler.OnError = null;
            }
            if (_runHandler != null)
            {
                _runHandler.PushLog    = null;
                _runHandler.OnProgress = null;
                _runHandler.OnComplete = null;
            }
        }
    }
}
