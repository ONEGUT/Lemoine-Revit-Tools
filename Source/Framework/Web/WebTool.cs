using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.Framework.Naming;

namespace LemoineTools.Framework.Web
{
    // =========================================================================
    // IWebTool + the serializable step/input spec (Phase 2b).
    //
    // The HTML analogue of IStepFlowTool: a tool describes its steps and inputs as
    // DATA (WebStep / WebInput -> JSON via the bridge `init` message) instead of
    // returning WPF FrameworkElements. WebStepFlowWindow drives all UI from this
    // contract. Validation stays C#-authoritative (rule R21): JS reports input
    // state via `state` messages; this interface decides IsStepValid / CanRun.
    //
    // The Run(...) callbacks are identical to IStepFlowTool.Run so a tool's existing
    // ExternalEvent handler wiring and the run lifecycle (RunState, RunLogSink,
    // RevitFailureCapture) carry over unchanged.
    // =========================================================================
    public interface IWebTool
    {
        string Title    { get; }
        string RunLabel { get; }

        /// <summary>The ordered step/input spec. Called once at window init; the window caches it.</summary>
        IReadOnlyList<WebStep> BuildSteps();

        /// <summary>An input changed in the page. <paramref name="value"/> is the JSON-parsed value
        /// (string / double / bool / list), per the input kind. No Revit API here.</summary>
        void OnState(string stepId, string inputId, object? value);

        bool   IsStepValid(string stepId);
        bool   CanRun();
        string SummaryFor(string stepId);
        bool   IsStepVisible(string stepId);

        /// <summary>Runs on the window's UI thread. Set the ExternalEvent handler's payload and
        /// Raise() it; the callbacks fan out to the page as log/progress/complete messages.</summary>
        void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete);

