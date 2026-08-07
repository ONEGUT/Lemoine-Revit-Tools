using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;
// This file imports both Autodesk.Revit.DB and the WPF namespaces, so any type name the two
// could share is aliased rather than left bare.
using WpfOrientation       = System.Windows.Controls.Orientation;
using WpfTextAlignment     = System.Windows.TextAlignment;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;

namespace LemoineTools.Tools.Sheets.AlignSheetViews
{
    /// <summary>
    /// Picks one source/reference sheet and a set of target sheets, then aligns each target
    /// sheet's viewports so its views overlay their counterparts on the source sheet. The
    /// source sheet's viewports are ground truth and are never moved.
    /// </summary>
    public sealed class AlignSheetViewsViewModel : IStepFlowTool, IReviewableTool, IStepAware, IToolCleanup
    {
        // ── IStepFlowTool ──────────────────────────────────────────────────────
        public string Title    => AppStrings.T("testing.alignSheetViews.title");
        public string RunLabel => AppStrings.T("testing.alignSheetViews.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("testing.alignSheetViews.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("testing.alignSheetViews.steps.S2"), required: true),
            new StepDefinition("S3", AppStrings.T("testing.alignSheetViews.steps.S3"),       required: false),
            new StepDefinition("S4", AppStrings.T("testing.alignSheetViews.steps.S4"),  required: false),
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
        private readonly BrowserTree            _browserTree;
        private readonly List<long>                    _sheetIds;
        private readonly Dictionary<long, string>      _sheetLabels;

        private List<ElementId> _sourceSheetIds = new List<ElementId>();
        private List<ElementId> _targetSheetIds = new List<ElementId>();
        private int             _overlapPercent = 50;

        // Inheritance toggles
        private bool _inheritGrids          = false;
        private bool _inheritScopeBox       = false;
        private bool _inheritCropVisibility = false;
        private bool _inheritCropSize       = false;

        // Sheet content — not an inheritance toggle: a legend has no crop and no world anchor, so
        // this copies a sheet placement rather than a property of a matched view.
        private bool _placeLegends          = false;

        // A scope box governs the crop rectangle it is assigned to, so a view that inherits one
        // inherits its crop size with it — there is nothing left for a separate crop-size choice to
        // decide. The checkbox is replaced by a static "inherited" row while scope box is ticked.
        private bool CropSizeInherited => _inheritCropSize || _inheritScopeBox;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly AlignSheetViewsEventHandler? _handler;
        private readonly ExternalEvent?               _event;

        // ── Constructors ──────────────────────────────────────────────────────
        public AlignSheetViewsViewModel(
            AlignSheetViewsEventHandler? handler,
            ExternalEvent?               externalEvent,
            IEnumerable<(ElementId Id, string Label)>? sheets,
            BrowserTree?          browserTree = null)
        {
            _handler     = handler;
            _event       = externalEvent;
            _browserTree = browserTree ?? new BrowserTree();

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

        // ── IStepAware ────────────────────────────────────────────────────────
        // S2's eligible sheets depend on S1's selection (reference sheets are excluded
        // from the target picker), so S2 must be rebuilt every time it is activated —
        // step content is built once at window construction and would otherwise keep
        // listing whatever was eligible before the source sheets were picked.
        // S3 is rebuilt on demand instead: ticking "scope box" changes which options exist.
        private Action<string>? _refreshStep;
        public void SetContentRefreshCallback(Action<string> rebuildStepContent) => _refreshStep = rebuildStepContent;

        public void OnStepActivated(string stepId)
        {
            if (stepId == "S2") _refreshStep?.Invoke(stepId);
        }

        // ── Content ───────────────────────────────────────────────────────────
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildSourceStep();
                case "S2": return BuildTargetStep();
                case "S3": return BuildOptionsStep();
                case "S4": return null; // framework renders IReviewableTool
                default:   return null;
            }
        }

        // ── Step 1 — Source sheets (multi-select) ─────────────────────────────
        private FrameworkElement BuildSourceStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(SectionLabel(AppStrings.T("testing.alignSheetViews.labels.secReference")));

            if (_sheetIds.Count == 0)
            {
                outer.Children.Add(Hint(AppStrings.T("testing.alignSheetViews.labels.noSheets")));
                return outer;
            }

