using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;
using NavisApp = Autodesk.Navisworks.Api.Application;
using NavisDoc = Autodesk.Navisworks.Api.Document;
using NavisItem = Autodesk.Navisworks.Api.ModelItem;

namespace LemoineNavisworks.LevelModels
{
    // =========================================================================
    // LevelModelsViewModel — one NWD per level, holding only that level's models.
    //
    // Step flow:
    //   S1  Levels & models — one row per level: name + a multi-select dropdown of
    //                         the appended models. A row expands to an optional
    //                         elevation band that trims those models vertically.
    //   S2  Output          — folder, filename pattern, straddle rule, export options.
    //   S3  Run             — per level: hide everything not assigned, optionally
    //                         trim by band, save a clipped viewpoint, export, restore.
    //
    // Navisworks runs plugin code on the main STA thread, so Run() calls the API
    // directly — no ExternalEvent (unlike every Revit tool in this repo).
    // =========================================================================
    public sealed class LevelModelsViewModel : IStepFlowTool, IStepAware, IToolCleanup
    {
        public string Title    => AppStrings.T("navis.levelModels.title");
        public string RunLabel => AppStrings.T("navis.levelModels.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("navis.levelModels.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("navis.levelModels.steps.S2"), required: true),
            new StepDefinition("S3", AppStrings.T("navis.levelModels.steps.S3"), required: false),
        };

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── State ─────────────────────────────────────────────────────────────

        private readonly List<LevelDef>  _levels  = new List<LevelDef>();
        private List<ModelRef>           _models  = new List<ModelRef>();
        private readonly string          _unit;
        private readonly bool            _hasDoc;
        private string                   _discoverNote = "";

        private StraddleRule _straddle    = StraddleRule.KeepOverlapping;
        private string       _outFolder   = "";
        private string       _pattern     = "{level}";
        private bool         _embedXrefs  = true;
        private bool         _keepProps   = true;
        private bool         _viewpoints  = true;
        private bool         _clip        = true;

        private Action<string>? _rebuild;
        private StackPanel?     _levelHost;
        private StackPanel?     _warnHost;

        public LevelModelsViewModel()
        {
            var doc = NavisApp.ActiveDocument;
            _hasDoc = doc != null && !doc.IsClear;
            _unit   = _hasDoc ? NavisLevelModels.UnitSuffix(doc!) : "";
            if (!_hasDoc) return;

            _models = NavisLevelModels.ListModels(doc!);
            SeedLevelsFromDocument(doc!);
        }

        /// <summary>Pre-fills the rows from the models' own Level property. Names and bands are
        /// both editable afterwards — assignment is always manual.</summary>
        private void SeedLevelsFromDocument(NavisDoc doc)
        {
            List<DiscoveredLevel> found;
            try { found = NavisLevelModels.DiscoverLevels(doc); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: discover levels", ex);
                found = new List<DiscoveredLevel>();
            }

            _levels.Clear();
            for (int i = 0; i < found.Count; i++)
            {
                // A level's band runs to the next level up; the topmost is left open (Top ==
                // Bottom means "no band", so Trim stays off until the user gives it one).
                double bottom = found[i].Elevation;
                double top    = i + 1 < found.Count ? found[i + 1].Elevation : bottom;
                _levels.Add(new LevelDef { Name = found[i].Name, Bottom = bottom, Top = top });
            }

            // A silent empty result is indistinguishable from a broken collector — say which.
            _discoverNote = found.Count > 0
                ? AppStrings.T("navis.levelModels.s1.discovered", found.Count)
                : AppStrings.T("navis.levelModels.s1.discoveredNone");
            if (NavisLevelModels.ProbeFailures > 0)
                _discoverNote += " " + AppStrings.T("navis.levelModels.s1.probeFailures",
                                                    NavisLevelModels.ProbeFailures);
        }

        // ── IStepAware ────────────────────────────────────────────────────────

