using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.Setup
{
    /// <summary>Web port of <see cref="AlignCoordinatesViewModel"/> — move host base points onto a
    /// resolved anchor and reposition selected links. Mirrors the redesigned four-way anchor model:
    /// host and each link independently anchor to Internal Origin / Project Base Point / Survey
    /// Point / Grid Intersection, with a Level supplying the Z (elevation) only for a Grid
    /// Intersection. Per-link rows are dynamic inputs keyed by link instance id
    /// ("sel_&lt;id&gt;", "anc_&lt;id&gt;", "g1_&lt;id&gt;", "g2_&lt;id&gt;", "lvl_&lt;id&gt;"),
    /// rebuilt via IWebStepRefresh. (Pre-dates AppStrings externalization; strings stay inline.)</summary>
    public class AlignCoordinatesWebTool : WebToolBase, IWebToolCleanup, IWebStepRefresh
    {
        private static readonly WebOption[] AnchorOptions =
        {
            new WebOption("origin", "Internal Origin (default)"),
            new WebOption("pbp",    "Project Base Point"),
            new WebOption("survey", "Survey Point"),
            new WebOption("grid",   "Grid Intersection"),
        };

        private static string       AnchorToken(AnchorSource a) =>
            a == AnchorSource.ProjectBasePoint ? "pbp" :
            a == AnchorSource.SurveyPoint      ? "survey" :
            a == AnchorSource.GridIntersection ? "grid" : "origin";
        private static AnchorSource AnchorFromToken(string t) =>
            t == "pbp"    ? AnchorSource.ProjectBasePoint :
            t == "survey" ? AnchorSource.SurveyPoint :
            t == "grid"   ? AnchorSource.GridIntersection : AnchorSource.InternalOrigin;
        private static string AnchorLabel(AnchorSource a) =>
            AnchorOptions.First(o => o.Value == AnchorToken(a)).Label;

        private readonly AlignCoordinatesRunHandler? _runHandler;
        private readonly ExternalEvent?              _runEvent;
        private readonly AlignCoordinatesData        _data;

        private AnchorSource _hostAnchorSource = AnchorSource.InternalOrigin;
        private string? _hostGrid1;
        private string? _hostGrid2;
        private string? _hostLevel;
        private bool _moveSurvey = true;
        private bool _movePbp    = true;
        private bool _rotate     = true;

        private readonly Dictionary<long, LinkAlignSpec> _linkSpecs = new Dictionary<long, LinkAlignSpec>();

        public event Action<string>? StepInputsChanged;

        public AlignCoordinatesWebTool(AlignCoordinatesRunHandler? runHandler, ExternalEvent? runEvent, AlignCoordinatesData? data)
        {
            _runHandler = runHandler;
            _runEvent   = runEvent;
            _data       = data ?? new AlignCoordinatesData();

            if (_data.HostGridNames.Count >= 1) _hostGrid1 = _data.HostGridNames[0];
            if (_data.HostGridNames.Count >= 2) _hostGrid2 = _data.HostGridNames[1];
            if (_data.HostLevelNames.Count >= 1) _hostLevel = _data.HostLevelNames[0];

            foreach (var l in _data.Links)
                _linkSpecs[l.LinkInstId] = new LinkAlignSpec { LinkInstId = l.LinkInstId, LinkName = l.Name };
        }

        public override string Title    => "Align Coordinates";
        public override string RunLabel => "Align in Revit →";

        // Effective moves: don't move a point onto itself when it IS the anchor.
        private bool EffectiveMoveSurvey => _moveSurvey && _hostAnchorSource != AnchorSource.SurveyPoint;
        private bool EffectiveMovePbp    => _movePbp    && _hostAnchorSource != AnchorSource.ProjectBasePoint;

        // Grids that cross the named grid within the SAME document. Non-Line grids are
        // treated as always crossing (same rules as the WPF twin).
        private static List<string> CrossingGridNames(List<GridGeom> grids, string? againstName)
        {
            var against = grids.FirstOrDefault(g => g.Name == againstName);
            if (against == null) return grids.Select(g => g.Name).Where(n => n != againstName).ToList();
            return grids.Where(g => g.Name != againstName && GridsCross(against, g))
                        .Select(g => g.Name).ToList();
        }

        private static bool GridsCross(GridGeom a, GridGeom b)
        {
            if (!a.IsLine || !b.IsLine) return true;

            const double ext = 1.0;
            (double x0, double y0, double x1, double y1) Extend(GridGeom g)
            {
                double dx = g.X1 - g.X0, dy = g.Y1 - g.Y0;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) return (g.X0, g.Y0, g.X1, g.Y1);
                double ux = dx / len, uy = dy / len;
                return (g.X0 - ux * ext, g.Y0 - uy * ext, g.X1 + ux * ext, g.Y1 + uy * ext);
            }

            var (ax0, ay0, ax1, ay1) = Extend(a);
            var (bx0, by0, bx1, by1) = Extend(b);

            double d1x = ax1 - ax0, d1y = ay1 - ay0;
            double d2x = bx1 - bx0, d2y = by1 - by0;
            double denom = d1x * d2y - d1y * d2x;
            if (Math.Abs(denom) < 1e-9) return false;

            double t = ((bx0 - ax0) * d2y - (by0 - ay0) * d2x) / denom;
            double u = ((bx0 - ax0) * d1y - (by0 - ay0) * d1x) / denom;
            return t >= 0 && t <= 1 && u >= 0 && u <= 1;
        }

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            // ── host ──────────────────────────────────────────────────────────
            var host = new WebStep("host", "Alignment Method")
                .Add(WebInput.SingleSelect("anchor", "Alignment method", AnchorToken(_hostAnchorSource), AnchorOptions))
                .Add(WebInput.Hint("anchorHint",
                    "Anchors the host to the chosen reference. Internal Origin needs no picking; Grid Intersection needs two crossing grids (and a level for the elevation)."));

            if (_hostAnchorSource == AnchorSource.GridIntersection)
            {
                if (_data.HostGridNames.Count < 2)
                {
                    host.Add(WebInput.Hint("hostNoGrids", "This document needs at least two grids to define an intersection."));
                }
                else
                {
                    if (_hostGrid1 == null) _hostGrid1 = _data.HostGridNames[0];
                    host.Add(WebInput.SingleSelect("hostGrid1", "Grid 1", _hostGrid1,
                        _data.HostGridNames.Select(n => new WebOption(n, n))));

                    var candidates = CrossingGridNames(_data.HostGrids, _hostGrid1);
                    if (candidates.Count == 0)
                    {
                        host.Add(WebInput.Hint("hostNoCrossing", $"No grids cross '{_hostGrid1}' — pick a different Grid 1."));
                        _hostGrid2 = null;
                    }
                    else
                    {
                        if (_hostGrid2 == null || !candidates.Contains(_hostGrid2)) _hostGrid2 = candidates[0];
                        host.Add(WebInput.SingleSelect("hostGrid2", "Grid 2", _hostGrid2,
                            candidates.Select(n => new WebOption(n, n))));
                    }

                    // Level supplies the Z for the grid intersection (falls back to Z=0 if absent).
                    if (_data.HostLevelNames.Count == 0)
                        host.Add(WebInput.Hint("hostNoLevels", "No levels — the elevation falls back to Z = 0."));
                    else
                    {
                        if (_hostLevel == null || !_data.HostLevelNames.Contains(_hostLevel)) _hostLevel = _data.HostLevelNames[0];
                        host.Add(WebInput.SingleSelect("hostLevel", "Level (elevation)", _hostLevel,
                            _data.HostLevelNames.Select(n => new WebOption(n, n))));
                    }
                }
            }

            host.Add(WebInput.Toggle("survey", "Move Survey Point",       _moveSurvey));
            host.Add(WebInput.Toggle("pbp",    "Move Project Base Point", _movePbp));
            if (_hostAnchorSource == AnchorSource.SurveyPoint)
                host.Add(WebInput.Hint("surveyIsRef", "The Survey Point is the anchor here, so it stays put."));
            if (_hostAnchorSource == AnchorSource.ProjectBasePoint)
                host.Add(WebInput.Hint("pbpIsRef", "The Project Base Point is the anchor here, so it stays put."));
            host.Add(WebInput.Hint("pointsHint", "Moves the chosen point(s) to the resolved host anchor."));

            // ── links ─────────────────────────────────────────────────────────
            var links = new WebStep("links", "Links to Align", required: false)
                .Add(WebInput.Hint("linksHeader", $"Links ({_data.Links.Count} loaded)"));

            if (_data.Links.Count == 0)
            {
                links.Add(WebInput.Hint("noLinks", "No loaded links found."));
            }
            else
            {
                foreach (var info in _data.Links)
                {
                    var spec = _linkSpecs[info.LinkInstId];
                    string key = info.LinkInstId.ToString();

                    links.Add(WebInput.Toggle("sel_" + key, info.Name, spec.Selected));
                    links.Add(WebInput.SingleSelect("anc_" + key, "    Anchor", AnchorToken(spec.AnchorSource), AnchorOptions));

                    if (spec.AnchorSource == AnchorSource.GridIntersection)
                    {
                        var grids = info.GridNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                        if (grids.Count < 2)
                        {
                            links.Add(WebInput.Hint("noGrids_" + key,
                                $"{info.Name} needs at least two grids for a Grid Intersection anchor."));
                        }
                        else
                        {
                            if (string.IsNullOrEmpty(spec.Grid1Name)) spec.Grid1Name = grids[0];
                            links.Add(WebInput.SingleSelect("g1_" + key, $"{info.Name} — Grid 1", spec.Grid1Name,
                                grids.Select(n => new WebOption(n, n))));

                            var candidates = CrossingGridNames(info.Grids, spec.Grid1Name);
                            if (candidates.Count == 0)
                            {
                                links.Add(WebInput.Hint("noCrossing_" + key,
                                    $"No grids in {info.Name} cross '{spec.Grid1Name}' — pick a different Grid 1."));
                                spec.Grid2Name = "";
                            }
                            else
                            {
                                if (string.IsNullOrEmpty(spec.Grid2Name) || !candidates.Contains(spec.Grid2Name))
                                    spec.Grid2Name = candidates[0];
                                links.Add(WebInput.SingleSelect("g2_" + key, $"{info.Name} — Grid 2", spec.Grid2Name,
                                    candidates.Select(n => new WebOption(n, n))));
                            }

                            if (info.LevelNames.Count == 0)
                                spec.LevelName = "";
                            else
                            {
                                if (string.IsNullOrEmpty(spec.LevelName) ||
                                    !info.LevelNames.Any(n => string.Equals(n, spec.LevelName, StringComparison.OrdinalIgnoreCase)))
                                    spec.LevelName = info.LevelNames[0];
                                links.Add(WebInput.SingleSelect("lvl_" + key, $"{info.Name} — Level (elevation)", spec.LevelName,
                                    info.LevelNames.Select(n => new WebOption(n, n))));
                            }
                        }
                    }
                }
                links.Add(WebInput.Hint("uncheckHint",
                    "Every loaded link is listed — turn a link off to leave it out of this run."));
            }

            links.Add(WebInput.Toggle("rotate", "Rotate to align orientation", _rotate));
            links.Add(WebInput.Hint("rotateHint", "Turn each link so its reference direction matches the host's."));
            links.Add(WebInput.Hint("pushHint",
                "Use the separate \"Push Coordinates to Links\" tool to commit this into the linked files."));

            // ── run ───────────────────────────────────────────────────────────
            var run = new WebStep("run", "Review & Run", required: false)
                .Add(WebInput.Review("review", new[]
                {
                    ("Alignment Method", AnchorSummary()),
                    ("Host Points",      PointsSummary()),
                    ("Links",            LinksSummary()),
                    ("Rotate",           _rotate ? "Yes" : "No"),
                },
                note: "Links are repositioned in the host only — use \"Push Coordinates to Links\" to commit this into the linked files.",
                warning: ReviewWarning()));

            return new List<WebStep> { host, links, run };
        }

        private string? ReviewWarning()
        {
            if (_hostAnchorSource == AnchorSource.GridIntersection && _hostGrid1 != null && _hostGrid1 == _hostGrid2)
                return "Host Grid 1 and Grid 2 are the same — pick two different grids.";

            foreach (var spec in _linkSpecs.Values)
                if (spec.Selected && spec.AnchorSource == AnchorSource.GridIntersection
                    && !string.IsNullOrEmpty(spec.Grid1Name) && spec.Grid1Name == spec.Grid2Name)
                    return $"{spec.LinkName}: Grid 1 and Grid 2 are the same — pick two different grids.";

            return null;
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "anchor":
                {
                    var a = AnchorFromToken(AsString(value));
                    if (a != _hostAnchorSource) { _hostAnchorSource = a; StepInputsChanged?.Invoke("host"); }
                    Fire(); return;
                }
                case "hostGrid1":
                    _hostGrid1 = AsString(value, _hostGrid1 ?? "");
                    StepInputsChanged?.Invoke("host"); // Grid 2 candidates depend on Grid 1
                    Fire(); return;
                case "hostGrid2": _hostGrid2 = AsString(value, _hostGrid2 ?? ""); Fire(); return;
                case "hostLevel": _hostLevel = AsString(value, _hostLevel ?? ""); Fire(); return;
                case "survey":    _moveSurvey = AsBool(value, _moveSurvey); Fire(); return;
                case "pbp":       _movePbp    = AsBool(value, _movePbp);    Fire(); return;
                case "rotate":    _rotate     = AsBool(value, _rotate);     Fire(); return;
            }

            // per-link inputs: "<kind>_<linkInstId>"
            int idx = inputId.IndexOf('_');
            if (idx <= 0) return;
            string kind = inputId.Substring(0, idx);
            if (!long.TryParse(inputId.Substring(idx + 1), out var linkId)) return;
            if (!_linkSpecs.TryGetValue(linkId, out var spec)) return;

            switch (kind)
            {
                case "sel":
                    spec.Selected = AsBool(value, spec.Selected);
                    Fire(); break;
                case "anc":
                    spec.AnchorSource = AnchorFromToken(AsString(value));
                    StepInputsChanged?.Invoke("links");
                    Fire(); break;
                case "g1":
                    spec.Grid1Name = AsString(value, spec.Grid1Name);
                    StepInputsChanged?.Invoke("links"); // Grid 2 candidates depend on Grid 1
                    Fire(); break;
                case "g2":
                    spec.Grid2Name = AsString(value, spec.Grid2Name);
                    Fire(); break;
                case "lvl":
                    spec.LevelName = AsString(value, spec.LevelName);
                    Fire(); break;
            }
        }

        private string AnchorSummary()
        {
            if (_hostAnchorSource != AnchorSource.GridIntersection) return AnchorLabel(_hostAnchorSource);
            string z = _data.HostLevelNames.Count == 0 ? "Z = 0" : $"Level '{_hostLevel ?? "-"}'";
            return $"{_hostGrid1 ?? "-"} × {_hostGrid2 ?? "-"} · {z}";
        }

        private string PointsSummary()
        {
            if (EffectiveMoveSurvey && EffectiveMovePbp) return "Survey + Project Base";
            if (EffectiveMoveSurvey) return "Survey Point";
            if (EffectiveMovePbp)    return "Project Base Point";
            return "None";
        }

        private string LinksSummary()
        {
            var selected = _linkSpecs.Values.Where(s => s.Selected).ToList();
            if (selected.Count == 0) return "None";
            int grid = selected.Count(s => s.AnchorSource == AnchorSource.GridIntersection);
            return grid == 0
                ? $"{selected.Count} link(s)"
                : $"{selected.Count} link(s) · {grid} anchored to Grid Intersection";
        }

        public override bool IsStepValid(string stepId)
        {
            switch (stepId)
            {
                case "host":
                    if (!(EffectiveMoveSurvey || EffectiveMovePbp)) return false;
                    if (_hostAnchorSource == AnchorSource.GridIntersection &&
                        (_hostGrid1 == null || _hostGrid2 == null || _hostGrid1 == _hostGrid2))
                        return false;
                    return true;

                case "links":
                    var selected = _linkSpecs.Values.Where(s => s.Selected).ToList();
                    if (selected.Count == 0) return false;
                    foreach (var s in selected)
                    {
                        if (s.AnchorSource == AnchorSource.GridIntersection &&
                            (string.IsNullOrEmpty(s.Grid1Name) || string.IsNullOrEmpty(s.Grid2Name) || s.Grid1Name == s.Grid2Name))
                            return false;
                    }
                    return true;

                default:
                    return true;
            }
        }

        public override bool CanRun() => IsStepValid("host") && IsStepValid("links");

        public override string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "host":
                    return $"{AnchorSummary()} · {PointsSummary()}";
                case "links":
                    var selected = _linkSpecs.Values.Where(s => s.Selected).ToList();
                    return selected.Count == 0 ? "-"
                        : $"{LinksSummary()} · rotate {(_rotate ? "on" : "off")}";
                case "run": return "Ready to run";
                default:    return "-";
            }
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog("Run handler not registered.", "fail"); onComplete(0, 1, 0); return; }

            _runHandler.HostAnchorSource = _hostAnchorSource;
            _runHandler.HostGrid1Name    = _hostGrid1 ?? "";
            _runHandler.HostGrid2Name    = _hostGrid2 ?? "";
            _runHandler.HostLevelName    = _hostLevel ?? "";
            _runHandler.MoveSurvey       = EffectiveMoveSurvey;
            _runHandler.MovePbp          = EffectiveMovePbp;
            _runHandler.Rotate           = _rotate;
            _runHandler.LinkSpecs        = _linkSpecs.Values.Select(s => new LinkAlignSpec
            {
                LinkInstId   = s.LinkInstId,
                LinkName     = s.LinkName,
                Selected     = s.Selected,
                AnchorSource = s.AnchorSource,
                Grid1Name    = s.Grid1Name,
                Grid2Name    = s.Grid2Name,
                LevelName    = s.LevelName,
            }).ToList();
            _runHandler.PushLog    = pushLog;
            _runHandler.OnProgress = onProgress;
            _runHandler.OnComplete = onComplete;

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
