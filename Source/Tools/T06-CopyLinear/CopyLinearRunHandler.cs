using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.CopyLinear
{
    /// <summary>
    /// Main run for the Copy Linear Elements tool. Pulls the chosen source runs out of a link and
    /// either splits each into standard-length segments or replaces each with a picked family placed
    /// at intervals. Every created host element is stamped (<see cref="CopyLinearStampSchema"/>) so a
    /// re-run can reconcile against the model: rebuild only changed/new sources, leave unchanged ones,
    /// and delete outputs whose source is gone. All work happens in one transaction with a single
    /// regen at the end (never per item — CLAUDE.md).
    /// </summary>
    public sealed class CopyLinearRunHandler : IExternalEventHandler
    {
        // ── Inputs (set before Raise) ─────────────────────────────────────────
        public CopyLinearSourceSpec Spec { get; set; } = new CopyLinearSourceSpec();
        public string Mode { get; set; } = "Split";   // "Split" | "Replace"

        public double SegmentLengthFeet { get; set; } = 20.0;
        public double GapFeet           { get; set; } = 0.0;
        public bool   KeepRemainder     { get; set; } = true;

        public long   SymbolId          { get; set; }       // Replace mode
        public double IntervalFeet      { get; set; } = 10.0;
        public double ExtraSpacingFeet  { get; set; } = 0.0;
        public bool   RotateToRun       { get; set; } = true;
        public string LengthParamName   { get; set; } = "";

        public bool   OnlyChanged       { get; set; }
        public bool   DeleteOrphans     { get; set; } = true;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.CopyLinear.CopyLinearRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        public void Execute(UIApplication app)
        {
            int pass = 0, fail = 0, skip = 0;
            long issues0 = LemoineLog.IssueCount;
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) { Log("No active document.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                var src = CopyLinearSource.Resolve(doc, Spec.LinkInstId);
                if (src?.Doc == null) { Log("Source document is not loaded.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                FamilySymbol? symbol = null;
                if (Mode == "Replace")
                {
                    symbol = doc.GetElement(new ElementId(SymbolId)) as FamilySymbol;
                    if (symbol == null) { Log("Pick a family type for Replace mode.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }
                }

                // ── Gather current source runs (straight lines only) keyed by stable identity ──
                var current = new Dictionary<string, SourceRun>(StringComparer.Ordinal);
                foreach (var el in CopyLinearSource.Collect(src.Doc, Spec))
                {
                    try
                    {
                        if (!CopyLinearSource.PassesFilters(el, src.Doc, Spec)) continue;
                        var line = CopyLinearSource.StraightLine(el);
                        if (line == null) { skip++; Log($"— {el.Id}: not a straight run, skipped.", "info"); continue; }

                        XYZ a = src.Transform.OfPoint(line.Value.A);
                        XYZ b = src.Transform.OfPoint(line.Value.B);
                        string typeName; try { typeName = el.Name ?? ""; } catch { typeName = ""; }
                        string key  = CopyLinearEngine.SourceKey(src.LinkInstUid, el.UniqueId);
                        string hash = CopyLinearEngine.GeoHash(a, b,
                            CopyLinearSource.ReadSize(el), CopyLinearSource.ReadSystem(el),
                            typeName, CopyLinearSource.ReadPhase(src.Doc, el));
                        current[key] = new SourceRun { Element = el, A = a, B = b, Key = key, Hash = hash };
                    }
                    catch (Exception ex)
                    {
                        skip++;
                        LemoineLog.Swallowed($"CopyLinear: read source {el?.Id}", ex);
                    }
                }

                // ── Read existing stamps and decide what to (re)build / delete ──
                var stampsByKey = new Dictionary<string, (List<ElementId> ids, string hash)>(StringComparer.Ordinal);
                foreach (var rec in CopyLinearStampSchema.ReadAll(doc))
                {
                    if (!stampsByKey.TryGetValue(rec.SourceKey, out var entry))
                        entry = (new List<ElementId>(), rec.GeoHash);
                    entry.ids.Add(rec.ElementId);
                    stampsByKey[rec.SourceKey] = entry;
                }

                var toBuild   = new List<SourceRun>();
                int unchanged = 0;
                foreach (var run in current.Values)
                {
                    bool stamped = stampsByKey.TryGetValue(run.Key, out var prior);
                    bool changed = !stamped || prior.hash != run.Hash;
                    if (OnlyChanged && !changed) { unchanged++; continue; }
                    toBuild.Add(run);
                }

                // Orphans: stamped outputs whose source no longer exists in the current selection.
                var orphanIds = new List<ElementId>();
                if (DeleteOrphans)
                    foreach (var kv in stampsByKey)
                        if (!current.ContainsKey(kv.Key))
                            orphanIds.AddRange(kv.Value.ids);

                string runId = Guid.NewGuid().ToString("N");
                int total = toBuild.Count, done = 0;
                Log($"{Mode} mode — {current.Count} source run(s); building {toBuild.Count}, "
                    + $"{unchanged} unchanged, {orphanIds.Count} orphaned output(s) to remove.", "info");

                using (var tx = new Transaction(doc, "Copy Linear Elements"))
                {
                    tx.Start();
                    ConfigureFailures(tx);

                    // Remove orphaned outputs, then any prior outputs for the keys we are rebuilding,
                    // so a re-run is idempotent and never duplicates.
                    SafeDelete(doc, orphanIds);
                    foreach (var run in toBuild)
                        if (stampsByKey.TryGetValue(run.Key, out var prior))
                            SafeDelete(doc, prior.ids);

                    if (Mode == "Replace") { if (!symbol!.IsActive) symbol.Activate(); }

                    foreach (var run in toBuild)
                    {
                        try
                        {
                            int made = Mode == "Replace"
                                ? BuildReplace(doc, run, symbol!, runId)
                                : BuildSplit(doc, src.Doc!, run, src.Transform, runId);
                            if (made > 0) pass += made;
                            else          skip++;
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            LemoineLog.Error($"CopyLinear: build {run.Key}", ex);
                            Log($"✗ {run.Element.Id}: {ex.Message}", "fail");
                        }
                        done++;
                        if (total > 0) OnProgress?.Invoke((int)(done * 95.0 / total), pass, fail, skip);
                    }

                    doc.Regenerate();
                    tx.Commit();
                }

                long issues = LemoineLog.IssuesSince(issues0);
                if (issues > 0) Log($"{issues} non-fatal issue(s) recorded — see diagnostics log.", "warn");
                Log($"Done. {pass} output(s) created, {skip} skipped, {fail} failed.", fail > 0 ? "warn" : "pass");
                OnProgress?.Invoke(100, pass, fail, skip);
                OnComplete?.Invoke(pass, fail, skip);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("CopyLinearRunHandler.Execute", ex);
                Log($"Run aborted: {ex.Message}", "fail");
                OnComplete?.Invoke(pass, fail + 1, skip);
            }
        }

        // ── Split: copy the run into the host, then break it into standard-length cells ──
        private int BuildSplit(Document doc, Document srcDoc, SourceRun run, Transform tx, string runId)
        {
            ICollection<ElementId> copied;
            try
            {
                // Host source → same-document copy (the cross-document overload rejects identical
                // source/dest); link source → cross-document copy with the link transform applied.
                copied = ReferenceEquals(srcDoc, doc)
                    ? ElementTransformUtils.CopyElement(doc, run.Element.Id, XYZ.Zero)
                    : ElementTransformUtils.CopyElements(
                        srcDoc, new List<ElementId> { run.Element.Id }, doc, tx, CopyOptions());
            }
            catch (Exception ex)
            {
                Log($"✗ {run.Element.Id}: copy failed — {ex.Message}", "fail");
                return 0;
            }

            var hostCopy = copied?.Select(doc.GetElement).FirstOrDefault(e => e?.Location is LocationCurve lc && lc.Curve is Line);
            if (hostCopy == null)
            {
                SafeDelete(doc, copied?.ToList() ?? new List<ElementId>());
                Log($"— {run.Element.Id}: copy has no straight curve, skipped.", "info");
                return 0;
            }

            var hostLine = ((LocationCurve)hostCopy.Location).Curve as Line;
            XYZ a = hostLine!.GetEndPoint(0), b = hostLine.GetEndPoint(1);
            double totalLen = a.DistanceTo(b);

            var cells = CopyLinearEngine.SplitStations(totalLen, SegmentLengthFeet, GapFeet, KeepRemainder);
            if (cells.Count == 0)
            {
                SafeDelete(doc, new List<ElementId> { hostCopy.Id });
                Log($"— {run.Element.Id}: run shorter than one segment, skipped.", "info");
                return 0;
            }

            // Make every segment id FIRST, copying the pristine full-length element, then reassign
            // each one's curve (mirrors SplitElementsShared — never copy an already-mutated element).
            var segIds = new List<ElementId> { hostCopy.Id };
            for (int i = 1; i < cells.Count; i++)
            {
                var c = ElementTransformUtils.CopyElement(doc, hostCopy.Id, XYZ.Zero);
                if (c == null || c.Count == 0)
                {
                    Log($"✗ {run.Element.Id}: copy #{i} failed — split is incomplete.", "warn");
                    break;
                }
                segIds.Add(c.First());
            }

            int made = 0;
            for (int i = 0; i < segIds.Count && i < cells.Count; i++)
            {
                Element seg = doc.GetElement(segIds[i]);
                if (seg == null) continue;

                XYZ segA = CopyLinearEngine.PointAlong(a, b, cells[i].Start);
                XYZ segB = CopyLinearEngine.PointAlong(a, b, cells[i].End);
                if (segA.DistanceTo(segB) < 1e-4)
                {
                    if (i > 0) SafeDelete(doc, new List<ElementId> { seg.Id });
                    continue;
                }

                try
                {
                    Line newCurve = Line.CreateBound(segA, segB);  // validate before touching connectors
                    DisconnectConnectors(seg);
                    ((LocationCurve)seg.Location).Curve = newCurve;
                    if (!CopyLinearStampSchema.Stamp(seg, run.Key, run.Hash, runId, "Split"))
                        LemoineLog.Warn("CopyLinear: stamp", $"segment for {run.Element.Id} not stamped — it won't reconcile on a later run.");
                    made++;
                }
                catch (Exception ex)
                {
                    if (i > 0) SafeDelete(doc, new List<ElementId> { seg.Id });
                    LemoineLog.Error($"CopyLinear: set segment curve {run.Element.Id}#{i}", ex);
                    Log($"✗ {run.Element.Id} seg {i}: {ex.Message}", "fail");
                }
            }
            return made;
        }

        // ── Replace: place the picked family at intervals along the run ──
        private int BuildReplace(Document doc, SourceRun run, FamilySymbol symbol, string runId)
        {
            XYZ a = run.A, b = run.B;
            double totalLen = a.DistanceTo(b);
            var stations = CopyLinearEngine.PlacementStations(totalLen, IntervalFeet, ExtraSpacingFeet);
            if (stations.Count == 0) { Log($"— {run.Element.Id}: no placement points, skipped.", "info"); return 0; }

            double angle = RotateToRun ? CopyLinearEngine.PlanRotation(b - a) : 0.0;
            int made = 0;
            foreach (double s in stations)
            {
                XYZ p = CopyLinearEngine.PointAlong(a, b, s);
                FamilyInstance? inst;
                try { inst = doc.Create.NewFamilyInstance(p, symbol, StructuralType.NonStructural); }
                catch (Exception ex)
                {
                    LemoineLog.Error($"CopyLinear: place instance for {run.Element.Id}", ex);
                    Log($"✗ {run.Element.Id}: place failed — {ex.Message}", "fail");
                    continue;
                }
                if (inst == null) continue;

                if (Math.Abs(angle) > 1e-6)
                {
                    var axis = Line.CreateBound(p, p + XYZ.BasisZ);
                    try { ElementTransformUtils.RotateElement(doc, inst.Id, axis, angle); }
                    catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: rotate instance", ex); }
                }

                if (!string.IsNullOrWhiteSpace(LengthParamName))
                {
                    var lp = inst.LookupParameter(LengthParamName);
                    if (lp != null && !lp.IsReadOnly && lp.StorageType == StorageType.Double)
                        try { lp.Set(IntervalFeet); } catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: set length param", ex); }
                    else if (lp == null)
                        LemoineLog.Warn("CopyLinear: length parameter", $"'{LengthParamName}' not found on {symbol.Name}");
                }

                if (!CopyLinearStampSchema.Stamp(inst, run.Key, run.Hash, runId, "Replace"))
                    LemoineLog.Warn("CopyLinear: stamp", $"instance for {run.Element.Id} not stamped — it won't reconcile on a later run.");
                made++;
            }
            return made;
        }

        // ── helpers ────────────────────────────────────────────────────────────
        private static void SafeDelete(Document doc, List<ElementId> ids)
        {
            if (ids == null) return;
            foreach (var id in ids)
            {
                if (id == null || id == ElementId.InvalidElementId) continue;
                try { if (doc.GetElement(id) != null) doc.Delete(id); }
                catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: delete output", ex); }
            }
        }

        private static void DisconnectConnectors(Element el)
        {
            try
            {
                ConnectorManager? cm = (el as MEPCurve)?.ConnectorManager
                                       ?? (el as FamilyInstance)?.MEPModel?.ConnectorManager;
                if (cm == null) return;
                foreach (Connector c in cm.Connectors)
                    foreach (Connector other in c.AllRefs.Cast<Connector>().ToList())
                        try { c.DisconnectFrom(other); } catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: disconnect connector", ex); }
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: disconnect connectors", ex); }
        }

        // Copy options that silently reuse the destination's types — suppresses the modal
        // "Duplicate Types" prompt that otherwise pops for every cross-document copy.
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

        private static void ConfigureFailures(Transaction tx)
        {
            try
            {
                var opts = tx.GetFailureHandlingOptions();
                opts.SetClearAfterRollback(true);
                opts.SetForcedModalHandling(false);
                tx.SetFailureHandlingOptions(opts);
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: configure failure handling", ex); }
        }

        private sealed class SourceRun
        {
            public Element Element = null!;
            public XYZ     A = XYZ.Zero;
            public XYZ     B = XYZ.Zero;
            public string  Key  = "";
            public string  Hash = "";
        }
    }
}
