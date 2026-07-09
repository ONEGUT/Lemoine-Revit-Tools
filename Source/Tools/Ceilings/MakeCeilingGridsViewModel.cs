using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

using WpfTextBox = System.Windows.Controls.TextBox;

namespace LemoineTools.Tools.Ceilings
{
    public class MakeCeilingGridsViewModel : IStepFlowTool, IStepAware, IReviewableTool, IRunResult, IToolCleanup
    {
        // Self-describing result label for the run strip (see IRunResult).
        public string? ResultNoun => "views";
        public System.Collections.Generic.IReadOnlyList<LemoineTools.Framework.ResultChip>? ResultChips => null;

        // ── DocEntry — passed in from Command ─────────────────────────────────
        public sealed class DocEntry
        {
            public string    Label = string.Empty;
            public bool      IsHost;
            public ElementId LinkInstId = ElementId.InvalidElementId;
        }

        // ── IStepFlowTool identity ─────────────────────────────────────────────
        public string Title    => AppStrings.T("ceilings.makeGrids.title");
        public string RunLabel => AppStrings.T("ceilings.makeGrids.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("docs",    AppStrings.T("ceilings.makeGrids.steps.docs"),    required: true),
            new StepDefinition("filter",  AppStrings.T("ceilings.makeGrids.steps.filter"), required: false),
            new StepDefinition("export",  AppStrings.T("ceilings.makeGrids.steps.export"),      required: true),
            new StepDefinition("run",     AppStrings.T("ceilings.makeGrids.steps.run"),         required: false),
        };

        // ── State ─────────────────────────────────────────────────────────────
        private readonly List<DocEntry>            _availableDocs;
        private readonly Dictionary<string, DocEntry> _docByLabel;

        private List<string>           _selectedDocLabels        = new List<string>();
        private List<CeilingTypeEntry> _ceilingTypes             = new List<CeilingTypeEntry>();

        // Exclusion is name-based ("Family|Type") so unchecking a type hides it across
        // every model — consistent with the name-based hide filter in the run handler.
        private HashSet<string>        _excludedTypeKeys         = new HashSet<string>(StringComparer.Ordinal);
        private HashSet<string>        _allTypeKeys              = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _displayToKey = new Dictionary<string, string>(StringComparer.Ordinal);

        private bool                   _scanning                 = false;
        private bool                   _scanDone                 = false;

        private string _outputFolder             = MakeCeilingGridsSettings.Instance.OutputFolder;
        private bool   _useCeilingGridsSubfolder = MakeCeilingGridsSettings.Instance.UseCeilingGridsSubfolder;

        // Live UI handles
        private StackPanel? _filterContainer;
        private Dispatcher? _filterDispatcher;

        // IStepAware: rebuilds a step's content widget (set by StepFlowWindow)
        private Action<string>? _rebuildContent;

        // ── ExternalEvent wiring ───────────────────────────────────────────
        private readonly MakeCeilingGridsPhase1Handler? _phase1Handler;
        private readonly Autodesk.Revit.UI.ExternalEvent?  _phase1Event;
        private readonly MakeCeilingGridsRunHandler?    _runHandler;
        private readonly Autodesk.Revit.UI.ExternalEvent?  _runEvent;

        public event EventHandler? ValidationChanged;

        // Null the callbacks parked on the static handlers so this VM isn't retained after close.
        public void OnWindowClosed()
        {
            if (_phase1Handler != null)
            {
                _phase1Handler.OnTypesLoaded = null;
                _phase1Handler.OnError = null;
            }
            if (_runHandler != null)
            {
                _runHandler.PushLog    = null;
                _runHandler.OnProgress = null;
                _runHandler.OnComplete = null;
            }
        }

        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public MakeCeilingGridsViewModel(
            MakeCeilingGridsPhase1Handler? phase1Handler, Autodesk.Revit.UI.ExternalEvent? phase1Event,
            MakeCeilingGridsRunHandler?    runHandler,    Autodesk.Revit.UI.ExternalEvent? runEvent,
            List<DocEntry>?                availableDocs)
        {
            _phase1Handler = phase1Handler;
            _phase1Event   = phase1Event;
            _runHandler    = runHandler;
            _runEvent      = runEvent;
            _availableDocs = availableDocs ?? new List<DocEntry>();

            _docByLabel = new Dictionary<string, DocEntry>(StringComparer.Ordinal);
            foreach (var d in _availableDocs)
                _docByLabel[d.Label] = d;

            _selectedDocLabels = _availableDocs.Select(d => d.Label).ToList();
        }

