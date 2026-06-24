using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.CopyLinear;

namespace LemoineTools.Tools.CopyFromLink
{
    /// <summary>
    /// Main run for Copy Elements from Link. Pulls the chosen link's selected categories +
    /// "Family: Type" picks into the host with the link transform applied, copying each element
    /// verbatim via the cross-document <see cref="ElementTransformUtils.CopyElements(Document,
    /// ICollection{ElementId}, Document, Transform, CopyPasteOptions)"/> overload (the
    /// duplicate-types prompt is suppressed with <see cref="UseDestinationTypes"/>). In
    /// delete-previous mode every created host element is stamped (<see cref="CopyFromLinkStampSchema"/>)
    /// so a re-run reconciles: rebuild only changed/new sources, leave unchanged ones, delete outputs
    /// whose source is gone. Otherwise outputs are left unstamped — plain elements a later run never
    /// touches. All work happens in one transaction with a single regen at the end (CLAUDE.md).
    /// </summary>
    public sealed class CopyFromLinkRunHandler : IExternalEventHandler
    {
        // ── Inputs (set before Raise) ─────────────────────────────────────────
        public CopyFromLinkSpec Spec { get; set; } = new CopyFromLinkSpec();

        // Off = previous runs' outputs are never touched and this run's outputs are left unstamped.
        // On = stamped reconciliation.
        public bool DeletePrevious { get; set; }
        public bool OnlyChanged    { get; set; }
        public bool DeleteOrphans  { get; set; } = true;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.CopyFromLink.CopyFromLinkRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        public void Execute(UIApplication app)
        {
            int pass = 0, fail = 0, skip = 0;
            long issues0 = LemoineLog.IssueCount;
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) { Log("No active document.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                if (Spec.LinkInstId == 0L) { Log("Pick a source link — this tool copies out of links only.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                var src = CopyFromLinkSource.Resolve(doc, Spec.LinkInstId);
                if (src?.Doc == null) { Log("Source link is not loaded.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                var selectedKeys = new HashSet<string>(Spec.SelectedTypeKeys ?? new List<string>(), StringComparer.Ordinal);
                if (selectedKeys.Count == 0) { Log("No family types selected — nothing to copy.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                // ── Gather current source elements matching the selected types, keyed by identity ──
                var current = new Dictionary<string, SourceElem>(StringComparer.Ordinal);
                int unmatched = 0;
                foreach (var el in CopyLinearSource.Collect(src.Doc, CopyFromLinkSource.ToLinearSpec(Spec)))
                {
                    try
                    {
                        string typeKey = CopyFromLinkSource.TypeKey(src.Doc!, el);
                        if (!selectedKeys.Contains(typeKey)) { unmatched++; continue; }
                        string key  = CopyFromLinkSource.SourceKey(src.LinkInstUid, el.UniqueId);
                        // The geometry hash (bounding-box read per element) only drives change
                        // detection + re-run reconciliation, so skip it entirely unless stamping.
                        string hash = DeletePrevious ? CopyFromLinkSource.GeoHash(src.Doc!, el, src.Transform, typeKey) : "";
                        current[key] = new SourceElem { Id = el.Id, Key = key, Hash = hash, TypeKey = typeKey };
                    }
                    catch (Exception ex)
                    {
                        skip++;
                        LemoineLog.Swallowed($"CopyFromLink: read source {el?.Id}", ex);
                    }
                }

                if (current.Count == 0)
                {
                    Log($"No matching elements found in the link for the selected types ({unmatched} element(s) in the categories did not match).", "warn");
                    OnComplete?.Invoke(0, 0, skip);
                    return;
                }
                Log($"Found {current.Count} element(s) to copy ({unmatched} in the categories skipped — type not selected).", "info");

                // ── Read existing stamps and decide what to (re)build / delete ──
                var stampsByKey = new Dictionary<string, (List<ElementId> ids, string hash)>(StringComparer.Ordinal);
                foreach (var rec in CopyFromLinkStampSchema.ReadAll(doc))
                {
                    if (!stampsByKey.TryGetValue(rec.SourceKey, out var entry))
                        entry = (new List<ElementId>(), rec.GeoHash);
                    entry.ids.Add(rec.ElementId);
                    stampsByKey[rec.SourceKey] = entry;
                }

                var toBuild   = new List<SourceElem>();
                int unchanged = 0;
                foreach (var s in current.Values)
                {
                    bool stamped = stampsByKey.TryGetValue(s.Key, out var prior);
                    bool changed = !stamped || prior.hash != s.Hash;
                    if (OnlyChanged && !changed) { unchanged++; continue; }
                    toBuild.Add(s);
                }

                // Orphans: stamped outputs whose source no longer matches the current selection.
                var orphanIds = new List<ElementId>();
                if (DeletePrevious && DeleteOrphans)
                    foreach (var kv in stampsByKey)
                        if (!current.ContainsKey(kv.Key))
                            orphanIds.AddRange(kv.Value.ids);

                string runId = Guid.NewGuid().ToString("N");
                int total = toBuild.Count, done = 0;
                Log($"Copying {toBuild.Count}, {unchanged} unchanged, "
                    + (DeletePrevious
                        ? $"{orphanIds.Count} orphaned output(s) to remove."
                        : "previous outputs left untouched."), "info");

                using (var tx = new Transaction(doc, "Copy Elements from Link"))
                {
                    tx.Start();
                    var failureHandler = ConfigureFailures(tx);
                    var opts = CopyOptions();

                    if (DeletePrevious)
                    {
                        SafeDelete(doc, orphanIds);
                        foreach (var s in toBuild)
                            if (stampsByKey.TryGetValue(s.Key, out var prior))
                                SafeDelete(doc, prior.ids);
                    }

                    // ── Batch copy (chunked) ───────────────────────────────────────────
                    // One CopyElements call per element is dominated by per-call overhead; copying
                    // many elements in a single call is dramatically faster (and keeps connected MEP
                    // networks wired, since they copy together). Chunked so the run stays cancellable
                    // and reports progress; a chunk that throws falls back to per-element copy so one
                    // bad element (e.g. an unsupported host) can't sink the whole batch.
                    const int ChunkSize = 500;
                    var attributable = new List<ElementId>();   // batch-copied outputs to stamp by hash
                    for (int i = 0; i < toBuild.Count; i += ChunkSize)
                    {
                        if (LemoineRun.CancelRequested)
                        {
                            Log($"Stopped by user — {done} of {total} processed; work so far preserved.", "warn");
                            break;   // falls through to doc.Regenerate() + tx.Commit() below
                        }
                        var slice    = toBuild.GetRange(i, Math.Min(ChunkSize, toBuild.Count - i));
                        var sliceIds = slice.Select(s => s.Id).ToList();
                        try
                        {
                            var copied = ElementTransformUtils.CopyElements(src.Doc!, sliceIds, doc, src.Transform, opts);
                            if (copied != null && copied.Count > 0)
                            {
                                pass += copied.Count;
                                if (DeletePrevious) attributable.AddRange(copied);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Isolate the offender: re-copy this chunk one element at a time, stamping
                            // directly (the source is known here, so no hash attribution is needed).
                            Log($"Batch of {slice.Count} failed ({ex.GetType().Name}) — retrying individually to isolate.", "warn");
                            LemoineLog.Swallowed("CopyFromLink: batch copy chunk", ex);
                            foreach (var s in slice)
                            {
                                try
                                {
                                    var one = ElementTransformUtils.CopyElements(src.Doc!, new List<ElementId> { s.Id }, doc, src.Transform, opts);
                                    if (one == null || one.Count == 0) { skip++; continue; }
                                    pass += one.Count;
                                    if (DeletePrevious)
                                        foreach (var id in one)
                                        {
                                            var made = doc.GetElement(id);
                                            if (made != null) CopyFromLinkStampSchema.Stamp(made, s.Key, s.Hash, runId);
                                        }
                                }
                                catch (Exception ex2)
                                {
                                    fail++;
                                    LemoineLog.Error($"CopyFromLink: copy {s.Key}", ex2);
                                    Log($"✗ {s.Id} ({s.TypeKey}): copy failed — {ex2.GetType().Name}: {ex2.Message}", "fail");
                                }
                            }
                        }
                        done += slice.Count;
                        if (total > 0) OnProgress?.Invoke((int)(done * 90.0 / total), pass, fail, skip);
                    }

                    doc.Regenerate();   // single regen for the whole run; also makes copied geometry readable for stamping

                    // ── Stamp batch-copied outputs by world-position hash ────────────────
                    // A cross-document copy doesn't report which output came from which source, so
                    // attribute each output to a source by matching the world-space identity hash: the
                    // copy applied the link transform, so an output's host-world hash equals its
                    // source's link-world hash. (Per-element fallback copies above already stamped.)
                    if (DeletePrevious && attributable.Count > 0)
                    {
                        var byHash = new Dictionary<string, Queue<SourceElem>>(StringComparer.Ordinal);
                        foreach (var s in toBuild)
                        {
                            if (!byHash.TryGetValue(s.Hash, out var q)) byHash[s.Hash] = q = new Queue<SourceElem>();
                            q.Enqueue(s);
                        }
                        int unattributed = 0;
                        foreach (var id in attributable)
                        {
                            var made = doc.GetElement(id);
                            if (made == null) continue;
                            string tk = CopyFromLinkSource.TypeKey(doc, made);
                            string h  = CopyFromLinkSource.GeoHash(doc, made, Transform.Identity, tk);
                            if (byHash.TryGetValue(h, out var q) && q.Count > 0)
                            {
                                var s = q.Dequeue();
                                CopyFromLinkStampSchema.Stamp(made, s.Key, s.Hash, runId);
                            }
                            else unattributed++;
                        }
                        if (unattributed > 0)
                            Log($"{unattributed} copied element(s) could not be linked back to a source for change-tracking — they won't reconcile on a re-run.", "warn");
                    }

                    tx.Commit();

                    foreach (var f in failureHandler.Captured.Distinct().Take(15))
                    {
                        Log($"Revit failure: {f}", "warn");
                        LemoineLog.Warn("CopyFromLink: revit failure", f);
                    }
                }

                long issues = LemoineLog.IssuesSince(issues0);
                if (issues > 0) Log($"{issues} non-fatal issue(s) recorded — see diagnostics log.", "warn");
                Log($"Done. {pass} element(s) copied, {skip} skipped, {fail} failed.", fail > 0 ? "warn" : "pass");
                OnProgress?.Invoke(100, pass, fail, skip);
                OnComplete?.Invoke(pass, fail, skip);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("CopyFromLinkRunHandler.Execute", ex);
                Log($"Run aborted: {ex.Message}", "fail");
                OnComplete?.Invoke(pass, fail + 1, skip);
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                Spec = new CopyFromLinkSpec();
            }
        }

        // ── helpers ────────────────────────────────────────────────────────────

        private static void SafeDelete(Document doc, List<ElementId> ids)
        {
            if (ids == null) return;
            foreach (var id in ids)
            {
                if (id == null || id == ElementId.InvalidElementId) continue;
                try { if (doc.GetElement(id) != null) doc.Delete(id); }
                catch (Exception ex) { LemoineLog.Swallowed("CopyFromLink: delete output", ex); }
            }
        }

        // Copy options that silently reuse the destination's types — suppresses the modal
        // "Duplicate Types" prompt that otherwise pops for every cross-document copy (CLAUDE.md).
        private static CopyPasteOptions CopyOptions()
        {
            var opts = new CopyPasteOptions();
            opts.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
            return opts;
        }

        private sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
                => DuplicateTypeAction.UseDestinationTypes;
        }

        private static SilentFailureHandler ConfigureFailures(Transaction tx)
        {
            var handler = new SilentFailureHandler();
            try
            {
                var opts = tx.GetFailureHandlingOptions();
                opts.SetClearAfterRollback(true);
                opts.SetForcedModalHandling(false);
                opts.SetFailuresPreprocessor(handler);
                tx.SetFailureHandlingOptions(opts);
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyFromLink: configure failure handling", ex); }
            return handler;
        }

        // Captures every Revit failure raised during the transaction so the run log can report
        // exactly which failure fired. Warnings are deleted (suppressed); errors are recorded.
        private sealed class SilentFailureHandler : IFailuresPreprocessor
        {
            private readonly List<string> _captured = new List<string>();
            public IReadOnlyList<string> Captured => _captured;

            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                foreach (var msg in failuresAccessor.GetFailureMessages().ToList())
                {
                    var sev = msg.GetSeverity();
                    string desc;
                    try { desc = msg.GetDescriptionText(); }
                    catch (Exception ex) { LemoineLog.Swallowed("CopyFromLink: read failure description", ex); desc = "(no description)"; }
                    string ids = "";
                    try { ids = string.Join(", ", msg.GetFailingElementIds().Select(i => i.Value)); }
                    catch (Exception ex) { LemoineLog.Swallowed("CopyFromLink: read failing ids", ex); }
                    _captured.Add($"[{sev}] {desc}" + (ids.Length > 0 ? $" — elements {ids}" : ""));

                    if (sev == FailureSeverity.Warning)
                        failuresAccessor.DeleteWarning(msg);
                }
                return FailureProcessingResult.Continue;
            }
        }

        private sealed class SourceElem
        {
            public ElementId Id = ElementId.InvalidElementId;
            public string    Key     = "";
            public string    Hash    = "";
            public string    TypeKey = "";
        }
    }
}
