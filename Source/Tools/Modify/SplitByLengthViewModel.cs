using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

using WpfGrid = System.Windows.Controls.Grid;

namespace LemoineTools.Tools.ModifyElements
{
    /// <summary>
    /// Step-flow ViewModel for Split by Length: pick the line-based elements, choose the view
    /// scope, set the piece length, run.
    ///
    /// The view step is conditional (<see cref="IConditionalSteps"/>): the scope defaults to the
    /// active view, so the common path is three steps and the picker only appears when the user
    /// turns that off. It sits second so the final Review &amp; Run step — which carries the Run
    /// button and log — is always the last one, as the interface requires.
    ///
    /// Steps 3 and 4 depend on nothing that is built later, so the eager content build
    /// StepFlowWindow does at construction is correct and no <c>IStepAware</c> refresh is needed;
    /// the only live element is the hint line, re-worded in place as the gap changes.
    /// </summary>
    public class SplitByLengthViewModel : IStepFlowTool, IConditionalSteps, IReviewableTool, IRunResult, IToolCleanup
    {
        // Self-describing result label for the run strip (see IRunResult).
        public string? ResultNoun => "pieces";
        public IReadOnlyList<ResultChip>? ResultChips => null;

        public string Title    => AppStrings.T("modify.splitByLength.title");
        public string RunLabel => AppStrings.T("modify.splitByLength.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("modify.splitByLength.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("modify.splitByLength.steps.S2"), required: true),
            new StepDefinition("S3", AppStrings.T("modify.splitByLength.steps.S3"), required: true),
            new StepDefinition("S4", AppStrings.T("modify.splitByLength.steps.S4"), required: false),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private List<string> _selectedCats = new List<string>();

        // Scope defaults to the active view. Turning it off reveals S2 and makes the picked views
        // the scope — there is no whole-document scope, by design.
        private bool         _useActiveView   = true;
        private List<long>   _selectedViewIds = new List<long>();

        private double _segLenFeet  = SplitByLengthSettings.Instance.SegmentLengthFeet;
        private double _gapInches   = SplitByLengthSettings.Instance.GapInches;
        private bool   _evenLengths = SplitByLengthSettings.Instance.EvenLengths;

        // Live hint under the gap field — re-worded, never re-parented (CLAUDE.md).
        private TextBlock? _hint;

        private readonly Dictionary<string, List<string>> _categoryGroups;
        private readonly int                              _totalElements;
        private readonly ElementId?                       _activeViewId;
        private readonly string                           _activeViewName;
        private readonly BrowserTree?                     _browserTree;
        private readonly IReadOnlyList<long>              _eligibleViewIds;
        private readonly IReadOnlyList<ElementId>         _preSelectedIds;
        private readonly IReadOnlyList<string>            _preSelectedCats;
        private readonly int                              _preSelectedIgnored;

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly SplitByLengthEventHandler _handler;
        private readonly ExternalEvent             _event;

        public event EventHandler? ValidationChanged;

        // Null the callbacks parked on the static handler so this VM isn't retained after close,
        // and drop the step-3 WPF reference so the closed window's visual tree can be collected.
        public void OnWindowClosed()
        {
            _hint = null;
            if (_handler == null) return;
            _handler.OnLog      = null;
            _handler.OnProgress = null;
            _handler.OnComplete = null;
        }

        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        /// <param name="categoryGroups">Discipline-grouped categories that actually have line-based elements.</param>
        /// <param name="totalElements">How many line-based elements the document holds, for the count strip.</param>
        /// <param name="browserTree">Project Browser tree captured on the Revit main thread.</param>
        /// <param name="eligibleViewIds">Graphical, non-template views the picker may offer.</param>
        /// <param name="preSelectedIgnored">
        /// How many selected elements were dropped for having no straight curve — surfaced in step
        /// 1 so a partly-ignored selection is never silent.
        /// </param>
        public SplitByLengthViewModel(
            SplitByLengthEventHandler        handler,
            ExternalEvent                    externalEvent,
            Dictionary<string, List<string>> categoryGroups,
            int                              totalElements,
            ElementId?                       activeViewId,
            string                           activeViewName,
            BrowserTree?                     browserTree,
            IReadOnlyList<long>              eligibleViewIds,
            IReadOnlyList<ElementId>         preSelectedIds,
            IReadOnlyList<string>            preSelectedCats,
            int                              preSelectedIgnored)
        {
            _handler            = handler;
            _event              = externalEvent;
            _categoryGroups     = categoryGroups;
            _totalElements      = totalElements;
            _activeViewId       = activeViewId;
            _activeViewName     = activeViewName ?? "";
            _browserTree        = browserTree;
            _eligibleViewIds    = eligibleViewIds;
            _preSelectedIds     = preSelectedIds;
            _preSelectedCats    = preSelectedCats;
            _preSelectedIgnored = preSelectedIgnored;

            if (_preSelectedIds.Count > 0)
                _selectedCats = new List<string>(_preSelectedCats);

            // With no active view to scope to there is nothing for the default to mean, so start
            // on the picker instead of silently scoping to a view that does not exist.
            if (_activeViewId == null) _useActiveView = false;
        }

