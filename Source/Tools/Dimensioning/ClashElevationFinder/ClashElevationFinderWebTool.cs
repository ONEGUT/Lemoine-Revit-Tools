using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.Dimensioning
{
    /// <summary>Web port of <see cref="ClashElevationFinderViewModel"/> — detect clashes in
    /// section/elevation views, place round markers, tag with spot elevations. Same handler
    /// and AppStrings keys. Marker oversize edited in inches, stored in millimetres.</summary>
    public class ClashElevationFinderWebTool : WebToolBase, IWebToolCleanup
    {
        private const double MmPerInch = 25.4;

        private readonly ClashElevationFinderEventHandler? _handler;
        private readonly ExternalEvent?                    _event;
        private readonly List<long> _allViewIds;
        private readonly Dictionary<string, ClashDefinition> _defDisplayToDef = new Dictionary<string, ClashDefinition>();
        private readonly List<(string Name, ElementId Id)> _spotTypes;
        private readonly Dictionary<string, ElementId> _spotTypeByName = new Dictionary<string, ElementId>();
        private readonly BrowserTree _browserTree;

        private List<string> _selectedDefDisplays = new List<string>();
        private List<long>   _selectedViewIds     = new List<long>();

        private bool      _clearPrevious = true;
        private double    _roundSizeMm   = 0.0;      // round marker oversize; 0 = exact element size
        private string    _anchorMode    = "Centre"; // "Top" | "Centre" | "Bottom"
        private ElementId _spotTypeId    = ElementId.InvalidElementId;

        public ClashElevationFinderWebTool(
            ClashElevationFinderEventHandler? handler,
            ExternalEvent?                    externalEvent,
            List<View>                        allViews,
            List<ClashDefinition>             definitions,
            List<(string Name, ElementId Id)> spotTypes,
            BrowserTree?                      browserTree = null)
        {
            _handler     = handler;
            _event       = externalEvent;
            _allViewIds  = (allViews ?? new List<View>()).Select(v => v.Id.Value).ToList();
            _spotTypes   = spotTypes ?? new List<(string, ElementId)>();
            _browserTree = browserTree ?? new BrowserTree();

            var used = new HashSet<string>();
            foreach (var def in definitions ?? new List<ClashDefinition>())
            {
                string baseName = string.IsNullOrWhiteSpace(def.Name) ? AppStrings.T("clash.elevationFinder.labels.unnamed") : def.Name;
                string display  = baseName;
                int n = 2;
                while (!used.Add(display)) display = $"{baseName} ({n++})";
                _defDisplayToDef[display] = def;
            }

            foreach (var t in _spotTypes)
                if (!_spotTypeByName.ContainsKey(t.Name))
                    _spotTypeByName[t.Name] = t.Id;

            if (_spotTypes.Count > 0) _spotTypeId = _spotTypes[0].Id;
        }

        public override string Title    => AppStrings.T("clash.elevationFinder.title");
        public override string RunLabel => AppStrings.T("clash.elevationFinder.runLabel");

        private string AnchorWord => _anchorMode == "Top" ? AppStrings.T("clash.elevationFinder.words.top")
            : _anchorMode == "Bottom" ? AppStrings.T("clash.elevationFinder.words.bottom")
            : AppStrings.T("clash.elevationFinder.words.center");

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var s1 = new WebStep("S1", AppStrings.T("clash.elevationFinder.steps.S1"));
            if (_defDisplayToDef.Count == 0)
                s1.Add(WebInput.Hint("noDefs", AppStrings.T("clash.elevationFinder.labels.noDefs")));
            else
                s1.Add(WebInput.MultiSelectTabs("defs", AppStrings.T("clash.elevationFinder.labels.pickDefs"),
                    new Dictionary<string, List<string>>
                    {
                        [AppStrings.T("clash.elevationFinder.labels.groupSaved")] = _defDisplayToDef.Keys.ToList(),
                    },
                    _selectedDefDisplays));

            var s2 = new WebStep("S2", AppStrings.T("clash.elevationFinder.steps.S2"))
                .Add(WebInput.BrowserTree("views", AppStrings.T("clash.elevationFinder.labels.pickViews"),
                    PruneTree(_browserTree, new HashSet<long>(_allViewIds)),
                    _selectedViewIds));

            var s3 = new WebStep("S3", AppStrings.T("clash.elevationFinder.steps.S3"), required: false)
                .Add(WebInput.Toggle("clear", AppStrings.T("clash.elevationFinder.labels.clearLabel"), _clearPrevious))
                .Add(WebInput.Hint("clearDesc", AppStrings.T("clash.elevationFinder.labels.clearDesc")))
                .Add(WebInput.Stepper("oversize", AppStrings.T("clash.elevationFinder.labels.oversizeLabel"),
                    _roundSizeMm / MmPerInch, 0, 40, 0.25, 2))
                .Add(WebInput.Hint("oversizeHint", AppStrings.T("clash.elevationFinder.labels.oversizeHint")))
                .Add(WebInput.SingleSelect("anchor", AppStrings.T("clash.elevationFinder.labels.tagPosLabel"),
                    _anchorMode, new[]
                    {
                        new WebOption("Top",    AppStrings.T("clash.elevationFinder.labels.anchorTop")),
                        new WebOption("Centre", AppStrings.T("clash.elevationFinder.labels.anchorCenter")),
                        new WebOption("Bottom", AppStrings.T("clash.elevationFinder.labels.anchorBottom")),
                    }));
            if (_spotTypes.Count == 0)
                s3.Add(WebInput.Hint("noSpotType", AppStrings.T("clash.elevationFinder.labels.noSpotType")));
            else
                s3.Add(WebInput.SingleSelect("spotType", AppStrings.T("clash.elevationFinder.labels.spotTypeLabel"),
                    _spotTypes.FirstOrDefault(t => t.Id == _spotTypeId).Name ?? _spotTypes[0].Name,
                    _spotTypes.Select(t => new WebOption(t.Name, t.Name))));

            var s4 = new WebStep("S4", AppStrings.T("clash.elevationFinder.steps.S4"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("clash.elevationFinder.review.itemDefs"),
                     _selectedDefDisplays.Count > 0 ? AppStrings.T("clash.elevationFinder.review.defsValue", _selectedDefDisplays.Count) : "-"),
                    (AppStrings.T("clash.elevationFinder.review.itemViews"),
                     _selectedViewIds.Count > 0 ? AppStrings.T("clash.elevationFinder.review.viewsValue", _selectedViewIds.Count) : "-"),
                    (AppStrings.T("clash.elevationFinder.review.itemMarker"),
                     _roundSizeMm > 0 ? AppStrings.T("clash.elevationFinder.review.markerOversize", _roundSizeMm / MmPerInch) : AppStrings.T("clash.elevationFinder.review.markerExact")),
                    (AppStrings.T("clash.elevationFinder.review.itemTag"),
                     AnchorWord + (_spotTypes.Count > 0
                        ? $" · {_spotTypes.FirstOrDefault(t => t.Id == _spotTypeId).Name ?? _spotTypes[0].Name}"
                        : AppStrings.T("clash.elevationFinder.review.tagNoSpot"))),
                },
                note: AppStrings.T("clash.elevationFinder.review.note"),
                warning: _spotTypes.Count == 0 ? AppStrings.T("clash.elevationFinder.review.warnNoSpot") : null));
            if (_clearPrevious)
                s4.Add(WebInput.Hint("chipClear", AppStrings.T("clash.elevationFinder.review.chipClear")));

            return new List<WebStep> { s1, s2, s3, s4 };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "defs":     _selectedDefDisplays = StrList(value); Fire(); break;
                case "views":    _selectedViewIds     = IdList(value);  Fire(); break;
                case "clear":    _clearPrevious       = AsBool(value, _clearPrevious); Fire(); break;
                case "oversize": _roundSizeMm         = AsDouble(value, _roundSizeMm / MmPerInch) * MmPerInch; Fire(); break;
                case "anchor":
                {
                    var a = AsString(value);
                    _anchorMode = a == "Top" ? "Top" : a == "Bottom" ? "Bottom" : "Centre";
                    Fire(); break;
                }
                case "spotType":
                    if (_spotTypeByName.TryGetValue(AsString(value), out var id)) _spotTypeId = id;
                    Fire(); break;
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
                case "S1": return _selectedDefDisplays.Count == 0 ? "-" : AppStrings.T("clash.elevationFinder.summaries.defCount", _selectedDefDisplays.Count);
                case "S2": return _selectedViewIds.Count     == 0 ? "-" : AppStrings.T("clash.elevationFinder.summaries.viewCount", _selectedViewIds.Count);
                case "S3":
                {
                    var bits = new List<string>();
                    if (_clearPrevious) bits.Add(AppStrings.T("clash.elevationFinder.summaries.clear"));
                    bits.Add(AppStrings.T("clash.elevationFinder.summaries.tag", AnchorWord));
                    bits.Add(_roundSizeMm > 0 ? AppStrings.T("clash.elevationFinder.summaries.oversize", _roundSizeMm / MmPerInch) : AppStrings.T("clash.elevationFinder.summaries.exactSize"));
                    return string.Join(" · ", bits);
                }
                case "S4": return AppStrings.T("clash.elevationFinder.summaries.S4");
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
            _handler.ClearPrevious = _clearPrevious;
            _handler.AnchorMode    = _anchorMode;
            _handler.SpotTypeId    = _spotTypeId;
            _handler.RoundSizeMm   = _roundSizeMm;
            _handler.PushLog       = pushLog;
            _handler.OnProgress    = onProgress;
            _handler.OnComplete    = onComplete;

            _event.Raise();
        }

        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog       = null;
            _handler.OnProgress    = null;
            _handler.OnComplete    = null;
            _handler.OnResultChips = null;
        }
    }
}
