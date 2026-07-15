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
    /// Push Coordinates to Links run. For each selected link, reads its CURRENT host position
    /// (however it got there — Align Coordinates or a manual move) and commits that into the
    /// link's own file:
    ///   1. Compute what the link's own Project Base Point / Survey Point need to become (in the
    ///      link's own internal coordinates) so they match the host's, given the link's current
    ///      total transform.
    ///   2. Unload the link from the host first. A file that is currently loaded as a link in this
    ///      session cannot be opened as an independent, transactable document — Application.OpenDocumentFile
    ///      on that same path just hands back the existing linked Document object, which throws
    ///      "Document is a linked file. Transactions can only be used in primary documents" on any
    ///      Transaction (confirmed on a real project run). Unloading first releases it so the
    ///      background open (never an activated view) returns a genuine standalone document.
    ///   3. Move the base point(s) in that opened document. The moves are all-or-nothing: if any
    ///      requested point can't be moved, the transaction rolls back and the file is closed
    ///      WITHOUT saving — a link is never saved/synced (and never reported pushed) unless the
    ///      correction actually happened. A workshared source is corrected and Synchronized With
    ///      Central in place (never detached, never saved to a copy — the whole point is to
    ///      correct the team's actual central model); a non-workshared source is corrected and
    ///      saved in place. Close.
    ///   4. Back in the host: reload the link type from the corrected file, publish the host's
    ///      shared coordinates to it, then delete and recreate the link instance with Shared
    ///      Coordinates positioning — this is what makes the correction actually take visual
    ///      effect, since these links use Origin-to-Origin positioning (a fixed per-instance
    ///      transform that ignores base points and would not move on a plain reload). Any host
    ///      elements that depended on the old instance (dimensions, tags, overrides) are deleted
    ///      with it — the run counts and reports them.
    ///
    /// A file placed in the host more than once is pushed through ONE instance only — publishing
    /// and re-placing per instance of the same file would fight over the file's single shared
    /// position — and the remaining placements are skipped with a log line telling the user to
    /// reposition them manually.
    ///
    /// If the link fails after being unloaded (for any reason), a best-effort reload is attempted
    /// so the host is never left with a link silently missing. If publishing fails, the recreate
    /// step is skipped and the original instance is left intact (never delete-then-fail-to-recreate
    /// — that would leave the link missing entirely).
    /// </summary>
    public sealed class PushCoordinatesToLinksRunHandler : IExternalEventHandler
    {
        // ── Run payload (set by the ViewModel before Raise) ──────────────────────
        public bool MovePbp    { get; set; } = true;
        public bool MoveSurvey { get; set; } = true;

        public List<PushLinkSpec> LinkSpecs { get; set; } = new List<PushLinkSpec>();

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Setup.PushCoordinatesToLinksRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);
        private static string T(string key, params object[] args) => AppStrings.T("setup.pushCoordinates." + key, args);

        private const double Eps = 1e-9;

        public void Execute(UIApplication app)
        {
            int pass = 0, fail = 0, skip = 0;
            long issues0 = DiagnosticsLog.IssueCount;
            try
            {
                var hostDoc = app.ActiveUIDocument?.Document;
                if (hostDoc == null) { Log(T("log.noDoc"), "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                var toRun = (LinkSpecs ?? new List<PushLinkSpec>()).Where(s => s.Selected).ToList();
                if (toRun.Count == 0) { Log(T("log.noLinksSelected"), "warn"); OnComplete?.Invoke(0, 0, 0); return; }

                bool movePbp = MovePbp, moveSurvey = MoveSurvey;
                var hostPbp    = BasePoint.GetProjectBasePoint(hostDoc);
                var hostSurvey = BasePoint.GetSurveyPoint(hostDoc);
                if (movePbp && hostPbp == null)
                {
                    Log(T("log.hostPbpMissing"), "warn");
                    movePbp = false;
                }
                if (moveSurvey && hostSurvey == null)
                {
                    Log(T("log.hostSurveyMissing"), "warn");
                    moveSurvey = false;
                }
                if (!movePbp && !moveSurvey)
                {
                    Log(T("log.noPoints"), "fail");
                    OnComplete?.Invoke(0, 1, 0); return;
                }

                // A file placed more than once shares one link type — publishing/re-placing is
                // per FILE, so only one instance per type can be pushed (see class doc comment).
                var typeCounts = new Dictionary<long, int>();
                foreach (var spec in toRun)
                {
                    var inst = hostDoc.GetElement(new ElementId(spec.LinkInstId)) as RevitLinkInstance;
                    if (inst == null) continue;
                    long tid = inst.GetTypeId().Value;
                    typeCounts[tid] = typeCounts.TryGetValue(tid, out var n) ? n + 1 : 1;
                }
                var pushedTypes = new HashSet<long>();

                var appApp = app.Application;
                int total = toRun.Count, done = 0;

                foreach (var spec in toRun)
                {
                    if (RunState.CancelRequested)
                    {
                        Log(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                        break;
                    }
                    done++;

                    try
                    {
                        // Same-file duplicate guard, re-resolved here so a stale instance still
                        // falls through to PushOneLink's own "no longer exists" skip.
                        var inst = hostDoc.GetElement(new ElementId(spec.LinkInstId)) as RevitLinkInstance;
                        long tid = inst?.GetTypeId().Value ?? 0;
                        if (inst != null && typeCounts.TryGetValue(tid, out var placedCount) && placedCount > 1)
                        {
                            if (pushedTypes.Contains(tid))
                            {
                                skip++;
                                Log(T("log.multiInstanceSkipped", spec.LinkName), "warn");
                                Progress(done, total, pass, fail, skip);
                                continue;
                            }
                            Log(T("log.multiInstance", spec.LinkName, placedCount), "warn");
                        }
                        if (inst != null) pushedTypes.Add(tid);

                        var result = PushOneLink(hostDoc, appApp, spec, movePbp ? hostPbp : null, moveSurvey ? hostSurvey : null);
                        if (result == PushResult.Pushed) pass++;
                        else if (result == PushResult.Skipped) skip++;
                        else fail++;
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        DiagnosticsLog.Error("PushCoordinatesToLinks: process link", ex);
                        Log(T("log.linkFail", spec.LinkName, ex.Message), "fail");
                    }

                    Progress(done, total, pass, fail, skip);
                }

                long issues = DiagnosticsLog.IssuesSince(issues0);
                if (issues > 0) Log(T("log.nonFatal", issues), "warn");
                Log(T("log.done", pass, skip, fail), fail > 0 ? "warn" : "pass");
                OnProgress?.Invoke(100, pass, fail, skip);
                OnComplete?.Invoke(pass, fail, skip);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("PushCoordinatesToLinksRunHandler.Execute", ex);
                Log(T("log.aborted", ex.Message), "fail");
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
            PushLinkSpec spec, BasePoint? hostPbp, BasePoint? hostSurvey)
        {
            var li = hostDoc.GetElement(new ElementId(spec.LinkInstId)) as RevitLinkInstance;
            if (li == null) { Log(T("log.instanceGone", spec.LinkName), "warn"); return PushResult.Skipped; }

            var typeId = li.GetTypeId();
            var linkType = hostDoc.GetElement(typeId) as RevitLinkType;
            if (linkType == null) { Log(T("log.typeMissing", spec.LinkName), "warn"); return PushResult.Skipped; }

            string srcPath;
            try
            {
                var extRef = linkType.GetExternalFileReference();
                if (extRef == null) { Log(T("log.noExternalRef", spec.LinkName), "warn"); return PushResult.Skipped; }
                srcPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetAbsolutePath());
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"PushCoordinatesToLinks: resolve path for {spec.LinkName}", ex);
                Log(T("log.pathFail", spec.LinkName, ex.Message), "warn");
                return PushResult.Skipped;
            }
            if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath))
            {
                Log(T("log.fileMissing", spec.LinkName, srcPath), "warn");
                return PushResult.Skipped;
            }

            // ── Compute the link-internal target(s) from its CURRENT host position, before
            //    touching anything — this is ground truth regardless of how it got here.
            var t = li.GetTotalTransform();
            XYZ? pbpTargetInternal    = hostPbp    != null ? t.Inverse.OfPoint(hostPbp.Position)    : null;
            XYZ? surveyTargetInternal = hostSurvey != null ? t.Inverse.OfPoint(hostSurvey.Position) : null;

            bool isWs = false;
            try { var bfi = BasicFileInfo.Extract(srcPath); isWs = bfi != null && bfi.IsWorkshared; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("PushCoordinatesToLinks: BasicFileInfo", ex); }

            // ── Unload the link so the file is no longer "in use" as a link in this session ──
            // ⚠ Unverified on Windows: assumes Unload() releases the in-memory link document so a
            // subsequent OpenDocumentFile on the same path returns a genuine standalone, transactable
            // Document rather than the same "this is a linked file" object.
            bool unloaded = false;
            try
            {
                // RevitLinkType.Unload/Load/Reload/LoadFrom are link-management calls that must run
                // OUTSIDE any transaction — they manage the document themselves and throw "The
                // operation is not permitted when there is any open transaction phase started by API
                // client" if one is open (confirmed on a Windows run). NO Transaction here.
                linkType.Unload(null);
                unloaded = true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"PushCoordinatesToLinks: unload {spec.LinkName}", ex);
                Log(T("log.unloadFail", spec.LinkName, ex.Message), "fail");
                return PushResult.Failed;
            }

            try
            {
                Document? linkedOpen = null;
                try
                {
                    var srcMp = ModelPathUtils.ConvertUserVisiblePathToModelPath(srcPath);
                    var oo = new OpenOptions { Audit = false };
                    linkedOpen = appApp.OpenDocumentFile(srcMp, oo);
                    if (linkedOpen == null)
                    {
                        Log(T("log.openFail", spec.LinkName), "fail");
                        return PushResult.Failed;
                    }

                    // All-or-nothing: a link whose points can't ALL be corrected must not be
                    // saved (or synced) at all — otherwise the run would sync a central file
                    // with a history comment claiming a correction that never happened.
                    bool corrected;
                    using (var tx = new Transaction(linkedOpen, "Correct Base Points"))
                    {
                        tx.Start();
                        ConfigureFailures(tx);
                        bool pbpOk = pbpTargetInternal == null ||
                            MoveBasePoint(linkedOpen, BasePoint.GetProjectBasePoint(linkedOpen), pbpTargetInternal, T("labels.projectBasePoint"));
                        bool surveyOk = surveyTargetInternal == null ||
                            MoveBasePoint(linkedOpen, BasePoint.GetSurveyPoint(linkedOpen), surveyTargetInternal, T("labels.surveyPoint"));
                        corrected = pbpOk && surveyOk;
                        if (corrected) tx.Commit(); else tx.RollBack();
                    }
                    if (!corrected)
                    {
                        Log(T("log.notCorrected", spec.LinkName), "fail");
                        return PushResult.Failed;   // finally closes without saving; recovery reload restores the link
                    }

                    if (isWs)
                    {
                        // ⚠ Unverified on Windows: SynchronizeWithCentral on a background-opened
                        // (never activated) Document. If this turns out to require the document to be
                        // the foreground/active one, switch to UIApplication.OpenAndActivateDocument
                        // and restore the original active document afterward.
                        var transactOpts = new TransactWithCentralOptions();
                        var syncOpts = new SynchronizeWithCentralOptions { Comment = "Lemoine Tools: corrected Project Base Point / Survey Point" };
                        syncOpts.SetRelinquishOptions(new RelinquishOptions(true));
                        linkedOpen.SynchronizeWithCentral(transactOpts, syncOpts);
                    }
                    else
                    {
                        linkedOpen.Save();
                    }
                }
                finally
                {
                    if (linkedOpen != null)
                    {
                        try { linkedOpen.Close(false); }
                        catch (Exception ex) { DiagnosticsLog.Swallowed("PushCoordinatesToLinks: close linked doc", ex); }
                    }
                }

                var srcMpForReload = ModelPathUtils.ConvertUserVisiblePathToModelPath(srcPath);

                // LoadFrom is a link-management call — OUTSIDE any transaction (same rule as Unload
                // above). Wrapping it in a Transaction throws the same "open transaction" error.
                linkType.LoadFrom(srcMpForReload, new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
                unloaded = false;   // reloaded — no longer needs the recovery reload below

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
                        DiagnosticsLog.Swallowed($"PushCoordinatesToLinks: publish coordinates for {spec.LinkName}", ex);
                        Log(T("log.publishFail", spec.LinkName, ex.Message), "fail");
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
                    catch (Exception ex) { DiagnosticsLog.Swallowed($"PushCoordinatesToLinks: unpin {spec.LinkName}", ex); }

                    // Deleting the instance also deletes host elements that depended on it
                    // (dimensions, tags, overrides). Count and report them — silently losing
                    // the user's annotations is worse than the scary-looking number.
                    var deleted = hostDoc.Delete(li.Id);
                    int collateral = deleted != null ? deleted.Count(id => id != li.Id) : 0;
                    if (collateral > 0)
                        Log(T("log.collateral", spec.LinkName, collateral), "warn");

                    var newInst = RevitLinkInstance.Create(hostDoc, typeId, ImportPlacement.Shared);

                    try { if (wasPinned) newInst.Pinned = true; }
                    catch (Exception ex) { DiagnosticsLog.Swallowed($"PushCoordinatesToLinks: re-pin {spec.LinkName}", ex); }

                    tx.Commit();
                }

                Log(T("log.pushed", spec.LinkName), "pass");
                return PushResult.Pushed;
            }
            finally
            {
                // Best-effort recovery: if something above threw after the unload but before the
                // reload succeeded, don't leave the host with this link silently missing.
                if (unloaded)
                {
                    try
                    {
                        // LoadFrom OUTSIDE any transaction (link-management call — see the unload note).
                        linkType.LoadFrom(ModelPathUtils.ConvertUserVisiblePathToModelPath(srcPath),
                            new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Error($"PushCoordinatesToLinks: recovery reload for {spec.LinkName}", ex);
                        Log(T("log.recoveryFail", spec.LinkName), "warn");
                    }
                }
            }
        }

        /// <summary>
        /// Move a base point in the opened link document to <paramref name="targetInternal"/>.
        /// Returns false when the point is missing or the move throws — the caller treats any
        /// false as "this link was not corrected" and rolls the whole correction back. The pin
        /// state is restored in a finally so a failed move can't leave the point unpinned.
        /// </summary>
        private bool MoveBasePoint(Document doc, BasePoint? bp, XYZ targetInternal, string label)
        {
            if (bp == null) { Log(T("log.pointMissing", label), "warn"); return false; }

            bool wasPinned = false;
            try
            {
                wasPinned = bp.Pinned;
                if (wasPinned) bp.Pinned = false;

                var delta = targetInternal - bp.Position;
                if (delta.GetLength() > Eps)
                    ElementTransformUtils.MoveElement(doc, bp.Id, delta);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"PushCoordinatesToLinks: move {label}", ex);
                Log(T("log.pointFail", label, ex.Message), "warn");
                return false;
            }
            finally
            {
                try { if (wasPinned) bp.Pinned = true; }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"PushCoordinatesToLinks: re-pin {label}", ex); }
            }
        }

        // ── helpers ──────────────────────────────────────────────────────────────
        private void Progress(int done, int total, int pass, int fail, int skip)
        {
            int pct = total > 0 ? (int)(done * 100.0 / total) : 100;
            OnProgress?.Invoke(pct, pass, fail, skip);
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
            catch (Exception ex) { DiagnosticsLog.Swallowed("PushCoordinatesToLinks: configure failure handling", ex); }
        }
    }
}