        // ── IConditionalSteps ─────────────────────────────────────────────────
        // The view picker is pointless when the scope is the active view, and equally pointless
        // when the user launched with a selection (that selection IS the scope).
        public bool IsStepVisible(string stepId)
        {
            if (stepId != "S2") return true;
            return _preSelectedIds.Count == 0 && !_useActiveView;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return BuildS3();
                case "S4": return null;   // framework renders review (IReviewableTool)
                default:   return null;
            }
        }

        private FrameworkElement BuildS1()
        {
            if (_preSelectedIds.Count > 0)
                return BuildS1_PreSelected();

            var outer = new StackPanel();

            int catCount = _categoryGroups.Values.Sum(g => g.Count);
            var countStrip = new TextBlock
            {
                Text         = AppStrings.T("modify.splitByLength.labels.countStrip", catCount, _totalElements),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 6),
            };
            countStrip.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            countStrip.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            countStrip.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            outer.Children.Add(countStrip);

            if (catCount == 0)
            {
                // A zero-result capture says so rather than showing an empty picker (CLAUDE.md).
                outer.Children.Add(Dim(AppStrings.T("modify.splitByLength.labels.noCategories")));
                return outer;
            }

            var tabs = new MultiSelectTabs();
            // Subscribe BEFORE SetGroups — that callback is the only thing that populates the
            // mirror field on initialisation (MultiSelectTabs contract, CLAUDE.md).
            tabs.SelectionChanged += selected =>
            {
                _selectedCats = new List<string>(selected);
                OnValidationChanged();
            };
            tabs.SetGroups(_categoryGroups);
            outer.Children.Add(tabs);

