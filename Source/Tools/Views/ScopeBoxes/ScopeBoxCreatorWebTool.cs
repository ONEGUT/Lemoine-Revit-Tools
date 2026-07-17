using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.ScopeBoxes
{
    /// <summary>Web port of <see cref="ScopeBoxCreatorViewModel"/> — cluster rooms into scope
    /// boxes duplicated from a seed box. Same scan/run handlers, settings, pattern store, and
    /// AppStrings keys. The scan runs on S1 Confirm; the seed list re-scans on S2 Confirm so a
    /// box drawn mid-session appears without reopening (WPF did both on step activation).</summary>
    public class ScopeBoxCreatorWebTool : WebToolBase, IWebToolCleanup, IWebConfirmable, IWebStepRefresh
    {
        private const string ModeFullHeight = "FullHeight";
        private const string ModePerLevel   = "PerLevel";

        private const string ToolId         = "scopeBoxes.creator";
        private const string DefaultPattern = "{BuildingLetter} - {LevelRange}";

        private static readonly TokenDefinition[] ScopeBoxComputedTokens =
        {
            new TokenDefinition("BuildingLetter", AppStrings.T("naming.computed.scopeBoxes.buildingLetter.label"), TokenOrigin.Computed, TokenSubject.Target, TokenEntity.Any),
            new TokenDefinition("LevelName",       AppStrings.T("naming.computed.scopeBoxes.levelName.label"),      TokenOrigin.Computed, TokenSubject.Target, TokenEntity.Any),
            new TokenDefinition("LevelRange",      AppStrings.T("naming.computed.scopeBoxes.levelRange.label"),     TokenOrigin.Computed, TokenSubject.Target, TokenEntity.Any),
            new TokenDefinition("ModelName",       AppStrings.T("naming.computed.scopeBoxes.modelName.label"),      TokenOrigin.Computed, TokenSubject.Target, TokenEntity.Any),
        };

        private const int MaxRoomsForLiveCount = 1500;

        private readonly List<ScopeBoxCreatorViewModel.DocEntry> _availableDocs;
        private readonly Dictionary<string, ScopeBoxCreatorViewModel.DocEntry> _docByLabel;

        private List<string>         _selectedDocLabels;
        private List<ScopeLevelInfo> _scannedLevels = new List<ScopeLevelInfo>();
        private List<RoomInfo>       _scannedRooms  = new List<RoomInfo>();
        private List<ScopeBoxEntry>  _scopeBoxes;
        private readonly Dictionary<string, ElementId> _levelKeyToId = new Dictionary<string, ElementId>(StringComparer.Ordinal);
        private List<ElementId>      _selectedLevelIds = new List<ElementId>();
        private string               _mode      = ModeFullHeight;
        private ElementId            _seedBoxId = ElementId.InvalidElementId;
        private bool                 _scanning  = false;
        private bool                 _scanDone  = false;
        private string?              _scanError;
        private string _namePattern = NamingPatternStore.Instance.GetOrDefault(ToolId, DefaultPattern);

        private readonly ScopeBoxCreatorScanHandler? _scanHandler;
        private readonly ExternalEvent?              _scanEvent;
        private readonly ScopeBoxCreatorRunHandler?  _runHandler;
        private readonly ExternalEvent?              _runEvent;

        public event Action<string>? StepInputsChanged;

        public ScopeBoxCreatorWebTool(
            ScopeBoxCreatorScanHandler? scanHandler, ExternalEvent? scanEvent,
            ScopeBoxCreatorRunHandler?  runHandler,  ExternalEvent? runEvent,
            List<ScopeBoxCreatorViewModel.DocEntry>? availableDocs,
            List<ScopeBoxEntry>?        initialScopeBoxes = null)
        {
            _scanHandler   = scanHandler;
            _scanEvent     = scanEvent;
            _runHandler    = runHandler;
            _runEvent      = runEvent;
            _availableDocs = availableDocs ?? new List<ScopeBoxCreatorViewModel.DocEntry>();
            _scopeBoxes    = initialScopeBoxes ?? new List<ScopeBoxEntry>();

            _docByLabel = new Dictionary<string, ScopeBoxCreatorViewModel.DocEntry>(StringComparer.Ordinal);
            foreach (var d in _availableDocs) _docByLabel[d.Label] = d;

            _selectedDocLabels = _availableDocs.Select(d => d.Label).ToList();
            if (_scopeBoxes.Count > 0) _seedBoxId = _scopeBoxes[0].Id;
        }

        public override string Title    => AppStrings.T("scopeBoxes.creator.title");
        public override string RunLabel => AppStrings.T("scopeBoxes.creator.runLabel");

        // ── Confirm-driven scans ──────────────────────────────────────────────

        public string? ConfirmLabelFor(string stepId) => null;

        public void OnStepConfirm(string stepId)
        {
            if (stepId == "S1")
            {
                if (!_scanDone && !_scanning) TriggerScan();
            }
            else if (stepId == "S2")
            {
                RefreshSeedList();
            }
        }

        private void ApplySelectedDocs()
        {
            _scanHandler!.IncludeHost = false;
            var linkInstIds = new List<ElementId>();
            bool includeHost = false;
            foreach (var label in _selectedDocLabels)
            {
                if (!_docByLabel.TryGetValue(label, out var entry)) continue;
                if (entry.IsHost) includeHost = true;
                else              linkInstIds.Add(entry.LinkInstId);
            }
            _scanHandler.IncludeHost = includeHost;
            _scanHandler.LinkInstIds = linkInstIds;
        }

        private void TriggerScan()
        {
            if (_scanHandler == null || _scanEvent == null) return;
            _scanning  = true;
            _scanError = null;

            ApplySelectedDocs();
            _scanHandler.BoxesOnly = false;

            // Callbacks land on the Revit thread; the window marshals StepInputsChanged.
            _scanHandler.OnScanComplete = (levels, rooms, boxes) =>
            {
                _scanning      = false;
                _scanDone      = true;
                _scannedLevels = levels ?? new List<ScopeLevelInfo>();
                _scannedRooms  = rooms  ?? new List<RoomInfo>();
                _scopeBoxes    = boxes  ?? new List<ScopeBoxEntry>();

                _selectedLevelIds = _scannedLevels
                    .Where(l => l.RoomCount > 0)
                    .Select(l => l.LevelId)
                    .ToList();
                if (_seedBoxId == ElementId.InvalidElementId && _scopeBoxes.Count > 0)
                    _seedBoxId = _scopeBoxes[0].Id;

                StepInputsChanged?.Invoke("S2");
                StepInputsChanged?.Invoke("S3");
                Fire();
            };

            _scanHandler.OnError = msg =>
            {
                _scanning  = false;
                _scanDone  = true;
                _scanError = msg;
                StepInputsChanged?.Invoke("S2");
                Fire();
            };

            _scanEvent.Raise();
        }

        // Fast seed-list refresh (BoxesOnly scan) — a box drawn while the window is open
        // shows up without reopening.
        private void RefreshSeedList()
        {
            if (_scanHandler == null || _scanEvent == null) return;

            ApplySelectedDocs();
            _scanHandler.BoxesOnly = true;
            _scanHandler.OnBoxesLoaded = boxes =>
            {
                var fresh = boxes ?? new List<ScopeBoxEntry>();
                bool same = fresh.Count == _scopeBoxes.Count
                    && fresh.Zip(_scopeBoxes, (a, b) => a.Id.Value == b.Id.Value && a.Name == b.Name)
                        .All(eq => eq);
                if (same) return;

                _scopeBoxes = fresh;
                if (_scopeBoxes.All(b => b.Id.Value != _seedBoxId.Value))
                    _seedBoxId = _scopeBoxes.Count > 0 ? _scopeBoxes[0].Id : ElementId.InvalidElementId;
                StepInputsChanged?.Invoke("S3");
                Fire();
            };
            _scanEvent.Raise();
        }

        // ── Steps ─────────────────────────────────────────────────────────────

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var s = ScopeBoxSettings.Instance;
            var s1 = new WebStep("S1", AppStrings.T("scopeBoxes.creator.steps.S1"))
                .Add(WebInput.MultiSelectTabs("docs", "",
                    new Dictionary<string, List<string>> { ["Documents"] = _availableDocs.Select(d => d.Label).ToList() },
                    _selectedDocLabels))
                .Add(WebInput.Stepper("bufferXY", AppStrings.T("scopeBoxes.creator.labels.bufferXY"),
                    s.BufferXY, 0, 200, 1, 0))
                .Add(WebInput.Hint("bufferXYHint", AppStrings.T("scopeBoxes.creator.labels.bufferXYHint")))
                .Add(WebInput.Stepper("clusterThreshold", AppStrings.T("scopeBoxes.creator.labels.clusterThreshold"),
                    s.ClusterThreshold, 0, 500, 1, 0))
                .Add(WebInput.Hint("clusterThresholdHint", AppStrings.T("scopeBoxes.creator.labels.clusterThresholdHint")))
                .Add(WebInput.Stepper("topLevelHeight", AppStrings.T("scopeBoxes.creator.labels.topLevelHeight"),
                    s.TopLevelHeight, 1, 100, 1, 0))
                .Add(WebInput.Hint("topLevelHeightHint", AppStrings.T("scopeBoxes.creator.labels.topLevelHeightHint")))
                .Add(WebInput.Stepper("perLevelEndExtension", AppStrings.T("scopeBoxes.creator.labels.perLevelEndExtension"),
                    s.PerLevelEndExtension, 0, 500, 5, 0))
                .Add(WebInput.Hint("perLevelEndExtensionHint", AppStrings.T("scopeBoxes.creator.labels.perLevelEndExtensionHint")));

            var s2 = new WebStep("S2", AppStrings.T("scopeBoxes.creator.steps.S2"));
            BuildS2Inputs(s2);

            var s3 = new WebStep("S3", AppStrings.T("scopeBoxes.creator.steps.S3"));
            BuildS3Inputs(s3);

            int? planned = ComputePlannedCount();
            var seed = _scopeBoxes.FirstOrDefault(b => b.Id.Value == _seedBoxId.Value);
            var s4 = new WebStep("S4", AppStrings.T("scopeBoxes.creator.steps.S4"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("scopeBoxes.creator.review.itemDocs"),
                     _selectedDocLabels.Count > 0 ? AppStrings.T("scopeBoxes.creator.review.docsValue", _selectedDocLabels.Count) : "-"),
                    (AppStrings.T("scopeBoxes.creator.review.itemLevels"),
                     _selectedLevelIds.Count > 0 ? AppStrings.T("scopeBoxes.creator.review.levelsValue", _selectedLevelIds.Count) : "-"),
                    (AppStrings.T("scopeBoxes.creator.review.itemMode"),
                     _mode == ModePerLevel
                        ? AppStrings.T("scopeBoxes.creator.labels.modePerLevel")
                        : AppStrings.T("scopeBoxes.creator.labels.modeFullHeight")),
                    (AppStrings.T("scopeBoxes.creator.review.itemSeed"), seed?.Name ?? "-"),
                    (AppStrings.T("scopeBoxes.creator.review.itemCount"),
                     planned.HasValue ? planned.Value.ToString() : AppStrings.T("scopeBoxes.creator.review.countDeferred")),
                },
                note: AppStrings.T("scopeBoxes.creator.review.note"),
                warning: _seedBoxId == ElementId.InvalidElementId
                    ? AppStrings.T("scopeBoxes.creator.review.warnNoSeed")
                    : null));

            return new List<WebStep> { s1, s2, s3, s4 };
        }

        private void BuildS2Inputs(WebStep s2)
        {
            if (_scanError != null)
            {
                s2.Add(WebInput.Warn("scanError", AppStrings.T("scopeBoxes.creator.labels.scanError", _scanError)));
                return;
            }
            if (!_scanDone)
            {
                s2.Add(WebInput.Hint("scanning", AppStrings.T("scopeBoxes.creator.labels.scanning")));
                return;
            }
            if (_scannedLevels.Count == 0)
            {
                s2.Add(WebInput.Hint("noLevels", AppStrings.T("scopeBoxes.creator.labels.noLevels")));
                return;
            }

            s2.Add(WebInput.SingleSelect("mode", AppStrings.T("scopeBoxes.creator.labels.modeHeader"),
                _mode, new[]
                {
                    new WebOption(ModeFullHeight, AppStrings.T("scopeBoxes.creator.labels.modeFullHeight")),
                    new WebOption(ModePerLevel,   AppStrings.T("scopeBoxes.creator.labels.modePerLevel")),
                }));
            s2.Add(WebInput.Hint("modeHint", AppStrings.T("scopeBoxes.creator.labels.modeHint")));

            _levelKeyToId.Clear();
            var levelKeys = _scannedLevels
                .OrderBy(l => l.ElevationInternal)
                .Select(l =>
                {
                    string key = l.RoomCount > 0
                        ? AppStrings.T("scopeBoxes.creator.labels.levelWithRooms", l.Name, l.ElevationFt, l.RoomCount)
                        : AppStrings.T("scopeBoxes.creator.labels.levelNoRooms",  l.Name, l.ElevationFt);
                    _levelKeyToId[key] = l.LevelId;
                    return key;
                })
                .ToList();

            var selectedIdSet = new HashSet<long>(_selectedLevelIds.Select(id => id.Value));
            var preSelected = _levelKeyToId
                .Where(kv => selectedIdSet.Contains(kv.Value.Value))
                .Select(kv => kv.Key)
                .ToList();

            s2.Add(WebInput.MultiSelectTabs("levels", "",
                new Dictionary<string, List<string>> { ["Levels"] = levelKeys }, preSelected));
        }

        private void BuildS3Inputs(WebStep s3)
        {
            if (_scopeBoxes.Count == 0)
            {
                s3.Add(WebInput.Hint("seedNone", AppStrings.T("scopeBoxes.creator.labels.seedNone")));
            }
            else
            {
                var seed = _scopeBoxes.FirstOrDefault(b => b.Id.Value == _seedBoxId.Value) ?? _scopeBoxes[0];
                s3.Add(WebInput.SingleSelect("seed", AppStrings.T("scopeBoxes.creator.labels.seedHeader"),
                    seed.Id.Value.ToString(),
                    _scopeBoxes.Select(b => new WebOption(b.Id.Value.ToString(), b.Name))));
                s3.Add(WebInput.Hint("seedSize", AppStrings.T("scopeBoxes.creator.labels.seedSize",
                    seed.WidthFt.ToString("0.#"), seed.DepthFt.ToString("0.#"), seed.HeightFt.ToString("0.#"))));
            }
            s3.Add(WebInput.Hint("seedHint", AppStrings.T("scopeBoxes.creator.labels.seedHint")));

            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.Any, hasSource: false, ScopeBoxComputedTokens);
            var selLevels = _scannedLevels
                .Where(l => _selectedLevelIds.Any(id => id.Value == l.LevelId.Value))
                .OrderBy(l => l.ElevationInternal)
                .ToList();
            string first = selLevels.FirstOrDefault()?.Name ?? "L01";
            string last  = selLevels.LastOrDefault()?.Name  ?? "L05";
            var ctx = new TokenContext();
            ctx.Computed["BuildingLetter"] = "A";
            ctx.Computed["LevelName"]      = first;
            ctx.Computed["LevelRange"]     = _mode == ModePerLevel || first == last
                ? first
                : AppStrings.T("scopeBoxes.creator.tokens.levelRange", first, last);
            ctx.Computed["ModelName"] = _scannedRooms
                .GroupBy(r => r.DocName, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key ?? "Architecture";

            s3.Add(WebInput.TokenInput("pattern", AppStrings.T("scopeBoxes.creator.labels.namingHeader"),
                _namePattern, DefaultPattern, tokens, SampleFor(tokens, ctx)));
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "docs":
                    _selectedDocLabels = StrList(value);
                    // Scan is stale for the new document set.
                    _scannedLevels.Clear();
                    _scannedRooms.Clear();
                    _selectedLevelIds.Clear();
                    _levelKeyToId.Clear();
                    _scanDone = false;
                    _scanning = false;
                    Fire();
                    break;
                case "bufferXY":             ScopeBoxSettings.Instance.BufferXY             = AsDouble(value, ScopeBoxSettings.Instance.BufferXY);             ScopeBoxSettings.Instance.Save(); Fire(); break;
                case "clusterThreshold":     ScopeBoxSettings.Instance.ClusterThreshold     = AsDouble(value, ScopeBoxSettings.Instance.ClusterThreshold);     ScopeBoxSettings.Instance.Save(); Fire(); break;
                case "topLevelHeight":       ScopeBoxSettings.Instance.TopLevelHeight       = AsDouble(value, ScopeBoxSettings.Instance.TopLevelHeight);       ScopeBoxSettings.Instance.Save(); break;
                case "perLevelEndExtension": ScopeBoxSettings.Instance.PerLevelEndExtension = AsDouble(value, ScopeBoxSettings.Instance.PerLevelEndExtension); ScopeBoxSettings.Instance.Save(); break;
                case "mode":
                {
                    var m = AsString(value) == ModePerLevel ? ModePerLevel : ModeFullHeight;
                    if (m != _mode) { _mode = m; Fire(); }
                    break;
                }
                case "levels":
                    _selectedLevelIds = StrList(value)
                        .Where(_levelKeyToId.ContainsKey)
                        .Select(k => _levelKeyToId[k])
                        .GroupBy(id => id.Value)
                        .Select(g => g.First())
                        .ToList();
                    Fire();
                    break;
                case "seed":
                {
                    long id = long.TryParse(AsString(value), out var v) ? v : 0;
                    var entry = _scopeBoxes.FirstOrDefault(b => b.Id.Value == id);
                    _seedBoxId = entry?.Id ?? ElementId.InvalidElementId;
                    StepInputsChanged?.Invoke("S3"); // refresh the seed-size line
                    Fire();
                    break;
                }
                case "pattern":
                    _namePattern = AsString(value, _namePattern);
                    NamingPatternStore.Instance.Set(ToolId, _namePattern);
                    Fire();
                    break;
            }
        }

        private int? ComputePlannedCount()
        {
            if (!_scanDone || _scannedRooms.Count == 0 || _selectedLevelIds.Count == 0) return 0;
            if (_scannedRooms.Count > MaxRoomsForLiveCount) return null;

            var selNames = new HashSet<string>(
                _scannedLevels.Where(l => _selectedLevelIds.Any(id => id.Value == l.LevelId.Value))
                              .Select(l => l.Name),
                StringComparer.Ordinal);

            double threshold = ScopeBoxSettings.Instance.ClusterThreshold;

            if (_mode == ModePerLevel)
            {
                int total = 0;
                foreach (var name in selNames)
                {
                    var lr = _scannedRooms.Where(r => r.LevelName == name).ToList();
                    if (lr.Count == 0) continue;
                    total += RoomClusterSearch.ClusterRooms(lr, threshold).Count;
                }
                return total;
            }

            var rooms = _scannedRooms.Where(r => selNames.Contains(r.LevelName)).ToList();
            if (rooms.Count == 0) return 0;
            return RoomClusterSearch.ClusterRooms(rooms, threshold).Count;
        }

        public override bool IsStepValid(string stepId)
        {
            if (stepId == "S1") return _selectedDocLabels.Count > 0;
            if (stepId == "S2") return _scanDone && _selectedLevelIds.Count > 0;
            if (stepId == "S3") return _seedBoxId != ElementId.InvalidElementId;
            return true;
        }

        public override bool CanRun() =>
            _selectedDocLabels.Count > 0 && _scanDone && _selectedLevelIds.Count > 0
            && _seedBoxId != ElementId.InvalidElementId;

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1") return _selectedDocLabels.Count > 0
                ? AppStrings.T("scopeBoxes.creator.summaries.docsCount", _selectedDocLabels.Count) : "-";
            if (stepId == "S2")
            {
                if (!_scanDone) return "-";
                string mode = _mode == ModePerLevel
                    ? AppStrings.T("scopeBoxes.creator.labels.modePerLevel")
                    : AppStrings.T("scopeBoxes.creator.labels.modeFullHeight");
                return AppStrings.T("scopeBoxes.creator.summaries.s2", _selectedLevelIds.Count, mode);
            }
            if (stepId == "S3")
            {
                var seed = _scopeBoxes.FirstOrDefault(b => b.Id.Value == _seedBoxId.Value);
                return seed?.Name ?? AppStrings.T("scopeBoxes.creator.summaries.s3NoSeed");
            }
            if (stepId == "S4") return AppStrings.T("scopeBoxes.creator.summaries.S4");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            bool includeHost = false;
            var  linkInstIds = new List<ElementId>();
            foreach (var label in _selectedDocLabels)
            {
                if (!_docByLabel.TryGetValue(label, out var entry)) continue;
                if (entry.IsHost) includeHost = true;
                else              linkInstIds.Add(entry.LinkInstId);
            }

            _runHandler!.IncludeHost     = includeHost;
            _runHandler.LinkInstIds      = linkInstIds;
            _runHandler.SelectedLevelIds = new List<ElementId>(
                _selectedLevelIds.GroupBy(id => id.Value).Select(g => g.First()));
            _runHandler.Mode        = _mode;
            _runHandler.SeedBoxId   = _seedBoxId;
            _runHandler.NamePattern = _namePattern;
            _runHandler.PushLog     = pushLog;
            _runHandler.OnProgress  = onProgress;
            _runHandler.OnComplete  = onComplete;

            pushLog(AppStrings.T("scopeBoxes.creator.log.raising"), "info");
            _runEvent!.Raise();
        }

        public void OnWindowClosed()
        {
            if (_scanHandler != null)
            {
                _scanHandler.OnScanComplete = null;
                _scanHandler.OnBoxesLoaded  = null;
                _scanHandler.OnError        = null;
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
