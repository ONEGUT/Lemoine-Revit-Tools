using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

namespace LemoineTools.Tools.ScopeBoxes
{
    /// <summary>
    /// Scope Box Creator — computes building-cluster footprints from rooms (host + links)
    /// and creates scope boxes by duplicating a user-picked seed box (the Revit API cannot
    /// create scope boxes from scratch; see ScopeBoxCreatorRunHandler).
    /// </summary>
    public class ScopeBoxCreatorViewModel : IStepFlowTool, IStepAware, IReviewableTool, IRunResult, IToolCleanup
    {
        public string? ResultNoun => "scope boxes";
        public IReadOnlyList<ResultChip>? ResultChips => null;

        // ── Identity ──────────────────────────────────────────────────
        public string Title    => AppStrings.T("scopeBoxes.creator.title");
        public string RunLabel => AppStrings.T("scopeBoxes.creator.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("scopeBoxes.creator.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("scopeBoxes.creator.steps.S2"), required: true),
            new StepDefinition("S3", AppStrings.T("scopeBoxes.creator.steps.S3"), required: true),
            new StepDefinition("S4", AppStrings.T("scopeBoxes.creator.steps.S4"), required: false),
        };

        // ── Data types passed in from Command ─────────────────────────
        public sealed class DocEntry
        {
            public string    Label = string.Empty;
            public bool      IsHost;
            public ElementId LinkInstId = ElementId.InvalidElementId;
        }

        // Mode tokens (logic identifiers, persisted per run — not externalized).
        private const string ModeFullHeight = "FullHeight";
        private const string ModePerLevel   = "PerLevel";

        // Naming token vocabulary shown in the slot dropdowns (logic identifiers).
        private static readonly string[] NamingTokens =
        {
            "None", "Building Letter", "Level", "Level Range", "Model Name", "Custom",
        };

        // Live cluster-count preview is skipped above this many rooms — union-find is
        // O(n²) and must not stall the UI thread on a checkbox click.
        private const int MaxRoomsForLiveCount = 1500;

        // ── State ──────────────────────────────────────────────────────
        private readonly List<DocEntry>                _availableDocs;
        private readonly Dictionary<string, DocEntry>  _docByLabel;

        private List<string>         _selectedDocLabels = new List<string>();
        private List<ScopeLevelInfo> _scannedLevels     = new List<ScopeLevelInfo>();
        private List<RoomInfo>       _scannedRooms      = new List<RoomInfo>();
        private List<ScopeBoxEntry>  _scopeBoxes        = new List<ScopeBoxEntry>();
        private Dictionary<string, ElementId> _levelKeyToId = new Dictionary<string, ElementId>(StringComparer.Ordinal);
        private List<ElementId>      _selectedLevelIds  = new List<ElementId>();
        private string               _mode              = ModeFullHeight;
        private ElementId            _seedBoxId         = ElementId.InvalidElementId;
        private bool                 _scanning          = false;
        private bool                 _scanDone          = false;
        private readonly NamingSlotsState _naming = new NamingSlotsState
        {
            Front  = "Building Letter",
            Center = "Level Range",
            End    = "None",
        };

        // S2/S3 live-update handles
        private StackPanel? _s2Container;
        private Dispatcher? _s2Dispatcher;
        private TextBlock?  _plannedCountText;
        private SingleSelect? _seedSelect;
        private TextBlock?  _seedSizeText;
        private TextBlock?  _namePreviewText;

        private Action<string>? _rebuildContent;

        // ── ExternalEvent wiring ───────────────────────────────────────
        private readonly ScopeBoxCreatorScanHandler? _scanHandler;
        private readonly ExternalEvent?              _scanEvent;
        private readonly ScopeBoxCreatorRunHandler?  _runHandler;
        private readonly ExternalEvent?              _runEvent;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public void OnWindowClosed()
        {
            if (_scanHandler != null)
            {
                _scanHandler.OnScanComplete = null;
                _scanHandler.OnBoxesLoaded  = null;
                _scanHandler.OnError        = null;
            }
            if (_runHandler != null)
            {
                _runHandler.PushLog    = null;
                _runHandler.OnProgress = null;
                _runHandler.OnComplete = null;
            }
        }