        /// <summary>Raise when a value changes such that validation/summaries should refresh.</summary>
        event EventHandler? ValidationChanged;
    }

    /// <summary>Optional teardown hook, mirrors IToolCleanup (null parked callbacks on close).</summary>
    public interface IWebToolCleanup
    {
        void OnWindowClosed();
    }

    /// <summary>Optional: custom per-step Confirm label + confirm hook (mirrors IStepConfirmable).</summary>
    public interface IWebConfirmable
    {
        /// <summary>Custom Confirm label for the step, or null for the default.</summary>
        string? ConfirmLabelFor(string stepId);
        /// <summary>Called when the user clicks Confirm on the step, before navigation.</summary>
        void OnStepConfirm(string stepId);
    }

    /// <summary>Optional: a run that pauses on a manual step (mirrors IRunPausable). While
    /// awaiting, the shell shows Continue/Skip footer buttons.</summary>
    public interface IWebRunPausable
    {
        /// <summary>(awaiting, continueLabel?, skipLabel?) — raised from any thread; the window marshals.</summary>
        event Action<bool, string?, string?>? AwaitingUserChanged;
        void ContinueRun();
        void SkipCurrentItem();
    }

    /// <summary>Optional: tool-initiated step content refresh (mirrors IStepAware's refresh
    /// callback). Raise with a step id after async data lands (scan results, Revit picks) and
    /// the window re-sends that step's inputs rebuilt from current state.</summary>
    public interface IWebStepRefresh
    {
        event Action<string>? StepInputsChanged;
    }

    /// <summary>Optional: receives clicks from <see cref="WebInput.Button"/> inputs (scan /
    /// pick-in-Revit style actions). Runs on the window thread; raise ExternalEvents from here.</summary>
    public interface IWebToolAction
    {
        void OnToolAction(string stepId, string inputId);
    }

    // ── Spec DTOs ────────────────────────────────────────────────────────────

    /// <summary>One accordion step. Fluent <see cref="Add"/> builds the input list.</summary>
    public sealed class WebStep
    {
        public string Id       { get; }
        public string Title    { get; }
        public bool   Required { get; }
        public List<WebInput> Inputs { get; } = new List<WebInput>();

        public WebStep(string id, string title, bool required = true)
        {
            Id = id; Title = title; Required = required;
        }

        public WebStep Add(WebInput input) { Inputs.Add(input); return this; }

        public Dictionary<string, object?> ToPayload(string summary, bool visible, string? confirmLabel = null)
        {
            var d = new Dictionary<string, object?>
            {
                ["id"]       = Id,
                ["title"]    = Title,
                ["required"] = Required,
                ["summary"]  = summary ?? "",
                ["hidden"]   = !visible,
                ["inputs"]   = Inputs.Select(i => i.ToPayload()).ToList(),
            };
            if (!string.IsNullOrEmpty(confirmLabel)) d["confirmLabel"] = confirmLabel;
            return d;
        }
    }

    /// <summary>An option in a single/multi select.</summary>
    public sealed class WebOption
    {
        public string Value    { get; }
        public string Label    { get; }
        public bool   Disabled { get; }

        public WebOption(string value, string label, bool disabled = false)
        {
            Value = value; Label = label; Disabled = disabled;
        }

        public Dictionary<string, object?> ToPayload() =>
            new Dictionary<string, object?> { ["value"] = Value, ["label"] = Label, ["disabled"] = Disabled };
    }

    /// <summary>
    /// One input. Built via the static factories, which map to the lemoine.js component of the
    /// same <c>kind</c>. Extra kind-specific props are carried in <see cref="_props"/>.
    /// </summary>
    public sealed class WebInput
    {
        public string  Kind  { get; }
        public string  Id    { get; }
        public string? Label { get; }
        private readonly Dictionary<string, object?> _props = new Dictionary<string, object?>();

        private WebInput(string kind, string id, string? label) { Kind = kind; Id = id; Label = label; }

        public static WebInput SingleSelect(string id, string label, string? value, IEnumerable<WebOption> options)
        {
            var w = new WebInput("singleSelect", id, label);
            w._props["value"]   = value;
            w._props["options"] = options.Select(o => o.ToPayload()).ToList();
            return w;
        }

        public static WebInput Toggle(string id, string label, bool isChecked, bool disabled = false)
        {
            var w = new WebInput("toggle", id, label);
            w._props["checked"]  = isChecked;
            w._props["disabled"] = disabled;
            return w;
        }

        public static WebInput TextField(string id, string label, string? value = null,
                                         string? placeholder = null, bool multiline = false)
        {
            var w = new WebInput("textField", id, label);
            w._props["value"] = value ?? "";
            if (placeholder != null) w._props["placeholder"] = placeholder;
            if (multiline) w._props["multiline"] = true;
            return w;
        }

        public static WebInput Stepper(string id, string label, double value,
                                      double min, double max, double step, int decimals)
        {
            var w = new WebInput("stepper", id, label);
            w._props["value"] = value; w._props["min"] = min; w._props["max"] = max;
            w._props["step"] = step;   w._props["decimals"] = decimals;
            return w;
        }

        /// <summary>
        /// The Project Browser view/sheet tree picker, fed a Revit-free <see cref="BrowserTree"/>
        /// snapshot (folders + view/sheet leaves, dependents nested). Ids travel as strings; the
        /// tool's OnState parses them back to <c>long</c>. See the R23 contract for the checkbox /
        /// right-click behaviour.
        /// </summary>
        public static WebInput BrowserTree(string id, string label, LemoineTools.Framework.BrowserTree tree,
            IEnumerable<long>? selected = null, bool singleSelect = false)
        {
            var w = new WebInput("browserTree", id, label);
            w._props["roots"]        = tree.Roots.Select(BrowserNodePayload).ToList();
            w._props["selected"]     = (selected ?? Enumerable.Empty<long>()).Select(x => (object?)x.ToString()).ToList();
            w._props["singleSelect"] = singleSelect;
            return w;
        }

        private static Dictionary<string, object?> BrowserNodePayload(LemoineTools.Framework.BrowserNode n) =>
            new Dictionary<string, object?>
            {
                ["title"]    = n.Title,
                ["id"]       = n.Id.HasValue ? (object?)n.Id.Value.ToString() : null,
                ["isSheet"]  = n.IsSheet,
                ["children"] = n.Children.Select(BrowserNodePayload).ToList(),
            };

        public static WebInput MultiSelectTabs(string id, string label,
            IDictionary<string, List<string>> groups, IEnumerable<string>? selected = null,
            bool singleSelect = false)
        {
            var w = new WebInput("multiSelectTabs", id, label);
            w._props["groups"]       = groups.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            w._props["selected"]     = (selected ?? Enumerable.Empty<string>()).Cast<object?>().ToList();
            w._props["singleSelect"] = singleSelect;
            return w;
        }

        public static WebInput CheckList(string id, string label,
            IEnumerable<(string Value, string Label, bool Checked)> items)
        {
            var w = new WebInput("checkList", id, label);
            w._props["items"] = items.Select(it => new Dictionary<string, object?>
            { ["value"] = it.Value, ["label"] = it.Label, ["checked"] = it.Checked }).ToList();
            return w;
        }

        public static WebInput Review(string id, IEnumerable<(string Label, string Value)> items,
                                     string? note = null, string? warning = null)
        {
            var w = new WebInput("review", id, null);
            w._props["items"] = items.Select(it => new Dictionary<string, object?>
            { ["label"] = it.Label, ["value"] = it.Value }).ToList();
            if (note != null)    w._props["note"]    = note;
            if (warning != null) w._props["warning"] = warning;
            return w;
        }

        public static WebInput FolderBrowser(string id, string label, string? value = null, string? placeholder = null)
        {
            var w = new WebInput("folderBrowser", id, label);
            w._props["value"] = value ?? "";
            if (placeholder != null) w._props["placeholder"] = placeholder;
            return w;
        }

        public static WebInput FileBrowser(string id, string label, string? value = null, string? placeholder = null)
        {
            var w = new WebInput("fileBrowser", id, label);
            w._props["value"] = value ?? "";
            if (placeholder != null) w._props["placeholder"] = placeholder;
            return w;
        }

        /// <summary>
        /// A naming-pattern editor fed the new naming-token registry. <paramref name="tokens"/> is
        /// what the tool got from <see cref="NamingTokenRegistry.TokensFor"/>; they are grouped here
        /// (Target/Source/Project/Date/User) exactly like the WPF <c>TokenInput</c>. <paramref name="sample"/>
        /// maps token key → sample value for the live preview (the tool computes it via TokenResolver
        /// against a representative element).
        /// </summary>
        public static WebInput TokenInput(string id, string label, string value, string defaultPattern,
            IReadOnlyList<TokenDefinition> tokens, IDictionary<string, string>? sample = null)
        {
            var w = new WebInput("tokenInput", id, label);
            w._props["value"]          = value ?? "";
            w._props["defaultPattern"] = defaultPattern ?? "";

            var groups = new List<Dictionary<string, object?>>();
            void AddGroup(string header, IEnumerable<TokenDefinition> items)
            {
                var list = items.ToList();
                if (list.Count == 0) return;
                groups.Add(new Dictionary<string, object?>
                {
                    ["header"] = header,
                    ["chips"]  = list.Select(t => new Dictionary<string, object?>
                    {
                        ["key"]     = t.Key,
                        ["label"]   = t.Label,
                        ["braced"]  = t.Braced,
                        ["user"]    = t.Origin == TokenOrigin.UserParameter,
                        ["tooltip"] = t.Description ?? "",
                    }).ToList(),
                });
            }

            var builtin = tokens.Where(t => t.Origin != TokenOrigin.UserParameter).ToList();
            var user    = tokens.Where(t => t.Origin == TokenOrigin.UserParameter).ToList();
            AddGroup(AppStrings.T("naming.input.groupTarget"),  builtin.Where(t => t.Subject == TokenSubject.Target));
            AddGroup(AppStrings.T("naming.input.groupSource"),  builtin.Where(t => t.Subject == TokenSubject.Source));
            AddGroup(AppStrings.T("naming.input.groupProject"), builtin.Where(t => t.Subject == TokenSubject.ProjectInfo));
            AddGroup(AppStrings.T("naming.input.groupDate"),    builtin.Where(t => t.Subject == TokenSubject.Environment));
            AddGroup(AppStrings.T("naming.input.groupUser"),    user);
            w._props["groups"] = groups;

            w._props["sample"] = (sample ?? new Dictionary<string, string>())
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            return w;
        }

        /// <summary>An action button inside a step ("Scan folder", "Pick in Revit"). Clicks route
        /// to <see cref="IWebToolAction.OnToolAction"/>; no value travels.</summary>
        public static WebInput Button(string id, string label, string? variant = null)
        {
            var w = new WebInput("actionButton", id, label);
            if (variant != null) w._props["variant"] = variant;
            return w;
        }

        /// <summary>A min/max pair of steppers (mirrors NumberRange). Value arrives as
        /// {"min":x,"max":y} in OnState.</summary>
        public static WebInput NumberRange(string id, string label,
            double min, double max, double lo, double hi, double step, int decimals)
        {
            var w = new WebInput("numberRange", id, label);
            w._props["min"] = min; w._props["max"] = max;
            w._props["lo"] = lo;   w._props["hi"] = hi;
            w._props["step"] = step; w._props["decimals"] = decimals;
            return w;
        }

        /// <summary>A filterable single-pick dropdown (mirrors SearchAutocomplete /
        /// editable combo). Value is the chosen option string.</summary>
        public static WebInput SearchSelect(string id, string label, IEnumerable<string> options,
            string? value = null, string? placeholder = null)
        {
            var w = new WebInput("searchSelect", id, label);
            w._props["options"] = options.Cast<object?>().ToList();
            w._props["value"]   = value ?? "";
            if (placeholder != null) w._props["placeholder"] = placeholder;
            return w;
        }

        /// <summary>Read-only informational text block (hint/description line).</summary>
        public static WebInput Hint(string id, string text)
        {
            var w = new WebInput("hint", id, null);
            w._props["text"] = text;
            return w;
        }

        public static WebInput Warn(string id, string text)
        {
            var w = new WebInput("warn", id, null);
            w._props["text"] = text;
            return w;
        }

        public Dictionary<string, object?> ToPayload()
        {
            var d = new Dictionary<string, object?> { ["kind"] = Kind, ["id"] = Id };
            if (Label != null) d["label"] = Label;
            foreach (var kv in _props) d[kv.Key] = kv.Value;
            return d;
        }
    }
}
