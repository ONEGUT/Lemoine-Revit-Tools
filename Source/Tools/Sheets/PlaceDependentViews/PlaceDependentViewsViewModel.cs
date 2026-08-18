using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;
using LemoineTools.Framework.Naming;
using LemoineTools.Framework.Sheets;

namespace LemoineTools.Tools.Sheets.PlaceDependentViews
{
    public sealed class PlaceDependentViewsViewModel : IStepFlowTool, IReviewableTool, IStepAware, IToolCleanup
    {
        private const string ToolId         = "sheets.placeDependent";
        private const string DefaultPattern = "{ParentViewName}";

        // ── IStepFlowTool ──────────────────────────────────────────────────────
        public string Title    => AppStrings.T("testing.placeDependentViews.title");
        public string RunLabel => AppStrings.T("testing.placeDependentViews.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("testing.placeDependentViews.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("testing.placeDependentViews.steps.S2"), required: true),
            new StepDefinition("S3", AppStrings.T("testing.placeDependentViews.steps.S3"), required: true),
            new StepDefinition("S4", AppStrings.T("testing.placeDependentViews.steps.S4"), required: true),
            new StepDefinition("S5", AppStrings.T("testing.placeDependentViews.steps.S5"), required: false),
            new StepDefinition("S6", AppStrings.T("testing.placeDependentViews.steps.S6"), required: true),
            new StepDefinition("S7", AppStrings.T("testing.placeDependentViews.steps.S7"), required: false),
            new StepDefinition("S8", AppStrings.T("testing.placeDependentViews.steps.S8"), required: false),
        };

        public event EventHandler? ValidationChanged;

