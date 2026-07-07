using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.Sheets.AlignSheetViews
{
    /// <summary>
    /// Picks one source/reference sheet and a set of target sheets, then aligns each target
    /// sheet's viewports so its views overlay their counterparts on the source sheet. The
    /// source sheet's viewports are ground truth and are never moved.
    /// </summary>
    public sealed class AlignSheetViewsViewModel : ILemoineTool, ILemoineReviewable, ILemoineToolCleanup
    {
        // ── ILemoineTool ──────────────────────────────────────────────────────
        public string Title    => LemoineStrings.T("testing.alignSheetViews.title");
        public string RunLabel => LemoineStrings.T("testing.alignSheetViews.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", LemoineStrings.T("testing.alignSheetViews.steps.S1"), required: true),
            new StepDefinition("S2", LemoineStrings.T("testing.alignSheetViews.steps.S2"), required: true),
            new StepDefinition("S3", LemoineStrings.T("testing.alignSheetViews.steps.S3"),       required: false),
            new StepDefinition("S4", LemoineStrings.T("testing.alignSheetViews.steps.S4"),  required: false),
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
            outer.Children.Add(SectionLabel(LemoineStrings.T("testing.alignSheetViews.labels.secReference")));
            outer.Children.Add(Note(LemoineStrings.T("testing.alignSheetViews.labels.noteReference")));

            if (_sheetIds.Count == 0)
            {
                outer.Children.Add(Hint(LemoineStrings.T("testing.alignSheetViews.labels.noSheets")));
                return outer;
            }

            var picker = new LemoineBrowserTreePicker
            {
                Height         = 280,
                AccessibleName = LemoineStrings.T("testing.alignSheetViews.labels.pickerSource"),
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
            outer.Children.Add(SectionLabel(LemoineStrings.T("testing.alignSheetViews.labels.secTargets")));
            outer.Children.Add(Note(LemoineStrings.T("testing.alignSheetViews.labels.noteTargets")));

            if (_sheetIds.Count == 0)
            {
                outer.Children.Add(Hint(LemoineStrings.T("testing.alignSheetViews.labels.noSheets")));
                return outer;
            }

            var picker = new LemoineBrowserTreePicker
            {
                Height         = 320,
                AccessibleName = LemoineStrings.T("testing.alignSheetViews.labels.pickerTarget"),
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

            outer.Children.Add(SectionLabel(LemoineStrings.T("testing.alignSheetViews.labels.secOverlap")));
            var stepper = new LemoineInlineStepper
            {
                Value = _overlapPercent, MinValue = 5, MaxValue = 100, Step = 5, Decimals = 0,
                ValueWidth = 56, HorizontalAlignment = HorizontalAlignment.Left,
            };
            stepper.ValueChanged += (s, v) => { _overlapPercent = (int)v; OnValidationChanged(); };
            outer.Children.Add(stepper);
            outer.Children.Add(Note(LemoineStrings.T("testing.alignSheetViews.labels.noteOverlap")));

            outer.Children.Add(OptionCheck(
                LemoineStrings.T("testing.alignSheetViews.labels.optAlignTitles"), _alignTitles,
                v => _alignTitles = v));
            outer.Children.Add(Note(LemoineStrings.T("testing.alignSheetViews.labels.noteAlignTitles")));

            // ── Inherit from source view ──────────────────────────────────────
            outer.Children.Add(SectionLabel2(LemoineStrings.T("testing.alignSheetViews.labels.secInherit")));

            outer.Children.Add(OptionCheck(
                LemoineStrings.T("testing.alignSheetViews.labels.optScopeBox"), _inheritScopeBox,
                v => _inheritScopeBox = v));
            outer.Children.Add(Note(LemoineStrings.T("testing.alignSheetViews.labels.noteScopeBox")));

            outer.Children.Add(OptionCheck(
                LemoineStrings.T("testing.alignSheetViews.labels.optGrids"), _inheritGrids,
                v => _inheritGrids = v));
            outer.Children.Add(Note(LemoineStrings.T("testing.alignSheetViews.labels.noteGrids")));

            outer.Children.Add(OptionCheck(
                LemoineStrings.T("testing.alignSheetViews.labels.optCropVis"), _inheritCropVisibility,
                v => _inheritCropVisibility = v));
            outer.Children.Add(Note(LemoineStrings.T("testing.alignSheetViews.labels.noteCropVis")));

            outer.Children.Add(OptionCheck(
                LemoineStrings.T("testing.alignSheetViews.labels.optCropSize"), _inheritCropSize,
                v => _inheritCropSize = v));
            outer.Children.Add(Note(LemoineStrings.T("testing.alignSheetViews.labels.noteCropSize")));

            // ── Mode ──────────────────────────────────────────────────────────
            outer.Children.Add(SectionLabel2(LemoineStrings.T("testing.alignSheetViews.labels.secMode")));
            outer.Children.Add(OptionCheck(
                LemoineStrings.T("testing.alignSheetViews.labels.optPreview"), _previewOnly,
                v => _previewOnly = v));
            outer.Children.Add(Note(LemoineStrings.T("testing.alignSheetViews.labels.notePreview")));
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
            ("source",  LemoineStrings.T("testing.alignSheetViews.review.itemSource")),
            ("targets", LemoineStrings.T("testing.alignSheetViews.review.itemTargets")),
            ("overlap", LemoineStrings.T("testing.alignSheetViews.review.itemOverlap")),
            ("titles",  LemoineStrings.T("testing.alignSheetViews.review.itemTitles")),
            ("inherit", LemoineStrings.T("testing.alignSheetViews.review.itemInherit")),
            ("mode",    LemoineStrings.T("testing.alignSheetViews.review.itemMode")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["source"]  = EffectiveSourceCount == 0
                ? LemoineStrings.T("testing.alignSheetViews.review.none")
                : (EffectiveSourceCount == 1
                    ? (_sheetLabels.TryGetValue(_sourceSheetIds[0].Value, out var lbl) ? lbl : _sourceSheetIds[0].Value.ToString())
                    : LemoineStrings.T("testing.alignSheetViews.review.sheetCount", EffectiveSourceCount)),
            ["targets"] = EffectiveTargetCount == 0
                ? LemoineStrings.T("testing.alignSheetViews.review.none")
                : LemoineStrings.T("testing.alignSheetViews.review.sheetCount", EffectiveTargetCount),
            ["overlap"] = LemoineStrings.T("testing.alignSheetViews.review.overlapValue", _overlapPercent),
            ["titles"]  = _alignTitles ? LemoineStrings.T("testing.alignSheetViews.review.titlesAligned") : LemoineStrings.T("testing.alignSheetViews.review.titlesUnchanged"),
            ["inherit"] = InheritSummary,
            ["mode"]    = _previewOnly ? LemoineStrings.T("testing.alignSheetViews.review.modePreview") : LemoineStrings.T("testing.alignSheetViews.review.modeAlign"),
        };

        public IList<string>? ReviewChips => null;
        public string?        ReviewNote  => LemoineStrings.T("testing.alignSheetViews.review.note");
        public string?        ReviewWarning => _previewOnly
            ? null
            : LemoineStrings.T("testing.alignSheetViews.review.warning", (_alignTitles ? LemoineStrings.T("testing.alignSheetViews.review.warnTitles") : ""), (InheritSummary == LemoineStrings.T("testing.alignSheetViews.inherit.nothing") ? "" : LemoineStrings.T("testing.alignSheetViews.review.warnInherit", InheritSummary.ToLowerInvariant())));

        private string InheritSummary
        {
            get
            {
                var parts = new List<string>();
                if (_inheritScopeBox)       parts.Add(LemoineStrings.T("testing.alignSheetViews.inherit.scopeBox"));
                if (_inheritGrids)          parts.Add(LemoineStrings.T("testing.alignSheetViews.inherit.gridExtents"));
                if (_inheritCropVisibility) parts.Add(LemoineStrings.T("testing.alignSheetViews.inherit.cropVisibility"));
                if (_inheritCropSize)       parts.Add(LemoineStrings.T("testing.alignSheetViews.inherit.cropSize"));
                return parts.Count == 0 ? LemoineStrings.T("testing.alignSheetViews.inherit.nothing") : string.Join(", ", parts);
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
                        ? (_sheetLabels.TryGetValue(_sourceSheetIds[0].Value, out var lbl) ? lbl : LemoineStrings.T("testing.alignSheetViews.summaries.s1Single"))
                        : LemoineStrings.T("testing.alignSheetViews.review.sheetCount", EffectiveSourceCount));
                case "S2": return EffectiveTargetCount == 0 ? "—" : LemoineStrings.T("testing.alignSheetViews.review.sheetCount", EffectiveTargetCount);
                case "S3": return LemoineStrings.T("testing.alignSheetViews.summaries.s3Overlap", _overlapPercent) +
                                  (_alignTitles ? LemoineStrings.T("testing.alignSheetViews.summaries.s3Titles") : "") +
                                  (InheritSummary == LemoineStrings.T("testing.alignSheetViews.inherit.nothing") ? "" : LemoineStrings.T("testing.alignSheetViews.summaries.s3Inherit")) +
                                  (_previewOnly ? LemoineStrings.T("testing.alignSheetViews.summaries.s3Preview") : "");
                case "S4": return LemoineStrings.T("testing.alignSheetViews.summaries.S4");
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
