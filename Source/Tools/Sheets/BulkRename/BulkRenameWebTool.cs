using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.LinkViews.BulkRename
{
    /// <summary>Web port of <see cref="BulkRenameViewModel"/> — bulk rename sheets/views with
    /// Find&amp;Replace / Prefix-Suffix / Sequential / Token operations. Same engine, handler,
    /// pattern stores, and AppStrings keys. The WPF in-step live preview panel moves to the
    /// review step (rebuilt automatically), with live counts in the S3 summary line.</summary>
    public class BulkRenameWebTool : WebToolBase, IWebToolCleanup, IWebStepRefresh
    {
        private const string ToolIdSeq   = "sheets.bulkRename.seq";
        private const string ToolIdToken = "sheets.bulkRename.token";

        private const string ModeTokenFind   = "findReplace";
        private const string ModeTokenPrefix = "prefixSuffix";
        private const string ModeTokenSeq    = "sequential";
        private const string ModeTokenToken  = "token";

        private readonly List<BulkRenameViewModel.SheetEntry> _sheets;
        private readonly List<BulkRenameViewModel.ViewEntry>  _views;
        private readonly BrowserTree _browserTree;

        private RenameTarget _target = RenameTarget.Sheets;
        private RenameField  _field  = RenameField.Name;
        private readonly RenameConfig _config = new RenameConfig();

        private readonly HashSet<long> _selectedSheetIds = new HashSet<long>();
        private readonly HashSet<long> _selectedViewIds  = new HashSet<long>();

        private readonly BulkRenameRunHandler? _runHandler;
        private readonly ExternalEvent?        _runEvent;

        public event Action<string>? StepInputsChanged;

        public BulkRenameWebTool(
            BulkRenameRunHandler? runHandler, ExternalEvent? runEvent,
            List<BulkRenameViewModel.SheetEntry>? sheets, List<BulkRenameViewModel.ViewEntry>? views,
            BrowserTree? browserTree = null)
        {
            _runHandler  = runHandler;
            _runEvent    = runEvent;
            _sheets      = sheets ?? new List<BulkRenameViewModel.SheetEntry>();
            _views       = views  ?? new List<BulkRenameViewModel.ViewEntry>();
            _browserTree = browserTree ?? new BrowserTree();

            _config.SeqPattern   = NamingPatternStore.Instance.GetOrDefault(ToolIdSeq,   _config.SeqPattern);
            _config.TokenPattern = NamingPatternStore.Instance.GetOrDefault(ToolIdToken, _config.TokenPattern);
        }

        public override string Title    => AppStrings.T("linkviews.bulkRename.title");
        public override string RunLabel => AppStrings.T("linkviews.bulkRename.runLabel");

        private static string ModeToken(RenameMode m) => m switch
        {
            RenameMode.PrefixSuffix => ModeTokenPrefix,
            RenameMode.Sequential   => ModeTokenSeq,
            RenameMode.Token        => ModeTokenToken,
            _                       => ModeTokenFind,
        };

        private static RenameMode ModeFromToken(string t) => t switch
        {
            ModeTokenPrefix => RenameMode.PrefixSuffix,
            ModeTokenSeq    => RenameMode.Sequential,
            ModeTokenToken  => RenameMode.Token,
            _               => RenameMode.FindReplace,
        };

        private static string ModeLabel(RenameMode m) => m switch
        {
            RenameMode.PrefixSuffix => AppStrings.T("linkviews.bulkRename.labels.modePrefixSuffix"),
            RenameMode.Sequential   => AppStrings.T("linkviews.bulkRename.labels.modeSequential"),
            RenameMode.Token        => AppStrings.T("linkviews.bulkRename.labels.modeToken"),
            _                       => AppStrings.T("linkviews.bulkRename.labels.modeFindReplace"),
        };

        private IReadOnlyList<TokenDefinition> FieldTokens() =>
            NamingTokenRegistry.TokensFor(
                _target == RenameTarget.Sheets ? TokenEntity.Sheet : TokenEntity.View,
                hasSource: false);

        private string DefaultTokenPattern() =>
            _target == RenameTarget.Sheets ? "{SheetNumber} - {SheetName}" : "{ViewName}";

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            // S1 — target
            var s1 = new WebStep("S1", AppStrings.T("linkviews.bulkRename.steps.S1"))
                .Add(WebInput.SingleSelect("target", AppStrings.T("linkviews.bulkRename.labels.headerTarget"),
                    _target == RenameTarget.Sheets ? "sheets" : "views", new[]
                    {
                        new WebOption("sheets", AppStrings.T("linkviews.bulkRename.labels.targetSheets")),
                        new WebOption("views",  AppStrings.T("linkviews.bulkRename.labels.targetViews")),
                    }))
                .Add(WebInput.Hint("hintTarget", AppStrings.T("linkviews.bulkRename.labels.hintTarget")));

            // S2 — items (browser tree pruned to the active target kind)
            var eligible = _target == RenameTarget.Sheets
                ? new HashSet<long>(_sheets.Select(s => s.Id.Value))
                : new HashSet<long>(_views.Select(v => v.Id.Value));
            var s2 = new WebStep("S2", AppStrings.T("linkviews.bulkRename.steps.S2"))
                .Add(WebInput.BrowserTree("items", AppStrings.T("linkviews.bulkRename.labels.itemsPicker"),
                    PruneTree(_browserTree, eligible), SelectedIds()));

            // S3 — field & operation
            var s3 = new WebStep("S3", AppStrings.T("linkviews.bulkRename.steps.S3"));
            if (_target == RenameTarget.Sheets)
                s3.Add(WebInput.SingleSelect("field", AppStrings.T("linkviews.bulkRename.labels.headerField"),
                    _field == RenameField.Number ? "number" : "name", new[]
                    {
                        new WebOption("name",   AppStrings.T("linkviews.bulkRename.labels.fieldSheetName")),
                        new WebOption("number", AppStrings.T("linkviews.bulkRename.labels.fieldSheetNumber")),
                    }));
            else
                s3.Add(WebInput.Hint("renamingView", AppStrings.T("linkviews.bulkRename.labels.renamingView")));

            s3.Add(WebInput.SingleSelect("mode", AppStrings.T("linkviews.bulkRename.labels.headerOperation"),
                ModeToken(_config.Mode), new[]
                {
                    new WebOption(ModeTokenFind,   ModeLabel(RenameMode.FindReplace)),
                    new WebOption(ModeTokenPrefix, ModeLabel(RenameMode.PrefixSuffix)),
                    new WebOption(ModeTokenSeq,    ModeLabel(RenameMode.Sequential)),
                    new WebOption(ModeTokenToken,  ModeLabel(RenameMode.Token)),
                }));

            switch (_config.Mode)
            {
                case RenameMode.FindReplace:
                    s3.Add(WebInput.TextField("find", AppStrings.T("linkviews.bulkRename.labels.findLabel"),
                        _config.Find, AppStrings.T("linkviews.bulkRename.labels.findPlaceholder")));
                    s3.Add(WebInput.TextField("replace", AppStrings.T("linkviews.bulkRename.labels.replaceLabel"),
                        _config.Replace, AppStrings.T("linkviews.bulkRename.labels.replacePlaceholder")));
                    s3.Add(WebInput.Toggle("case",  AppStrings.T("linkviews.bulkRename.labels.caseSensitive"), _config.CaseSensitive));
                    s3.Add(WebInput.Toggle("whole", AppStrings.T("linkviews.bulkRename.labels.wholeField"),    _config.WholeField));
                    break;

                case RenameMode.PrefixSuffix:
                    s3.Add(WebInput.TextField("prefix", AppStrings.T("linkviews.bulkRename.labels.prefixLabel"),
                        _config.Prefix, AppStrings.T("linkviews.bulkRename.labels.prefixPlaceholder")));
                    s3.Add(WebInput.TextField("suffix", AppStrings.T("linkviews.bulkRename.labels.suffixLabel"),
                        _config.Suffix, AppStrings.T("linkviews.bulkRename.labels.suffixPlaceholder")));
                    break;

                case RenameMode.Sequential:
                    s3.Add(WebInput.TokenInput("seqPattern", "", _config.SeqPattern, "{Seq}",
                        FieldTokens(), TokenSample()));
                    AddSeqSteppers(s3);
                    break;

                case RenameMode.Token:
                    s3.Add(WebInput.TokenInput("tokenPattern", "", _config.TokenPattern, DefaultTokenPattern(),
                        FieldTokens(), TokenSample()));
                    AddSeqSteppers(s3);
                    break;
            }

            // S4 — review + plan preview (this step is rebuilt automatically, so the preview
            // lines stay current; the WPF version showed them inside S3 instead).
            var plan = BuildPlan();
            int changes    = plan.Count(p => p.Status == RenameStatus.Change);
            int collisions = plan.Count(p => p.Status == RenameStatus.Collision);
            int empties    = plan.Count(p => p.Status == RenameStatus.Empty);
            int skips      = plan.Count - changes;
            string fieldLabel = _target == RenameTarget.Views
                ? AppStrings.T("linkviews.bulkRename.labels.fieldViewName")
                : (_field == RenameField.Number ? AppStrings.T("linkviews.bulkRename.labels.fieldSheetNumber") : AppStrings.T("linkviews.bulkRename.labels.fieldSheetName"));

            string? warning = null;
            if (collisions > 0 || empties > 0)
            {
                var parts = new List<string>();
                if (collisions > 0) parts.Add(AppStrings.T("linkviews.bulkRename.review.warnCollide", collisions));
                if (empties    > 0) parts.Add(AppStrings.T("linkviews.bulkRename.review.warnEmpty", empties));
                warning = string.Join("; ", parts) + AppStrings.T("linkviews.bulkRename.review.warnSuffix");
            }

            var s4 = new WebStep("S4", AppStrings.T("linkviews.bulkRename.steps.S4"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("linkviews.bulkRename.review.itemTarget"),
                     _target == RenameTarget.Sheets ? AppStrings.T("linkviews.bulkRename.labels.targetSheets") : AppStrings.T("linkviews.bulkRename.labels.targetViews")),
                    (AppStrings.T("linkviews.bulkRename.review.itemField"), fieldLabel),
                    (AppStrings.T("linkviews.bulkRename.review.itemMode"),  ModeLabel(_config.Mode)),
                    (AppStrings.T("linkviews.bulkRename.review.itemCount"),
                     SelectedIds().Count > 0 ? AppStrings.T("linkviews.bulkRename.review.countValue", SelectedIds().Count) : "-"),
                    (AppStrings.T("linkviews.bulkRename.review.itemChanges"), changes.ToString()),
                    (AppStrings.T("linkviews.bulkRename.review.itemSkips"),   skips.ToString()),
                },
                note: AppStrings.T("linkviews.bulkRename.review.note"),
                warning: warning));

            if (plan.Count > 0)
            {
                s4.Add(WebInput.Hint("previewCounts",
                    AppStrings.T("linkviews.bulkRename.labels.previewCounts", changes, collisions, empties)));
                s4.Add(WebInput.Review("preview", plan.Take(12).Select(p =>
                {
                    string suffix =
                        p.Status == RenameStatus.Collision ? AppStrings.T("linkviews.bulkRename.labels.sfxCollides") :
                        p.Status == RenameStatus.Empty     ? AppStrings.T("linkviews.bulkRename.labels.sfxEmpty") :
                        p.Status == RenameStatus.Unchanged ? AppStrings.T("linkviews.bulkRename.labels.sfxNoChange") : "";
                    return (p.OldValue, p.NewValue + suffix);
                }).ToArray(),
                note: plan.Count > 12 ? AppStrings.T("linkviews.bulkRename.labels.previewMore", plan.Count - 12) : null));
            }

            return new List<WebStep> { s1, s2, s3, s4 };
        }

        private void AddSeqSteppers(WebStep step)
        {
            step.Add(WebInput.Stepper("seqStart",     AppStrings.T("linkviews.bulkRename.labels.seqStart"),     _config.SeqStart,     0, 100000, 1, 0));
            step.Add(WebInput.Stepper("seqIncrement", AppStrings.T("linkviews.bulkRename.labels.seqIncrement"), _config.SeqIncrement, 1, 1000,   1, 0));
            step.Add(WebInput.Stepper("seqPad",       AppStrings.T("linkviews.bulkRename.labels.seqPad"),       _config.SeqPad,       0, 8,      1, 0));
        }

        // Token→sample map for the page-side pattern preview, seeded from the first
        // selected entry (or the first available one) plus a formatted {Seq} sample.
        private Dictionary<string, string> TokenSample()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var entry = OrderedEntries().FirstOrDefault();
            if (entry.tokens != null)
                foreach (var kv in entry.tokens) map[kv.Key] = kv.Value;
            else if (_target == RenameTarget.Sheets && _sheets.Count > 0)
            {
                map["SheetNumber"] = _sheets[0].Number;
                map["SheetName"]   = _sheets[0].Name;
            }
            else if (_target == RenameTarget.Views && _views.Count > 0)
            {
                map["ViewName"] = _views[0].Name;
                map["ViewType"] = _views[0].TypeLabel;
            }
            map["Seq"] = _config.SeqStart.ToString(new string('0', Math.Max(1, _config.SeqPad)));
            return map;
        }

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "target":
                {
                    var t = AsString(value) == "views" ? RenameTarget.Views : RenameTarget.Sheets;
                    if (t != _target)
                    {
                        _target = t;
                        if (_target == RenameTarget.Views) _field = RenameField.Name;
                        StepInputsChanged?.Invoke("S2");
                        StepInputsChanged?.Invoke("S3");
                        Fire();
                    }
                    break;
                }
                case "items":
                {
                    var set = _target == RenameTarget.Sheets ? _selectedSheetIds : _selectedViewIds;
                    set.Clear();
                    foreach (var id in IdList(value)) set.Add(id);
                    Fire();
                    break;
                }
                case "field":
                    _field = AsString(value) == "number" ? RenameField.Number : RenameField.Name;
                    Fire(); break;
                case "mode":
                {
                    var m = ModeFromToken(AsString(value));
                    if (m != _config.Mode)
                    {
                        _config.Mode = m;
                        StepInputsChanged?.Invoke("S3");
                        Fire();
                    }
                    break;
                }
                case "find":    _config.Find    = AsString(value); Fire(); break;
                case "replace": _config.Replace = AsString(value); Fire(); break;
                case "case":    _config.CaseSensitive = AsBool(value, _config.CaseSensitive); Fire(); break;
                case "whole":   _config.WholeField    = AsBool(value, _config.WholeField);    Fire(); break;
                case "prefix":  _config.Prefix  = AsString(value); Fire(); break;
                case "suffix":  _config.Suffix  = AsString(value); Fire(); break;
                case "seqPattern":
                    _config.SeqPattern = AsString(value, _config.SeqPattern);
                    NamingPatternStore.Instance.Set(ToolIdSeq, _config.SeqPattern);
                    Fire(); break;
                case "tokenPattern":
                    _config.TokenPattern = AsString(value, _config.TokenPattern);
                    NamingPatternStore.Instance.Set(ToolIdToken, _config.TokenPattern);
                    Fire(); break;
                case "seqStart":     _config.SeqStart     = (int)AsDouble(value, _config.SeqStart);     Fire(); break;
                case "seqIncrement": _config.SeqIncrement = (int)AsDouble(value, _config.SeqIncrement); Fire(); break;
                case "seqPad":       _config.SeqPad       = (int)AsDouble(value, _config.SeqPad);       Fire(); break;
            }
        }

        // ── Planning (shared with the run handler via BulkRenameEngine.Plan) ──

        private bool EnforceUnique() =>
            _target == RenameTarget.Views || (_target == RenameTarget.Sheets && _field == RenameField.Number);

        private HashSet<long> SelectedIds() =>
            _target == RenameTarget.Sheets ? _selectedSheetIds : _selectedViewIds;

        private List<(string oldValue, Dictionary<string, string> tokens, object? tag)> OrderedEntries()
        {
            var sel = SelectedIds();
            var result = new List<(string, Dictionary<string, string>, object?)>();

            if (_target == RenameTarget.Sheets)
            {
                foreach (var s in _sheets.OrderBy(s => s.Number, StringComparer.OrdinalIgnoreCase))
                {
                    if (!sel.Contains(s.Id.Value)) continue;
                    string oldValue = _field == RenameField.Number ? s.Number : s.Name;
                    var tokens = new Dictionary<string, string> { ["SheetNumber"] = s.Number, ["SheetName"] = s.Name };
                    result.Add((oldValue, tokens, s.Id));
                }
            }
            else
            {
                foreach (var v in _views.OrderBy(v => v.TypeLabel).ThenBy(v => v.Name))
                {
                    if (!sel.Contains(v.Id.Value)) continue;
                    var tokens = new Dictionary<string, string> { ["ViewName"] = v.Name, ["ViewType"] = v.TypeLabel };
                    result.Add((v.Name, tokens, v.Id));
                }
            }
            return result;
        }

        private IEnumerable<string> ExistingValuesNotSelected()
        {
            if (!EnforceUnique()) return Enumerable.Empty<string>();
            var sel = SelectedIds();
            if (_target == RenameTarget.Sheets)
                return _sheets.Where(s => !sel.Contains(s.Id.Value)).Select(s => s.Number);
            return _views.Where(v => !sel.Contains(v.Id.Value)).Select(v => v.Name);
        }

        private List<RenamePlanItem> BuildPlan()
            => BulkRenameEngine.Plan(_config, OrderedEntries(), ExistingValuesNotSelected(), EnforceUnique());

        private bool ModeConfigured()
        {
            switch (_config.Mode)
            {
                case RenameMode.FindReplace:  return !string.IsNullOrEmpty(_config.Find);
                case RenameMode.PrefixSuffix: return !string.IsNullOrEmpty(_config.Prefix) || !string.IsNullOrEmpty(_config.Suffix);
                case RenameMode.Sequential:   return !string.IsNullOrWhiteSpace(_config.SeqPattern);
                case RenameMode.Token:        return !string.IsNullOrWhiteSpace(_config.TokenPattern);
            }
            return false;
        }

        public override bool IsStepValid(string stepId)
        {
            if (stepId == "S1") return true;
            if (stepId == "S2") return SelectedIds().Count > 0;
            if (stepId == "S3") return ModeConfigured() && BuildPlan().Any(p => p.Status == RenameStatus.Change);
            return true;
        }

        public override bool CanRun() => IsStepValid("S2") && IsStepValid("S3");

        public override string SummaryFor(string stepId)
        {
            if (stepId == "S1") return _target == RenameTarget.Sheets ? AppStrings.T("linkviews.bulkRename.labels.targetSheets") : AppStrings.T("linkviews.bulkRename.labels.targetViews");
            if (stepId == "S2")
            {
                int n = SelectedIds().Count;
                return n > 0 ? AppStrings.T("linkviews.bulkRename.summaries.itemCount", n) : "-";
            }
            if (stepId == "S3")
            {
                if (!ModeConfigured()) return ModeLabel(_config.Mode);
                int c = BuildPlan().Count(p => p.Status == RenameStatus.Change);
                return AppStrings.T("linkviews.bulkRename.summaries.s3", ModeLabel(_config.Mode), c);
            }
            if (stepId == "S4") return AppStrings.T("linkviews.bulkRename.summaries.S4");
            return "-";
        }

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            _runHandler!.Target    = _target;
            _runHandler.Field      = _field;
            _runHandler.OrderedIds = OrderedEntries().Select(e => (ElementId)e.tag!).ToList();
            _runHandler.Config     = _config;
            _runHandler.PushLog    = pushLog;
            _runHandler.OnProgress = onProgress;
            _runHandler.OnComplete = onComplete;

            pushLog(AppStrings.T("linkviews.bulkRename.log.raising"), "info");
            _runEvent!.Raise();
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