        public ScopeBoxCreatorViewModel(
            ScopeBoxCreatorScanHandler? scanHandler, ExternalEvent? scanEvent,
            ScopeBoxCreatorRunHandler?  runHandler,  ExternalEvent? runEvent,
            List<DocEntry>?             availableDocs,
            List<ScopeBoxEntry>?        initialScopeBoxes = null)
        {
            _scanHandler   = scanHandler;
            _scanEvent     = scanEvent;
            _runHandler    = runHandler;
            _runEvent      = runEvent;
            _availableDocs = availableDocs ?? new List<DocEntry>();
            _scopeBoxes    = initialScopeBoxes ?? new List<ScopeBoxEntry>();

            _docByLabel = new Dictionary<string, DocEntry>(StringComparer.Ordinal);
            foreach (var d in _availableDocs)
                _docByLabel[d.Label] = d;

            _selectedDocLabels = _availableDocs.Select(d => d.Label).ToList();
        }

        // ═══════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildS1();
            if (stepId == "S2") return BuildS2();
            if (stepId == "S3") return BuildS3();
            if (stepId == "S4") return null; // framework renders review (IReviewableTool)
            return null;
        }

        // ── IStepAware ──────────────────────────────────────────────────
        // S2's scan is invalidated by S1 doc changes; rebuilding on activation re-triggers
        // it (same contract as Bulk Views by Level). S3's seed list refreshes on every
        // activation so a scope box drawn mid-session (the "draw one first" flow) appears
        // without reopening the window.
        public void SetContentRefreshCallback(Action<string> rebuildStepContent)
            => _rebuildContent = rebuildStepContent;

        public void OnStepActivated(string stepId)
        {
            if (stepId == "S2")
            {
                if (!_scanDone && !_scanning)
                    _rebuildContent?.Invoke("S2");
            }
            else if (stepId == "S3")
            {
                RefreshSeedList();
            }
        }

        // ── S1: Sources + geometry ─────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            var tabs = new MultiSelectTabs();
            tabs.SelectionChanged += selected =>
            {
                _selectedDocLabels = new List<string>(selected);
                // Invalidate scan — doc selection changed
                _scannedLevels.Clear();
                _scannedRooms.Clear();
                _selectedLevelIds.Clear();
                _levelKeyToId.Clear();
                _scanDone = false;
                OnValidationChanged();
            };
            tabs.SetGroups(
                new Dictionary<string, List<string>>
                {
                    { "Documents", _availableDocs.Select(d => d.Label).ToList() }
                },
                _selectedDocLabels);
            outer.Children.Add(tabs);

            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 14, 0, 10) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            outer.Children.Add(sep);

            AddDimHeader(outer, AppStrings.T("scopeBoxes.creator.labels.geoHeader"));

            var s = ScopeBoxSettings.Instance;
            AddStepperRow(outer, AppStrings.T("scopeBoxes.creator.labels.bufferXY"),
                AppStrings.T("scopeBoxes.creator.labels.bufferXYHint"),
                s.BufferXY, 0, 200, 1, 0,
                v => { ScopeBoxSettings.Instance.BufferXY = v; ScopeBoxSettings.Instance.Save(); InvalidatePlannedCount(); });
            AddStepperRow(outer, AppStrings.T("scopeBoxes.creator.labels.clusterThreshold"),
                AppStrings.T("scopeBoxes.creator.labels.clusterThresholdHint"),
                s.ClusterThreshold, 0, 500, 1, 0,
                v => { ScopeBoxSettings.Instance.ClusterThreshold = v; ScopeBoxSettings.Instance.Save(); InvalidatePlannedCount(); });
            AddStepperRow(outer, AppStrings.T("scopeBoxes.creator.labels.topLevelHeight"),
                AppStrings.T("scopeBoxes.creator.labels.topLevelHeightHint"),
                s.TopLevelHeight, 1, 100, 1, 0,
                v => { ScopeBoxSettings.Instance.TopLevelHeight = v; ScopeBoxSettings.Instance.Save(); });

            return outer;
        }

        private static void AddDimHeader(StackPanel parent, string text)
        {
            var h = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 6) };
            h.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            h.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            h.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            parent.Children.Add(h);
        }

        private static void AddStepperRow(
            StackPanel parent, string label, string hint,
            double value, double min, double max, double step, int decimals,
            Action<double> onChange)
        {
            var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4),
                                      TextWrapping = TextWrapping.Wrap };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            parent.Children.Add(lbl);

            var stepper = new InlineStepper
            {
                Value = value, MinValue = min, MaxValue = max, Step = step, Decimals = decimals,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin  = new Thickness(0, 0, 0, 2),
                ToolTip = hint,
            };
            stepper.ValueChanged += (s, v) => onChange(v);
            parent.Children.Add(stepper);

            var dim = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap,
                                      Margin = new Thickness(0, 0, 0, 10) };
            dim.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            dim.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            dim.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            parent.Children.Add(dim);
        }

        // ── S2: Scan & Layout ──────────────────────────────────────────
        private FrameworkElement BuildS2()
        {
            _s2Dispatcher = Dispatcher.CurrentDispatcher;
            _s2Container  = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            if (_scanDone)
            {
                PopulateS2();
            }
            else if (!_scanning)
            {
                ShowS2Message(AppStrings.T("scopeBoxes.creator.labels.scanning"));
                TriggerScan();
            }
            else
            {
                ShowS2Message(AppStrings.T("scopeBoxes.creator.labels.scanning"));
            }

            return _s2Container;
        }

        private void ShowS2Message(string text)
        {
            if (_s2Container == null) return;
            _s2Container.Children.Clear();
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
                                     FontStyle = FontStyles.Italic,
                                     Margin = new Thickness(0, 4, 0, 0) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _s2Container.Children.Add(tb);
        }

        private void TriggerScan()
        {
            _scanning = true;

            bool includeHost = false;
            var  linkInstIds = new List<ElementId>();
            foreach (var label in _selectedDocLabels)
            {
                if (!_docByLabel.TryGetValue(label, out var entry)) continue;
                if (entry.IsHost) includeHost = true;
                else              linkInstIds.Add(entry.LinkInstId);
            }

            _scanHandler!.IncludeHost = includeHost;
            _scanHandler.LinkInstIds  = linkInstIds;
            _scanHandler.BoxesOnly    = false;

            _scanHandler.OnScanComplete = (levels, rooms, boxes) =>
            {
                _s2Dispatcher?.BeginInvoke((Action)(() =>
                {
                    _scanning      = false;
                    _scanDone      = true;
                    _scannedLevels = levels ?? new List<ScopeLevelInfo>();
                    _scannedRooms  = rooms  ?? new List<RoomInfo>();
                    _scopeBoxes    = boxes  ?? new List<ScopeBoxEntry>();

                    // Pre-select levels that have rooms
                    _selectedLevelIds = _scannedLevels
                        .Where(l => l.RoomCount > 0)
                        .Select(l => l.LevelId)
                        .ToList();

                    PopulateS2();
                    OnValidationChanged();
                }));
            };

            _scanHandler.OnError = msg =>
            {
                _s2Dispatcher?.BeginInvoke((Action)(() =>
                {
                    _scanning = false;
                    _scanDone = true;
                    ShowS2Message(AppStrings.T("scopeBoxes.creator.labels.scanError", msg));
                    OnValidationChanged();
                }));
            };

            _scanEvent!.Raise();
        }

        private void PopulateS2()
        {
            if (_s2Container == null) return;
            _s2Container.Children.Clear();
            _levelKeyToId.Clear();

            if (_scannedLevels.Count == 0)
            {
                ShowS2Message(AppStrings.T("scopeBoxes.creator.labels.noLevels"));
                return;
            }

            // ── Layout mode ────────────────────────────────────────────
            AddDimHeader(_s2Container, AppStrings.T("scopeBoxes.creator.labels.modeHeader"));

            string fullHeightLabel = AppStrings.T("scopeBoxes.creator.labels.modeFullHeight");
            string perLevelLabel   = AppStrings.T("scopeBoxes.creator.labels.modePerLevel");
            var modeSelect = new SingleSelect
            {
                Width = 260,
                HorizontalAlignment = HorizontalAlignment.Left,
                Items = new List<string> { fullHeightLabel, perLevelLabel },
                SelectedItem = _mode == ModePerLevel ? perLevelLabel : fullHeightLabel,
            };
            modeSelect.SelectionChanged += v =>
            {
                _mode = v == perLevelLabel ? ModePerLevel : ModeFullHeight;
                InvalidatePlannedCount();
                OnValidationChanged();
            };
            _s2Container.Children.Add(modeSelect);

            var modeHint = new TextBlock
            {
                Text = AppStrings.T("scopeBoxes.creator.labels.modeHint"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 10),
            };
            modeHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            modeHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            modeHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _s2Container.Children.Add(modeHint);

            // ── Levels ─────────────────────────────────────────────────
            var groups = new Dictionary<string, List<string>>
            {
                ["Levels"] = _scannedLevels
                    .OrderBy(l => l.ElevationInternal)
                    .Select(l =>
                    {
                        string key = l.RoomCount > 0
                            ? AppStrings.T("scopeBoxes.creator.labels.levelWithRooms", l.Name, l.ElevationFt, l.RoomCount)
                            : AppStrings.T("scopeBoxes.creator.labels.levelNoRooms",  l.Name, l.ElevationFt);
                        _levelKeyToId[key] = l.LevelId;
                        return key;
                    })
                    .ToList(),
            };

            var selectedIdSet = new HashSet<long>(_selectedLevelIds.Select(id => id.Value));
            var preSelected   = _levelKeyToId
                .Where(kv => selectedIdSet.Contains(kv.Value.Value))
                .Select(kv => kv.Key)
                .ToList();

            var levelTabs = new MultiSelectTabs();
            levelTabs.SelectionChanged += selected =>
            {
                _selectedLevelIds = selected
                    .Where(k => _levelKeyToId.ContainsKey(k))
                    .Select(k => _levelKeyToId[k])
                    .GroupBy(id => id.Value)
                    .Select(g => g.First())
                    .ToList();
                InvalidatePlannedCount();
                OnValidationChanged();
            };
            levelTabs.SetGroups(groups, preSelected);
            _s2Container.Children.Add(levelTabs);

            // ── Planned-count line ─────────────────────────────────────
            _plannedCountText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            };
            _plannedCountText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _plannedCountText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            _plannedCountText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            _s2Container.Children.Add(_plannedCountText);
            InvalidatePlannedCount();
        }

        // ── Planned box count (live) ───────────────────────────────────
        // Pure math over the scanned RoomInfo records — no Revit access, so it is safe
        // on the UI thread. Skipped for very large room counts (O(n²) union-find).
        private void InvalidatePlannedCount()
        {
            if (_plannedCountText == null) return;
            int? n = ComputePlannedCount();
            _plannedCountText.Text = n.HasValue
                ? AppStrings.T("scopeBoxes.creator.labels.plannedCount", n.Value)
                : AppStrings.T("scopeBoxes.creator.labels.plannedCountDeferred");
        }

        private int? ComputePlannedCount()
        {
            if (!_scanDone || _scannedRooms.Count == 0 || _selectedLevelIds.Count == 0) return 0;
            if (_scannedRooms.Count > MaxRoomsForLiveCount) return null;

            var selNames = new HashSet<string>(
                _scannedLevels.Where(l => _selectedLevelIds.Any(id => id.Value == l.LevelId.Value))
                              .Select(l => l.Name),
                StringComparer.Ordinal);

            double threshold = ScopeBoxSettings.Instance.ClusterThreshold;

            if (_mode == ModePerLevel)
            {
                int total = 0;
                foreach (var name in selNames)
                {
                    var lr = _scannedRooms.Where(r => r.LevelName == name).ToList();
                    if (lr.Count == 0) continue;
                    total += RoomClusterSearch.ClusterRooms(lr, threshold).Count;
                }
                return total;
            }

            var rooms = _scannedRooms.Where(r => selNames.Contains(r.LevelName)).ToList();
            if (rooms.Count == 0) return 0;
            return RoomClusterSearch.ClusterRooms(rooms, threshold).Count;
        }

        // ── S3: Seed & Naming ──────────────────────────────────────────
        private FrameworkElement BuildS3()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };

            // ── Seed picker ────────────────────────────────────────────
            AddDimHeader(outer, AppStrings.T("scopeBoxes.creator.labels.seedHeader"));

            _seedSelect = new SingleSelect
            {
                Width = 260,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            outer.Children.Add(_seedSelect);

            _seedSizeText = new TextBlock { Margin = new Thickness(0, 4, 0, 0) };
            _seedSizeText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _seedSizeText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            _seedSizeText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            outer.Children.Add(_seedSizeText);

            var seedHint = new TextBlock
            {
                Text = AppStrings.T("scopeBoxes.creator.labels.seedHint"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 12),
            };
            seedHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            seedHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            seedHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(seedHint);

            PopulateSeedSelect();

            // ── Naming slots ───────────────────────────────────────────
            AddDimHeader(outer, AppStrings.T("scopeBoxes.creator.labels.namingHeader"));

            var slots = new NamingSlots(NamingTokens, _naming);
            slots.Changed += () => { UpdateNamePreview(); OnValidationChanged(); };
            outer.Children.Add(slots);

            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 4, 0, 10) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            outer.Children.Add(sep);

            AddDimHeader(outer, AppStrings.T("scopeBoxes.creator.labels.previewHeader"));

            _namePreviewText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            _namePreviewText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _namePreviewText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            _namePreviewText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var previewBorder = new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10, 6, 10, 6),
            };
            previewBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            previewBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            previewBorder.Child = _namePreviewText;
            outer.Children.Add(previewBorder);

            UpdateNamePreview();
            return outer;
        }

        private void PopulateSeedSelect()
        {
            if (_seedSelect == null) return;

            if (_scopeBoxes.Count == 0)
            {
                _seedSelect.Items = new List<string>
                    { AppStrings.T("scopeBoxes.creator.labels.seedNone") };
                _seedBoxId = ElementId.InvalidElementId;
                if (_seedSizeText != null) _seedSizeText.Text = "";
                OnValidationChanged();
                return;
            }

            string? currentName = _scopeBoxes
                .FirstOrDefault(b => b.Id.Value == _seedBoxId.Value)?.Name;

            _seedSelect.Items        = _scopeBoxes.Select(b => b.Name).ToList();
            _seedSelect.SelectedItem = currentName ?? _scopeBoxes[0].Name;
            if (currentName == null) _seedBoxId = _scopeBoxes[0].Id;
            UpdateSeedSizeText();

            _seedSelect.SelectionChanged += name =>
            {
                var entry = _scopeBoxes.FirstOrDefault(b => b.Name == name);
                _seedBoxId = entry?.Id ?? ElementId.InvalidElementId;
                UpdateSeedSizeText();
                OnValidationChanged();
            };
            OnValidationChanged();
        }

        private void UpdateSeedSizeText()
        {
            if (_seedSizeText == null) return;
            var entry = _scopeBoxes.FirstOrDefault(b => b.Id.Value == _seedBoxId.Value);
            _seedSizeText.Text = entry == null
                ? ""
                : AppStrings.T("scopeBoxes.creator.labels.seedSize",
                    entry.WidthFt.ToString("0.#"), entry.DepthFt.ToString("0.#"),
                    entry.HeightFt.ToString("0.#"));
        }

        // Fast seed-list refresh on S3 activation (BoxesOnly scan) — a box drawn while the
        // window is open shows up without reopening. Rebuilds the whole S3 step content via
        // the IStepAware callback (never re-parents children by hand).
        private void RefreshSeedList()
        {
            if (_scanHandler == null || _scanEvent == null) return;
            var dispatcher = Dispatcher.CurrentDispatcher;

            _scanHandler.BoxesOnly = true;
            _scanHandler.OnBoxesLoaded = boxes =>
            {
                dispatcher.BeginInvoke((Action)(() =>
                {
                    var fresh = boxes ?? new List<ScopeBoxEntry>();

                    // Only rebuild when the list actually changed — avoids dropdown churn on
                    // every S3 entry and guards against any rebuild→activate feedback.
                    bool same = fresh.Count == _scopeBoxes.Count
                        && fresh.Zip(_scopeBoxes, (a, b) =>
                               a.Id.Value == b.Id.Value && a.Name == b.Name)
                           .All(eq => eq);
                    if (same) return;

                    _scopeBoxes = fresh;
                    _rebuildContent?.Invoke("S3");
                    OnValidationChanged();
                }));
            };
            _scanEvent.Raise();
        }

        private void UpdateNamePreview()
        {
            if (_namePreviewText == null) return;

            var selLevels = _scannedLevels
                .Where(l => _selectedLevelIds.Any(id => id.Value == l.LevelId.Value))
                .OrderBy(l => l.ElevationInternal)
                .ToList();
            string first = selLevels.FirstOrDefault()?.Name ?? "L01";
            string last  = selLevels.LastOrDefault()?.Name  ?? "L05";
            string range = _mode == ModePerLevel || first == last
                ? first
                : AppStrings.T("scopeBoxes.creator.tokens.levelRange", first, last);
            string model = _scannedRooms
                .GroupBy(r => r.DocName, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key ?? "Architecture";

            string Resolve(string letter) => string.Join(" - ", ResolvePartsFor(letter, first, range, model));

            string a = Resolve("A");
            string b = Resolve("B");
            _namePreviewText.Text = a == b
                ? a
                : AppStrings.T("scopeBoxes.creator.labels.previewTwo", a, b);
        }

        private List<string> ResolvePartsFor(string letter, string level, string range, string model)
        {
            var parts = _naming.ResolveParts(token =>
            {
                switch (token)
                {
                    case "Building Letter": return letter;
                    case "Level":           return level;
                    case "Level Range":     return range;
                    case "Model Name":      return model;
                    default:                return "";
                }
            });
            if (parts.Count == 0) { parts.Add(letter); parts.Add(range); }
            return parts;
        }

        // ── IReviewableTool ─────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("docs",   AppStrings.T("scopeBoxes.creator.review.itemDocs")),
            ("levels", AppStrings.T("scopeBoxes.creator.review.itemLevels")),
            ("mode",   AppStrings.T("scopeBoxes.creator.review.itemMode")),
            ("seed",   AppStrings.T("scopeBoxes.creator.review.itemSeed")),
            ("count",  AppStrings.T("scopeBoxes.creator.review.itemCount")),
        };

        public IDictionary<string, string> ReviewValues
        {
            get
            {
                int? planned = ComputePlannedCount();
                var seed = _scopeBoxes.FirstOrDefault(b => b.Id.Value == _seedBoxId.Value);
                return new Dictionary<string, string>
                {
                    ["docs"]   = _selectedDocLabels.Count > 0
                        ? AppStrings.T("scopeBoxes.creator.review.docsValue", _selectedDocLabels.Count) : "—",
                    ["levels"] = _selectedLevelIds.Count > 0
                        ? AppStrings.T("scopeBoxes.creator.review.levelsValue", _selectedLevelIds.Count) : "—",
                    ["mode"]   = _mode == ModePerLevel
                        ? AppStrings.T("scopeBoxes.creator.labels.modePerLevel")
                        : AppStrings.T("scopeBoxes.creator.labels.modeFullHeight"),
                    ["seed"]   = seed?.Name ?? "—",
                    ["count"]  = planned.HasValue
                        ? planned.Value.ToString()
                        : AppStrings.T("scopeBoxes.creator.review.countDeferred"),
                };
            }
        }

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => AppStrings.T("scopeBoxes.creator.review.note");
        public string?        ReviewWarning => _seedBoxId == ElementId.InvalidElementId
            ? AppStrings.T("scopeBoxes.creator.review.warnNoSeed")
            : null;

        // ═══════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedDocLabels.Count > 0;
            if (stepId == "S2") return _scanDone && _selectedLevelIds.Count > 0;
            if (stepId == "S3") return _seedBoxId != ElementId.InvalidElementId;
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1") return _selectedDocLabels.Count > 0
                ? AppStrings.T("scopeBoxes.creator.summaries.docsCount", _selectedDocLabels.Count) : "—";
            if (stepId == "S2")
            {
                if (!_scanDone) return "—";
                string mode = _mode == ModePerLevel
                    ? AppStrings.T("scopeBoxes.creator.labels.modePerLevel")
                    : AppStrings.T("scopeBoxes.creator.labels.modeFullHeight");
                return AppStrings.T("scopeBoxes.creator.summaries.s2", _selectedLevelIds.Count, mode);
            }
            if (stepId == "S3")
            {
                var seed = _scopeBoxes.FirstOrDefault(b => b.Id.Value == _seedBoxId.Value);
                return seed?.Name ?? AppStrings.T("scopeBoxes.creator.summaries.s3NoSeed");
            }
            if (stepId == "S4") return AppStrings.T("scopeBoxes.creator.summaries.S4");
            return "—";
        }

        // ═══════════════════════════════════════════════════════════════
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            bool includeHost = false;
            var  linkInstIds = new List<ElementId>();
            foreach (var label in _selectedDocLabels)
            {
                if (!_docByLabel.TryGetValue(label, out var entry)) continue;
                if (entry.IsHost) includeHost = true;
                else              linkInstIds.Add(entry.LinkInstId);
            }

            _runHandler!.IncludeHost      = includeHost;
            _runHandler.LinkInstIds       = linkInstIds;
            _runHandler.SelectedLevelIds  = new List<ElementId>(
                _selectedLevelIds.GroupBy(id => id.Value).Select(g => g.First()));
            _runHandler.Mode              = _mode;
            _runHandler.SeedBoxId         = _seedBoxId;
            _runHandler.NamingFront        = _naming.Front;
            _runHandler.NamingFrontCustom  = _naming.FrontCustom;
            _runHandler.NamingCenter       = _naming.Center;
            _runHandler.NamingCenterCustom = _naming.CenterCustom;
            _runHandler.NamingEnd          = _naming.End;
            _runHandler.NamingEndCustom    = _naming.EndCustom;
            _runHandler.PushLog    = pushLog;
            _runHandler.OnProgress = onProgress;
            _runHandler.OnComplete = onComplete;

            pushLog(AppStrings.T("scopeBoxes.creator.log.raising"), "info");
            _runEvent!.Raise();
        }
    }
}
