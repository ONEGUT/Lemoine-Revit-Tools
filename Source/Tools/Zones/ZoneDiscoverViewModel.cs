using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
    ///
    /// FOUR steps, in the house shape:
    ///
    ///   S1 Source model    which documents to read
    ///   S2 Discover        what to look for, and the Scan button
    ///   S3 Results         what the scan found — keep or drop each proposal
    ///   S4 Confirm         the review summary and Run
    ///
    /// Pressing Scan on S2 ADVANCES to S3 (via IStepNavigable) once the results are in, so
    /// the button visibly leads somewhere rather than appearing to do nothing.
    ///
    /// THREADING — the reason this tool did nothing at all before:
    ///
    ///   ZoneDiscoverScanHandler.Execute runs on REVIT'S MAIN THREAD, but every element this
    ///   ViewModel builds belongs to the tool window's own dedicated STA thread. Wiring
    ///   OnScanComplete straight to a method that touches those elements threw
    ///   InvalidOperationException inside the ExternalEvent, where Revit discards it — no
    ///   proposals, no error, no log line. StepFlowWindow marshals pushLog/onProgress/
    ///   onComplete for us, but NOT handler callbacks a ViewModel wires itself. So both
    ///   callbacks below are marshalled explicitly through the window's dispatcher.
    /// </summary>
    public sealed class ZoneDiscoverViewModel
        : IStepFlowTool, IStepAware, IStepNavigable, IReviewableTool, IRunResult, IToolCleanup
    {
        public string? ResultNoun => "zones";
        public IReadOnlyList<ResultChip>? ResultChips => null;

        public string Title    => AppStrings.T("zones.discover.title");
        public string RunLabel => AppStrings.T("zones.discover.runLabel");

        // Step ids. Logic tokens, deliberately not externalized.
        private const string StepSource  = "S1";
        private const string StepWhat    = "S2";
        private const string StepResults = "S3";
        private const string StepConfirm = "S4";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition(StepSource,  AppStrings.T("zones.discover.steps.S1"), required: true),
            new StepDefinition(StepWhat,    AppStrings.T("zones.discover.steps.S2"), required: true),
            new StepDefinition(StepResults, AppStrings.T("zones.discover.steps.S3"), required: false),
            new StepDefinition(StepConfirm, AppStrings.T("zones.discover.steps.S4"), required: false),
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
        private bool   _scanning;
        /// <summary>Last scan failure, shown on S3. Null when the scan has not failed.</summary>
        private string? _scanError;

        private Action<string>? _refreshStep;
        private StackPanel? _resultsPanel;

        /// <summary>
        /// The tool window's dispatcher. Captured lazily in GetStepContent, which runs on the
        /// window's STA thread — the constructor does NOT, it runs on Revit's main thread inside
        /// the launching command, so capturing there would grab the wrong dispatcher entirely.
        /// </summary>
        private Dispatcher? _wpfDispatcher;

        public event EventHandler? ValidationChanged;
        private void Fire() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public event EventHandler<int>? NavigateRequested;

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
            // Step content is built eagerly at window construction, so any step that reads an
            // earlier step's state must rebuild itself here or it renders once, stale, forever.
            if (stepId == StepResults || stepId == StepConfirm) _refreshStep?.Invoke(stepId);
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
            _wpfDispatcher = null;
        }

        // ── Step content ──────────────────────────────────────────────────────
        public FrameworkElement? GetStepContent(string stepId)
        {
            // First call happens on the window's STA thread during construction. This is the
            // only correct moment to capture the dispatcher the handler callbacks marshal to.
            if (_wpfDispatcher == null) _wpfDispatcher = Dispatcher.CurrentDispatcher;

            switch (stepId)
            {
                case StepSource:  return BuildSourceStep();
                case StepWhat:    return BuildWhatStep();
                case StepResults: return BuildResultsStep();
                case StepConfirm: return BuildConfirmStep();
                default:          return null;
            }
        }

        // ── S1 — Source model ─────────────────────────────────────────────────

        /// <summary>
        /// The same card list Discover Rules uses to pick links, minus the trade column — Zone
        /// Discover has no trade concept, and that is the only difference between the two.
        /// </summary>
        private FrameworkElement BuildSourceStep()
        {
            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 320,
            };
            ControlStyles.WireBubblingScroll(sv);

            var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            sp.Children.Add(Hint(AppStrings.T("zones.discover.sourceHint")));

            if (_docs.Count == 0)
            {
                sp.Children.Add(new WarnBanner(AppStrings.T("zones.discover.noSources")));
            }
            else
            {
                foreach (var d in _docs) sp.Children.Add(BuildSourceRow(d));
            }

            sv.Content = sp;
            return sv;
        }

        private FrameworkElement BuildSourceRow(DocEntry doc)
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Margin          = new Thickness(0, 0, 0, 4),
                Padding         = new Thickness(8, 6, 8, 6),
            };
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");

            var g = new WpfGrid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cb = new CheckBox
            {
                IsChecked         = _selectedDocs.Contains(doc.Label),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 8, 0),
            };
            string label = doc.Label;
            cb.Checked   += (s, e) => { _selectedDocs.Add(label);    Fire(); };
            cb.Unchecked += (s, e) => { _selectedDocs.Remove(label); Fire(); };

            var lbl = new TextBlock
            {
                Text              = doc.Label,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            WpfGrid.SetColumn(cb,  0);
            WpfGrid.SetColumn(lbl, 1);
            g.Children.Add(cb);
            g.Children.Add(lbl);

            // The host document is tagged rather than renamed, so its real title still reads.
            if (doc.IsHost)
            {
                var tag = Chip(AppStrings.T("zones.discover.hostSuffix"));
                WpfGrid.SetColumn(tag, 2);
                g.Children.Add(tag);
            }

            card.Child = g;
            return card;
        }

        // ── S2 — Discover ─────────────────────────────────────────────────────
        private FrameworkElement BuildWhatStep()
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

        // ── S3 — Results ──────────────────────────────────────────────────────
        private FrameworkElement BuildResultsStep()
        {
            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 360,
            };
            ControlStyles.WireBubblingScroll(sv);

            _resultsPanel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            RenderResults();
            sv.Content = _resultsPanel;
            return sv;
        }

        private void RenderResults()
        {
            if (_resultsPanel == null) return;
            _resultsPanel.Children.Clear();

            if (_scanning)
            {
                _resultsPanel.Children.Add(Hint(AppStrings.T("zones.discover.scanning")));
                return;
            }

            // A failed scan used to reach DiagnosticsLog only, so the user saw an empty step and
            // no reason for it. It is stated here, in the step where the results should be.
            if (_scanError != null)
            {
                _resultsPanel.Children.Add(new WarnBanner(
                    AppStrings.T("zones.discover.scanFailed", _scanError)));
                return;
            }

            if (_result == null)
            {
                _resultsPanel.Children.Add(Hint(AppStrings.T("zones.discover.notScanned")));
                return;
            }

            _resultsPanel.Children.Add(Hint(AppStrings.T("zones.discover.resultsHint")));

            AddProposalGroup(AppStrings.T("zones.discover.group.buildings"), _result.Buildings.Cast<ZoneProposal>().ToList());
            AddProposalGroup(AppStrings.T("zones.discover.group.levels"),    _result.Levels.Cast<ZoneProposal>().ToList());
            AddProposalGroup(AppStrings.T("zones.discover.group.areas"),     _result.Areas.Cast<ZoneProposal>().ToList());

            // Every "Found 0 ..." note the collector produced is surfaced here — a silent empty
            // result is indistinguishable from a broken collector.
            foreach (var note in _result.Notes)
                _resultsPanel.Children.Add(Warn(note));

            if (_result.TotalProposals == 0)
                _resultsPanel.Children.Add(Warn(AppStrings.T("zones.discover.nothingFound")));
        }

        private void AddProposalGroup(string title, List<ZoneProposal> items)
        {
            if (items.Count == 0) return;

            var headerRow = new WpfGrid { Margin = new Thickness(0, 10, 0, 4) };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var header = new TextBlock
            {
                Text = $"{title} ({items.Count})",
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            header.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            WpfGrid.SetColumn(header, 0);
            headerRow.Children.Add(header);

            // Keep-all / drop-all for the group, so a 60-level scan is not 60 clicks.
            var all = new CheckBox
            {
                Content = AppStrings.T("zones.discover.keepAll"),
                IsChecked = items.All(p => p.Accepted),
                VerticalAlignment = VerticalAlignment.Center,
            };
            all.SetResourceReference(CheckBox.FontSizeProperty, "LemoineFS_SM");
            var groupItems = items;
            all.Click += (s, e) =>
            {
                bool on = all.IsChecked == true;
                foreach (var p in groupItems) p.Accepted = on;
                RenderResults();
                Fire();
            };
            WpfGrid.SetColumn(all, 1);
            headerRow.Children.Add(all);

            _resultsPanel!.Children.Add(headerRow);

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

                _resultsPanel!.Children.Add(g);
            }
        }

        // ── S4 — Confirm ──────────────────────────────────────────────────────
        private FrameworkElement BuildConfirmStep()
        {
            var panel = new StackPanel();

            if (_result == null)
            {
                panel.Children.Add(new WarnBanner(AppStrings.T("zones.discover.notScanned")));
                return panel;
            }
            if (_result.AcceptedCount == 0)
            {
                panel.Children.Add(new WarnBanner(AppStrings.T("zones.discover.nothingAccepted")));
                return panel;
            }

            panel.Children.Add(Hint(AppStrings.T("zones.discover.summary.review",
                                                 _result.AcceptedCount, _result.TotalProposals)));

            AddConfirmLine(panel, AppStrings.T("zones.discover.group.buildings"),
                           _result.Buildings.Count(b => b.Accepted));
            AddConfirmLine(panel, AppStrings.T("zones.discover.group.levels"),
                           _result.Levels.Count(l => l.Accepted));
            AddConfirmLine(panel, AppStrings.T("zones.discover.group.areas"),
                           _result.Areas.Count(a => a.Accepted));

            return panel;
        }

        private void AddConfirmLine(StackPanel panel, string label, int count)
        {
            if (count == 0) return;
            var t = new TextBlock { Text = $"{label} · {count}", Margin = new Thickness(0, 2, 0, 2) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            panel.Children.Add(t);
        }

        // ── Scan ──────────────────────────────────────────────────────────────
        private void StartScan()
        {
            if (_scanHandler == null || _scanEvent == null)
            {
                _scanError = AppStrings.T("zones.discover.noHandler");
                RenderResults();
                GoToResults();
                return;
            }

            _scanning  = true;
            _result    = null;
            _scanError = null;
            RenderResults();
            Fire();

            _scanHandler.IncludeHost = _docs.Any(d => d.IsHost && _selectedDocs.Contains(d.Label));
            _scanHandler.LinkInstIds = _docs
                .Where(d => !d.IsHost && _selectedDocs.Contains(d.Label))
                .Select(d => d.LinkInstId).ToList();

            _scanHandler.DiscoverLevels        = _discoverLevels;
            _scanHandler.DiscoverBuildings     = _discoverBuildings;
            _scanHandler.AreasFromScopeBoxes   = _areasFromScopeBoxes;
            _scanHandler.AreasFromRoomClusters = _areasFromRooms;

            // Both callbacks fire on REVIT'S MAIN THREAD. Everything they touch lives on this
            // window's STA thread, so both must be marshalled — doing this inline was the bug
            // that made the whole tool look like it never scanned.
            _scanHandler.OnScanComplete = r => OnUiThread(() =>
            {
                _scanning  = false;
                _scanError = null;
                _result    = r;
                RenderResults();
                _refreshStep?.Invoke(StepResults);
                Fire();
                GoToResults();
            });

            _scanHandler.OnError = msg => OnUiThread(() =>
            {
                _scanning  = false;
                _result    = null;
                _scanError = string.IsNullOrWhiteSpace(msg)
                    ? AppStrings.T("zones.discover.scanFailedUnknown")
                    : msg;
                DiagnosticsLog.Warn("ZoneDiscover", $"Scan failed: {msg}");
                RenderResults();
                _refreshStep?.Invoke(StepResults);
                Fire();
                GoToResults();
            });

            _scanEvent.Raise();
        }

        /// <summary>
        /// Marshals onto the tool window's dispatcher, non-blocking and shutdown-guarded.
        /// Blocking Invoke could deadlock against Revit's main thread, and a BeginInvoke onto a
        /// dispatcher that has already shut down throws on the CALLING thread — which here is
        /// Revit's, so it would take Revit with it.
        /// </summary>
        private void OnUiThread(Action action)
        {
            var d = _wpfDispatcher;
            if (d == null)
            {
                // No window thread to marshal to (the step content was never built). Running
                // inline is safe in that case and better than dropping the result silently.
                try { action(); }
                catch (Exception ex) { DiagnosticsLog.Error("ZoneDiscover: scan callback", ex); }
                return;
            }
            if (d.HasShutdownStarted || d.HasShutdownFinished) return;

            try
            {
                d.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    try { action(); }
                    catch (Exception ex) { DiagnosticsLog.Error("ZoneDiscover: scan callback", ex); }
                }));
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ZoneDiscover: marshal scan callback", ex);
            }
        }

        /// <summary>Moves the accordion to the Results step, so Scan visibly leads somewhere.</summary>
        private void GoToResults()
        {
            int idx = Array.FindIndex(Steps, s => s.Id == StepResults);
            if (idx >= 0) NavigateRequested?.Invoke(this, idx);
        }

        // ── Contract ──────────────────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case StepSource: return _selectedDocs.Count > 0;
                case StepWhat:   return _discoverLevels || _discoverBuildings ||
                                        _areasFromScopeBoxes || _areasFromRooms;
                default:         return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case StepSource:
                    return AppStrings.T("zones.discover.summary.sources", _selectedDocs.Count);
                case StepWhat:
                {
                    var parts = new List<string>();
                    if (_discoverLevels)      parts.Add(AppStrings.T("zones.discover.opt.levels"));
                    if (_discoverBuildings)   parts.Add(AppStrings.T("zones.discover.opt.buildings"));
                    if (_areasFromScopeBoxes) parts.Add(AppStrings.T("zones.discover.opt.areasFromBoxes"));
                    if (_areasFromRooms)      parts.Add(AppStrings.T("zones.discover.opt.areasFromRooms"));
                    return string.Join(", ", parts);
                }
                default:
                    if (_scanError != null) return AppStrings.T("zones.discover.scanFailed", _scanError);
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
            ("results", AppStrings.T("zones.discover.steps.S3")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["sources"] = AppStrings.T("zones.discover.summary.sources", _selectedDocs.Count),
            ["what"]    = SummaryFor(StepWhat),
            ["results"] = SummaryFor(StepResults),
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

        private static Border Chip(string text)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 1, 7, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
            };
            b.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var t = new TextBlock { Text = text, Background = Brushes.Transparent };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            b.Child = t;
            return b;
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
