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
    /// Replace Link — swap a linked model for a new file, in place. Each queued row pairs an
    /// existing link in this model with the file that replaces it; the run upgrades that file to
    /// the current Revit version, writes it over the linked file's own path (or beside it), and
    /// re-points the SAME <see cref="Autodesk.Revit.DB.RevitLinkType"/> at it so the link's
    /// instances, view overrides and filters survive untouched. See
    /// <see cref="ReplaceLinkRunHandler"/> for the run itself.
    /// </summary>
    public sealed class ReplaceLinkViewModel : IStepFlowTool, IReviewableTool, IRunResult, IStepAware, IToolCleanup
    {
        public string Title       => AppStrings.T("replaceLink.title");
        public string RunLabel    => AppStrings.T("replaceLink.runLabel");
        public string? ResultNoun => AppStrings.T("replaceLink.resultNoun");
        public IReadOnlyList<ResultChip>? ResultChips => null;

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("links",    AppStrings.T("replaceLink.steps.links"),    required: true),
            new StepDefinition("dest",     AppStrings.T("replaceLink.steps.dest"),     required: true),
            new StepDefinition("position", AppStrings.T("replaceLink.steps.position"), required: true),
            new StepDefinition("run",      AppStrings.T("replaceLink.steps.run"),      required: false),
        };

        // ── Injected ───────────────────────────────────────────────────────────
        private readonly UpgradeLinksScanHandler? _scanHandler;   // shared read-only version scan
        private readonly ExternalEvent?           _scanEvent;
        private readonly ReplaceLinkRunHandler?   _runHandler;
        private readonly ExternalEvent?           _runEvent;
        private readonly List<HostLinkInfo>       _hostLinks;     // captured on the Revit thread at launch

        // ── State ──────────────────────────────────────────────────────────────
        private readonly List<ReplaceRow> _rows = new List<ReplaceRow>();
        private ReplaceDestination _dest     = ReplaceLinkSettings.Instance.Destination;
        private ReplacePosition    _position = ReplaceLinkSettings.Instance.Position;
        private string _selectedFolder       = ReplaceLinkSettings.Instance.LastSelectedFolder;
        private bool   _backup   = ReplaceLinkSettings.Instance.BackupOriginal;
        private bool   _audit    = ReplaceLinkSettings.Instance.AuditOnOpen;
        private bool   _movement = ReplaceLinkSettings.Instance.ReportMovement;
        private bool   _scanning;
        private string? _scanError;

        // Live UI handles
        private StackPanel? _linksContainer, _destContainer, _posContainer;
        private Dispatcher? _disp;
        private Action<string>? _refreshStep;

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        public ReplaceLinkViewModel(
            UpgradeLinksScanHandler? scanHandler, ExternalEvent? scanEvent,
            ReplaceLinkRunHandler?   runHandler,  ExternalEvent?  runEvent,
            List<HostLinkInfo>?      hostLinks)
        {
            _scanHandler = scanHandler; _scanEvent = scanEvent;
            _runHandler  = runHandler;  _runEvent  = runEvent;
            _hostLinks   = hostLinks ?? new List<HostLinkInfo>();
        }

        public void OnWindowClosed()
        {
            if (_scanHandler != null) { _scanHandler.OnScanned = null; _scanHandler.OnError = null; }
            if (_runHandler  != null) { _runHandler.PushLog = null; _runHandler.OnProgress = null; _runHandler.OnComplete = null; }
        }

        // ═══════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "links":    return BuildLinksStep();
                case "dest":     return BuildDestStep();
                case "position": return BuildPositionStep();
                default:         return null;   // "run" rendered by the framework (IReviewableTool)
            }
        }

        // ── IStepAware ─────────────────────────────────────────────────────────
        public void SetContentRefreshCallback(Action<string> rebuildStepContent) => _refreshStep = rebuildStepContent;
        public void OnStepActivated(string stepId)
        {
            if (stepId == "links") _disp = Dispatcher.CurrentDispatcher;
            // The destination step's warnings depend on which links are queued, so it is rebuilt
            // on activation rather than rendering the state the queue had at construction.
            if (stepId == "dest") _refreshStep?.Invoke("dest");
        }

        // ══════════════════════ Step 1: Links ══════════════════════════════════
        private FrameworkElement BuildLinksStep()
        {
            _disp = Dispatcher.CurrentDispatcher;
            var outer = new StackPanel();
            outer.Children.Add(Dim(AppStrings.T("replaceLink.labels.linksHint")));
            _linksContainer = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            outer.Children.Add(_linksContainer);
            RebuildLinksTable();
            return outer;
        }

        private List<HostLinkInfo> Replaceable => _hostLinks.Where(l => l.Replaceable).ToList();

        private void RebuildLinksTable()
        {
            if (_linksContainer == null) return;
            _linksContainer.Children.Clear();

            if (_hostLinks.Count == 0)
            {
                _linksContainer.Children.Add(Warn(AppStrings.T("replaceLink.labels.noLinks")));
                return;
            }

            var replaceable = Replaceable;
            var blocked     = _hostLinks.Where(l => !l.Replaceable).ToList();

            if (_scanning)          _linksContainer.Children.Add(Dim(AppStrings.T("replaceLink.labels.scanning")));
            if (_scanError != null) _linksContainer.Children.Add(Warn(AppStrings.T("replaceLink.labels.scanFailed", _scanError)));

            if (_rows.Count == 0)
            {
                _linksContainer.Children.Add(Dim(AppStrings.T("replaceLink.labels.empty")));
            }
            else
            {
                foreach (var row in _rows) _linksContainer.Children.Add(BuildRowCard(row, replaceable));

                int unreadable = _rows.Count(r => r.Scanned && !r.Readable && !r.IsFutureVersion);
                if (unreadable > 0) _linksContainer.Children.Add(Warn(AppStrings.T("replaceLink.labels.unreadableNote", unreadable)));

                int tooNew = _rows.Count(r => r.IsFutureVersion);
                if (tooNew > 0) _linksContainer.Children.Add(Warn(AppStrings.T("replaceLink.labels.tooNewNote", tooNew)));

                string curVer = _scanHandler?.CurrentVersionNumber ?? "";
                _linksContainer.Children.Add(Dim(string.IsNullOrEmpty(curVer)
                    ? AppStrings.T("replaceLink.labels.futureVersionNoteGeneric")
                    : AppStrings.T("replaceLink.labels.futureVersionNote", curVer)));
            }

            // Links that can't be replaced are listed with their reason — a silently shorter
            // picker is indistinguishable from a broken collector (CLAUDE.md).
            if (blocked.Count > 0)
            {
                _linksContainer.Children.Add(Dim(AppStrings.T("replaceLink.labels.blockedNote", blocked.Count)));
                foreach (var b in blocked)
                    _linksContainer.Children.Add(Dim($"   {b.Name} — {b.BlockedReason}"));
            }

            if (replaceable.Count == 0)
            {
                _linksContainer.Children.Add(Warn(AppStrings.T("replaceLink.labels.noneReplaceable")));
                return;
            }

            // Add / Clear toolbar
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var add = ControlStyles.BuildButton(AppStrings.T("replaceLink.labels.addRow"), ControlStyles.ButtonVariant.Primary);
            add.Click += (s, e) =>
            {
                _rows.Add(new ReplaceRow());
                RebuildLinksTable();
                Changed();
            };
            bar.Children.Add(add);
            if (_rows.Count > 0)
            {
                var clear = ControlStyles.BuildSmallButton(AppStrings.T("replaceLink.labels.clearList"));
                clear.Margin = new Thickness(8, 0, 0, 0);
                clear.Click += (s, e) => { _rows.Clear(); _scanError = null; RebuildLinksTable(); Changed(); };
                bar.Children.Add(clear);
            }
            _linksContainer.Children.Add(bar);

            var toggles = new ToggleSwitches { Margin = new Thickness(0, 12, 0, 0) };
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem { Id = "backup", Label = AppStrings.T("replaceLink.labels.backupLabel"), Desc = AppStrings.T("replaceLink.labels.backupDesc"), DefaultOn = _backup },
                new ToggleItem { Id = "audit",  Label = AppStrings.T("replaceLink.labels.auditLabel"),  Desc = AppStrings.T("replaceLink.labels.auditDesc"),  DefaultOn = _audit  },
            });
            toggles.StateChanged += st =>
            {
                if (st.TryGetValue("backup", out var b)) _backup = b;
                if (st.TryGetValue("audit",  out var a)) _audit  = a;
                Changed();   // the review step shows these as chips — keep it current
            };
            _linksContainer.Children.Add(toggles);
        }

        /// <summary>One replacement: the existing link on top, the file that replaces it below.</summary>
        private FrameworkElement BuildRowCard(ReplaceRow row, List<HostLinkInfo> replaceable)
        {
            var content = new StackPanel();

            // ── Existing link picker + remove ────────────────────────────────
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var linkPick = new SingleSelect();
            linkPick.Items = replaceable.Select(LinkLabel).ToList();
            linkPick.SelectedItem = row.TypeId != 0 ? LinkLabelFor(row.TypeId, replaceable) : null;
            linkPick.SelectionChanged += sel =>
            {
                var hit = replaceable.FirstOrDefault(l => string.Equals(LinkLabel(l), sel, StringComparison.Ordinal));
                if (hit == null) return;
                row.TypeId   = hit.TypeId;
                row.LinkName = hit.Name;
                row.LinkPath = hit.Path;
                // The save-as name defaults to the linked file's own name, so the rename
                // destinations start from something valid rather than empty.
                if (string.IsNullOrEmpty(row.SaveAsName) && !string.IsNullOrEmpty(hit.Path))
                    row.SaveAsName = System.IO.Path.GetFileNameWithoutExtension(hit.Path);
                RebuildLinksTable();
                Changed();
            };
            Grid.SetColumn(linkPick, 0);
            top.Children.Add(linkPick);

            var rm = ControlStyles.BuildSmallButton(char.ConvertFromUtf32(0xE74D), ControlStyles.ButtonVariant.Danger); // Delete (trash)
            rm.FontFamily = new FontFamily("Segoe MDL2 Assets");   // glyph font — LemoineUiFont can't render MDL2 codepoints
            rm.VerticalAlignment = VerticalAlignment.Center;
            rm.Margin = new Thickness(8, 0, 0, 0);
            rm.Click += (s, e) => { _rows.Remove(row); RebuildLinksTable(); Changed(); };
            Grid.SetColumn(rm, 1);
            top.Children.Add(rm);

            content.Children.Add(top);
            if (!string.IsNullOrEmpty(row.LinkPath)) content.Children.Add(Mono(row.LinkPath));

            content.Children.Add(Sub(AppStrings.T("replaceLink.labels.replaceWith")));

            // ── Replacement file + browse ────────────────────────────────────
            var fileRow = new Grid();
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var fileBox = new WpfTextBox
            {
                Text = string.IsNullOrEmpty(row.NewFilePath)
                    ? ""
                    : System.IO.Path.GetFileName(row.NewFilePath),
                IsReadOnly = true,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 3, 4, 3),
            };
            fileBox.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            fileBox.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            fileBox.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorderMid");
            fileBox.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineMonoFont");
            fileBox.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_SM");
            Grid.SetColumn(fileBox, 0);
            fileRow.Children.Add(fileBox);

            var browse = ControlStyles.BuildSmallButton(AppStrings.T("replaceLink.labels.browse"));
            browse.Margin = new Thickness(8, 0, 0, 0);
            browse.Click += (s, e) => OnBrowse(row);
            Grid.SetColumn(browse, 1);
            fileRow.Children.Add(browse);

            content.Children.Add(fileRow);

            if (!string.IsNullOrEmpty(row.NewFilePath))
            {
                content.Children.Add(Mono(System.IO.Path.GetDirectoryName(row.NewFilePath) ?? ""));

                var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                meta.Children.Add(VersionBadge(row));
                if (row.IsWorkshared)
                {
                    var ws = Badge(AppStrings.T("replaceLink.labels.verWorkshared"), "LemoineTextDim");
                    ws.Margin = new Thickness(6, 0, 0, 0);
                    meta.Children.Add(ws);
                }
                content.Children.Add(meta);
            }

            // Save-as name — only meaningful for the two non-overwrite destinations.
            if (_dest != ReplaceDestination.OverwriteLinkedFile && row.TypeId != 0)
            {
                content.Children.Add(Label(AppStrings.T("replaceLink.labels.saveLocationLabel")));
                var nameBox = new WpfTextBox
                {
                    Text = row.SaveAsName,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(4, 3, 4, 3),
                };
                nameBox.SetResourceReference(WpfTextBox.BackgroundProperty,     "LemoineSelectBg");
                nameBox.SetResourceReference(WpfTextBox.ForegroundProperty,     "LemoineText");
                nameBox.SetResourceReference(WpfTextBox.BorderBrushProperty,    "LemoineBorderMid");
                nameBox.SetResourceReference(WpfTextBox.CaretBrushProperty,     "LemoineText");
                nameBox.SetResourceReference(WpfTextBox.SelectionBrushProperty, "LemoineAccent");
                nameBox.SetResourceReference(WpfTextBox.FontFamilyProperty,     "LemoineMonoFont");
                nameBox.SetResourceReference(WpfTextBox.FontSizeProperty,       "LemoineFS_SM");
                nameBox.TextChanged += (s, e) => { row.SaveAsName = nameBox.Text; Changed(); };
                content.Children.Add(nameBox);
            }

            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(12, 10, 12, 10),
                Margin          = new Thickness(0, 0, 0, 8),
                Child           = content,
            };
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            return card;
        }

        private static string LinkLabel(HostLinkInfo l) =>
            l.InstanceCount > 1 ? $"{l.Name}  ({l.InstanceCount})" : l.Name;

        private static string? LinkLabelFor(long typeId, List<HostLinkInfo> pool)
        {
            var hit = pool.FirstOrDefault(l => l.TypeId == typeId);
            return hit != null ? LinkLabel(hit) : null;
        }

        private void OnBrowse(ReplaceRow row)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = false,
                Filter      = "Revit files (*.rvt)|*.rvt",
                Title       = AppStrings.T("replaceLink.labels.fileDialog"),
            };
            bool? ok;
            try { ok = dlg.ShowDialog(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ReplaceLink: open file dialog", ex); return; }
            if (ok != true || string.IsNullOrWhiteSpace(dlg.FileName)) return;

            row.NewFilePath = dlg.FileName;
            row.Scanned = false; row.Readable = true; row.Version = "?";
            row.IsCurrent = false; row.IsFutureVersion = false; row.IsWorkshared = false;
            RebuildLinksTable();
            Changed();
            ScanRow(row);
        }

        private void ScanRow(ReplaceRow row)
        {
            if (_scanHandler == null || _scanEvent == null) return;
            _scanning  = true;
            _scanError = null;
            RebuildLinksTable();
            Changed();   // the links step is invalid while the scan is in flight

            _scanHandler.Paths = new List<string> { row.NewFilePath };
            _scanHandler.OnScanned = results => _disp?.BeginInvoke((Action)(() =>
            {
                _scanning = false;
                if (results != null)
                {
                    foreach (var res in results)
                    {
                        // Match by path — a second Browse can land while the first scan is in
                        // flight, and the row it belongs to is the one holding that path. The
                        // lambda's parameter is named apart from the loop variable (CS0136).
                        var matches = _rows
                            .Where(candidate => string.Equals(candidate.NewFilePath, res.Path, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        foreach (var r in matches)
                        {
                            r.Scanned         = true;
                            r.Readable        = res.Readable;
                            r.Version         = res.Version;
                            r.IsWorkshared    = res.IsWorkshared;
                            r.IsCurrent       = res.IsCurrent;
                            r.IsFutureVersion = res.IsFutureVersion;
                        }
                    }
                }
                RebuildLinksTable();
                Changed();
            }));
            _scanHandler.OnError = err => _disp?.BeginInvoke((Action)(() =>
            {
                _scanning  = false;
                _scanError = string.IsNullOrEmpty(err) ? "?" : err;
                DiagnosticsLog.Warn("ReplaceLink: scan error", err ?? "");
                RebuildLinksTable();
                Changed();
            }));
            _scanEvent.Raise();
        }

        private FrameworkElement VersionBadge(ReplaceRow row)
        {
            string text; string colorKey;
            if (row.IsFutureVersion) { text = AppStrings.T("replaceLink.labels.verTooNew", row.Version);   colorKey = "LemoineRed"; }
            else if (!row.Readable)  { text = AppStrings.T("replaceLink.labels.verUnreadable");            colorKey = "LemoineRed"; }
            else if (!row.Scanned)   { text = AppStrings.T("replaceLink.labels.verUnknown");               colorKey = "LemoineTextDim"; }
            else if (row.IsCurrent)  { text = AppStrings.T("replaceLink.labels.verCurrent", row.Version);  colorKey = "LemoineGreen"; }
            else                     { text = AppStrings.T("replaceLink.labels.verUpgrade", row.Version);  colorKey = "LemoineAccent"; }
            return Badge(text, colorKey);
        }

        private static Border Badge(string text, string colorKey)
        {
            var tb = new TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            var b = new Border
            {
                Padding = new Thickness(7, 2, 7, 2),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = tb,
            };
            b.SetResourceReference(Border.BorderBrushProperty, colorKey);
            return b;
        }

        // ══════════════════════ Step 2: Destination ════════════════════════════
        private FrameworkElement BuildDestStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(Label(AppStrings.T("replaceLink.labels.destQuestion")));
            outer.Children.Add(Dim(AppStrings.T("replaceLink.labels.destHint")));
            _destContainer = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            outer.Children.Add(_destContainer);
            RebuildDestCards();
            return outer;
        }

        private void RebuildDestCards()
        {
            if (_destContainer == null) return;
            _destContainer.Children.Clear();

            bool overwrite = _dest == ReplaceDestination.OverwriteLinkedFile;
            _destContainer.Children.Add(BuildCard(
                selected: overwrite,
                title: AppStrings.T("replaceLink.labels.optOverwriteTitle"),
                desc:  AppStrings.T("replaceLink.labels.optOverwriteDesc"),
                onClick: () => { SetDestination(ReplaceDestination.OverwriteLinkedFile); },
                extra: overwrite ? BuildOverwriteExtra() : null));

            bool rename = _dest == ReplaceDestination.RenameBesideIt;
            _destContainer.Children.Add(BuildCard(
                selected: rename,
                title: AppStrings.T("replaceLink.labels.optRenameTitle"),
                desc:  AppStrings.T("replaceLink.labels.optRenameDesc"),
                onClick: () => { SetDestination(ReplaceDestination.RenameBesideIt); },
                extra: rename ? Dim(AppStrings.T("replaceLink.labels.renameNote")) : null));

            bool folder = _dest == ReplaceDestination.SelectedFolder;
            _destContainer.Children.Add(BuildCard(
                selected: folder,
                title: AppStrings.T("replaceLink.labels.optFolderTitle"),
                desc:  AppStrings.T("replaceLink.labels.optFolderDesc"),
                onClick: () => { SetDestination(ReplaceDestination.SelectedFolder); },
                extra: folder ? BuildFolderExtra() : null));
        }

        // Changing the destination changes what a row in step 1 shows (the save-as name box
        // only applies to the two non-overwrite destinations), so step 1's content is rebuilt
        // through IStepAware rather than left stale.
        private void SetDestination(ReplaceDestination dest)
        {
            if (_dest == dest) return;
            _dest = dest;
            RebuildDestCards();
            _refreshStep?.Invoke("links");
            Changed();
        }

        private FrameworkElement BuildOverwriteExtra()
        {
            var panel = new StackPanel();
            panel.Children.Add(Warn(AppStrings.T("replaceLink.labels.optOverwriteWarn")));
            // Overwriting a workshared CENTRAL orphans every local copy made from it — a
            // coordination decision, so it is surfaced before the run rather than in the log.
            if (_rows.Any(r => r.IsWorkshared))
                panel.Children.Add(Warn(AppStrings.T("replaceLink.labels.optOverwriteCentralWarn")));
            return panel;
        }

        private FrameworkElement BuildFolderExtra()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var picker = new FolderBrowser
            {
                Label       = AppStrings.T("replaceLink.labels.saveLocationLabel"),
                Path        = _selectedFolder,
                DialogTitle = AppStrings.T("replaceLink.labels.saveLocationDialogTitle"),
            };
            picker.PathChanged += p => { _selectedFolder = p ?? ""; Changed(); };
            panel.Children.Add(picker);
            return panel;
        }

        // ══════════════════════ Step 3: Position ═══════════════════════════════
        private FrameworkElement BuildPositionStep()
        {
            var outer = new StackPanel();
            outer.Children.Add(Label(AppStrings.T("replaceLink.labels.posQuestion")));
            outer.Children.Add(Dim(AppStrings.T("replaceLink.labels.posHint")));
            _posContainer = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            outer.Children.Add(_posContainer);
            RebuildPositionCards();
            return outer;
        }

        private void RebuildPositionCards()
        {
            if (_posContainer == null) return;
            _posContainer.Children.Clear();

            _posContainer.Children.Add(BuildCard(
                selected: _position == ReplacePosition.KeepPlacement,
                title: AppStrings.T("replaceLink.labels.optKeepTitle"),
                desc:  AppStrings.T("replaceLink.labels.optKeepDesc"),
                onClick: () => { _position = ReplacePosition.KeepPlacement; RebuildPositionCards(); Changed(); },
                extra: null));

            _posContainer.Children.Add(BuildCard(
                selected: _position == ReplacePosition.ProjectBasePoint,
                title: AppStrings.T("replaceLink.labels.optPbpTitle"),
                desc:  AppStrings.T("replaceLink.labels.optPbpDesc"),
                onClick: () => { _position = ReplacePosition.ProjectBasePoint; RebuildPositionCards(); Changed(); },
                extra: null));

            _posContainer.Children.Add(BuildCard(
                selected: _position == ReplacePosition.SurveyPoint,
                title: AppStrings.T("replaceLink.labels.optSurveyTitle"),
                desc:  AppStrings.T("replaceLink.labels.optSurveyDesc"),
                onClick: () => { _position = ReplacePosition.SurveyPoint; RebuildPositionCards(); Changed(); },
                extra: null));

            if (_position != ReplacePosition.KeepPlacement)
                _posContainer.Children.Add(Dim(AppStrings.T("replaceLink.labels.posNote")));

            var toggles = new ToggleSwitches { Margin = new Thickness(0, 12, 0, 0) };
            toggles.SetItems(new List<ToggleItem>
            {
                new ToggleItem
                {
                    Id = "movement",
                    Label = AppStrings.T("replaceLink.labels.movementLabel"),
                    Desc  = AppStrings.T("replaceLink.labels.movementDesc"),
                    DefaultOn = _movement,
                },
            });
            toggles.StateChanged += st => { if (st.TryGetValue("movement", out var m)) { _movement = m; Changed(); } };
            _posContainer.Children.Add(toggles);
        }

        // ── Shared card ────────────────────────────────────────────────────────
        private FrameworkElement BuildCard(bool selected, string title, string desc, Action onClick, FrameworkElement? extra)
        {
            var content = new StackPanel();
            var titleTb = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            titleTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            titleTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            content.Children.Add(titleTb);
            content.Children.Add(Dim(desc));
            if (selected && extra != null) content.Children.Add(extra);

            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(14, 12, 14, 12),
                Margin          = new Thickness(0, 0, 0, 10),
                Cursor          = Cursors.Hand,
                Child           = content,
            };
            // A solid (or transparent) background is required for the whole card to be
            // hit-testable — a null background only hits the rendered text (CLAUDE.md).
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

        // ── Review ─────────────────────────────────────────────────────────────
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("links",    AppStrings.T("replaceLink.review.itemLinks")),
            ("dest",     AppStrings.T("replaceLink.review.itemDest")),
            ("position", AppStrings.T("replaceLink.review.itemPosition")),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["links"]    = RunnableCount() == 0
                ? AppStrings.T("replaceLink.review.linksNone")
                : AppStrings.T("replaceLink.review.linksValue", RunnableCount(), UpgradeCount()),
            ["dest"]     = DestSummary(),
            ["position"] = PositionLabel(_position),
        };

        public IList<string>? ReviewChips => new List<string>
        {
            AppStrings.T("replaceLink.review.chipBackup")   + (_backup   ? " ✓" : " ✗"),
            AppStrings.T("replaceLink.review.chipAudit")    + (_audit    ? " ✓" : " ✗"),
            AppStrings.T("replaceLink.review.chipMovement") + (_movement ? " ✓" : " ✗"),
        };

        public string? ReviewNote => null;

        public string? ReviewWarning
        {
            get
            {
                if (RunnableCount() == 0) return AppStrings.T("replaceLink.review.warnNoLinks");

                int incomplete = _rows.Count(r => !IsComplete(r));
                if (incomplete > 0) return AppStrings.T("replaceLink.review.warnIncomplete", incomplete);

                int tooNew = _rows.Count(r => IsComplete(r) && r.IsFutureVersion);
                if (tooNew > 0) return AppStrings.T("replaceLink.review.warnTooNew", tooNew);

                int unreadable = _rows.Count(r => IsComplete(r) && r.Scanned && !r.Readable && !r.IsFutureVersion);
                if (unreadable > 0) return AppStrings.T("replaceLink.review.warnUnreadable", unreadable);

                if (_dest == ReplaceDestination.OverwriteLinkedFile)
                {
                    return _backup
                        ? AppStrings.T("replaceLink.review.warnOverwrite", RunnableCount())
                        : AppStrings.T("replaceLink.review.warnOverwriteNoBackup", RunnableCount());
                }
                return null;
            }
        }

        // ── Validation / Summary ───────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                // Invalid while the version scan is in flight — a not-yet-scanned row defaults to
                // readable, so running early could include a file the scan would have flagged.
                case "links":    return !_scanning && RunnableCount() > 0;
                case "dest":     return _dest != ReplaceDestination.SelectedFolder || !string.IsNullOrWhiteSpace(_selectedFolder);
                case "position": return true;
                default:         return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "links":
                    return RunnableCount() == 0
                        ? AppStrings.T("replaceLink.summaries.linksEmpty")
                        : AppStrings.T("replaceLink.summaries.links", RunnableCount());
                case "dest":     return DestSummary();
                case "position": return PositionLabel(_position);
                case "run":      return AppStrings.T("replaceLink.summaries.run");
                default:         return "—";
            }
        }

        private string DestSummary()
        {
            switch (_dest)
            {
                case ReplaceDestination.RenameBesideIt: return AppStrings.T("replaceLink.summaries.destRename");
                case ReplaceDestination.SelectedFolder: return AppStrings.T("replaceLink.summaries.destFolder", _selectedFolder);
                default:                                return AppStrings.T("replaceLink.summaries.destOverwrite");
            }
        }

        // A row runs only when it names both ends and the new file is usable — Revit cannot open
        // a file saved in a later release, so a future-version row is excluded like an unreadable one.
        private static bool IsComplete(ReplaceRow r) => r.TypeId != 0 && !string.IsNullOrEmpty(r.NewFilePath);
        private static bool IsRunnable(ReplaceRow r) => IsComplete(r) && r.Readable && !r.IsFutureVersion;

        private int RunnableCount() => _rows.Count(IsRunnable);
        private int UpgradeCount()  => _rows.Count(r => IsRunnable(r) && !r.IsCurrent);

        // ── Run ────────────────────────────────────────────────────────────────
        public void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null)
            {
                pushLog(AppStrings.T("replaceLink.log.handlerMissing"), "fail");
                onComplete(0, 1, 0);
                return;
            }

            SaveSettings();

            _runHandler.Spec = new ReplaceLinkSpec
            {
                Items = _rows.Where(IsRunnable).Select(r => new ReplaceItem
                {
                    TypeId      = r.TypeId,
                    LinkName    = r.LinkName,
                    NewFilePath = r.NewFilePath,
                    SaveAsName  = r.SaveAsName,
                }).ToList(),
                Destination    = _dest,
                Position       = _position,
                SelectedFolder = _selectedFolder,
                BackupOriginal = _backup,
                AuditOnOpen    = _audit,
                ReportMovement = _movement,
            };
            _runHandler.PushLog    = pushLog;
            _runHandler.OnProgress = onProgress;
            _runHandler.OnComplete = onComplete;

            pushLog(AppStrings.T("replaceLink.log.raising"), "info");
            _runEvent.Raise();
        }

        private void SaveSettings()
        {
            var s = ReplaceLinkSettings.Instance;
            s.Destination    = _dest;
            s.Position       = _position;
            s.BackupOriginal = _backup;
            s.AuditOnOpen    = _audit;
            s.ReportMovement = _movement;
            s.Save();
        }

        // ── Position label ↔ enum ──────────────────────────────────────────────
        // internal (not private): reused by GlobalSettingsWindow's Setup tab for the persisted default.
        internal static readonly ReplacePosition[] PositionOrder =
        {
            ReplacePosition.KeepPlacement, ReplacePosition.ProjectBasePoint, ReplacePosition.SurveyPoint,
        };

        private static string PositionKey(ReplacePosition p)
        {
            switch (p)
            {
                case ReplacePosition.ProjectBasePoint: return "optPbpTitle";
                case ReplacePosition.SurveyPoint:      return "optSurveyTitle";
                default:                               return "optKeepTitle";
            }
        }

        internal static string PositionLabel(ReplacePosition p) => AppStrings.T("replaceLink.labels." + PositionKey(p));
        internal static List<string> PositionLabels() => PositionOrder.Select(PositionLabel).ToList();

        // Resolved per call (never a cached static dictionary): a static map keyed by display
        // labels freezes the first-touched language, so after a language switch every pick in a
        // newly opened window would silently miss the lookup.
        internal static bool TryLabelToPosition(string? label, out ReplacePosition position)
        {
            foreach (var p in PositionOrder)
            {
                if (string.Equals(PositionLabel(p), label, StringComparison.Ordinal))
                {
                    position = p;
                    return true;
                }
            }
            position = ReplacePosition.KeepPlacement;
            return false;
        }

        // ── Small WPF helpers (theme via resource refs only) ────────────────────
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

        private static TextBlock Sub(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 7, 0, 5) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }

        private static TextBlock Mono(string text)
        {
            var tb = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
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