        // ═════════════════════════════════════════════════════════════════════
        // GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "docs":   return BuildDocsStep();
                case "filter": return BuildFilterStep();
                case "export": return BuildExportStep();
                case "run":    return null; // framework renders review (IReviewableTool)
                default:       return null;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // IStepAware — re-scan when the user enters the filter step
        // ═════════════════════════════════════════════════════════════════════
        // The filter step's content is built eagerly once at window construction, so
        // a change to the document selection (which clears the scan via the docs-step
        // handler) would otherwise leave the filter step showing the stale ceiling
        // types from the previous document set, with exclusions that no longer match.
        // Rebuilding on activation re-runs BuildFilterStep, which re-triggers the scan
        // for the current document selection.
        public void SetContentRefreshCallback(Action<string> rebuildStepContent)
            => _rebuildContent = rebuildStepContent;

        public void OnStepActivated(string stepId)
        {
            if (stepId != "filter") return;
            // Only rebuild when the scan is stale; a completed scan is preserved so
            // the user's type exclusions survive navigating away and back.
            if (!_scanDone && !_scanning)
                _rebuildContent?.Invoke("filter");
        }

        // ── Step 1: Select Documents ───────────────────────────────────────
        private FrameworkElement BuildDocsStep()
        {
            var outer = new StackPanel();

            if (_availableDocs.Count == 0)
            {
                var none = new TextBlock
                {
                    Text         = AppStrings.T("ceilings.makeGrids.labels.noDocs"),
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle    = FontStyles.Italic,
                };
                none.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                none.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                none.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                outer.Children.Add(none);
                return outer;
            }

            var tabs = new MultiSelectTabs();
            tabs.SelectionChanged += selected =>
            {
                _selectedDocLabels = new List<string>(selected);
                _scanDone  = false;
                _scanning  = false;
                _ceilingTypes.Clear();
                _excludedTypeKeys.Clear();
                OnValidationChanged();
            };
            tabs.SetGroups(
                new Dictionary<string, List<string>>
                {
                    { "Documents", _availableDocs.Select(d => d.Label).ToList() }
                },
                _selectedDocLabels);

            outer.Children.Add(tabs);
            return outer;
        }

        // ── Step 2: Filter Ceiling Types ───────────────────────────────────
        private FrameworkElement BuildFilterStep()
        {
            _filterDispatcher = Dispatcher.CurrentDispatcher;
            _filterContainer  = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            if (_scanDone && _ceilingTypes.Count > 0)
                PopulateFilterPanel();
            else if (_scanDone && _ceilingTypes.Count == 0)
                ShowFilterMessage(AppStrings.T("ceilings.makeGrids.labels.noTypes"));
            else if (!_scanning)
            {
                ShowFilterMessage(AppStrings.T("ceilings.makeGrids.labels.scanning"));
                TriggerPhase1();
            }
            else
                ShowFilterMessage(AppStrings.T("ceilings.makeGrids.labels.scanning"));

            return _filterContainer;
        }

        private void ShowFilterMessage(string text)
        {
            _filterContainer!.Children.Clear();
            var tb = new TextBlock
            {
                Text         = text,
                TextWrapping = TextWrapping.Wrap,
                FontStyle    = FontStyles.Italic,
                Margin       = new Thickness(0, 4, 0, 0),
            };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            _filterContainer.Children.Add(tb);
        }

