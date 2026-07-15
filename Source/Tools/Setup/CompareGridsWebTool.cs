using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.Setup
{
    /// <summary>Web port of <see cref="CompareGridsViewModel"/> — read-only grid audit across
    /// host + selected links. Same handler and display strings (this tool pre-dates AppStrings
    /// externalization, so its literals are kept verbatim).</summary>
    public class CompareGridsWebTool : WebToolBase, IWebToolCleanup
    {
        private readonly CompareGridsRunHandler? _runHandler;
        private readonly ExternalEvent?          _runEvent;
        private readonly CompareGridsData        _data;

        private readonly Dictionary<string, long> _fileByName = new Dictionary<string, long>();
        private HashSet<long> _selectedLinkIds;
        private bool   _includeHost = true;
        private double _posTolInches = 0.0625;
        private double _angleTolDeg  = 0.10;

        public CompareGridsWebTool(CompareGridsRunHandler? runHandler, ExternalEvent? runEvent, CompareGridsData? data)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _data       = data ?? new CompareGridsData();
            foreach (var f in _data.Files.Where(f => !f.IsHost)) _fileByName[f.Name] = f.LinkInstId;
            // default: every loaded link selected
            _selectedLinkIds = new HashSet<long>(_fileByName.Values);
        }

        public override string Title    => "Compare Grids Across Links";
        public override string RunLabel => "Compare Grids →";

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var files = new WebStep("files", "Files & Tolerances");
            if (_fileByName.Count == 0)
            {
                files.Add(WebInput.Hint("noLinks", "No loaded links found — nothing to compare against the host."));
            }
            else
            {
                var all = _fileByName.Keys.ToList();
                files.Add(WebInput.MultiSelectTabs("links", "Links to include",
                    new Dictionary<string, List<string>> { ["Links"] = all },
                    all.Where(n => _selectedLinkIds.Contains(_fileByName[n]))));
                files.Add(WebInput.Toggle("host", "Include host grids", _includeHost));
                files.Add(WebInput.Stepper("posTol", "Lateral offset (inches)", _posTolInches, 0.0, 120.0, 0.0625, 3));
                files.Add(WebInput.Stepper("angleTol", "Angle (degrees)", _angleTolDeg, 0.0, 45.0, 0.05, 2));
                files.Add(WebInput.Hint("frameHint",
                    "Grids are compared in a common world frame. Meaningful once files share coordinates."));
            }

            var run = new WebStep("run", "Review & Run", required: false)
                .Add(WebInput.Review("review", new[]
                {
                    ("Files", $"{(_includeHost ? "Host + " : "")}{_selectedLinkIds.Count} link(s)"),
                    ("Tolerances", $"{_posTolInches:0.###}\" · {_angleTolDeg:0.##}°"),
                },
                note: "Read-only — reports grid differences, makes no changes."));

            return new List<WebStep> { files, run };
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "links":
                    _selectedLinkIds = new HashSet<long>(
                        StrList(value).Where(_fileByName.ContainsKey).Select(n => _fileByName[n]));
                    Fire(); break;
                case "host":     _includeHost = AsBool(value, _includeHost); Fire(); break;
                case "posTol":   { var v = AsDouble(value, _posTolInches); if (v >= 0) _posTolInches = v; Fire(); break; }
                case "angleTol": { var v = AsDouble(value, _angleTolDeg);  if (v >= 0) _angleTolDeg  = v; Fire(); break; }
            }
        }

        public override bool IsStepValid(string stepId)
        {
            if (stepId != "files") return true;
            int fileCount = _selectedLinkIds.Count + (_includeHost ? 1 : 0);
            return fileCount >= 2;
        }

        public override bool CanRun() => _selectedLinkIds.Count + (_includeHost ? 1 : 0) >= 2;

        public override string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "files":
                    return $"{(_includeHost ? "Host + " : "")}{_selectedLinkIds.Count} link(s) · {_posTolInches:0.###}\" / {_angleTolDeg:0.##}°";
                case "run": return "Ready to run";
                default:    return "-";
            }
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog("Run handler not registered.", "fail"); onComplete(0, 1, 0); return; }

            _runHandler.FileLinkInstIds = _selectedLinkIds.ToList();
            _runHandler.IncludeHost     = _includeHost;
            _runHandler.PosTolInches    = _posTolInches;
            _runHandler.AngleTolDegrees = _angleTolDeg;
            _runHandler.PushLog         = pushLog;
            _runHandler.OnProgress      = onProgress;
            _runHandler.OnComplete      = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
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
