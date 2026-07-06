using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.UpgradeLinks
{
    /// <summary>
    /// Upgrade &amp; Link Models run. Local destinations (CurrentLocation / SelectedFolder) process
    /// strictly one file at a time within a single <see cref="Execute"/> call: open (which upgrades
    /// the file in memory — background <see cref="Application.OpenDocumentFile"/>, never an activated
    /// view) → save → close → link into the host. Cloud is different: Revit's native "Save As Cloud
    /// Model" dialog is posted (<see cref="UIApplication.PostCommand"/>), which only runs after this
    /// method returns, so Cloud spans MULTIPLE Execute() calls — one per file, paused in between on
    /// the user via <see cref="ILemoineRunPausable"/> (Continue/Skip). See <see cref="ProcessNextCloudFile"/>.
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
        // (isAwaiting, continueLabel, skipLabel) — see ILemoineRunPausable.
        public Action<bool, string?, string?>? OnAwaitingUser { get; set; }

        // Set by the ViewModel's ILemoineRunPausable.ContinueRun()/SkipCurrentItem(), then the
        // SAME ExternalEvent is raised again — Execute() sees _cloudActive and resumes here.
        public bool CloudContinueRequested { get; set; }
        public bool CloudSkipRequested     { get; set; }

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

        public string GetName() => "LemoineTools.Tools.UpgradeLinks.UpgradeLinksRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        public void Execute(UIApplication app)
        {
            if (_cloudActive)
            {
                try { ContinueCloudRun(app); }
                catch (Exception ex)
                {
                    LemoineLog.Error("UpgradeLinksRunHandler.ContinueCloudRun", ex);
                    Log(LemoineStrings.T("upgradeLinks.log.aborted", ex.Message), "fail");
                    int p = _cloudPass, f = _cloudFail + 1, s = _cloudSkip;
                    _cloudActive = false;
                    CloseCloudWaitDoc();
                    OnAwaitingUser?.Invoke(false, null, null);
                    Spec = new UpgradeLinksSpec();
                    HostFolder = null;
                    OnComplete?.Invoke(p, f, s);
                }
                return;
            }

            int pass = 0, fail = 0, skip = 0;
            long issues0 = LemoineLog.IssueCount;
            try
            {
                var hostDoc = app.ActiveUIDocument?.Document;
                if (hostDoc == null) { Log(LemoineStrings.T("upgradeLinks.log.noDoc"), "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                var files = Spec.Files ?? new List<UpgradeFileItem>();
                if (files.Count == 0) { Log(LemoineStrings.T("upgradeLinks.log.noFiles"), "warn"); OnComplete?.Invoke(0, 0, 0); return; }

                if (Spec.Destination == UpgradeDestination.Cloud)
                {
                    if (!Spec.CloudReady)
                    {
                        Log(LemoineStrings.T("upgradeLinks.log.cloudNotReady"), "fail");
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
                    if (string.IsNullOrWhiteSpace(folder)) { Log(LemoineStrings.T("upgradeLinks.log.noSelectedFolder"), "fail"); OnComplete?.Invoke(0, 1, 0); return; }
                    destFolder = folder;
                    try { Directory.CreateDirectory(destFolder); }
                    catch (Exception ex)
                    {
                        LemoineLog.Error("UpgradeLinks: create selected folder", ex);
                        Log(LemoineStrings.T("upgradeLinks.log.subfolderFail", destFolder, ex.Message), "fail");
                        OnComplete?.Invoke(0, 1, 0); return;
                    }
                }

                Log(LemoineStrings.T("upgradeLinks.log.start", files.Count, DestLabel(destFolder)), "info");

                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var appApp    = app.Application;
                int total = files.Count, done = 0;

                foreach (var item in files)
                {
                    if (LemoineRun.CancelRequested)
                    {
                        Log(LemoineStrings.T("common.log.stoppedByUser", done, total), "warn");
                        break;   // committed links + saved files already preserved
                    }

                    done++;
                    string srcPath  = item.Path;
                    string fileName = Path.GetFileName(srcPath);
                    string baseName = Path.GetFileNameWithoutExtension(srcPath);

                    if (!File.Exists(srcPath))
                    {
                        skip++;
                        Log(LemoineStrings.T("upgradeLinks.log.missing", fileName), "warn");
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
                            Log(LemoineStrings.T("upgradeLinks.log.renameInvalid", fileName), "warn");
                            LemoineLog.Warn("UpgradeLinks: rename resolved to an unusable name", $"{fileName} -> '{requested}'");
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
                        catch (Exception ex) { LemoineLog.Swallowed("UpgradeLinks: BasicFileInfo at run", ex); }

                        var oo = new OpenOptions { Audit = Spec.AuditOnOpen };
                        if (isWs)
                        {
                            // Detached + all worksets closed: the file's elements are never loaded into
                            // memory (the dominant RAM saver) yet are fully preserved on save.
                            oo.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                            oo.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets));
                        }

                        // Opening upgrades the file in memory. Background open ONLY — an activated view
                        // pins its graphics in native RAM for the whole session (CLAUDE.md).
                        linkDoc = appApp.OpenDocumentFile(srcMp, oo);
                        if (linkDoc == null)
                        {
                            fail++;
                            Log(LemoineStrings.T("upgradeLinks.log.openFail", fileName), "fail");
                            Progress(done, total, pass, fail, skip);
                            continue;
                        }

                        string destPath = Spec.Destination == UpgradeDestination.CurrentLocation
                            ? srcPath
                            : Path.Combine(destFolder!, UniqueFileName(effectiveBaseName + Path.GetExtension(srcPath), usedNames));
                        SaveLocal(linkDoc, destPath, isWs);
                        var savedMp = ModelPathUtils.ConvertUserVisiblePathToModelPath(destPath);

                        // Close before linking — keeps at most the host (+ the link type Revit loads) in RAM.
                        try { linkDoc.Close(false); }
                        catch (Exception ex) { LemoineLog.Swallowed("UpgradeLinks: close", ex); }
                        linkDoc = null;

                        if (LinkIntoHost(hostDoc, savedMp, effectiveBaseName, item.Placement))
                        {
                            pass++;
                            if (!string.Equals(effectiveBaseName, baseName, StringComparison.Ordinal))
                                Log(LemoineStrings.T("upgradeLinks.log.linkedRenamed", done, total, fileName, effectiveBaseName), "info");
                            else
                                Log(LemoineStrings.T("upgradeLinks.log.linked", done, total, fileName), "info");
                        }
                        else
                        {
                            skip++;
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        LemoineLog.Error($"UpgradeLinks: process {fileName}", ex);
                        Log(LemoineStrings.T("upgradeLinks.log.fileFail", fileName, ex.GetType().Name, ex.Message), "fail");
                    }
                    finally
                    {
                        if (linkDoc != null)
                        {
                            try { linkDoc.Close(false); }
                            catch (Exception ex) { LemoineLog.Swallowed("UpgradeLinks: close (finally)", ex); }
                        }
                    }

                    Progress(done, total, pass, fail, skip);
                }

                long issues = LemoineLog.IssuesSince(issues0);
                if (issues > 0) Log(LemoineStrings.T("upgradeLinks.log.nonFatal", issues), "warn");
                Log(LemoineStrings.T("upgradeLinks.log.done", pass, skip, fail), fail > 0 ? "warn" : "pass");
                OnProgress?.Invoke(100, pass, fail, skip);
                OnComplete?.Invoke(pass, fail, skip);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("UpgradeLinksRunHandler.Execute", ex);
                Log(LemoineStrings.T("upgradeLinks.log.aborted", ex.Message), "fail");
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

        // ── Cloud — native "Save As Cloud Model" per file, paused on the user between files ────
        private void StartCloudRun(UIApplication app, Document hostDoc, List<UpgradeFileItem> files)
        {
            _cloudActive    = true;
            _cloudHostDoc   = hostDoc;
            _cloudFiles     = files;
            _cloudIndex     = 0;
            _cloudPass = _cloudFail = _cloudSkip = 0;
            _cloudIssues0   = LemoineLog.IssueCount;
            Log(LemoineStrings.T("upgradeLinks.log.start", files.Count, LemoineStrings.T("upgradeLinks.summaries.destCloud")), "info");
            ProcessNextCloudFile(app);
        }

        private void ProcessNextCloudFile(UIApplication app)
        {
            if (LemoineRun.CancelRequested)
            {
                Log(LemoineStrings.T("common.log.stoppedByUser", _cloudIndex, _cloudFiles.Count), "warn");
                FinishCloudRun();
                return;
            }
            if (_cloudIndex >= _cloudFiles.Count) { FinishCloudRun(); return; }

            var item = _cloudFiles[_cloudIndex];
            _cloudIndex++;
            string srcPath  = item.Path;
            string fileName = Path.GetFileName(srcPath);
            string baseName = Path.GetFileNameWithoutExtension(srcPath);

            if (!File.Exists(srcPath))
            {
                _cloudSkip++;
                Log(LemoineStrings.T("upgradeLinks.log.missing", fileName), "warn");
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
                Log(LemoineStrings.T("upgradeLinks.log.renameInvalid", fileName), "warn");
                LemoineLog.Warn("UpgradeLinks: rename resolved to an unusable name", $"{fileName} -> '{requested}'");
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
                catch (Exception ex) { LemoineLog.Swallowed("UpgradeLinks: BasicFileInfo at cloud run", ex); }

                var oo = new OpenOptions { Audit = Spec.AuditOnOpen };
                if (isWs)
                {
                    // Foreground/activated open for the native dialog — keep worksets OPEN (unlike
                    // the Local-mode background open) so the user sees the real model, not an empty one.
                    oo.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
                }

                // PostCommand operates on the ACTIVE document, so this file must be opened AND
                // activated in the UI (RAM cost accepted — see CLAUDE.md memory-discipline note;
                // this is the explicit trade-off of using Revit's own native cloud-save dialog).
                var uidoc = app.OpenAndActivateDocument(srcMp, oo, false);
                var linkDoc = uidoc?.Document;
                if (linkDoc == null)
                {
                    _cloudFail++;
                    Log(LemoineStrings.T("upgradeLinks.log.openFail", fileName), "fail");
                    Progress(_cloudIndex, _cloudFiles.Count, _cloudPass, _cloudFail, _cloudSkip);
                    ProcessNextCloudFile(app);
                    return;
                }

                var cmdId = RevitCommandId.LookupPostableCommandId(PostableCommand.SaveAsCloudModel);
                if (cmdId == null || !app.CanPostCommand(cmdId))
                {
                    _cloudFail++;
                    Log(LemoineStrings.T("upgradeLinks.log.cloudPostFail", fileName), "fail");
                    Progress(_cloudIndex, _cloudFiles.Count, _cloudPass, _cloudFail, _cloudSkip);
                    ProcessNextCloudFile(app);
                    return;
                }

                _cloudWaitDoc       = linkDoc;
                _cloudWaitFileName  = fileName;
                _cloudWaitBaseName  = effectiveBaseName;
                _cloudWaitPlacement = item.Placement;

                Log(LemoineStrings.T("upgradeLinks.log.cloudWaiting", _cloudIndex, _cloudFiles.Count, fileName), "info");
                OnAwaitingUser?.Invoke(true,
                    LemoineStrings.T("upgradeLinks.log.cloudContinueLabel"),
                    LemoineStrings.T("upgradeLinks.log.cloudSkipLabel"));

                // PostCommand only runs the native dialog once this Execute() call returns — do
                // not do anything more here. ContinueCloudRun resumes on the next Continue/Skip.
                app.PostCommand(cmdId);
            }
            catch (Exception ex)
            {
                _cloudFail++;
                LemoineLog.Error($"UpgradeLinks: cloud open/post {fileName}", ex);
                Log(LemoineStrings.T("upgradeLinks.log.fileFail", fileName, ex.GetType().Name, ex.Message), "fail");
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
                Log(LemoineStrings.T("upgradeLinks.log.cloudSkipped", _cloudWaitFileName), "warn");
                CloseCloudWaitDoc();
                Progress(_cloudIndex, _cloudFiles.Count, _cloudPass, _cloudFail, _cloudSkip);
                if (LemoineRun.CancelRequested)
                {
                    Log(LemoineStrings.T("common.log.stoppedByUser", _cloudIndex, _cloudFiles.Count), "warn");
                    FinishCloudRun();
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
                Log(LemoineStrings.T("upgradeLinks.log.cloudNotSavedYet", _cloudWaitFileName), "warn");
                OnAwaitingUser?.Invoke(true,
                    LemoineStrings.T("upgradeLinks.log.cloudContinueLabel"),
                    LemoineStrings.T("upgradeLinks.log.cloudSkipLabel"));
                return;
            }

            try
            {
                var savedMp = doc.GetCloudModelPath();
                CloseCloudWaitDoc();
                if (_cloudHostDoc != null && LinkIntoHost(_cloudHostDoc, savedMp, _cloudWaitBaseName, _cloudWaitPlacement))
                {
                    _cloudPass++;
                    Log(LemoineStrings.T("upgradeLinks.log.linked", _cloudIndex, _cloudFiles.Count, _cloudWaitFileName), "info");
                }
                else
                {
                    _cloudSkip++;
                }
            }
            catch (Exception ex)
            {
                _cloudFail++;
                LemoineLog.Error($"UpgradeLinks: cloud link {_cloudWaitFileName}", ex);
                Log(LemoineStrings.T("upgradeLinks.log.fileFail", _cloudWaitFileName, ex.GetType().Name, ex.Message), "fail");
            }

            Progress(_cloudIndex, _cloudFiles.Count, _cloudPass, _cloudFail, _cloudSkip);
            if (LemoineRun.CancelRequested)
            {
                Log(LemoineStrings.T("common.log.stoppedByUser", _cloudIndex, _cloudFiles.Count), "warn");
                FinishCloudRun();
            }
            else ProcessNextCloudFile(app);
        }

        private void CloseCloudWaitDoc()
        {
            if (_cloudWaitDoc == null) return;
            try { _cloudWaitDoc.Close(false); }
            catch (Exception ex) { LemoineLog.Swallowed("UpgradeLinks: close cloud wait doc", ex); }
            _cloudWaitDoc = null;
        }

        private void FinishCloudRun()
        {
            long issues = LemoineLog.IssuesSince(_cloudIssues0);
            if (issues > 0) Log(LemoineStrings.T("upgradeLinks.log.nonFatal", issues), "warn");
            Log(LemoineStrings.T("upgradeLinks.log.done", _cloudPass, _cloudSkip, _cloudFail), _cloudFail > 0 ? "warn" : "pass");
            OnProgress?.Invoke(100, _cloudPass, _cloudFail, _cloudSkip);
            int pass = _cloudPass, fail = _cloudFail, skip = _cloudSkip;

            _cloudActive  = false;
            _cloudFiles   = new List<UpgradeFileItem>();
            _cloudHostDoc = null;
            CloseCloudWaitDoc();
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
        private bool LinkIntoHost(Document hostDoc, ModelPath savedMp, string baseName, UpgradePlacement placement)
        {
            var import = UpgradePlacementMap.ToImportPlacement(placement);
            using (var tx = new Transaction(hostDoc, "Link upgraded model"))
            {
                tx.Start();
                ConfigureFailures(tx);

                ElementId typeId;
                try
                {
                    var result = RevitLinkType.Create(hostDoc, savedMp, new RevitLinkOptions(false));
                    typeId = result.ElementId;
                }
                catch (Exception ex)
                {
                    // Most commonly: a link with this file/name already exists in the host.
                    LemoineLog.Swallowed("UpgradeLinks: RevitLinkType.Create", ex);
                    if (!Spec.ReloadExisting)
                    {
                        Log(LemoineStrings.T("upgradeLinks.log.linkExistsSkip", baseName), "warn");
                        tx.RollBack();
                        return false;
                    }

                    typeId = ReloadExistingType(hostDoc, savedMp, baseName);
                    if (typeId == ElementId.InvalidElementId)
                    {
                        Log(LemoineStrings.T("upgradeLinks.log.linkExistsSkip", baseName), "warn");
                        tx.RollBack();
                        return false;
                    }
                    Log(LemoineStrings.T("upgradeLinks.log.linkReloaded", baseName), "info");

                    // The reloaded type already points at the upgraded copy; if it carries instances,
                    // leave them (adding another would duplicate the link on the model).
                    if (HasInstances(hostDoc, typeId)) { tx.Commit(); return true; }
                }

                if (typeId == ElementId.InvalidElementId) { tx.RollBack(); return false; }

                try
                {
                    RevitLinkInstance.Create(hostDoc, typeId, import);
                }
                catch (Exception ex)
                {
                    LemoineLog.Swallowed("UpgradeLinks: RevitLinkInstance.Create", ex);
                    if (placement == UpgradePlacement.SharedCoordinates)
                    {
                        // No shared-coordinate relationship in the file — fall back to Origin, reported.
                        try
                        {
                            RevitLinkInstance.Create(hostDoc, typeId, ImportPlacement.Origin);
                            Log(LemoineStrings.T("upgradeLinks.log.sharedFallback", baseName), "warn");
                        }
                        catch (Exception ex2)
                        {
                            LemoineLog.Error("UpgradeLinks: instance origin fallback", ex2);
                            tx.RollBack();
                            return false;
                        }
                    }
                    else
                    {
                        LemoineLog.Error("UpgradeLinks: instance placement", ex);
                        tx.RollBack();
                        return false;
                    }
                }

                tx.Commit();
                return true;
            }
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
                LemoineLog.Swallowed("UpgradeLinks: reload existing type", ex);
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
            Log(LemoineStrings.T("upgradeLinks.log.progress", pct, done, total, pass), "info");
            OnProgress?.Invoke(pct, pass, fail, skip);
        }

        private string DestLabel(string? destFolder)
        {
            switch (Spec.Destination)
            {
                case UpgradeDestination.CurrentLocation: return LemoineStrings.T("upgradeLinks.summaries.destCurrentLocation");
                case UpgradeDestination.Cloud:            return LemoineStrings.T("upgradeLinks.summaries.destCloud");
                default:                                  return LemoineStrings.T("upgradeLinks.summaries.destSelectedFolder", destFolder ?? "");
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
            catch (Exception ex) { LemoineLog.Swallowed("UpgradeLinks: configure failure handling", ex); }
        }
    }
}
