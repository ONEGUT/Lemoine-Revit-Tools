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
    /// Replace Link run. For each queued replacement: capture where the existing link sits →
    /// back up the file it points at → unload it → open the new file (which upgrades it) and save
    /// it over that path → re-point the SAME <see cref="RevitLinkType"/> at the saved copy →
    /// measure how far the model actually moved, and optionally re-seat it.
    ///
    /// <para><b>The type is reused, never deleted and recreated.</b> Reloading keeps the type's
    /// <see cref="ElementId"/>, and with it every instance and its transform, per-view visibility
    /// and graphic overrides, view filters referencing the link, copy/monitor relationships, phase
    /// mapping, workset assignment and the Manage Links row. Deleting and re-linking would mint a
    /// new id and silently detach all of it.</para>
    ///
    /// <para>Transaction discipline (CLAUDE.md): <see cref="RevitLinkType.Unload"/>,
    /// <see cref="RevitLinkType.Reload()"/> and <see cref="RevitLinkType.LoadFrom(ModelPath, WorksetConfiguration)"/>
    /// are link-management calls and must run with NO transaction open; only the reposition step
    /// opens one.</para>
    /// </summary>
    public sealed class ReplaceLinkRunHandler : IExternalEventHandler
    {
        // ── Inputs (set before Raise) ─────────────────────────────────────────
        public ReplaceLinkSpec Spec { get; set; } = new ReplaceLinkSpec();

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        /// <summary>Below this, the instance is already where it was — moving it would be
        /// floating-point noise, not a correction.</summary>
        private const double MoveEpsilonFt = 1e-9;

        /// <summary>Cross-check tolerance for the second base point (~1/64"). Only gates a
        /// warning; the distance reported is always the real measured value.</summary>
        private const double PointMatchToleranceFt = 1.0 / 768.0;

        public string GetName() => "LemoineTools.Tools.Setup.ReplaceLinkRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        // Links that were replaced but whose position could not be verified — surfaced in the
        // run's closing lines so it survives a long scroll-back, not just at the point of failure.
        private int _unverified;

        /// <summary>Where one link instance sat before the swap, in HOST internal coordinates.
        /// This is the fingerprint that makes "did it land in the same place?" answerable.</summary>
        private sealed class InstanceAnchor
        {
            public ElementId Id      = ElementId.InvalidElementId;
            public XYZ?      Pbp;        // link's Project Base Point, through the instance transform
            public XYZ?      Survey;     // link's Survey Point, likewise
        }

        public void Execute(UIApplication app)
        {
            int pass = 0, fail = 0, skip = 0;
            _unverified = 0;
            long issues0 = DiagnosticsLog.IssueCount;
            try
            {
                var hostDoc = app.ActiveUIDocument?.Document;
                if (hostDoc == null)
                {
                    Log(AppStrings.T("replaceLink.log.noDoc"), "fail");
                    OnComplete?.Invoke(0, 1, 0);
                    return;
                }

                var items = Spec.Items ?? new List<ReplaceItem>();
                if (items.Count == 0)
                {
                    Log(AppStrings.T("replaceLink.log.noItems"), "warn");
                    OnComplete?.Invoke(0, 0, 0);
                    return;
                }

                // Resolve the SelectedFolder destination up front so a bad path fails before any
                // link is unloaded.
                string? destFolder = null;
                if (Spec.Destination == ReplaceDestination.SelectedFolder)
                {
                    if (string.IsNullOrWhiteSpace(Spec.SelectedFolder))
                    {
                        Log(AppStrings.T("replaceLink.log.noSelectedFolder"), "fail");
                        OnComplete?.Invoke(0, 1, 0);
                        return;
                    }
                    destFolder = Spec.SelectedFolder;
                    try { Directory.CreateDirectory(destFolder); }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Error("ReplaceLink: create selected folder", ex);
                        Log(AppStrings.T("replaceLink.log.folderFail", destFolder, ex.Message), "fail");
                        OnComplete?.Invoke(0, 1, 0);
                        return;
                    }
                }

                Log(AppStrings.T("replaceLink.log.start", items.Count, DestLabel(destFolder)), "info");

                int total = items.Count, done = 0;
                foreach (var item in items)
                {
                    if (RunState.CancelRequested)
                    {
                        Log(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                        break;   // links already swapped stay swapped — nothing to roll back
                    }

                    done++;
                    var outcome = ProcessOne(app, hostDoc, item, destFolder, done, total);
                    if      (outcome == Outcome.Replaced) pass++;
                    else if (outcome == Outcome.Skipped)  skip++;
                    else                                  fail++;

                    Progress(done, total, pass, fail, skip);
                }

                if (_unverified > 0) Log(AppStrings.T("replaceLink.log.unverifiedSummary", _unverified), "fail");

                long issues = DiagnosticsLog.IssuesSince(issues0);
                if (issues > 0) Log(AppStrings.T("replaceLink.log.nonFatal", issues), "warn");
                Log(AppStrings.T("replaceLink.log.done", pass, skip, fail), fail > 0 ? "warn" : "pass");
                OnProgress?.Invoke(100, pass, fail, skip);
                OnComplete?.Invoke(pass, fail, skip);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ReplaceLinkRunHandler.Execute", ex);
                Log(AppStrings.T("replaceLink.log.aborted", ex.Message), "fail");
                OnComplete?.Invoke(pass, fail + 1, skip);
            }
            finally
            {
                // Session-long static handler — drop the run's payload (CLAUDE.md memory discipline).
                Spec = new ReplaceLinkSpec();
            }
        }

        private enum Outcome { Replaced, Skipped, Failed }

        private Outcome ProcessOne(UIApplication app, Document hostDoc, ReplaceItem item,
                                   string? destFolder, int index, int total)
        {
            string label = string.IsNullOrEmpty(item.LinkName)
                ? AppStrings.T("replaceLink.log.unnamedLink")
                : item.LinkName;

            var typeId = new ElementId(item.TypeId);
            RevitLinkType? type;
            try { type = hostDoc.GetElement(typeId) as RevitLinkType; }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"ReplaceLink: resolve link type for '{label}'", ex);
                Log(AppStrings.T("replaceLink.log.typeGone", label), "fail");
                return Outcome.Failed;
            }
            if (type == null)
            {
                // The link was deleted between building the queue and running.
                Log(AppStrings.T("replaceLink.log.typeGone", label), "fail");
                return Outcome.Failed;
            }

            // ── Resolve the path the link currently points at ────────────────────
            string oldPath;
            try
            {
                var extRef = type.GetExternalFileReference();
                if (extRef == null)
                {
                    Log(AppStrings.T("replaceLink.log.noReference", label), "fail");
                    return Outcome.Failed;
                }
                oldPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetAbsolutePath()) ?? "";
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"ReplaceLink: read external reference for '{label}'", ex);
                Log(AppStrings.T("replaceLink.log.noReference", label), "fail");
                return Outcome.Failed;
            }

            if (string.IsNullOrEmpty(oldPath))
            {
                // Cloud-hosted (or otherwise unresolvable) — there is no local file to write over.
                Log(AppStrings.T("replaceLink.log.cloudUnsupported", label), "warn");
                return Outcome.Skipped;
            }

            string newPath = item.NewFilePath;
            if (!File.Exists(newPath))
            {
                Log(AppStrings.T("replaceLink.log.newFileMissing", label, newPath), "warn");
                return Outcome.Skipped;
            }

            // ── Where the upgraded copy goes ─────────────────────────────────────
            string destPath;
            try { destPath = ResolveDestPath(item, oldPath, destFolder, label); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"ReplaceLink: resolve destination for '{label}'", ex);
                Log(AppStrings.T("replaceLink.log.destFail", label, ex.Message), "fail");
                return Outcome.Failed;
            }

            bool overwritingLinked = string.Equals(destPath, oldPath, StringComparison.OrdinalIgnoreCase);

            // ── Capture where the link sits right now ────────────────────────────
            var anchors = CaptureAnchors(hostDoc, typeId, label);
            if (anchors.Count == 0)
            {
                // No loaded link document (unloaded/not found) — the swap can still proceed, but
                // movement can neither be measured nor corrected. Say so rather than implying it.
                Log(AppStrings.T("replaceLink.log.noAnchors", label), "warn");
            }

            // ── Back up the file about to be overwritten ─────────────────────────
            if (Spec.BackupOriginal && overwritingLinked)
            {
                if (!BackupOriginal(oldPath, label)) return Outcome.Failed;
            }

            // ── Release the old file so it can be written over ───────────────────
            // Unload is a link-management call — NO transaction may be open (CLAUDE.md).
            bool unloaded = false;
            try
            {
                type.Unload(null);
                unloaded = true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"ReplaceLink: unload '{label}'", ex);
                Log(AppStrings.T("replaceLink.log.unloadFail", label, ex.Message), "fail");
                return Outcome.Failed;
            }

            // ── Release the replacement file if it is ALSO loaded as another link ──
            // A file already loaded as a link in this host can't be opened as a standalone
            // document — OpenDocumentFile hands back the linked doc, whose SaveAs then throws.
            // (Same constraint Upgrade Links works around.) Unloaded here, re-loaded below.
            var otherLink = UnloadIfLoadedAsOtherLink(hostDoc, newPath, typeId, label);

            // ── Upgrade + save the replacement file ──────────────────────────────
            bool saved = UpgradeAndSave(app, newPath, destPath, label);

            // Put the other link back regardless of the outcome — it is unrelated to this
            // replacement and must not be left unloaded as a side effect of it.
            ReloadOtherLink(hostDoc, otherLink, label);

            if (!saved)
            {
                // The link is unloaded and nothing replaced it — restore it rather than leaving
                // the model silently missing a link.
                if (unloaded) RestoreOriginal(type, oldPath, label);
                return Outcome.Failed;
            }

            // ── Re-point the SAME type at the saved copy ─────────────────────────
            // Also outside any transaction, for the same reason as Unload.
            if (!RePoint(type, destPath, overwritingLinked, label))
            {
                if (unloaded) RestoreOriginal(type, oldPath, label);
                return Outcome.Failed;
            }

            Log(overwritingLinked
                    ? AppStrings.T("replaceLink.log.replaced", index, total, label, Path.GetFileName(newPath))
                    : AppStrings.T("replaceLink.log.replacedRenamed", index, total, label,
                                   Path.GetFileName(newPath), Path.GetFileName(destPath)),
                "info");

            // ── Measure movement, and re-seat if asked ───────────────────────────
            ReconcilePosition(hostDoc, typeId, anchors, label);
            return Outcome.Replaced;
        }

        // ── Destination ──────────────────────────────────────────────────────────
        private string ResolveDestPath(ReplaceItem item, string oldPath, string? destFolder, string label)
        {
            if (Spec.Destination == ReplaceDestination.OverwriteLinkedFile) return oldPath;

            string ext      = Path.GetExtension(oldPath);
            string fallback = Path.GetFileNameWithoutExtension(oldPath);
            string requested = string.IsNullOrWhiteSpace(item.SaveAsName) ? fallback : item.SaveAsName.Trim();
            string sanitized = SanitizeBaseName(requested);

            // A resolved name with no alphanumeric character is a failure, not a silent fallback.
            if (sanitized.Length == 0 || !sanitized.Any(char.IsLetterOrDigit))
            {
                Log(AppStrings.T("replaceLink.log.renameInvalid", label), "warn");
                DiagnosticsLog.Warn("ReplaceLink: rename resolved to an unusable name", $"{label} -> '{requested}'");
                sanitized = fallback;
            }

            string folder = Spec.Destination == ReplaceDestination.SelectedFolder
                ? destFolder!
                : (Path.GetDirectoryName(oldPath) ?? "");

            return Path.Combine(folder, sanitized + ext);
        }

        // ── Capture / measure ────────────────────────────────────────────────────
        private List<InstanceAnchor> CaptureAnchors(Document hostDoc, ElementId typeId, string label)
        {
            var anchors = new List<InstanceAnchor>();
            try
            {
                foreach (var inst in InstancesOf(hostDoc, typeId))
                {
                    var a = new InstanceAnchor { Id = inst.Id };
                    try
                    {
                        var t = inst.GetTotalTransform();
                        var linkDoc = inst.GetLinkDocument();
                        if (linkDoc != null && t != null)
                        {
                            var pbp = BasePoint.GetProjectBasePoint(linkDoc);
                            var svy = BasePoint.GetSurveyPoint(linkDoc);
                            if (pbp != null) a.Pbp    = t.OfPoint(pbp.Position);
                            if (svy != null) a.Survey = t.OfPoint(svy.Position);
                        }
                    }
                    catch (Exception ex)
                    { DiagnosticsLog.Swallowed($"ReplaceLink: capture base points for '{label}'", ex); }

                    // Base points are the only anchor the re-seat can use, so an instance with
                    // neither read is not worth carrying — the caller reports the whole link as
                    // un-seatable instead.
                    if (a.Pbp != null || a.Survey != null) anchors.Add(a);
                }
            }
            catch (Exception ex)
            { DiagnosticsLog.Error($"ReplaceLink: capture anchors for '{label}'", ex); }
            return anchors;
        }

        /// <summary>
        /// Re-seats every instance of the replaced link onto the Survey Point / Project Base
        /// Point positions captured from the OLD link, then reports the result. This is not
        /// optional: reloading preserves the instance transform, which only lands the geometry
        /// correctly when the new file shares the old file's internal origin. Measuring against
        /// the captured points and correcting is what makes "the same place" true for any file.
        ///
        /// <para>The Survey Point is the primary anchor (it is what shared coordinates are hung
        /// off); the Project Base Point is the fallback when the survey point can't be read, and
        /// otherwise a cross-check. If correcting on one point leaves the other off its captured
        /// position, the two files disagree about their own internal PBP-to-SP relationship — no
        /// single translation can satisfy both, so the residual is reported rather than hidden.</para>
        ///
        /// <para>Runs in its own transaction: everything before it (unload / reload / load-from)
        /// had to be transaction-free.</para>
        /// </summary>
        private void ReconcilePosition(Document hostDoc, ElementId typeId, List<InstanceAnchor> anchors, string label)
        {
            if (anchors.Count == 0)
            {
                // Nothing was captured (link wasn't loaded) — say so, and count it as unverified.
                Log(AppStrings.T("replaceLink.log.noRealign", label), "fail");
                _unverified++;
                return;
            }

            try
            {
                using (var tx = new Transaction(hostDoc, "Re-seat Replaced Link"))
                {
                    tx.Start();
                    ConfigureFailures(tx);

                    // The link was just re-pointed — regenerate so the new document's base points
                    // and bounding boxes read true. Regenerate requires an open transaction,
                    // which is why it lives here.
                    try { hostDoc.Regenerate(); }
                    catch (Exception ex) { DiagnosticsLog.Swallowed($"ReplaceLink: regenerate for '{label}'", ex); }

                    var byId = anchors.ToDictionary(a => a.Id.Value, a => a);
                    bool moved = false;

                    foreach (var inst in InstancesOf(hostDoc, typeId))
                    {
                        if (!byId.TryGetValue(inst.Id.Value, out var before)) continue;
                        if (RealignInstance(hostDoc, inst, before, label)) moved = true;
                    }

                    if (moved)
                    {
                        try { hostDoc.Regenerate(); }
                        catch (Exception ex) { DiagnosticsLog.Swallowed($"ReplaceLink: regenerate after re-seat for '{label}'", ex); }
                    }
                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                // The file swap itself already succeeded, so this is not a failed replacement —
                // but the link's position was never verified, which is the one thing this tool
                // exists to guarantee. Reported at "fail" severity so it can't be mistaken for a
                // routine warning, and named as unverified rather than as a completed check.
                DiagnosticsLog.Error($"ReplaceLink: reconcile position for '{label}'", ex);
                Log(AppStrings.T("replaceLink.log.reconcileFail", label, ex.Message), "fail");
                _unverified++;
            }
        }

        /// <summary>Moves one instance so its base points return to the captured positions.
        /// Returns true when the instance was actually moved.</summary>
        private bool RealignInstance(Document hostDoc, RevitLinkInstance inst, InstanceAnchor before, string label)
        {
            var linkDoc = inst.GetLinkDocument();
            var t       = inst.GetTotalTransform();
            if (linkDoc == null || t == null)
            {
                Log(AppStrings.T("replaceLink.log.noRealign", label), "fail");
                _unverified++;
                return false;
            }

            // Where the NEW model's own points sit right now, in host coordinates.
            XYZ? newSurvey = PointInHost(linkDoc, t, survey: true,  label: label);
            XYZ? newPbp    = PointInHost(linkDoc, t, survey: false, label: label);

            // Survey Point first, Project Base Point as the fallback.
            XYZ? from = null, to = null;
            string anchorName;
            if (before.Survey != null && newSurvey != null)
            {
                from = newSurvey; to = before.Survey;
                anchorName = AppStrings.T("replaceLink.log.anchorSurvey");
            }
            else if (before.Pbp != null && newPbp != null)
            {
                from = newPbp; to = before.Pbp;
                anchorName = AppStrings.T("replaceLink.log.anchorPbp");
            }
            else
            {
                // Neither point is readable on both sides — the transform stands as reloaded and
                // nothing has been verified. Never silently accept that.
                Log(AppStrings.T("replaceLink.log.noRealign", label), "fail");
                _unverified++;
                return false;
            }

            XYZ delta = to - from;
            double dist = delta.GetLength();
            bool moved = false;

            if (dist > MoveEpsilonFt)
            {
                MoveInstance(hostDoc, inst, delta);
                moved = true;
                Log(AppStrings.T("replaceLink.log.realigned", label, FormatLength(hostDoc, dist), anchorName), "info");
            }
            else
            {
                Log(AppStrings.T("replaceLink.log.alreadyAligned", label, anchorName), "pass");
            }

            // Cross-check the OTHER point: after a pure translation it should also be back on its
            // captured position. If it isn't, the new file's internal Survey-to-PBP relationship
            // differs from the old file's and no single move can satisfy both.
            XYZ? otherNew    = ReferenceEquals(from, newSurvey) ? newPbp : newSurvey;
            XYZ? otherBefore = ReferenceEquals(from, newSurvey) ? before.Pbp : before.Survey;
            if (otherNew != null && otherBefore != null)
            {
                double residual = ((otherNew + delta) - otherBefore).GetLength();
                if (residual > PointMatchToleranceFt)
                {
                    Log(AppStrings.T("replaceLink.log.pointsDisagree", label,
                        FormatLength(hostDoc, residual)), "warn");
                }
            }

            return moved;
        }

        /// <summary>A link document's Survey Point or Project Base Point, expressed in the host's
        /// internal coordinates through the instance transform.</summary>
        private static XYZ? PointInHost(Document linkDoc, Transform t, bool survey, string label)
        {
            try
            {
                var bp = survey ? BasePoint.GetSurveyPoint(linkDoc) : BasePoint.GetProjectBasePoint(linkDoc);
                return bp != null ? t.OfPoint(bp.Position) : null;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"ReplaceLink: read {(survey ? "survey point" : "project base point")} for '{label}'", ex);
                return null;
            }
        }

        // ── File operations ──────────────────────────────────────────────────────
        /// <summary>Copies the file about to be overwritten into a <c>_Superseded</c> sibling
        /// folder with a timestamp. Returns false (and reports) when the backup fails — a failed
        /// backup must stop the replacement, since overwriting is otherwise irreversible.</summary>
        private bool BackupOriginal(string oldPath, string label)
        {
            try
            {
                string folder = Path.GetDirectoryName(oldPath) ?? "";
                string backupDir = Path.Combine(folder, "_Superseded");
                Directory.CreateDirectory(backupDir);
                string stamped = $"{Path.GetFileNameWithoutExtension(oldPath)}_{DateTime.Now:yyyyMMdd-HHmm}{Path.GetExtension(oldPath)}";
                string dest = Path.Combine(backupDir, stamped);

                // Two runs in the same minute would otherwise clobber the first backup.
                int n = 2;
                while (File.Exists(dest))
                {
                    dest = Path.Combine(backupDir,
                        $"{Path.GetFileNameWithoutExtension(oldPath)}_{DateTime.Now:yyyyMMdd-HHmm} ({n++}){Path.GetExtension(oldPath)}");
                }

                File.Copy(oldPath, dest);
                Log(AppStrings.T("replaceLink.log.backedUp", label, Path.GetFileName(dest)), "info");
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"ReplaceLink: back up '{label}'", ex);
                Log(AppStrings.T("replaceLink.log.backupFail", label, ex.Message), "fail");
                return false;
            }
        }

        /// <summary>Opens the replacement file (opening IS the upgrade) and saves it to
        /// <paramref name="destPath"/>. Background open only — an activated view pins its
        /// graphics in native RAM for the rest of the session (CLAUDE.md).</summary>
        private bool UpgradeAndSave(UIApplication app, string newPath, string destPath, string label)
        {
            Document? doc = null;
            try
            {
                bool isWs = false;
                try
                {
                    var bfi = BasicFileInfo.Extract(newPath);
                    isWs = bfi != null && bfi.IsWorkshared;
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"ReplaceLink: BasicFileInfo for '{label}'", ex); }

                var oo = new OpenOptions { Audit = Spec.AuditOnOpen };
                if (isWs)
                {
                    // Detached + all worksets closed: elements are never loaded into memory (the
                    // dominant RAM saver) yet are fully preserved on save.
                    oo.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                    oo.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets));
                }

                var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(newPath);
                doc = app.Application.OpenDocumentFile(mp, oo);
                if (doc == null)
                {
                    Log(AppStrings.T("replaceLink.log.openFail", label, Path.GetFileName(newPath)), "fail");
                    return false;
                }

                var so = new SaveAsOptions { OverwriteExistingFile = true };
                if (isWs)
                {
                    // A detached workshared doc is re-saved as a new central at the destination.
                    so.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                }
                doc.SaveAs(destPath, so);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"ReplaceLink: upgrade/save '{label}'", ex);
                Log(AppStrings.T("replaceLink.log.saveFail", label, ex.GetType().Name, ex.Message), "fail");
                return false;
            }
            finally
            {
                if (doc != null)
                {
                    try { doc.Close(false); }
                    catch (Exception ex)
                    {
                        // The file was already saved, so this doesn't fail the replacement — but
                        // the document stays open in this Revit session holding its memory for
                        // the rest of it, which the user has to know to act on.
                        DiagnosticsLog.Warn($"ReplaceLink: close upgraded doc for '{label}'", ex.Message);
                        Log(AppStrings.T("replaceLink.log.closeFail", label), "warn");
                    }
                }
            }
        }

        /// <summary>Points the existing link type at the saved copy — <see cref="RevitLinkType.Reload()"/>
        /// when the file was written back over the same path, <c>LoadFrom</c> when it was written
        /// somewhere else. Never a delete-and-recreate: the type's id (and everything hanging off
        /// it) has to survive.</summary>
        private bool RePoint(RevitLinkType type, string destPath, bool samePath, string label)
        {
            try
            {
                var wsConfig = new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets);
                LinkLoadResult result = samePath
                    ? type.Reload()
                    : type.LoadFrom(ModelPathUtils.ConvertUserVisiblePathToModelPath(destPath), wsConfig);

                if (result != null && result.LoadResult != LinkLoadResultType.LinkLoaded)
                {
                    // A non-fatal load result still means the link is not showing the new file —
                    // report the actual result rather than assuming success.
                    Log(AppStrings.T("replaceLink.log.reloadResult", label, result.LoadResult.ToString()), "fail");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"ReplaceLink: re-point '{label}'", ex);
                Log(AppStrings.T("replaceLink.log.reloadFail", label, ex.Message), "fail");
                return false;
            }
        }

        /// <summary>Unloads any OTHER link type in this host that points at the replacement file,
        /// so the file can be opened standalone. Returns that type (to be re-loaded afterwards),
        /// or null when the file isn't linked here. The type being replaced is excluded — it was
        /// already unloaded by the caller.</summary>
        private RevitLinkType? UnloadIfLoadedAsOtherLink(Document hostDoc, string newPath, ElementId skipTypeId, string label)
        {
            try
            {
                foreach (var t in new FilteredElementCollector(hostDoc)
                             .OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
                {
                    if (t.Id == skipTypeId) continue;
                    try
                    {
                        var er = t.GetExternalFileReference();
                        if (er == null) continue;
                        if (er.GetLinkedFileStatus() != LinkedFileStatus.Loaded) continue;
                        string p = ModelPathUtils.ConvertModelPathToUserVisiblePath(er.GetAbsolutePath()) ?? "";
                        if (!string.Equals(p, newPath, StringComparison.OrdinalIgnoreCase)) continue;

                        // Link-management call — NO transaction may be open (CLAUDE.md).
                        t.Unload(null);
                        Log(AppStrings.T("replaceLink.log.unloadedOther", label, SafeTypeName(t)), "info");
                        return t;
                    }
                    catch (Exception ex)
                    { DiagnosticsLog.Swallowed($"ReplaceLink: probe link type path for '{label}'", ex); }
                }
            }
            catch (Exception ex)
            { DiagnosticsLog.Error($"ReplaceLink: scan for the replacement file as a link ('{label}')", ex); }
            return null;
        }

        /// <summary>Re-loads the unrelated link unloaded by <see cref="UnloadIfLoadedAsOtherLink"/>.
        /// A failure here is reported to the user — leaving someone else's link silently unloaded
        /// is exactly the kind of side effect this tool must not have.</summary>
        private void ReloadOtherLink(Document hostDoc, RevitLinkType? other, string label)
        {
            if (other == null) return;
            string name = SafeTypeName(other);
            try
            {
                other.Reload();
                Log(AppStrings.T("replaceLink.log.reloadedOther", label, name), "info");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"ReplaceLink: reload the other link '{name}' after '{label}'", ex);
                Log(AppStrings.T("replaceLink.log.reloadOtherFail", label, name), "fail");
            }
        }

        private static string SafeTypeName(RevitLinkType type)
        {
            try { return type.Name ?? "?"; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ReplaceLink: read link type name", ex); return "?"; }
        }

        /// <summary>Last-resort restore after a failed swap: the link was unloaded but nothing
        /// replaced it, so put it back at its original file. Leaving a silently unloaded link
        /// behind would be worse than the original failure.</summary>
        private void RestoreOriginal(RevitLinkType type, string oldPath, string label)
        {
            try
            {
                // LoadFrom the ORIGINAL path rather than Reload(): if the failure happened after
                // a partially-successful re-point, the type may already be aimed at the new file,
                // and Reload() would then reload that instead of putting the old one back.
                type.LoadFrom(ModelPathUtils.ConvertUserVisiblePathToModelPath(oldPath),
                              new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
                Log(AppStrings.T("replaceLink.log.restored", label), "warn");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"ReplaceLink: restore '{label}' after failure", ex);
                Log(AppStrings.T("replaceLink.log.restoreFail", label, Path.GetFileName(oldPath)), "fail");
            }
        }

        // ── helpers ──────────────────────────────────────────────────────────────
        private static IEnumerable<RevitLinkInstance> InstancesOf(Document hostDoc, ElementId typeId)
        {
            return new FilteredElementCollector(hostDoc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .Where(i => i.GetTypeId() == typeId);
        }

        private static void MoveInstance(Document hostDoc, RevitLinkInstance instance, XYZ delta)
        {
            // A pinned instance silently refuses the move — unpin, move, restore the pin state.
            bool pinned = instance.Pinned;
            if (pinned) instance.Pinned = false;
            ElementTransformUtils.MoveElement(hostDoc, instance.Id, delta);
            if (pinned) instance.Pinned = true;
        }

        /// <summary>Formats an internal-units length through the document's own unit settings, so
        /// the log reads in whatever the project uses. Falls back to decimal feet.</summary>
        private static string FormatLength(Document doc, double internalLength)
        {
            try
            {
                return UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, internalLength, false);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ReplaceLink: format length", ex);
                return internalLength.ToString("0.###") + "'";
            }
        }

        private static string SanitizeBaseName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            return new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        }

        private void Progress(int done, int total, int pass, int fail, int skip)
        {
            int pct = total > 0 ? (int)(done * 100.0 / total) : 100;
            Log(AppStrings.T("replaceLink.log.progress", pct, done, total, pass), "info");
            OnProgress?.Invoke(pct, pass, fail, skip);
        }

        private string DestLabel(string? destFolder)
        {
            switch (Spec.Destination)
            {
                case ReplaceDestination.RenameBesideIt: return AppStrings.T("replaceLink.summaries.destRename");
                case ReplaceDestination.SelectedFolder: return AppStrings.T("replaceLink.summaries.destFolder", destFolder ?? "");
                default:                                return AppStrings.T("replaceLink.summaries.destOverwrite");
            }
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
            catch (Exception ex) { DiagnosticsLog.Swallowed("ReplaceLink: configure failure handling", ex); }
        }
    }
}
