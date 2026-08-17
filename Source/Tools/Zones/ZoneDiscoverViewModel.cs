using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

using WpfGrid       = System.Windows.Controls.Grid;
using WpfVisibility = System.Windows.Visibility;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace LemoineTools.Tools.Zones
{
    /// <summary>
    /// Zone Discover — reads a linked (usually architectural) model and proposes the
    /// building/level/area structure, so a zone library is a review rather than a typing
    /// exercise.
    ///
    /// Nothing is applied until the run: the scan only proposes, and every proposal is a
    /// tick box carrying what it would do and where it came from.
    /// </summary>
    public sealed class ZoneDiscoverViewModel : IStepFlowTool, IStepAware, IReviewableTool, IRunResult, IToolCleanup
    {
        public string? ResultNoun => "zones";
        public IReadOnlyList<ResultChip>? ResultChips => null;

        public string Title    => AppStrings.T("zones.discover.title");
        public string RunLabel => AppStrings.T("zones.discover.runLabel");

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", AppStrings.T("zones.discover.steps.S1"), required: true),
            new StepDefinition("S2", AppStrings.T("zones.discover.steps.S2"), required: true),
            new StepDefinition("S3", AppStrings.T("zones.discover.steps.S3"), required: false),
        };

        /// <summary>One selectable source document, captured on the main thread.</summary>
        public sealed class DocEntry
        {
            public string    Label      { get; set; } = "";
            public bool      IsHost     { get; set; }
            public ElementId LinkInstId { get; set; } = ElementId.InvalidElementId;
        }

        private readonly List<DocEntry> _docs;
        private readonly ZoneDiscoverScanHandler? _scanHandler;
        private readonly ExternalEvent?           _scanEvent;
        private readonly ZoneDiscoverRunHandler?  _runHandler;
        private readonly ExternalEvent?           _runEvent;

        private readonly HashSet<string> _selectedDocs = new HashSet<string>(StringComparer.Ordinal);

        private bool _discoverLevels      = true;
        private bool _discoverBuildings   = true;
        private bool _areasFromScopeBoxes = true;
        private bool _areasFromRooms;

        private ZoneDiscoverResult? _result;
        private bool _scanning;

        private Action<string>? _refreshStep;
        private StackPanel? _reviewPanel;

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public ZoneDiscoverViewModel(
            ZoneDiscoverScanHandler? scanHandler, ExternalEvent? scanEvent,
            ZoneDiscoverRunHandler?  runHandler,  ExternalEvent? runEvent,
            List<DocEntry>? docs)
        {
            _scanHandler = scanHandler;
            _scanEvent   = scanEvent;
            _runHandler  = runHandler;
            _runEvent    = runEvent;
            _docs        = docs ?? new List<DocEntry>();

            // Default to every link — the architectural model is the usual source and the user
            // is far more likely to deselect one than to hunt for the right one.
            foreach (var d in _docs) _selectedDocs.Add(d.Label);
        }

        public void SetContentRefreshCallback(Action<string> refresh) => _refreshStep = refresh;

        public void OnStepActivated(string stepId)
        {
            // Step content is built eagerly at window construction, so the review step must
            // rebuild itself here or it renders once, empty, and never updates.
            if (stepId == "S3") _refreshStep?.Invoke("S3");
        }

        public void OnWindowClosed()
        {
            if (_scanHandler != null)
            {
                _scanHandler.OnScanComplete = null;
                _scanHandler.OnError        = null;
            }
            if (_runHandler != null)
            {
                _runHandler.PushLog    = null;
                _runHandler.OnProgress = null;
                _runHandler.OnComplete = null;
            }
        }

        // ── Step content ──────────────────────────────────────────────────────
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildSourceStep();
                case "S2": return BuildOptionsStep();
                case "S3": return BuildReviewStep();
                default:   return null;
            }
        }

        private FrameworkElement BuildSourceStep()
        {
            var panel = new StackPanel();
            panel.Children.Add(Hint(AppStrings.T("zones.discover.sourceHint")));

            foreach (var d in _docs)
            {
                var cb = new CheckBox
                {
                    Content = d.Label + (d.IsHost ? "  " + AppStrings.T("zones.discover.hostSuffix") : ""),
                    IsChecked = _selectedDocs.Contains(d.Label),
                    Margin = new Thickness(0, 3, 0, 3),
                };
                cb.SetResourceReference(CheckBox.FontSizeProperty, "LemoineFS_SM");
                string label = d.Label;
                cb.Click += (s, e) =>
                {
                    if (cb.IsChecked == true) _selectedDocs.Add(label); else _selectedDocs.Remove(label);
                    Fire();
                };
                panel.Children.Add(cb);
            }

            if (_docs.Count == 0)
                panel.Children.Add(Hint(AppStrings.T("zones.discover.noSources")));

            return panel;
        }

        private FrameworkElement BuildOptionsStep()
        {
            var panel = new StackPanel();
            panel.Children.Add(Toggle(AppStrings.T("zones.discover.opt.levels"), _discoverLevels,
                v => { _discoverLevels = v; Fire(); }));
            panel.Children.Add(Toggle(AppStrings.T("zones.discover.opt.buildings"), _discoverBuildings,
                v => { _discoverBuildings = v; Fire(); }));
            panel.Children.Add(Toggle(AppStrings.T("zones.discover.opt.areasFromBoxes"), _areasFromScopeBoxes,
                v => { _areasFromScopeBoxes = v; Fire(); }));
            panel.Children.Add(Toggle(AppStrings.T("zones.discover.opt.areasFromRooms"), _areasFromRooms,
                v => { _areasFromRooms = v; Fire(); }));
            panel.Children.Add(Hint(AppStrings.T("zones.discover.optHint")));

            var scanBtn = new Button
            {
                Content = AppStrings.T("zones.discover.scan"),
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 4, 12, 4),
            };
            scanBtn.SetResourceReference(Button.FontSizeProperty, "LemoineFS_SM");
            scanBtn.Click += (s, e) => StartScan();
            panel.Children.Add(scanBtn);

            return panel;
        }

        private FrameworkElement BuildReviewStep()
        {
            _reviewPanel = new StackPanel();
            RenderReview();
            return _reviewPanel;
        }

        private void RenderReview()
        {
            if (_reviewPanel == null) return;
            _reviewPanel.Children.Clear();

            if (_scanning)
            {
                _reviewPanel.Children.Add(Hint(AppStrings.T("zones.discover.scanning")));
                return;
            }
            if (_result == null)
            {
                _reviewPanel.Children.Add(Hint(AppStrings.T("zones.discover.notScanned")));
                return;
            }

            AddProposalGroup(AppStrings.T("zones.discover.group.buildings"), _result.Buildings.Cast<ZoneProposal>().ToList());
            AddProposalGroup(AppStrings.T("zones.discover.group.levels"),    _result.Levels.Cast<ZoneProposal>().ToList());
            AddProposalGroup(AppStrings.T("zones.discover.group.areas"),     _result.Areas.Cast<ZoneProposal>().ToList());

            foreach (var note in _result.Notes)
                _reviewPanel.Children.Add(Warn(note));

            if (_result.TotalProposals == 0)
                _reviewPanel.Children.Add(Warn(AppStrings.T("zones.discover.nothingFound")));
        }

        private void AddProposalGroup(string title, List<ZoneProposal> items)
        {
            if (items.Count == 0) return;

            var header = new TextBlock
            {
                Text = $"{title} ({items.Count})",
                Margin = new Thickness(0, 8, 0, 4),
                FontWeight = FontWeights.SemiBold,
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            _reviewPanel!.Children.Add(header);

            foreach (var p in items)
            {
                var g = new WpfGrid { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var cb = new CheckBox { IsChecked = p.Accepted, VerticalAlignment = VerticalAlignment.Center };
                var prop = p;
                cb.Click += (s, e) => { prop.Accepted = cb.IsChecked == true; Fire(); };
                WpfGrid.SetColumn(cb, 0);
                g.Children.Add(cb);

                var name = new TextBlock
                {
                    Text = p.Label,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                WpfGrid.SetColumn(name, 1);
                g.Children.Add(name);

                string actionLabel =
                    p.Action == ZoneProposalAction.Add       ? AppStrings.T("zones.discover.action.add") :
                    p.Action == ZoneProposalAction.Update    ? AppStrings.T("zones.discover.action.update") :
                                                               AppStrings.T("zones.discover.action.unchanged");

                var meta = new TextBlock
                {
                    Text = $"{actionLabel} · {p.Provenance}",
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                meta.SetResourceReference(TextBlock.ForegroundProperty,
                    p.Action == ZoneProposalAction.Update ? "LemoineRed" : "LemoineTextDim");
                meta.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
                WpfGrid.SetColumn(meta, 2);
                g.Children.Add(meta);

                _reviewPanel!.Children.Add(g);
            }
        }

        // ── Scan ──────────────────────────────────────────────────────────────
        private void StartScan()
        {
            if (_scanHandler == null || _scanEvent == null) return;

            _scanning = true;
            _result   = null;
            RenderReview();
            Fire();

            _scanHandler.IncludeHost = _docs.Any(d => d.IsHost && _selectedDocs.Contains(d.Label));
            _scanHandler.LinkInstIds = _docs
                .Where(d => !d.IsHost && _selectedDocs.Contains(d.Label))
                .Select(d => d.LinkInstId).ToList();

            _scanHandler.DiscoverLevels        = _discoverLevels;
            _scanHandler.DiscoverBuildings     = _discoverBuildings;
            _scanHandler.AreasFromScopeBoxes   = _areasFromScopeBoxes;
            _scanHandler.AreasFromRoomClusters = _areasFromRooms;

            _scanHandler.OnScanComplete = r =>
            {
                _scanning = false;
                _result   = r;
                RenderReview();
                _refreshStep?.Invoke("S3");
                Fire();
            };
            _scanHandler.OnError = msg =>
            {
                _scanning = false;
                _result   = null;
                RenderReview();
                DiagnosticsLog.Warn("ZoneDiscover", $"Scan failed: {msg}");
                Fire();
            };

            _scanEvent.Raise();
        }

        // ── Contract ──────────────────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _selectedDocs.Count > 0;
                case "S2": return _discoverLevels || _discoverBuildings || _areasFromScopeBoxes || _areasFromRooms;
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "S1":
                    return AppStrings.T("zones.discover.summary.sources", _selectedDocs.Count);
                case "S2":
                {
                    var parts = new List<string>();
                    if (_discoverLevels)      parts.Add(AppStrings.T("zones.discover.opt.levels"));
                    if (_discoverBuildings)   parts.Add(AppStrings.T("zones.discover.opt.buildings"));
                    if (_areasFromScopeBoxes) parts.Add(AppStrings.T("zones.discover.opt.areasFromBoxes"));
                    if (_areasFromRooms)      parts.Add(AppStrings.T("zones.discover.opt.areasFromRooms"));
                    return string.Join(", ", parts);
                }
                default:
                    return _result == null
                        ? AppStrings.T("zones.discover.notScanned")
                        : AppStrings.T("zones.discover.summary.review",
                                       _result.AcceptedCount, _result.TotalProposals);
            }
        }

        // ── IReviewableTool ───────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems => new List<(string, string)>
        {
            ("sources", AppStrings.T("zones.discover.steps.S1")),
            ("what",    AppStrings.T("zones.discover.steps.S2")),
            ("review",  AppStrings.T("zones.discover.steps.S3")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["sources"] = AppStrings.T("zones.discover.summary.sources", _selectedDocs.Count),
            ["what"]    = SummaryFor("S2"),
            ["review"]  = SummaryFor("S3"),
        };

        public IList<string>? ReviewChips => null;

        /// <summary>
        /// Says plainly that nothing is deleted. A discover pass that could remove
        /// hand-authored zones would be the thing to fear here, so the review states it.
        /// </summary>
        public string? ReviewNote => AppStrings.T("zones.discover.reviewNote");

        /// <summary>
        /// Re-adopting an area's extents moves where its views land on a sheet, so an accepted
        /// Update is the one proposal worth stopping on.
        /// </summary>
        public string? ReviewWarning
        {
            get
            {
                if (_result == null) return null;
                int updates = _result.Areas.Count(a => a.Accepted && a.Action == ZoneProposalAction.Update);
                return updates > 0 ? AppStrings.T("zones.discover.updateWarning", updates) : null;
            }
        }

        public void Run(Action<string, string> pushLog,
                        Action<int, int, int, int> onProgress,
                        Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null)
            {
                pushLog(AppStrings.T("zones.discover.noHandler"), "fail");
                onComplete(0, 1, 0);
                return;
            }
            if (_result == null || _result.AcceptedCount == 0)
            {
                pushLog(AppStrings.T("zones.discover.nothingAccepted"), "warn");
                onComplete(0, 0, 0);
                return;
            }

            _runHandler.Result     = _result;
            _runHandler.PushLog    = pushLog;
            _runHandler.OnProgress = onProgress;
            _runHandler.OnComplete = onComplete;
            _runEvent.Raise();
        }

        // ── Small builders ────────────────────────────────────────────────────
        private static TextBlock Hint(string text)
        {
            var t = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 6),
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }

        private static TextBlock Warn(string text)
        {
            var t = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 2),
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            return t;
        }

        private static CheckBox Toggle(string label, bool value, Action<bool> onChange)
        {
            var cb = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 3, 0, 3) };
            cb.SetResourceReference(CheckBox.FontSizeProperty, "LemoineFS_SM");
            cb.Click += (s, e) => onChange(cb.IsChecked == true);
            return cb;
        }
    }
}