        // Null the callbacks parked on the static handler so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog    = null;
            _handler.OnProgress = null;
            _handler.OnComplete = null;
        }

        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // Mode labels are compared by reference against these same instances, so they are loaded
        // once rather than re-resolved per comparison.
        private static readonly string ModePlaceViews = AppStrings.T("testing.placeDependentViews.modePlaceViews");
        private static readonly string ModeDependents = AppStrings.T("testing.placeDependentViews.modeDependents");
        private static readonly string ModeComposite  = AppStrings.T("testing.placeDependentViews.modeComposite");

        // ── State ─────────────────────────────────────────────────────────────
        // One view per sheet is the default: it is the mode that applies to any view at all, where
        // the other two need dependents or markers to exist first.
        private PlaceViewsMode _mode = PlaceViewsMode.OneViewPerSheet;

        private readonly List<ParentViewEntry> _parents;
        private readonly List<ParentViewEntry> _compositeCandidates;
        private readonly List<ParentViewEntry> _placeableViews;
        private readonly BrowserTree           _browserTree;
        private readonly Dictionary<long, ParentViewEntry> _entryById;

        private List<ElementId> _selectedParentIds    = new List<ElementId>();
        private List<ElementId> _selectedCompositeIds = new List<ElementId>();
        private List<ElementId> _selectedPlaceableIds = new List<ElementId>();

        /// <summary>The sheet order the run uses, one entry per selected view. Kept in step with the
        /// picker (new picks append, dropped picks vanish) so an arrangement survives an edit.</summary>
        private readonly List<ElementId> _order = new List<ElementId>();

        private readonly List<string>                  _titleblockNames;
        private readonly Dictionary<string, ElementId> _titleblockMap;
        private readonly Dictionary<string, (double W, double H)> _titleblockSizes;
        private string _selectedTitleblock = "";

        private double _marginTop, _marginBottom, _marginLeft, _marginRight;
        private double _gapInches = SheetMarginStore.DefaultGapIn;

        private int    _startingNumber = 1;
        private string _numberPrefix   = "";
        private int    _numberDigits   = 3;
        private readonly HashSet<string> _usedNumbers;

        private readonly List<SheetSeriesParam> _seriesParams;
        private SheetSeriesParam? _seriesParam;
        private string _seriesValue = "";

        private string _namingPattern = NamingPatternStore.Instance.GetOrDefault(ToolId, DefaultPattern);

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly PlaceDependentViewsEventHandler? _handler;
        private readonly ExternalEvent?                   _event;

        // ── Constructors ──────────────────────────────────────────────────────
        public PlaceDependentViewsViewModel(
            PlaceDependentViewsEventHandler? handler,
            ExternalEvent?                   externalEvent,
            List<ParentViewEntry>?           parents,
            List<ParentViewEntry>?           compositeCandidates,
            List<FamilySymbol>?              titleblocks,
            BrowserTree?                     browserTree = null,
            List<ParentViewEntry>?           placeableViews = null,
            Dictionary<string, (double W, double H)>? titleblockSizes = null,
            HashSet<string>?                 usedSheetNumbers = null,
            List<SheetSeriesParam>?          seriesParams = null)
        {
            _handler     = handler;
            _event       = externalEvent;
            _browserTree = browserTree ?? new BrowserTree();

            _parents = (parents ?? new List<ParentViewEntry>())
                .OrderBy(p => p.TypeLabel).ThenBy(p => p.Name, NaturalOrderComparer.OrdinalIgnoreCase).ToList();

            _compositeCandidates = (compositeCandidates ?? new List<ParentViewEntry>())
                .OrderBy(p => p.TypeLabel).ThenBy(p => p.Name, NaturalOrderComparer.OrdinalIgnoreCase).ToList();

            _placeableViews = (placeableViews ?? new List<ParentViewEntry>())
                .OrderBy(p => p.TypeLabel).ThenBy(p => p.Name, NaturalOrderComparer.OrdinalIgnoreCase).ToList();

            _entryById = new Dictionary<long, ParentViewEntry>();
            foreach (var e in _placeableViews.Concat(_parents).Concat(_compositeCandidates))
                if (!_entryById.ContainsKey(e.Id.Value)) _entryById[e.Id.Value] = e;

            _titleblockSizes = titleblockSizes ?? new Dictionary<string, (double, double)>();
            _usedNumbers     = usedSheetNumbers ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _seriesParams    = seriesParams ?? new List<SheetSeriesParam>();

            _titleblockMap   = new Dictionary<string, ElementId>();
            _titleblockNames = new List<string>();
            foreach (var tb in titleblocks ?? Enumerable.Empty<FamilySymbol>())
            {
                string label = $"{tb.FamilyName} : {tb.Name}";
                if (!_titleblockMap.ContainsKey(label))
                {
                    _titleblockMap[label] = tb.Id;
                    _titleblockNames.Add(label);
                }
            }
            if (_titleblockNames.Count > 0) _selectedTitleblock = _titleblockNames[0];

            LoadMarginsForTitleBlock();
            _gapInches = SheetMarginStore.Instance.Gap;
        }

        /// <summary>Settings-only constructor (no document open).</summary>
        public PlaceDependentViewsViewModel() : this(null, null, null, null, null) { }

        // ── IStepAware ────────────────────────────────────────────────────────
        // Step content is built eagerly at window construction, so every step that reads a choice
        // made on an EARLIER step has to rebuild itself on activation or it renders once, empty,
        // and never updates. That is: the picker (which candidate list the mode selects), the
        // numbering and naming previews (which views were picked), and the order list (all of it).
        private Action<string>? _refreshStep;
        public void SetContentRefreshCallback(Action<string> rebuildStepContent) => _refreshStep = rebuildStepContent;

        public void OnStepActivated(string stepId)
        {
            switch (stepId)
            {
                case "S2":
                case "S4":
                case "S6":
                case "S7":
                    _refreshStep?.Invoke(stepId);
                    break;
            }
        }

        // ── Content ───────────────────────────────────────────────────────────
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildModeStep();
                case "S2": return BuildViewsStep();
                case "S3": return BuildTitleBlockStep();
                case "S4": return BuildNumberingStep();
                case "S5": return BuildSeriesStep();
                case "S6": return BuildNamingStep();
                case "S7": return BuildOrderStep();
                case "S8": return null;   // framework renders IReviewableTool
                default:   return null;
            }
        }

        // ── Step 1 — Mode ─────────────────────────────────────────────────────
        private FrameworkElement BuildModeStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(SectionLabel(AppStrings.T("testing.placeDependentViews.labels.secMode")));

            var modeSelect = new SingleSelect
            {
                Items = new List<string> { ModePlaceViews, ModeDependents, ModeComposite },
            };
            modeSelect.SelectedItem = ModeLabelFor(_mode);
            outer.Children.Add(modeSelect);

            var modeNote = Note(ModeNoteText());
            modeNote.Margin = new Thickness(0, 8, 0, 0);
            outer.Children.Add(modeNote);

            modeSelect.SelectionChanged += s =>
            {
                var next = s == ModeComposite  ? PlaceViewsMode.CompositeOneSheet
                         : s == ModeDependents ? PlaceViewsMode.DependentsPerParent
                         : PlaceViewsMode.OneViewPerSheet;
                if (next == _mode) return;
                _mode = next;
                // The order belongs to one mode's selection; carrying it across would leave rows
                // for views the new mode does not even offer.
                _order.Clear();
                modeNote.Text = ModeNoteText();
                OnValidationChanged();
            };

            return outer;
        }

        private static string ModeLabelFor(PlaceViewsMode mode) =>
              mode == PlaceViewsMode.CompositeOneSheet  ? ModeComposite
            : mode == PlaceViewsMode.DependentsPerParent ? ModeDependents
            : ModePlaceViews;

        private string ModeNoteText() =>
              _mode == PlaceViewsMode.CompositeOneSheet   ? AppStrings.T("testing.placeDependentViews.labels.modeNoteComposite")
            : _mode == PlaceViewsMode.DependentsPerParent ? AppStrings.T("testing.placeDependentViews.labels.modeNoteDependents")
            : AppStrings.T("testing.placeDependentViews.labels.modeNotePlaceViews");

        // ── Step 2 — Views to place ───────────────────────────────────────────
        private FrameworkElement BuildViewsStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(SectionLabel(AppStrings.T("testing.placeDependentViews.labels.secViews")));

            List<ParentViewEntry> candidates;
            string emptyHint, pickerName;
            if (_mode == PlaceViewsMode.CompositeOneSheet)
            {
                candidates = _compositeCandidates;
                emptyHint  = AppStrings.T("testing.placeDependentViews.labels.hintNoComposite");
                pickerName = AppStrings.T("testing.placeDependentViews.labels.pickerCompositeName");
            }
            else if (_mode == PlaceViewsMode.DependentsPerParent)
            {
                candidates = _parents;
                emptyHint  = AppStrings.T("testing.placeDependentViews.labels.hintNoParents");
                pickerName = AppStrings.T("testing.placeDependentViews.labels.pickerDependentsName");
            }
            else
            {
                candidates = _placeableViews;
                emptyHint  = AppStrings.T("testing.placeDependentViews.labels.hintNoPlaceable");
                pickerName = AppStrings.T("testing.placeDependentViews.labels.pickerPlaceViewsName");
            }

            if (candidates.Count == 0)
            {
                outer.Children.Add(Hint(emptyHint));
                return outer;
            }

            var picker = new BrowserTreePicker
            {
                Height         = 320,
                AccessibleName = pickerName,
                Margin         = new Thickness(0, 8, 0, 0),
            };
            // Subscribe BEFORE SetTree — its end-of-setup SelectionChanged seeds the mirror list.
            picker.SelectionChanged += ids =>
            {
                var picked = ids.Select(id => new ElementId(id)).ToList();
                SetActiveSelection(picked);
                SyncOrder(picked);
                OnValidationChanged();
            };
            picker.SetTree(_browserTree,
                candidates.Select(p => p.Id.Value),
                ActiveSelectionRaw.Select(id => id.Value).ToList());
            outer.Children.Add(picker);
            return outer;
        }

        private List<ElementId> ActiveSelectionRaw =>
              _mode == PlaceViewsMode.CompositeOneSheet   ? _selectedCompositeIds
            : _mode == PlaceViewsMode.DependentsPerParent ? _selectedParentIds
            : _selectedPlaceableIds;

        private void SetActiveSelection(List<ElementId> picked)
        {
            if      (_mode == PlaceViewsMode.CompositeOneSheet)   _selectedCompositeIds = picked;
            else if (_mode == PlaceViewsMode.DependentsPerParent) _selectedParentIds    = picked;
            else                                                  _selectedPlaceableIds = picked;
        }

        /// <summary>Reconciles the sheet order against a fresh pick: drops views no longer selected,
        /// appends newly selected ones at the end, and leaves everything else where the user put it.
        /// Re-sorting here would silently undo an arrangement every time a box was ticked.</summary>
        private void SyncOrder(List<ElementId> picked)
        {
            var pickedKeys = new HashSet<long>(picked.Select(i => i.Value));
            _order.RemoveAll(id => !pickedKeys.Contains(id.Value));
            var have = new HashSet<long>(_order.Select(i => i.Value));
            foreach (var id in picked)
                if (have.Add(id.Value)) _order.Add(id);
        }

        /// <summary>The views to place, in sheet order.</summary>
        private List<ElementId> OrderedSelection
        {
            get
            {
                // Defensive: a step reached without the picker's callback having run (or a settings
                // -only construction) leaves the order empty while a selection exists.
                if (_order.Count == 0 && ActiveSelectionRaw.Count > 0) SyncOrder(ActiveSelectionRaw);
                return _order.ToList();
            }
        }

        // ── Step 3 — Title block, margins, gap ────────────────────────────────
        private FrameworkElement BuildTitleBlockStep()
        {
            var outer = new StackPanel();

            var sel = new SingleSelect
            {
                Items = _titleblockNames.Count > 0
                    ? _titleblockNames
                    : new List<string> { AppStrings.T("testing.placeDependentViews.labels.noTitleBlocks") },
            };
            if (!string.IsNullOrEmpty(_selectedTitleblock)) sel.SelectedItem = _selectedTitleblock;

            var preview = new TitleBlockPreview { Margin = new Thickness(0, 14, 0, 0) };
            ApplyTitleBlockToPreview(preview);

            // Margins belong to the title block, not to the run: the same border should always lay
            // out the same way, in this project and the next one. Saved on change — no Apply button.
            preview.MarginsChanged += (t, b, l, r) =>
            {
                _marginTop = t; _marginBottom = b; _marginLeft = l; _marginRight = r;
                SheetMarginStore.Instance.SetMargins(_selectedTitleblock, t, b, l, r);
                OnValidationChanged();
            };

            sel.SelectionChanged += s =>
            {
                _selectedTitleblock = s ?? "";
                LoadMarginsForTitleBlock();
                ApplyTitleBlockToPreview(preview);
                OnValidationChanged();
            };

            outer.Children.Add(Card(AppStrings.T("testing.placeDependentViews.labels.secTitleBlock"), sel));

            var marginCard = new StackPanel();
            marginCard.Children.Add(preview);
            marginCard.Children.Add(Spaced(Note(AppStrings.T("testing.placeDependentViews.labels.noteMargins")), 10));
            outer.Children.Add(Card(AppStrings.T("testing.placeDependentViews.labels.secMargins"), marginCard, 12));

            var gapPanel = new StackPanel();
            gapPanel.Children.Add(Stepper(_gapInches, 0.0, 12.0, 0.125, 3, v =>
            {
                _gapInches = v;
                SheetMarginStore.Instance.Gap = v;
            }));
            gapPanel.Children.Add(Spaced(Note(AppStrings.T("testing.placeDependentViews.labels.noteGap")), 8));
            outer.Children.Add(Card(AppStrings.T("testing.placeDependentViews.labels.secGap"), gapPanel, 12));

            return outer;
        }

        private void LoadMarginsForTitleBlock()
        {
            var m = SheetMarginStore.Instance.GetMargins(_selectedTitleblock);
            _marginTop = m.Top; _marginBottom = m.Bottom; _marginLeft = m.Left; _marginRight = m.Right;
        }

        private void ApplyTitleBlockToPreview(TitleBlockPreview preview)
        {
            _titleblockSizes.TryGetValue(_selectedTitleblock ?? "", out var size);
            preview.SetSheet(size.W, size.H);
            preview.SetMargins(_marginTop, _marginBottom, _marginLeft, _marginRight);
        }

        // ── Step 4 — Sheet numbering ──────────────────────────────────────────
        private FrameworkElement BuildNumberingStep()
        {
            var outer = new StackPanel();
            var body  = new StackPanel();

            body.Children.Add(SectionLabel(AppStrings.T("testing.placeDependentViews.labels.secStartNumber")));
            var numStepper = new InlineStepper
            {
                Value = _startingNumber, MinValue = 1, MaxValue = 99999,
                Step = 1, Decimals = 0, ValueWidth = 56,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            body.Children.Add(numStepper);

            var prefixField = new TextField
            {
                Label       = AppStrings.T("testing.placeDependentViews.labels.lblNumberPrefix"),
                Placeholder = AppStrings.T("testing.placeDependentViews.labels.phNumberPrefix"),
                Text        = _numberPrefix,
                Margin      = new Thickness(0, 14, 0, 0),
            };
            body.Children.Add(prefixField);

            body.Children.Add(Spaced(SectionLabel(AppStrings.T("testing.placeDependentViews.labels.secDigits")), 14));
            var digitStepper = new InlineStepper
            {
                Value = _numberDigits, MinValue = 1, MaxValue = 8,
                Step = 1, Decimals = 0, ValueWidth = 56,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            body.Children.Add(digitStepper);
            body.Children.Add(Spaced(Note(AppStrings.T("testing.placeDependentViews.labels.noteDigits")), 8));

            outer.Children.Add(Card(AppStrings.T("testing.placeDependentViews.labels.secNumbering"), body));

            var previewText = BigPreview();
            outer.Children.Add(Card(AppStrings.T("testing.placeDependentViews.labels.secPreview"), previewText, 12));

            Action update = () => previewText.Text = NumberPreviewText();
            update();
            numStepper.ValueChanged   += (s, v) => { _startingNumber = (int)v; update(); OnValidationChanged(); };
            digitStepper.ValueChanged += (s, v) => { _numberDigits   = (int)v; update(); OnValidationChanged(); };
            prefixField.TextChanged   += t      => { _numberPrefix   = t ?? ""; update(); OnValidationChanged(); };

            return outer;
        }

        /// <summary>
        /// The sheet numbers this run will assign, in order — prefix + the running number padded to
        /// the chosen width, skipping any number the project already uses.
        ///
        /// Computed HERE, not in the handler, because the Order step shows these numbers to the user
        /// before the run: a handler that quietly skipped a taken number would print one thing and
        /// create another. The handler is given this exact list and reports a collision rather than
        /// silently shifting.
        /// </summary>
        private List<string> BuildNumbers(int count)
        {
            var result = new List<string>(count);
            var taken  = new HashSet<string>(_usedNumbers, StringComparer.OrdinalIgnoreCase);
            string pad = new string('0', Math.Max(1, _numberDigits));
            int n = Math.Max(1, _startingNumber);

            for (int i = 0; i < count; i++)
            {
                string candidate;
                int guard = 0;
                do
                {
                    candidate = (_numberPrefix ?? "") + n.ToString(pad);
                    n++;
                }
                while (!taken.Add(candidate) && ++guard < 100000);
                result.Add(candidate);
            }
            return result;
        }

        private string NumberPreviewText()
        {
            int count = Math.Max(OrderedSelection.Count, 3);
            var numbers = BuildNumbers(count);
            if (numbers.Count <= 4) return string.Join("   ", numbers);
            return string.Join("   ", numbers.Take(3)) + "   …   " + numbers[numbers.Count - 1];
        }

        // ── Step 5 — Sheet series ─────────────────────────────────────────────
        private FrameworkElement BuildSeriesStep()
        {
            var outer = new StackPanel();

            if (_seriesParams.Count == 0)
            {
                // A zero-result capture says so. A silently empty picker is indistinguishable from
                // a broken one, and this capture has a real failure mode (a project with no sheets).
                outer.Children.Add(Hint(AppStrings.T("testing.placeDependentViews.labels.noSeriesParams")));
                return outer;
            }

            var body = new StackPanel();

            var labels = _seriesParams.Select(p => p.Label).ToList();
            var none   = AppStrings.T("testing.placeDependentViews.labels.seriesParamNone");
            labels.Insert(0, none);

            var paramSelect = new SingleSelect { Items = labels };
            paramSelect.SelectedItem = _seriesParam?.Label ?? none;
            body.Children.Add(paramSelect);
            body.Children.Add(Spaced(Note(AppStrings.T("testing.placeDependentViews.labels.noteSeries")), 8));

            var valueBox = new SearchAutocomplete
            {
                Items       = _seriesParam?.ExistingValues ?? new List<string>(),
                Value       = _seriesValue,
                Placeholder = AppStrings.T("testing.placeDependentViews.labels.phSeriesValue"),
                Margin      = new Thickness(0, 12, 0, 0),
            };
            valueBox.SelectionChanged += v => { _seriesValue = v ?? ""; OnValidationChanged(); };

            paramSelect.SelectionChanged += s =>
            {
                _seriesParam = _seriesParams.FirstOrDefault(p => p.Label == s);
                valueBox.Items = _seriesParam?.ExistingValues ?? new List<string>();
                OnValidationChanged();
            };

            body.Children.Add(valueBox);
            outer.Children.Add(Card(AppStrings.T("testing.placeDependentViews.labels.secSeries"), body));
            return outer;
        }

        // ── Step 6 — Sheet naming ─────────────────────────────────────────────
        private FrameworkElement BuildNamingStep()
        {
            var outer = new StackPanel();

            var tokens     = NamingTokenRegistry.TokensFor(TokenEntity.Sheet, hasSource: true);
            var tokenInput = new TokenInput(tokens, DefaultPattern) { Text = _namingPattern };

            outer.Children.Add(Card(AppStrings.T("testing.placeDependentViews.labels.secNamingPattern"), tokenInput));

            var previewText = BigPreview();
            var previewSub  = Note("");
            var previewBody = new StackPanel();
            previewBody.Children.Add(previewText);
            previewBody.Children.Add(Spaced(previewSub, 6));
            outer.Children.Add(Card(AppStrings.T("testing.placeDependentViews.labels.secPreview"), previewBody, 12));

            Action update = () =>
            {
                var ordered = OrderedSelection;
                var numbers = BuildNumbers(Math.Max(ordered.Count, 1));
                var sample  = ordered.Count > 0 ? EntryFor(ordered[0]) : FirstCandidate();
                string number = numbers[0];
                previewText.Text = ResolveSheetName(sample, number);
                previewSub.Text  = AppStrings.T("testing.placeDependentViews.labels.previewFor",
                                                number, sample?.Name ?? AppStrings.T("testing.placeDependentViews.labels.previewSampleName"));
            };
            update();
            tokenInput.TextChanged += (s, e) =>
            {
                _namingPattern = tokenInput.Text;
                NamingPatternStore.Instance.Set(ToolId, _namingPattern);
                update();
                OnValidationChanged();
            };

            return outer;
        }

        private ParentViewEntry? FirstCandidate() =>
              _mode == PlaceViewsMode.CompositeOneSheet   ? _compositeCandidates.FirstOrDefault()
            : _mode == PlaceViewsMode.DependentsPerParent ? _parents.FirstOrDefault()
            : _placeableViews.FirstOrDefault();

        private ParentViewEntry? EntryFor(ElementId id) =>
            _entryById.TryGetValue(id.Value, out var e) ? e : null;

        /// <summary>
        /// The sheet name for one view, resolved exactly the way the run will resolve it — same
        /// pattern, same token values — so the Order step is a preview and not an approximation.
        /// Document-backed tokens (project info, dates) resolve doc-free here and are left to the
        /// run; the tokens this tool supplies are all Computed and are all supplied.
        /// </summary>
        private string ResolveSheetName(ParentViewEntry? entry, string number)
        {
            var ctx = new TokenContext();
            ctx.Computed["ParentViewName"] = entry?.Name      ?? AppStrings.T("testing.placeDependentViews.labels.previewSampleName");
            ctx.Computed["SourceViewName"] = ctx.Computed["ParentViewName"];
            ctx.Computed["ViewName"]       = ctx.Computed["ParentViewName"];
            ctx.Computed["ViewType"]       = entry?.TypeLabel ?? "FloorPlan";
            ctx.Computed["Level"]          = entry?.LevelName ?? AppStrings.T("testing.placeDependentViews.labels.previewSampleLevel");
            ctx.Computed["SheetNumber"]    = number;
            return TokenResolver.Resolve(_namingPattern, ctx);
        }

        // ── Step 7 — Order ────────────────────────────────────────────────────
        private FrameworkElement BuildOrderStep()
        {
            var outer = new StackPanel();

            if (OrderedSelection.Count == 0)
            {
                outer.Children.Add(Hint(AppStrings.T("testing.placeDependentViews.labels.orderNoViews")));
                return outer;
            }

            var list = new ReorderList
            {
                Height      = 300,
                LeftHeader  = AppStrings.T("testing.placeDependentViews.labels.orderColView"),
                RightHeader = AppStrings.T("testing.placeDependentViews.labels.orderColSheet"),
            };
            list.SetRows(BuildOrderRows());

            // The number column belongs to the ROW, not to the view: moving a view moves its name
            // into a different numbered slot, which is the whole point of this step. So the rows are
            // rebuilt from scratch after every move rather than swapped in place.
            list.MoveRequested += (selected, delta) =>
            {
                MoveInOrder(selected, delta);
                var moved = selected.Select(i => i + delta).Where(i => i >= 0 && i < _order.Count).ToList();
                list.SetRows(BuildOrderRows(), moved);
                OnValidationChanged();
            };

            outer.Children.Add(list);
            outer.Children.Add(Spaced(Note(AppStrings.T("testing.placeDependentViews.labels.noteOrder")), 10));
            return outer;
        }

        private List<(string Left, string Right)> BuildOrderRows()
        {
            var ordered = OrderedSelection;
            var numbers = BuildNumbers(ordered.Count);
            var rows    = new List<(string, string)>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                var entry = EntryFor(ordered[i]);
                rows.Add((entry?.Name ?? ordered[i].Value.ToString(),
                          numbers[i] + "   " + ResolveSheetName(entry, numbers[i])));
            }
            return rows;
        }

        /// <summary>Moves every selected row one place. Walks in the direction of travel so a
        /// multi-row block slides as a block instead of the rows overwriting each other.</summary>
        private void MoveInOrder(IReadOnlyList<int> selected, int delta)
        {
            if (delta == 0 || selected.Count == 0) return;
            var indices = selected.OrderBy(i => i).ToList();
            if (delta > 0) indices.Reverse();

            foreach (int i in indices)
            {
                int j = i + delta;
                if (i < 0 || i >= _order.Count || j < 0 || j >= _order.Count) return;
                var tmp = _order[i];
                _order[i] = _order[j];
                _order[j] = tmp;
            }
        }

        // ── IReviewableTool ───────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("mode",    AppStrings.T("testing.placeDependentViews.review.itemMode")),
            ("views",   AppStrings.T("testing.placeDependentViews.review.itemViews")),
            ("tb",      AppStrings.T("testing.placeDependentViews.review.itemTb")),
            ("numbers", AppStrings.T("testing.placeDependentViews.review.itemNumbers")),
            ("series",  AppStrings.T("testing.placeDependentViews.review.itemSeries")),
            ("naming",  AppStrings.T("testing.placeDependentViews.review.itemNaming")),
        };

        public IDictionary<string, string> ReviewValues
        {
            get
            {
                var ordered = OrderedSelection;
                var numbers = BuildNumbers(Math.Max(ordered.Count, 1));
                var sample  = ordered.Count > 0 ? EntryFor(ordered[0]) : null;
                return new Dictionary<string, string>
                {
                    ["mode"]  = ModeLabelFor(_mode),
                    ["views"] = ordered.Count == 0
                        ? AppStrings.T("testing.placeDependentViews.review.viewsNone")
                        : AppStrings.T("testing.placeDependentViews.review.viewsValue", ordered.Count),
                    ["tb"] = string.IsNullOrEmpty(_selectedTitleblock)
                        ? "—"
                        : AppStrings.T("testing.placeDependentViews.review.tbValue", _selectedTitleblock,
                                       _marginTop, _marginBottom, _marginLeft, _marginRight, _gapInches),
                    ["numbers"] = NumberPreviewText(),
                    ["series"]  = _seriesParam == null || string.IsNullOrWhiteSpace(_seriesValue)
                        ? "—"
                        : AppStrings.T("testing.placeDependentViews.review.seriesValue", _seriesValue, _seriesParam.Name),
                    ["naming"] = AppStrings.T("testing.placeDependentViews.review.namingValue",
                                              _namingPattern, ResolveSheetName(sample, numbers[0])),
                };
            }
        }

        public IList<string>? ReviewChips => null;
        public string?        ReviewNote  =>
              _mode == PlaceViewsMode.CompositeOneSheet   ? AppStrings.T("testing.placeDependentViews.review.noteComposite")
            : _mode == PlaceViewsMode.DependentsPerParent ? AppStrings.T("testing.placeDependentViews.review.noteDependents")
            : AppStrings.T("testing.placeDependentViews.review.notePlaceViews");
        public string?        ReviewWarning => null;

        // ── Validation / summary ──────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return true;
                case "S2": return ActiveSelectionRaw.Count > 0;
                case "S3": return !string.IsNullOrEmpty(_selectedTitleblock) &&
                                  _titleblockMap.ContainsKey(_selectedTitleblock);
                case "S4": return _numberDigits >= 1 && _startingNumber >= 1;
                case "S6": return !string.IsNullOrWhiteSpace(_namingPattern);
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1": return ModeLabelFor(_mode);
                case "S2": return ActiveSelectionRaw.Count == 0
                    ? "—"
                    : AppStrings.T("testing.placeDependentViews.summaries.s2Value", ActiveSelectionRaw.Count);
                case "S3": return string.IsNullOrEmpty(_selectedTitleblock)
                    ? "—"
                    : AppStrings.T("testing.placeDependentViews.summaries.s3Value", _selectedTitleblock);
                case "S4": return NumberPreviewText();
                case "S5": return _seriesParam == null || string.IsNullOrWhiteSpace(_seriesValue)
                    ? AppStrings.T("testing.placeDependentViews.summaries.s5None")
                    : AppStrings.T("testing.placeDependentViews.summaries.s5Value", _seriesValue, _seriesParam.Name);
                case "S6": return _namingPattern;
                case "S7": return OrderedSelection.Count == 0
                    ? "—"
                    : AppStrings.T("testing.placeDependentViews.summaries.s7Value", OrderedSelection.Count);
                case "S8": return AppStrings.T("testing.placeDependentViews.summaries.S8");
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

            var ordered = OrderedSelection;

            _handler.Mode             = _mode;
            _handler.ParentViewIds    = new List<ElementId>(ordered);
            _handler.SheetNumbers     = BuildNumbers(ordered.Count);
            _handler.TitleBlockTypeId = _titleblockMap.TryGetValue(_selectedTitleblock, out var tbId)
                                        ? tbId : ElementId.InvalidElementId;
            _handler.NamingPattern    = _namingPattern;
            _handler.SeriesParam      = _seriesParam;
            _handler.SheetSeries      = _seriesValue;
            _handler.MarginTopIn      = _marginTop;
            _handler.MarginBottomIn   = _marginBottom;
            _handler.MarginLeftIn     = _marginLeft;
            _handler.MarginRightIn    = _marginRight;
            _handler.GapIn            = _gapInches;
            _handler.PushLog          = pushLog;
            _handler.OnProgress       = onProgress;
            _handler.OnComplete       = onComplete;

            _event.Raise();
        }

        // ── Small UI helpers ──────────────────────────────────────────────────
        /// <summary>A titled, bordered group. Every input group on the numbering, series and naming
        /// steps gets one, so what belongs to what is visible rather than inferred from spacing.</summary>
        private static SectionCard Card(string header, UIElement content, double topMargin = 0)
        {
            return new SectionCard
            {
                Header      = header,
                CardContent = content,
                Margin      = new Thickness(0, topMargin, 0, 0),
            };
        }

        /// <summary>The large preview line — the one thing on a pattern step the user actually
        /// reads, so it is set at heading size rather than the small italic note it used to be.</summary>
        private static TextBlock BigPreview()
        {
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
            return t;
        }

        private InlineStepper Stepper(double value, double min, double max, double step, int decimals, Action<double> set)
        {
            var s = new InlineStepper
            {
                Value = value, MinValue = min, MaxValue = max, Step = step, Decimals = decimals,
                ValueWidth = 56, HorizontalAlignment = HorizontalAlignment.Left,
            };
            s.ValueChanged += (sender, v) => { set(v); OnValidationChanged(); };
            return s;
        }

        private static TextBlock SectionLabel(string text)
        {
            var t = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4) };
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return t;
        }

        private static FrameworkElement Spaced(FrameworkElement el, double top)
        {
            el.Margin = new Thickness(0, top, 0, 0);
            return el;
        }

        private static TextBlock Note(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }

        private static TextBlock Hint(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }
    }
}
