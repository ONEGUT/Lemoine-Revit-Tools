using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Coordinates
{
    /// <summary>
    /// Aligns the host project to a picked grid-intersection + level, then aligns every selected
    /// link to match — the "move-then-publish" workflow:
    ///   1. Move the Survey Point and/or Project Base Point to the host grid intersection.
    ///   2. For each link: find the same-named grids, rotate (vertical axis) so the link's grid runs
    ///      parallel to the host's, then translate so the intersection coincides (and, when the link
    ///      has a same-named level, match that level's elevation).
    ///   3. PublishCoordinates() each aligned link so the alignment is recorded as shared coordinates.
    ///
    /// PublishCoordinates must be called OUTSIDE a transaction, so the instance moves commit first,
    /// then publishing runs on the committed positions. Shared coordinates are written back to each
    /// link file when the host is saved.
    /// </summary>
    public sealed class AlignCoordinatesRunHandler : IExternalEventHandler
    {
        // ── Run payload (set by the ViewModel before Raise) ──────────────────────
        public string     Grid1Name   { get; set; } = "";
        public string     Grid2Name   { get; set; } = "";
        public string     LevelName   { get; set; } = "";
        public bool       MoveSurvey  { get; set; } = true;
        public bool       MovePbp     { get; set; } = true;
        public bool       Rotate      { get; set; } = true;
        public bool       Publish     { get; set; } = true;
        public List<long> LinkInstIds { get; set; } = new List<long>();

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

                // ── Resolve host reference: grid intersection + level elevation ──
                var g1 = FindGrid(doc, Grid1Name);
                var g2 = FindGrid(doc, Grid2Name);
                if (g1 == null || g2 == null)
                {
                    Log($"Host grid '{(g1 == null ? Grid1Name : Grid2Name)}' not found.", "fail");
                    OnComplete?.Invoke(0, 1, 0); return;
                }
                if (!CoordinatesGeometry.TryGridLine(g1, out var hp1, out var hd1) ||
                    !CoordinatesGeometry.TryGridLine(g2, out var hp2, out var hd2))
                {
                    Log("A host grid has no usable straight line in plan.", "fail");
                    OnComplete?.Invoke(0, 1, 0); return;
                }
                var hostXY = CoordinatesGeometry.Intersect(hp1, hd1, hp2, hd2);
                if (hostXY == null)
                {
                    Log($"Host grids '{Grid1Name}' and '{Grid2Name}' are parallel — they do not intersect.", "fail");
                    OnComplete?.Invoke(0, 1, 0); return;
                }
                var level = FindLevel(doc, LevelName);
                if (level == null) { Log($"Level '{LevelName}' not found.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                double hostZ = level.ProjectElevation;
                var pHost = new XYZ(hostXY.X, hostXY.Y, hostZ);
                double hostBearing = CoordinatesGeometry.Bearing(hd1);

                Log($"Host reference: grid '{Grid1Name}' × '{Grid2Name}' at level '{level.Name}'.", "info");

                // ── Transaction 1: move the host point(s) ────────────────────────
                using (var tx = new Transaction(doc, "Align Host Coordinate Points"))
                {
                    tx.Start();
                    ConfigureFailures(tx);
                    if (MoveSurvey) MoveBasePoint(doc, BasePoint.GetSurveyPoint(doc),      pHost, "Survey Point");
                    if (MovePbp)    MoveBasePoint(doc, BasePoint.GetProjectBasePoint(doc), pHost, "Project Base Point");
                    tx.Commit();
                }

                // ── Transaction 2: rotate + translate each link instance ─────────
                var aligned = new List<long>();
                using (var tx = new Transaction(doc, "Align Linked Models"))
                {
                    tx.Start();
                    ConfigureFailures(tx);

                    int total = LinkInstIds?.Count ?? 0;
                    int idx = 0;
                    foreach (long linkId in LinkInstIds ?? new List<long>())
                    {
                        idx++;
                        if (LemoineRun.CancelRequested)
                        {
                            Log($"Stopped by user — {pass} link(s) aligned so far; work preserved.", "warn");
                            break;   // fall through to commit
                        }

                        try
                        {
                            var res = AlignOneLink(doc, linkId, pHost, hostBearing, level.Name);
                            if (res == AlignResult.Aligned) { pass++; aligned.Add(linkId); }
                            else if (res == AlignResult.Skipped) skip++;
                            else fail++;
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            LemoineLog.Error("AlignCoordinates: align link", ex);
                            Log($"✗ Link {linkId}: {ex.Message}", "fail");
                        }

                        OnProgress?.Invoke(total > 0 ? (int)(100.0 * idx / total) : 100, pass, fail, skip);
                    }

                    tx.Commit();
                }

                // ── Publish shared coordinates (must be OUTSIDE any transaction) ──
                if (Publish && aligned.Count > 0)
                {
                    int published = 0;
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
                LinkInstIds = new List<long>();
            }
        }

        private enum AlignResult { Aligned, Skipped, Failed }

        /// <summary>Rotate + translate a single link instance so its grid intersection matches the host's.</summary>
        private AlignResult AlignOneLink(Document doc, long linkId, XYZ pHost, double hostBearing, string hostLevelName)
        {
            var li = doc.GetElement(new ElementId(linkId)) as RevitLinkInstance;
            var ld = li?.GetLinkDocument();
            if (li == null || ld == null) { Log($"⚠ Link {linkId} is not loaded — skipped.", "warn"); return AlignResult.Skipped; }

            string linkName = SafeLinkName(ld);

            var lg1 = FindGrid(ld, Grid1Name);
            var lg2 = FindGrid(ld, Grid2Name);
            if (lg1 == null || lg2 == null)
            {
                Log($"— {linkName}: grid '{(lg1 == null ? Grid1Name : Grid2Name)}' not found — skipped.", "info");
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
                Log($"— {linkName}: grids '{Grid1Name}' and '{Grid2Name}' are parallel — skipped.", "info");
                return AlignResult.Skipped;
            }

            // Reference point in the link's internal coordinates. Z = same-named level if present,
            // so a vertical shift only happens when we can match the level meaningfully.
            var matchLevel = FindLevel(ld, hostLevelName);
            bool hasLevel = matchLevel != null;
            double linkZ = matchLevel != null ? matchLevel.ProjectElevation : 0.0;
            var refInternal = new XYZ(linkXY.X, linkXY.Y, linkZ);

            bool wasPinned = false;
            try { wasPinned = li.Pinned; if (wasPinned) li.Pinned = false; }
            catch (Exception ex) { LemoineLog.Swallowed($"AlignCoordinates: unpin {linkName}", ex); }

            // World pivot (current position of the reference point).
            var t0 = li.GetTotalTransform();
            var pivot = t0.OfPoint(refInternal);

            // ── Rotation about the vertical axis through the pivot ──
            if (Rotate)
            {
                var linkDirWorld = t0.OfVector(ld1);
                double linkBearing = CoordinatesGeometry.Bearing(new XYZ(linkDirWorld.X, linkDirWorld.Y, 0));
                double theta = CoordinatesGeometry.UndirectedDelta(linkBearing, hostBearing);
                if (Math.Abs(theta) > 1e-6)
                {
                    var axis = Line.CreateBound(pivot, pivot + XYZ.BasisZ);
                    ElementTransformUtils.RotateElement(doc, li.Id, axis, theta);
                }
            }

            // ── Translation: move the (rotated) reference point onto the host point ──
            var t1 = li.GetTotalTransform();
            var refWorld = t1.OfPoint(refInternal);
            var target = hasLevel ? pHost : new XYZ(pHost.X, pHost.Y, refWorld.Z); // no level match → keep current Z
            var delta = target - refWorld;
            if (delta.GetLength() > Eps)
                ElementTransformUtils.MoveElement(doc, li.Id, delta);

            try { if (wasPinned) li.Pinned = true; }
            catch (Exception ex) { LemoineLog.Swallowed($"AlignCoordinates: re-pin {linkName}", ex); }

            string vnote = hasLevel ? $", level '{hostLevelName}'" : ", plan only (no matching level)";
            Log($"✓ {linkName}: aligned to grid '{Grid1Name}' × '{Grid2Name}'{vnote}.", "pass");
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
                Log($"✓ {label} moved to the host grid intersection.", "pass");
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