            var toggle = new ToggleSwitches();
            toggle.SetItems(new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id        = "activeView",
                    Label     = AppStrings.T("modify.splitByLength.labels.activeViewLabel"),
                    Desc      = _activeViewId != null
                                    ? AppStrings.T("modify.splitByLength.labels.activeViewDesc", _activeViewName)
                                    : AppStrings.T("modify.splitByLength.labels.activeViewNone"),
                    DefaultOn = _useActiveView,
                },
            });
            toggle.StateChanged += state =>
            {
                if (!state.TryGetValue("activeView", out bool v)) return;
                // No active view to scope to — refuse to turn it on rather than scoping to nothing.
                _useActiveView = v && _activeViewId != null;
                // Re-raising validation is what makes StepFlowWindow re-evaluate IsStepVisible,
                // so the view step appears/disappears live.
                OnValidationChanged();
            };
            outer.Children.Add(toggle);

            return outer;
        }

        private FrameworkElement BuildS1_PreSelected()
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
            };
            card.SetResourceReference(Border.PaddingProperty,     "LemoineTh_CardPad");
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var header = new TextBlock
            {
                Text   = AppStrings.T("modify.splitByLength.labels.fromSelection"),
                Margin = new Thickness(0, 0, 0, 4),
            };
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            header.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var countLine = new TextBlock
            {
                Text         = AppStrings.T("modify.splitByLength.labels.preselCount",
                                            _preSelectedIds.Count, _selectedCats.Count),
                FontWeight   = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap,
            };
            countLine.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            countLine.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            countLine.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var catLine = new TextBlock
            {
                Text         = string.Join("  ·  ", _selectedCats),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 2, 0, 6),
            };
            catLine.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            catLine.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            catLine.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var sp = new StackPanel();
            sp.Children.Add(header);
            sp.Children.Add(countLine);
            sp.Children.Add(catLine);

            // A partly-ignored selection must say so — silently splitting only some of what the
            // user highlighted would look like the tool missed them.
            if (_preSelectedIgnored > 0)
            {
                var ignored = new TextBlock
                {
                    Text         = AppStrings.T("modify.splitByLength.labels.preselIgnored", _preSelectedIgnored),
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 0, 0, 6),
                };
                ignored.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                ignored.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
                ignored.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                sp.Children.Add(ignored);
            }

            var note = new TextBlock
            {
                Text         = AppStrings.T("modify.splitByLength.labels.preselNote"),
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
            };
            note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            sp.Children.Add(note);

            card.Child = sp;
            return card;
        }

        // ── S2: which views the run covers (only when the active-view scope is off) ──
        private FrameworkElement BuildS2()
        {
            var outer = new StackPanel();

            if (_browserTree == null || _eligibleViewIds.Count == 0)
            {
                outer.Children.Add(Dim(AppStrings.T("modify.splitByLength.labels.noViews")));
                return outer;
            }

            var picker = new BrowserTreePicker
            {
                Height         = 300,
                AccessibleName = AppStrings.T("modify.splitByLength.labels.pickerName"),
            };
            // Subscribe BEFORE SetTree — its end-of-setup SelectionChanged seeds the mirror list.
            picker.SelectionChanged += ids =>
            {
                _selectedViewIds = ids.ToList();
                OnValidationChanged();
            };
            picker.SetTree(_browserTree, _eligibleViewIds, _selectedViewIds.ToList());
            outer.Children.Add(picker);

            var note = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Margin          = new Thickness(0, 9, 0, 0),
            };
            note.SetResourceReference(Border.PaddingProperty,     "LemoineTh_CardPad");
            note.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            note.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var noteText = new TextBlock
            {
                Text         = AppStrings.T("modify.splitByLength.labels.viewsNote"),
                TextWrapping = TextWrapping.Wrap,
            };
            noteText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            noteText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            noteText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            note.Child = noteText;
            outer.Children.Add(note);

            return outer;
        }

        private FrameworkElement BuildS3()
        {
            var outer = new StackPanel();

            outer.Children.Add(Label(AppStrings.T("modify.splitByLength.labels.segLength")));
            outer.Children.Add(WithUnit(
                Stepper(_segLenFeet, 0.5, 1000, 1, 1, v => { _segLenFeet = v; UpdateHint(); OnValidationChanged(); }),
                AppStrings.T("modify.splitByLength.labels.unitFeet")));

            outer.Children.Add(Label(AppStrings.T("modify.splitByLength.labels.remainder")));
            string offcut = AppStrings.T("modify.splitByLength.labels.modeOffcut");
            string even   = AppStrings.T("modify.splitByLength.labels.modeEven");
            var mode = new SingleSelect
            {
                AccessibleName = AppStrings.T("modify.splitByLength.labels.remainder"),
                Items          = new List<string> { offcut, even },
            };
            mode.SelectedItem = _evenLengths ? even : offcut;
            mode.SelectionChanged += v =>
            {
                _evenLengths = string.Equals(v, even, StringComparison.Ordinal);
                UpdateHint();
                OnValidationChanged();
            };
            outer.Children.Add(mode);

            outer.Children.Add(Label(AppStrings.T("modify.splitByLength.labels.gap")));
            outer.Children.Add(WithUnit(
                Stepper(_gapInches, 0, 48, 0.25, 2, v => { _gapInches = v; UpdateHint(); OnValidationChanged(); }),
                AppStrings.T("modify.splitByLength.labels.unitInches")));

            var hintCard = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Margin          = new Thickness(0, 10, 0, 0),
            };
            hintCard.SetResourceReference(Border.PaddingProperty,     "LemoineTh_CardPad");
            hintCard.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            hintCard.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            _hint = new TextBlock { TextWrapping = TextWrapping.Wrap };
            _hint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _hint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            _hint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            hintCard.Child = _hint;
            outer.Children.Add(hintCard);

            UpdateHint();
            return outer;
        }

        // Re-words the hint in place. The gap alone decides whether a run can stay connected, and
        // only ducts and pipes can stay connected at all, so the hint is how the user learns what
        // their current numbers will actually do to each category.
        private void UpdateHint()
        {
            if (_hint == null) return;
            _hint.Text = _gapInches <= 0
                ? AppStrings.T("modify.splitByLength.labels.hintConnected")
                : AppStrings.T("modify.splitByLength.labels.hintDetached", _gapInches);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  IReviewableTool — framework renders the review step
        // ═════════════════════════════════════════════════════════════════════
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("cats",  AppStrings.T("modify.splitByLength.review.itemCats")),
            ("scope", AppStrings.T("modify.splitByLength.review.itemScope")),
            ("len",   AppStrings.T("modify.splitByLength.review.itemLength")),
            ("rem",   AppStrings.T("modify.splitByLength.review.itemRemainder")),
            ("gap",   AppStrings.T("modify.splitByLength.review.itemGap")),
            ("op",    AppStrings.T("modify.splitByLength.review.itemOp")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["cats"]  = _selectedCats.Count == 0 ? "—" : string.Join(", ", _selectedCats),
            ["scope"] = ScopeSummary(),
            ["len"]   = AppStrings.T("modify.splitByLength.review.lenValue", _segLenFeet),
            ["rem"]   = _evenLengths ? AppStrings.T("modify.splitByLength.labels.modeEven")
                                     : AppStrings.T("modify.splitByLength.labels.modeOffcut"),
            ["gap"]   = _gapInches <= 0
                            ? AppStrings.T("modify.splitByLength.review.gapNone")
                            : AppStrings.T("modify.splitByLength.review.gapValue", _gapInches),
            ["op"]    = AppStrings.T("modify.splitByLength.review.op", _segLenFeet),
        };

        private string ScopeSummary()
        {
            if (_preSelectedIds.Count > 0)
                return AppStrings.T("modify.splitByLength.review.scopeFromSel", _preSelectedIds.Count);
            if (_useActiveView)
                return AppStrings.T("modify.splitByLength.review.scopeActive", _activeViewName);
            return _selectedViewIds.Count == 0
                ? "—"
                : AppStrings.T("modify.splitByLength.review.scopeViews", _selectedViewIds.Count);
        }

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => AppStrings.T("modify.splitByLength.review.note");
        public string?        ReviewWarning => _gapInches > 0
            ? AppStrings.T("modify.splitByLength.review.warnDetached")
            : null;

        // ═════════════════════════════════════════════════════════════════════
        //  IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _preSelectedIds.Count > 0 || _selectedCats.Count > 0;
            // Only reachable while visible, but guard anyway so a hidden step can never block.
            if (stepId == "S2") return !IsStepVisible("S2") || _selectedViewIds.Count > 0;
            if (stepId == "S3") return _segLenFeet > 0;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
            {
                if (_preSelectedIds.Count > 0)
                    return AppStrings.T("modify.splitByLength.review.scopeFromSel", _preSelectedIds.Count);
                return _selectedCats.Count == 0 ? "—" : string.Join(", ", _selectedCats);
            }
            if (stepId == "S2")
                return _selectedViewIds.Count == 0
                    ? "—"
                    : AppStrings.T("modify.splitByLength.summaries.S2", _selectedViewIds.Count);
            if (stepId == "S3")
                return AppStrings.T("modify.splitByLength.summaries.S3",
                    _segLenFeet,
                    _evenLengths ? AppStrings.T("modify.splitByLength.labels.modeEven")
                                 : AppStrings.T("modify.splitByLength.labels.modeOffcut"),
                    _gapInches <= 0 ? AppStrings.T("modify.splitByLength.review.gapNone")
                                    : AppStrings.T("modify.splitByLength.review.gapValue", _gapInches));
            if (stepId == "S4")
                return AppStrings.T("modify.splitByLength.summaries.S4");
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            PersistSettings();

            _handler.PreSelectedIds        = _preSelectedIds.Count > 0 ? new List<ElementId>(_preSelectedIds) : null;
            _handler.SelectedCategoryNames = new List<string>(_selectedCats);

            // Exactly one of these two carries the scope, so the handler never has to guess.
            _handler.ActiveViewId    = _useActiveView ? _activeViewId : null;
            _handler.SelectedViewIds = _useActiveView
                ? null
                : _selectedViewIds.Select(id => new ElementId(id)).ToList();

            _handler.SegmentLengthFeet = _segLenFeet;
            _handler.GapFeet           = _gapInches / 12.0;
            _handler.EvenLengths       = _evenLengths;

            _handler.OnLog      = pushLog;
            _handler.OnProgress = onProgress;
            _handler.OnComplete = onComplete;

            _event.Raise();
        }

        private void PersistSettings()
        {
            var s = SplitByLengthSettings.Instance;
            s.SegmentLengthFeet = _segLenFeet;
            s.GapInches         = _gapInches;
            s.EvenLengths       = _evenLengths;
            s.Save();
        }

        // ── Small builders ────────────────────────────────────────────────────

        private static TextBlock Label(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 10, 0, 5) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock Dim(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static InlineStepper Stepper(
            double value, double min, double max, double step, int decimals, Action<double> onChange)
        {
            var st = new InlineStepper
            {
                MinValue = min, MaxValue = max, Step = step, Decimals = decimals, Value = value,
            };
            st.ValueChanged += (s, v) => onChange(v);
            return st;
        }

        // Stepper + unit caption on one line. A 3-column Grid (Auto | Auto | *) rather than a
        // horizontal StackPanel so the trailing space stays inert and nothing is measured against
        // an infinite width.
        private static FrameworkElement WithUnit(InlineStepper stepper, string unit)
        {
            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var caption = new TextBlock
            {
                Text              = unit,
                Margin            = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            caption.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            caption.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            caption.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            WpfGrid.SetColumn(stepper, 0);
            WpfGrid.SetColumn(caption, 1);
            grid.Children.Add(stepper);
            grid.Children.Add(caption);
            return grid;
        }
    }
}