            var picker = new BrowserTreePicker
            {
                Height         = 280,
                AccessibleName = AppStrings.T("testing.alignSheetViews.labels.pickerSource"),
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
            outer.Children.Add(SectionLabel(AppStrings.T("testing.alignSheetViews.labels.secTargets")));

            if (_sheetIds.Count == 0)
            {
                outer.Children.Add(Hint(AppStrings.T("testing.alignSheetViews.labels.noSheets")));
                return outer;
            }

            // Reference sheets are ground truth and are never moved, so they are not
            // offered as targets at all — a sheet picked in S1 is dropped from the
            // eligible set here (and from any selection carried over from an earlier
            // visit to this step) rather than being listed and silently skipped.
            var srcKeys      = new HashSet<long>(SourceKeys);
            var eligibleKeys = _sheetIds.Where(k => !srcKeys.Contains(k)).ToList();

            if (eligibleKeys.Count == 0)
            {
                outer.Children.Add(Hint(AppStrings.T("testing.alignSheetViews.labels.noTargetsLeft")));
                return outer;
            }

            var picker = new BrowserTreePicker
            {
                Height         = 320,
                AccessibleName = AppStrings.T("testing.alignSheetViews.labels.pickerTarget"),
                Margin         = new Thickness(0, 8, 0, 0),
            };
            picker.SelectionChanged += ids =>
            {
                _targetSheetIds = ids.Select(id => new ElementId(id)).ToList();
                OnValidationChanged();
            };
            picker.SetTree(_browserTree, eligibleKeys,
                _targetSheetIds.Select(id => id.Value).Where(k => !srcKeys.Contains(k)).ToList());
            outer.Children.Add(picker);
            return outer;
        }