        private void TriggerPhase1()
        {
            if (_phase1Handler == null || _phase1Event == null) return;
            _scanning = true;

            bool includeHost = false;
            var  linkInstIds = new List<ElementId>();

            foreach (var label in _selectedDocLabels)
            {
                if (!_docByLabel.TryGetValue(label, out var entry)) continue;
                if (entry.IsHost) includeHost = true;
                else              linkInstIds.Add(entry.LinkInstId);
            }

            _phase1Handler.IncludeHost = includeHost;
            _phase1Handler.LinkInstIds = linkInstIds;

            _phase1Handler.OnTypesLoaded = results =>
            {
                _filterDispatcher?.BeginInvoke((Action)(() =>
                {
                    _scanning     = false;
                    _scanDone     = true;
                    _ceilingTypes = results ?? new List<CeilingTypeEntry>();

                    if (_ceilingTypes.Count == 0)
                    {
                        ShowFilterMessage(AppStrings.T("ceilings.makeGrids.labels.noTypes"));
                        OnValidationChanged();
                        return;
                    }

                    PopulateFilterPanel();
                    OnValidationChanged();
                }));
            };

            _phase1Handler.OnError = err =>
            {
                _filterDispatcher?.BeginInvoke((Action)(() =>
                {
                    _scanning = false;
                    ShowFilterMessage(AppStrings.T("ceilings.makeGrids.labels.scanError", err));
                }));
            };

            _phase1Event.Raise();
        }

        // Builds the model-tabbed include/exclude list. One tab per source model
        // (host + each link that has ceiling types); items are this model's
        // "{Family}  —  {Type}" rows. Checked = included. Selection is by display
        // string, so the same family+type checked in one model applies everywhere —
        // which matches the name-based hide filter the run handler creates.
        private void PopulateFilterPanel()
        {
            if (_filterContainer == null) return;
            _filterContainer.Children.Clear();

            _displayToKey.Clear();
            _allTypeKeys.Clear();

            // Group types by source, preserving the scan order (host first via the
            // command's doc collection). Build the display→key map and key set.
            var groups   = new Dictionary<string, List<string>>();
            var groupOrder = new List<string>();
            foreach (var t in _ceilingTypes
                .OrderBy(t => t.FamilyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.TypeName,   StringComparer.OrdinalIgnoreCase))
            {
                string display = AppStrings.T("ceilings.makeGrids.labels.typeDisplay", t.FamilyName, t.TypeName);
                string key      = $"{t.FamilyName}|{t.TypeName}";

                _displayToKey[display] = key;
                _allTypeKeys.Add(key);

                if (!groups.TryGetValue(t.Source, out var list))
                {
                    list = new List<string>();
                    groups[t.Source] = list;
                    groupOrder.Add(t.Source);
                }
                if (!list.Contains(display)) list.Add(display);
            }

            // Drop any stale exclusions that no longer correspond to a scanned type.
            _excludedTypeKeys.IntersectWith(_allTypeKeys);

            // Ordered groups (host source label sorts naturally; keep scan order).
            var orderedGroups = new Dictionary<string, List<string>>();
            foreach (var src in groupOrder) orderedGroups[src] = groups[src];

            // Initial selection = every display whose key is NOT excluded (i.e. included).
            var initialSelected = _displayToKey
                .Where(kv => !_excludedTypeKeys.Contains(kv.Value))
                .Select(kv => kv.Key)
                .ToList();

            var tabs = new MultiSelectTabs { AccessibleName = AppStrings.T("ceilings.makeGrids.labels.typesByModel") };
            // Subscribe BEFORE SetGroups — SetGroups fires SelectionChanged once at the
            // end of setup, which is what initialises our excluded-key mirror.
            tabs.SelectionChanged += selected =>
            {
                var includedKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var d in selected)
                    if (_displayToKey.TryGetValue(d, out var k)) includedKeys.Add(k);

                _excludedTypeKeys = new HashSet<string>(
                    _allTypeKeys.Where(k => !includedKeys.Contains(k)), StringComparer.Ordinal);
                OnValidationChanged();
            };
            tabs.SetGroups(orderedGroups, initialSelected);

