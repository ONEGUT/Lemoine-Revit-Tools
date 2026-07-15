using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// Aligns the host project and its links to a resolved host reference.
    ///   1. Resolve the host reference point (and, for Grid Intersection / Survey Point, a
    ///      direction) from <see cref="HostAnchorSource"/>. Each source carries its own elevation:
    ///      Internal Origin = 0, Project Base Point / Survey Point = the marker's own elevation,
    ///      Grid Intersection = the picked level's elevation.
    ///   2. Move the host Survey Point and/or Project Base Point onto that reference (the point
    ///      that IS the reference is excluded upstream in the ViewModel, so it never moves).
    ///   3. For each selected link: resolve that link's own reference (its own <see cref="AnchorSource"/>),
    ///      rotate it (vertical axis) to match the host's direction when BOTH sides carry one
    ///      (Grid Intersection or Survey Point), then translate so the link's reference point
    ///      lands on the host reference — full 3D, since each side supplies its own Z.
    ///
    /// Each link's rotate + translate runs in its own <see cref="SubTransaction"/> so a mid-link
    /// failure rolls that link back to untouched instead of committing a half-transformed state.
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
        public string       HostLevelName    { get; set; } = "";

        public bool MoveSurvey { get; set; } = true;   // already excludes the reference point
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

                // ── Resolve the host reference (point + direction) ──────────────
                var hostRes = ResolveAnchor(doc, HostAnchorSource, HostGrid1Name, HostGrid2Name, HostLevelName);
                if (!hostRes.Ok)
                {
                    Log(T("log.hostRefFail", hostRes.Fail), "fail");
                    OnComplete?.Invoke(0, 1, 0); return;
                }
                var pHost = hostRes.Point;                       // host internal == host world
                bool hostHasDir = hostRes.Dir != null;
                double hostBearing = hostHasDir ? CoordinatesGeometry.Bearing(hostRes.Dir!) : 0.0;
                if (HostAnchorSource == AnchorSource.SurveyPoint && hostRes.Dir == null)
                    Log(T("log.noSurveyDir", T("log.descSurvey")), "warn");
                Log(T("log.hostRef", hostRes.Desc), "info");

                var toRun = (LinkSpecs ?? new List<LinkAlignSpec>()).Where(s => s.Selected).ToList();

                if (!MoveSurvey && !MovePbp && toRun.Count == 0)
                {
                    Log(T("log.nothingToMove"), "warn");
                    OnComplete?.Invoke(0, 0, 0); return;
                }

                // ── Transaction 1: move the host point(s) onto the reference ─────
                using (var tx = new Transaction(doc, "Align Host Coordinate Points"))
                {
                    tx.Start();
                    ConfigureFailures(tx);
                    if (MoveSurvey) MoveBasePoint(doc, BasePoint.GetSurveyPoint(doc),      pHost, T("labels.surveyPoint"));
                    if (MovePbp)    MoveBasePoint(doc, BasePoint.GetProjectBasePoint(doc), pHost, T("labels.projectBasePoint"));
                    tx.Commit();
                }

                // ── Transaction 2: rotate + translate each selected link ─────────
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
                            var res = AlignOneLink(doc, spec, pHost, hostHasDir, hostBearing);
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

        /// <summary>A resolved reference: a point in the document's internal coordinates, plus an
        /// optional direction (null = no orientation, so no rotation).</summary>
        private sealed class AnchorResolution
        {
            public XYZ    Point = XYZ.Zero;
            public XYZ?   Dir;
            public string Desc = "";
            public bool   Ok;
            public string Fail = "";
        }

        /// <summary>
        /// Resolve a document's reference point (internal coords) and optional direction from an
        /// <see cref="AnchorSource"/>. Internal Origin / Project Base Point carry no direction;
        /// Survey Point carries the project's true-north rotation; Grid Intersection carries the
        /// first grid's line bearing. Each carries its own Z.
        /// </summary>
        private AnchorResolution ResolveAnchor(Document doc, AnchorSource src, string grid1, string grid2, string levelName)
        {
            switch (src)
            {
                case AnchorSource.ProjectBasePoint:
                {
                    var bp = BasePoint.GetProjectBasePoint(doc);
                    if (bp == null) return new AnchorResolution { Ok = false, Fail = T("log.pointMissing", T("log.descPbp")) };
                    return new AnchorResolution { Ok = true, Point = bp.Position, Dir = null, Desc = T("log.descPbp") };
                }

                case AnchorSource.SurveyPoint:
                {
                    var sp = BasePoint.GetSurveyPoint(doc);
                    if (sp == null) return new AnchorResolution { Ok = false, Fail = T("log.pointMissing", T("log.descSurvey")) };
                    return new AnchorResolution { Ok = true, Point = sp.Position, Dir = SurveyDir(doc), Desc = T("log.descSurvey") };
                }

                case AnchorSource.GridIntersection:
                {
                    var g1 = FindGrid(doc, grid1);
                    var g2 = FindGrid(doc, grid2);
                    if (g1 == null) return new AnchorResolution { Ok = false, Fail = T("log.gridMissing", grid1) };
                    if (g2 == null) return new AnchorResolution { Ok = false, Fail = T("log.gridMissing", grid2) };
                    if (!CoordinatesGeometry.TryGridLine(g1, out var p1, out var d1))
                        return new AnchorResolution { Ok = false, Fail = T("log.gridNotLine", grid1) };
                    if (!CoordinatesGeometry.TryGridLine(g2, out var p2, out var d2))
                        return new AnchorResolution { Ok = false, Fail = T("log.gridNotLine", grid2) };
                    var xy = CoordinatesGeometry.Intersect(p1, d1, p2, d2);
                    if (xy == null) return new AnchorResolution { Ok = false, Fail = T("log.gridsParallel", grid1, grid2) };

                    var level = FindLevel(doc, levelName);
                    double z = level?.ProjectElevation ?? 0.0;
                    string desc = level != null
                        ? T("log.descGrids", grid1, grid2, level.Name)
                        : T("log.descGridsNoLevel", grid1, grid2);
                    return new AnchorResolution { Ok = true, Point = new XYZ(xy.X, xy.Y, z), Dir = d1, Desc = desc };
                }

                default: // InternalOrigin
                    return new AnchorResolution { Ok = true, Point = XYZ.Zero, Dir = null, Desc = T("log.descOrigin") };
            }
        }

        // The document's survey/true-north direction in its own internal coordinates, from the
        // active project location's rotation. ⚠ Convention (sign of the angle) is unverified on
        // Windows — but the SAME convention is used for host and link, so a survey↔survey rotation
        // is self-consistent regardless; a grid↔survey mix is the case that needs a plot check.
        private static XYZ? SurveyDir(Document doc)
        {
            try
            {
                var pos = doc.ActiveProjectLocation?.GetProjectPosition(XYZ.Zero);
                if (pos == null) return null;
                double a = pos.Angle;
                return new XYZ(Math.Cos(a), Math.Sin(a), 0);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("AlignCoordinates: read survey rotation", ex);
                return null;
            }
        }

        /// <summary>
        /// Rotate + translate a single link so its resolved reference point lands on the host
        /// reference. Rotation only happens when the run's Rotate flag is on AND both the host and
        /// this link carry a direction (Grid Intersection or Survey Point). Runs in a SubTransaction
        /// so a mid-link failure rolls the link back; the pin state is restored in a finally.
        /// </summary>
        private AlignResult AlignOneLink(Document doc, LinkAlignSpec spec, XYZ pHost, bool hostHasDir, double hostBearing)
        {
            var li = doc.GetElement(new ElementId(spec.LinkInstId)) as RevitLinkInstance;
            var ld = li?.GetLinkDocument();
            if (li == null || ld == null) { Log(T("log.linkNotLoaded", spec.LinkName), "warn"); return AlignResult.Skipped; }

            string linkName = SafeLinkName(ld);

            var res = ResolveAnchor(ld, spec.AnchorSource, spec.Grid1Name, spec.Grid2Name, spec.LevelName);
            if (!res.Ok) { Log(T("log.linkSkip", linkName, res.Fail), "warn"); return AlignResult.Skipped; }
            if (spec.AnchorSource == AnchorSource.SurveyPoint && res.Dir == null)
                Log(T("log.noSurveyDir", linkName), "warn");

            XYZ refInternal = res.Point;
            XYZ? dirInternal = res.Dir;

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
                    // Only when both sides carry a direction (grid or survey) and Rotate is on.
                    if (Rotate && hostHasDir && dirInternal != null)
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

                    // ── Translation: move the (rotated) reference point onto the host reference ──
                    // Full 3D — each side supplies its own Z.
                    var t1 = li.GetTotalTransform();
                    var refWorld = t1.OfPoint(refInternal);
                    var delta = pHost - refWorld;
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

            Log(T("log.aligned", linkName, res.Desc), "pass");
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
                Log(T("log.pointMoveFail", label, ex.Message), "fail");
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
