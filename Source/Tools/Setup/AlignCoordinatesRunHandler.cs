using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// Aligns the host project to a resolved anchor (Internal Origin by default, or a picked grid
    /// intersection), then aligns every selected link to that same anchor:
    ///   1. Move the Survey Point and/or Project Base Point to the resolved host anchor.
    ///   2. For each selected link: resolve that link's own point + direction (its own Internal
    ///      Origin by default, or its own named grid intersection when overridden), rotate
    ///      (vertical axis) so it runs parallel to the host's anchor direction, then translate so
    ///      it coincides with the host anchor. With the Matched Level Z method, the link's own
    ///      user-picked level (<see cref="LinkAlignSpec.LevelName"/>) is what gets moved to the
    ///      host target elevation; a link without that level keeps its current Z (reported).
    ///
    /// Each link's rotate+translate runs inside its own <see cref="SubTransaction"/> so a failure
    /// mid-link rolls that link back to untouched instead of committing a half-transformed state.
    ///
    /// This only repositions the host's copy of each link instance. Use the separate "Push
    /// Coordinates to Links" tool to commit the correction into the linked files themselves.
    /// </summary>
    public sealed class AlignCoordinatesRunHandler : IExternalEventHandler
    {
        // ── Run payload (set by the ViewModel before Raise) ──────────────────────
        public AnchorSource HostAnchorSource { get; set; } = AnchorSource.InternalOrigin;
        public string       HostGrid1Name    { get; set; } = "";
        public string       HostGrid2Name    { get; set; } = "";
        public ZSource      HostZSource      { get; set; } = ZSource.InternalOriginZ;
        public string       HostLevelName    { get; set; } = "";

        public bool MoveSurvey { get; set; } = true;
        public bool MovePbp    { get; set; } = true;
        public bool Rotate     { get; set; } = true;

        public List<LinkAlignSpec> LinkSpecs { get; set; } = new List<LinkAlignSpec>();

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Setup.AlignCoordinatesRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);
        private static string T(string key, params object[] args) => AppStrings.T("setup.alignCoordinates." + key, args);

        private const double Eps = 1e-9;

        public void Execute(UIApplication app)
        {
            int pass = 0, fail = 0, skip = 0;
            long issues0 = DiagnosticsLog.IssueCount;
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) { Log(T("log.noDoc"), "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                // ── Resolve the host anchor: point (XY) + direction, and Z ───────
                XYZ hostXY; double hostBearing;
                if (HostAnchorSource == AnchorSource.GridIntersection)
                {
                    var g1 = FindGrid(doc, HostGrid1Name);
                    var g2 = FindGrid(doc, HostGrid2Name);
                    if (g1 == null || g2 == null)
                    {
                        Log(T("log.hostGridMissing", g1 == null ? HostGrid1Name : HostGrid2Name), "fail");
                        OnComplete?.Invoke(0, 1, 0); return;
                    }
                    if (!CoordinatesGeometry.TryGridLine(g1, out var hp1, out var hd1))
                    {
                        Log(T("log.hostGridNotLine", HostGrid1Name), "fail");
                        OnComplete?.Invoke(0, 1, 0); return;
                    }
                    if (!CoordinatesGeometry.TryGridLine(g2, out var hp2, out var hd2))
                    {
                        Log(T("log.hostGridNotLine", HostGrid2Name), "fail");
                        OnComplete?.Invoke(0, 1, 0); return;
                    }
                    var xy = CoordinatesGeometry.Intersect(hp1, hd1, hp2, hd2);
                    if (xy == null)
                    {
                        Log(T("log.hostParallel", HostGrid1Name, HostGrid2Name), "fail");
                        OnComplete?.Invoke(0, 1, 0); return;
                    }
                    hostXY = xy;
                    hostBearing = CoordinatesGeometry.Bearing(hd1);
                    Log(T("log.hostAnchorGrids", HostGrid1Name, HostGrid2Name), "info");
                }
                else
                {
                    hostXY = XYZ.Zero;
                    hostBearing = 0.0;
                    Log(T("log.hostAnchorOrigin"), "info");
                }

                double hostZ;
                if (HostZSource == ZSource.MatchedLevel)
                {
                    var level = FindLevel(doc, HostLevelName);
                    if (level == null) { Log(T("log.levelMissing", HostLevelName), "fail"); OnComplete?.Invoke(0, 1, 0); return; }
                    hostZ = level.ProjectElevation;
                    Log(T("log.hostElevLevel", level.Name), "info");
                }
                else
                {
                    hostZ = 0.0;
                    Log(T("log.hostElevOrigin"), "info");
                }

                var pHost = new XYZ(hostXY.X, hostXY.Y, hostZ);

                // ── Transaction 1: move the host point(s) ────────────────────────
                using (var tx = new Transaction(doc, "Align Host Coordinate Points"))
                {
                    tx.Start();
                    ConfigureFailures(tx);
                    if (MoveSurvey) MoveBasePoint(doc, BasePoint.GetSurveyPoint(doc),      pHost, T("labels.surveyPoint"));
                    if (MovePbp)    MoveBasePoint(doc, BasePoint.GetProjectBasePoint(doc), pHost, T("labels.projectBasePoint"));
                    tx.Commit();
                }

                // ── Transaction 2: rotate + translate each selected link ─────────
                var toRun = (LinkSpecs ?? new List<LinkAlignSpec>()).Where(s => s.Selected).ToList();
                using (var tx = new Transaction(doc, "Align Linked Models"))
                {
                    tx.Start();
                    ConfigureFailures(tx);

                    int total = toRun.Count;
                    int idx = 0;
                    foreach (var spec in toRun)
                    {
                        if (RunState.CancelRequested)
                        {
                            Log(AppStrings.T("common.log.stoppedByUser", idx, total), "warn");
                            break;   // fall through to commit — aligned links are preserved
                        }
                        idx++;

                        try
                        {
                            var res = AlignOneLink(doc, spec, pHost, hostBearing);
                            if (res == AlignResult.Aligned) pass++;
                            else if (res == AlignResult.Skipped) skip++;
                            else fail++;
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            DiagnosticsLog.Error("AlignCoordinates: align link", ex);
                            Log(T("log.linkFail", spec.LinkName, ex.Message), "fail");
                        }

                        OnProgress?.Invoke(total > 0 ? (int)(100.0 * idx / total) : 100, pass, fail, skip);
                    }

                    tx.Commit();
                }

                long issues = DiagnosticsLog.IssuesSince(issues0);
                if (issues > 0) Log(T("log.nonFatal", issues), "warn");
                if (toRun.Count == 0) Log(T("log.doneHostOnly"), "pass");
                else Log(T("log.done", pass, skip, fail), fail > 0 ? "warn" : "pass");
                OnProgress?.Invoke(100, pass, fail, skip);
                OnComplete?.Invoke(pass, fail, skip);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("AlignCoordinatesRunHandler.Execute", ex);
                Log(T("log.aborted", ex.Message), "fail");
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
        /// overridden. Z: with Internal Origin Z the reference point lands at Z = 0; with Matched
        /// Level, the link's user-picked level (<see cref="LinkAlignSpec.LevelName"/>) is moved to
        /// the host target elevation — a link without that level keeps its current Z (reported).
        /// The rotate + translate run in a SubTransaction so a mid-link failure rolls the link
        /// back to untouched; the pin state is restored in a finally either way.
        /// </summary>
        private AlignResult AlignOneLink(Document doc, LinkAlignSpec spec, XYZ pHost, double hostBearing)
        {
            var li = doc.GetElement(new ElementId(spec.LinkInstId)) as RevitLinkInstance;
            var ld = li?.GetLinkDocument();
            if (li == null || ld == null) { Log(T("log.linkNotLoaded", spec.LinkName), "warn"); return AlignResult.Skipped; }

            string linkName = SafeLinkName(ld);

            XYZ refInternal; XYZ dirInternal; string methodNote;

            if (spec.Overridden && spec.AnchorSource == AnchorSource.GridIntersection)
            {
                var lg1 = FindGrid(ld, spec.Grid1Name);
                var lg2 = FindGrid(ld, spec.Grid2Name);
                if (lg1 == null || lg2 == null)
                {
                    Log(T("log.linkGridMissing", linkName, lg1 == null ? spec.Grid1Name : spec.Grid2Name), "warn");
                    return AlignResult.Skipped;
                }
                if (!CoordinatesGeometry.TryGridLine(lg1, out var lp1, out var ld1) ||
                    !CoordinatesGeometry.TryGridLine(lg2, out var lp2, out var ld2))
                {
                    Log(T("log.linkGridNotLine", linkName), "warn");
                    return AlignResult.Skipped;
                }
                var linkXY = CoordinatesGeometry.Intersect(lp1, ld1, lp2, ld2);
                if (linkXY == null)
                {
                    Log(T("log.linkParallel", linkName, spec.Grid1Name, spec.Grid2Name), "warn");
                    return AlignResult.Skipped;
                }

                refInternal = new XYZ(linkXY.X, linkXY.Y, 0);
                dirInternal = ld1;
                methodNote  = T("log.methodGrids", spec.Grid1Name, spec.Grid2Name);
            }
            else
            {
                refInternal = XYZ.Zero;
                dirInternal = XYZ.BasisX;
                methodNote  = T("log.methodOrigin");
            }

            // The link's Z anchor for Matched Level mode: the user-picked level in the link.
            Level? linkLevel = null;
            bool zSkipped = false;
            if (HostZSource == ZSource.MatchedLevel)
            {
                linkLevel = FindLevel(ld, spec.LevelName);
                zSkipped  = linkLevel == null;
            }

            bool wasPinned = false;
            try { wasPinned = li.Pinned; if (wasPinned) li.Pinned = false; }
            catch (Exception ex) { DiagnosticsLog.Swallowed($"AlignCoordinates: unpin {linkName}", ex); }

            try
            {
                using (var st = new SubTransaction(doc))
                {
                    st.Start();

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

                    double dz;
                    if (HostZSource == ZSource.MatchedLevel)
                    {
                        if (linkLevel != null)
                        {
                            // Move the picked level's elevation (rotation about Z preserves it)
                            // onto the host target elevation — this is what moves the whole model.
                            double worldLevelZ = t1.OfPoint(new XYZ(0, 0, linkLevel.ProjectElevation)).Z;
                            dz = pHost.Z - worldLevelZ;
                        }
                        else
                        {
                            dz = 0;   // no such level in this link — leave Z alone, reported below
                        }
                    }
                    else
                    {
                        dz = pHost.Z - refWorld.Z;
                    }

                    var delta = new XYZ(pHost.X - refWorld.X, pHost.Y - refWorld.Y, dz);
                    if (delta.GetLength() > Eps)
                        ElementTransformUtils.MoveElement(doc, li.Id, delta);

                    st.Commit();
                }
            }
            catch (Exception ex)
            {
                // SubTransaction not committed → disposed = rolled back; the link is untouched.
                DiagnosticsLog.Error($"AlignCoordinates: align {linkName}", ex);
                Log(T("log.linkFail", linkName, ex.Message), "fail");
                return AlignResult.Failed;
            }
            finally
            {
                try { if (wasPinned) li.Pinned = true; }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"AlignCoordinates: re-pin {linkName}", ex); }
            }

            if (zSkipped) Log(T("log.alignedPlanOnly", linkName, methodNote, string.IsNullOrEmpty(spec.LevelName) ? "—" : spec.LevelName), "pass");
            else Log(T("log.aligned", linkName, methodNote), "pass");
            return AlignResult.Aligned;
        }

        /// <summary>
        /// Move a base point so it sits at <paramref name="targetInternal"/> (internal coordinates).
        /// NOTE: clipped vs. unclipped state changes whether the shared-coordinate origin travels with
        /// the marker; verify on a Windows/Revit plot. Pinned points are unpinned for the move and
        /// re-pinned in a finally so a failed move can't leave them unpinned.
        /// </summary>
        private void MoveBasePoint(Document doc, BasePoint? bp, XYZ targetInternal, string label)
        {
            if (bp == null) { Log(T("log.pointMissing", label), "warn"); return; }

            bool wasPinned = false;
            try
            {
                wasPinned = bp.Pinned;
                if (wasPinned) bp.Pinned = false;

                var delta = targetInternal - bp.Position;
                if (delta.GetLength() > Eps)
                    ElementTransformUtils.MoveElement(doc, bp.Id, delta);

                Log(T("log.pointMoved", label), "pass");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"AlignCoordinates: move {label}", ex);
                Log(T("log.pointFail", label, ex.Message), "fail");
            }
            finally
            {
                try { if (wasPinned) bp.Pinned = true; }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"AlignCoordinates: re-pin {label}", ex); }
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
            catch (Exception ex) { DiagnosticsLog.Swallowed("AlignCoordinates: read link title", ex); return "[link]"; }
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
            catch (Exception ex) { DiagnosticsLog.Swallowed("AlignCoordinates: configure failure handling", ex); }
        }
    }
}