        public void SetContentRefreshCallback(Action<string> rebuild) => _rebuild = rebuild;

        public void OnStepActivated(string stepId)
        {
            // S2's straddle row and S3's summary both read S1's state, and step content is built
            // eagerly at window construction — without this they'd render once and never update.
            if (stepId == "S2" || stepId == "S3") _rebuild?.Invoke(stepId);
        }

        public void OnWindowClosed()
        {
            // Release the captured document data; nothing else is parked on a static handler
            // (Navisworks has no ExternalEvent, so Run() owns its own lifetime).
            _rebuild   = null;
            _levelHost = null;
            _warnHost  = null;
            _models    = new List<ModelRef>();
            _levels.Clear();
        }

        // ── Step content ──────────────────────────────────────────────────────

        public FrameworkElement? GetStepContent(string stepId) => stepId switch
        {
            "S1" => BuildLevelsStep(),
            "S2" => BuildOutputStep(),
            "S3" => BuildRunStep(),
            _    => null,
        };

        // ── S1: levels & models ───────────────────────────────────────────────

        private FrameworkElement BuildLevelsStep()
        {
            if (!_hasDoc) return Hint(AppStrings.T("navis.levelModels.s1.noDocument"));

            var panel = new StackPanel();
            panel.Children.Add(Sub(AppStrings.T("navis.levelModels.s1.intro")));
            panel.Children.Add(Sub(_discoverNote));
            if (_models.Count == 0)
                panel.Children.Add(Warn(AppStrings.T("navis.levelModels.s1.noModels")));
            panel.Children.Add(Gap());

            panel.Children.Add(BuildColumnHeader());

            _levelHost = new StackPanel();
            RebuildLevelRows();
            panel.Children.Add(_levelHost);
            panel.Children.Add(Gap());

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var rescan = ControlStyles.BuildSmallButton(AppStrings.T("navis.levelModels.s1.rescan"));
            rescan.Click += (s, e) => Rescan();
            var add = ControlStyles.BuildButton(AppStrings.T("navis.levelModels.s1.addLevel"),
                                                ControlStyles.ButtonVariant.Primary);
            add.Margin = new Thickness(8, 0, 0, 0);
            add.Click += (s, e) => AddLevel();
            buttons.Children.Add(rescan);
            buttons.Children.Add(add);
            panel.Children.Add(buttons);

            _warnHost = new StackPanel();
            RefreshWarnings();
            panel.Children.Add(_warnHost);

            return panel;
        }

