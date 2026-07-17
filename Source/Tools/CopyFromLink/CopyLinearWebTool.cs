using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Tools.CopyFromLink
{
    /// <summary>Web port of <see cref="CopyLinearViewModel"/> — split linear runs into segments
    /// or replace them with families at intervals. Same scan/run handlers, settings, and
    /// AppStrings keys. The parameter scan runs on Source-step Confirm; filter rows are dynamic
    /// inputs ("fparam_i"/"fvals_i"/"fdel_i") managed via IWebToolAction + IWebStepRefresh; the
    /// family length-parameter list is fetched lazily on the Revit thread and cached.</summary>
    public class CopyLinearWebTool : WebToolBase, IWebToolCleanup, IWebConfirmable, IWebStepRefresh, IWebToolAction
    {
        private readonly CopyLinearScanHandler? _scanHandler;
        private readonly ExternalEvent?         _scanEvent;
        private readonly CopyLinearRunHandler?  _runHandler;
        private readonly ExternalEvent?         _runEvent;
        private readonly List<CopyLinearDocInfo>    _docs;
        private readonly List<CopyLinearFamilyInfo> _families;

        private readonly CopyLinearSourceSpec _spec = new CopyLinearSourceSpec();
        private readonly Dictionary<string, long> _docDisplayToId = new Dictionary<string, long>();
        private readonly Dictionary<string, CopyLinearFamilyInfo> _familyByKey = new Dictionary<string, CopyLinearFamilyInfo>();

        private string _mode = CopyLinearSettings.Instance.Mode;

        private double _segLenFeet    = CopyLinearSettings.Instance.SegmentLengthFeet;
        private double _gapInches     = CopyLinearSettings.Instance.GapInches;
        private bool   _keepRemainder = CopyLinearSettings.Instance.KeepRemainder;

        private double _intervalFeet       = CopyLinearSettings.Instance.IntervalFeet;
        private double _extraSpacingInches = CopyLinearSettings.Instance.ExtraSpacingInches;
        private bool   _alignToSource      = CopyLinearSettings.Instance.AlignToSource;
        private string _lengthParam        = CopyLinearSettings.Instance.LengthParamName ?? "";
        private string _familyKey          = CopyLinearSettings.Instance.FamilyKey ?? "";

        private double _manualXInches = CopyLinearSettings.Instance.ManualOffsetXInches;
        private double _manualYInches = CopyLinearSettings.Instance.ManualOffsetYInches;
        private double _manualZInches = CopyLinearSettings.Instance.ManualOffsetZInches;

        private double _rotXDeg = CopyLinearSettings.Instance.RotationXDegrees;
        private double _rotYDeg = CopyLinearSettings.Instance.RotationYDegrees;
        private double _rotZDeg = CopyLinearSettings.Instance.RotationZDegrees;

        private readonly Dictionary<long, List<string>> _famParamCache = new Dictionary<long, List<string>>();
        private bool _famParamsLoading;

        private bool _deletePrevious = CopyLinearSettings.Instance.DeletePrevious;
        private bool _onlyChanged    = CopyLinearSettings.Instance.OnlyChanged;
        private bool _deleteOrphans  = CopyLinearSettings.Instance.DeleteOrphans;

        private bool _scanning, _scanDone;
        private string? _scanError;
        private CopyLinearScanResult _scan = new CopyLinearScanResult();
        private readonly List<string> _activeFilterParams = new List<string>();

        public event Action<string>? StepInputsChanged;

        public CopyLinearWebTool(
            CopyLinearScanHandler? scanHandler, ExternalEvent? scanEvent,
            CopyLinearRunHandler?  runHandler,  ExternalEvent?  runEvent,
            List<CopyLinearDocInfo>?    docs,
            List<CopyLinearFamilyInfo>? families)
        {
            _scanHandler = scanHandler; _scanEvent = scanEvent;
            _runHandler  = runHandler;  _runEvent  = runEvent;
            _docs        = docs ?? new List<CopyLinearDocInfo>();
            _families    = families ?? new List<CopyLinearFamilyInfo>();

            foreach (var f in _families) _familyByKey[f.Key] = f;
            foreach (var d in _docs) _docDisplayToId[d.Name] = d.LinkInstId;
            if (_docs.Count > 0) _spec.LinkInstId = _docs[0].LinkInstId;
        }

        public override string Title    => AppStrings.T("copy.linear.title");
        public override string RunLabel => AppStrings.T("copy.linear.runLabel");

        // ── Scan orchestration ────────────────────────────────────────────────

        public string? ConfirmLabelFor(string stepId) => null;

        // Mirrors WPF OnStepActivated("filters"): confirming Source scans the picked doc.
        public void OnStepConfirm(string stepId)
        {
            if (stepId != "source") return;
            if (_spec.Categories.Count == 0) { _scanDone = false; StepInputsChanged?.Invoke("filters"); return; }
            if (!_scanDone && !_scanning) TriggerScan();
        }

        private void ResetScan()
        {
            _scanDone = false;
            _scanning = false;
            _scanError = null;
            _activeFilterParams.Clear();
            _spec.ParamFilters.Clear();
        }

        private void TriggerScan()
        {
            if (_scanHandler == null || _scanEvent == null) return;
            _scanning = true; _scanDone = false; _scanError = null;
            StepInputsChanged?.Invoke("filters"); // show "Scanning…"

            _scanHandler.ScanRequested = true;
            _scanHandler.Spec = new CopyLinearSourceSpec
            {
                LinkInstId         = _spec.LinkInstId,
                Categories         = new List<string>(_spec.Categories),
                ExcludedWorksetIds = new List<int>(_spec.ExcludedWorksetIds),
            };
            _scanHandler.OnScanned = result =>
            {
                _scanning = false; _scanDone = true; _scan = result ?? new CopyLinearScanResult();
                StepInputsChanged?.Invoke("filters");
                Fire();
            };
            _scanHandler.OnError = err =>
            {
                _scanning = false; _scanError = err;
                StepInputsChanged?.Invoke("filters");
                Fire();
            };
            _scanEvent.Raise();
        }

        private long CurrentSymbolId => _familyByKey.TryGetValue(_familyKey, out var f) ? f.SymbolId : 0L;

        private void TriggerFamilyParams(long symbolId)
        {
            if (_scanHandler == null || _scanEvent == null || _famParamsLoading) return;
            _famParamsLoading = true;
            _scanHandler.FamilyParamRequest = symbolId;
            _scanHandler.OnFamilyParams = (id, names) =>
            {
                _famParamsLoading = false;
                _famParamCache[id] = names ?? new List<string>();
                StepInputsChanged?.Invoke("operation");
                Fire();
            };
            _scanEvent.Raise();
        }

        // ── Steps ─────────────────────────────────────────────────────────────

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var source = new WebStep("source", AppStrings.T("copy.linear.steps.source"));
            BuildSourceInputs(source);

            var filters = new WebStep("filters", AppStrings.T("copy.linear.steps.filters"), required: false)
                .Add(WebInput.Hint("filtersHelp", AppStrings.T("copy.linear.labels.paramFiltersHelp")));
            BuildFilterInputs(filters);

            var operation = new WebStep("operation", AppStrings.T("copy.linear.steps.operation"))
                .Add(WebInput.SingleSelect("mode", AppStrings.T("copy.linear.labels.operation"),
                    _mode == "Replace" ? "Replace" : "Split", new[]
                    {
                        new WebOption("Split",   AppStrings.T("copy.linear.labels.modeSplit")),
                        new WebOption("Replace", AppStrings.T("copy.linear.labels.modeReplace")),
                    }));
            if (_mode == "Replace") BuildReplaceInputs(operation);
            else                    BuildSplitInputs(operation);

            var changes = new WebStep("changes", AppStrings.T("copy.linear.steps.changes"), required: false)
                .Add(WebInput.Hint("changesNote", AppStrings.T("copy.linear.labels.changesNote")))
                .Add(WebInput.Toggle("prev", AppStrings.T("copy.linear.labels.prevLabel"), _deletePrevious))
                .Add(WebInput.Hint("prevDesc", AppStrings.T("copy.linear.labels.prevDesc")));
            if (_deletePrevious)
            {
                changes.Add(WebInput.Toggle("only", AppStrings.T("copy.linear.labels.onlyLabel"), _onlyChanged));
                changes.Add(WebInput.Hint("onlyDesc", AppStrings.T("copy.linear.labels.onlyDesc")));
                changes.Add(WebInput.Toggle("orph", AppStrings.T("copy.linear.labels.orphLabel"), _deleteOrphans));
                changes.Add(WebInput.Hint("orphDesc", AppStrings.T("copy.linear.labels.orphDesc")));
            }

            var run = new WebStep("run", AppStrings.T("copy.linear.steps.run"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("copy.linear.review.itemDoc"),
                     _docs.FirstOrDefault(d => d.LinkInstId == _spec.LinkInstId)?.Name ?? "-"),
                    (AppStrings.T("copy.linear.review.itemCats"),
                     _spec.Categories.Count == 0 ? "-" : AppStrings.T("copy.linear.review.catsValue", _spec.Categories.Count)),
                    (AppStrings.T("copy.linear.review.itemFilters"), FilterSummary()),
                    (AppStrings.T("copy.linear.review.itemMode"),
                     _mode == "Replace" ? AppStrings.T("copy.linear.review.modeReplace") : AppStrings.T("copy.linear.review.modeSplit")),
                    (AppStrings.T("copy.linear.review.itemParams"),
                     _mode == "Replace"
                        ? AppStrings.T("copy.linear.review.paramsInterval", _intervalFeet) + (_extraSpacingInches > 0 ? AppStrings.T("copy.linear.review.paramsExtra", _extraSpacingInches) : "")
                            + (_alignToSource ? AppStrings.T("copy.linear.review.paramsAligned") : HasManualOverride ? AppStrings.T("copy.linear.review.paramsManual") : "")
                            + (HasRotation ? AppStrings.T("copy.linear.review.paramsRotated") : "")
                        : AppStrings.T("copy.linear.review.paramsSegLen", _segLenFeet) + (_gapInches > 0 ? AppStrings.T("copy.linear.review.paramsGap", _gapInches) : "")),
                    (AppStrings.T("copy.linear.review.itemChanges"), ChangesSummary()),
                },
                warning: _mode == "Replace" && string.IsNullOrEmpty(_familyKey)
                    ? AppStrings.T("copy.linear.review.warnPickFamily") : null));

            return new List<WebStep> { source, filters, operation, changes, run };
        }

        private void BuildSourceInputs(WebStep source)
        {
            if (_docs.Count == 0)
            {
                source.Add(WebInput.Hint("noDocs", AppStrings.T("copy.linear.labels.noDocs")));
                return;
            }

            string current = _docs.FirstOrDefault(d => d.LinkInstId == _spec.LinkInstId)?.Name ?? _docs[0].Name;
            source.Add(WebInput.SingleSelect("doc", AppStrings.T("copy.linear.labels.sourceDoc"),
                current, _docs.Select(d => new WebOption(d.Name, d.Name))));

            var catGroups = BuildCategoryGroups();
            var allCatDisplayNames = new HashSet<string>(catGroups.Values.SelectMany(v => v), StringComparer.Ordinal);
            var selectedCatLabels = AutoFiltersSettings.KnownCategoryMap
                .Where(kv => _spec.Categories.Contains(kv.Value) && allCatDisplayNames.Contains(kv.Key))
                .Select(kv => kv.Key).ToList();
            source.Add(WebInput.MultiSelectTabs("cats", AppStrings.T("copy.linear.labels.catsToCopy"),
                catGroups, selectedCatLabels));

            var doc = _docs.FirstOrDefault(d => d.LinkInstId == _spec.LinkInstId);
            var ws  = doc?.Worksets ?? new List<CopyLinearWorksetInfo>();
            if (ws.Count == 0)
            {
                source.Add(WebInput.Hint("noWorksets", AppStrings.T("copy.linear.labels.noWorksets")));
            }
            else
            {
                var excluded = new HashSet<int>(_spec.ExcludedWorksetIds);
                var included = ws.Where(w => !excluded.Contains(w.Id)).Select(w => w.Name).ToList();
                source.Add(WebInput.MultiSelectTabs("worksets", AppStrings.T("copy.linear.labels.worksets"),
                    new Dictionary<string, List<string>> { ["Worksets"] = ws.Select(w => w.Name).ToList() },
                    included));
            }
        }

        private void BuildFilterInputs(WebStep filters)
        {
            if (_scanning)          { filters.Add(WebInput.Hint("scanning", AppStrings.T("copy.linear.labels.scanning"))); return; }
            if (_scanError != null) { filters.Add(WebInput.Warn("scanError", AppStrings.T("copy.linear.labels.scanError", _scanError))); return; }
            if (!_scanDone)
            {
                filters.Add(WebInput.Hint("notScanned", _spec.Categories.Count == 0
                    ? AppStrings.T("copy.linear.labels.pickCatFirst")
                    : AppStrings.T("copy.linear.labels.confirmToScan")));
                return;
            }
            if (_scan.ParameterValues.Count == 0)
            {
                filters.Add(WebInput.Hint("noParams", AppStrings.T("copy.linear.labels.noParams", _scan.ElementCount)));
                return;
            }

            filters.Add(WebInput.Hint("paramsSummary",
                AppStrings.T("copy.linear.labels.paramsSummary", _scan.ElementCount, _scan.ParameterValues.Count)));

            _activeFilterParams.RemoveAll(p => !_scan.ParameterValues.ContainsKey(p));
            foreach (var k in _spec.ParamFilters.Keys.Where(k => !_scan.ParameterValues.ContainsKey(k)).ToList())
                _spec.ParamFilters.Remove(k);

            for (int i = 0; i < _activeFilterParams.Count; i++)
            {
                string paramName = _activeFilterParams[i];

                var others  = new HashSet<string>(_activeFilterParams.Where(p => p != paramName), StringComparer.Ordinal);
                var options = _scan.ParameterValues.Keys
                    .Where(k => !others.Contains(k))
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                filters.Add(WebInput.SingleSelect($"fparam_{i}", AppStrings.T("copy.linear.labels.parameter"),
                    paramName, options.Select(n => new WebOption(n, n))));

                var values   = _scan.ParameterValues.TryGetValue(paramName, out var v)   ? v   : new List<string>();
                var selected = _spec.ParamFilters.TryGetValue(paramName,    out var sel) ? sel : new List<string>();
                var initial  = selected.Where(values.Contains).ToList();
                _spec.ParamFilters[paramName] = initial;
                filters.Add(WebInput.MultiSelectTabs($"fvals_{i}", "",
                    new Dictionary<string, List<string>> { [paramName] = values }, initial));

                filters.Add(WebInput.Button($"fdel_{i}", AppStrings.T("copy.linear.labels.removeFilter"), variant: "ghost"));
            }

            var used = new HashSet<string>(_activeFilterParams, StringComparer.Ordinal);
            if (_scan.ParameterValues.Keys.Any(k => !used.Contains(k)))
                filters.Add(WebInput.Button("addFilter", AppStrings.T("copy.linear.labels.addFilter")));
        }

        private void BuildSplitInputs(WebStep op)
        {
            op.Add(WebInput.Stepper("segLen", AppStrings.T("copy.linear.labels.segLength"), _segLenFeet, 0.5, 1000, 1, 1));
            op.Add(WebInput.Stepper("gap", AppStrings.T("copy.linear.labels.gap"), _gapInches, 0, 48, 0.25, 2));
            op.Add(WebInput.Toggle("keepRem", AppStrings.T("copy.linear.labels.keepRemLabel"), _keepRemainder));
            op.Add(WebInput.Hint("keepRemDesc", AppStrings.T("copy.linear.labels.keepRemDesc")));
        }

        private void BuildReplaceInputs(WebStep op)
        {
            if (_families.Count == 0)
            {
                op.Add(WebInput.Hint("noFamilies", AppStrings.T("copy.linear.labels.noFamilies")));
            }
            else
            {
                if (!_familyByKey.ContainsKey(_familyKey)) _familyKey = _families[0].Key;
                op.Add(WebInput.SearchSelect("family", AppStrings.T("copy.linear.labels.familyType"),
                    _families.Select(f => f.Key), _familyKey,
                    AppStrings.T("copy.linear.labels.familySearch")));
            }

            op.Add(WebInput.Stepper("interval", AppStrings.T("copy.linear.labels.interval"), _intervalFeet, 0.25, 1000, 1, 2));
            op.Add(WebInput.Stepper("extraSpacing", AppStrings.T("copy.linear.labels.extraSpacing"), _extraSpacingInches, 0, 120, 0.25, 2));

            // Length parameter — cached names render as a dropdown, a cache miss shows a
            // loading line and kicks the Revit-thread fetch (which rebuilds this step).
            op.Add(WebInput.Hint("lengthParamHelp", AppStrings.T("copy.linear.labels.lengthParamHelp")));
            long symbolId = CurrentSymbolId;
            if (symbolId == 0L)
            {
                op.Add(WebInput.Hint("pickFamilyFirst", AppStrings.T("copy.linear.labels.pickFamilyFirst")));
            }
            else if (_famParamCache.TryGetValue(symbolId, out var names))
            {
                if (names.Count == 0)
                {
                    _lengthParam = "";
                    op.Add(WebInput.Hint("noWritableParams", AppStrings.T("copy.linear.labels.noWritableParams")));
                }
                else
                {
                    _lengthParam = names.FirstOrDefault(n =>
                        string.Equals(n, _lengthParam, StringComparison.OrdinalIgnoreCase)) ?? "";
                    string none = AppStrings.T("copy.linear.labels.noLengthParam");
                    op.Add(WebInput.SingleSelect("lengthParam", AppStrings.T("copy.linear.labels.lengthParam"),
                        string.IsNullOrEmpty(_lengthParam) ? "" : _lengthParam,
                        new[] { new WebOption("", none) }.Concat(names.Select(n => new WebOption(n, n)))));
                }
            }
            else
            {
                op.Add(WebInput.Hint("readingParams", AppStrings.T("copy.linear.labels.readingParams")));
                TriggerFamilyParams(symbolId);
            }

            op.Add(WebInput.Hint("rotateHelp", AppStrings.T("copy.linear.labels.rotateHelp")));
            op.Add(WebInput.Stepper("rotX", AppStrings.T("copy.linear.labels.rotateX"), _rotXDeg, -360, 360, 5, 1));
            op.Add(WebInput.Stepper("rotY", AppStrings.T("copy.linear.labels.rotateY"), _rotYDeg, -360, 360, 5, 1));
            op.Add(WebInput.Stepper("rotZ", AppStrings.T("copy.linear.labels.rotateZ"), _rotZDeg, -360, 360, 5, 1));

            op.Add(WebInput.Toggle("align", AppStrings.T("copy.linear.labels.alignLabel"), _alignToSource));
            op.Add(WebInput.Hint("alignDesc", AppStrings.T("copy.linear.labels.alignDesc")));
            if (!_alignToSource)
            {
                op.Add(WebInput.Hint("manualHelp", AppStrings.T("copy.linear.labels.manualHelp")));
                op.Add(WebInput.Stepper("moveX", AppStrings.T("copy.linear.labels.moveX"), _manualXInches, -1200, 1200, 1, 2));
                op.Add(WebInput.Stepper("moveY", AppStrings.T("copy.linear.labels.moveY"), _manualYInches, -1200, 1200, 1, 2));
                op.Add(WebInput.Stepper("moveZ", AppStrings.T("copy.linear.labels.moveZ"), _manualZInches, -1200, 1200, 1, 2));
            }
        }

        // ── Action buttons (add/remove filter rows) ───────────────────────────

        public void OnToolAction(string stepId, string inputId)
        {
            if (inputId == "addFilter")
            {
                var firstAvail = _scan.ParameterValues.Keys
                    .Where(k => !_activeFilterParams.Contains(k))
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (firstAvail == null) return;
                _activeFilterParams.Add(firstAvail);
                StepInputsChanged?.Invoke("filters");
                Fire();
                return;
            }

            if (inputId.StartsWith("fdel_", StringComparison.Ordinal)
                && int.TryParse(inputId.Substring(5), out var idx)
                && idx >= 0 && idx < _activeFilterParams.Count)
            {
                var param = _activeFilterParams[idx];
                _activeFilterParams.RemoveAt(idx);
                _spec.ParamFilters.Remove(param);
                StepInputsChanged?.Invoke("filters");
                Fire();
            }
        }

        // ── State ─────────────────────────────────────────────────────────────

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "doc":
                {
                    var name = AsString(value);
                    if (_docDisplayToId.TryGetValue(name, out var id) && id != _spec.LinkInstId)
                    {
                        _spec.LinkInstId = id;
                        _spec.ExcludedWorksetIds.Clear();
                        ResetScan();
                        StepInputsChanged?.Invoke("source");   // worksets list is per-doc
                        StepInputsChanged?.Invoke("filters");
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
                    var doc = _docs.FirstOrDefault(d => d.LinkInstId == _spec.LinkInstId);
                    var ws  = doc?.Worksets ?? new List<CopyLinearWorksetInfo>();
                    var byName = ws.ToDictionary(w => w.Name, w => w.Id);
                    var includedIds = new HashSet<int>(StrList(value).Where(byName.ContainsKey).Select(n => byName[n]));
                    _spec.ExcludedWorksetIds = ws.Where(w => !includedIds.Contains(w.Id)).Select(w => w.Id).ToList();
                    ResetScan();
                    Fire();
                    return;
                }
                case "mode":
                {
                    var m = AsString(value) == "Replace" ? "Replace" : "Split";
                    if (m != _mode)
                    {
                        _mode = m;
                        StepInputsChanged?.Invoke("operation");
                        Fire();
                    }
                    return;
                }
                case "segLen":       _segLenFeet         = AsDouble(value, _segLenFeet);         Fire(); return;
                case "gap":          _gapInches          = AsDouble(value, _gapInches);          Fire(); return;
                case "keepRem":      _keepRemainder      = AsBool(value, _keepRemainder);        Fire(); return;
                case "family":
                {
                    var v = AsString(value);
                    string newKey = _familyByKey.ContainsKey(v) ? v : "";
                    if (newKey != _familyKey)
                    {
                        _familyKey = newKey;
                        StepInputsChanged?.Invoke("operation");   // length-param list is per-family
                    }
                    Fire();
                    return;
                }
                case "interval":     _intervalFeet       = AsDouble(value, _intervalFeet);       Fire(); return;
                case "extraSpacing": _extraSpacingInches = AsDouble(value, _extraSpacingInches); Fire(); return;
                case "lengthParam":  _lengthParam        = AsString(value);                      Fire(); return;
                case "rotX":         _rotXDeg            = AsDouble(value, _rotXDeg);            Fire(); return;
                case "rotY":         _rotYDeg            = AsDouble(value, _rotYDeg);            Fire(); return;
                case "rotZ":         _rotZDeg            = AsDouble(value, _rotZDeg);            Fire(); return;
                case "align":
                    _alignToSource = AsBool(value, _alignToSource);
                    StepInputsChanged?.Invoke("operation");   // manual offsets are conditional
                    Fire(); return;
                case "moveX":        _manualXInches      = AsDouble(value, _manualXInches);      Fire(); return;
                case "moveY":        _manualYInches      = AsDouble(value, _manualYInches);      Fire(); return;
                case "moveZ":        _manualZInches      = AsDouble(value, _manualZInches);      Fire(); return;
                case "prev":
                    _deletePrevious = AsBool(value, _deletePrevious);
                    StepInputsChanged?.Invoke("changes");     // sub-toggles are conditional
                    Fire(); return;
                case "only":         _onlyChanged        = AsBool(value, _onlyChanged);          Fire(); return;
                case "orph":         _deleteOrphans      = AsBool(value, _deleteOrphans);        Fire(); return;
            }

            // Filter-row inputs: fparam_i / fvals_i
            if (inputId.StartsWith("fparam_", StringComparison.Ordinal)
                && int.TryParse(inputId.Substring(7), out var pi)
                && pi >= 0 && pi < _activeFilterParams.Count)
            {
                var newParam = AsString(value);
                var oldParam = _activeFilterParams[pi];
                if (!string.IsNullOrEmpty(newParam) && newParam != oldParam
                    && _scan.ParameterValues.ContainsKey(newParam))
                {
                    _spec.ParamFilters.Remove(oldParam);
                    _activeFilterParams[pi] = newParam;
                    StepInputsChanged?.Invoke("filters");
                    Fire();
                }
                return;
            }

            if (inputId.StartsWith("fvals_", StringComparison.Ordinal)
                && int.TryParse(inputId.Substring(6), out var vi)
                && vi >= 0 && vi < _activeFilterParams.Count)
            {
                _spec.ParamFilters[_activeFilterParams[vi]] = StrList(value);
                Fire();
            }
        }

        // ── Summaries / validation ────────────────────────────────────────────

        private string FilterSummary()
        {
            int active = _spec.ParamFilters.Count(kv => kv.Value?.Count > 0);
            return active == 0 ? AppStrings.T("copy.linear.review.filterAll") : AppStrings.T("copy.linear.review.filterCount", active);
        }

        private bool HasManualOverride =>
            Math.Abs(_manualXInches) > 1e-9 || Math.Abs(_manualYInches) > 1e-9 || Math.Abs(_manualZInches) > 1e-9;

        private bool HasRotation =>
            Math.Abs(_rotXDeg) > 1e-9 || Math.Abs(_rotYDeg) > 1e-9 || Math.Abs(_rotZDeg) > 1e-9;

        private string ChangesSummary() => !_deletePrevious
            ? AppStrings.T("copy.linear.review.changesKeep")
            : _onlyChanged ? AppStrings.T("copy.linear.review.changesOnlyChanged") : AppStrings.T("copy.linear.review.changesRebuildAll");

        public override bool IsStepValid(string stepId)
        {
            switch (stepId)
            {
                case "source":    return _spec.Categories.Count > 0;
                case "operation": return _mode == "Replace"
                                       ? (_familyByKey.ContainsKey(_familyKey) && _intervalFeet > 0)
                                       : _segLenFeet > 0;
                default:          return true;
            }
        }

        public override bool CanRun() => IsStepValid("source") && IsStepValid("operation");

        public override string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "source":
                    return _spec.Categories.Count == 0 ? "-"
                        : AppStrings.T("copy.linear.summaries.source",
                            (_docs.FirstOrDefault(d => d.LinkInstId == _spec.LinkInstId)?.Name ?? "source"), _spec.Categories.Count);
                case "filters":
                    return FilterSummary();
                case "operation":
                    return _mode == "Replace" ? AppStrings.T("copy.linear.summaries.opReplace", _intervalFeet) : AppStrings.T("copy.linear.summaries.opSplit", _segLenFeet);
                case "changes":
                    return ChangesSummary();
                case "run": return AppStrings.T("copy.linear.summaries.run");
                default:    return "-";
            }
        }

        // ── Run ───────────────────────────────────────────────────────────────

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog(AppStrings.T("copy.linear.log.handlerMissing"), "fail"); onComplete(0, 1, 0); return; }

            SaveSettings();

            _runHandler.Spec              = _spec;
            _runHandler.Mode              = _mode;
            _runHandler.SegmentLengthFeet = _segLenFeet;
            _runHandler.GapFeet           = _gapInches / 12.0;
            _runHandler.KeepRemainder     = _keepRemainder;
            _runHandler.IntervalFeet      = _intervalFeet;
            _runHandler.ExtraSpacingFeet  = _extraSpacingInches / 12.0;
            _runHandler.AlignToSource     = _alignToSource;
            _runHandler.LengthParamName   = _lengthParam;
            _runHandler.SymbolId          = _familyByKey.TryGetValue(_familyKey, out var f) ? f.SymbolId : 0L;
            _runHandler.ManualOffsetAlongFeet = _alignToSource ? 0.0 : _manualXInches / 12.0;
            _runHandler.ManualOffsetSideFeet  = _alignToSource ? 0.0 : _manualYInches / 12.0;
            _runHandler.ManualOffsetUpFeet    = _alignToSource ? 0.0 : _manualZInches / 12.0;
            _runHandler.RotationXRad          = _rotXDeg * Math.PI / 180.0;
            _runHandler.RotationYRad          = _rotYDeg * Math.PI / 180.0;
            _runHandler.RotationZRad          = _rotZDeg * Math.PI / 180.0;
            _runHandler.DeletePrevious    = _deletePrevious;
            _runHandler.OnlyChanged       = _deletePrevious && _onlyChanged;
            _runHandler.DeleteOrphans     = _deletePrevious && _deleteOrphans;
            _runHandler.PushLog    = pushLog;
            _runHandler.OnProgress = onProgress;
            _runHandler.OnComplete = onComplete;

            pushLog(AppStrings.T("copy.linear.log.raising"), "info");
            _runEvent.Raise();
        }

        private void SaveSettings()
        {
            var s = CopyLinearSettings.Instance;
            s.Mode = _mode;
            s.SegmentLengthFeet = _segLenFeet; s.GapInches = _gapInches; s.KeepRemainder = _keepRemainder;
            s.IntervalFeet = _intervalFeet; s.ExtraSpacingInches = _extraSpacingInches;
            s.AlignToSource = _alignToSource;
            s.ManualOffsetXInches = _manualXInches; s.ManualOffsetYInches = _manualYInches;
            s.ManualOffsetZInches = _manualZInches;
            s.RotationXDegrees = _rotXDeg; s.RotationYDegrees = _rotYDeg; s.RotationZDegrees = _rotZDeg;
            s.LengthParamName = _lengthParam; s.FamilyKey = _familyKey;
            s.DeletePrevious = _deletePrevious;
            s.OnlyChanged = _onlyChanged; s.DeleteOrphans = _deleteOrphans;
            s.Save();
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
            if (_scanHandler != null)
            {
                _scanHandler.OnScanned = null;
                _scanHandler.OnError = null;
                _scanHandler.OnFamilyParams = null;
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
