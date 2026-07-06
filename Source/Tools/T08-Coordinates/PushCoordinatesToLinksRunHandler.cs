using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Coordinates
{
    /// <summary>
    /// Push Coordinates to Links run. For each selected link, reads its CURRENT host position
    /// (however it got there — Align Coordinates or a manual move) and commits that into the
    /// link's own file:
    ///   1. Compute what the link's own Project Base Point / Survey Point need to become (in the
    ///      link's own internal coordinates) so they match the host's, given the link's current
    ///      total transform.
    ///   2. Open the link file in the background (never an activated view). A workshared source
    ///      opens DETACHED with all worksets closed and is saved as a NEW file in a subfolder next
    ///      to the host — the live central model is never opened with worksharing enabled and
    ///      never synced. A non-workshared source is corrected and saved in place.
    ///   3. Move the base point(s) in that opened document, save, close.
    ///   4. Back in the host: reload the link type from the (possibly relocated) saved file,
    ///      publish the host's shared coordinates to it, then delete and recreate the link
    ///      instance with Shared Coordinates positioning — this is what makes the correction
    ///      actually take visual effect, since these links use Origin-to-Origin positioning
    ///      (a fixed per-instance transform that ignores base points and would not move on a
    ///      plain reload).
    ///
    /// If publishing fails, the recreate step is skipped and the original instance is left intact
    /// (never delete-then-fail-to-recreate — that would leave the link missing entirely).
    /// </summary>
    public sealed class PushCoordinatesToLinksRunHandler : IExternalEventHandler
    {
        // ── Run payload (set by the ViewModel before Raise) ──────────────────────
        public bool   MovePbp       { get; set; } = true;
        public bool   MoveSurvey    { get; set; } = true;
        public string SubfolderName { get; set; } = "Coordinated Links";
        public string? HostFolder   { get; set; }

        public List<PushLinkSpec> LinkSpecs { get; set; } = new List<PushLinkSpec>();

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Coordinates.PushCoordinatesToLinksRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        private const double Eps = 1e-9;

        public void Execute(UIApplication app)
        {
            int pass = 0, fail = 0, skip = 0;
            long issues0 = LemoineLog.IssueCount;
            try
            {
                var hostDoc = app.ActiveUIDocument?.Document;
                if (hostDoc == null) { Log("No active document.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                var toRun = (LinkSpecs ?? new List<PushLinkSpec>()).Where(s => s.Selected).ToList();
                if (toRun.Count == 0) { Log("No links selected.", "warn"); OnComplete?.Invoke(0, 0, 0); return; }

                bool movePbp = MovePbp, moveSurvey = MoveSurvey;
                var hostPbp    = BasePoint.GetProjectBasePoint(hostDoc);
                var hostSurvey = BasePoint.GetSurveyPoint(hostDoc);
                if (movePbp && hostPbp == null)
                {
                    Log("⚠ Host Project Base Point not found — Project Base Point correction skipped for every link.", "warn");
                    movePbp = false;
                }
                if (moveSurvey && hostSurvey == null)
                {
                    Log("⚠ Host Survey Point not found — Survey Point correction skipped for every link.", "warn");
                    moveSurvey = false;
                }
                if (!movePbp && !moveSurvey)
                {
                    Log("No host reference point available — nothing to push.", "fail");
                    OnComplete?.Invoke(0, 1, 0); return;
                }

                var appApp = app.Application;
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int total = toRun.Count, done = 0;

                foreach (var spec in toRun)
                {
                    if (LemoineRun.CancelRequested)
                    {
                        Log($"Stopped by user — {pass} link(s) pushed so far; work preserved.", "warn");
                        break;
                    }
                    done++;

                    try
                    {
                        var result = PushOneLink(hostDoc, appApp, spec, movePbp ? hostPbp : null, moveSurvey ? hostSurvey : null, usedNames);
                        if (result == PushResult.Pushed) pass++;
                        else if (result == PushResult.Skipped) skip++;
                        else fail++;
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        LemoineLog.Error("PushCoordinatesToLinks: process link", ex);
                        Log($"✗ {spec.LinkName}: {ex.Message}", "fail");
                    }

                    Progress(done, total, pass, fail, skip);
                }

                long issues = LemoineLog.IssuesSince(issues0);
                if (issues > 0) Log($"{issues} non-fatal issue(s) recorded — see diagnostics log.", "warn");
                Log($"Done. {pass} link(s) pushed, {skip} skipped, {fail} failed.", fail > 0 ? "warn" : "pass");
                OnProgress?.Invoke(100, pass, fail, skip);
                OnComplete?.Invoke(pass, fail, skip);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("PushCoordinatesToLinksRunHandler.Execute", ex);
                Log($"Run aborted: {ex.Message}", "fail");
                OnComplete?.Invoke(pass, fail + 1, skip);
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                LinkSpecs = new List<PushLinkSpec>();
            }
        }

        private enum PushResult { Pushed, Skipped, Failed }

        private PushResult PushOneLink(Document hostDoc, Autodesk.Revit.ApplicationServices.Application appApp,
            PushLinkSpec spec, BasePoint? hostPbp, BasePoint? hostSurvey, HashSet<string> usedNames)
        {
            var li = hostDoc.GetElement(new ElementId(spec.LinkInstId)) as RevitLinkInstance;
            if (li == null) { Log($"⚠ {spec.LinkName}: link instance no longer exists — skipped.", "warn"); return PushResult.Skipped; }

            var typeId = li.GetTypeId();
            var linkType = hostDoc.GetElement(typeId) as RevitLinkType;
            var extRef = linkType?.GetExternalFileReference();
            if (linkType == null || extRef == null)
            {
                Log($"⚠ {spec.LinkName}: could not resolve its source file — skipped.", "warn");
                return PushResult.Skipped;
            }

            string srcPath;
            try { srcPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetAbsolutePath()); }
            catch (Exception ex)
            {
                LemoineLog.Swallowed($"PushCoordinatesToLinks: resolve path for {spec.LinkName}", ex);
                Log($"⚠ {spec.LinkName}: could not resolve its source file path — skipped.", "warn");
                return PushResult.Skipped;
            }
            if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath))
            {
                Log($"⚠ {spec.LinkName}: source file not found on disk ({srcPath}) — skipped.", "warn");
                return PushResult.Skipped;
            }

            // ── Compute the link-internal target(s) from its CURRENT host position ──
            var t = li.GetTotalTransform();
            XYZ? pbpTargetInternal    = hostPbp    != null ? t.Inverse.OfPoint(hostPbp.Position)    : null;
            XYZ? surveyTargetInternal = hostSurvey != null ? t.Inverse.OfPoint(hostSurvey.Position) : null;

            bool isWs = false;
            try { var bfi = BasicFileInfo.Extract(srcPath); isWs = bfi != null && bfi.IsWorkshared; }
            catch (Exception ex) { LemoineLog.Swallowed("PushCoordinatesToLinks: BasicFileInfo", ex); }

            string fileName = Path.GetFileName(srcPath);

            var oo = new OpenOptions { Audit = false };
            if (isWs)
            {
                oo.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                oo.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets));
            }

            Document? linkedOpen = null;
            try
            {
                var srcMp = ModelPathUtils.ConvertUserVisiblePathToModelPath(srcPath);
                linkedOpen = appApp.OpenDocumentFile(srcMp, oo);
                if (linkedOpen == null)
                {
                    Log($"✗ {spec.LinkName}: could not open its source file — skipped.", "fail");
                    return PushResult.Failed;
                }

                using (var tx = new Transaction(linkedOpen, "Correct Base Points"))
                {
                    tx.Start();
                    ConfigureFailures(tx);
                    if (pbpTargetInternal != null)
                        MoveBasePoint(linkedOpen, BasePoint.GetProjectBasePoint(linkedOpen), pbpTargetInternal, "Project Base Point");
                    if (surveyTargetInternal != null)
                        MoveBasePoint(linkedOpen, BasePoint.GetSurveyPoint(linkedOpen), surveyTargetInternal, "Survey Point");
                    tx.Commit();
                }

                string savedPath;
                if (isWs)
                {
                    if (string.IsNullOrWhiteSpace(HostFolder))
                    {
                        Log($"✗ {spec.LinkName}: host is not saved to disk — no folder to save the workshared copy into.", "fail");
                        return PushResult.Failed;
                    }
                    string destFolder = Path.Combine(HostFolder, SanitizeFolder(SubfolderName));
                    Directory.CreateDirectory(destFolder);
                    savedPath = Path.Combine(destFolder, UniqueFileName(fileName, usedNames));

                    var so = new SaveAsOptions { OverwriteExistingFile = true };
                    so.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                    linkedOpen.SaveAs(savedPath, so);
                }
                else
                {
                    savedPath = srcPath;
                    linkedOpen.SaveAs(savedPath, new SaveAsOptions { OverwriteExistingFile = true });
                }

                try { linkedOpen.Close(false); }
                catch (Exception ex) { LemoineLog.Swallowed("PushCoordinatesToLinks: close linked doc", ex); }
                linkedOpen = null;

                var savedMp = ModelPathUtils.ConvertUserVisiblePathToModelPath(savedPath);

                // ── Reload the link type from the corrected (possibly relocated) file ──
                // ⚠ Assumes LoadFrom accepts repointing to a different path than the type's current
                // one (same call UpgradeLinksRunHandler.ReloadExistingType uses for a same-path
                // reload) — unverified for a changed path until tested on Windows/Revit.
                using (var tx = new Transaction(hostDoc, "Reload Link From Corrected File"))
                {
                    tx.Start();
                    ConfigureFailures(tx);
                    linkType.LoadFrom(savedMp, new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
                    tx.Commit();
                }

                // ── Publish shared coordinates, then re-place the instance to pick them up ──
                bool published = false;
                using (var tx = new Transaction(hostDoc, "Publish Shared Coordinates"))
                {
                    tx.Start();
                    ConfigureFailures(tx);
                    try
                    {
                        hostDoc.PublishCoordinates(new LinkElementId(new ElementId(spec.LinkInstId), ElementId.InvalidElementId));
                        published = true;
                    }
                    catch (Exception ex)
                    {
                        LemoineLog.Swallowed($"PushCoordinatesToLinks: publish coordinates for {spec.LinkName}", ex);
                        Log($"✗ {spec.LinkName}: reloaded the corrected file but could not publish shared coordinates ({ex.Message}) — link left as-is.", "fail");
                    }
                    tx.Commit();
                }
                if (!published) return PushResult.Failed;

                using (var tx = new Transaction(hostDoc, "Reposition Link via Shared Coordinates"))
                {
                    tx.Start();
                    ConfigureFailures(tx);

                    bool wasPinned = false;
                    try { wasPinned = li.Pinned; if (wasPinned) li.Pinned = false; }
                    catch (Exception ex) { LemoineLog.Swallowed($"PushCoordinatesToLinks: unpin {spec.LinkName}", ex); }

                    hostDoc.Delete(li.Id);
                    var newInst = RevitLinkInstance.Create(hostDoc, typeId, ImportPlacement.Shared);

                    try { if (wasPinned) newInst.Pinned = true; }
                    catch (Exception ex) { LemoineLog.Swallowed($"PushCoordinatesToLinks: re-pin {spec.LinkName}", ex); }

                    tx.Commit();
                }

                Log($"✓ {spec.LinkName}: corrected in its own file and re-placed via Shared Coordinates.", "pass");
                return PushResult.Pushed;
            }
            finally
            {
                if (linkedOpen != null)
                {
                    try { linkedOpen.Close(false); }
                    catch (Exception ex) { LemoineLog.Swallowed("PushCoordinatesToLinks: close linked doc (finally)", ex); }
                }
            }
        }

        private void MoveBasePoint(Document doc, BasePoint? bp, XYZ targetInternal, string label)
        {
            if (bp == null) { Log($"⚠ {label} not found in the link's own document.", "warn"); return; }
            try
            {
                bool wasPinned = bp.Pinned;
                if (wasPinned) bp.Pinned = false;

                var delta = targetInternal - bp.Position;
                if (delta.GetLength() > Eps)
                    ElementTransformUtils.MoveElement(doc, bp.Id, delta);

                if (wasPinned) bp.Pinned = true;
            }
            catch (Exception ex)
            {
                LemoineLog.Error($"PushCoordinatesToLinks: move {label}", ex);
                Log($"⚠ Could not move the link's own {label}: {ex.Message}", "warn");
            }
        }

        // ── helpers ──────────────────────────────────────────────────────────────
        private void Progress(int done, int total, int pass, int fail, int skip)
        {
            int pct = total > 0 ? (int)(done * 100.0 / total) : 100;
            OnProgress?.Invoke(pct, pass, fail, skip);
        }

        private static string SanitizeFolder(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Coordinated Links";
            var cleaned = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
            return cleaned.Length == 0 ? "Coordinated Links" : cleaned;
        }

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
            catch (Exception ex) { LemoineLog.Swallowed("PushCoordinatesToLinks: configure failure handling", ex); }
        }
    }
}
