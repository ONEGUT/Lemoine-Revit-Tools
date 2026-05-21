using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// ViewModel for the Auto Filters tool.
    /// Step 1 — choose which linked documents to scan.
    /// Step 2 — choose which disciplines to include.
    /// Step 3 — Review & Run.
    ///
    /// Also implements ILemoineToolSettings to expose the dedicated
    /// AutoFiltersSettingsWindow via the gear-icon overlay and global settings panel.
    /// </summary>
    public class AutoFiltersViewModel : ILemoineTool, ILemoineToolSettings
    {
        // ── ILemoineTool identity ──────────────────────────────────────────────
        public string Title    => "Auto Filters";
        public string RunLabel => "Create Filters →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Source Documents", required: true),
            new StepDefinition("S2", "Disciplines",      required: true),
            new StepDefinition("S3", "Review & Run",     required: false),
        };

        // ── State ──────────────────────────────────────────────────────────────
        private readonly IReadOnlyList<string> _availableLinkTitles;
        private readonly IReadOnlyList<string> _availableDisciplines;

        private Dictionary<string, bool> _linkState       = new Dictionary<string, bool>();
        private Dictionary<string, bool> _disciplineState = new Dictionary<string, bool>();

        // ── Validation change notification ─────────────────────────────────────
        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── ExternalEvent wiring ───────────────────────────────────────────────
        private readonly AutoFiltersEventHandler         _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent _event;

        public AutoFiltersViewModel(
            AutoFiltersEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent,
            IReadOnlyList<string> availableLinkTitles,
            IReadOnlyList<string> availableDisciplines)
        {
            _handler              = handler;
            _event                = externalEvent;
            _availableLinkTitles  = availableLinkTitles  ?? new List<string>();
            _availableDisciplines = availableDisciplines ?? new List<string>();
        }

        // ═══════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1")
            {
                var stack = new StackPanel();

                var hostNote = new TextBlock
                {
                    Text         = "The host document is always scanned. Toggle any loaded links to include below.",
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                    Margin       = new Thickness(0, 0, 0, 8),
                };
                hostNote.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                hostNote.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                hostNote.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                stack.Children.Add(hostNote);

                if (_availableLinkTitles.Count == 0)
                {
                    var none = new TextBlock
                    {
                        Text         = "No loaded Revit links found in the host document.",
                        TextWrapping = TextWrapping.Wrap,
                        FontStyle    = FontStyles.Italic,
                    };
                    none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    stack.Children.Add(none);
                    _linkState.Clear();
                    OnValidationChanged();
                    return stack;
                }

                var items = _availableLinkTitles
                    .Select(t => new ToggleItem { Id = t, Label = t, Desc = "Scan this linked model", DefaultOn = true })
                    .ToList();

                var toggles = new LemoineToggleSwitches();
                toggles.SetItems(items, _linkState.Count > 0 ? _linkState : null);
                toggles.StateChanged += state =>
                {
                    _linkState = new Dictionary<string, bool>(state);
                    OnValidationChanged();
                };
                stack.Children.Add(toggles);
                return stack;
            }

            if (stepId == "S2")
            {
                if (_availableDisciplines.Count == 0)
                {
                    var msg = new TextBlock
                    {
                        Text         = "No disciplines found in Auto Filters Settings. Configure disciplines in the Settings panel before running.",
                        TextWrapping = TextWrapping.Wrap,
                        FontStyle    = FontStyles.Italic,
                    };
                    msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    return msg;
                }

                var items = _availableDisciplines
                    .Select(d => new ToggleItem { Id = d, Label = d, Desc = "Create filters for this discipline", DefaultOn = true })
                    .ToList();

                var toggles = new LemoineToggleSwitches();
                toggles.SetItems(items, _disciplineState.Count > 0 ? _disciplineState : null);
                toggles.StateChanged += state =>
                {
                    _disciplineState = new Dictionary<string, bool>(state);
                    OnValidationChanged();
                };
                return toggles;
            }

            if (stepId == "S3") return BuildReviewPanel();
            return null;
        }

        private FrameworkElement BuildReviewPanel()
        {
            var outer = new StackPanel();

            // ── Summary card grid via reusable LemoineReviewSummary ────────────
            var reviewItems = new List<(string id, string label)>
            {
                ("src",  "Source Docs"),
                ("disc", "Disciplines"),
                ("tgt",  "Target"),
                ("op",   "Operation"),
            };

            IDictionary<string, string> BuildValues() => new Dictionary<string, string>
            {
                ["src"]  = _linkState.Count(kv => kv.Value) == 0
                               ? "Host only"
                               : $"Host + {_linkState.Count(kv => kv.Value)} link(s)",
                ["disc"] = $"{(_disciplineState.Count == 0 ? _availableDisciplines.Count : _disciplineState.Count(kv => kv.Value))} selected",
                ["tgt"]  = "Active view",
                ["op"]   = "Create + color filters",
            };

            var review = new LemoineReviewSummary();
            review.SetItems(reviewItems, BuildValues());
            // Refresh card values whenever upstream selection changes
            ValidationChanged += (s, e) => review.SetItems(reviewItems, BuildValues());
            outer.Children.Add(review);

            var desc = new TextBlock
            {
                Text         = "Scans selected documents for each enabled discipline, then creates " +
                               "ParameterFilterElements with color overrides in the active view. " +
                               "Existing filters with matching names are reused rather than duplicated.",
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 8, 0, 0),
            };
            desc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            desc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            desc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(desc);

            return outer;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // IsValid / SummaryFor / Run
        // ═══════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return true; // host-only is always valid
            if (stepId == "S2")
            {
                if (_disciplineState.Count == 0) return _availableDisciplines.Count > 0;
                return _disciplineState.Any(kv => kv.Value);
            }
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1") { int l = _linkState.Count(kv => kv.Value); return l == 0 ? "Host only" : $"Host + {l} link(s)"; }
            if (stepId == "S2") { int on = _disciplineState.Count == 0 ? _availableDisciplines.Count : _disciplineState.Count(kv => kv.Value); return $"{on} discipline(s) selected"; }
            if (stepId == "S3") return "Ready to run";
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            var selectedLinks = _availableLinkTitles.Where(t => !_linkState.TryGetValue(t, out bool on) || on).ToList();
            var selectedDiscs = _availableDisciplines.Where(d => !_disciplineState.TryGetValue(d, out bool on) || on).ToList();

            _handler.SelectedLinkTitles  = selectedLinks;
            _handler.SelectedDisciplines = selectedDiscs;
            _handler.PushLog             = pushLog;
            _handler.OnProgress          = onProgress;
            _handler.OnComplete          = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
            _event.Raise();
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ILemoineToolSettings — opens the dedicated AutoFiltersSettingsWindow
        // ═══════════════════════════════════════════════════════════════════════

        // ⚠ AutoFilters uses a bespoke 3-tab modal (AutoFiltersSettingsWindow).
        //   GetSettingsSpec() returns null. Registered via RegisterToolWithCustomWindow()
        //   in OpenSettingsCommand — see that file and the comment in ILemoineToolSettings.cs.
        //   In-tool gear overlay will show "No settings for this tool." until the library
        //   gains a native hook for custom settings windows inside StepFlowWindow.

        public LemoineToolSettingsSpec? GetSettingsSpec() => null;

        public void ApplySettings(string groupId, string settingId, object value)
        {
            // Not called — settings applied inside AutoFiltersSettingsWindow.OnSave().
        }

        /// <summary>
        /// Panel factory for GlobalSettingsWindow.RegisterToolWithCustomWindow().
        /// Shows a description + "Configure…" button that opens AutoFiltersSettingsWindow.
        /// </summary>
        public static System.Windows.UIElement BuildCustomSettingsPanel()
            => AutoFiltersSettingsWindow.BuildPanel();


    }
}
