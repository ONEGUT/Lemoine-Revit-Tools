using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.Dimensioning
{
    /// <summary>Web port of <see cref="ClashFinderViewModel"/> — detect clashes, place markers,
    /// optionally dimension to grids/slab edge. Same handler and AppStrings keys. The slab-edge
    /// pick buttons run through IWebToolAction; the picked-floor status line and the
    /// destination-conditional sections rebuild S4 via IWebStepRefresh. (The WPF window
    /// re-activated itself after a pick; the web window has no activate hook yet — logged.)</summary>
    public class ClashFinderWebTool : WebToolBase, IWebToolCleanup, IWebStepRefresh, IWebToolAction
    {
        private const double MmPerInch = 25.4;

        private readonly ClashFinderEventHandler? _handler;
        private readonly ExternalEvent?           _event;
        private readonly AutoDimension.SlabPickEventHandler? _slabPickHandler;
        private readonly ExternalEvent?                      _slabPickEvent;

        private AutoDimension.Resolvers.SlabScope? _pickedSlab;
        private string _pickedSlabName = "";

        private readonly List<long> _allViewIds;
        private readonly Dictionary<string, ClashDefinition> _defDisplayToDef = new Dictionary<string, ClashDefinition>();
        private readonly BrowserTree _browserTree;

        private List<string> _selectedDefDisplays = new List<string>();
        private List<long>   _selectedViewIds     = new List<long>();

        private bool _clearPrevious     = true;
        private bool _runDimensionPass  = true;
        private bool _adoptUserCallouts = true;
        private double _roundSizeMm     = 0.0;

        private static readonly (int Denom, string Label)[] RevitScales =
        {
            (2400, "1\" = 200'-0\""),
            (1200, "1\" = 100'-0\""),
            (720,  "1\" = 60'-0\""),
            (600,  "1\" = 50'-0\""),
            (480,  "1\" = 40'-0\""),
            (384,  "1/32\" = 1'-0\""),
            (360,  "1\" = 30'-0\""),
            (240,  "1\" = 20'-0\""),
            (192,  "1/16\" = 1'-0\""),
            (128,  "3/32\" = 1'-0\""),
            (120,  "1\" = 10'-0\""),
            (96,   "1/8\" = 1'-0\""),
            (64,   "3/16\" = 1'-0\""),
            (48,   "1/4\" = 1'-0\""),
            (32,   "3/8\" = 1'-0\""),
            (24,   "1/2\" = 1'-0\""),
            (16,   "3/4\" = 1'-0\""),
            (12,   "1\" = 1'-0\""),
            (8,    "1 1/2\" = 1'-0\""),
            (4,    "3\" = 1'-0\""),
            (2,    "6\" = 1'-0\""),
            (1,    "12\" = 1'-0\""),
        };
        private int _maxCalloutScale = AutoDimension.AutoDimensionConfig.Instance.MaxCalloutScale;

        private static string ArchScaleLabel(int denom)
        {
            foreach (var s in RevitScales) if (s.Denom == denom) return s.Label;
            return $"1:{denom}";
        }

        private string _dimTargetType =
            string.Equals(AutoDimension.AutoDimensionConfig.Instance.TargetType, "SlabEdge", StringComparison.OrdinalIgnoreCase) ? "SlabEdge"
          : string.Equals(AutoDimension.AutoDimensionConfig.Instance.TargetType, "ManualDatum", StringComparison.OrdinalIgnoreCase) ? "ManualDatum"
          : "Grid";

        public event Action<string>? StepInputsChanged;

        public ClashFinderWebTool(
            ClashFinderEventHandler? handler,
            ExternalEvent?           externalEvent,
            List<View>               allViews,
            List<ClashDefinition>    definitions,
            AutoDimension.SlabPickEventHandler? slabPickHandler = null,
            ExternalEvent?                      slabPickEvent   = null,
            BrowserTree?                        browserTree     = null)
        {
            _handler         = handler;
            _event           = externalEvent;
            _slabPickHandler = slabPickHandler;
            _slabPickEvent   = slabPickEvent;
            _allViewIds      = (allViews ?? new List<View>()).Select(v => v.Id.Value).ToList();
            _browserTree     = browserTree ?? new BrowserTree();

            var used = new HashSet<string>();
            foreach (var def in definitions ?? new List<ClashDefinition>())
            {
                string baseName = string.IsNullOrWhiteSpace(def.Name) ? AppStrings.T("clash.finder.labels.unnamed") : def.Name;
                string display  = baseName;
                int n = 2;
                while (!used.Add(display)) display = $"{baseName} ({n++})";
                _defDisplayToDef[display] = def;
            }
        }

        public override string Title    => AppStrings.T("clash.finder.title");
        public override string RunLabel => AppStrings.T("clash.finder.runLabel");

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var s1 = new WebStep("S1", AppStrings.T("clash.finder.steps.S1"));
            if (_defDisplayToDef.Count == 0)
                s1.Add(WebInput.Hint("noDefs", AppStrings.T("clash.finder.labels.noDefs")));
            else
                s1.Add(WebInput.MultiSelectTabs("defs", AppStrings.T("clash.finder.labels.pickDefs"),
                    new Dictionary<string, List<string>>
                    {
                        [AppStrings.T("clash.finder.labels.groupSaved")] = _defDisplayToDef.Keys.ToList(),
                    },
                    _selectedDefDisplays));

            var s2 = new WebStep("S2", AppStrings.T("clash.finder.steps.S2"))
                .Add(WebInput.BrowserTree("views", AppStrings.T("clash.finder.labels.pickerName"),
                    PruneTree(_browserTree, new HashSet<long>(_allViewIds)),
                    _selectedViewIds));

            var s3 = new WebStep("S3", AppStrings.T("clash.finder.steps.S3"), required: false)
                .Add(WebInput.Toggle("clear", AppStrings.T("clash.finder.labels.clearLabel"), _clearPrevious))
                .Add(WebInput.Hint("clearDesc", AppStrings.T("clash.finder.labels.clearDesc")))
                .Add(WebInput.Stepper("oversize", AppStrings.T("clash.finder.labels.oversizeLabel"),
                    _roundSizeMm / MmPerInch, 0, 40, 0.25, 2))
                .Add(WebInput.Hint("oversizeHint", AppStrings.T("clash.finder.labels.oversizeHint")))
                .Add(WebInput.Hint("storeyNote", AppStrings.T("clash.finder.labels.storeyNote")));

            var s4 = new WebStep("S4", AppStrings.T("clash.finder.steps.S4"), required: false)
                .Add(WebInput.Hint("dimDefaults", AppStrings.T("clash.finder.labels.dimDefaults")))
                .Add(WebInput.Toggle("dimPass", AppStrings.T("clash.finder.labels.dimPassLabel"), _runDimensionPass))
                .Add(WebInput.Hint("dimPassDesc", AppStrings.T("clash.finder.labels.dimPassDesc")))
                .Add(WebInput.Toggle("userCallouts", AppStrings.T("clash.finder.labels.userCalloutsLabel"), _adoptUserCallouts))
                .Add(WebInput.Hint("userCalloutsDesc", AppStrings.T("clash.finder.labels.userCalloutsDesc")))
                .Add(WebInput.Hint("calloutScaleHelp", AppStrings.T("clash.finder.labels.calloutScaleHelp")))
                .Add(WebInput.SingleSelect("calloutScale", AppStrings.T("clash.finder.labels.calloutScaleLabel"),
                    ArchScaleLabel(_maxCalloutScale),
                    RevitScales.Select(s => new WebOption(s.Label, s.Label))))
                .Add(WebInput.Hint("destHelp", AppStrings.T("clash.finder.labels.destHelp")))
                .Add(WebInput.SingleSelect("dest", AppStrings.T("clash.finder.labels.destLabel"),
                    _dimTargetType, new[]
                    {
                        new WebOption("Grid",        AppStrings.T("clash.finder.labels.destGrid")),
                        new WebOption("SlabEdge",    AppStrings.T("clash.finder.labels.destSlab")),
                        new WebOption("ManualDatum", AppStrings.T("clash.finder.labels.destManual")),
                    }));

            if (_dimTargetType == "ManualDatum")
                s4.Add(WebInput.Hint("manualNote", AppStrings.T("clash.finder.labels.manualNote")));

            if (_dimTargetType == "SlabEdge")
            {
                s4.Add(WebInput.Hint("slabHelp", AppStrings.T("clash.finder.labels.slabHelp")));
                s4.Add(WebInput.Button("pickHost",   AppStrings.T("clash.finder.labels.pickHost"),   variant: "primary"));
                s4.Add(WebInput.Button("pickLinked", AppStrings.T("clash.finder.labels.pickLinked"), variant: "primary"));
                s4.Add(WebInput.Button("clearSlab",  AppStrings.T("clash.finder.labels.clear"),      variant: "ghost"));
                s4.Add(WebInput.Hint("slabStatus", _pickedSlab == null
                    ? AppStrings.T("clash.finder.labels.slabDefault")
                    : AppStrings.T("clash.finder.labels.slabOverride", _pickedSlabName)));
            }

            var chips = new List<(string, string)>
            {
                (AppStrings.T("clash.finder.review.itemDefs"),
                 _selectedDefDisplays.Count > 0 ? AppStrings.T("clash.finder.review.defsValue", _selectedDefDisplays.Count) : "-"),
                (AppStrings.T("clash.finder.review.itemViews"),
                 _selectedViewIds.Count > 0 ? AppStrings.T("clash.finder.review.viewsValue", _selectedViewIds.Count) : "-"),
                (AppStrings.T("clash.finder.review.itemMarker"),
                 _roundSizeMm > 0 ? AppStrings.T("clash.finder.review.markerOversize", _roundSizeMm / MmPerInch) : AppStrings.T("clash.finder.review.markerExact")),
                (AppStrings.T("clash.finder.review.itemDim"),
                 _runDimensionPass
                    ? (_dimTargetType == "SlabEdge" ? AppStrings.T("clash.finder.words.slabEdge") : _dimTargetType == "ManualDatum" ? AppStrings.T("clash.finder.words.pickedEdge") : AppStrings.T("clash.finder.words.grid"))
                    : AppStrings.T("clash.finder.review.dimOff")),
            };
            if (_clearPrevious) chips.Add((AppStrings.T("clash.finder.review.chipClear"), "✓"));
            if (_runDimensionPass && _adoptUserCallouts) chips.Add((AppStrings.T("clash.finder.review.chipMyCallouts"), "✓"));
            if (_runDimensionPass && _dimTargetType == "SlabEdge")
                chips.Add((_pickedSlab != null ? AppStrings.T("clash.finder.review.chipSlab", _pickedSlabName) : AppStrings.T("clash.finder.review.chipSlabClashed"), "✓"));
            if (_runDimensionPass) chips.Add((AppStrings.T("clash.finder.review.chipCalloutsCap", ArchScaleLabel(_maxCalloutScale)), "✓"));

            var s5 = new WebStep("S5", AppStrings.T("clash.finder.steps.S5"), required: false)
                .Add(WebInput.Review("review", chips.ToArray(),
                    note: AppStrings.T("clash.finder.review.note"),
                    warning: _runDimensionPass && _dimTargetType == "ManualDatum"
                        ? AppStrings.T("clash.finder.review.warnManual")
                        : null));

            return new List<WebStep> { s1, s2, s3, s4, s5 };
        }

        // ── Slab pick buttons ─────────────────────────────────────────────────

        public void OnToolAction(string stepId, string inputId)
        {
            switch (inputId)
            {
                case "pickHost":   StartSlabPick(inLinks: false); break;
                case "pickLinked": StartSlabPick(inLinks: true);  break;
                case "clearSlab":
                    _pickedSlab = null;
                    _pickedSlabName = "";
                    StepInputsChanged?.Invoke("S4");
                    Fire();
                    break;
            }
        }

        // Raises the slab-pick external event; the picked floor comes back on Revit's main
        // thread — the window's StepInputsChanged handler marshals to the UI dispatcher.
        private void StartSlabPick(bool inLinks)
        {
            if (_slabPickHandler == null || _slabPickEvent == null) return;
            _slabPickHandler.InLinks = inLinks;
            _slabPickHandler.OnPicked = (scope, name) =>
            {
                if (scope == null) return;   // cancelled / not a floor — keep the prior choice
                _pickedSlab = scope;
                _pickedSlabName = name;
                StepInputsChanged?.Invoke("S4");
                Fire();
            };
            _slabPickEvent.Raise();
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "defs":     _selectedDefDisplays = StrList(value); Fire(); break;
                case "views":    _selectedViewIds     = IdList(value);  Fire(); break;
                case "clear":    _clearPrevious       = AsBool(value, _clearPrevious); Fire(); break;
                case "oversize": _roundSizeMm         = AsDouble(value, _roundSizeMm / MmPerInch) * MmPerInch; Fire(); break;
                case "dimPass":      _runDimensionPass  = AsBool(value, _runDimensionPass);  Fire(); break;
                case "userCallouts": _adoptUserCallouts = AsBool(value, _adoptUserCallouts); Fire(); break;
                case "calloutScale":
                {
                    var sel = AsString(value);
                    foreach (var s in RevitScales)
                        if (s.Label == sel) { _maxCalloutScale = s.Denom; break; }
                    Fire(); break;
                }
                case "dest":
                {
                    var d = AsString(value);
                    d = d == "SlabEdge" ? "SlabEdge" : d == "ManualDatum" ? "ManualDatum" : "Grid";
                    if (d != _dimTargetType)
                    {
                        _dimTargetType = d;
                        StepInputsChanged?.Invoke("S4"); // destination-conditional sections
                        Fire();
                    }
                    break;
                }
            }
        }

        public override bool IsStepValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedDefDisplays.Count > 0;
                case "S2": return _selectedViewIds.Count > 0;
                default:   return true;
            }
        }

        public override bool CanRun() => _selectedDefDisplays.Count > 0 && _selectedViewIds.Count > 0;

        public override string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedDefDisplays.Count == 0 ? "-" : AppStrings.T("clash.finder.summaries.defCount", _selectedDefDisplays.Count);
                case "S2": return _selectedViewIds.Count     == 0 ? "-" : AppStrings.T("clash.finder.summaries.viewCount", _selectedViewIds.Count);
                case "S3":
                {
                    var bits = new List<string>();
                    if (_clearPrevious) bits.Add(AppStrings.T("clash.finder.summaries.clear"));
                    bits.Add(_roundSizeMm > 0 ? AppStrings.T("clash.finder.summaries.oversize", _roundSizeMm / MmPerInch) : AppStrings.T("clash.finder.summaries.exactSize"));
                    return string.Join(" · ", bits);
                }
                case "S4":
                    return _runDimensionPass
                        ? AppStrings.T("clash.finder.summaries.dimTo", _dimTargetType == "SlabEdge" ? AppStrings.T("clash.finder.words.slabEdge") : _dimTargetType == "ManualDatum" ? AppStrings.T("clash.finder.words.pickedEdge") : AppStrings.T("clash.finder.words.grid"))
                          + (_adoptUserCallouts ? AppStrings.T("clash.finder.summaries.myCallouts") : "")
                        : AppStrings.T("clash.finder.summaries.dimOff");
                case "S5": return AppStrings.T("clash.finder.summaries.S5");
                default: return "-";
            }
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_handler == null || _event == null) return;

            _handler.Definitions = _selectedDefDisplays
                .Where(d => _defDisplayToDef.ContainsKey(d))
                .Select(d => _defDisplayToDef[d])
                .ToList();
            _handler.ViewIds = _selectedViewIds
                .Select(id => new ElementId(id))
                .ToList();
            _handler.ClearPrevious     = _clearPrevious;
            _handler.RunDimensionPass  = _runDimensionPass;
            _handler.AdoptUserCallouts = _adoptUserCallouts;
            _handler.DimTargetType     = _dimTargetType;
            _handler.MaxCalloutScale   = _maxCalloutScale;
            _handler.RoundSizeMm       = _roundSizeMm;
            _handler.SlabScopes = _pickedSlab != null
                ? new List<AutoDimension.Resolvers.SlabScope> { _pickedSlab }
                : new List<AutoDimension.Resolvers.SlabScope>();
            _handler.PushLog    = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;

            _event.Raise();
        }

        public void OnWindowClosed()
        {
            if (_slabPickHandler != null) _slabPickHandler.OnPicked = null;
            if (_handler == null) return;
            _handler.PushLog       = null;
            _handler.OnProgress    = null;
            _handler.OnComplete    = null;
            _handler.OnResultChips = null;
        }
    }
}
