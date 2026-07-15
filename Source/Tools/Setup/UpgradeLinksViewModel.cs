using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// Upgrade &amp; Link Models — queue Revit files from any folder, choose each one's link placement,
    /// pick where the upgraded copies are saved, then run: each file is opened (which upgrades it),
    /// saved to the destination, closed, and linked into the host. Files are processed serially on the
    /// Revit thread for RAM control (see <see cref="UpgradeLinksRunHandler"/>).
    /// </summary>
    public sealed class UpgradeLinksViewModel : IStepFlowTool, IReviewableTool, IRunResult, IStepAware, IToolCleanup, IRunPausable
    {
        public string Title       => AppStrings.T("upgradeLinks.title");
        public string RunLabel    => AppStrings.T("upgradeLinks.runLabel");
        public string? ResultNoun => AppStrings.T("upgradeLinks.resultNoun");
        public IReadOnlyList<ResultChip>? ResultChips => null;

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("files", AppStrings.T("upgradeLinks.steps.files"), required: true),
            new StepDefinition("dest",  AppStrings.T("upgradeLinks.steps.dest"),  required: true),
            new StepDefinition("run",   AppStrings.T("upgradeLinks.steps.run"),   required: false),
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
        // The user's last "Set all placement" choice — RebuildFilesTable() reseeds the
        // control from this (falling back to _defaultPlacement) instead of always reseeding
        // from _defaultPlacement, which previously made the picker snap back to the default
        // label on every rebuild even though the rows themselves had been updated correctly.
        private UpgradePlacement?  _setAllSelection;
        private bool               _scanning;
        private string?            _scanError;   // whole-scan failure shown in the files table
        private int                _dupCount;    // files skipped by the last add because they were already listed

        // Live UI handles
        private StackPanel? _filesContainer, _destContainer;
        private Dispatcher? _disp;
        private Action<string>? _refreshStep;

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── IRunPausable — Cloud mode pauses per file on Revit's native Save dialog ──────
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

                // Closing the window during a Cloud pause would otherwise strand the handler's
                // continuation state for the rest of the session (every later run would be
                // swallowed) and leave the paused upgrade document open. Tell the handler to
                // abort and clean up on its next Execute.
                if (_runEvent != null && _runHandler.IsCloudRunActive)
                {
                    _runHandler.CloudAbortRequested = true;
                    try { _runEvent.Raise(); }
                    catch (Exception ex) { DiagnosticsLog.Swallowed("UpgradeLinks: raise cloud abort on window close", ex); }
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "files": return BuildFilesStep();
                case "dest":  return BuildDestStep();
                default:      return null;   // "run" rendered by the framework (IReviewableTool)
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
            outer.Children.Add(Dim(AppStrings.T("upgradeLinks.labels.filesHint")));
            _filesContainer = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            outer.Children.Add(_filesContainer);
            RebuildFilesTable();
            return outer;
        }

        private void RebuildFilesTable()
        {
            if (_filesContainer == null) return;
            _filesContainer.Children.Clear();

            if (_scanning) _filesContainer.Children.Add(Dim(AppStrings.T("upgradeLinks.labels.scanning")));
            if (_scanError != null) _filesContainer.Children.Add(Warn(AppStrings.T("upgradeLinks.labels.scanFailed", _scanError)));
            if (_dupCount > 0) _filesContainer.Children.Add(Dim(AppStrings.T("upgradeLinks.labels.dupSkipped", _dupCount)));

            if (_rows.Count == 0)
            {
                _filesContainer.Children.Add(Dim(AppStrings.T("upgradeLinks.labels.empty")));
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

                int unreadable = _rows.Count(r => !r.Readable && !r.IsFutureVersion);
                if (unreadable > 0) _filesContainer.Children.Add(Warn(AppStrings.T("upgradeLinks.labels.unreadableNote", unreadable)));

                int tooNew = _rows.Count(r => r.IsFutureVersion);
                if (tooNew > 0) _filesContainer.Children.Add(Warn(AppStrings.T("upgradeLinks.labels.tooNewNote", tooNew)));
            }

            if (_rows.Count > 0)
            {
                string curVer = _scanHandler?.CurrentVersionNumber ?? "";
                _filesContainer.Children.Add(Dim(string.IsNullOrEmpty(curVer)
                    ? AppStrings.T("upgradeLinks.labels.futureVersionNoteGeneric")
                    : AppStrings.T("upgradeLinks.labels.futureVersionNote", curVer)));
            }

            // Add / Clear toolbar
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var add = ControlStyles.BuildButton(AppStrings.T("upgradeLinks.labels.addFiles"), ControlStyles.ButtonVariant.Primary);
            add.Click += (s, e) => OnAddFiles();
            bar.Children.Add(add);
            if (_rows.Count > 0)
            {
                var clear = ControlStyles.BuildSmallButton(AppStrings.T("upgradeLinks.labels.clearList"));
                clear.Margin = new Thickness(8, 0, 0, 0);
                clear.Click += (s, e) => { _rows.Clear(); _dupCount = 0; _scanError = null; RebuildFilesTable(); Changed(); };
                bar.Children.Add(clear);
            }
            _filesContainer.Children.Add(bar);

            if (_rows.Count > 0)
            {
                var setAllPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var lbl = Label(AppStrings.T("upgradeLinks.labels.setAll") + ":");
                lbl.VerticalAlignment = VerticalAlignment.Center;
                lbl.Margin = new Thickness(0, 0, 8, 0);
                setAllPanel.Children.Add(lbl);
                var setAll = new SingleSelect { Width = 180 };
                setAll.Items = PlacementLabels();
                setAll.SelectedItem = PlacementLabel(_setAllSelection ?? _defaultPlacement);
                setAll.SelectionChanged += lblSel =>
                {
                    if (TryLabelToPlacement(lblSel, out var p))
                    {
                        _setAllSelection = p;
                        foreach (var r in _rows) r.Placement = p;
                        RebuildFilesTable();
                        Changed();
                    }
                };
                setAllPanel.Children.Add(setAll);
                _filesContainer.Children.Add(setAllPanel);

                var toggles = new ToggleSwitches { Margin = new Thickness(0, 12, 0, 0) };
                toggles.SetItems(new List<ToggleItem>
                {
                    new ToggleItem { Id = "audit",  Label = AppStrings.T("upgradeLinks.labels.auditLabel"),  Desc = AppStrings.T("upgradeLinks.labels.auditDesc"),  DefaultOn = _audit },
                    new ToggleItem { Id = "reload", Label = AppStrings.T("upgradeLinks.labels.reloadLabel"), Desc = AppStrings.T("upgradeLinks.labels.reloadDesc"), DefaultOn = _reload },
                });
                toggles.StateChanged += st =>
                {
                    if (st.TryGetValue("audit",  out var a)) _audit  = a;
                    if (st.TryGetValue("reload", out var b)) _reload = b;
                    Changed();   // the review step shows these as chips — keep it current
                };
                _filesContainer.Children.Add(toggles);
            }
        }

        private Grid BuildHeaderRow()
        {
            var g = FileRowGrid();
            g.SetResourceReference(Grid.BackgroundProperty, "LemoineRaised");
            var file = Dim(AppStrings.T("upgradeLinks.labels.colFile"));       Grid.SetColumn(file, 1);
            var ver  = Dim(AppStrings.T("upgradeLinks.labels.colVersion"));    Grid.SetColumn(ver, 2);
            var plc  = Dim(AppStrings.T("upgradeLinks.labels.colPlacement"));  Grid.SetColumn(plc, 3);
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

            // Column 2 — version badge. Top-aligned (not Center) so it lines up with the
            // first line of the two-line name+path cell rather than the cell's full height.
            var badge = VersionBadge(row);
            badge.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(badge, 2);
            g.Children.Add(badge);

            // Column 3 — placement picker (same top-alignment as the badge above).
            var pick = new SingleSelect { IsEnabled = Usable(row), VerticalAlignment = VerticalAlignment.Top };
            pick.Items = PlacementLabels();
            pick.SelectedItem = PlacementLabel(row.Placement);
            pick.SelectionChanged += lblSel =>
            {
                if (TryLabelToPlacement(lblSel, out var p)) { row.Placement = p; Changed(); }
            };
            Grid.SetColumn(pick, 3);
            g.Children.Add(pick);

            // Column 4 — remove
            var rm = ControlStyles.BuildSmallButton(char.ConvertFromUtf32(0xE74D), ControlStyles.ButtonVariant.Danger); // Delete (trash)
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
                IsEnabled = Usable(row),
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
            // Fixed (not Auto) so the header row — which never populates column 4 with a
            // button — reserves the same width as a data row's remove button. An Auto
            // column collapses to 0 in the header, which pushes the star column 1 wider
            // there and throws Version/Placement out of alignment with the rows below.
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });                    // 4 remove
            return g;
        }

        private FrameworkElement VersionBadge(UpgradeFileRow row)
        {
            string text; string colorKey;
            if (row.IsFutureVersion) { text = AppStrings.T("upgradeLinks.labels.verTooNew", row.Version);   colorKey = "LemoineRed"; }
            else if (!row.Readable)  { text = AppStrings.T("upgradeLinks.labels.verUnreadable");            colorKey = "LemoineRed"; }
            else if (!row.Scanned)   { text = AppStrings.T("upgradeLinks.labels.verUnknown");               colorKey = "LemoineTextDim"; }
            else if (row.IsCurrent)  { text = AppStrings.T("upgradeLinks.labels.verCurrent", row.Version);  colorKey = "LemoineGreen"; }
            else                     { text = AppStrings.T("upgradeLinks.labels.verUpgrade", row.Version);  colorKey = "LemoineAccent"; }

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
                Title       = AppStrings.T("upgradeLinks.title"),
            };
            bool? ok;
            try { ok = dlg.ShowDialog(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("UpgradeLinks: open file dialog", ex); return; }
            if (ok != true || dlg.FileNames == null || dlg.FileNames.Length == 0) return;

            var existing = new HashSet<string>(_rows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            var added = new List<UpgradeFileRow>();
            _dupCount = 0;
            foreach (var p in dlg.FileNames)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (!existing.Add(p)) { _dupCount++; continue; }
                var row = new UpgradeFileRow
                {
                    Path = p,
                    Placement = _defaultPlacement,
                    SaveAsName = System.IO.Path.GetFileNameWithoutExtension(p),
                };
                _rows.Add(row); added.Add(row);
            }
            if (added.Count == 0)
            {
                // Nothing new — but if everything picked was a duplicate, say so instead of
                // silently doing nothing.
                if (_dupCount > 0) RebuildFilesTable();
                return;
            }

            RebuildFilesTable();
            Changed();
            ScanNewRows(added);
        }

        private void ScanNewRows(List<UpgradeFileRow> rows)
        {
            if (_scanHandler == null || _scanEvent == null) return;
            _scanning  = true;
            _scanError = null;
            RebuildFilesTable();
            Changed();   // the files step is invalid while the scan is in flight

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
                        row.IsFutureVersion = res.IsFutureVersion;
                    }
                }
                RebuildFilesTable();
                Changed();
            }));
            _scanHandler.OnError = err => _disp?.BeginInvoke((Action)(() =>
            {
                _scanning  = false;
                _scanError = string.IsNullOrEmpty(err) ? "?" : err;
                DiagnosticsLog.Warn("UpgradeLinks: scan error", err ?? "");
                RebuildFilesTable();
                Changed();
            }));
            _scanEvent.Raise();
        }

        // ══════════════════════ Step 2: Destination ════════════════════════════
        private FrameworkElement BuildDestStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(Label(AppStrings.T("upgradeLinks.labels.destQuestion")));
            outer.Children.Add(Dim(AppStrings.T("upgradeLinks.labels.destHint")));
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
                title: AppStrings.T("upgradeLinks.labels.optLocalTitle"),
                desc:  AppStrings.T("upgradeLinks.labels.optLocalDesc"),
                onClick: () => { if (_dest == UpgradeDestination.Cloud) { _dest = UpgradeDestination.SelectedFolder; RebuildDestCards(); Changed(); } },
                extra: localSelected ? BuildLocalSubCards() : null));

            if (_hostIsCloud)
            {
                bool cloudSelected = _dest == UpgradeDestination.Cloud;
                _destContainer.Children.Add(BuildCard(
                    selected: cloudSelected, sub: false,
                    title: AppStrings.T("upgradeLinks.labels.optCloudTitle"),
                    desc:  AppStrings.T("upgradeLinks.labels.optCloudDesc"),
                    onClick: () => { if (_dest != UpgradeDestination.Cloud) { _dest = UpgradeDestination.Cloud; RebuildDestCards(); Changed(); } },
                    extra: cloudSelected ? Dim(AppStrings.T("upgradeLinks.labels.optCloudNote")) : null));
            }
        }

        private FrameworkElement BuildLocalSubCards()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

            bool selFolder = _dest == UpgradeDestination.SelectedFolder;
            panel.Children.Add(BuildCard(
                selected: selFolder, sub: true,
                title: AppStrings.T("upgradeLinks.labels.optSelectedFolderTitle"),
                desc:  AppStrings.T("upgradeLinks.labels.optSelectedFolderDesc"),
                onClick: () => { _dest = UpgradeDestination.SelectedFolder; RebuildDestCards(); Changed(); },
                extra: selFolder ? BuildSelectedFolderExtra() : null));

            bool curLoc = _dest == UpgradeDestination.CurrentLocation;
            panel.Children.Add(BuildCard(
                selected: curLoc, sub: true,
                title: AppStrings.T("upgradeLinks.labels.optCurrentLocationTitle"),
                desc:  AppStrings.T("upgradeLinks.labels.optCurrentLocationDesc"),
                onClick: () => { _dest = UpgradeDestination.CurrentLocation; RebuildDestCards(); Changed(); },
                extra: curLoc ? BuildCurrentLocationExtra() : null));

            return panel;
        }

        private FrameworkElement BuildCurrentLocationExtra()
        {
            var panel = new StackPanel();
            panel.Children.Add(Warn(AppStrings.T("upgradeLinks.labels.optOverwriteWarn")));
            panel.Children.Add(Dim(AppStrings.T("upgradeLinks.labels.optOverwriteRenameNote")));
            return panel;
        }

        private FrameworkElement BuildSelectedFolderExtra()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var folderPicker = new FolderBrowser
            {
                Label       = AppStrings.T("upgradeLinks.labels.saveLocationLabel"),
                Path        = _selectedFolder,
                DialogTitle = AppStrings.T("upgradeLinks.labels.saveLocationDialogTitle"),
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
            ("files",     AppStrings.T("upgradeLinks.review.itemFiles")),
            ("placement", AppStrings.T("upgradeLinks.review.itemPlacement")),
            ("dest",      AppStrings.T("upgradeLinks.review.itemDest")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["files"]     = ReadableCount() == 0 ? AppStrings.T("upgradeLinks.review.filesNone")
                                                 : AppStrings.T("upgradeLinks.review.filesValue", ReadableCount(), UpgradeCount()),
            ["placement"] = PlacementSummary(),
            ["dest"]      = DestSummary(),
        };

        public IList<string>? ReviewChips
        {
            get
            {
                var chips = new List<string>
                {
                    AppStrings.T("upgradeLinks.review.chipAudit")  + (_audit  ? " ✓" : " ✗"),
                    AppStrings.T("upgradeLinks.review.chipReload") + (_reload ? " ✓" : " ✗"),
                };
                if (_dest == UpgradeDestination.CurrentLocation)
                    chips.Add(AppStrings.T("upgradeLinks.review.chipNamesIgnored"));
                return chips;
            }
        }
        public string?        ReviewNote  => null;
        public string?        ReviewWarning
        {
            get
            {
                if (ReadableCount() == 0) return AppStrings.T("upgradeLinks.review.warnNoFiles");
                int tooNew = _rows.Count(r => r.IsFutureVersion);
                if (tooNew > 0) return AppStrings.T("upgradeLinks.review.warnTooNew", tooNew);
                int unreadable = _rows.Count(r => !r.Readable && !r.IsFutureVersion);
                if (unreadable > 0) return AppStrings.T("upgradeLinks.review.warnUnreadable", unreadable);
                if (_dest == UpgradeDestination.CurrentLocation) return AppStrings.T("upgradeLinks.review.warnOverwrite");
                return null;
            }
        }

        // ── Validation / Summary ─────────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                // Invalid while the version scan is in flight — a not-yet-scanned row defaults to
                // readable, so running early could include a file the scan would have flagged.
                case "files": return !_scanning && ReadableCount() > 0;
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
                    return _rows.Count == 0 ? AppStrings.T("upgradeLinks.summaries.filesEmpty")
                        : AppStrings.T("upgradeLinks.summaries.files", ReadableCount(), UpgradeCount());
                case "dest": return DestSummary();
                case "run":  return AppStrings.T("upgradeLinks.summaries.run");
                default:     return "—";
            }
        }

        private string DestSummary()
        {
            switch (_dest)
            {
                case UpgradeDestination.CurrentLocation: return AppStrings.T("upgradeLinks.summaries.destCurrentLocation");
                case UpgradeDestination.Cloud:            return AppStrings.T("upgradeLinks.summaries.destCloud");
                default:                                  return AppStrings.T("upgradeLinks.summaries.destSelectedFolder", _selectedFolder);
            }
        }

        private string PlacementSummary()
        {
            var readable = _rows.Where(Usable).ToList();
            if (readable.Count == 0) return "—";
            var distinct = readable.Select(r => r.Placement).Distinct().ToList();
            return distinct.Count == 1 ? PlacementLabel(distinct[0]) : AppStrings.T("upgradeLinks.review.placementMixed");
        }

        // A row is usable when it's both readable and not saved in a Revit version newer
        // than this one — Revit cannot open files from a later release (not backwards
        // compatible), so a future-version row is excluded the same way an unreadable one is.
        private static bool Usable(UpgradeFileRow r) => r.Readable && !r.IsFutureVersion;

        private int ReadableCount() => _rows.Count(Usable);
        private int UpgradeCount()  => _rows.Count(r => Usable(r) && !r.IsCurrent);

        // ── Run ──────────────────────────────────────────────────────────────────
        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog(AppStrings.T("upgradeLinks.log.handlerMissing"), "fail"); onComplete(0, 1, 0); return; }

            SaveSettings();

            var spec = new UpgradeLinksSpec
            {
                Files          = _rows.Where(Usable).Select(r => new UpgradeFileItem { Path = r.Path, Placement = r.Placement, SaveAsName = r.SaveAsName }).ToList(),
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

            pushLog(AppStrings.T("upgradeLinks.log.raising"), "info");
            _runEvent.Raise();
        }

        // Persists only the non-path toggles this run used — the folder/destination
        // defaults are set exclusively from the Settings window (see CLAUDE.md WS-10:
        // a run no longer remembers its own last-used path back into the defaults).
        private void SaveSettings()
        {
            var s = UpgradeLinksSettings.Instance;
            s.AuditOnOpen    = _audit;
            s.ReloadExisting = _reload;
            s.Save();
        }

        // ── Placement label ↔ enum ────────────────────────────────────────────────
        // internal (not private): reused by GlobalSettingsWindow's Setup tab (WS-10) to
        // present the same placement picker for the persisted default.
        internal static readonly UpgradePlacement[] PlacementOrder =
        {
            UpgradePlacement.InternalOrigin, UpgradePlacement.ProjectBasePoint,
            UpgradePlacement.CenterToCenter, UpgradePlacement.SurveyPoint,
        };

        private static string PlacementKey(UpgradePlacement p)
        {
            switch (p)
            {
                case UpgradePlacement.ProjectBasePoint: return "projectBasePoint";
                case UpgradePlacement.CenterToCenter:   return "center";
                case UpgradePlacement.SurveyPoint:      return "surveyPoint";
                default:                                return "internalOrigin";
            }
        }

        internal static string PlacementLabel(UpgradePlacement p) => AppStrings.T("upgradeLinks.placement." + PlacementKey(p));
        internal static List<string> PlacementLabels() => PlacementOrder.Select(PlacementLabel).ToList();

        // Resolved per call (never a cached static dictionary): a static map keyed by display
        // labels freezes the first-touched language, so after a language switch every placement
        // pick in a newly opened window would silently miss the lookup.
        internal static bool TryLabelToPlacement(string? label, out UpgradePlacement placement)
        {
            foreach (var p in PlacementOrder)
            {
                if (string.Equals(PlacementLabel(p), label, StringComparison.Ordinal))
                {
                    placement = p;
                    return true;
                }
            }
            placement = UpgradePlacement.InternalOrigin;
            return false;
        }

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
