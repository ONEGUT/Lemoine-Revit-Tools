using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

namespace LemoineTools.Tools.Debuggers
{
    /// <summary>
    /// TEMPORARY Phase-0 harness UI for the Family Modification Tools plan. See
    /// <see cref="FamilyApiProbeHandler"/> for what each probe answers and why.
    ///
    /// Delete this file, the handler, FamilyApiProbeCommand, and the temporary ribbon button
    /// in App.cs once the results are captured.
    ///
    /// Strings are hardcoded rather than routed through AppStrings — developer diagnostics with
    /// a scheduled deletion date, not shipping user-facing text.
    /// </summary>
    public class FamilyApiProbeViewModel : IStepFlowTool, IReviewableTool, IConditionalSteps, IToolCleanup
    {
        public string Title    => "Family API Probe (Phase 0)";
        public string RunLabel => "Run Probes →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("probes", "Probes to run", required: true),
            new StepDefinition("scope",  "Category scope", required: true),
            new StepDefinition("run",    "Review & Run",   required: false),
        };

        // ── Candidate categories ──────────────────────────────────────────────────
        // Curated rather than captured from the document: this is a probe, and a short honest
        // list is more useful than the full filterable-category tree.
        private static readonly Dictionary<string, BuiltInCategory> FamilyCandidates =
            new Dictionary<string, BuiltInCategory>
            {
                ["Air Terminals"]        = BuiltInCategory.OST_DuctTerminal,
                ["Mechanical Equipment"] = BuiltInCategory.OST_MechanicalEquipment,
                ["Pipe Accessories"]     = BuiltInCategory.OST_PipeAccessory,
                ["Pipe Fittings"]        = BuiltInCategory.OST_PipeFitting,
                ["Duct Accessories"]     = BuiltInCategory.OST_DuctAccessory,
                ["Duct Fittings"]        = BuiltInCategory.OST_DuctFitting,
                ["Plumbing Fixtures"]    = BuiltInCategory.OST_PlumbingFixtures,
                ["Sprinklers"]           = BuiltInCategory.OST_Sprinklers,
                ["Lighting Fixtures"]    = BuiltInCategory.OST_LightingFixtures,
                ["Generic Models"]       = BuiltInCategory.OST_GenericModel,
            };

        private static readonly Dictionary<string, BuiltInCategory> SourceCandidates =
            new Dictionary<string, BuiltInCategory>
            {
                ["Pipe Accessories"]     = BuiltInCategory.OST_PipeAccessory,
                ["Pipe Fittings"]        = BuiltInCategory.OST_PipeFitting,
                ["Mechanical Equipment"] = BuiltInCategory.OST_MechanicalEquipment,
                ["Plumbing Fixtures"]    = BuiltInCategory.OST_PlumbingFixtures,
                ["Generic Models"]       = BuiltInCategory.OST_GenericModel,
            };

        private static readonly Dictionary<string, BuiltInCategory> TargetCandidates =
            new Dictionary<string, BuiltInCategory>
            {
                ["Ceilings"]       = BuiltInCategory.OST_Ceilings,
                ["Walls"]          = BuiltInCategory.OST_Walls,
                ["Generic Models"] = BuiltInCategory.OST_GenericModel,
                ["Floors"]         = BuiltInCategory.OST_Floors,
                ["Roofs"]          = BuiltInCategory.OST_Roofs,
            };

        private readonly FamilyApiProbeHandler? _handler;
        private readonly ExternalEvent?         _event;

        private bool _visibilityApi = true;
        private bool _familyWalk    = true;
        private bool _cuttable      = true;
        private bool _linkIntersect = true;
        private int  _sampleSize    = 25;

        private HashSet<string> _familyCats = new HashSet<string> { "Air Terminals" };
        private HashSet<string> _sourceCats = new HashSet<string> { "Pipe Accessories", "Pipe Fittings" };
        private HashSet<string> _targetCats = new HashSet<string> { "Ceilings", "Walls", "Generic Models" };

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public FamilyApiProbeViewModel(FamilyApiProbeHandler? handler, ExternalEvent? ev)
        {
            _handler = handler;
            _event   = ev;
        }

