using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace LemoineTools.Tools.UpgradeLinks
{
    /// <summary>
    /// Upgrade &amp; Link Models — queue Revit files from any folder, choose each one's link placement,
    /// pick where the upgraded copies are saved, then run: each file is opened (which upgrades it),
    /// saved to the destination, closed, and linked into the host. Files are processed serially on the
    /// Revit thread for RAM control (see <see cref="UpgradeLinksRunHandler"/>).
    /// </summary>
    public sealed class UpgradeLinksViewModel : ILemoineTool, ILemoineReviewable, ILemoineRunResult, IStepAware, ILemoineToolCleanup, ILemoineRunPausable
    {
        public string Title       => LemoineStrings.T("upgradeLinks.title");
        public string RunLabel    => LemoineStrings.T("upgradeLinks.runLabel");
        public string? ResultNoun => LemoineStrings.T("upgradeLinks.resultNoun");
        public IReadOnlyList<ResultChip>? ResultChips => null;

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("files", LemoineStrings.T("upgradeLinks.steps.files"), required: true),
            new StepDefinition("dest",  LemoineStrings.T("upgradeLinks.steps.dest"),  required: true),
            new StepDefinition("run",   LemoineStrings.T("upgradeLinks.steps.run"),   required: false),
        };

        // ── Injected ───────────────────────────────────────────────────────────
        private readonly UpgradeLinksScanHandler? _scanHandler;
        private readonly ExternalEvent?           _scanEvent;
        private readonly UpgradeLinksRunHandler?  _runHandler;
        private readonly ExternalEvent?           _runEvent;
        private readonly string?                  _hostFolder;   // null when the host has no local folder (or is cloud)
        private readonly bool                     _hostIsCloud;  // host is a cloud model — offers the Cloud destination

        // ── State ────────────────────────────────────────────────────────────────
        private readonly List<UpgradeFileRow> _rows = new List<UpgradeFileRow>();
        private UpgradeDestination _dest = UpgradeLinksSettings.Instance.Destination;
        // Absolute folder path for the SelectedFolder destination. Defaults to the host's own
        // folder when there is one, else the last folder the user picked (settings), else empty.
        private string             _selectedFolder = "";
        private bool               _audit   = UpgradeLinksSettings.Instance.AuditOnOpen;
        private bool               _reload  = UpgradeLinksSettings.Instance.ReloadExisting;
        private readonly UpgradePlacement _defaultPlacement = UpgradeLinksSettings.Instance.DefaultPlacement;
        private bool               _scanning;

        // Live UI handles
        private StackPanel? _filesContainer, _destContainer;
        private Dispatcher? _disp;
        private Action<string>? _refreshStep;

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── ILemoineRunPausable — Cloud mode pauses per file on Revit's native Save dialog ──────
        public event Action<bool, string?, string?>? AwaitingUserChanged;
        public void ContinueRun()
        {
            if (_runHandler == null || _runEvent == null) return;
            _runHandler.CloudContinueRequested = true;
            _runEvent.Raise();
        }
        public void SkipCurrentItem()
        {
            if (_runHandler == null || _runEvent == null) return;
            _runHandler.CloudSkipRequested = true;
            _runEvent.Raise();
        }

        public UpgradeLinksViewModel(
            UpgradeLinksScanHandler? scanHandler, ExternalEvent? scanEvent,
            UpgradeLinksRunHandler?  runHandler,  ExternalEvent?  runEvent,
            string? hostFolder, bool hostIsCloud)
        {
            _scanHandler = scanHandler; _scanEvent = scanEvent;
            _runHandler  = runHandler;  _runEvent  = runEvent;
            _hostFolder  = hostFolder;  _hostIsCloud = hostIsCloud;

            _selectedFolder = !string.IsNullOrEmpty(_hostFolder) ? _hostFolder! : UpgradeLinksSettings.Instance.LastSelectedFolder;

            if (_dest == UpgradeDestination.Cloud && !_hostIsCloud) _dest = UpgradeDestination.SelectedFolder;
        }

        public void OnWindowClosed()
        {
            if (_scanHandler != null) { _scanHandler.OnScanned = null; _scanHandler.OnError = null; }
            if (_runHandler  != null)
            {
                _runHandler.PushLog = null; _runHandler.OnProgress = null; _runHandler.OnComplete = null;
                _runHandler.OnAwaitingUser = null;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "files": return BuildFilesStep();
                case "dest":  return BuildDestStep();
                default:      return null;   // "run" rendered by the framework (ILemoineReviewable)
            }
        }

        // ── IStepAware ───────────────────────────────────────────────────────────
        public void SetContentRefreshCallback(Action<string> rebuildStepContent) => _refreshStep = rebuildStepContent;
        public void OnStepActivated(string stepId) { if (stepId == "files") _disp = Dispatcher.CurrentDispatcher; }

        // ══════════════════════ Step 1: Files & placement ══════════════════════
        private FrameworkElement BuildFilesStep()
        {
            _disp = Dispatcher.CurrentDispatcher;
            var outer = new StackPanel();
            outer.Children.Add(Dim(LemoineStrings.T("upgradeLinks.labels.filesHint")));
            _filesContainer = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            outer.Children.Add(_filesContainer);
            RebuildFilesTable();
            return outer;
        }

        private void RebuildFilesTable()
        {
            if (_filesContainer == null) return;
            _filesContainer.Children.Clear();

            if (_scanning) _filesContainer.Children.Add(Dim(LemoineStrings.T("upgradeLinks.labels.scanning")));

            if (_rows.Count == 0)
            {
                _filesContainer.Children.Add(Dim(LemoineStrings.T("upgradeLinks.labels.empty")));
            }
            else
            {
                var table = new StackPanel();
                table.Children.Add(BuildHeaderRow());
                foreach (var row in _rows) table.Children.Add(BuildFileRow(row));

                var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
                border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                border.Child = table;
                _filesContainer.Children.Add(border);

                int unreadable = _rows.Count(r => !r.Readable);
                if (unreadable > 0) _filesContainer.Children.Add(Warn(LemoineStrings.T("upgradeLinks.labels.unreadableNote", unreadable)));
            }

            // Add / Clear toolbar
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var add = LemoineControlStyles.BuildButton(LemoineStrings.T("upgradeLinks.labels.addFiles"), LemoineControlStyles.LemoineButtonVariant.Primary);
            add.Click += (s, e) => OnAddFiles();
            bar.Children.Add(add);
            if (_rows.Count > 0)
            {
                var clear = LemoineControlStyles.BuildSmallButton(LemoineStrings.T("upgradeLinks.labels.clearList"));
                clear.Margin = new Thickness(8, 0, 0, 0);
                clear.Click += (s, e) => { _rows.Clear(); RebuildFilesTable(); Changed(); };
                bar.Children.Add(clear);
            }
            _filesContainer.Children.Add(bar);

            if (_rows.Count > 0)
            {
                var setAllPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var lbl = Label(LemoineStrings.T("upgradeLinks.labels.setAll") + ":");
                lbl.VerticalAlignment = VerticalAlignment.Center;
                lbl.Margin = new Thickness(0, 0, 8, 0);
                setAllPanel.Children.Add(lbl);
                var setAll = new LemoineSingleSelect { Width = 180 };
                setAll.Items = PlacementLabels();
                setAll.SelectedItem = PlacementLabel(_defaultPlacement);
                setAll.SelectionChanged += lblSel =>
                {
                    if (lblSel != null && LabelToPlacement.TryGetValue(lblSel, out var p))
                    {
                        foreach (var r in _rows) r.Placement = p;
                        RebuildFilesTable();
                        Changed();
                    }
                };
                setAllPanel.Children.Add(setAll);
                _filesContainer.Children.Add(setAllPanel);

                var toggles = new LemoineToggleSwitches { Margin = new Thickness(0, 12, 0, 0) };
                toggles.SetItems(new List<ToggleItem>
                {
                    new ToggleItem { Id = "audit",  Label = LemoineStrings.T("upgradeLinks.labels.auditLabel"),  Desc = LemoineStrings.T("upgradeLinks.labels.auditDesc"),  DefaultOn = _audit },
                    new ToggleItem { Id = "reload", Label = LemoineStrings.T("upgradeLinks.labels.reloadLabel"), Desc = LemoineStrings.T("upgradeLinks.labels.reloadDesc"), DefaultOn = _reload },
                });
                toggles.StateChanged += st =>
                {
                    if (st.TryGetValue("audit",  out var a)) _audit  = a;
                    if (st.TryGetValue("reload", out var b)) _reload = b;
                };
                _filesContainer.Children.Add(toggles);
            }
        }

        private Grid BuildHeaderRow()
        {
            var g = FileRowGrid();
            g.SetResourceReference(Grid.BackgroundProperty, "LemoineRaised");
            var file = Dim(LemoineStrings.T("upgradeLinks.labels.colFile"));       Grid.SetColumn(file, 1);
            var ver  = Dim(LemoineStrings.T("upgradeLinks.labels.colVersion"));    Grid.SetColumn(ver, 2);
            var plc  = Dim(LemoineStrings.T("upgradeLinks.labels.colPlacement"));  Grid.SetColumn(plc, 3);
            g.Children.Add(file); g.Children.Add(ver); g.Children.Add(plc);
            return g;
        }

        private Grid BuildFileRow(UpgradeFileRow row)
        {
            var g = FileRowGrid();

            // Column 1 — editable "save as" name + source path
            var names = new StackPanel();
            Grid.SetColumn(names, 1);

            var nameGrid = new Grid();
            nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var nameBox = BuildInlineNameBox(row);
            Grid.SetColumn(nameBox, 0);
            var ext = new TextBlock { Text = ".rvt", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            ext.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            ext.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            ext.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            Grid.SetColumn(ext, 1);
            nameGrid.Children.Add(nameBox);
            nameGrid.Children.Add(ext);

            var path = new TextBlock { Text = row.Folder, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
            path.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            path.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            path.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            names.Children.Add(nameGrid); names.Children.Add(path);
            g.Children.Add(names);

            // Column 2 — version badge
            var badge = VersionBadge(row);
            Grid.SetColumn(badge, 2);
            g.Children.Add(badge);

            // Column 3 — placement picker
            var pick = new LemoineSingleSelect { IsEnabled = row.Readable };
            pick.Items = PlacementLabels();
            pick.SelectedItem = PlacementLabel(row.Placement);
            pick.SelectionChanged += lblSel =>
            {
                if (lblSel != null && LabelToPlacement.TryGetValue(lblSel, out var p)) { row.Placement = p; Changed(); }
            };
            Grid.SetColumn(pick, 3);
            g.Children.Add(pick);

            // Column 4 — remove
            var rm = LemoineControlStyles.BuildSmallButton(char.ConvertFromUtf32(0xE74D), LemoineControlStyles.LemoineButtonVariant.Danger); // Delete (trash)
            rm.FontFamily = new FontFamily("Segoe MDL2 Assets");   // glyph font — LemoineUiFont can't render MDL2 codepoints
            rm.VerticalAlignment = VerticalAlignment.Center;
            rm.Click += (s, e) => { _rows.Remove(row); RebuildFilesTable(); Changed(); };
            Grid.SetColumn(rm, 4);
            g.Children.Add(rm);

            return g;
        }

        private WpfTextBox BuildInlineNameBox(UpgradeFileRow row)
        {
            var tb = new WpfTextBox
            {
                Text = row.SaveAsName,
                IsEnabled = row.Readable,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 2, 4, 2),
            };
            tb.SetResourceReference(WpfTextBox.BackgroundProperty,     "LemoineSelectBg");
            tb.SetResourceReference(WpfTextBox.ForegroundProperty,     "LemoineText");
            tb.SetResourceReference(WpfTextBox.BorderBrushProperty,    "LemoineBorderMid");
            tb.SetResourceReference(WpfTextBox.CaretBrushProperty,     "LemoineText");
            tb.SetResourceReference(WpfTextBox.FontFamilyProperty,     "LemoineMonoFont");
            tb.SetResourceReference(WpfTextBox.FontSizeProperty,       "LemoineFS_SM");
            tb.SetResourceReference(WpfTextBox.SelectionBrushProperty, "LemoineAccent");
            tb.TextChanged += (s, e) => { row.SaveAsName = tb.Text; Changed(); };
            return tb;
        }

        private static Grid FileRowGrid()
        {
            var g = new Grid { Margin = new Thickness(12, 9, 12, 9) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });                     // 0 left inset
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 1 name + path
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });                   // 2 version badge
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });                   // 3 placement picker
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // 4 remove
            return g;
        }

        private FrameworkElement VersionBadge(UpgradeFileRow row)
        {
            string text; string colorKey;
            if (!row.Readable)      { text = LemoineStrings.T("upgradeLinks.labels.verUnreadable");           colorKey = "LemoineRed"; }
            else if (!row.Scanned)  { text = LemoineStrings.T("upgradeLinks.labels.verUnknown");              colorKey = "LemoineTextDim"; }
            else if (row.IsCurrent) { text = LemoineStrings.T("upgradeLinks.labels.verCurrent", row.Version); colorKey = "LemoineGreen"; }
            else                    { text = LemoineStrings.T("upgradeLinks.labels.verUpgrade", row.Version); colorKey = "LemoineAccent"; }

            var tb = new TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            var b = new Border { Padding = new Thickness(7, 2, 7, 2), CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Child = tb };
            b.SetResourceReference(Border.BorderBrushProperty, colorKey);
            b.BorderThickness = new Thickness(1);
            return b;
        }

        private void OnAddFiles()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter      = "Revit files (*.rvt)|*.rvt",
                Title       = LemoineStrings.T("upgradeLinks.title"),
            };
            bool? ok;
            try { ok = dlg.ShowDialog(); }
            catch (Exception ex) { LemoineLog.Swallowed("UpgradeLinks: open file dialog", ex); return; }
            if (ok != true || dlg.FileNames == null || dlg.FileNames.Length == 0) return;

            var existing = new HashSet<string>(_rows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            var added = new List<UpgradeFileRow>();
            foreach (var p in dlg.FileNames)
            {
                if (string.IsNullOrWhiteSpace(p) || !existing.Add(p)) continue;
                var row = new UpgradeFileRow
                {
                    Path = p,
                    Placement = _defaultPlacement,
                    SaveAsName = System.IO.Path.GetFileNameWithoutExtension(p),
                };
                _rows.Add(row); added.Add(row);
            }
            if (added.Count == 0) return;

            RebuildFilesTable();
            Changed();
            ScanNewRows(added);
        }

        private void ScanNewRows(List<UpgradeFileRow> rows)
        {
            if (_scanHandler == null || _scanEvent == null) return;
            _scanning = true;
            RebuildFilesTable();

            _scanHandler.Paths = rows.Select(r => r.Path).ToList();
            _scanHandler.OnScanned = results => _disp?.BeginInvoke((Action)(() =>
            {
                _scanning = false;
                if (results != null)
                {
                    var byPath = _rows.ToDictionary(r => r.Path, r => r, StringComparer.OrdinalIgnoreCase);
                    foreach (var res in results)
                    {
                        if (!byPath.TryGetValue(res.Path, out var row)) continue;
                        row.Scanned = true;
                        row.Readable = res.Readable;
                        row.Version = res.Version;
                        row.IsWorkshared = res.IsWorkshared;
                        row.IsCurrent = res.IsCurrent;
                    }
                }
                RebuildFilesTable();
                Changed();
            }));
            _scanHandler.OnError = err => _disp?.BeginInvoke((Action)(() =>
            {
                _scanning = false;
                LemoineLog.Warn("UpgradeLinks: scan error", err ?? "");
                RebuildFilesTable();
            }));
            _scanEvent.Raise();
        }

        // ══════════════════════ Step 2: Destination ════════════════════════════
        private FrameworkElement BuildDestStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(Label(LemoineStrings.T("upgradeLinks.labels.destQuestion")));
            outer.Children.Add(Dim(LemoineStrings.T("upgradeLinks.labels.destHint")));
            _destContainer = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            outer.Children.Add(_destContainer);
            RebuildDestCards();
            return outer;
        }

        // Top level: Local vs Cloud (Cloud hidden unless the host is itself a cloud model).
        // Local reveals two sub-choices: Selected folder / Current location.
        private void RebuildDestCards()
        {
            if (_destContainer == null) return;
            _destContainer.Children.Clear();

            bool localSelected = _dest != UpgradeDestination.Cloud;
            _destContainer.Children.Add(BuildCard(
                selected: localSelected, sub: false,
                title: LemoineStrings.T("upgradeLinks.labels.optLocalTitle"),
                desc:  LemoineStrings.T("upgradeLinks.labels.optLocalDesc"),
                onClick: () => { if (_dest == UpgradeDestination.Cloud) { _dest = UpgradeDestination.SelectedFolder; RebuildDestCards(); Changed(); } },
                extra: localSelected ? BuildLocalSubCards() : null));

            if (_hostIsCloud)
            {
                bool cloudSelected = _dest == UpgradeDestination.Cloud;
                _destContainer.Children.Add(BuildCard(
                    selected: cloudSelected, sub: false,
                    title: LemoineStrings.T("upgradeLinks.labels.optCloudTitle"),
                    desc:  LemoineStrings.T("upgradeLinks.labels.optCloudDesc"),
                    onClick: () => { if (_dest != UpgradeDestination.Cloud) { _dest = UpgradeDestination.Cloud; RebuildDestCards(); Changed(); } },
                    extra: cloudSelected ? Dim(LemoineStrings.T("upgradeLinks.labels.optCloudNote")) : null));
            }
        }

        private FrameworkElement BuildLocalSubCards()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

            bool selFolder = _dest == UpgradeDestination.SelectedFolder;
            panel.Children.Add(BuildCard(
                selected: selFolder, sub: true,
                title: LemoineStrings.T("upgradeLinks.labels.optSelectedFolderTitle"),
                desc:  LemoineStrings.T("upgradeLinks.labels.optSelectedFolderDesc"),
                onClick: () => { _dest = UpgradeDestination.SelectedFolder; RebuildDestCards(); Changed(); },
                extra: selFolder ? BuildSelectedFolderExtra() : null));

            bool curLoc = _dest == UpgradeDestination.CurrentLocation;
            panel.Children.Add(BuildCard(
                selected: curLoc, sub: true,
                title: LemoineStrings.T("upgradeLinks.labels.optCurrentLocationTitle"),
                desc:  LemoineStrings.T("upgradeLinks.labels.optCurrentLocationDesc"),
                onClick: () => { _dest = UpgradeDestination.CurrentLocation; RebuildDestCards(); Changed(); },
                extra: curLoc ? BuildCurrentLocationExtra() : null));

            return panel;
        }

        private FrameworkElement BuildCurrentLocationExtra()
        {
            var panel = new StackPanel();
            panel.Children.Add(Warn(LemoineStrings.T("upgradeLinks.labels.optOverwriteWarn")));
            panel.Children.Add(Dim(LemoineStrings.T("upgradeLinks.labels.optOverwriteRenameNote")));
            return panel;
        }

        private FrameworkElement BuildSelectedFolderExtra()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var folderPicker = new LemoineFolderBrowser
            {
                Label       = LemoineStrings.T("upgradeLinks.labels.saveLocationLabel"),
                Path        = _selectedFolder,
                DialogTitle = LemoineStrings.T("upgradeLinks.labels.saveLocationDialogTitle"),
            };
            folderPicker.PathChanged += p => { _selectedFolder = p ?? ""; Changed(); };
            panel.Children.Add(folderPicker);
            return panel;
        }

        private FrameworkElement BuildCard(bool selected, bool sub, string title, string desc, Action onClick, FrameworkElement? extra)
        {
            var content = new StackPanel();
            var titleTb = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold };
            titleTb.SetResourceReference(TextBlock.FontSizeProperty,   sub ? "LemoineFS_SM" : "LemoineFS_MD");
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            titleTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            content.Children.Add(titleTb);
            content.Children.Add(Dim(desc));
            if (selected && extra != null) content.Children.Add(extra);

            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = sub ? new Thickness(12, 10, 12, 10) : new Thickness(14, 12, 14, 12),
                Margin          = sub ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 0, 10),
                Cursor          = Cursors.Hand,
                Child           = content,
            };
            // A solid (or transparent) background is required for the whole card to be hit-testable —
            // a null background only hits the rendered text (CLAUDE.md WPF hit-testing rule).
            if (selected)
            {
                card.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                card.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
            }
            else
            {
                card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                card.Background = Brushes.Transparent;
            }
            card.MouseLeftButtonUp += (s, e) => onClick();
            return card;
        }

        // ── Review ──────────────────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("files",     LemoineStrings.T("upgradeLinks.review.itemFiles")),
            ("placement", LemoineStrings.T("upgradeLinks.review.itemPlacement")),
            ("dest",      LemoineStrings.T("upgradeLinks.review.itemDest")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["files"]     = ReadableCount() == 0 ? LemoineStrings.T("upgradeLinks.review.filesNone")
                                                 : LemoineStrings.T("upgradeLinks.review.filesValue", ReadableCount(), UpgradeCount()),
            ["placement"] = PlacementSummary(),
            ["dest"]      = DestSummary(),
        };

        public IList<string>? ReviewChips => null;
        public string?        ReviewNote  => null;
        public string?        ReviewWarning
        {
            get
            {
                if (ReadableCount() == 0) return LemoineStrings.T("upgradeLinks.review.warnNoFiles");
                int unreadable = _rows.Count(r => !r.Readable);
                if (unreadable > 0) return LemoineStrings.T("upgradeLinks.review.warnUnreadable", unreadable);
                if (_dest == UpgradeDestination.CurrentLocation) return LemoineStrings.T("upgradeLinks.review.warnOverwrite");
                return null;
            }
        }

        // ── Validation / Summary ─────────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "files": return ReadableCount() > 0;
                case "dest":  return DestValid();
                default:      return true;
            }
        }

        private bool DestValid()
        {
            switch (_dest)
            {
                case UpgradeDestination.SelectedFolder: return !string.IsNullOrWhiteSpace(_selectedFolder);
                case UpgradeDestination.Cloud:          return _hostIsCloud;
                default:                                return true;   // CurrentLocation
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "files":
                    return _rows.Count == 0 ? LemoineStrings.T("upgradeLinks.summaries.filesEmpty")
                        : LemoineStrings.T("upgradeLinks.summaries.files", ReadableCount(), UpgradeCount());
                case "dest": return DestSummary();
                case "run":  return LemoineStrings.T("upgradeLinks.summaries.run");
                default:     return "—";
            }
        }

        private string DestSummary()
        {
            switch (_dest)
            {
                case UpgradeDestination.CurrentLocation: return LemoineStrings.T("upgradeLinks.summaries.destCurrentLocation");
                case UpgradeDestination.Cloud:            return LemoineStrings.T("upgradeLinks.summaries.destCloud");
                default:                                  return LemoineStrings.T("upgradeLinks.summaries.destSelectedFolder", _selectedFolder);
            }
        }

        private string PlacementSummary()
        {
            var readable = _rows.Where(r => r.Readable).ToList();
            if (readable.Count == 0) return "—";
            var distinct = readable.Select(r => r.Placement).Distinct().ToList();
            return distinct.Count == 1 ? PlacementLabel(distinct[0]) : LemoineStrings.T("upgradeLinks.review.placementMixed");
        }

        private int ReadableCount() => _rows.Count(r => r.Readable);
        private int UpgradeCount()  => _rows.Count(r => r.Readable && !r.IsCurrent);

        // ── Run ──────────────────────────────────────────────────────────────────
        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog(LemoineStrings.T("upgradeLinks.log.handlerMissing"), "fail"); onComplete(0, 1, 0); return; }

            SaveSettings();

            var spec = new UpgradeLinksSpec
            {
                Files          = _rows.Where(r => r.Readable).Select(r => new UpgradeFileItem { Path = r.Path, Placement = r.Placement, SaveAsName = r.SaveAsName }).ToList(),
                Destination    = _dest,
                SelectedFolder = _selectedFolder,
                AuditOnOpen    = _audit,
                ReloadExisting = _reload,
                CloudReady     = _hostIsCloud && _dest == UpgradeDestination.Cloud,
            };

            _runHandler.Spec           = spec;
            _runHandler.HostFolder     = _hostFolder;
            _runHandler.PushLog        = pushLog;
            _runHandler.OnProgress     = onProgress;
            _runHandler.OnComplete     = onComplete;
            _runHandler.OnAwaitingUser = (awaiting, cLabel, sLabel) => AwaitingUserChanged?.Invoke(awaiting, cLabel, sLabel);

            pushLog(LemoineStrings.T("upgradeLinks.log.raising"), "info");
            _runEvent.Raise();
        }

        private void SaveSettings()
        {
            var s = UpgradeLinksSettings.Instance;
            if (_dest == UpgradeDestination.SelectedFolder && !string.IsNullOrWhiteSpace(_selectedFolder))
                s.LastSelectedFolder = _selectedFolder;
            s.Destination    = _dest;
            s.AuditOnOpen    = _audit;
            s.ReloadExisting = _reload;
            s.Save();
        }

        // ── Placement label ↔ enum ────────────────────────────────────────────────
        private static readonly UpgradePlacement[] PlacementOrder =
        {
            UpgradePlacement.OriginToOrigin, UpgradePlacement.CenterToCenter,
            UpgradePlacement.SharedCoordinates, UpgradePlacement.Site,
        };

        private static string PlacementKey(UpgradePlacement p)
        {
            switch (p)
            {
                case UpgradePlacement.CenterToCenter:    return "center";
                case UpgradePlacement.SharedCoordinates: return "shared";
                case UpgradePlacement.Site:              return "site";
                default:                                 return "origin";
            }
        }

        private static string PlacementLabel(UpgradePlacement p) => LemoineStrings.T("upgradeLinks.placement." + PlacementKey(p));
        private static List<string> PlacementLabels() => PlacementOrder.Select(PlacementLabel).ToList();

        private static readonly Dictionary<string, UpgradePlacement> LabelToPlacement =
            PlacementOrder.ToDictionary(PlacementLabel, p => p, StringComparer.Ordinal);

        // ── Small WPF helpers (theme via resource refs only) ──────────────────────
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
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }

        private static TextBlock Warn(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineRed");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }
    }
}