        // ── Step 3 — Options ──────────────────────────────────────────────────
        private FrameworkElement BuildOptionsStep()
        {
            var outer = new StackPanel();

            outer.Children.Add(SectionLabel(AppStrings.T("testing.alignSheetViews.labels.secOverlap")));
            var stepper = new InlineStepper
            {
                Value = _overlapPercent, MinValue = 5, MaxValue = 100, Step = 5, Decimals = 0,
                ValueWidth = 56, HorizontalAlignment = HorizontalAlignment.Left,
            };
            stepper.ValueChanged += (s, v) => { _overlapPercent = (int)v; OnValidationChanged(); };
            outer.Children.Add(stepper);

            // ── Inherit from source view ──────────────────────────────────────
            outer.Children.Add(SectionLabel2(AppStrings.T("testing.alignSheetViews.labels.secInherit")));

            outer.Children.Add(OptionCheck(
                AppStrings.T("testing.alignSheetViews.labels.optScopeBox"), _inheritScopeBox,
                v =>
                {
                    _inheritScopeBox = v;
                    // Ticking this absorbs the crop-size choice, so the step has to be rebuilt for
                    // the checkbox to be swapped for the "inherited" row (and back).
                    _refreshStep?.Invoke("S3");
                }));

            if (_inheritScopeBox)
                outer.Children.Add(ImpliedRow(AppStrings.T("testing.alignSheetViews.labels.impliedCropSize")));
            else
                outer.Children.Add(OptionCheck(
                    AppStrings.T("testing.alignSheetViews.labels.optCropSize"), _inheritCropSize,
                    v => _inheritCropSize = v));

            outer.Children.Add(OptionCheck(
                AppStrings.T("testing.alignSheetViews.labels.optGrids"), _inheritGrids,
                v => _inheritGrids = v));

            outer.Children.Add(OptionCheck(
                AppStrings.T("testing.alignSheetViews.labels.optCropVis"), _inheritCropVisibility,
                v => _inheritCropVisibility = v));

            // ── Sheet content ─────────────────────────────────────────────────
            // Its own section rather than another row under "inherit from source view": legends are
            // not a property of a matched view, they are sheet content copied from the reference.
            outer.Children.Add(SectionLabel2(AppStrings.T("testing.alignSheetViews.labels.secSheetContent")));

            outer.Children.Add(OptionCheck(
                AppStrings.T("testing.alignSheetViews.labels.optLegends"), _placeLegends,
                v => _placeLegends = v));

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

        /// <summary>A read-only row in a checkbox's place, for a setting another option has already
        /// decided. Not a disabled checkbox: there is nothing here for the user to change.</summary>
        private static StackPanel ImpliedRow(string text)
        {
            var row = new StackPanel
            {
                Orientation = WpfOrientation.Horizontal,
                Margin      = new Thickness(0, 12, 0, 0),
            };

            var tick = new TextBlock
            {
                Text                = char.ConvertFromUtf32(0x2713),   // ✓
                Width               = 13,
                TextAlignment       = WpfTextAlignment.Center,
                VerticalAlignment   = WpfVerticalAlignment.Center,
            };
            tick.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            tick.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tick.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            row.Children.Add(tick);

            var label = new TextBlock
            {
                Text              = text,
                Margin            = new Thickness(8, 0, 0, 0),
                TextWrapping      = TextWrapping.Wrap,
                VerticalAlignment = WpfVerticalAlignment.Center,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            label.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            label.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            row.Children.Add(label);

            return row;
        }

        // ── IReviewableTool ────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("source",  AppStrings.T("testing.alignSheetViews.review.itemSource")),
            ("targets", AppStrings.T("testing.alignSheetViews.review.itemTargets")),
            ("overlap", AppStrings.T("testing.alignSheetViews.review.itemOverlap")),
            ("inherit", AppStrings.T("testing.alignSheetViews.review.itemInherit")),
            ("legends", AppStrings.T("testing.alignSheetViews.review.itemLegends")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["source"]  = EffectiveSourceCount == 0
                ? AppStrings.T("testing.alignSheetViews.review.none")
                : (EffectiveSourceCount == 1
                    ? (_sheetLabels.TryGetValue(_sourceSheetIds[0].Value, out var lbl) ? lbl : _sourceSheetIds[0].Value.ToString())
                    : AppStrings.T("testing.alignSheetViews.review.sheetCount", EffectiveSourceCount)),
            ["targets"] = EffectiveTargetCount == 0
                ? AppStrings.T("testing.alignSheetViews.review.none")
                : AppStrings.T("testing.alignSheetViews.review.sheetCount", EffectiveTargetCount),
            ["overlap"] = AppStrings.T("testing.alignSheetViews.review.overlapValue", _overlapPercent),
            ["inherit"] = InheritSummary,
            ["legends"] = _placeLegends
                ? AppStrings.T("testing.alignSheetViews.review.legendsOn")
                : AppStrings.T("testing.alignSheetViews.review.legendsOff"),
        };

        public IList<string>? ReviewChips => null;
        public string?        ReviewNote  => null;
        public string?        ReviewWarning => AppStrings.T("testing.alignSheetViews.review.warning",
            InheritSummary == AppStrings.T("testing.alignSheetViews.inherit.nothing")
                ? ""
                : AppStrings.T("testing.alignSheetViews.review.warnInherit", InheritSummary.ToLowerInvariant()),
            _placeLegends ? AppStrings.T("testing.alignSheetViews.review.warnLegends") : "");

        private string InheritSummary
        {
            get
            {
                var parts = new List<string>();
                if (_inheritScopeBox)       parts.Add(AppStrings.T("testing.alignSheetViews.inherit.scopeBox"));
                if (CropSizeInherited)      parts.Add(AppStrings.T("testing.alignSheetViews.inherit.cropSize"));
                if (_inheritGrids)          parts.Add(AppStrings.T("testing.alignSheetViews.inherit.gridExtents"));
                if (_inheritCropVisibility) parts.Add(AppStrings.T("testing.alignSheetViews.inherit.cropVisibility"));
                return parts.Count == 0 ? AppStrings.T("testing.alignSheetViews.inherit.nothing") : string.Join(", ", parts);
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
                        ? (_sheetLabels.TryGetValue(_sourceSheetIds[0].Value, out var lbl) ? lbl : AppStrings.T("testing.alignSheetViews.summaries.s1Single"))
                        : AppStrings.T("testing.alignSheetViews.review.sheetCount", EffectiveSourceCount));
                case "S2": return EffectiveTargetCount == 0 ? "—" : AppStrings.T("testing.alignSheetViews.review.sheetCount", EffectiveTargetCount);
                case "S3": return AppStrings.T("testing.alignSheetViews.summaries.s3Overlap", _overlapPercent) +
                                  (InheritSummary == AppStrings.T("testing.alignSheetViews.inherit.nothing") ? "" : AppStrings.T("testing.alignSheetViews.summaries.s3Inherit")) +
                                  (_placeLegends ? AppStrings.T("testing.alignSheetViews.summaries.s3Legends") : "");
                case "S4": return AppStrings.T("testing.alignSheetViews.summaries.S4");
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
            _handler.InheritScopeBox       = _inheritScopeBox;
            _handler.InheritGridExtents    = _inheritGrids;
            _handler.InheritCropVisibility = _inheritCropVisibility;
            _handler.InheritCropSize       = CropSizeInherited;
            _handler.PlaceLegends          = _placeLegends;
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
