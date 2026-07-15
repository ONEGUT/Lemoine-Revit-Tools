using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.Setup
{
    /// <summary>Web port of <see cref="UpgradeLinksViewModel"/> — queue Revit files, upgrade
    /// and link them. Same scan/run handlers, settings, and AppStrings keys. File rows become
    /// dynamic inputs ("name_i"/"plc_i"/"rm_i"); Add Files opens the native multiselect dialog
    /// from IWebToolAction (window thread); the Cloud per-file pause maps to IWebRunPausable.
    /// The WPF destination cards flatten to a single-select list.</summary>
    public sealed class UpgradeLinksWebTool : WebToolBase, IWebToolCleanup, IWebStepRefresh, IWebToolAction, IWebRunPausable
    {
        private readonly UpgradeLinksScanHandler? _scanHandler;
        private readonly ExternalEvent?           _scanEvent;
        private readonly UpgradeLinksRunHandler?  _runHandler;
        private readonly ExternalEvent?           _runEvent;
        private readonly string?                  _hostFolder;
        private readonly bool                     _hostIsCloud;

        private readonly List<UpgradeFileRow> _rows = new List<UpgradeFileRow>();
        private UpgradeDestination _dest = UpgradeLinksSettings.Instance.Destination;
        private string             _selectedFolder = "";
        private bool               _audit  = UpgradeLinksSettings.Instance.AuditOnOpen;
        private bool               _reload = UpgradeLinksSettings.Instance.ReloadExisting;
        private readonly UpgradePlacement _defaultPlacement = UpgradeLinksSettings.Instance.DefaultPlacement;
        private UpgradePlacement?  _setAllSelection;
        private bool               _scanning;

        public event Action<string>? StepInputsChanged;

        // ── IWebRunPausable — Cloud mode pauses per file on Revit's native Save dialog ──
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

        public UpgradeLinksWebTool(
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

        public override string Title    => AppStrings.T("upgradeLinks.title");
        public override string RunLabel => AppStrings.T("upgradeLinks.runLabel");

        // ── Steps ─────────────────────────────────────────────────────────────

        public override IReadOnlyList<WebStep> BuildSteps()
        {
            var files = new WebStep("files", AppStrings.T("upgradeLinks.steps.files"))
                .Add(WebInput.Hint("filesHint", AppStrings.T("upgradeLinks.labels.filesHint")));
            BuildFileRows(files);

            var dest = new WebStep("dest", AppStrings.T("upgradeLinks.steps.dest"))
                .Add(WebInput.Hint("destHint", AppStrings.T("upgradeLinks.labels.destHint")));
            var destOptions = new List<WebOption>
            {
                new WebOption("selectedFolder",  AppStrings.T("upgradeLinks.labels.optSelectedFolderTitle")),
                new WebOption("currentLocation", AppStrings.T("upgradeLinks.labels.optCurrentLocationTitle")),
            };
            if (_hostIsCloud)
                destOptions.Add(new WebOption("cloud", AppStrings.T("upgradeLinks.labels.optCloudTitle")));
            dest.Add(WebInput.SingleSelect("dest", AppStrings.T("upgradeLinks.labels.destQuestion"),
                DestToken(_dest), destOptions));

            switch (_dest)
            {
                case UpgradeDestination.SelectedFolder:
                    dest.Add(WebInput.Hint("selFolderDesc", AppStrings.T("upgradeLinks.labels.optSelectedFolderDesc")));
                    dest.Add(WebInput.FolderBrowser("folder",
                        AppStrings.T("upgradeLinks.labels.saveLocationLabel"), _selectedFolder));
                    break;
                case UpgradeDestination.CurrentLocation:
                    dest.Add(WebInput.Hint("curLocDesc", AppStrings.T("upgradeLinks.labels.optCurrentLocationDesc")));
                    dest.Add(WebInput.Warn("overwriteWarn", AppStrings.T("upgradeLinks.labels.optOverwriteWarn")));
                    dest.Add(WebInput.Hint("overwriteRename", AppStrings.T("upgradeLinks.labels.optOverwriteRenameNote")));
                    break;
                case UpgradeDestination.Cloud:
                    dest.Add(WebInput.Hint("cloudDesc", AppStrings.T("upgradeLinks.labels.optCloudDesc")));
                    dest.Add(WebInput.Hint("cloudNote", AppStrings.T("upgradeLinks.labels.optCloudNote")));
                    break;
            }

            var run = new WebStep("run", AppStrings.T("upgradeLinks.steps.run"), required: false)
                .Add(WebInput.Review("review", new[]
                {
                    (AppStrings.T("upgradeLinks.review.itemFiles"),
                     ReadableCount() == 0 ? AppStrings.T("upgradeLinks.review.filesNone")
                                          : AppStrings.T("upgradeLinks.review.filesValue", ReadableCount(), UpgradeCount())),
                    (AppStrings.T("upgradeLinks.review.itemPlacement"), PlacementSummary()),
                    (AppStrings.T("upgradeLinks.review.itemDest"),      DestSummary()),
                },
                warning: ReviewWarning()));

            return new List<WebStep> { files, dest, run };
        }

        private void BuildFileRows(WebStep files)
        {
            if (_scanning)
                files.Add(WebInput.Hint("scanning", AppStrings.T("upgradeLinks.labels.scanning")));

            if (_rows.Count == 0)
            {
                files.Add(WebInput.Hint("empty", AppStrings.T("upgradeLinks.labels.empty")));
            }
            else
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    files.Add(WebInput.TextField($"name_{i}",
                        $"{AppStrings.T("upgradeLinks.labels.colFile")} — {row.Folder}", row.SaveAsName));
                    files.Add(WebInput.Hint($"ver_{i}",
                        AppStrings.T("upgradeLinks.labels.colVersion") + ": " + VersionText(row)));
                    if (Usable(row))
                        files.Add(WebInput.SingleSelect($"plc_{i}",
                            AppStrings.T("upgradeLinks.labels.colPlacement"),
                            UpgradeLinksViewModel.PlacementLabel(row.Placement),
                            UpgradeLinksViewModel.PlacementLabels().Select(l => new WebOption(l, l))));
                    files.Add(WebInput.Button($"rm_{i}",
                        "×  " + System.IO.Path.GetFileName(row.Path), variant: "ghost"));
                }

                int unreadable = _rows.Count(r => !r.Readable && !r.IsFutureVersion && r.Scanned);
                if (unreadable > 0) files.Add(WebInput.Warn("unreadableNote", AppStrings.T("upgradeLinks.labels.unreadableNote", unreadable)));
                int tooNew = _rows.Count(r => r.IsFutureVersion);
                if (tooNew > 0) files.Add(WebInput.Warn("tooNewNote", AppStrings.T("upgradeLinks.labels.tooNewNote", tooNew)));

                string curVer = _scanHandler?.CurrentVersionNumber ?? "";
                files.Add(WebInput.Hint("futureVersionNote", string.IsNullOrEmpty(curVer)
                    ? AppStrings.T("upgradeLinks.labels.futureVersionNoteGeneric")
                    : AppStrings.T("upgradeLinks.labels.futureVersionNote", curVer)));
            }

            files.Add(WebInput.Button("addFiles", AppStrings.T("upgradeLinks.labels.addFiles"), variant: "primary"));
            if (_rows.Count > 0)
            {
                files.Add(WebInput.Button("clearList", AppStrings.T("upgradeLinks.labels.clearList"), variant: "ghost"));
                files.Add(WebInput.SingleSelect("setAll", AppStrings.T("upgradeLinks.labels.setAll"),
                    UpgradeLinksViewModel.PlacementLabel(_setAllSelection ?? _defaultPlacement),
                    UpgradeLinksViewModel.PlacementLabels().Select(l => new WebOption(l, l))));
                files.Add(WebInput.Toggle("audit", AppStrings.T("upgradeLinks.labels.auditLabel"), _audit));
                files.Add(WebInput.Hint("auditDesc", AppStrings.T("upgradeLinks.labels.auditDesc")));
                files.Add(WebInput.Toggle("reload", AppStrings.T("upgradeLinks.labels.reloadLabel"), _reload));
                files.Add(WebInput.Hint("reloadDesc", AppStrings.T("upgradeLinks.labels.reloadDesc")));
            }
        }

        private string VersionText(UpgradeFileRow row)
        {
            if (row.IsFutureVersion) return AppStrings.T("upgradeLinks.labels.verTooNew", row.Version);
            if (!row.Readable && row.Scanned) return AppStrings.T("upgradeLinks.labels.verUnreadable");
            if (!row.Scanned)   return AppStrings.T("upgradeLinks.labels.verUnknown");
            if (row.IsCurrent)  return AppStrings.T("upgradeLinks.labels.verCurrent", row.Version);
            return AppStrings.T("upgradeLinks.labels.verUpgrade", row.Version);
        }

        // ── Action buttons ────────────────────────────────────────────────────

        public void OnToolAction(string stepId, string inputId)
        {
            if (inputId == "addFiles") { OnAddFiles(); return; }
            if (inputId == "clearList")
            {
                _rows.Clear();
                StepInputsChanged?.Invoke("files");
                Fire();
                return;
            }
            if (inputId.StartsWith("rm_", StringComparison.Ordinal)
                && int.TryParse(inputId.Substring(3), out var idx)
                && idx >= 0 && idx < _rows.Count)
            {
                _rows.RemoveAt(idx);
                StepInputsChanged?.Invoke("files");
                Fire();
            }
        }

        // Runs on the window's UI thread (OnToolAction), so the native dialog is safe here —
        // same pattern as the WPF twin's Add Files button.
        private void OnAddFiles()
        {
            string[]? picked = null;
            try
            {
                using var dlg = new System.Windows.Forms.OpenFileDialog
                {
                    Multiselect = true,
                    Filter      = "Revit files (*.rvt)|*.rvt",
                    Title       = AppStrings.T("upgradeLinks.title"),
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) picked = dlg.FileNames;
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("UpgradeLinksWebTool: open file dialog", ex); return; }
            if (picked == null || picked.Length == 0) return;

            var existing = new HashSet<string>(_rows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            var added = new List<UpgradeFileRow>();
            foreach (var p in picked)
            {
                if (string.IsNullOrWhiteSpace(p) || !existing.Add(p)) continue;
                var row = new UpgradeFileRow
                {
                    Path = p,
                    Placement = _setAllSelection ?? _defaultPlacement,
                    SaveAsName = System.IO.Path.GetFileNameWithoutExtension(p),
                };
                _rows.Add(row); added.Add(row);
            }
            if (added.Count == 0) return;

            StepInputsChanged?.Invoke("files");
            Fire();
            ScanNewRows(added);
        }

        private void ScanNewRows(List<UpgradeFileRow> rows)
        {
            if (_scanHandler == null || _scanEvent == null) return;
            _scanning = true;
            StepInputsChanged?.Invoke("files");

            _scanHandler.Paths = rows.Select(r => r.Path).ToList();
            // Callbacks land on the Revit thread; the window marshals StepInputsChanged.
            _scanHandler.OnScanned = results =>
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
                StepInputsChanged?.Invoke("files");
                Fire();
            };
            _scanHandler.OnError = err =>
            {
                _scanning = false;
                DiagnosticsLog.Warn("UpgradeLinksWebTool: scan error", err ?? "");
                StepInputsChanged?.Invoke("files");
                Fire();
            };
            _scanEvent.Raise();
        }

        // ── State ─────────────────────────────────────────────────────────────

        private static string DestToken(UpgradeDestination d) => d switch
        {
            UpgradeDestination.CurrentLocation => "currentLocation",
            UpgradeDestination.Cloud           => "cloud",
            _                                  => "selectedFolder",
        };

        public override void OnState(string stepId, string inputId, object? value)
        {
            switch (inputId)
            {
                case "dest":
                {
                    var d = AsString(value) switch
                    {
                        "currentLocation" => UpgradeDestination.CurrentLocation,
                        "cloud"           => _hostIsCloud ? UpgradeDestination.Cloud : UpgradeDestination.SelectedFolder,
                        _                 => UpgradeDestination.SelectedFolder,
                    };
                    if (d != _dest)
                    {
                        _dest = d;
                        StepInputsChanged?.Invoke("dest");
                        Fire();
                    }
                    return;
                }
                case "folder": _selectedFolder = AsString(value, _selectedFolder); Fire(); return;
                case "setAll":
                {
                    if (UpgradeLinksViewModel.LabelToPlacement.TryGetValue(AsString(value), out var p))
                    {
                        _setAllSelection = p;
                        foreach (var r in _rows) r.Placement = p;
                        StepInputsChanged?.Invoke("files");
                        Fire();
                    }
                    return;
                }
                case "audit":  _audit  = AsBool(value, _audit);  return;
                case "reload": _reload = AsBool(value, _reload); return;
            }

            // Per-row inputs: name_i / plc_i
            if (inputId.StartsWith("name_", StringComparison.Ordinal)
                && int.TryParse(inputId.Substring(5), out var ni)
                && ni >= 0 && ni < _rows.Count)
            {
                _rows[ni].SaveAsName = AsString(value, _rows[ni].SaveAsName);
                Fire();
                return;
            }
            if (inputId.StartsWith("plc_", StringComparison.Ordinal)
                && int.TryParse(inputId.Substring(4), out var pi)
                && pi >= 0 && pi < _rows.Count)
            {
                if (UpgradeLinksViewModel.LabelToPlacement.TryGetValue(AsString(value), out var p))
                {
                    _rows[pi].Placement = p;
                    Fire();
                }
            }
        }

        // ── Validation / summaries ────────────────────────────────────────────

        private static bool Usable(UpgradeFileRow r) => r.Readable && !r.IsFutureVersion;
        private int ReadableCount() => _rows.Count(Usable);
        private int UpgradeCount()  => _rows.Count(r => Usable(r) && !r.IsCurrent);

        private bool DestValid()
        {
            switch (_dest)
            {
                case UpgradeDestination.SelectedFolder: return !string.IsNullOrWhiteSpace(_selectedFolder);
                case UpgradeDestination.Cloud:          return _hostIsCloud;
                default:                                return true;
            }
        }

        public override bool IsStepValid(string stepId)
        {
            switch (stepId)
            {
                case "files": return ReadableCount() > 0;
                case "dest":  return DestValid();
                default:      return true;
            }
        }

        public override bool CanRun() => ReadableCount() > 0 && DestValid();

        public override string SummaryFor(string stepId)
        {
            switch (stepId)
            {
                case "files":
                    return _rows.Count == 0 ? AppStrings.T("upgradeLinks.summaries.filesEmpty")
                        : AppStrings.T("upgradeLinks.summaries.files", ReadableCount(), UpgradeCount());
                case "dest": return DestSummary();
                case "run":  return AppStrings.T("upgradeLinks.summaries.run");
                default:     return "-";
            }
        }

        private string DestSummary()
        {
            switch (_dest)
            {
                case UpgradeDestination.CurrentLocation: return AppStrings.T("upgradeLinks.summaries.destCurrentLocation");
                case UpgradeDestination.Cloud:           return AppStrings.T("upgradeLinks.summaries.destCloud");
                default:                                 return AppStrings.T("upgradeLinks.summaries.destSelectedFolder", _selectedFolder);
            }
        }

        private string PlacementSummary()
        {
            var readable = _rows.Where(Usable).ToList();
            if (readable.Count == 0) return "-";
            var distinct = readable.Select(r => r.Placement).Distinct().ToList();
            return distinct.Count == 1 ? UpgradeLinksViewModel.PlacementLabel(distinct[0]) : AppStrings.T("upgradeLinks.review.placementMixed");
        }

        private string? ReviewWarning()
        {
            if (ReadableCount() == 0) return AppStrings.T("upgradeLinks.review.warnNoFiles");
            int tooNew = _rows.Count(r => r.IsFutureVersion);
            if (tooNew > 0) return AppStrings.T("upgradeLinks.review.warnTooNew", tooNew);
            int unreadable = _rows.Count(r => !r.Readable && !r.IsFutureVersion && r.Scanned);
            if (unreadable > 0) return AppStrings.T("upgradeLinks.review.warnUnreadable", unreadable);
            if (_dest == UpgradeDestination.CurrentLocation) return AppStrings.T("upgradeLinks.review.warnOverwrite");
            return null;
        }

        // ── Run ───────────────────────────────────────────────────────────────

        public override void Run(Action<string, string> pushLog, Action<int, int, int, int> onProgress, Action<int, int, int> onComplete)
        {
            if (_runHandler == null || _runEvent == null) { pushLog(AppStrings.T("upgradeLinks.log.handlerMissing"), "fail"); onComplete(0, 1, 0); return; }

            var s = UpgradeLinksSettings.Instance;
            s.AuditOnOpen    = _audit;
            s.ReloadExisting = _reload;
            s.Save();

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

        public void OnWindowClosed()
        {
            if (_scanHandler != null) { _scanHandler.OnScanned = null; _scanHandler.OnError = null; }
            if (_runHandler  != null)
            {
                _runHandler.PushLog = null; _runHandler.OnProgress = null; _runHandler.OnComplete = null;
                _runHandler.OnAwaitingUser = null;
            }
        }
    }
}
