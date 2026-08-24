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
    // THREADING. The window runs on its own STA thread (NavisToolWindow explains why:
    // on Navisworks' own thread the host ate every keystroke). The Navisworks API may
    // only be touched from Navisworks' main thread, so:
    //   • the constructor runs there, called from AddInPlugin.Execute, and captures
    //     everything the UI will need — models, levels, units, document title;
    //   • Rescan and Run marshal back through NavisMainThread;
    //   • nothing else in this file may call the API.
    // =========================================================================
    public sealed class LevelModelsViewModel : IStepFlowTool, IStepAware, IStepNavigable, IToolCleanup
    {
        public string Title    => AppStrings.T("navis.levelModels.title");
        public string RunLabel => AppStrings.T("navis.levelModels.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S0", AppStrings.T("navis.levelModels.steps.S0"), required: true),
            new StepDefinition("S1", AppStrings.T("navis.levelModels.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("navis.levelModels.steps.S2"), required: true),
            new StepDefinition("S3", AppStrings.T("navis.levelModels.steps.S3"), required: false),
        };

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── State ─────────────────────────────────────────────────────────────

        private readonly List<LevelDef>  _levels  = new List<LevelDef>();
        private List<ModelRef>           _models  = new List<ModelRef>();
        // Bands are always shown in FEET now, so this is a constant label rather than the
        // document's own unit — a metric model still types and reads feet.
        private const string             _unit = "ft";
        private readonly bool            _hasDoc;
        private readonly string          _docTitle;

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

        // ── Scan state (step S0) ──────────────────────────────────────────────
        private enum ScanState { Idle, Running, Done, Failed }
        private ScanState _scan = ScanState.Idle;
        private string    _scanMessage = "";
        private string    _documentKey = "";
        private bool      _restoredFromStore;
        /// <summary>Key of the model the level list is read from. Empty until the scan picks one.</summary>
        private string    _sourceModelKey = "";
        private Action?   _stopThrobber;

        /// <summary>
        /// Built on Navisworks' MAIN thread (from AddInPlugin.Execute, before the window's thread
        /// starts), so every read here is a legal API call and needs no marshalling. Everything the
        /// UI needs later is captured now — including the document title, because the filename
        /// preview reaches for it while the first step is being built on the window thread, at
        /// which point the main thread is still blocked waiting for that window to appear.
        /// </summary>
        public LevelModelsViewModel()
        {
            // Deliberately CHEAP. The window must appear before any scanning starts, so this only
            // reads what the chrome needs; the real work happens in step S0 once the window is up.
            var doc   = NavisApp.ActiveDocument;
            _hasDoc   = doc != null && !doc.IsClear;
            _docTitle = ReadDocTitle(doc);
            _documentKey = ReadDocumentKey(doc);
        }

        /// <summary>Identifies the document for the saved-setup store. The file path is the only
        /// stable handle Navisworks offers; an unsaved document has none and simply does not
        /// persist.</summary>
        private static string ReadDocumentKey(NavisDoc? doc)
        {
            try
            {
                string f = doc?.FileName ?? "";
                if (!string.IsNullOrWhiteSpace(f)) return f;
                string c = doc?.CurrentFileName ?? "";
                if (!string.IsNullOrWhiteSpace(c)) return c;
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: document key", ex); }
            return "";
        }

        // ── Scan (step S0) ────────────────────────────────────────────────────

        /// <summary>Kicks the scan off on Navisworks' main thread and repaints S0 when it lands.
        /// Called from OnStepActivated("S0"), which fires as soon as the window opens — so the
        /// window is on screen and showing a throbber before any work starts.</summary>
        private void BeginScan(bool isRescan)
        {
            if (_scan == ScanState.Running) return;
            _scan = ScanState.Running;
            _scanMessage = "";
            _rebuild?.Invoke("S0");
            Changed();

            NavisMainThread.Post(() =>
            {
                string message;
                ScanState result;
                try
                {
                    message = RunScan(isRescan);
                    result  = ScanState.Done;
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("LevelModels: scan", ex);
                    message = AppStrings.T("navis.levelModels.s0.failed", ex.Message);
                    result  = ScanState.Failed;
                }

                // A federation that has never been saved has no file path, so the setup store has
                // no bucket to file it under and NOTHING will persist. Say it once, here, rather
                // than letting every later edit no-op in silence.
                if (result == ScanState.Done && string.IsNullOrEmpty(_documentKey))
                    message += " " + AppStrings.T("navis.levelModels.s0.notPersisted");

                // Back to the window's thread to touch any UI state.
                _scan        = result;
                _scanMessage = message;
                _rebuild?.Invoke("S0");
                _rebuild?.Invoke("S1");
                Changed();
                // Hand over to Levels & models. StepFlowWindow marshals this onto the window's
                // own dispatcher, so raising it from Navisworks' main thread is safe.
                if (result == ScanState.Done)
                {
                    try { NavigateRequested?.Invoke(this, 1); }
                    catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: advance past scan", ex); }
                }
            });
        }

        /// <summary>The scan itself. Runs on the main thread. Returns the line shown under the
        /// throbber when it finishes.</summary>
        private string RunScan(bool isRescan)
        {
            var doc = NavisApp.ActiveDocument;
            if (doc == null || doc.IsClear) return AppStrings.T("navis.levelModels.s0.noDocument");

            _models = NavisLevelModels.ListModels(doc);
            if (_models.Count == 0) return AppStrings.T("navis.levelModels.s0.noModels");

            // A saved setup wins on a first open — it is the user's own work from last time.
            if (!isRescan && TryRestoreSaved()) return _scanMessage;

            int sourceIndex = ResolveSourceModelIndex();
            var found = NavisLevelModels.DiscoverLevels(doc, sourceIndex);
            MergeDiscovered(found, isRescan);

            var report = NavisLevelModels.LastDiscovery;

            if (_levels.Count == 0)
            {
                // Say WHAT the tree held, not just that nothing was found — a bare "no levels" is
                // indistinguishable from a broken scan, and that is exactly how the wrapper-node
                // bug hid. DescribeTree prints every depth the search looked at.
                return report.Layers.Count > 0
                    ? AppStrings.T("navis.levelModels.s0.noLevelsButChildren",
                                   report.SourceModel, DescribeTree(report))
                    : AppStrings.T("navis.levelModels.s0.noLevels", report.SourceModel);
            }

            var assignMode = NavisLevelModels.AutoAssign(_models, _levels.Where(l => !l.UserEdited).ToList());

            int assigned = _levels.SelectMany(l => l.Models).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            SaveSetup();

            string done = AppStrings.T("navis.levelModels.s0.done",
                                       _levels.Count, report.SourceModel,
                                       assigned, _models.Count);

            // Levels normally sit under a "<file>.rvt : n : location <…>" node rather than at the
            // top of the tree, so name the branch they came from — it is the one thing that tells
            // the user at a glance whether the scan read levels or something else entirely.
            if (!string.IsNullOrWhiteSpace(report.LayerPath))
                done += " " + AppStrings.T("navis.levelModels.s0.readFrom", report.LayerPath);

            // Say which way the assignment went. A federation split by DISCIPLINE names no levels
            // in any file name, so the all-to-all fallback is the NORMAL path there, not an edge
            // case — and silently doing it would look like the tool assigning models at random.
            if (assignMode == NavisLevelModels.AutoAssignMode.AllToAll)
                done += " " + AppStrings.T("navis.levelModels.s0.allToAll");
            else if (assigned == 0 && _models.Count > 0)
                done += " " + AppStrings.T("navis.levelModels.s0.noneMatched");

            return done;
        }

        /// <summary>A one-line rendering of what the source model's tree actually holds, depth by
        /// depth. This is what turns "no levels" from a dead end into an answer — it shows whether
        /// the model was exported divided by level at all.</summary>
        private static string DescribeTree(NavisLevelModels.DiscoveryReport report)
        {
            var parts = new List<string>();
            foreach (var layer in report.Layers)
            {
                if (layer.Names.Count == 0) continue;
                string shown = string.Join(", ", layer.Names.Take(6));
                int rest = layer.Names.Count - 6;
                if (rest > 0) shown += AppStrings.T("navis.levelModels.s0.andMore", rest);
                parts.Add(shown);
            }
            return parts.Count == 0
                ? AppStrings.T("navis.levelModels.s0.emptyTree")
                : string.Join("  \u203a  ", parts);
        }

        /// <summary>Which model the level list is read from. Honours an explicit pick, otherwise
        /// prefers an architectural model by name, otherwise the first model.</summary>
        private int ResolveSourceModelIndex()
        {
            if (!string.IsNullOrEmpty(_sourceModelKey))
            {
                var picked = _models.FirstOrDefault(
                    m => string.Equals(m.Key, _sourceModelKey, StringComparison.OrdinalIgnoreCase));
                if (picked != null) return picked.Index;
            }

            // Default to the Arch file — it is the one that carries a full level structure.
            var arch = _models.FirstOrDefault(m => LooksArchitectural(m));
            var chosen = arch ?? _models.FirstOrDefault();
            if (chosen != null) _sourceModelKey = chosen.Key;
            return chosen?.Index ?? 0;
        }

        private static bool LooksArchitectural(ModelRef m)
        {
            string hay = ((m.DisplayName ?? "") + " " + (m.SourceFile ?? "")).ToUpperInvariant();
            return hay.Contains("ARCH") || hay.Contains("-AR-") || hay.Contains("_AR_");
        }

        /// <summary>
        /// Folds a fresh discovery into the current list. A level the user has touched is left
        /// exactly as it is — name, band and models — so a rescan can never undo hand-tuning; an
        /// untouched level takes the newly-read name and band; a level that is new to the model is
        /// added; and an untouched level the model no longer has is dropped.
        /// </summary>
        private void MergeDiscovered(List<DiscoveredLevel> found, bool isRescan)
        {
            if (!isRescan) _levels.RemoveAll(l => !l.UserEdited);

            foreach (var d in found)
            {
                var existing = _levels.FirstOrDefault(
                    l => string.Equals(l.Name, d.Name, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    _levels.Add(new LevelDef { Name = d.Name, Bottom = Finite(d.Elevation), Top = Finite(d.Top) });
                    continue;
                }
                if (existing.UserEdited) continue;   // hands off — the user owns this row

                existing.Bottom = Finite(d.Elevation);
                existing.Top    = Finite(d.Top);
            }

            // Drop untouched rows the source model no longer reports.
            var names = new HashSet<string>(found.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            _levels.RemoveAll(l => !l.UserEdited && !names.Contains(l.Name));

            // Bands run floor-to-floor: each untouched level's top becomes the next one's bottom,
            // which is more useful than the group's own geometry height (a level group's bounding
            // box stops at its tallest element, leaving a gap under the floor above).
            var ordered = _levels.OrderBy(l => l.Bottom).ToList();
            for (int i = 0; i < ordered.Count - 1; i++)
            {
                if (ordered[i].UserEdited) continue;
                double nextBottom = ordered[i + 1].Bottom;
                if (nextBottom > ordered[i].Bottom) ordered[i].Top = nextBottom;
            }

            // Only impose discovery's ordering when the user has not arranged the list themselves.
            if (!_levels.Any(l => l.UserEdited))
            {
                _levels.Clear();
                _levels.AddRange(ordered);
            }
        }

        // ── Saved setup ───────────────────────────────────────────────────────

        private bool TryRestoreSaved()
        {
            try
            {
                var saved = LevelModelsStore.Load(_documentKey);
                if (saved == null) return false;

                _levels.Clear();
                _levels.AddRange(saved.Value.Levels);
                if (!string.IsNullOrEmpty(saved.Value.SourceModel)) _sourceModelKey = saved.Value.SourceModel;
                _restoredFromStore = true;

                // The output settings come back with the levels. A pattern of "" means the setup
                // predates them being stored, so the current default stands rather than being
                // overwritten with an empty box.
                var o = saved.Value.Output;
                if (o != null)
                {
                    _outFolder  = o.Folder ?? "";
                    if (!string.IsNullOrWhiteSpace(o.Pattern)) _pattern = o.Pattern;
                    _straddle   = o.Straddle;
                    _viewpoints = o.Viewpoints;
                    _clip       = o.Clip;
                    _embedXrefs = o.EmbedXrefs;
                    _keepProps  = o.KeepProps;
                }

                // A model that has since left the federation must not linger as a phantom
                // assignment that silently exports nothing.
                var live = new HashSet<string>(_models.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
                int dropped = 0;
                foreach (var lv in _levels)
                    for (int i = lv.Models.Count - 1; i >= 0; i--)
                        if (!live.Contains(lv.Models[i])) { lv.Models.RemoveAt(i); dropped++; }

                _scanMessage = dropped > 0
                    ? AppStrings.T("navis.levelModels.s0.restoredDropped", _levels.Count, dropped)
                    : AppStrings.T("navis.levelModels.s0.restored", _levels.Count);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: restore saved setup", ex);
                return false;
            }
        }

        private void SaveSetup()
        {
            // An unsaved federation has no file path, so there is no bucket to file the setup
            // under and nothing persists. That is reported once on the scan step rather than
            // silently doing nothing every time the user changes something.
            if (string.IsNullOrEmpty(_documentKey)) return;
            LevelModelsStore.Save(_documentKey, _levels, _sourceModelKey, CurrentOutput());
        }

        /// <summary>The S2 settings as one value for the store.</summary>
        private LevelModelsStore.OutputSettings CurrentOutput() => new LevelModelsStore.OutputSettings
        {
            Folder     = _outFolder,
            Pattern    = _pattern,
            Straddle   = _straddle,
            Viewpoints = _viewpoints,
            Clip       = _clip,
            EmbedXrefs = _embedXrefs,
            KeepProps  = _keepProps,
        };

        /// <summary>Marks a level as the user's and persists — every edit path calls this.</summary>
        private void TouchLevel(LevelDef lv)
        {
            lv.UserEdited = true;
            SaveSetup();
        }

        /// <summary>Discovery can report a level whose geometry could not be measured; the UI needs
        /// a real number, so anything non-finite becomes 0.</summary>
        private static double Finite(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;

        // ── IStepAware ────────────────────────────────────────────────────────

        public void SetContentRefreshCallback(Action<string> rebuild) => _rebuild = rebuild;

        public void OnStepActivated(string stepId)
        {
            // S0 fires the moment the window opens (StepFlowWindow activates step 0 during setup),
            // which is exactly the hook the scan wants: the window is already on screen.
            if (stepId == "S0" && _scan == ScanState.Idle) { BeginScan(isRescan: false); return; }

            // S2's straddle row and S3's summary both read S1's state, and step content is built
            // eagerly at window construction — without this they'd render once and never update.
            if (stepId == "S2" || stepId == "S3") _rebuild?.Invoke(stepId);
        }

        public void OnWindowClosed()
        {
            // Release the captured document data; nothing else is parked on a static handler
            // (Navisworks has no ExternalEvent, so Run() owns its own lifetime).
            _stopThrobber?.Invoke();
            _stopThrobber = null;
            _rebuild   = null;
            _levelHost = null;
            _warnHost  = null;
            _models    = new List<ModelRef>();
            _levels.Clear();
        }

        // ── IStepNavigable — lets the scan step hand over once it finishes ────

        public event EventHandler<int>? NavigateRequested;

        // ── Step content ──────────────────────────────────────────────────────

        public FrameworkElement? GetStepContent(string stepId) => stepId switch
        {
            "S0" => BuildScanStep(),
            "S1" => BuildLevelsStep(),
            "S2" => BuildOutputStep(),
            "S3" => BuildRunStep(),
            _    => null,
        };

        // ── S0: scanning ──────────────────────────────────────────────────────

        private FrameworkElement BuildScanStep()
        {
            var panel = new StackPanel();

            if (!_hasDoc)
                { panel.Children.Add(Hint(AppStrings.T("navis.levelModels.s1.noDocument"))); return panel; }

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (_scan == ScanState.Running) row.Children.Add(BuildThrobber());

            var status = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping      = TextWrapping.Wrap,
                Text = _scan switch
                {
                    ScanState.Running => AppStrings.T("navis.levelModels.s0.running"),
                    ScanState.Done    => _scanMessage,
                    ScanState.Failed  => _scanMessage,
                    _                 => AppStrings.T("navis.levelModels.s0.idle"),
                },
            };
            status.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            status.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            status.SetResourceReference(TextBlock.ForegroundProperty,
                _scan == ScanState.Failed ? "LemoineRed" : "LemoineText");
            row.Children.Add(status);
            panel.Children.Add(row);

            panel.Children.Add(Gap());
            panel.Children.Add(Sub(AppStrings.T("navis.levelModels.s0.explain")));

            if (_scan == ScanState.Done || _scan == ScanState.Failed)
            {
                panel.Children.Add(Gap());
                var again = ControlStyles.BuildSmallButton(AppStrings.T("navis.levelModels.s0.scanAgain"));
                again.Click += (s, e) => BeginScan(isRescan: true);
                panel.Children.Add(again);
            }
            return panel;
        }

        /// <summary>A spinning arc. The animation and its transform are built PER INSTANCE and
        /// never shared: a static Freezable reused across tool windows crashes Revit/Navisworks
        /// outright (CLAUDE.md), because each window is a separate STA thread.</summary>
        private FrameworkElement BuildThrobber()
        {
            var arc = new System.Windows.Shapes.Path
            {
                Width  = 16,
                Height = 16,
                StrokeThickness = 2,
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Data = System.Windows.Media.Geometry.Parse(
                    "M 8,1 A 7,7 0 1 1 1,8"),   // three-quarter arc
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            arc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "LemoineAccent");

            var spin = new RotateTransform(0);
            arc.RenderTransform = spin;

            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From           = 0,
                To             = 360,
                Duration       = new Duration(TimeSpan.FromSeconds(0.9)),
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            };
            spin.BeginAnimation(RotateTransform.AngleProperty, anim);

            // Stopped explicitly on close: an animation left running holds the render thread's
            // clock and keeps the visual alive after the window has gone.
            _stopThrobber = () =>
            {
                try { spin.BeginAnimation(RotateTransform.AngleProperty, null); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: stop throbber", ex); }
            };
            return arc;
        }

        // ── S1: levels & models ───────────────────────────────────────────────

        private FrameworkElement BuildLevelsStep()
        {
            if (!_hasDoc) return Hint(AppStrings.T("navis.levelModels.s1.noDocument"));

            var panel = new StackPanel();
            panel.Children.Add(Sub(AppStrings.T("navis.levelModels.s1.intro")));
            if (_models.Count == 0)
                panel.Children.Add(Warn(AppStrings.T("navis.levelModels.s1.noModels")));
            panel.Children.Add(Gap());

            // Which model the levels are read from. Defaults to the Arch file; changing it
            // rescans, because the level list is that model's tree.
            if (_models.Count > 0)
            {
                var src = new SingleSelect
                {
                    Label = AppStrings.T("navis.levelModels.s1.sourceModel"),
                    Items = _models.Select(m => m.Key).ToList(),
                };
                if (!string.IsNullOrEmpty(_sourceModelKey)) src.SelectedItem = _sourceModelKey;
                src.SelectionChanged += sel =>
                {
                    if (string.IsNullOrEmpty(sel) || sel == _sourceModelKey) return;
                    _sourceModelKey = sel!;
                    SaveSetup();
                    BeginScan(isRescan: true);
                };
                panel.Children.Add(src);
                panel.Children.Add(Sub(AppStrings.T("navis.levelModels.s1.sourceModelHint")));
                panel.Children.Add(Gap());
            }

            panel.Children.Add(BuildColumnHeader());

            _levelHost = new StackPanel();
            RebuildLevelRows();
            panel.Children.Add(_levelHost);
            panel.Children.Add(Gap());

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var rescan = ControlStyles.BuildSmallButton(AppStrings.T("navis.levelModels.s1.rescan"));
            rescan.ToolTip = AppStrings.T("navis.levelModels.s1.rescanTip");
            rescan.Click += (s, e) => BeginScan(isRescan: true);
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
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // reorder
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(114) });                   // name
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

            // Reorder — explicit up/down rather than drag. The rows carry a text box, a dropdown
            // and a delete button, so a drag would fight every one of them for the same gesture.
            int idx = _levels.IndexOf(lv);
            var moves = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(4, 0, 0, 0) };
            var up = MoveButton("▲", AppStrings.T("navis.levelModels.s1.moveUp"), idx > 0,
                                () => MoveLevel(lv, -1));
            var down = MoveButton("▼", AppStrings.T("navis.levelModels.s1.moveDown"), idx < _levels.Count - 1,
                                () => MoveLevel(lv, +1));
            moves.Children.Add(up);
            moves.Children.Add(down);
            Grid.SetColumn(moves, 1);
            row.Children.Add(moves);

            var name = new TextBox { Text = lv.Name, Margin = new Thickness(8, 0, 0, 0) };
            name.SetResourceReference(Control.BackgroundProperty, "LemoineSelectBg");
            name.SetResourceReference(Control.ForegroundProperty, "LemoineText");
            name.SetResourceReference(Control.BorderBrushProperty, "LemoineBorderMid");
            name.SetResourceReference(Control.FontFamilyProperty, "LemoineUiFont");
            name.SetResourceReference(Control.FontSizeProperty,   "LemoineFS_MD");
            name.SetResourceReference(Control.PaddingProperty,    "LemoineTh_InputPad");
            name.SetResourceReference(Control.MinHeightProperty,  "LemoineH_Input");
            name.TextChanged += (s, e) => { lv.Name = name.Text ?? ""; TouchLevel(lv); RefreshWarnings(); Changed(); };
            Grid.SetColumn(name, 2);
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
            picker.SelectionChanged += _ => { TouchLevel(lv); RefreshWarnings(); Changed(); };
            Grid.SetColumn(picker, 3);
            row.Children.Add(picker);

            var del = ControlStyles.BuildSmallButton(char.ConvertFromUtf32(0xE74D),
                                                     ControlStyles.ButtonVariant.Danger); // Delete (trash)
            del.Margin  = new Thickness(8, 0, 0, 0);
            del.ToolTip = AppStrings.T("navis.levelModels.s1.removeLevel");
            del.Click += (s, e) =>
            {
                _levels.Remove(lv);
                SaveSetup();
                RebuildLevelRows();
                RefreshWarnings();
                Changed();
            };
            Grid.SetColumn(del, 4);
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
            bottom.ValueChanged += (s, v) => { lv.Bottom = v; TouchLevel(lv); RefreshWarnings(); Changed(); };
            zrow.Children.Add(bottom);
            zrow.Children.Add(BandCaption(AppStrings.T("navis.levelModels.s1.top")));
            var top = BandStepper(lv.Top);
            top.ValueChanged += (s, v) => { lv.Top = v; TouchLevel(lv); RefreshWarnings(); Changed(); };
            zrow.Children.Add(top);
            zrow.Children.Add(BandCaption(_unit));   // always feet, whatever the model is authored in
            stack.Children.Add(zrow);

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

        /// <summary>Moves a level one place up or down. Reordering IS an edit — it is the user
        /// arranging the list — so it marks the row and persists, which also stops the next
        /// rescan from re-sorting the list back into elevation order.</summary>
        private void MoveLevel(LevelDef lv, int delta)
        {
            int from = _levels.IndexOf(lv);
            int to   = from + delta;
            if (from < 0 || to < 0 || to >= _levels.Count) return;

            _levels.RemoveAt(from);
            _levels.Insert(to, lv);
            lv.UserEdited = true;
            SaveSetup();
            RebuildLevelRows();
            Changed();
        }

        private Button MoveButton(string glyph, string tip, bool enabled, Action onClick)
        {
            var b = ControlStyles.BuildSmallButton(glyph);
            b.ToolTip   = tip;
            b.IsEnabled = enabled;
            b.MinWidth  = 22;
            b.Padding   = new Thickness(0);
            b.Margin    = new Thickness(0, 0, 0, 1);
            b.Click    += (s, e) => onClick();
            return b;
        }

        private void AddLevel()
        {
            var lv = new LevelDef
            {
                Name       = AppStrings.T("navis.levelModels.s1.newLevelName", _levels.Count + 1),
                UserEdited = true,   // hand-added: a rescan must never remove or rewrite it
            };
            _levels.Add(lv);
            SaveSetup();
            RebuildLevelRows();
            RefreshWarnings();
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

            foreach (var lv in _levels.Where(l => !l.HasBand))
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
            folder.PathChanged += p => { _outFolder = p ?? ""; SaveSetup(); Changed(); };
            panel.Children.Add(folder);
            panel.Children.Add(Sub(AppStrings.T("navis.levelModels.s2.localOnly")));
            panel.Children.Add(Gap());

            var pattern = new TextField
            {
                Label       = AppStrings.T("navis.levelModels.s2.pattern"),
                Text        = _pattern,
                Placeholder = "{level}",
            };
            pattern.TextChanged += t => { _pattern = t ?? ""; SaveSetup(); Changed(); };
            panel.Children.Add(pattern);
            panel.Children.Add(Sub(AppStrings.T("navis.levelModels.s2.patternTokens")));
            panel.Children.Add(Gap());

            // Only meaningful when something actually trims — hidden otherwise rather than shown
            // disabled, so the step never offers a control that cannot affect the run.
            if (_levels.Any(l => l.HasBand))
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
                    SaveSetup();
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
                SaveSetup();
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
                string band = lv.HasBand
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
            "S0" => _scan == ScanState.Done || _scan == ScanState.Failed,
            "S1" => _hasDoc && Exportable().Any(),
            "S2" => !string.IsNullOrWhiteSpace(_outFolder) && !string.IsNullOrWhiteSpace(_pattern),
            _    => true,
        };

        public string SummaryFor(string stepId)
        {
            int levels = Exportable().Count();
            switch (stepId)
            {
                case "S0":
                    return _scan switch
                    {
                        ScanState.Running => AppStrings.T("navis.levelModels.summary.s0Running"),
                        ScanState.Done    => AppStrings.T("navis.levelModels.summary.s0Done", _levels.Count, _models.Count),
                        ScanState.Failed  => AppStrings.T("navis.levelModels.summary.s0Failed"),
                        _                 => AppStrings.T("navis.levelModels.summary.s0Idle"),
                    };
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

        /// <summary>
        /// Called on the window's own thread. The export is queued onto Navisworks' main thread
        /// and this returns immediately — exactly the shape the Revit tools have, where Run() only
        /// raises an ExternalEvent and completion arrives through the callbacks.
        ///
        /// Deliberately Post (fire-and-forget) rather than a blocking Invoke: the window thread has
        /// to stay free to paint progress and to accept a Cancel click, and RunState's flag is what
        /// carries that cancellation across to the main thread.
        /// </summary>
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            // onComplete MUST fire exactly once, whatever happens on the far side. Without this
            // guard an escape before the run's own onComplete would leave StepFlowWindow stuck
            // showing a run in progress, with Reset flipped to Cancel and no way back short of
            // closing the window.
            bool completed = false;
            void CompleteOnce(int pass, int fail, int skip)
            {
                if (completed) return;
                completed = true;
                onComplete(pass, fail, skip);
            }

            NavisMainThread.Post(() =>
            {
                try { RunOnMainThread(pushLog, onProgress, CompleteOnce); }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("LevelModels: run failed on main thread", ex);
                    pushLog(AppStrings.T("navis.levelModels.log.aborted", ex.Message), "fail");
                    CompleteOnce(0, 1, 0);
                }
                finally
                {
                    CompleteOnce(0, 0, 0);   // no-op if the run already reported its own result
                }
            });
        }

        private void RunOnMainThread(
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
            bool anyTrim = targets.Any(l => l.HasBand);

            List<ItemZ> items = new List<ItemZ>();
            if (anyTrim)
            {
                pushLog(AppStrings.T("navis.levelModels.log.scanning"), "info");
                PumpUi();
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

            // Projected once, not per level: rebuilding this for every level allocated a fresh
            // list of every geometry item in the federation each time round the loop.
            var allItems  = anyTrim ? items.Select(z => z.Item).ToList() : new List<NavisItem>();
            var touched   = new List<NavisItem>(roots);
            touched.AddRange(allItems);
            var wasHidden = NavisLevelModels.CurrentlyHidden(touched);

            // Each exported NWD must contain ONLY the viewpoint this run makes for that level.
            // The document's existing viewpoints are taken out for the duration and put back in
            // the finally below — nothing here saves the document, so a failure costs the
            // session's viewpoint list, never the file's.
            var heldViewpoints = NavisLevelModels.TakeViewpoints(doc);
            if (heldViewpoints != null)
                pushLog(AppStrings.T("navis.levelModels.log.viewpointsHeld", heldViewpoints.Count), "info");

            pushLog(AppStrings.T("navis.levelModels.log.start", targets.Count, folder), "info");
            if (_viewpoints)
                pushLog(AppStrings.T("navis.levelModels.log.viewpointHint"), "info");

            int pass = 0, fail = 0, skip = 0;
            int viewpointsSaved = 0, viewpointsClipped = 0;
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
                    var outcome = ExportLevel(doc, lv, byKey, roots, items, allItems, folder, pushLog);
                    if (outcome.Viewpoint) viewpointsSaved++;
                    if (outcome.Clipped)   viewpointsClipped++;

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
                    PumpUi();
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
                NavisLevelModels.RestoreViewpoints(doc, heldViewpoints);
                if (_clip) NavisLevelModels.ClearClip(doc);
                items.Clear();
                allItems.Clear();
                touched.Clear();
                wasHidden.Clear();
            }

            // One line for the whole run rather than one per level: a clip that failed has already
            // printed its own warning, and this is the count that says whether the option did
            // anything at all.
            if (_viewpoints)
                pushLog(AppStrings.T("navis.levelModels.log.viewpointSummary",
                                     viewpointsSaved, viewpointsClipped),
                        (_clip && viewpointsClipped < viewpointsSaved) ? "warn" : "info");

            pushLog(AppStrings.T("navis.levelModels.log.done", pass, fail), fail == 0 ? "pass" : "warn");
            onComplete(pass, fail, skip);
        }

        private LevelOutcome ExportLevel(
            NavisDoc doc, LevelDef lv, Dictionary<string, int> byKey,
            List<NavisItem> roots, List<ItemZ> items, List<NavisItem> allItems, string folder,
            Action<string, string> pushLog)
        {
            var outcome = new LevelOutcome
            {
                Level   = Display(lv),
                Models  = lv.Models.Count,
                Trimmed = lv.HasBand,
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
                if (allItems.Count > 0) NavisLevelModels.SetHidden(doc, allItems, false);
                NavisLevelModels.SetHidden(doc, hide, true);

                // Drop the previous level's viewpoint before adding this one, or level 2's NWD
                // would ship carrying level 1's as well.
                NavisLevelModels.ClearViewpoints(doc);
                if (_viewpoints)
                {
                    var vpResult = NavisLevelModels.SaveViewpoint(doc, Display(lv), lv, _clip, pushLog);
                    outcome.Viewpoint = vpResult.Saved;
                    outcome.Clipped   = vpResult.Clipped;   // the CLIP, not "a viewpoint was saved"
                }

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

        /// <summary>The captured title — never an API call, so it is safe from any thread.</summary>
        private string SafeDocTitle() => _docTitle;

        private static string ReadDocTitle(NavisDoc? doc)
        {
            try
            {
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

        /// <summary>Keeps NAVISWORKS responsive while a long export holds its main thread.
        ///
        /// The run body executes on Navisworks' main thread, so this drains that thread's queue —
        /// not the window's. The window is on its own thread now and paints progress freely
        /// without any help, so this is no longer what makes the progress bar move or the Cancel
        /// button clickable; it exists so the host itself does not appear hung for the length of
        /// a big federation export.</summary>
        private static void PumpUi()
        {
            try
            {
                var d = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                if (d.HasShutdownStarted || d.HasShutdownFinished) return;
                d.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: pump UI thread", ex); }
        }
    }
}
