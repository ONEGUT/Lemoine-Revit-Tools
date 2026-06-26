using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Testing.AlignSheetViews
{
    /// <summary>
    /// Picks one source/reference sheet and a set of target sheets, then aligns each target
    /// sheet's viewports so its views overlay their counterparts on the source sheet. The
    /// source sheet's viewports are ground truth and are never moved.
    /// </summary>
    public sealed class AlignSheetViewsViewModel : ILemoineTool, ILemoineReviewable, ILemoineToolCleanup
    {
        // ── ILemoineTool ──────────────────────────────────────────────────────
        public string Title    => "Align Sheet Views";
        public string RunLabel => "Align Views →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Source Sheets", required: true),
            new StepDefinition("S2", "Target Sheets", required: true),
            new StepDefinition("S3", "Options",       required: false),
            new StepDefinition("S4", "Review & Run",  required: false),
        };

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // Null the callbacks parked on the static handler so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog    = null;
            _handler.OnProgress = null;
            _handler.OnComplete = null;
        }

        // ── State ─────────────────────────────────────────────────────────────
        private readonly LemoineBrowserTree            _browserTree;
        private readonly List<long>                    _sheetIds;
        private readonly Dictionary<long, string>      _sheetLabels;

        private List<ElementId> _sourceSheetIds = new List<ElementId>();
        private List<ElementId> _targetSheetIds = new List<ElementId>();
        private int             _overlapPercent = 50;
        private bool            _alignTitles    = true;
        private bool            _previewOnly    = false;

        // Inheritance toggles
        private bool _inheritGrids          = false;
        private bool _inheritScopeBox       = false;
        private bool _inheritCropVisibility = false;
        private bool _inheritCropSize       = false;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly AlignSheetViewsEventHandler? _handler;
        private readonly ExternalEvent?               _event;

        // ── Constructors ──────────────────────────────────────────────────────
        public AlignSheetViewsViewModel(
            AlignSheetViewsEventHandler? handler,
            ExternalEvent?               externalEvent,
            IEnumerable<(ElementId Id, string Label)>? sheets,
            LemoineBrowserTree?          browserTree = null)
        {
            _handler     = handler;
            _event       = externalEvent;
            _browserTree = browserTree ?? new LemoineBrowserTree();

            _sheetIds    = new List<long>();
            _sheetLabels = new Dictionary<long, string>();
            foreach (var s in sheets ?? Enumerable.Empty<(ElementId, string)>())
            {
                long key = s.Id.Value;
                if (_sheetLabels.ContainsKey(key)) continue;
                _sheetIds.Add(key);
                _sheetLabels[key] = s.Label;
            }
        }

        /// <summary>Settings-only constructor (no document open).</summary>
        public AlignSheetViewsViewModel() : this(null, null, null, null) { }

        // ── Content ───────────────────────────────────────────────────────────
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildSourceStep();
                case "S2": return BuildTargetStep();
                case "S3": return BuildOptionsStep();
                case "S4": return null; // framework renders ILemoineReviewable
                default:   return null;
            }
        }

        // ── Step 1 — Source sheets (multi-select) ─────────────────────────────
        private FrameworkElement BuildSourceStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(SectionLabel("REFERENCE SHEETS"));
            outer.Children.Add(Note("Their viewport positions are the ground truth. These sheets are never moved. " +
                                    "Each target sheet is aligned to whichever reference sheet matches it best."));

            if (_sheetIds.Count == 0)
            {
                outer.Children.Add(Hint("No sheets were found in this project."));
                return outer;
            }

            var picker = new LemoineBrowserTreePicker
            {
                Height         = 280,
                AccessibleName = "Source reference sheets",
                Margin         = new Thickness(0, 8, 0, 0),
            };
            // Subscribe BEFORE SetTree — its end-of-setup SelectionChanged seeds the mirror field.
            picker.SelectionChanged += ids =>
            {
                _sourceSheetIds = ids.Select(id => new ElementId(id)).ToList();
                OnValidationChanged();
            };
            picker.SetTree(_browserTree, _sheetIds,
                _sourceSheetIds.Select(id => id.Value).ToList());
            outer.Children.Add(picker);
            return outer;
        }

        // ── Step 2 — Target sheets (multi-select) ─────────────────────────────
        private FrameworkElement BuildTargetStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(SectionLabel("SHEETS TO ALIGN"));
            outer.Children.Add(Note("Each target sheet's views are moved to overlay their counterparts on the " +
                                    "best-matching reference sheet. Any reference sheet ticked here is ignored."));

            if (_sheetIds.Count == 0)
            {
                outer.Children.Add(Hint("No sheets were found in this project."));
                return outer;
            }

            var picker = new LemoineBrowserTreePicker
            {
                Height         = 320,
                AccessibleName = "Target sheets to align",
                Margin         = new Thickness(0, 8, 0, 0),
            };
            picker.SelectionChanged += ids =>
            {
                _targetSheetIds = ids.Select(id => new ElementId(id)).ToList();
                OnValidationChanged();
            };
            picker.SetTree(_browserTree, _sheetIds,
                _targetSheetIds.Select(id => id.Value).ToList());
            outer.Children.Add(picker);
            return outer;
        }

        // ── Step 3 — Options ──────────────────────────────────────────────────
        private FrameworkElement BuildOptionsStep()
        {
            var outer = new StackPanel();

            outer.Children.Add(SectionLabel("FALLBACK MATCH — MINIMUM OVERLAP (%)"));
            var stepper = new LemoineInlineStepper
            {
                Value = _overlapPercent, MinValue = 5, MaxValue = 100, Step = 5, Decimals = 0,
                ValueWidth = 56, HorizontalAlignment = HorizontalAlignment.Left,
            };
            stepper.ValueChanged += (s, v) => { _overlapPercent = (int)v; OnValidationChanged(); };
            outer.Children.Add(stepper);
            outer.Children.Add(Note("Views are matched by shared scope box first (exact). This overlap threshold is the " +
                                    "fallback for views with no scope box: how much of a target view's drawn area must " +
                                    "overlap a source view to be treated as counterparts. Lower it if counterparts are " +
                                    "cropped to different sizes; raise it if unrelated views match."));

            outer.Children.Add(OptionCheck(
                "Also align view titles (location + line length)", _alignTitles,
                v => _alignTitles = v));
            outer.Children.Add(Note("After the viewports are aligned, each view title is moved to overlay its source " +
                                    "title and its title line length is matched. Done last, since moving a viewport moves its title."));

            // ── Inherit from source view ──────────────────────────────────────
            outer.Children.Add(SectionLabel2("INHERIT FROM SOURCE VIEW"));

            outer.Children.Add(OptionCheck(
                "Scope box assignment", _inheritScopeBox,
                v => _inheritScopeBox = v));
            outer.Children.Add(Note("Assign the source view's scope box to each matched target view. Applied FIRST — " +
                                    "before alignment — because a scope box rewrites the crop the alignment reads."));

            outer.Children.Add(OptionCheck(
                "Grid 2D extents (trim gridlines to match)", _inheritGrids,
                v => _inheritGrids = v));
            outer.Children.Add(Note("Trim each target grid's per-view (2D) extents so its gridlines start and stop " +
                                    "exactly where they do in the source view."));

            outer.Children.Add(OptionCheck(
                "Crop region visibility", _inheritCropVisibility,
                v => _inheritCropVisibility = v));
            outer.Children.Add(Note("Match whether the crop-region boundary is shown or hidden, to the source view."));

            outer.Children.Add(OptionCheck(
                "Crop size + annotation crop", _inheritCropSize,
                v => _inheritCropSize = v));
            outer.Children.Add(Note("Match the target's crop size and annotation-crop margins to the source. For views " +
                                    "that also inherit a scope box, the scope box governs the crop, so only annotation-crop margins are applied."));

            // ── Mode ──────────────────────────────────────────────────────────
            outer.Children.Add(SectionLabel2("MODE"));
            outer.Children.Add(OptionCheck(
                "Preview only — report matches and errors without moving any viewport", _previewOnly,
                v => _previewOnly = v));
            outer.Children.Add(Note("Run this first to confirm every target sheet has a clean set of counterparts " +
                                    "before committing any moves."));
            return outer;
        }

        // ── Reusable themed checkbox bound to a setter ────────────────────────
        private CheckBox OptionCheck(string text, bool initial, Action<bool> set)
        {
            var cb = new CheckBox
            {
                Content   = text,
                IsChecked = initial,
                Margin    = new Thickness(0, 12, 0, 0),
            };
            cb.SetResourceReference(CheckBox.ForegroundProperty, "LemoineText");
            cb.SetResourceReference(CheckBox.FontFamilyProperty, "LemoineUiFont");
            cb.SetResourceReference(CheckBox.FontSizeProperty,   "LemoineFS_MD");
            cb.Checked   += (s, e) => { set(true);  OnValidationChanged(); };
            cb.Unchecked += (s, e) => { set(false); OnValidationChanged(); };
            return cb;
        }

        // ── ILemoineReviewable ────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("source",  "Reference Sheets"),
            ("targets", "Target Sheets"),
            ("overlap", "Fallback Overlap"),
            ("titles",  "View Titles"),
            ("inherit", "Inherit"),
            ("mode",    "Mode"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["source"]  = EffectiveSourceCount == 0
                ? "— (none selected)"
                : (EffectiveSourceCount == 1
                    ? (_sheetLabels.TryGetValue(_sourceSheetIds[0].Value, out var lbl) ? lbl : _sourceSheetIds[0].Value.ToString())
                    : $"{EffectiveSourceCount} sheet(s)"),
            ["targets"] = EffectiveTargetCount == 0
                ? "— (none selected)"
                : $"{EffectiveTargetCount} sheet(s)",
            ["overlap"] = $"{_overlapPercent}% minimum (scope box matched first)",
            ["titles"]  = _alignTitles ? "Aligned (location + line length)" : "Left unchanged",
            ["inherit"] = InheritSummary,
            ["mode"]    = _previewOnly ? "Preview only (no moves)" : "Align viewports",
        };

        public IList<string>? ReviewChips => null;
        public string?        ReviewNote  =>
            "Each target sheet is aligned to the reference sheet it matches best. Views are paired by shared " +
            "scope box first, crop-region overlap otherwise, then the target viewport is moved so the two register. " +
            "Missing or ambiguous counterparts are reported, never moved.";
        public string?        ReviewWarning => _previewOnly
            ? null
            : "Target viewports" + (_alignTitles ? " and their view titles" : "") +
              (InheritSummary == "Nothing" ? "" : " (plus inherited " + InheritSummary.ToLowerInvariant() + ")") +
              " will be modified on every selected sheet.";

        private string InheritSummary
        {
            get
            {
                var parts = new List<string>();
                if (_inheritScopeBox)       parts.Add("scope box");
                if (_inheritGrids)          parts.Add("grid extents");
                if (_inheritCropVisibility) parts.Add("crop visibility");
                if (_inheritCropSize)       parts.Add("crop size");
                return parts.Count == 0 ? "Nothing" : string.Join(", ", parts);
            }
        }

        // ── Validation / summary ──────────────────────────────────────────────
        private List<long> SourceKeys => _sourceSheetIds.Select(id => id.Value).ToList();

        private int EffectiveSourceCount => _sourceSheetIds.Count;

        private int EffectiveTargetCount
        {
            get
            {
                var srcKeys = new HashSet<long>(SourceKeys);
                return _targetSheetIds.Count(id => !srcKeys.Contains(id.Value));
            }
        }

        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return EffectiveSourceCount > 0;
                case "S2": return EffectiveTargetCount > 0;
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return EffectiveSourceCount == 0
                    ? "—"
                    : (EffectiveSourceCount == 1
                        ? (_sheetLabels.TryGetValue(_sourceSheetIds[0].Value, out var lbl) ? lbl : "1 selected")
                        : $"{EffectiveSourceCount} sheet(s)");
                case "S2": return EffectiveTargetCount == 0 ? "—" : $"{EffectiveTargetCount} sheet(s)";
                case "S3": return $"overlap {_overlapPercent}%" +
                                  (_alignTitles ? "  |  titles" : "") +
                                  (InheritSummary == "Nothing" ? "" : "  |  inherit") +
                                  (_previewOnly ? "  |  preview" : "");
                case "S4": return "Ready to run";
                default:   return "—";
            }
        }

        // ── Run ───────────────────────────────────────────────────────────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

            var srcKeys = new HashSet<long>(SourceKeys);

            _handler.SourceSheetIds        = _sourceSheetIds.ToList();
            _handler.TargetSheetIds        = _targetSheetIds.Where(id => !srcKeys.Contains(id.Value)).ToList();
            _handler.OverlapThreshold      = _overlapPercent / 100.0;
            _handler.AlignTitles           = _alignTitles;
            _handler.PreviewOnly           = _previewOnly;
            _handler.InheritScopeBox       = _inheritScopeBox;
            _handler.InheritGridExtents    = _inheritGrids;
            _handler.InheritCropVisibility = _inheritCropVisibility;
            _handler.InheritCropSize       = _inheritCropSize;
            _handler.PushLog               = pushLog;
            _handler.OnProgress            = onProgress;
            _handler.OnComplete            = onComplete;

            _event.Raise();
        }

        // ── Small UI helpers ──────────────────────────────────────────────────
        private static TextBlock SectionLabel(string text)
        {
            var t = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4) };
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return t;
        }

        // Section header with top spacing, for separating option groups.
        private static TextBlock SectionLabel2(string text)
        {
            var t = new TextBlock { Text = text, Margin = new Thickness(0, 22, 0, 4) };
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return t;
        }

        private static TextBlock Note(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }

        private static TextBlock Hint(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }
    }
}
