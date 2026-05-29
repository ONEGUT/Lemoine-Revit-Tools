using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

using WpfGrid       = System.Windows.Controls.Grid;
using WpfPoint      = System.Windows.Point;
using WpfTextBox    = System.Windows.Controls.TextBox;
using WpfVisibility = System.Windows.Visibility;

namespace LemoineTools.Tools.LinkViews
{
    public class LinkViewsLevelViewModel : ILemoineTool, ILemoineToolSettings, ILemoineReviewable
    {
        // ── Identity ──────────────────────────────────────────────────
        public string Title    => "Bulk Views by Level";
        public string RunLabel => "Create Views in Revit →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Source Documents",    required: true),
            new StepDefinition("S2", "Levels & View Types", required: true),
            new StepDefinition("S3", "View Naming",         required: false),
            new StepDefinition("S4", "Review & Run",        required: false),
        };

        // ── Data types passed in from Command ─────────────────────────
        public sealed class DocEntry
        {
            public string    Label;
            public bool      IsHost;
            public ElementId LinkInstId;
        }

        public sealed class ViewTemplateEntry
        {
            public ElementId Id   { get; set; }
            public string    Name { get; set; }
        }

        // ── State ──────────────────────────────────────────────────────
        private readonly List<DocEntry>          _availableDocs;
        private readonly Dictionary<string, DocEntry> _docByLabel;

        private List<string>         _selectedDocLabels  = new List<string>();
        private List<LevelScanResult>_scannedLevels      = new List<LevelScanResult>();
        private Dictionary<string, ElementId> _levelKeyToId = new Dictionary<string, ElementId>(StringComparer.Ordinal);
        private List<ElementId>      _selectedLevelIds   = new List<ElementId>();
        private bool                 _create3D         = true;
        private bool                 _createFP         = true;
        private bool                 _createRCP        = true;
        private bool                 _createPrintSets  = false;
        private bool                 _scanning         = false;
        private bool                 _scanDone         = false;
        private bool                 _showAllLevels    = false;
        private string               _subDisc3D        = "";
        private string               _subDiscFP        = "";
        private string               _subDiscRCP       = "";
        private string               _buildingLabel    = "Bldg";
        private bool                 _appendViewType   = true;
        private ElementId            _template3DId   = ElementId.InvalidElementId;
        private ElementId            _templateFPId   = ElementId.InvalidElementId;
        private ElementId            _templateRCPId  = ElementId.InvalidElementId;
        private FrameworkElement     _subDiscRow3D;
        private FrameworkElement     _subDiscRowFP;
        private FrameworkElement     _subDiscRowRCP;

        // S3 naming state — Front/Center/End slots
        private string _namingFront        = "Host Level";
        private string _namingFrontCustom  = "";
        private string _namingCenter       = "Model Name";
        private string _namingCenterCustom = "";
        private string _namingEnd          = "None";
        private string _namingEndCustom    = "";
        // ModelName per LevelId (populated after Phase1 scan)
        private Dictionary<long, string> _levelModelNames = new Dictionary<long, string>();

        // S2 live-update handles
        private StackPanel  _s2Container;
        private Dispatcher  _s2Dispatcher;

        // ── ExternalEvent wiring ───────────────────────────────────────
        private readonly LinkViewsLevelPhase1Handler? _phase1Handler;
        private readonly ExternalEvent?               _phase1Event;
        private readonly LinkViewsLevelRunHandler?    _runHandler;
        private readonly ExternalEvent?               _runEvent;

        // ── Available view templates (populated by command on main thread) ─
        private readonly List<ViewTemplateEntry> _templates3D;
        private readonly List<ViewTemplateEntry> _templatesFP;
        private readonly List<ViewTemplateEntry> _templatesRCP;

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public LinkViewsLevelViewModel(
            LinkViewsLevelPhase1Handler? phase1Handler, ExternalEvent? phase1Event,
            LinkViewsLevelRunHandler?    runHandler,    ExternalEvent? runEvent,
            List<DocEntry>?              availableDocs,
            List<ViewTemplateEntry>?     templates3D  = null,
            List<ViewTemplateEntry>?     templatesFP  = null,
            List<ViewTemplateEntry>?     templatesRCP = null)
        {
            _phase1Handler = phase1Handler;
            _phase1Event   = phase1Event;
            _templates3D   = templates3D  ?? new List<ViewTemplateEntry>();
            _templatesFP   = templatesFP  ?? new List<ViewTemplateEntry>();
            _templatesRCP  = templatesRCP ?? new List<ViewTemplateEntry>();
            _runHandler    = runHandler;
            _runEvent      = runEvent;
            _availableDocs = availableDocs ?? new List<DocEntry>();

            _docByLabel = new Dictionary<string, DocEntry>(StringComparer.Ordinal);
            foreach (var d in _availableDocs)
                _docByLabel[d.Label] = d;

            // Default: all docs selected
            _selectedDocLabels = _availableDocs.Select(d => d.Label).ToList();
        }

        // ═══════════════════════════════════════════════════════════════
        // GetStepContent
        // ═══════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            if (stepId == "S1") return BuildS1();
            if (stepId == "S2") return BuildS2();
            if (stepId == "S3") return BuildS3Naming();
            if (stepId == "S4") return null; // framework renders review (ILemoineReviewable)
            return null;
        }

        // ── S1: Source Documents ───────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            var tabs = new LemoineMultiSelectTabs();
            tabs.SelectionChanged += selected =>
            {
                _selectedDocLabels = new List<string>(selected);
                // Invalidate scan — doc selection changed
                _scannedLevels.Clear();
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
                _selectedDocLabels); // pre-select all
            return tabs;
        }

        // ── S2: Levels & View Types ────────────────────────────────────
        private FrameworkElement BuildS2()
        {
            _s2Dispatcher = Dispatcher.CurrentDispatcher;
            _s2Container  = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            if (_scanDone && _scannedLevels.Count > 0)
            {
                PopulateS2();
            }
            else if (_scanDone && _scannedLevels.Count == 0)
            {
                ShowS2Message("No levels found in the selected documents.");
            }
            else if (!_scanning)
            {
                ShowS2Message("Scanning rooms in selected documents…");
                TriggerPhase1();
            }
            else
            {
                ShowS2Message("Scanning rooms in selected documents…");
            }

            return _s2Container;
        }

        private void ShowS2Message(string text)
        {
            _s2Container.Children.Clear();
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
                                     FontStyle = FontStyles.Italic,
                                     Margin = new Thickness(0, 4, 0, 0) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _s2Container.Children.Add(tb);
        }

        private void TriggerPhase1()
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

            _phase1Handler!.IncludeHost = includeHost;
            _phase1Handler.LinkInstIds = linkInstIds;

            _phase1Handler.OnLevelsLoaded = results =>
            {
                _s2Dispatcher?.BeginInvoke((Action)(() =>
                {
                    _scanning      = false;
                    _scanDone      = true;
                    _scannedLevels = results ?? new List<LevelScanResult>();

                    if (_scannedLevels.Count == 0)
                    {
                        ShowS2Message("No levels found in the selected documents.");
                        OnValidationChanged();
                        return;
                    }

                    // Pre-select only levels that have rooms; PopulateS2 handles key format
                    _selectedLevelIds = _scannedLevels
                        .Where(l => l.RoomCount > 0)
                        .Select(l => l.LevelId)
                        .GroupBy(id => id.Value)
                        .Select(g => g.First())
                        .ToList();

                    // Build model-name map: levelId.Value → dominant doc name
                    _levelModelNames = _scannedLevels
                        .GroupBy(l => l.LevelId.Value)
                        .ToDictionary(g => g.Key, g => g.First().ModelName ?? "");
                    PopulateS2();
                    OnValidationChanged();
                }));
            };

            _phase1Handler.OnError = msg =>
            {
                _s2Dispatcher?.BeginInvoke((Action)(() =>
                {
                    _scanning = false;
                    _scanDone = true;
                    ShowS2Message($"Scan error: {msg}");
                    OnValidationChanged();
                }));
            };

            _phase1Event!.Raise();
        }

        private void PopulateS2()
        {
            _s2Container.Children.Clear();
            _levelKeyToId.Clear();
            _subDiscRow3D  = null;
            _subDiscRowFP  = null;
            _subDiscRowRCP = null;

            // Filter levels based on showAllLevels toggle
            var visibleLevels = _showAllLevels
                ? _scannedLevels
                : _scannedLevels.Where(l => l.RoomCount > 0).ToList();

            if (visibleLevels.Count == 0)
            {
                string hint = _showAllLevels
                    ? "No levels found in the selected documents."
                    : "No levels with placed rooms found. Enable \"Show all levels\" below to see all model levels.";
                var msg = new TextBlock
                {
                    Text = hint, TextWrapping = TextWrapping.Wrap,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 4, 0, 0),
                };
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                _s2Container.Children.Add(msg);
            }
            else
            {
                // Group by source document, elevation order within each group
                var groups = visibleLevels
                    .GroupBy(l => l.DocumentName ?? "(Unknown)")
                    .ToDictionary(
                        g => g.Key,
                        g => g.OrderBy(l => l.ElevationFt)
                              .Select(l =>
                              {
                                  string key = l.RoomCount > 0
                                      ? $"{l.Name}   (Elev {l.ElevationFt:F2} ft · {l.RoomCount} room{(l.RoomCount != 1 ? "s" : "")})"
                                      : $"{l.Name}   (Elev {l.ElevationFt:F2} ft)";
                                  string uniqueKey = $"[{l.DocumentName}] {key}";
                                  _levelKeyToId[uniqueKey] = l.LevelId;
                                  return uniqueKey;
                              })
                              .ToList());

                var selectedIdSet = new HashSet<long>(_selectedLevelIds.Select(id => id.Value));
                var preSelected   = _levelKeyToId
                    .Where(kv => selectedIdSet.Contains(kv.Value.Value))
                    .Select(kv => kv.Key)
                    .ToList();
                if (preSelected.Count == 0)
                    preSelected = _levelKeyToId.Keys.ToList();

                var levelTabs = new LemoineMultiSelectTabs();
                levelTabs.SelectionChanged += selected =>
                {
                    _selectedLevelIds = selected
                        .Where(s => _levelKeyToId.ContainsKey(s))
                        .Select(s => _levelKeyToId[s])
                        .GroupBy(id => id.Value)
                        .Select(g => g.First())
                        .ToList();
                    OnValidationChanged();
                };
                levelTabs.SetGroups(groups, preSelected);
                _s2Container.Children.Add(levelTabs);
            }

            // ── Show All Levels toggle ─────────────────────────────────
            _s2Container.Children.Add(new FrameworkElement { Height = 8 });
            var showAllToggle = new LemoineToggleSwitches();
            showAllToggle.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "showAll", Label = "Show all levels",
                                 Desc = "Include levels without placed rooms (fallback)",
                                 DefaultOn = _showAllLevels },
            });
            showAllToggle.StateChanged += state =>
            {
                _showAllLevels = state.TryGetValue("showAll", out var v) && v;
                PopulateS2();
                OnValidationChanged();
            };
            _s2Container.Children.Add(showAllToggle);

            if (visibleLevels.Count == 0) return;

            // ── View-type section ──────────────────────────────────────
            _s2Container.Children.Add(new FrameworkElement { Height = 12 });

            var typeHeader = new TextBlock { Text = "VIEW TYPES TO CREATE",
                                             Margin = new Thickness(0, 0, 0, 6) };
            typeHeader.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            typeHeader.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            typeHeader.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _s2Container.Children.Add(typeHeader);

            var toggles = new LemoineToggleSwitches();
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "3d",  Label = "3D Views",
                                 Desc = "Isometric view with section box per cluster",       DefaultOn = _create3D  },
                new ToggleItem { Id = "fp",  Label = "Floor Plans",
                                 Desc = "Plan view with crop box and view range per cluster", DefaultOn = _createFP  },
                new ToggleItem { Id = "rcp", Label = "Ceiling Plans",
                                 Desc = "RCP with crop box and view range per cluster",       DefaultOn = _createRCP },
            });

            // ── Sub Discipline / View Template section ─────────────────
            var subDiscHeader = new TextBlock { Text = "VIEW OPTIONS",
                                                Margin = new Thickness(0, 10, 0, 4) };
            subDiscHeader.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            subDiscHeader.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            subDiscHeader.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            // Column header row
            var colHeader = new StackPanel { Orientation = Orientation.Horizontal,
                                              Margin = new Thickness(40, 0, 0, 4) };
            var colSubDisc = new TextBlock { Text = "Sub Discipline", Width = 120 };
            colSubDisc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            colSubDisc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            colSubDisc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            var colTemplate = new TextBlock { Text = "View Template", Width = 150,
                                              Margin = new Thickness(8, 0, 0, 0) };
            colTemplate.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            colTemplate.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            colTemplate.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            colHeader.Children.Add(colSubDisc);
            colHeader.Children.Add(colTemplate);

            _subDiscRow3D  = BuildViewTypeRow("3D",  _subDisc3D,  v => _subDisc3D  = v,
                                              _templates3D,  _template3DId,  id => _template3DId  = id, _create3D);
            _subDiscRowFP  = BuildViewTypeRow("FP",  _subDiscFP,  v => _subDiscFP  = v,
                                              _templatesFP,  _templateFPId,  id => _templateFPId  = id, _createFP);
            _subDiscRowRCP = BuildViewTypeRow("RCP", _subDiscRCP, v => _subDiscRCP = v,
                                              _templatesRCP, _templateRCPId, id => _templateRCPId = id, _createRCP);

            toggles.StateChanged += state =>
            {
                _create3D  = state.TryGetValue("3d",  out var v3)  && v3;
                _createFP  = state.TryGetValue("fp",  out var vf)  && vf;
                _createRCP = state.TryGetValue("rcp", out var vr2) && vr2;
                if (_subDiscRow3D  != null) _subDiscRow3D.Visibility  = _create3D  ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                if (_subDiscRowFP  != null) _subDiscRowFP.Visibility  = _createFP  ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                if (_subDiscRowRCP != null) _subDiscRowRCP.Visibility = _createRCP ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                OnValidationChanged();
            };

            // ── Print sets (opt-in per run) ────────────────────────────
            var printSetToggles = new LemoineToggleSwitches();
            printSetToggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "printSets", Label = "Create print sets",
                                 Desc = "Add created views to Coordination print sets (3D Views, Floor Plans, Ceiling Plans)",
                                 DefaultOn = _createPrintSets },
            });
            printSetToggles.StateChanged += state =>
            {
                _createPrintSets = state.TryGetValue("printSets", out var v) && v;
            };

            _s2Container.Children.Add(toggles);
            _s2Container.Children.Add(printSetToggles);
            _s2Container.Children.Add(subDiscHeader);
            _s2Container.Children.Add(colHeader);
            _s2Container.Children.Add(_subDiscRow3D);
            _s2Container.Children.Add(_subDiscRowFP);
            _s2Container.Children.Add(_subDiscRowRCP);
        }

        private FrameworkElement BuildViewTypeRow(
            string typeLabel,
            string subDiscValue,  Action<string>   subDiscSetter,
            List<ViewTemplateEntry> templates, ElementId selectedTemplateId, Action<ElementId> templateSetter,
            bool visible)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 4),
                Visibility  = visible ? WpfVisibility.Visible : WpfVisibility.Collapsed,
            };

            var lbl = new TextBlock
            {
                Text              = typeLabel,
                Width             = 40,
                VerticalAlignment = VerticalAlignment.Center,
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            row.Children.Add(lbl);

            var tb = new WpfTextBox { Text = subDiscValue, Width = 120 };
            tb.SetResourceReference(FrameworkElement.HeightProperty,                 "LemoineH_Input");
            tb.SetResourceReference(System.Windows.Controls.Control.PaddingProperty, "LemoineTh_InputPad");
            tb.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            tb.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            tb.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_SM");
            tb.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineMonoFont");
            tb.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorder");
            tb.TextChanged += (s, e) => subDiscSetter(tb.Text);
            row.Children.Add(tb);

            row.Children.Add(new FrameworkElement { Width = 8 });

            var templateNames = new List<string>(new[] { "(none)" }
                .Concat(templates.Select(t => t.Name)));
            string selectedName = templates.FirstOrDefault(
                t => t.Id != null && t.Id.Value == selectedTemplateId.Value)?.Name ?? "(none)";

            var templateSelect = new LemoineSingleSelect
            {
                Width        = 150,
                Items        = templateNames,
                SelectedItem = selectedName,
            };
            templateSelect.SelectionChanged += name =>
            {
                var entry = templates.FirstOrDefault(t => t.Name == name);
                templateSetter(entry?.Id ?? ElementId.InvalidElementId);
            };
            row.Children.Add(templateSelect);

            return row;
        }

        // ── S3: Review & Run ───────────────────────────────────────────
        private struct CardDef
        {
            public string Label; public Func<string> Val; public int Row; public int Col;
            public CardDef(string l, Func<string> v, int r, int c)
            { Label = l; Val = v; Row = r; Col = c; }
        }

        // ── S3: View Naming ────────────────────────────────────────────
        // Naming token options for LVL
        private static readonly string[] LvlNamingOptions =
        {
            "None", "Host Level", "Model Name", "View Type", "Custom"
        };

        private FrameworkElement BuildS3Naming()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };

            // Preview declared early so all slot closures can capture it
            var previewText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            previewText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            previewText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            previewText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            void UpdatePreview()
            {
                string levelEx   = _scannedLevels.FirstOrDefault()?.Name ?? "Level 2";
                string modelEx   = _scannedLevels.FirstOrDefault()?.ModelName ?? "Architecture";
                string typeEx    = _create3D ? "3D" : _createFP ? "FP" : "RCP";
                string bldgLabel  = string.IsNullOrWhiteSpace(_buildingLabel) ? "Bldg" : _buildingLabel.Trim();
                string baseEx     = $"L{levelEx}";
                string baseExMult = $"L{levelEx} - {bldgLabel} A";

                string ResolveSlot(string slot, string custom)
                {
                    switch (slot)
                    {
                        case "Host Level":  return baseEx;
                        case "Model Name":  return modelEx;
                        case "View Type":   return typeEx;
                        case "Custom":      return string.IsNullOrWhiteSpace(custom) ? "(custom)" : custom.Trim();
                        default:            return null;
                    }
                }

                bool anySet = _namingFront != "None" || _namingCenter != "None" || _namingEnd != "None";
                List<string> parts;
                if (anySet)
                {
                    parts = new[] {
                        ResolveSlot(_namingFront,  _namingFrontCustom),
                        ResolveSlot(_namingCenter, _namingCenterCustom),
                        ResolveSlot(_namingEnd,    _namingEndCustom)
                    }.Where(s => !string.IsNullOrEmpty(s)).ToList();
                }
                else
                {
                    parts = new List<string> { baseEx };
                }

                if (_appendViewType) parts.Add(typeEx);
                if (parts.Count == 0) { parts.Add(baseEx); parts.Add(typeEx); }

                // Show multi-cluster variant on a second line when building label is relevant
                bool hostLevelInSlots = _namingFront == "Host Level" || _namingCenter == "Host Level" || _namingEnd == "Host Level"
                                        || (!anySet);
                string single = string.Join(" - ", parts);
                string multi  = hostLevelInSlots
                    ? single.Replace(baseEx, baseExMult)
                    : single;
                previewText.Text = single == multi
                    ? single
                    : $"{single}\n{multi}  (multi-cluster)";
            }

            // Helper: one slot row — [label] [combo] [textbox, only if Custom]
            void AddSlotRow(string label,
                            string curVal, Action<string> setVal,
                            string curCustom, Action<string> setCustom)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal,
                                           Margin = new Thickness(0, 0, 0, 8) };

                var lbl = new TextBlock { Text = label, Width = 60,
                                          VerticalAlignment = VerticalAlignment.Center };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                row.Children.Add(lbl);

                var cb = new LemoineSingleSelect
                {
                    Width        = 130,
                    Items        = LvlNamingOptions,
                    SelectedItem = curVal,
                };
                row.Children.Add(cb);

                var tb = new WpfTextBox
                {
                    Text       = curCustom,
                    Width      = 140,
                    Margin     = new Thickness(6, 0, 0, 0),
                    Visibility = curVal == "Custom" ? WpfVisibility.Visible : WpfVisibility.Collapsed,
                };
                tb.SetResourceReference(FrameworkElement.HeightProperty,                   "LemoineH_Input");
                tb.SetResourceReference(System.Windows.Controls.Control.PaddingProperty,   "LemoineTh_InputPad");
                tb.SetResourceReference(WpfTextBox.ForegroundProperty,   "LemoineText");
                tb.SetResourceReference(WpfTextBox.BackgroundProperty,   "LemoineSelectBg");
                tb.SetResourceReference(WpfTextBox.FontSizeProperty,     "LemoineFS_SM");
                tb.SetResourceReference(WpfTextBox.FontFamilyProperty,   "LemoineMonoFont");
                tb.SetResourceReference(WpfTextBox.BorderBrushProperty,  "LemoineBorder");
                row.Children.Add(tb);

                cb.SelectionChanged += v =>
                {
                    if (v == null) return;
                    setVal(v);
                    tb.Visibility = v == "Custom" ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                    UpdatePreview();
                    OnValidationChanged();
                };
                tb.TextChanged += (s, e) => { setCustom(tb.Text); UpdatePreview(); };

                outer.Children.Add(row);
            }

            AddSlotRow("Front",  _namingFront,  v => _namingFront  = v, _namingFrontCustom,  v => _namingFrontCustom  = v);
            AddSlotRow("Center", _namingCenter, v => _namingCenter = v, _namingCenterCustom, v => _namingCenterCustom = v);
            AddSlotRow("End",    _namingEnd,    v => _namingEnd    = v, _namingEndCustom,    v => _namingEndCustom    = v);

            // ── Building label input ───────────────────────────────────────────
            var bldgLabelRow = new StackPanel { Orientation = Orientation.Horizontal,
                                                Margin = new Thickness(0, 0, 0, 8) };
            var bldgLbl = new TextBlock { Text = "Bldg label", Width = 60,
                                          VerticalAlignment = VerticalAlignment.Center };
            bldgLbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            bldgLbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            bldgLbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            bldgLabelRow.Children.Add(bldgLbl);

            var bldgLabelBox = new WpfTextBox { Text = _buildingLabel, Width = 80 };
            bldgLabelBox.SetResourceReference(FrameworkElement.HeightProperty,                   "LemoineH_Input");
            bldgLabelBox.SetResourceReference(System.Windows.Controls.Control.PaddingProperty,   "LemoineTh_InputPad");
            bldgLabelBox.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            bldgLabelBox.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            bldgLabelBox.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_SM");
            bldgLabelBox.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineMonoFont");
            bldgLabelBox.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorder");
            bldgLabelBox.TextChanged += (s, e) => { _buildingLabel = bldgLabelBox.Text; UpdatePreview(); };
            bldgLabelRow.Children.Add(bldgLabelBox);

            var bldgHint = new TextBlock
            {
                Text = " — used when multiple building clusters found per level",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            bldgHint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            bldgHint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            bldgHint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            bldgLabelRow.Children.Add(bldgHint);
            outer.Children.Add(bldgLabelRow);

            // ── Append view type suffix toggle ────────────────────────────────
            var appendTypeToggle = new LemoineToggleSwitches();
            appendTypeToggle.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "appendType", Label = "Append view type",
                                 Desc = "Append 3D / FP / RCP as the final name segment",
                                 DefaultOn = _appendViewType },
            });
            appendTypeToggle.StateChanged += state =>
            {
                _appendViewType = state.TryGetValue("appendType", out var v) && v;
                UpdatePreview();
            };
            outer.Children.Add(appendTypeToggle);
            outer.Children.Add(new FrameworkElement { Height = 6 });

            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 4, 0, 10) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            outer.Children.Add(sep);

            var previewHeader = new TextBlock { Text = "PREVIEW", Margin = new Thickness(0, 0, 0, 4) };
            previewHeader.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            previewHeader.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            previewHeader.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(previewHeader);

            var previewBorder = new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10, 6, 10, 6),
            };
            previewBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
            previewBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            previewBorder.Child = previewText;
            outer.Children.Add(previewBorder);

            UpdatePreview();
            ValidationChanged += (s, e) => UpdatePreview();
            return outer;
        }

        // ── ILemoineReviewable (P3) — framework renders the review step ───────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("docs",   "Source Documents"),
            ("levels", "Levels Selected"),
            ("types",  "View Types"),
            ("min",    "Min. Views Expected"),
        };

        public IDictionary<string, string> ReviewValues
        {
            get
            {
                var types = new List<string>();
                if (_create3D)  types.Add("3D");
                if (_createFP)  types.Add("FP");
                if (_createRCP) types.Add("RCP");
                int typeCount = (_create3D ? 1 : 0) + (_createFP ? 1 : 0) + (_createRCP ? 1 : 0);
                int min       = _selectedLevelIds.Count * typeCount;
                return new Dictionary<string, string>
                {
                    ["docs"]   = _selectedDocLabels.Count > 0 ? $"{_selectedDocLabels.Count} document(s)" : "—",
                    ["levels"] = _selectedLevelIds.Count > 0 ? $"{_selectedLevelIds.Count} level(s)" : "—",
                    ["types"]  = types.Count > 0 ? string.Join(" · ", types) : "—",
                    ["min"]    = min > 0 ? $"≥ {min}  (more with multi-building levels)" : "—",
                };
            }
        }

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => "Creates cropped coordination views per level and building cluster. " +
            "Levels with rooms spread across separate building footprints will generate one view per cluster.";
        public string?        ReviewWarning => null;

        // ═══════════════════════════════════════════════════════════════
        // IsValid / SummaryFor
        // ═══════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1") return _selectedDocLabels.Count > 0;
            if (stepId == "S2") return _scanDone
                                     && _selectedLevelIds.Count > 0
                                     && (_create3D || _createFP || _createRCP);
            if (stepId == "S3") return true; // naming is always optional
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1") return _selectedDocLabels.Count > 0
                ? $"{_selectedDocLabels.Count} document(s)" : "—";
            if (stepId == "S2")
            {
                if (!_scanDone) return "—";
                var t = new List<string>();
                if (_create3D)  t.Add("3D");
                if (_createFP)  t.Add("FP");
                if (_createRCP) t.Add("RCP");
                return $"{_selectedLevelIds.Count} level(s) · {string.Join("/", t)}";
            }
            if (stepId == "S3")
            {
                var set = new[] { _namingFront, _namingCenter, _namingEnd }
                    .Where(s => s != "None").ToList();
                return set.Count > 0 ? string.Join(" / ", set) : "Default";
            }
            if (stepId == "S4") return "Ready to run";
            return "—";
        }

        // ═══════════════════════════════════════════════════════════════
        // Run
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
            _runHandler.LinkInstIds      = linkInstIds;
            _runHandler.SelectedLevelIds = new List<ElementId>(
                _selectedLevelIds.GroupBy(id => id.Value).Select(g => g.First()));
            _runHandler.Create3D         = _create3D;
            _runHandler.CreateFP         = _createFP;
            _runHandler.CreateRCP        = _createRCP;
            _runHandler.NamingFront        = _namingFront;
            _runHandler.NamingFrontCustom  = _namingFrontCustom;
            _runHandler.NamingCenter       = _namingCenter;
            _runHandler.NamingCenterCustom = _namingCenterCustom;
            _runHandler.NamingEnd          = _namingEnd;
            _runHandler.NamingEndCustom    = _namingEndCustom;
            _runHandler.LevelModelNames    = new Dictionary<long, string>(_levelModelNames);
            _runHandler.SubDisc3D          = _subDisc3D;
            _runHandler.SubDiscFP          = _subDiscFP;
            _runHandler.SubDiscRCP         = _subDiscRCP;
            _runHandler.Template3D         = _template3DId;
            _runHandler.TemplateFP         = _templateFPId;
            _runHandler.TemplateRCP        = _templateRCPId;
            _runHandler.CreatePrintSets    = _createPrintSets;
            _runHandler.BuildingLabel      = _buildingLabel;
            _runHandler.AppendViewType     = _appendViewType;
            _runHandler.PushLog          = pushLog;
            _runHandler.OnProgress       = onProgress;
            _runHandler.OnComplete       = onComplete;

            pushLog("Raising Revit ExternalEvent…", "info");
            _runEvent!.Raise();
        }

        // ═══════════════════════════════════════════════════════════════
        // ILemoineToolSettings — declarative spec
        // ═══════════════════════════════════════════════════════════════
        public LemoineToolSettingsSpec GetSettingsSpec()
        {
            var s = LinkViewsLevelSettings.Instance;
            return new LemoineToolSettingsSpec
            {
                Id          = "T04a",
                Label       = "Link Views \u2014 Level",
                Icon        = "04",
                Description = "Clustering and view geometry settings for Link Views Level.",
                Groups = new List<LemoineSettingsGroup>
                {
                    new LemoineSettingsGroup
                    {
                        Id = "G1", Title = "View Geometry", OpenByDefault = true,
                        Settings = new List<LemoineSettingDef>
                        {
                            new LemoineSettingDef { Id="bufferXY", Kind="number",
                                Label="XY buffer",
                                Hint="Margin added around each building cluster crop box (feet).",
                                Options=new NumberOpts { Unit="ft", Min=0, Max=200, Step=1 },
                                Default=s.BufferXY },
                            new LemoineSettingDef { Id="clusterThreshold", Kind="number",
                                Label="Cluster threshold",
                                Hint="Maximum room-edge gap for union-find building cluster merging (feet).",
                                Options=new NumberOpts { Unit="ft", Min=0, Max=500, Step=1 },
                                Default=s.ClusterThreshold },
                            new LemoineSettingDef { Id="cutOffset", Kind="number",
                                Label="Cut plane offset",
                                Hint="Height above level elevation for the floor/ceiling plan cut plane (feet).",
                                Options=new NumberOpts { Unit="ft", Min=0, Max=30, Step=0.5 },
                                Default=s.CutOffset },
                        }
                    }
                }
            };
        }

        public void ApplySettings(string groupId, string settingId, object value)
        {
            var s = LinkViewsLevelSettings.Instance;
            if (groupId == "G1")
            {
                if (settingId == "bufferXY"         && value is double bxy)  s.BufferXY         = bxy;
                if (settingId == "clusterThreshold" && value is double ct)   s.ClusterThreshold = ct;
                if (settingId == "cutOffset"        && value is double co)   s.CutOffset        = co;
            }
            s.Save();
        }

    }
}