            _filterContainer.Children.Add(tabs);
        }

        // ── Step 3: Export Location ───────────────────────────────────────
        private FrameworkElement BuildExportStep()
        {
            var outer = new StackPanel();

            var folderLabel = new TextBlock { Text = AppStrings.T("ceilings.makeGrids.labels.outputFolder"), Margin = new Thickness(0, 0, 0, 4) };
            folderLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            folderLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            folderLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(folderLabel);

            var pathBox = new WpfTextBox
            {
                Text            = _outputFolder,
                Padding         = new Thickness(8, 4, 8, 4),
                BorderThickness = new Thickness(1),
            };
            pathBox.SetResourceReference(WpfTextBox.MinHeightProperty,   "LemoineH_Input");
            pathBox.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            pathBox.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            pathBox.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorderMid");
            pathBox.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineMonoFont");
            pathBox.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_MD");
            pathBox.TextChanged += (s, e) => { _outputFolder = pathBox.Text; OnValidationChanged(); };
            outer.Children.Add(pathBox);

            var browseBtn = ControlStyles.BuildButton(AppStrings.T("ceilings.makeGrids.labels.browse"));
            browseBtn.Margin = new Thickness(0, 4, 0, 0);
            browseBtn.Click += (s, e) =>
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description         = AppStrings.T("ceilings.makeGrids.labels.browseDialog"),
                    SelectedPath        = _outputFolder,
                    ShowNewFolderButton = true,
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    pathBox.Text  = dlg.SelectedPath;
                    _outputFolder = dlg.SelectedPath;
                    OnValidationChanged();
                }
            };
            outer.Children.Add(browseBtn);

            // Split by level toggle
            var toggleItems = new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id        = "subfolder",
                    Label     = AppStrings.T("ceilings.makeGrids.labels.subfolderLabel"),
                    Desc      = AppStrings.T("ceilings.makeGrids.labels.subfolderDesc"),
                    DefaultOn = _useCeilingGridsSubfolder,
                },
            };
            var toggleSwitch = new ToggleSwitches();
            toggleSwitch.Margin = new Thickness(0, 14, 0, 0);
            toggleSwitch.SetItems(toggleItems);
            toggleSwitch.StateChanged += state =>
            {
                if (state.TryGetValue("subfolder", out bool on))
                    _useCeilingGridsSubfolder = on;
            };
            outer.Children.Add(toggleSwitch);

            return outer;
        }

        // ── Step 5: Review & Run ───────────────────────────────────────────
        // ── IReviewableTool (P3) — framework renders the review step ───────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("types",     AppStrings.T("ceilings.makeGrids.review.itemTypes")),
            ("folder",    AppStrings.T("ceilings.makeGrids.review.itemFolder")),
            ("subfolder", AppStrings.T("ceilings.makeGrids.review.itemSubfolder")),
            ("dwg",       AppStrings.T("ceilings.makeGrids.review.itemDwg")),
            ("docs",      AppStrings.T("ceilings.makeGrids.review.itemDocs")),
        };

        public IDictionary<string, string> ReviewValues
        {
            get
            {
                int total    = DistinctTypeCount();
                int excluded = _excludedTypeKeys.Count;
                return new Dictionary<string, string>
                {
                    ["types"]     = total == 0 ? AppStrings.T("ceilings.makeGrids.review.typesPending") : AppStrings.T("ceilings.makeGrids.review.typesIncluded", total - excluded, total),
                    ["folder"]    = string.IsNullOrEmpty(_outputFolder) ? "—"
                        : (_outputFolder.Length > 40 ? "…" + _outputFolder.Substring(_outputFolder.Length - 37) : _outputFolder),
                    ["subfolder"] = _useCeilingGridsSubfolder ? AppStrings.T("ceilings.makeGrids.review.subfolderOn") : AppStrings.T("ceilings.makeGrids.review.subfolderOff"),
                    ["dwg"]       = AppStrings.T("ceilings.makeGrids.review.dwg"),
                    ["docs"]      = _selectedDocLabels.Count == 0 ? AppStrings.T("ceilings.makeGrids.review.docsNone")
                        : string.Join(", ", _selectedDocLabels.Take(2)) + (_selectedDocLabels.Count > 2 ? AppStrings.T("ceilings.makeGrids.review.docsMore", _selectedDocLabels.Count - 2) : ""),
                };
            }
        }

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => null;
        public string?        ReviewWarning => null;


        // ═════════════════════════════════════════════════════════════════════
        // IsValid
        // ═════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "docs")   return _selectedDocLabels.Count > 0;
            if (stepId == "export") return !string.IsNullOrWhiteSpace(_outputFolder);
            return true;
        }

        // ═════════════════════════════════════════════════════════════════════
        // SummaryFor
        // ═════════════════════════════════════════════════════════════════════
        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "docs":
                    return _selectedDocLabels.Count == 0 ? "—"
                        : AppStrings.T("ceilings.makeGrids.summaries.docsCount", _selectedDocLabels.Count);
                case "filter":
                    if (!_scanDone) return AppStrings.T("ceilings.makeGrids.summaries.scanPending");
                    int total    = DistinctTypeCount();
                    int excluded = _excludedTypeKeys.Count;
                    return excluded == 0 ? AppStrings.T("ceilings.makeGrids.summaries.allIncluded", total)
                        : AppStrings.T("ceilings.makeGrids.summaries.someIncluded", total - excluded, total);
                case "export":
                    return string.IsNullOrEmpty(_outputFolder) ? "—"
                        : System.IO.Path.GetFileName(_outputFolder.TrimEnd('\\', '/'));
                case "run":
                    return AppStrings.T("ceilings.makeGrids.summaries.run");
                default:
                    return "—";
            }
        }

        // Distinct ceiling types by Family+Type name across all scanned models.
        private int DistinctTypeCount()
            => _ceilingTypes
                .Select(t => $"{t.FamilyName}|{t.TypeName}")
                .Distinct(StringComparer.Ordinal)
                .Count();

        // ═════════════════════════════════════════════════════════════════════
        // Run
        // ═════════════════════════════════════════════════════════════════════
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_runHandler == null || _runEvent == null)
            {
                pushLog(AppStrings.T("ceilings.makeGrids.log.runHandlerMissing"), "fail");
                onComplete(0, 1, 0);
                return;
            }

            // Output folder/subfolder default is Settings-window-only now (see GlobalSettingsWindow.ToolGroups.cs).

            // Build excluded (Family, Type) name pairs — distinct across all models.
            var excludedTypeNames = _ceilingTypes
                .Select(t => (t.FamilyName, t.TypeName))
                .Distinct()
                .Where(p => _excludedTypeKeys.Contains($"{p.FamilyName}|{p.TypeName}"))
                .ToList();

            bool includeHost = false;
            var  linkInstIds = new List<ElementId>();
            foreach (var label in _selectedDocLabels)
            {
                if (!_docByLabel.TryGetValue(label, out var entry)) continue;
                if (entry.IsHost) includeHost = true;
                else              linkInstIds.Add(entry.LinkInstId);
            }

            _runHandler.IncludeHost             = includeHost;
            _runHandler.LinkInstIds             = linkInstIds;
            _runHandler.ExcludedTypeNames       = excludedTypeNames;
            _runHandler.OutputFolder            = _outputFolder;
            _runHandler.UseCeilingGridsSubfolder = _useCeilingGridsSubfolder;
            _runHandler.PushLog                 = pushLog;
            _runHandler.OnProgress    = onProgress;
            _runHandler.OnComplete    = onComplete;

            pushLog(AppStrings.T("ceilings.makeGrids.log.raising"), "info");
            _runEvent.Raise();
        }
    }
}
