using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// Upgrade &amp; Link Models run. Local destinations (CurrentLocation / SelectedFolder) process
    /// strictly one file at a time within a single <see cref="Execute"/> call: open (which upgrades
    /// the file in memory — background <see cref="Application.OpenDocumentFile"/>, never an activated
    /// view) → save → close → link into the host. Cloud is different: Revit's native "Save As Cloud
    /// Model" dialog is posted (<see cref="UIApplication.PostCommand"/>), which only runs after this
    /// method returns, so Cloud spans MULTIPLE Execute() calls — one per file, paused in between on
    /// the user via <see cref="IRunPausable"/> (Continue/Skip). See <see cref="ProcessNextCloudFile"/>.
    /// </summary>
    public sealed class UpgradeLinksRunHandler : IExternalEventHandler
    {
        // ── Inputs (set before Raise) ─────────────────────────────────────────
        public UpgradeLinksSpec Spec { get; set; } = new UpgradeLinksSpec();
        public string? HostFolder { get; set; }   // folder of the active document (SelectedFolder default)

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }
        // (isAwaiting, continueLabel, skipLabel) — see IRunPausable.
        public Action<bool, string?, string?>? OnAwaitingUser { get; set; }

        // Set by the ViewModel's IRunPausable.ContinueRun()/SkipCurrentItem(), then the
        // SAME ExternalEvent is raised again — Execute() sees _cloudActive and resumes here.
        public bool CloudContinueRequested { get; set; }
        public bool CloudSkipRequested     { get; set; }

        // Set by the ViewModel's OnWindowClosed when the window closes during a Cloud pause
        // (then the event is raised once more). Without this, the stale _cloudActive state
        // would swallow every future run of the tool for the rest of the Revit session and
        // leave the paused upgrade document open.
        public bool CloudAbortRequested    { get; set; }

        /// <summary>True while a Cloud run is paused/spanning Execute() calls — the ViewModel
        /// checks this on window close to know whether a cloud abort needs to be raised.</summary>
        public bool IsCloudRunActive => _cloudActive;

        // ── Cloud continuation state — spans multiple Execute() calls for one Cloud run ─────
        private bool                   _cloudActive;
        private List<UpgradeFileItem>  _cloudFiles = new List<UpgradeFileItem>();
        private int                    _cloudIndex;
        private int                    _cloudPass, _cloudFail, _cloudSkip;
        private Document?              _cloudHostDoc;
        private Document?              _cloudWaitDoc;
        private string                 _cloudWaitFileName = "";
        private string                 _cloudWaitBaseName = "";
        private UpgradePlacement       _cloudWaitPlacement;
        private long                   _cloudIssues0;

        // Host model extents centre for Center-to-Center placement — computed once per run, reused
        // across every file, and reset at each run start (the active host doc can differ per run).
        private XYZ?                   _hostModelCenter;
        private bool                   _hostCenterComputed;

        public string GetName() => "LemoineTools.Tools.Setup.UpgradeLinksRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        public void Execute(UIApplication app)
        {
            if (_cloudActive)
            {
                // A raise with neither Continue nor Skip set is either an explicit abort (window
                // closed mid-pause) or a brand-new run started after the old window was closed
                // mid-pause (fresh Spec, no flags). Both mean the paused cloud run is dead —
                // clean it up instead of letting the stale state swallow the event forever.
                bool freshRun = !CloudContinueRequested && !CloudSkipRequested &&
                                (Spec.Files?.Count ?? 0) > 0;
                if (CloudAbortRequested || freshRun)
                {
                    CloudAbortRequested = false;
                    AbortStaleCloudRun(app);
                    if (!freshRun)
                    {
                        // Abort-only (window closed) — nothing new to run.
                        Spec = new UpgradeLinksSpec();
                        HostFolder = null;
                        return;
                    }
                    // Fall through: Spec holds the fresh run's payload — start it below.
                }
                else
                {
                    try { ContinueCloudRun(app); }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Error("UpgradeLinksRunHandler.ContinueCloudRun", ex);
                        Log(AppStrings.T("upgradeLinks.log.aborted", ex.Message), "fail");
                        int p = _cloudPass, f = _cloudFail + 1, s = _cloudSkip;
                        _cloudActive = false;
                        CloseCloudWaitDoc(app);
                        OnAwaitingUser?.Invoke(false, null, null);
                        Spec = new UpgradeLinksSpec();
                        HostFolder = null;
                        OnComplete?.Invoke(p, f, s);
                    }
                    return;
                }
            }
            CloudAbortRequested = false;   // stale flag from a close with no cloud run in flight
            _hostCenterComputed = false;   // recompute the host extents centre for this run

            int pass = 0, fail = 0, skip = 0;
            long issues0 = DiagnosticsLog.IssueCount;
            try
            {
                var hostDoc = app.ActiveUIDocument?.Document;
                if (hostDoc == null) { Log(AppStrings.T("upgradeLinks.log.noDoc"), "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                var files = Spec.Files ?? new List<UpgradeFileItem>();
                if (files.Count == 0) { Log(AppStrings.T("upgradeLinks.log.noFiles"), "warn"); OnComplete?.Invoke(0, 0, 0); return; }

                if (Spec.Destination == UpgradeDestination.Cloud)
                {
                    if (!Spec.CloudReady)
                    {
                        Log(AppStrings.T("upgradeLinks.log.cloudNotReady"), "fail");
                        OnComplete?.Invoke(0, 1, 0); return;
                    }
                    StartCloudRun(app, hostDoc, files);
                    return;   // continues asynchronously — see ContinueCloudRun
                }

                // Resolve the SelectedFolder destination up front so a bad path fails before any file opens.
                string? destFolder = null;
                if (Spec.Destination == UpgradeDestination.SelectedFolder)
                {
                    string folder = string.IsNullOrWhiteSpace(Spec.SelectedFolder) ? (HostFolder ?? "") : Spec.SelectedFolder;
                    if (string.IsNullOrWhiteSpace(folder)) { Log(AppStrings.T("upgradeLinks.log.noSelectedFolder"), "fail"); OnComplete?.Invoke(0, 1, 0); return; }
                    destFolder = folder;
                    try { Directory.CreateDirectory(destFolder); }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Error("UpgradeLinks: create selected folder", ex);
                        Log(AppStrings.T("upgradeLinks.log.subfolderFail", destFolder, ex.Message), "fail");
                        OnComplete?.Invoke(0, 1, 0); return;
                    }
                }

                Log(AppStrings.T("upgradeLinks.log.start", files.Count, DestLabel(destFolder)), "info");

                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var appApp    = app.Application;
                int total = files.Count, done = 0;

                foreach (var item in files)
                {
                    if (RunState.CancelRequested)
                    {
                        Log(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                        break;   // committed links + saved files already preserved
                    }

                    done++;
                    string srcPath  = item.Path;
                    string fileName = Path.GetFileName(srcPath);
                    string baseName = Path.GetFileNameWithoutExtension(srcPath);

                    if (!File.Exists(srcPath))
                    {
                        skip++;
                        Log(AppStrings.T("upgradeLinks.log.missing", fileName), "warn");
                        Progress(done, total, pass, fail, skip);
                        continue;
                    }

                    // CurrentLocation always saves back to the file's own original path, so a rename
                    // would leave the original untouched at a name it no longer matches — ignored
                    // there (per the note on the Current location destination card).
                    string effectiveBaseName = baseName;
                    if (Spec.Destination != UpgradeDestination.CurrentLocation)
                    {
                        string requested = string.IsNullOrWhiteSpace(item.SaveAsName) ? baseName : item.SaveAsName.Trim();
                        string sanitized = SanitizeBaseName(requested);
                        if (sanitized.Length == 0 || !sanitized.Any(char.IsLetterOrDigit))
                        {
                            // A resolved name with no alphanumeric character is a failure, not a
                            // silent fallback — report it before substituting the original name.
                            Log(AppStrings.T("upgradeLinks.log.renameInvalid", fileName), "warn");
                            DiagnosticsLog.Warn("UpgradeLinks: rename resolved to an unusable name", $"{fileName} -> '{requested}'");
                        }
                        else
                        {
                            effectiveBaseName = sanitized;
                        }
                    }

                    Document? linkDoc = null;
                    try
                    {
                        var srcMp = ModelPathUtils.ConvertUserVisiblePathToModelPath(srcPath);

                        bool isWs = false;
                        try { var bfi = BasicFileInfo.Extract(srcPath); isWs = bfi != null && bfi.IsWorkshared; }
                        catch (Exception ex) { DiagnosticsLog.Swallowed("UpgradeLinks: BasicFileInfo at run", ex); }

                        var oo = new OpenOptions { Audit = Spec.AuditOnOpen };
                        if (isWs)
                        {
                            // Detached + all worksets closed: the file's elements are never loaded into
                            // memory (the dominant RAM saver) yet are fully preserved on save.
                            oo.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                            oo.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets));
                        }

                        // A file currently loaded as a link in this host can't be opened as a
                        // standalone document (OpenDocumentFile hands back the linked doc, whose
                        // SaveAs then throws — same constraint Push Coordinates works around).
                        // Unload it first; the post-save LinkIntoHost reload re-points it.
                        UnloadIfCurrentlyLinked(hostDoc, srcPath, fileName);

                        // Opening upgrades the file in memory. Background open ONLY — an activated view
                        // pins its graphics in native RAM for the whole session (CLAUDE.md).
                        linkDoc = appApp.OpenDocumentFile(srcMp, oo);
                        if (linkDoc == null)
                        {
                            fail++;
                            Log(AppStrings.T("upgradeLinks.log.openFail", fileName), "fail");
                            Progress(done, total, pass, fail, skip);
                            continue;
                        }

                        string destPath = Spec.Destination == UpgradeDestination.CurrentLocation
                            ? srcPath
                            : Path.Combine(destFolder!, UniqueFileName(effectiveBaseName + Path.GetExtension(srcPath), usedNames));
                        // Overwriting a pre-existing file in the chosen folder is allowed (re-runs
                        // refresh the upgraded copies) but never silent.
                        if (Spec.Destination == UpgradeDestination.SelectedFolder && File.Exists(destPath))
                            Log(AppStrings.T("upgradeLinks.log.overwriteWarn", Path.GetFileName(destPath)), "warn");
                        SaveLocal(linkDoc, destPath, isWs);
                        var savedMp = ModelPathUtils.ConvertUserVisiblePathToModelPath(destPath);

                        // Close before linking — keeps at most the host (+ the link type Revit loads) in RAM.
                        try { linkDoc.Close(false); }
                        catch (Exception ex) { DiagnosticsLog.Swallowed("UpgradeLinks: close", ex); }
                        linkDoc = null;

                        var linkResult = LinkIntoHost(hostDoc, savedMp, effectiveBaseName, item.Placement);
                        if (linkResult == LinkResult.Linked)
                        {
                            pass++;
                            if (!string.Equals(effectiveBaseName, baseName, StringComparison.Ordinal))
                                Log(AppStrings.T("upgradeLinks.log.linkedRenamed", done, total, fileName, effectiveBaseName), "info");
                            else
                                Log(AppStrings.T("upgradeLinks.log.linked", done, total, fileName), "info");
                        }
                        else if (linkResult == LinkResult.SkippedExisting)
                        {
                            skip++;
                        }
                        else
                        {
                            fail++;
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        DiagnosticsLog.Error($"UpgradeLinks: process {fileName}", ex);
                        Log(AppStrings.T("upgradeLinks.log.fileFail", fileName, ex.GetType().Name, ex.Message), "fail");
                    }
                    finally
                    {
                        if (linkDoc != null)
                        {
                            try { linkDoc.Close(false); }
                            catch (Exception ex) { DiagnosticsLog.Swallowed("UpgradeLinks: close (finally)", ex); }
                        }
                    }

                    Progress(done, total, pass, fail, skip);
                }

                long issues = DiagnosticsLog.IssuesSince(issues0);
                if (issues > 0) Log(AppStrings.T("upgradeLinks.log.nonFatal", issues), "warn");
                Log(AppStrings.T("upgradeLinks.log.done", pass, skip, fail), fail > 0 ? "warn" : "pass");
                OnProgress?.Invoke(100, pass, fail, skip);
                OnComplete?.Invoke(pass, fail, skip);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("UpgradeLinksRunHandler.Execute", ex);
                Log(AppStrings.T("upgradeLinks.log.aborted", ex.Message), "fail");
                OnComplete?.Invoke(pass, fail + 1, skip);
            }
            finally
            {
                // Session-long static handler — drop the run's payload (CLAUDE.md memory discipline).
                // Skip while a Cloud run is still in flight across further Execute() calls; it clears
                // itself in FinishCloudRun once truly done.
                if (!_cloudActive)
                {
                    Spec = new UpgradeLinksSpec();
                    HostFolder = null;
                }
            }
        }

        // Cleans up a Cloud run that can no longer continue (window closed mid-pause, or a new
        // run superseding it). Closes the waiting document, drops the continuation state, and
        // tells any still-wired UI the pause is over. Logs to the run log when one is attached
        // (a fresh run's PushLog is already set) and always to diagnostics.
        private void AbortStaleCloudRun(UIApplication app)
        {
            DiagnosticsLog.Warn("UpgradeLinks: stale cloud run aborted",
                $"file {_cloudIndex} of {_cloudFiles.Count} was awaiting the user");
            Log(AppStrings.T("upgradeLinks.log.cloudStaleAborted"), "warn");
            _cloudActive = false;
            _cloudFiles  = new List<UpgradeFileItem>();
            CloseCloudWaitDoc(app);
            _cloudHostDoc = null;
            OnAwaitingUser?.Invoke(false, null, null);
        }

        // ── Cloud — native "Save As Cloud Model" per file, paused on the user between files ────
        private void StartCloudRun(UIApplication app, Document hostDoc, List<UpgradeFileItem> files)
        {
            _cloudActive    = true;
            _hostCenterComputed = false;   // recompute the host extents centre for this run
            _cloudHostDoc   = hostDoc;
            _cloudFiles     = files;
            _cloudIndex     = 0;
            _cloudPass = _cloudFail = _cloudSkip = 0;
            _cloudIssues0   = DiagnosticsLog.IssueCount;
            Log(AppStrings.T("upgradeLinks.log.start", files.Count, AppStrings.T("upgradeLinks.summaries.destCloud")), "info");
            ProcessNextCloudFile(app);
        }

        private void ProcessNextCloudFile(UIApplication app)
        {
            if (RunState.CancelRequested)
            {
                Log(AppStrings.T("common.log.stoppedByUser", _cloudIndex, _cloudFiles.Count), "warn");
                FinishCloudRun(app);
                return;
            }
            if (_cloudIndex >= _cloudFiles.Count) { FinishCloudRun(app); return; }

            var item = _cloudFiles[_cloudIndex];
            _cloudIndex++;
            string srcPath  = item.Path;
            string fileName = Path.GetFileName(srcPath);
            string baseName = Path.GetFileNameWithoutExtension(srcPath);

            if (!File.Exists(srcPath))
            {
                _cloudSkip++;
                Log(AppStrings.T("upgradeLinks.log.missing", fileName), "warn");
                Progress(_cloudIndex, _cloudFiles.Count, _cloudPass, _cloudFail, _cloudSkip);
                ProcessNextCloudFile(app);
                return;
            }

            // Cosmetic only — the file's actual saved name is whatever the user types in Revit's
            // native Save As Cloud Model dialog. This is just the name shown/used for the RunHandler's
            // own link-reload matching (ReloadExistingType) and log lines.
            string effectiveBaseName = baseName;
            string requested = string.IsNullOrWhiteSpace(item.SaveAsName) ? baseName : item.SaveAsName.Trim();
            string sanitized = SanitizeBaseName(requested);
            if (sanitized.Length == 0 || !sanitized.Any(char.IsLetterOrDigit))
            {
                Log(AppStrings.T("upgradeLinks.log.renameInvalid", fileName), "warn");
                DiagnosticsLog.Warn("UpgradeLinks: rename resolved to an unusable name", $"{fileName} -> '{requested}'");
            }
            else
            {
                effectiveBaseName = sanitized;
            }

            try
            {
                var srcMp = ModelPathUtils.ConvertUserVisiblePathToModelPath(srcPath);

                bool isWs = false;
                try { var bfi = BasicFileInfo.Extract(srcPath); isWs = bfi != null && bfi.IsWorkshared; }
                catch (Exception ex) { DiagnosticsLog.Swallowed("UpgradeLinks: BasicFileInfo at cloud run", ex); }

                var oo = new OpenOptions { Audit = Spec.AuditOnOpen };
                if (isWs)
                {
                    // Foreground/activated open for the native dialog — keep worksets OPEN (unlike
                    // the Local-mode background open) so the user sees the real model, not an empty one.
                    oo.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
                }

                // A file currently loaded as a link in this host can't be opened standalone —
                // unload it first (same constraint as the Local path; the link reload after the
                // cloud save re-points it).
                if (_cloudHostDoc != null) UnloadIfCurrentlyLinked(_cloudHostDoc, srcPath, fileName);

                // PostCommand operates on the ACTIVE document, so this file must be opened AND
                // activated in the UI (RAM cost accepted — see CLAUDE.md memory-discipline note;
                // this is the explicit trade-off of using Revit's own native cloud-save dialog).
                var uidoc = app.OpenAndActivateDocument(srcMp, oo, false);
                var linkDoc = uidoc?.Document;
                if (linkDoc == null)
                {
                    _cloudFail++;
                    Log(AppStrings.T("upgradeLinks.log.openFail", fileName), "fail");
                    Progress(_cloudIndex, _cloudFiles.Count, _cloudPass, _cloudFail, _cloudSkip);
                    ProcessNextCloudFile(app);
                    return;
                }

                var cmdId = RevitCommandId.LookupPostableCommandId(PostableCommand.SaveAsCloudModel);
                if (cmdId == null || !app.CanPostCommand(cmdId))
                {
                    _cloudFail++;
                    Log(AppStrings.T("upgradeLinks.log.cloudPostFail", fileName), "fail");
                    Progress(_cloudIndex, _cloudFiles.Count, _cloudPass, _cloudFail, _cloudSkip);
                    ProcessNextCloudFile(app);
                    return;
                }

                _cloudWaitDoc       = linkDoc;
                _cloudWaitFileName  = fileName;
                _cloudWaitBaseName  = effectiveBaseName;
                _cloudWaitPlacement = item.Placement;

                Log(AppStrings.T("upgradeLinks.log.cloudWaiting", _cloudIndex, _cloudFiles.Count, fileName), "info");
                OnAwaitingUser?.Invoke(true,
                    AppStrings.T("upgradeLinks.log.cloudContinueLabel"),
                    AppStrings.T("upgradeLinks.log.cloudSkipLabel"));

                // PostCommand only runs the native dialog once this Execute() call returns — do
                // not do anything more here. ContinueCloudRun resumes on the next Continue/Skip.
                app.PostCommand(cmdId);
            }
            catch (Exception ex)
            {
                _cloudFail++;
                DiagnosticsLog.Error($"UpgradeLinks: cloud open/post {fileName}", ex);
                Log(AppStrings.T("upgradeLinks.log.fileFail", fileName, ex.GetType().Name, ex.Message), "fail");
                Progress(_cloudIndex, _cloudFiles.Count, _cloudPass, _cloudFail, _cloudSkip);
                ProcessNextCloudFile(app);
            }
        }

        private void ContinueCloudRun(UIApplication app)
        {
            OnAwaitingUser?.Invoke(false, null, null);

            bool doSkip     = CloudSkipRequested;
            bool doContinue = CloudContinueRequested;
            CloudSkipRequested = false;
            CloudContinueRequested = false;

            if (doSkip)
            {
                _cloudSkip++;
                Log(AppStrings.T("upgradeLinks.log.cloudSkipped", _cloudWaitFileName), "warn");
                CloseCloudWaitDoc(app);
                Progress(_cloudIndex, _cloudFiles.Count, _cloudPass, _cloudFail, _cloudSkip);
                if (RunState.CancelRequested)
                {
                    Log(AppStrings.T("common.log.stoppedByUser", _cloudIndex, _cloudFiles.Count), "warn");
                    FinishCloudRun(app);
                }
                else ProcessNextCloudFile(app);
                return;
            }

            if (!doContinue) return;   // stray re-entry with neither flag set — stay paused

            var doc = _cloudWaitDoc;
            if (doc == null || !doc.IsModelInCloud)
            {
                // Not saved to the cloud yet (or the user hasn't finished) — re-show the pause and
                // let them try again rather than guessing/forcing a close.
                Log(AppStrings.T("upgradeLinks.log.cloudNotSavedYet", _cloudWaitFileName), "warn");
                OnAwaitingUser?.Invoke(true,
                    AppStrings.T("upgradeLinks.log.cloudContinueLabel"),
                    AppStrings.T("upgradeLinks.log.cloudSkipLabel"));
                return;
            }

            try
            {
                var savedMp = doc.GetCloudModelPath();
                CloseCloudWaitDoc(app);
                var linkResult = _cloudHostDoc != null
                    ? LinkIntoHost(_cloudHostDoc, savedMp, _cloudWaitBaseName, _cloudWaitPlacement)
                    : LinkResult.Failed;
                if (linkResult == LinkResult.Linked)
                {
                    _cloudPass++;
                    Log(AppStrings.T("upgradeLinks.log.linked", _cloudIndex, _cloudFiles.Count, _cloudWaitFileName), "info");
                }
                else if (linkResult == LinkResult.SkippedExisting)
                {
                    _cloudSkip++;
                }
                else
                {
                    _cloudFail++;
                }
            }
            catch (Exception ex)
            {
                _cloudFail++;
                DiagnosticsLog.Error($"UpgradeLinks: cloud link {_cloudWaitFileName}", ex);
                Log(AppStrings.T("upgradeLinks.log.fileFail", _cloudWaitFileName, ex.GetType().Name, ex.Message), "fail");
            }

            Progress(_cloudIndex, _cloudFiles.Count, _cloudPass, _cloudFail, _cloudSkip);
            if (RunState.CancelRequested)
            {
                Log(AppStrings.T("common.log.stoppedByUser", _cloudIndex, _cloudFiles.Count), "warn");
                FinishCloudRun(app);
            }
            else ProcessNextCloudFile(app);
        }

        // Revit will not let the API close a document while it is the ACTIVE document — the file
        // we just posted the native Save-As-Cloud command to is still active at this point, so
        // reactivate the host first (best effort: OpenAndActivateDocument on an already-open path
        // just switches focus to it, it does not duplicate-open it), then close. If closing still
        // fails, report it to the user instead of hiding it — the file was already saved+linked
        // successfully, so this is a "you may need to close this tab yourself" note, not a failure
        // of the run.
        private void CloseCloudWaitDoc(UIApplication app)
        {
            if (_cloudWaitDoc == null) return;
            var waitDoc  = _cloudWaitDoc;
            var fileName = _cloudWaitFileName;
            _cloudWaitDoc = null;

            if (_cloudHostDoc != null)
            {
                try
                {
                    var hostMp = GetModelPath(_cloudHostDoc);
                    if (hostMp != null) app.OpenAndActivateDocument(hostMp, new OpenOptions(), false);
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed("UpgradeLinks: reactivate host before close", ex); }
            }

            try
            {
                waitDoc.Close(false);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Warn("UpgradeLinks: close cloud wait doc", ex.Message);
                Log(AppStrings.T("upgradeLinks.log.cloudCloseFail", fileName), "warn");
            }
        }

        // Resolves a Document's own ModelPath, for reactivating it via OpenAndActivateDocument.
        private static ModelPath? GetModelPath(Document doc)
        {
            try
            {
                if (doc.IsModelInCloud) return doc.GetCloudModelPath();
                if (doc.IsWorkshared)   return doc.GetWorksharingCentralModelPath();
                if (!string.IsNullOrEmpty(doc.PathName)) return ModelPathUtils.ConvertUserVisiblePathToModelPath(doc.PathName);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("UpgradeLinks: resolve document model path", ex); }
            return null;
        }

        private void FinishCloudRun(UIApplication app)
        {
            long issues = DiagnosticsLog.IssuesSince(_cloudIssues0);
            if (issues > 0) Log(AppStrings.T("upgradeLinks.log.nonFatal", issues), "warn");
            Log(AppStrings.T("upgradeLinks.log.done", _cloudPass, _cloudSkip, _cloudFail), _cloudFail > 0 ? "warn" : "pass");
            OnProgress?.Invoke(100, _cloudPass, _cloudFail, _cloudSkip);
            int pass = _cloudPass, fail = _cloudFail, skip = _cloudSkip;

            _cloudActive  = false;
            _cloudFiles   = new List<UpgradeFileItem>();
            // Close before dropping the host reference — CloseCloudWaitDoc needs it to reactivate
            // the host and switch focus away from the wait doc before Revit will let it close.
            CloseCloudWaitDoc(app);
            _cloudHostDoc = null;
            Spec = new UpgradeLinksSpec();
            HostFolder = null;

            OnComplete?.Invoke(pass, fail, skip);
        }

        // ── Save ─────────────────────────────────────────────────────────────────
        private static void SaveLocal(Document doc, string destPath, bool isWorkshared)
        {
            var so = new SaveAsOptions { OverwriteExistingFile = true };
            if (isWorkshared)
            {
                // A detached workshared doc is re-saved as a new central at the destination.
                so.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
            }
            doc.SaveAs(destPath, so);
        }

        // ── Link ─────────────────────────────────────────────────────────────────
        private enum LinkResult { Linked, SkippedExisting, Failed }

        private LinkResult LinkIntoHost(Document hostDoc, ModelPath savedMp, string baseName, UpgradePlacement placement)
        {
            // RevitLinkType.Create needs a transaction, but the reload-existing path
            // (ReloadExistingType → LoadFrom) must NOT be inside one — link-management calls throw
            // "operation is not permitted when there is any open transaction" (CLAUDE.md). So the
            // type step and the instance step each get their own transaction, and any LoadFrom runs
            // between them with no transaction open.
            ElementId typeId = ElementId.InvalidElementId;
            bool createdFresh = false;
            using (var tx = new Transaction(hostDoc, "Create Link Type"))
            {
                tx.Start();
                ConfigureFailures(tx);
                try
                {
                    var result = RevitLinkType.Create(hostDoc, savedMp, new RevitLinkOptions(false));
                    typeId = result.ElementId;
                    createdFresh = true;
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    // Most commonly: a link with this file/name already exists in the host.
                    DiagnosticsLog.Swallowed("UpgradeLinks: RevitLinkType.Create", ex);
                    tx.RollBack();
                }
            }

            if (!createdFresh)
            {
                if (!Spec.ReloadExisting)
                {
                    Log(AppStrings.T("upgradeLinks.log.linkExistsSkip", baseName), "warn");
                    return LinkResult.SkippedExisting;
                }

                // LoadFrom OUTSIDE any transaction — repoints the existing type at the upgraded copy.
                typeId = ReloadExistingType(hostDoc, savedMp, baseName);
                if (typeId == ElementId.InvalidElementId)
                {
                    Log(AppStrings.T("upgradeLinks.log.linkExistsSkip", baseName), "warn");
                    return LinkResult.SkippedExisting;
                }
                Log(AppStrings.T("upgradeLinks.log.linkReloaded", baseName), "info");

                // The reloaded type already points at the upgraded copy; if it carries instances,
                // leave them (adding another would duplicate the link on the model).
                if (HasInstances(hostDoc, typeId)) return LinkResult.Linked;
            }

            if (typeId == ElementId.InvalidElementId)
            {
                // The file was upgraded and saved but never linked — a real failure, not a skip.
                Log(AppStrings.T("upgradeLinks.log.typeInvalid", baseName), "fail");
                return LinkResult.Failed;
            }

            using (var tx = new Transaction(hostDoc, "Place Link Instance"))
            {
                tx.Start();
                ConfigureFailures(tx);

                RevitLinkInstance? instance = null;
                try
                {
                    // Only Origin and Shared are valid for RevitLinkInstance.Create — Centered /
                    // Site (Project Base Point) throw "Placement isn't supported." So always link
                    // at Origin, then translate to reproduce the chosen placement (ApplyPlacement).
                    instance = RevitLinkInstance.Create(hostDoc, typeId, ImportPlacement.Origin);
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("UpgradeLinks: instance placement", ex);
                    Log(AppStrings.T("upgradeLinks.log.placeFail", baseName, ex.Message), "fail");
                    tx.RollBack();
                    // A freshly-created type with no instance is an orphan (loaded but unplaced) —
                    // drop it so a re-run starts clean instead of falling into the reload path.
                    if (createdFresh) DeleteOrphanType(hostDoc, typeId);
                    return LinkResult.Failed;
                }

                if (instance != null)
                    ApplyPlacement(hostDoc, instance, placement, baseName);

                tx.Commit();
                return LinkResult.Linked;
            }
        }

        private static void DeleteOrphanType(Document hostDoc, ElementId typeId)
        {
            try
            {
                using (var tx = new Transaction(hostDoc, "Remove Unplaced Link Type"))
                {
                    tx.Start();
                    ConfigureFailures(tx);
                    hostDoc.Delete(typeId);
                    tx.Commit();
                }
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("UpgradeLinks: delete orphan link type", ex); }
        }

        // A source file that is currently loaded as a link in the host can't be opened as a
        // standalone document — unload its link type first (the post-save reload in LinkIntoHost
        // re-points it at the upgraded copy). No-op when the file isn't linked or isn't loaded.
        private void UnloadIfCurrentlyLinked(Document hostDoc, string srcPath, string fileName)
        {
            try
            {
                RevitLinkType? loadedType = null;
                foreach (var t in new FilteredElementCollector(hostDoc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
                {
                    try
                    {
                        var er = t.GetExternalFileReference();
                        if (er == null) continue;
                        string p = ModelPathUtils.ConvertModelPathToUserVisiblePath(er.GetAbsolutePath());
                        if (string.Equals(p, srcPath, StringComparison.OrdinalIgnoreCase) &&
                            er.GetLinkedFileStatus() == LinkedFileStatus.Loaded)
                        {
                            loadedType = t;
                            break;
                        }
                    }
                    catch (Exception ex) { DiagnosticsLog.Swallowed("UpgradeLinks: probe link type path", ex); }
                }
                if (loadedType == null) return;

                // Unload is a link-management call — it must run OUTSIDE any transaction, or it
                // throws "operation is not permitted when there is any open transaction" (confirmed
                // on a Windows run). No Transaction here.
                loadedType.Unload(null);
                Log(AppStrings.T("upgradeLinks.log.unloadedExisting", fileName), "info");
            }
            catch (Exception ex)
            {
                // Not fatal here — the open/save that follows will report its own failure.
                DiagnosticsLog.Error("UpgradeLinks: unload existing link before upgrade", ex);
            }
        }

        // Reproduces the chosen placement by translating a just-linked (Origin-placed) instance.
        // InternalOrigin leaves it at Origin; the other three move it (base-point → base-point, or
        // extents-center → extents-center). All helpers are best-effort: a failure leaves the
        // Origin placement (reported) rather than throwing out of the run.
        private void ApplyPlacement(Document hostDoc, RevitLinkInstance instance, UpgradePlacement placement, string baseName)
        {
            switch (placement)
            {
                case UpgradePlacement.ProjectBasePoint:
                    TranslateBasePointToBasePoint(hostDoc, instance, baseName, survey: false);
                    break;
                case UpgradePlacement.SurveyPoint:
                    TranslateBasePointToBasePoint(hostDoc, instance, baseName, survey: true);
                    break;
                case UpgradePlacement.CenterToCenter:
                    TranslateCenterToCenter(hostDoc, instance, baseName);
                    break;
                // InternalOrigin: already at Origin — nothing to do.
            }
        }

        // Moves a just-linked (Origin-placed) instance so its link document's Project Base Point
        // (survey: false) or Survey Point (survey: true) coincides with the host's same point.
        // Both points are read/moved in internal coordinates.
        private void TranslateBasePointToBasePoint(Document hostDoc, RevitLinkInstance instance, string baseName, bool survey)
        {
            string fallbackKey = survey ? "upgradeLinks.log.surveyFallback" : "upgradeLinks.log.pbpFallback";
            try
            {
                var linkDoc = instance.GetLinkDocument();
                if (linkDoc == null) { Log(AppStrings.T(fallbackKey, baseName), "warn"); return; }

                var hostBp = survey ? BasePoint.GetSurveyPoint(hostDoc) : BasePoint.GetProjectBasePoint(hostDoc);
                var linkBp = survey ? BasePoint.GetSurveyPoint(linkDoc) : BasePoint.GetProjectBasePoint(linkDoc);
                if (hostBp == null || linkBp == null) { Log(AppStrings.T(fallbackKey, baseName), "warn"); return; }

                // The link's own base-point position (link internal coords) projected through the
                // just-created (Origin) instance transform into the host's internal coordinates.
                XYZ linkBpInHost = instance.GetTotalTransform().OfPoint(linkBp.Position);
                XYZ delta = hostBp.Position - linkBpInHost;
                MoveInstance(hostDoc, instance, delta);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"UpgradeLinks: base-point placement (survey={survey}) for '{baseName}'", ex);
                Log(AppStrings.T(fallbackKey, baseName), "warn");
            }
        }

        // Moves a just-linked (Origin-placed) instance so the centre of the link's extents lands
        // on the centre of the host model's extents — the API equivalent of "Center to Center".
        private void TranslateCenterToCenter(Document hostDoc, RevitLinkInstance instance, string baseName)
        {
            try
            {
                var hostCenter = GetHostModelCenter(hostDoc);
                // The instance was just created — regenerate so its bounding box reads true.
                hostDoc.Regenerate();
                var linkBox = instance.get_BoundingBox(null);
                if (hostCenter == null || linkBox == null)
                {
                    Log(AppStrings.T("upgradeLinks.log.centerFallback", baseName), "warn");
                    return;
                }
                XYZ linkCenter = (linkBox.Min + linkBox.Max) * 0.5;
                MoveInstance(hostDoc, instance, hostCenter - linkCenter);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"UpgradeLinks: center-to-center for '{baseName}'", ex);
                Log(AppStrings.T("upgradeLinks.log.centerFallback", baseName), "warn");
            }
        }

        private static void MoveInstance(Document hostDoc, RevitLinkInstance instance, XYZ delta)
        {
            if (delta.GetLength() < 1e-9) return;
            bool pinned = instance.Pinned;
            if (pinned) instance.Pinned = false;
            ElementTransformUtils.MoveElement(hostDoc, instance.Id, delta);
            if (pinned) instance.Pinned = true;
        }

        // Centre of the host model's extents (union of model-element bounding boxes, link
        // instances excluded so they can't skew it), computed once per run and cached. Returns
        // null when the model has no measurable geometry.
        private XYZ? GetHostModelCenter(Document hostDoc)
        {
            if (_hostCenterComputed) return _hostModelCenter;
            _hostCenterComputed = true;
            try
            {
                XYZ? min = null, max = null;
                int unreadable = 0;
                var col = new FilteredElementCollector(hostDoc)
                    .WhereElementIsNotElementType()
                    .WhereElementIsViewIndependent();
                foreach (var el in col)
                {
                    // Exclude links (would skew the host centre) and datums (grids/levels/reference
                    // planes span far past the building geometry and would drag the centre off).
                    if (el is RevitLinkInstance || el is Grid || el is Level || el is ReferencePlane) continue;
                    BoundingBoxXYZ? bb;
                    try { bb = el.get_BoundingBox(null); }
                    catch { bb = null; unreadable++; }
                    if (bb == null) continue;
                    min = min == null ? bb.Min : new XYZ(Math.Min(min.X, bb.Min.X), Math.Min(min.Y, bb.Min.Y), Math.Min(min.Z, bb.Min.Z));
                    max = max == null ? bb.Max : new XYZ(Math.Max(max.X, bb.Max.X), Math.Max(max.Y, bb.Max.Y), Math.Max(max.Z, bb.Max.Z));
                }
                if (unreadable > 0)
                    DiagnosticsLog.Warn("UpgradeLinks: host model center", $"{unreadable} element(s) had no readable bounding box");
                _hostModelCenter = (min != null && max != null) ? (min + max) * 0.5 : (XYZ?)null;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("UpgradeLinks: compute host model center", ex);
                _hostModelCenter = null;
            }
            return _hostModelCenter;
        }

        private static ElementId ReloadExistingType(Document hostDoc, ModelPath savedMp, string baseName)
        {
            try
            {
                var type = new FilteredElementCollector(hostDoc)
                    .OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                    .FirstOrDefault(t => string.Equals(t.Name, baseName, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(t.Name, baseName + ".rvt", StringComparison.OrdinalIgnoreCase));
                if (type == null) return ElementId.InvalidElementId;
                type.LoadFrom(savedMp, new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
                return type.Id;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("UpgradeLinks: reload existing type", ex);
                return ElementId.InvalidElementId;
            }
        }

        private static bool HasInstances(Document hostDoc, ElementId typeId)
        {
            return new FilteredElementCollector(hostDoc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .Any(i => i.GetTypeId() == typeId);
        }

        // ── helpers ────────────────────────────────────────────────────────────
        private void Progress(int done, int total, int pass, int fail, int skip)
        {
            int pct = total > 0 ? (int)(done * 100.0 / total) : 100;
            Log(AppStrings.T("upgradeLinks.log.progress", pct, done, total, pass), "info");
            OnProgress?.Invoke(pct, pass, fail, skip);
        }

        private string DestLabel(string? destFolder)
        {
            switch (Spec.Destination)
            {
                case UpgradeDestination.CurrentLocation: return AppStrings.T("upgradeLinks.summaries.destCurrentLocation");
                case UpgradeDestination.Cloud:            return AppStrings.T("upgradeLinks.summaries.destCloud");
                default:                                  return AppStrings.T("upgradeLinks.summaries.destSelectedFolder", destFolder ?? "");
            }
        }

        // Strips characters Windows can't have in a file name from a user-typed "save as" value.
        // Returns "" (rather than a fallback name) when nothing usable survives — the caller
        // decides and reports the fallback, per the "empty resolved name is a failure" rule.
        private static string SanitizeBaseName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            return new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        }

        // Two queued sources can share a file name (same name, different folders); disambiguate so the
        // second doesn't overwrite the first in the destination folder.
        private static string UniqueFileName(string fileName, HashSet<string> used)
        {
            string name = Path.GetFileNameWithoutExtension(fileName);
            string ext  = Path.GetExtension(fileName);
            string candidate = fileName;
            int n = 2;
            while (!used.Add(candidate)) candidate = $"{name} ({n++}){ext}";
            return candidate;
        }

        private static void ConfigureFailures(Transaction tx)
        {
            try
            {
                var opts = tx.GetFailureHandlingOptions();
                opts.SetClearAfterRollback(true);
                opts.SetForcedModalHandling(false);
                tx.SetFailureHandlingOptions(opts);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("UpgradeLinks: configure failure handling", ex); }
        }
    }
}
