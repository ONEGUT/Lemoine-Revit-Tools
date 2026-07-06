using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ForgeDM;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.UpgradeLinks
{
    /// <summary>
    /// Upgrade &amp; Link Models run. Processes queued files strictly one at a time to keep RAM flat:
    /// open (which upgrades the file in memory — background <see cref="Application.OpenDocumentFile"/>,
    /// never an activated view) → save to the chosen destination → close → link the saved copy into the
    /// host with the row's placement. Workshared sources open detached with all worksets closed so their
    /// elements are never loaded. The loop is cancellable between files (committed links + saved files are
    /// preserved). Everything Revit does is single-threaded here on the API thread.
    /// </summary>
    public sealed class UpgradeLinksRunHandler : IExternalEventHandler
    {
        // ── Inputs (set before Raise) ─────────────────────────────────────────
        public UpgradeLinksSpec Spec { get; set; } = new UpgradeLinksSpec();
        public string? HostFolder { get; set; }   // folder of the active document (subfolder-mode base)

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.UpgradeLinks.UpgradeLinksRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        public void Execute(UIApplication app)
        {
            int pass = 0, fail = 0, skip = 0;
            long issues0 = LemoineLog.IssueCount;
            try
            {
                var hostDoc = app.ActiveUIDocument?.Document;
                if (hostDoc == null) { Log(LemoineStrings.T("upgradeLinks.log.noDoc"), "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                var files = Spec.Files ?? new List<UpgradeFileItem>();
                if (files.Count == 0) { Log(LemoineStrings.T("upgradeLinks.log.noFiles"), "warn"); OnComplete?.Invoke(0, 0, 0); return; }

                // Resolve the subfolder destination up front so a bad path fails before any file opens.
                string? destFolder = null;
                if (Spec.Destination == UpgradeDestination.Subfolder)
                {
                    if (string.IsNullOrWhiteSpace(HostFolder)) { Log(LemoineStrings.T("upgradeLinks.log.noHostFolder"), "fail"); OnComplete?.Invoke(0, 1, 0); return; }
                    destFolder = Path.Combine(HostFolder, SanitizeFolder(Spec.SubfolderName));
                    try { Directory.CreateDirectory(destFolder); }
                    catch (Exception ex)
                    {
                        LemoineLog.Error("UpgradeLinks: create subfolder", ex);
                        Log(LemoineStrings.T("upgradeLinks.log.subfolderFail", destFolder, ex.Message), "fail");
                        OnComplete?.Invoke(0, 1, 0); return;
                    }
                }
                CloudFolder? cloudFolder = null;
                if (Spec.Destination == UpgradeDestination.Cloud)
                {
                    if (!Spec.CloudReady)
                    {
                        Log(LemoineStrings.T("upgradeLinks.log.cloudNotReady"), "fail");
                        OnComplete?.Invoke(0, 1, 0); return;
                    }
                    // Resolved once up front (not per-file) — the ForgeDM browsing calls hit ACC,
                    // and every file in this run saves into the same host folder.
                    cloudFolder = ResolveCloudFolder(Spec.CloudHubId, Spec.CloudProjectId, Spec.CloudFolderId);
                    if (cloudFolder == null)
                    {
                        Log(LemoineStrings.T("upgradeLinks.log.cloudFolderNotFound"), "fail");
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

                    // Overwrite always saves back to the file's own original path, so a rename
                    // would leave the original untouched at a name it no longer matches — ignored
                    // there (per the note on the Overwrite destination card).
                    string effectiveBaseName = baseName;
                    if (Spec.Destination != UpgradeDestination.Overwrite)
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

                        // Save to the chosen destination and note the saved model path to link from.
                        ModelPath savedMp;
                        if (Spec.Destination == UpgradeDestination.Cloud)
                        {
                            string cloudName = UniqueName(effectiveBaseName, usedNames);
                            // SaveAsCloudModel returns void — the resulting cloud ModelPath is read back
                            // from the document itself once the save has re-pointed it at the cloud.
                            linkDoc.SaveAsCloudModel(cloudFolder!, cloudName);
                            savedMp = linkDoc.GetCloudModelPath();
                        }
                        else
                        {
                            string destPath = Spec.Destination == UpgradeDestination.Overwrite
                                ? srcPath
                                : Path.Combine(destFolder!, UniqueFileName(effectiveBaseName + Path.GetExtension(srcPath), usedNames));
                            SaveLocal(linkDoc, destPath, isWs);
                            savedMp = ModelPathUtils.ConvertUserVisiblePathToModelPath(destPath);
                        }

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
                Spec = new UpgradeLinksSpec();
                HostFolder = null;
            }
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
                case UpgradeDestination.Overwrite: return LemoineStrings.T("upgradeLinks.summaries.destOverwrite");
                case UpgradeDestination.Cloud:     return LemoineStrings.T("upgradeLinks.summaries.destCloud");
                default:                           return LemoineStrings.T("upgradeLinks.summaries.destSubfolder", Spec.SubfolderName);
            }
        }

        // Resolves the host's own ACC folder by matching Document.GetHubId()/GetProjectId()/
        // GetCloudFolderId() (all strings — see UpgradeLinksModels.UpgradeLinksSpec) against the
        // real CloudHub/CloudProject/CloudFolder browsing API, so SaveAsCloudModel can be called
        // with an actual CloudFolder object rather than a guessed-at Guid. Folders can nest, so the
        // search walks the project's folder tree (capped — real ACC folder trees are shallow).
        private static CloudFolder? ResolveCloudFolder(string hubId, string projectId, string folderId)
        {
            if (string.IsNullOrEmpty(hubId) || string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(folderId))
                return null;
            try
            {
                var hub = CloudHub.GetAllHubs()
                    ?.FirstOrDefault(h => string.Equals(h.Id, hubId, StringComparison.Ordinal));
                var project = hub?.GetProjects()
                    ?.FirstOrDefault(p => string.Equals(p.Id, projectId, StringComparison.Ordinal));
                if (project == null) return null;
                return FindCloudFolder(project.GetFolders(), folderId, 0);
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("UpgradeLinks: resolve cloud folder", ex);
                return null;
            }
        }

        private static CloudFolder? FindCloudFolder(IList<CloudFolder>? folders, string folderId, int depth)
        {
            if (folders == null || depth > 8) return null;
            foreach (var f in folders)
                if (string.Equals(f.Id, folderId, StringComparison.Ordinal)) return f;
            foreach (var f in folders)
            {
                var found = FindCloudFolder(f.GetFolders(), folderId, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        private static string SanitizeFolder(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Upgraded Links";
            var cleaned = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
            return cleaned.Length == 0 ? "Upgraded Links" : cleaned;
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

        private static string UniqueName(string baseName, HashSet<string> used)
        {
            string candidate = baseName;
            int n = 2;
            while (!used.Add(candidate)) candidate = $"{baseName} ({n++})";
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