        public void OnWindowClosed()
        {
            if (_handler == null) return;
            _handler.PushLog    = null;
            _handler.OnProgress = null;
            _handler.OnComplete = null;
        }

        // The scope step is only meaningful for the two probes that take categories. It is not
        // the last step, so hiding it is legal (IConditionalSteps contract).
        public bool IsStepVisible(string stepId)
            => stepId != "scope" || _familyWalk || _linkIntersect;

        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "probes": return BuildProbesStep();
                case "scope":  return BuildScopeStep();
                default:       return null;   // "run" is rendered by IReviewableTool
            }
        }

        private FrameworkElement BuildProbesStep()
        {
            var outer = new StackPanel();

            var toggles = new ToggleSwitches { AccessibleName = "Probes" };
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "vis",  Label = "FamilyElementVisibility surface",
                                 Desc = "Reflects the exact member names for this Revit year. Tool A's rule body depends on them.",
                                 DefaultOn = _visibilityApi },
                new ToggleItem { Id = "cut",  Label = "Category.IsCuttable",
                                 Desc = "Lists every model category and whether it can show cut geometry.",
                                 DefaultOn = _cuttable },
                new ToggleItem { Id = "walk", Label = "EditFamily walk (cost + shape)",
                                 Desc = "Times EditFamily per family and counts forms, voids, nested and shared families. Also tests nested recursion.",
                                 DefaultOn = _familyWalk },
                new ToggleItem { Id = "ri",   Label = "ReferenceIntersector into links",
                                 Desc = "Shoots down from sample instances to confirm ceiling faces come back from the linked ARCH model.",
                                 DefaultOn = _linkIntersect },
            });
            toggles.StateChanged += st =>
            {
                _visibilityApi = st.TryGetValue("vis",  out var a) && a;
                _cuttable      = st.TryGetValue("cut",  out var b) && b;
                _familyWalk    = st.TryGetValue("walk", out var c) && c;
                _linkIntersect = st.TryGetValue("ri",   out var d) && d;
                Changed();
            };
            outer.Children.Add(toggles);

            Divider(outer);
            outer.Children.Add(Label("Sample size"));

            var dp = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var tb = new TextBlock { Text = "Families / instances to probe", VerticalAlignment = VerticalAlignment.Center };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            DockPanel.SetDock(tb, Dock.Left);

            var stepper = new InlineStepper
            {
                Value = _sampleSize, MinValue = 1, MaxValue = 500, Step = 5, Decimals = 0, ValueWidth = 52,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            stepper.ValueChanged += (s, v) => { _sampleSize = (int)Math.Round(v); Changed(); };
            dp.Children.Add(tb);
            dp.Children.Add(stepper);
            outer.Children.Add(dp);

            outer.Children.Add(Dim("At roughly 0.5–2s per family, 25 is about a minute. Raise it only if the timing " +
                                   "spread looks unrepresentative — the projection at the end scales the median to the whole library."));
            return outer;
        }

        private FrameworkElement BuildScopeStep()
        {
            var outer = new StackPanel();

            if (_familyWalk)
            {
                outer.Children.Add(Label("Families to walk (EditFamily probe)"));
                outer.Children.Add(Picker(FamilyCandidates, _familyCats, sel => { _familyCats = sel; Changed(); }));
            }

            if (_linkIntersect)
            {
                if (_familyWalk) Divider(outer);
                outer.Children.Add(Label("Shoot down from (valve candidates)"));
                outer.Children.Add(Picker(SourceCandidates, _sourceCats, sel => { _sourceCats = sel; Changed(); }));

                outer.Children.Add(Label("Count as a ceiling/soffit hit"));
                outer.Children.Add(Picker(TargetCandidates, _targetCats, sel => { _targetCats = sel; Changed(); }));
                outer.Children.Add(Dim("Soffits and bulkheads are frequently Walls or Generic Models rather than Ceilings — " +
                                       "that is exactly what this probe is here to confirm on your models."));
            }

            return outer;
        }

        private static MultiSelectTabs Picker(Dictionary<string, BuiltInCategory> candidates,
                                              HashSet<string> initial, Action<HashSet<string>> onChange)
        {
            var tabs = new MultiSelectTabs();
            // Subscribe BEFORE SetGroups — SetGroups fires SelectionChanged once at the end of setup
            // and that callback is the only thing that populates the mirror field.
            tabs.SelectionChanged += sel => onChange(new HashSet<string>(sel));
            var all = candidates.Keys.ToList();
            tabs.SetGroups(new Dictionary<string, List<string>> { { "Categories", all } }, initial.ToList());
            return tabs;
        }

        // ── Review ────────────────────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("probes", "Probes"), ("scope", "Scope"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["probes"] = SelectedProbeCount + " of 4 selected",
            ["scope"]  = _familyWalk || _linkIntersect
                            ? $"{_familyCats.Count} family · {_sourceCats.Count} source · {_targetCats.Count} target"
                            : "n/a",
        };

        public IList<string>? ReviewChips => SelectedProbeNames;

        public string? ReviewNote =>
            "Read-only on your model. The one exception is a temporary 3D view created and deleted " +
            "for the intersector probe. A report file is written to %AppData%\\LemoineTools\\Reports — " +
            "attach it to the plan thread.";

        public string? ReviewWarning =>
            SelectedProbeCount == 0 ? "No probes selected." : null;

        private int SelectedProbeCount =>
            (_visibilityApi ? 1 : 0) + (_cuttable ? 1 : 0) + (_familyWalk ? 1 : 0) + (_linkIntersect ? 1 : 0);

        private List<string> SelectedProbeNames
        {
            get
            {
                var l = new List<string>();
                if (_visibilityApi) l.Add("Visibility API");
                if (_cuttable)      l.Add("IsCuttable");
                if (_familyWalk)    l.Add("EditFamily walk");
                if (_linkIntersect) l.Add("Intersector");
                return l;
            }
        }

        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "probes": return SelectedProbeCount > 0;
                case "scope":
                    if (_familyWalk    && _familyCats.Count == 0) return false;
                    if (_linkIntersect && (_sourceCats.Count == 0 || _targetCats.Count == 0)) return false;
                    return true;
                default: return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "probes": return $"{SelectedProbeCount} of 4 · sample {_sampleSize}";
                case "scope":  return _familyWalk || _linkIntersect
                                        ? $"{_familyCats.Count} family · {_sourceCats.Count} source · {_targetCats.Count} target"
                                        : "Not needed for the selected probes";
                case "run":    return "Ready to run";
                default:       return "—";
            }
        }

        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_handler == null || _event == null)
            {
                pushLog("Probe handler not registered.", "fail");
                onComplete(0, 1, 0);
                return;
            }

            _handler.ProbeVisibilityApi = _visibilityApi;
            _handler.ProbeCuttable      = _cuttable;
            _handler.ProbeFamilyWalk    = _familyWalk;
            _handler.ProbeLinkIntersect = _linkIntersect;
            _handler.SampleSize         = _sampleSize;
            _handler.FamilyCategories   = _familyCats.Where(FamilyCandidates.ContainsKey).Select(k => (int)FamilyCandidates[k]).ToList();
            _handler.SourceCategories   = _sourceCats.Where(SourceCandidates.ContainsKey).Select(k => (int)SourceCandidates[k]).ToList();
            _handler.TargetCategories   = _targetCats.Where(TargetCandidates.ContainsKey).Select(k => (int)TargetCandidates[k]).ToList();
            _handler.PushLog            = pushLog;
            _handler.OnProgress         = onProgress;
            _handler.OnComplete         = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
            _event.Raise();
        }

        // ── helpers ───────────────────────────────────────────────────────────────
        private static TextBlock Label(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock Dim(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static void Divider(StackPanel parent)
        {
            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            parent.Children.Add(sep);
        }
    }
}
