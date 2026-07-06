using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Coordinates
{
    /// <summary>
    /// Aligns the host project to a resolved anchor (Internal Origin by default, or a picked grid
    /// intersection), then aligns every selected link to that same anchor — the "move-then-publish"
    /// workflow:
    ///   1. Move the Survey Point and/or Project Base Point to the resolved host anchor.
    ///   2. For each selected link: resolve that link's own point + direction (its own Internal
    ///      Origin by default, or its own named grid intersection when overridden), rotate
    ///      (vertical axis) so it runs parallel to the host's anchor direction, then translate so
    ///      it coincides with the host anchor (Z only when the vertical target is meaningful for
    ///      that link — see <see cref="AlignOneLink"/>).
    ///   3. PublishCoordinates() each aligned link so the alignment is recorded as shared coordinates.
    ///
    /// PublishCoordinates requires an OPEN transaction — calling it with none throws "Modifying X is
    /// forbidden because the document has no open transaction" (confirmed on a Windows/Revit run,
    /// contradicting this handler's original assumption). It runs in its own transaction, after the
    /// instance moves have already committed in theirs. Shared coordinates are written back to each
    /// link file when the host is saved.
    /// </summary>
    public sealed class AlignCoordinatesRunHandler : IExternalEventHandler
    {
        // ── Run payload (set by the ViewModel before Raise) ──────────────────────
        public AnchorSource HostAnchorSource { get; set; } = AnchorSource.InternalOrigin;
        public string       HostGrid1Name    { get; set; } = "";
        public string       HostGrid2Name    { get; set; } = "";
        public ZSource       HostZSource     { get; set; } = ZSource.InternalOriginZ;
        public string        HostLevelName   { get; set; } = "";

        public bool MoveSurvey { get; set; } = true;
        public bool MovePbp    { get; set; } = true;
        public bool Rotate     { get; set; } = true;
        public bool Publish    { get; set; } = true;

        public List<LinkAlignSpec> LinkSpecs { get; set; } = new List<LinkAlignSpec>();

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Coordinates.AlignCoordinatesRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        private const double Eps = 1e-9;

        public void Execute(UIApplication app)
        {
            int pass = 0, fail = 0, skip = 0;
            long issues0 = LemoineLog.IssueCount;
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) { Log("No active document.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                // ── Resolve the host anchor: point (XY) + direction, and Z ───────
                XYZ hostXY; double hostBearing;
                if (HostAnchorSource == AnchorSource.GridIntersection)
                {
                    var g1 = FindGrid(doc, HostGrid1Name);
                    var g2 = FindGrid(doc, HostGrid2Name);
                    if (g1 == null || g2 == null)
                    {
                        Log($"Host grid '{(g1 == null ? HostGrid1Name : HostGrid2Name)}' not found.", "fail");
                        OnComplete?.Invoke(0, 1, 0); return;
                    }
                    if (!CoordinatesGeometry.TryGridLine(g1, out var hp1, out var hd1) ||
                        !CoordinatesGeometry.TryGridLine(g2, out var hp2, out var hd2))
                    {
                        Log("A host grid has no usable straight line in plan.", "fail");
                        OnComplete?.Invoke(0, 1, 0); return;
                    }
                    var xy = CoordinatesGeometry.Intersect(hp1, hd1, hp2, hd2);
                    if (xy == null)
                    {
                        Log($"Host grids '{HostGrid1Name}' and '{HostGrid2Name}' are parallel — they do not intersect.", "fail");
                        OnComplete?.Invoke(0, 1, 0); return;
                    }
                    hostXY = xy;
                    hostBearing = CoordinatesGeometry.Bearing(hd1);
                    Log($"Host anchor: grid '{HostGrid1Name}' × '{HostGrid2Name}'.", "info");
                }
                else
                {
                    hostXY = XYZ.Zero;
                    hostBearing = 0.0;
                    Log("Host anchor: Internal Origin.", "info");
                }

                double hostZ;
                if (HostZSource == ZSource.MatchedLevel)
                {
                    var level = FindLevel(doc, HostLevelName);
                    if (level == null) { Log($"Level '{HostLevelName}' not found.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }
                    hostZ = level.ProjectElevation;
                    Log($"Host elevation: level '{level.Name}'.", "info");
                }
                else
                {
                    hostZ = 0.0;
                    Log("Host elevation: Internal Origin (Z = 0).", "info");
                }

                var pHost = new XYZ(hostXY.X, hostXY.Y, hostZ);

                // ── Transaction 1: move the host point(s) ────────────────────────
                using (var tx = new Transaction(doc, "Align Host Coordinate Points"))
                {
                    tx.Start();
                    ConfigureFailures(tx);
                    if (MoveSurvey) MoveBasePoint(doc, BasePoint.GetSurveyPoint(doc),      pHost, "Survey Point");
                    if (MovePbp)    MoveBasePoint(doc, BasePoint.GetProjectBasePoint(doc), pHost, "Project Base Point");
                    tx.Commit();
                }

                // ── Transaction 2: rotate + translate each selected link ─────────
                var aligned = new List<long>();
                var toRun = (LinkSpecs ?? new List<LinkAlignSpec>()).Where(s => s.Selected).ToList();
                using (var tx = new Transaction(doc, "Align Linked Models"))
                {
                    tx.Start();
                    ConfigureFailures(tx);

                    int total = toRun.Count;
                    int idx = 0;
                    foreach (var spec in toRun)
                    {
                        idx++;
                        if (LemoineRun.CancelRequested)
                        {
                            Log($"Stopped by user — {pass} link(s) aligned so far; work preserved.", "warn");
                            break;   // fall through to commit
                        }

                        try
                        {
                            var res = AlignOneLink(doc, spec, pHost, hostBearing);
                            if (res == AlignResult.Aligned) { pass++; aligned.Add(spec.LinkInstId); }
                            else if (res == AlignResult.Skipped) skip++;
                            else fail++;
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            LemoineLog.Error("AlignCoordinates: align link", ex);
                            Log($"✗ Link {spec.LinkInstId}: {ex.Message}", "fail");
                        }

                        OnProgress?.Invoke(total > 0 ? (int)(100.0 * idx / total) : 100, pass, fail, skip);
                    }

                    tx.Commit();
                }

                // ── Publish shared coordinates ──
                // Contrary to this handler's original assumption, PublishCoordinates requires an
                // OPEN transaction — calling it with none throws "Modifying X is forbidden because
                // the document has no open transaction" (confirmed on a Windows/Revit run). Give it
                // its own transaction, committed after every link has been attempted.
                if (Publish && aligned.Count > 0)
                {
                    int published = 0;
                    using (var tx = new Transaction(doc, "Publish Shared Coordinates"))
                    {
                        tx.Start();
                        ConfigureFailures(tx);
                        foreach (long linkId in aligned)
                        {
                            try
                            {
                                doc.PublishCoordinates(new LinkElementId(new ElementId(linkId)));
                                published++;
                            }
                            catch (Exception ex)
                            {
                                LemoineLog.Swallowed($"AlignCoordinates: publish coordinates to link {linkId}", ex);
                                Log($"⚠ Could not publish shared coordinates to link {linkId}: {ex.Message}", "warn");
                            }
                        }
                        tx.Commit();
                    }
                    if (published > 0)
                        Log($"Published host shared coordinates to {published} link(s). Save the host to write them into the link files.", "info");
                }
                else if (!Publish)
                {
                    Log("Shared coordinates not published (toggle off) — links were repositioned only.", "info");
                }

                long issues = LemoineLog.IssuesSince(issues0);
                if (issues > 0) Log($"{issues} non-fatal issue(s) recorded — see diagnostics log.", "warn");
                Log($"Done. {pass} link(s) aligned, {skip} skipped, {fail} failed.", fail > 0 ? "warn" : "pass");
                OnProgress?.Invoke(100, pass, fail, skip);
                OnComplete?.Invoke(pass, fail, skip);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("AlignCoordinatesRunHandler.Execute", ex);
                Log($"Run aborted: {ex.Message}", "fail");
                OnComplete?.Invoke(pass, fail + 1, skip);
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                LinkSpecs = new List<LinkAlignSpec>();
            }
        }

        private enum AlignResult { Aligned, Skipped, Failed }

        /// <summary>
        /// Rotate + translate a single link instance so its resolved reference point matches the
        /// host anchor. The reference point/direction come from <paramref name="spec"/>: that
        /// link's own Internal Origin by default, or its own named grid intersection when
        /// overridden. Z only moves when the vertical target is meaningful for this link — Internal
        /// Origin Z (0) always applies (it's an intrinsic document property), while a Matched Level
        /// target only applies to a Grid-Intersection-overridden link that has a same-named level.
        /// </summary>
        private AlignResult AlignOneLink(Document doc, LinkAlignSpec spec, XYZ pHost, double hostBearing)
        {
            var li = doc.GetElement(new ElementId(spec.LinkInstId)) as RevitLinkInstance;
            var ld = li?.GetLinkDocument();
            if (li == null || ld == null) { Log($"⚠ Link {spec.LinkInstId} is not loaded — skipped.", "warn"); return AlignResult.Skipped; }

            string linkName = SafeLinkName(ld);

            XYZ refInternal; XYZ dirInternal; string methodNote; bool applyZ;

            if (spec.Overridden && spec.AnchorSource == AnchorSource.GridIntersection)
            {
                var lg1 = FindGrid(ld, spec.Grid1Name);
                var lg2 = FindGrid(ld, spec.Grid2Name);
                if (lg1 == null || lg2 == null)
                {
                    Log($"— {linkName}: grid '{(lg1 == null ? spec.Grid1Name : spec.Grid2Name)}' not found — skipped.", "info");
                    return AlignResult.Skipped;
                }
                if (!CoordinatesGeometry.TryGridLine(lg1, out var lp1, out var ld1) ||
                    !CoordinatesGeometry.TryGridLine(lg2, out var lp2, out var ld2))
                {
                    Log($"— {linkName}: a matched grid has no usable plan line — skipped.", "info");
                    return AlignResult.Skipped;
                }
                var linkXY = CoordinatesGeometry.Intersect(lp1, ld1, lp2, ld2);
                if (linkXY == null)
                {
                    Log($"— {linkName}: grids '{spec.Grid1Name}' and '{spec.Grid2Name}' are parallel — skipped.", "info");
                    return AlignResult.Skipped;
                }

                refInternal = new XYZ(linkXY.X, linkXY.Y, 0);
                dirInternal = ld1;
                methodNote  = $"grid '{spec.Grid1Name}' × '{spec.Grid2Name}'";

                // A named-level Z target only makes sense once we've already tied this link's XY
                // to one of its own named grids — keep the legacy "skip Z, still move XY" fallback
                // when no same-named level exists in this link.
                applyZ = HostZSource == ZSource.InternalOriginZ || FindLevel(ld, HostLevelName) != null;
            }
            else
            {
                refInternal = XYZ.Zero;
                dirInternal = XYZ.BasisX;
                methodNote  = "Internal Origin";
                applyZ      = true;   // Internal Origin Z is intrinsic to every document — always applies
            }

            bool wasPinned = false;
            try { wasPinned = li.Pinned; if (wasPinned) li.Pinned = false; }
            catch (Exception ex) { LemoineLog.Swallowed($"AlignCoordinates: unpin {linkName}", ex); }

            // World pivot (current position of the reference point).
            var t0 = li.GetTotalTransform();
            var pivot = t0.OfPoint(refInternal);

            // ── Rotation about the vertical axis through the pivot ──
            if (Rotate)
            {
                var linkDirWorld = t0.OfVector(dirInternal);
                double linkBearing = CoordinatesGeometry.Bearing(new XYZ(linkDirWorld.X, linkDirWorld.Y, 0));
                double theta = CoordinatesGeometry.UndirectedDelta(linkBearing, hostBearing);
                if (Math.Abs(theta) > 1e-6)
                {
                    var axis = Line.CreateBound(pivot, pivot + XYZ.BasisZ);
                    ElementTransformUtils.RotateElement(doc, li.Id, axis, theta);
                }
            }

            // ── Translation: move the (rotated) reference point onto the host anchor ──
            var t1 = li.GetTotalTransform();
            var refWorld = t1.OfPoint(refInternal);
            var target = applyZ ? pHost : new XYZ(pHost.X, pHost.Y, refWorld.Z); // no Z target → keep current Z
            var delta = target - refWorld;
            if (delta.GetLength() > Eps)
                ElementTransformUtils.MoveElement(doc, li.Id, delta);

            try { if (wasPinned) li.Pinned = true; }
            catch (Exception ex) { LemoineLog.Swallowed($"AlignCoordinates: re-pin {linkName}", ex); }

            string vnote = applyZ ? "" : ", plan only (no matching level — Z unchanged)";
            Log($"✓ {linkName}: aligned to {methodNote}{vnote}.", "pass");
            return AlignResult.Aligned;
        }

        /// <summary>
        /// Move a base point so it sits at <paramref name="targetInternal"/> (internal coordinates).
        /// NOTE: clipped vs. unclipped state changes whether the shared-coordinate origin travels with
        /// the marker; verify on a Windows/Revit plot. Pinned points are unpinned for the move.
        /// </summary>
        private void MoveBasePoint(Document doc, BasePoint? bp, XYZ targetInternal, string label)
        {
            if (bp == null) { Log($"⚠ {label} not found in this document.", "warn"); return; }
            try
            {
                bool wasPinned = bp.Pinned;
                if (wasPinned) bp.Pinned = false;

                var delta = targetInternal - bp.Position;
                if (delta.GetLength() > Eps)
                    ElementTransformUtils.MoveElement(doc, bp.Id, delta);

                if (wasPinned) bp.Pinned = true;
                Log($"✓ {label} moved to the host anchor.", "pass");
            }
            catch (Exception ex)
            {
                LemoineLog.Error($"AlignCoordinates: move {label}", ex);
                Log($"✗ Could not move {label}: {ex.Message}", "fail");
            }
        }

        // ── Lookups ──────────────────────────────────────────────────────────────
        private static Grid? FindGrid(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                .FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static Level? FindLevel(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static string SafeLinkName(Document ld)
        {
            try { return "[" + System.IO.Path.GetFileNameWithoutExtension(ld.Title) + "]"; }
            catch { return "[link]"; }
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
            catch (Exception ex) { LemoineLog.Swallowed("AlignCoordinates: configure failure handling", ex); }
        }
    }
}
