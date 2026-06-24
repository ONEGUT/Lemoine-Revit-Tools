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
            new StepDefinition("S1", "Source Sheet",  required: true),
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

        private ElementId       _sourceSheetId = ElementId.InvalidElementId;
        private List<ElementId> _targetSheetIds = new List<ElementId>();
        private int             _overlapPercent = 50;
        private bool            _previewOnly    = false;

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

        // ── Step 1 — Source sheet (single-select) ─────────────────────────────
        private FrameworkElement BuildSourceStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(SectionLabel("REFERENCE SHEET"));
            outer.Children.Add(Note("Its viewport positions are the ground truth. This sheet is never moved — " +
                                    "every target sheet's views are aligned to overlay it."));

            if (_sheetIds.Count == 0)
            {
                outer.Children.Add(Hint("No sheets were found in this project."));
                return outer;
            }

            var picker = new LemoineBrowserTreePicker
            {
                Height         = 280,
                SingleSelect   = true,
                AccessibleName = "Source reference sheet",
                Margin         = new Thickness(0, 8, 0, 0),
            };
            // Subscribe BEFORE SetTree — its end-of-setup SelectionChanged seeds the mirror field.
            picker.SelectionChanged += ids =>
            {
                _sourceSheetId = ids.Count > 0 ? new ElementId(ids.First()) : ElementId.InvalidElementId;
                OnValidationChanged();
            };
            picker.SetTree(_browserTree, _sheetIds,
                _sourceSheetId != ElementId.InvalidElementId
                    ? new List<long> { _sourceSheetId.Value }
                    : null);
            outer.Children.Add(picker);
            return outer;
        }

        // ── Step 2 — Target sheets (multi-select) ─────────────────────────────
        private FrameworkElement BuildTargetStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(SectionLabel("SHEETS TO ALIGN"));
            outer.Children.Add(Note("Each target sheet's views are moved to overlay their counterparts on the " +
                                    "reference sheet. If the reference sheet is ticked here it is ignored."));

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

            outer.Children.Add(SectionLabel("MINIMUM OVERLAP TO MATCH (%)"));
            var stepper = new LemoineInlineStepper
            {
                Value = _overlapPercent, MinValue = 5, MaxValue = 100, Step = 5, Decimals = 0,
                ValueWidth = 56, HorizontalAlignment = HorizontalAlignment.Left,
            };
            stepper.ValueChanged += (s, v) => { _overlapPercent = (int)v; OnValidationChanged(); };
            outer.Children.Add(stepper);
            outer.Children.Add(Note("How much of a target view's drawn area must overlap a source view (in the " +
                                    "source view's own plane) for the two to be treated as counterparts. Lower this " +
                                    "if counterparts are cropped to different sizes; raise it if unrelated views match."));

            var preview = new CheckBox
            {
                Content   = "Preview only — report matches and errors without moving any viewport",
                IsChecked = _previewOnly,
                Margin    = new Thickness(0, 16, 0, 0),
            };
            preview.SetResourceReference(CheckBox.ForegroundProperty, "LemoineText");
            preview.SetResourceReference(CheckBox.FontFamilyProperty, "LemoineUiFont");
            preview.SetResourceReference(CheckBox.FontSizeProperty,   "LemoineFS_MD");
            preview.Checked   += (s, e) => { _previewOnly = true;  OnValidationChanged(); };
            preview.Unchecked += (s, e) => { _previewOnly = false; OnValidationChanged(); };
            outer.Children.Add(preview);
            outer.Children.Add(Note("Run this first to confirm every target sheet has a clean set of counterparts " +
                                    "before committing any moves."));
            return outer;
        }

        // ── ILemoineReviewable ────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("source",  "Reference Sheet"),
            ("targets", "Target Sheets"),
            ("overlap", "Match Overlap"),
            ("mode",    "Mode"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["source"]  = _sourceSheetId == ElementId.InvalidElementId
                ? "— (none selected)"
                : (_sheetLabels.TryGetValue(_sourceSheetId.Value, out var lbl) ? lbl : _sourceSheetId.Value.ToString()),
            ["targets"] = EffectiveTargetCount == 0
                ? "— (none selected)"
                : $"{EffectiveTargetCount} sheet(s)",
            ["overlap"] = $"{_overlapPercent}% minimum",
            ["mode"]    = _previewOnly ? "Preview only (no moves)" : "Align viewports",
        };

        public IList<string>? ReviewChips => null;
        public string?        ReviewNote  =>
            "Matches each source view to a target view by model-region overlap, then moves the target " +
            "viewport so the two register. Missing or ambiguous counterparts are reported, never moved.";
        public string?        ReviewWarning => _previewOnly
            ? null
            : "Target viewports will be repositioned on every selected sheet.";

        // ── Validation / summary ──────────────────────────────────────────────
        private int EffectiveTargetCount =>
            _targetSheetIds.Count(id => id != _sourceSheetId);

        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _sourceSheetId != ElementId.InvalidElementId;
                case "S2": return EffectiveTargetCount > 0;
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _sourceSheetId == ElementId.InvalidElementId
                    ? "—"
                    : (_sheetLabels.TryGetValue(_sourceSheetId.Value, out var lbl) ? lbl : "1 selected");
                case "S2": return EffectiveTargetCount == 0 ? "—" : $"{EffectiveTargetCount} sheet(s)";
                case "S3": return $"overlap {_overlapPercent}%" + (_previewOnly ? "  |  preview" : "");
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

            _handler.SourceSheetId    = _sourceSheetId;
            _handler.TargetSheetIds   = _targetSheetIds.Where(id => id != _sourceSheetId).ToList();
            _handler.OverlapThreshold = _overlapPercent / 100.0;
            _handler.PreviewOnly      = _previewOnly;
            _handler.PushLog          = pushLog;
            _handler.OnProgress       = onProgress;
            _handler.OnComplete       = onComplete;

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