        private FrameworkElement BuildColumnHeader()
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lv = Sub(AppStrings.T("navis.levelModels.s1.colLevel"));
            lv.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(lv, 1);
            var md = Sub(AppStrings.T("navis.levelModels.s1.colModels"));
            md.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(md, 2);
            grid.Children.Add(lv);
            grid.Children.Add(md);
            return grid;
        }

        private void RebuildLevelRows()
        {
            if (_levelHost == null) return;
            _levelHost.Children.Clear();

            if (_levels.Count == 0)
            {
                _levelHost.Children.Add(Hint(AppStrings.T("navis.levelModels.s1.noLevels")));
                return;
            }
            foreach (var lv in _levels)
            {
                _levelHost.Children.Add(BuildLevelRow(lv));
                if (lv.Expanded) _levelHost.Children.Add(BuildBandPanel(lv));
            }
        }

        private FrameworkElement BuildLevelRow(LevelDef lv)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });                    // caret
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });                   // name
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // models
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // remove

            // Caret — opens the optional elevation band.
            var caret = new TextBlock
            {
                Text                = lv.Expanded ? "▼" : "▶",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Cursor              = Cursors.Hand,
            };
            caret.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            caret.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            caret.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            // Without an explicit background a TextBlock is hit-testable only on its glyph.
            var caretHit = new Border { Child = caret, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            caretHit.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled   = true;
                lv.Expanded = !lv.Expanded;
                RebuildLevelRows();
            };
            Grid.SetColumn(caretHit, 0);
            row.Children.Add(caretHit);

            var name = new TextBox { Text = lv.Name, Margin = new Thickness(8, 0, 0, 0) };
            name.SetResourceReference(Control.BackgroundProperty, "LemoineSelectBg");
            name.SetResourceReference(Control.ForegroundProperty, "LemoineText");
            name.SetResourceReference(Control.BorderBrushProperty, "LemoineBorderMid");
            name.SetResourceReference(Control.FontFamilyProperty, "LemoineUiFont");
            name.SetResourceReference(Control.FontSizeProperty,   "LemoineFS_MD");
            name.SetResourceReference(Control.PaddingProperty,    "LemoineTh_InputPad");
            name.SetResourceReference(Control.MinHeightProperty,  "LemoineH_Input");
            name.TextChanged += (s, e) => { lv.Name = name.Text ?? ""; RefreshWarnings(); Changed(); };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var picker = new MultiSelectDropdown
            {
                Margin        = new Thickness(8, 0, 0, 0),
                ItemsSource   = _models.Select(m => m.Key).ToList(),
                SecondaryText = _models.ToDictionary(m => m.Key, m => m.SourceFile),
                SelectedItems = lv.Models,
                Placeholder   = AppStrings.T("navis.levelModels.s1.pickModels"),
                AccessibleName = AppStrings.T("navis.levelModels.s1.pickerAccessible", lv.Name),
            };
            picker.SelectionChanged += _ => { RefreshWarnings(); Changed(); };
            Grid.SetColumn(picker, 2);
            row.Children.Add(picker);

            var del = ControlStyles.BuildSmallButton(char.ConvertFromUtf32(0xE74D),
                                                     ControlStyles.ButtonVariant.Danger); // Delete (trash)
            del.Margin  = new Thickness(8, 0, 0, 0);
            del.ToolTip = AppStrings.T("navis.levelModels.s1.removeLevel");
            del.Click += (s, e) =>
            {
                _levels.Remove(lv);
                RebuildLevelRows();
                RefreshWarnings();
                Changed();
            };
            Grid.SetColumn(del, 3);
            row.Children.Add(del);

            return row;
        }

        private FrameworkElement BuildBandPanel(LevelDef lv)
        {
            var box = new Border
            {
                Margin          = new Thickness(26, 0, 0, 8),
                Padding         = new Thickness(11, 9, 11, 9),
                BorderThickness = new Thickness(2, 0, 0, 0),
                CornerRadius    = new CornerRadius(0, 3, 3, 0),
            };
            box.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            box.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");

            var stack = new StackPanel();
            stack.Children.Add(Sub(AppStrings.T("navis.levelModels.s1.bandLabel")));

            var zrow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            zrow.Children.Add(BandCaption(AppStrings.T("navis.levelModels.s1.bottom")));
            var bottom = BandStepper(lv.Bottom);
            bottom.ValueChanged += (s, v) => { lv.Bottom = v; RefreshWarnings(); Changed(); };
            zrow.Children.Add(bottom);
            zrow.Children.Add(BandCaption(AppStrings.T("navis.levelModels.s1.top")));
            var top = BandStepper(lv.Top);
            top.ValueChanged += (s, v) => { lv.Top = v; RefreshWarnings(); Changed(); };
            zrow.Children.Add(top);
            if (!string.IsNullOrEmpty(_unit)) zrow.Children.Add(BandCaption(_unit));
            stack.Children.Add(zrow);

            var trim = new CheckBox
            {
                IsChecked = lv.Trim,
                Content   = AppStrings.T("navis.levelModels.s1.trim"),
                Margin    = new Thickness(0, 9, 0, 0),
            };
            trim.SetResourceReference(Control.ForegroundProperty, "LemoineText");
            trim.SetResourceReference(Control.FontFamilyProperty, "LemoineUiFont");
            trim.SetResourceReference(Control.FontSizeProperty,   "LemoineFS_MD");
            trim.Checked   += (s, e) => { lv.Trim = true;  RefreshWarnings(); Changed(); };
            trim.Unchecked += (s, e) => { lv.Trim = false; RefreshWarnings(); Changed(); };
            stack.Children.Add(trim);

            var note = Sub(AppStrings.T("navis.levelModels.s1.trimNote"));
            note.Margin    = new Thickness(0, 6, 0, 0);
            note.FontStyle = FontStyles.Italic;
            stack.Children.Add(note);

            box.Child = stack;
            return box;
        }

        private InlineStepper BandStepper(double value) => new InlineStepper
        {
            Value    = value,
            Decimals = 2,
            Step     = 1,
            MinValue = -1_000_000,
            MaxValue =  1_000_000,
            Margin   = new Thickness(6, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        private void AddLevel()
        {
            _levels.Add(new LevelDef { Name = AppStrings.T("navis.levelModels.s1.newLevelName", _levels.Count + 1) });
            RebuildLevelRows();
            RefreshWarnings();
            Changed();
        }

        private void Rescan()
        {
            var doc = NavisApp.ActiveDocument;
            if (doc == null || doc.IsClear) return;
            _models = NavisLevelModels.ListModels(doc);
            SeedLevelsFromDocument(doc);
            _rebuild?.Invoke("S1");   // repopulate the whole step, including the discovery note
            Changed();
        }

        // ── Warnings ──────────────────────────────────────────────────────────

        private List<string> CollectWarnings()
        {
            var list = new List<string>();
            if (!_hasDoc) return list;

            foreach (var lv in _levels.Where(l => l.Models.Count == 0))
                list.Add(AppStrings.T("navis.levelModels.warn.levelNoModels", Display(lv)));

            var assigned = new HashSet<string>(_levels.SelectMany(l => l.Models), StringComparer.OrdinalIgnoreCase);
            foreach (var m in _models.Where(m => !assigned.Contains(m.Key)))
                list.Add(AppStrings.T("navis.levelModels.warn.modelUnassigned", m.Key));

            foreach (var lv in _levels.Where(l => l.Trim && !l.HasBand))
                list.Add(AppStrings.T("navis.levelModels.warn.trimNoBand", Display(lv)));

            // Two levels writing the same filename would silently overwrite each other.
            var byFile = _levels.Where(l => l.Models.Count > 0)
                                .GroupBy(l => ResolveFileName(l.Name), StringComparer.OrdinalIgnoreCase)
                                .Where(g => g.Count() > 1);
            foreach (var g in byFile)
                list.Add(AppStrings.T("navis.levelModels.warn.duplicateFile", g.Key, g.Count()));

            return list;
        }

        private void RefreshWarnings()
        {
            if (_warnHost == null) return;
            _warnHost.Children.Clear();

            var warns = CollectWarnings();
            if (warns.Count == 0) return;

            var box = new Border
            {
                Margin          = new Thickness(0, 9, 0, 0),
                Padding         = new Thickness(9, 7, 9, 7),
                BorderThickness = new Thickness(2, 0, 0, 0),
                CornerRadius    = new CornerRadius(0, 3, 3, 0),
            };
            box.SetResourceReference(Border.BorderBrushProperty, "LemoineRed");
            box.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");

            var stack = new StackPanel();
            var head = new TextBlock
            {
                Text       = warns.Count == 1
                           ? AppStrings.T("navis.levelModels.warn.headOne")
                           : AppStrings.T("navis.levelModels.warn.head", warns.Count),
                FontWeight = FontWeights.SemiBold,
            };
            head.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            head.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            head.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            stack.Children.Add(head);

            // Bounded so a 200-model federation can't produce a 200-line banner.
            const int maxShown = 8;
            foreach (var w in warns.Take(maxShown))
            {
                var line = Sub("• " + w);
                line.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                stack.Children.Add(line);
            }
            if (warns.Count > maxShown)
                stack.Children.Add(Sub(AppStrings.T("navis.levelModels.warn.andMore", warns.Count - maxShown)));

            box.Child = stack;
            _warnHost.Children.Add(box);
        }

        // ── S2: output ────────────────────────────────────────────────────────

        private FrameworkElement BuildOutputStep()
        {
            var panel = new StackPanel();

            var folder = new FolderBrowser
            {
                Label       = AppStrings.T("navis.levelModels.s2.folder"),
                Path        = _outFolder,
                DialogTitle = AppStrings.T("navis.levelModels.s2.folderDialog"),
            };
            folder.PathChanged += p => { _outFolder = p ?? ""; Changed(); };
            panel.Children.Add(folder);
            panel.Children.Add(Sub(AppStrings.T("navis.levelModels.s2.localOnly")));
            panel.Children.Add(Gap());

            var pattern = new TextField
            {
                Label       = AppStrings.T("navis.levelModels.s2.pattern"),
                Text        = _pattern,
                Placeholder = "{level}",
            };
            pattern.TextChanged += t => { _pattern = t ?? ""; Changed(); };
            panel.Children.Add(pattern);
            panel.Children.Add(Sub(AppStrings.T("navis.levelModels.s2.patternTokens")));
            panel.Children.Add(Gap());

            // Only meaningful when something actually trims — hidden otherwise rather than shown
            // disabled, so the step never offers a control that cannot affect the run.
            if (_levels.Any(l => l.Trim))
            {
                var straddle = new SingleSelect
                {
                    Label = AppStrings.T("navis.levelModels.s2.straddle"),
                    Items = new List<string> { StraddleKeepLabel, StraddleCentroidLabel },
                };
                straddle.SelectedItem = _straddle == StraddleRule.ByCentroid ? StraddleCentroidLabel : StraddleKeepLabel;
                straddle.SelectionChanged += sel =>
                {
                    _straddle = sel == StraddleCentroidLabel ? StraddleRule.ByCentroid : StraddleRule.KeepOverlapping;
                    Changed();
                };
                panel.Children.Add(straddle);
                panel.Children.Add(Gap());
            }

            var options = new ToggleSwitches();
            options.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "views", Label = AppStrings.T("navis.levelModels.s2.optViewpoint"),
                                 Desc = AppStrings.T("navis.levelModels.s2.optViewpointDesc"), DefaultOn = _viewpoints },
                new ToggleItem { Id = "clip",  Label = AppStrings.T("navis.levelModels.s2.optClip"),
                                 Desc = AppStrings.T("navis.levelModels.s2.optClipDesc"),      DefaultOn = _clip },
                new ToggleItem { Id = "xrefs", Label = AppStrings.T("navis.levelModels.s2.optXrefs"),
                                 Desc = AppStrings.T("navis.levelModels.s2.optXrefsDesc"),     DefaultOn = _embedXrefs },
                new ToggleItem { Id = "props", Label = AppStrings.T("navis.levelModels.s2.optProps"),
                                 Desc = AppStrings.T("navis.levelModels.s2.optPropsDesc"),     DefaultOn = _keepProps },
            });
            options.StateChanged += st =>
            {
                if (st.TryGetValue("views", out var v)) _viewpoints = v;
                if (st.TryGetValue("clip",  out var c)) _clip       = c;
                if (st.TryGetValue("xrefs", out var x)) _embedXrefs = x;
                if (st.TryGetValue("props", out var p)) _keepProps  = p;
                Changed();
            };
            panel.Children.Add(options);
            panel.Children.Add(Gap());

            panel.Children.Add(Note(AppStrings.T("navis.levelModels.s2.clipNote")));
            return panel;
        }

        // Properties, not static readonly fields: a static initializer would freeze these at
        // type-load, before AppStrings is necessarily loaded, and would never pick up a language
        // change. They are compared by value against SingleSelect's selection, so both sides must
        // resolve through the same call.
        private static string StraddleKeepLabel     => AppStrings.T("navis.levelModels.s2.straddleKeep");
        private static string StraddleCentroidLabel => AppStrings.T("navis.levelModels.s2.straddleCentroid");

        // ── S3: run ───────────────────────────────────────────────────────────

        private FrameworkElement BuildRunStep()
        {
            var exportable = Exportable().ToList();
            if (exportable.Count == 0)
                return Hint(AppStrings.T("navis.levelModels.s3.nothing"));

            var panel = new StackPanel();
            panel.Children.Add(Sub(AppStrings.T("navis.levelModels.s3.head", exportable.Count,
                string.IsNullOrWhiteSpace(_outFolder) ? AppStrings.T("navis.levelModels.s3.noFolder") : _outFolder)));
            panel.Children.Add(Gap());

            foreach (var lv in exportable)
            {
                string band = lv.Trim && lv.HasBand
                    ? AppStrings.T("navis.levelModels.s3.withBand", Fmt(lv.Bottom), Fmt(lv.Top))
                    : "";
                panel.Children.Add(Sub($"• {ResolveFileName(lv.Name)}  —  "
                    + AppStrings.T("navis.levelModels.s3.modelCount", lv.Models.Count) + band));
            }
            return panel;
        }

        private IEnumerable<LevelDef> Exportable() => _levels.Where(l => l.Models.Count > 0);

        // ── Validation / summaries ────────────────────────────────────────────

        public bool IsValid(string stepId) => stepId switch
        {
            "S1" => _hasDoc && Exportable().Any(),
            "S2" => !string.IsNullOrWhiteSpace(_outFolder) && !string.IsNullOrWhiteSpace(_pattern),
            _    => true,
        };

        public string SummaryFor(string stepId)
        {
            int levels = Exportable().Count();
            switch (stepId)
            {
                case "S1":
                    int assigned = _levels.SelectMany(l => l.Models)
                                          .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    return AppStrings.T("navis.levelModels.summary.s1", levels, assigned, _models.Count);
                case "S2":
                    return string.IsNullOrWhiteSpace(_outFolder)
                        ? AppStrings.T("navis.levelModels.summary.s2None")
                        : AppStrings.T("navis.levelModels.summary.s2", levels, _outFolder);
                default:
                    return AppStrings.T("navis.levelModels.summary.s3", levels);
            }
        }

        // ── Run ───────────────────────────────────────────────────────────────

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            NavisDoc? doc = NavisApp.ActiveDocument;
            if (doc == null || doc.IsClear)
            {
                pushLog(AppStrings.T("navis.levelModels.log.noDocument"), "fail");
                onComplete(0, 1, 0);
                return;
            }

            var targets = Exportable().ToList();
            if (targets.Count == 0)
            {
                pushLog(AppStrings.T("navis.levelModels.log.noLevels"), "fail");
                onComplete(0, 1, 0);
                return;
            }

            string folder = (_outFolder ?? "").Trim();
            if (!TryPrepareFolder(folder, pushLog)) { onComplete(0, 1, 0); return; }

            // Everything the run mutates, captured so the model is restored exactly as found.
            var roots  = NavisLevelModels.RootItems(doc);
            var byKey  = _models.ToDictionary(m => m.Key, m => m.Index, StringComparer.OrdinalIgnoreCase);
            bool anyTrim = targets.Any(l => l.Trim && l.HasBand);

            List<ItemZ> items = new List<ItemZ>();
            if (anyTrim)
            {
                pushLog(AppStrings.T("navis.levelModels.log.scanning"), "info");
                try { items = NavisLevelModels.GatherItemZ(doc); }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("LevelModels: gather extents", ex);
                    pushLog(AppStrings.T("navis.levelModels.log.scanFailed", ex.Message), "fail");
                    onComplete(0, 1, 0);
                    return;
                }
                pushLog(AppStrings.T("navis.levelModels.log.scanned", items.Count),
                        items.Count > 0 ? "pass" : "warn");
                // A probe that failed on many items would otherwise read as "this model has no
                // geometry up there" and quietly trim away real elements.
                if (NavisLevelModels.ProbeFailures > 0)
                    pushLog(AppStrings.T("navis.levelModels.log.probeFailures",
                                         NavisLevelModels.ProbeFailures), "warn");
            }

            var touched = new List<NavisItem>(roots);
            if (anyTrim) touched.AddRange(items.Select(z => z.Item));
            var wasHidden = NavisLevelModels.CurrentlyHidden(touched);

            pushLog(AppStrings.T("navis.levelModels.log.start", targets.Count, folder), "info");
            if (_viewpoints)
                pushLog(AppStrings.T("navis.levelModels.log.viewpointHint"), "info");

            int pass = 0, fail = 0, skip = 0;
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    if (RunState.CancelRequested)
                    {
                        pushLog(AppStrings.T("navis.levelModels.log.stopped", i, targets.Count), "warn");
                        break;
                    }

                    var lv = targets[i];
                    var outcome = ExportLevel(doc, lv, byKey, roots, items, folder, pushLog);

                    if (outcome.Written)
                    {
                        pass++;
                        pushLog(AppStrings.T("navis.levelModels.log.wrote",
                            Display(lv), outcome.Models, outcome.File), "pass");
                    }
                    else
                    {
                        fail++;
                        pushLog(AppStrings.T("navis.levelModels.log.failed",
                            Display(lv), outcome.File, outcome.Failure), "fail");
                    }

                    onProgress((int)((i + 1) * 100.0 / targets.Count), pass, fail, skip);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: run aborted", ex);
                pushLog(AppStrings.T("navis.levelModels.log.aborted", ex.Message), "fail");
                fail++;
            }
            finally
            {
                RestoreVisibility(doc, touched, wasHidden, pushLog);
                if (_clip) NavisLevelModels.ClearClip(doc);
                items.Clear();
                touched.Clear();
                wasHidden.Clear();
            }

            pushLog(AppStrings.T("navis.levelModels.log.done", pass, fail), fail == 0 ? "pass" : "warn");
            onComplete(pass, fail, skip);
        }

        private LevelOutcome ExportLevel(
            NavisDoc doc, LevelDef lv, Dictionary<string, int> byKey,
            List<NavisItem> roots, List<ItemZ> items, string folder,
            Action<string, string> pushLog)
        {
            var outcome = new LevelOutcome
            {
                Level   = Display(lv),
                Models  = lv.Models.Count,
                Trimmed = lv.Trim && lv.HasBand,
                File    = ResolveFileName(lv.Name),
            };

            try
            {
                var owned = new HashSet<int>();
                foreach (var key in lv.Models)
                    if (byKey.TryGetValue(key, out int idx)) owned.Add(idx);

                var hide = NavisLevelModels.HideSetFor(lv, owned, roots, items, _straddle);
                outcome.Hidden = hide.Count;

                // Reveal everything first so the previous level's hides never leak into this one.
                NavisLevelModels.SetHidden(doc, roots, false);
                if (outcome.Trimmed) NavisLevelModels.SetHidden(doc, items.Select(z => z.Item).ToList(), false);
                NavisLevelModels.SetHidden(doc, hide, true);

                if (_viewpoints)
                    outcome.Clipped = NavisLevelModels.SaveViewpoint(doc, Display(lv), lv, _clip, pushLog);

                string path = Path.Combine(folder, outcome.File);
                string err  = NavisLevelModels.ExportNwd(doc, path, _embedXrefs, _keepProps);
                outcome.Written = err.Length == 0;
                outcome.Failure = err;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"LevelModels: export level '{outcome.Level}'", ex);
                outcome.Written = false;
                outcome.Failure = ex.Message;
            }
            return outcome;
        }

        private static void RestoreVisibility(NavisDoc doc, List<NavisItem> touched,
                                              List<NavisItem> wasHidden, Action<string, string> pushLog)
        {
            try
            {
                NavisLevelModels.SetHidden(doc, touched, false);
                NavisLevelModels.SetHidden(doc, wasHidden, true);
            }
            catch (Exception ex)
            {
                // The model is left with the last level's hide state — the user must know, or they
                // will think the federation lost geometry.
                DiagnosticsLog.Error("LevelModels: restore visibility", ex);
                pushLog?.Invoke(AppStrings.T("navis.levelModels.log.restoreFailed"), "fail");
            }
        }

        private bool TryPrepareFolder(string folder, Action<string, string> pushLog)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                pushLog(AppStrings.T("navis.levelModels.log.noFolder"), "fail");
                return false;
            }
            // Local-only by construction: TryExportToNwd writes a plain file and no publish/upload
            // API is called anywhere in this tool. Resolve and state the absolute destination so
            // there is never a question of where the NWDs went.
            string full;
            try { full = Path.GetFullPath(folder); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: resolve output folder", ex);
                pushLog(AppStrings.T("navis.levelModels.log.badFolder", folder, ex.Message), "fail");
                return false;
            }
            try { Directory.CreateDirectory(full); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: create output folder", ex);
                pushLog(AppStrings.T("navis.levelModels.log.folderFailed", full, ex.Message), "fail");
                return false;
            }
            pushLog(AppStrings.T("navis.levelModels.log.folder", full), "info");
            return true;
        }

        // ── Naming ────────────────────────────────────────────────────────────

        private string ResolveFileName(string levelName)
        {
            string modelBase = SafeDocTitle();
            string name = (_pattern ?? "")
                .Replace("{level}", levelName ?? "")
                .Replace("{model}", modelBase);

            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim();

            // A pattern that resolves to nothing usable is a failure, not a silent fallback.
            if (!name.Any(char.IsLetterOrDigit))
            {
                DiagnosticsLog.Warn("LevelModels", $"filename pattern '{_pattern}' resolved to '{name}' for level '{levelName}'");
                name = string.IsNullOrWhiteSpace(levelName) ? "level" : levelName.Trim();
                foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            }
            if (!name.EndsWith(".nwd", StringComparison.OrdinalIgnoreCase)) name += ".nwd";
            return name;
        }

        private static string SafeDocTitle()
        {
            try
            {
                var doc = NavisApp.ActiveDocument;
                string t = doc?.Title ?? "";
                if (!string.IsNullOrWhiteSpace(t)) return Path.GetFileNameWithoutExtension(t);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: document title", ex); }
            return "model";
        }

        private static string Display(LevelDef lv) =>
            string.IsNullOrWhiteSpace(lv.Name) ? AppStrings.T("navis.levelModels.unnamedLevel") : lv.Name.Trim();

        private string Fmt(double z) => z.ToString("0.##") + _unit;

        // ── Small UI helpers ──────────────────────────────────────────────────

        private static TextBlock Sub(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return tb;
        }

        private static TextBlock Hint(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            return tb;
        }

        private static FrameworkElement Warn(string text)
        {
            var tb = Sub(text);
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            return tb;
        }

        private static FrameworkElement Note(string text)
        {
            var box = new Border
            {
                Padding         = new Thickness(9, 7, 9, 7),
                BorderThickness = new Thickness(2, 0, 0, 0),
                CornerRadius    = new CornerRadius(0, 3, 3, 0),
            };
            box.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            box.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
            box.Child = Sub(text);
            return box;
        }

        private static TextBlock BandCaption(string text)
        {
            var tb = Sub(text);
            tb.VerticalAlignment = VerticalAlignment.Center;
            return tb;
        }

        private static FrameworkElement Gap() => new Border { Height = 8 };
    }
}
