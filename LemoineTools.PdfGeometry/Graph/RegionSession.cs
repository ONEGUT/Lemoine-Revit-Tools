using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.PdfGeometry.Arcs;
using LemoineTools.PdfGeometry.Plans;
using LemoineTools.PdfGeometry.Primitives;

namespace LemoineTools.PdfGeometry.Graph
{
    public enum RegisterFaceStatus { Ok, Duplicate, TooSmall, SelfIntersecting, Failed }

    public sealed class RegisterFaceResult
    {
        public RegisterFaceStatus Status { get; }
        public RegionFace? Face { get; }
        public string? Warning { get; }

        public RegisterFaceResult(RegisterFaceStatus status, RegionFace? face, string? warning)
        {
            Status = status; Face = face; Warning = warning;
        }
    }

    public sealed class SplitLineResult
    {
        public SharedEdge Edge { get; }
        public IReadOnlyList<int> InvalidatedFaceIds { get; }

        public SplitLineResult(SharedEdge edge, List<int> invalidated)
        {
            Edge = edge; InvalidatedFaceIds = invalidated;
        }
    }

    /// <summary>
    /// The session's central data structure: a DCEL-style planar region graph.
    /// Barrier sources (binarized PDF linework + user split lines), registered
    /// region boundaries composed of shared edges, and face statuses all live
    /// here. Everything is PDF point space and Revit-free.
    ///
    /// Exactness model: every edge endpoint and split point is canonicalized
    /// through a node registry, so edges that meet at a junction share the
    /// exact same point and resolved loops chain with zero weld error.
    /// </summary>
    public sealed class RegionSession
    {
        private readonly Dictionary<int, SharedEdge> _edges = new Dictionary<int, SharedEdge>();
        private readonly List<RegionFace> _faces = new List<RegionFace>();
        private readonly List<Pt2> _nodes = new List<Pt2>();
        private readonly Dictionary<int, List<PlanCurve>> _arcCache = new Dictionary<int, List<PlanCurve>>();
        private int _nextEdgeId = 1;
        private int _nextFaceId = 1;

        public GeomTol Tol { get; }

        /// <summary>
        /// Non-fatal session findings (side-claim conflicts, reconcile fallbacks,
        /// post-commit edge splits). The Revit side routes these to LemoineLog.
        /// </summary>
        public event Action<string>? Diagnostic;

        public RegionSession(GeomTol tol) { Tol = tol; }

        public IReadOnlyList<RegionFace> Faces => _faces;
        public int EdgeCount => _edges.Count;
        public IEnumerable<SharedEdge> SplitLineEdges => _edges.Values.Where(e => e.IsSplitLine);

        public RegionFace? GetFace(int faceId) => _faces.FirstOrDefault(f => f.FaceId == faceId);

        internal SharedEdge GetEdge(int edgeId) => _edges[edgeId];
        internal List<SharedEdge> EdgesSnapshot() => _edges.Values.ToList();

        // ---------------------------------------------------------------- nodes

        /// <summary>
        /// Returns the canonical junction point for <paramref name="p"/>: an
        /// existing node within ReconcileTol, or <paramref name="p"/> itself,
        /// newly registered. Guarantees edges meeting at a junction carry the
        /// identical endpoint.
        /// </summary>
        internal Pt2 CanonNode(Pt2 p)
        {
            double tol = Tol.ReconcileTol;
            foreach (var n in _nodes)
                if (n.DistanceTo(p) <= tol) return n;
            _nodes.Add(p);
            return p;
        }

        // ---------------------------------------------------------------- edges

        internal SharedEdge AddEdge(List<Pt2> polyline, bool isSplitLine)
        {
            var pts = PolylineOps.Dedupe(polyline, 1e-9, isRing: false);
            if (pts.Count < 2)
                throw new ArgumentException("Edge polyline needs at least 2 distinct points.", nameof(polyline));
            pts[0] = CanonNode(pts[0]);
            pts[pts.Count - 1] = CanonNode(pts[pts.Count - 1]);
            pts = PolylineOps.Dedupe(pts, 1e-9, isRing: false);
            if (pts.Count < 2) pts = new List<Pt2> { pts[0], pts[0] + new Pt2(1e-6, 0) };

            var edge = new SharedEdge(_nextEdgeId++, pts, isSplitLine);
            _edges[edge.EdgeId] = edge;
            return edge;
        }

        /// <summary>Removes reconcile-created edges that ended up unclaimed (failed-loop rollback).</summary>
        internal void DropUnclaimedEdges(IEnumerable<int> edgeIds)
        {
            foreach (var id in edgeIds)
                if (_edges.TryGetValue(id, out var e) &&
                    e.LeftFaceId == SharedEdge.Unclaimed && e.RightFaceId == SharedEdge.Unclaimed &&
                    !e.IsSplitLine)
                {
                    _edges.Remove(id);
                    _arcCache.Remove(id);
                }
        }

        /// <summary>
        /// Splits an edge at the given interior arc-length params. Children keep
        /// the parent's side claims and split-line flag; faces referencing the
        /// parent are rewritten to reference the children in order. Returns the
        /// children with their parent-param ranges, ascending.
        /// </summary>
        internal List<(SharedEdge edge, double t0, double t1)> SplitEdge(int edgeId, List<double> interiorParams)
        {
            var parent = _edges[edgeId];
            var poly = parent.Polyline;

            var cuts = new List<double> { 0 };
            cuts.AddRange(interiorParams.Where(t => t > 1e-9 && t < parent.Length - 1e-9).OrderBy(t => t));
            cuts.Add(parent.Length);

            // Canonical cut points first, so adjacent children share them exactly.
            var cutPts = new Pt2[cuts.Count];
            cutPts[0] = parent.Start;
            cutPts[cuts.Count - 1] = parent.End;
            for (int i = 1; i < cuts.Count - 1; i++)
                cutPts[i] = CanonNode(PolylineOps.PointAt(poly, cuts[i]));

            var children = new List<(SharedEdge, double, double)>();
            for (int i = 0; i < cuts.Count - 1; i++)
            {
                var sub = PolylineOps.SubPolyline(poly, cuts[i], cuts[i + 1]);
                sub[0] = cutPts[i];
                sub[sub.Count - 1] = cutPts[i + 1];
                sub = PolylineOps.Dedupe(sub, 1e-9, isRing: false);
                if (sub.Count < 2) sub = new List<Pt2> { cutPts[i], cutPts[i + 1] };

                var child = new SharedEdge(_nextEdgeId++, sub, parent.IsSplitLine)
                {
                    LeftFaceId = parent.LeftFaceId,
                    RightFaceId = parent.RightFaceId,
                };
                _edges[child.EdgeId] = child;
                children.Add((child, cuts[i], cuts[i + 1]));
            }

            _edges.Remove(edgeId);
            _arcCache.Remove(edgeId);

            var childEdges = children.Select(c => c.Item1).ToList();
            foreach (var face in _faces)
            {
                ReplaceEdgeRefs(face.OuterLoop, edgeId, childEdges);
                foreach (var hole in face.Holes)
                    ReplaceEdgeRefs(hole, edgeId, childEdges);

                if (face.Status == FaceStatus.ElementCreated &&
                    (parent.LeftFaceId == face.FaceId || parent.RightFaceId == face.FaceId))
                    Diagnostic?.Invoke(
                        $"Edge {edgeId} split after face {face.FaceId} was committed — arc reconstruction of the halves may deviate from the committed boundary within fit tolerance.");
            }
            return children;
        }

        private static void ReplaceEdgeRefs(List<EdgeRef> loop, int parentId, List<SharedEdge> children)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                if (loop[i].EdgeId != parentId) continue;
                bool reversed = loop[i].Reversed;
                loop.RemoveAt(i);
                if (!reversed)
                    for (int c = 0; c < children.Count; c++)
                        loop.Insert(i + c, new EdgeRef(children[c].EdgeId, false));
                else
                    for (int c = 0; c < children.Count; c++)
                        loop.Insert(i + c, new EdgeRef(children[children.Count - 1 - c].EdgeId, true));
                return; // a loop references a given edge at most once
            }
        }

        // ----------------------------------------------------------- split lines

        /// <summary>
        /// Registers a user split line as an exact barrier polyline + shared
        /// edge, invalidating any traced-but-uncreated face it cuts through.
        /// </summary>
        public SplitLineResult AddSplitLine(List<Pt2> exactPolyline)
        {
            var edge = AddEdge(exactPolyline, isSplitLine: true);
            var invalidated = new List<int>();
            foreach (var face in _faces)
            {
                if (face.Status == FaceStatus.ElementCreated)
                {
                    if (CutsFace(edge.Polyline, face))
                        Diagnostic?.Invoke($"Split line crosses created region {face.FaceId} — existing element left untouched.");
                    continue;
                }
                if (face.Status == FaceStatus.Invalidated) continue;
                if (CutsFace(edge.Polyline, face))
                {
                    face.Status = FaceStatus.Invalidated;
                    invalidated.Add(face.FaceId);
                    Diagnostic?.Invoke($"Face {face.FaceId} invalidated by a new split line — re-wand to pick up the new boundary.");
                }
            }
            return new SplitLineResult(edge, invalidated);
        }

        private bool CutsFace(IReadOnlyList<Pt2> polyline, RegionFace face)
        {
            var outer = ResolveLoop(face.OuterLoop);
            if (outer.Count < 3) return false;
            var holes = face.Holes.Select(ResolveLoop).Where(h => h.Count >= 3).ToList();

            for (int i = 1; i < polyline.Count; i++)
            {
                for (int k = 1; k <= 3; k++)
                {
                    var p = Pt2.Lerp(polyline[i - 1], polyline[i], k / 4.0);
                    if (!PolylineOps.ContainsPoint(outer, p)) continue;
                    bool inHole = holes.Any(h => PolylineOps.ContainsPoint(h, p));
                    if (!inHole) return true;
                }
            }
            return false;
        }

        // ----------------------------------------------------------------- faces

        /// <summary>
        /// Registers a traced region (outer ring + holes, PDF points) as a face
        /// composed of shared edges. Runs the reconciler so portions that
        /// coincide with existing edges (including split lines) reference the
        /// stored geometry verbatim.
        /// </summary>
        public RegisterFaceResult RegisterFace(List<Pt2> outerRing, List<List<Pt2>> holeRings, string extractionMode)
        {
            var outer = PolylineOps.Dedupe(outerRing, 1e-9, isRing: true);
            if (outer.Count < 3)
                return new RegisterFaceResult(RegisterFaceStatus.Failed, null, "degenerate outer loop");

            double outerArea = PolylineOps.Area(outer);
            if (outerArea < Tol.MinFaceArea)
                return new RegisterFaceResult(RegisterFaceStatus.TooSmall, null,
                    $"region area {outerArea:0.#} pt² is below the minimum {Tol.MinFaceArea:0.#} pt²");

            if (!PolylineOps.IsSimpleRing(outer, 1e-9))
                return new RegisterFaceResult(RegisterFaceStatus.SelfIntersecting, null,
                    "traced boundary self-intersects after simplification");

            int faceId = _nextFaceId;
            string? warning = null;

            var outerComp = ReconcileWithFallback(outer, ref warning);
            if (outerComp == null)
                return new RegisterFaceResult(RegisterFaceStatus.Failed, null, warning);
            NormalizeOrientation(outerComp.Refs, wantCcw: true);

            // Duplicate detection: same edges, same sides ⇒ same region.
            string signature = LoopSignature(outerComp.Refs);
            var duplicate = _faces.FirstOrDefault(f => LoopSignature(f.OuterLoop) == signature);
            if (duplicate != null)
            {
                DropUnclaimedEdges(outerComp.CreatedEdgeIds);
                return new RegisterFaceResult(RegisterFaceStatus.Duplicate, duplicate,
                    $"region already traced as face {duplicate.FaceId}");
            }

            var holeLoops = new List<List<EdgeRef>>();
            double holeArea = 0;
            foreach (var holeRing in holeRings)
            {
                var hole = PolylineOps.Dedupe(holeRing, 1e-9, isRing: true);
                if (hole.Count < 3) continue;
                if (!PolylineOps.IsSimpleRing(hole, 1e-9))
                {
                    warning = AppendWarning(warning, "a hole boundary self-intersects and was kept as-is");
                }
                var holeComp = ReconcileWithFallback(hole, ref warning);
                if (holeComp == null) continue;
                NormalizeOrientation(holeComp.Refs, wantCcw: false);
                holeLoops.Add(holeComp.Refs);
                holeArea += PolylineOps.Area(ResolveLoop(holeComp.Refs));
            }

            var face = new RegionFace(faceId, outerComp.Refs, holeLoops)
            {
                AreaSqPts = PolylineOps.Area(ResolveLoop(outerComp.Refs)) - holeArea,
                ExtractionMode = extractionMode,
            };

            ClaimLoop(face.OuterLoop, faceId);
            foreach (var hole in face.Holes) ClaimLoop(hole, faceId);

            _faces.Add(face);
            _nextFaceId++;
            if (warning != null) Diagnostic?.Invoke($"Face {faceId}: {warning}");
            return new RegisterFaceResult(RegisterFaceStatus.Ok, face, warning);
        }

        private LoopComposition? ReconcileWithFallback(List<Pt2> ring, ref string? warning)
        {
            var comp = EdgeReconciler.Reconcile(this, ring, Tol.ReconcileTol);
            if (comp.Success) return comp;

            DropUnclaimedEdges(comp.CreatedEdgeIds);
            warning = AppendWarning(warning,
                $"shared-edge reconciliation failed ({comp.Warning}) — boundary registered with private edges; adjacency to neighbors is not exact here");

            var fallback = EdgeReconciler.ComposeAsNewEdges(this, ring);
            if (fallback.Success) return fallback;

            DropUnclaimedEdges(fallback.CreatedEdgeIds);
            warning = AppendWarning(warning, fallback.Warning ?? "fallback composition failed");
            return null;
        }

        private static string AppendWarning(string? existing, string? add) =>
            existing == null ? (add ?? "") : add == null ? existing : existing + "; " + add;

        /// <summary>Outer loops run counter-clockwise, holes clockwise — face interior on the left of travel.</summary>
        private void NormalizeOrientation(List<EdgeRef> refs, bool wantCcw)
        {
            var ring = ResolveLoop(refs);
            if (ring.Count < 3) return;
            bool isCcw = PolylineOps.SignedArea(ring) > 0;
            if (isCcw == wantCcw) return;

            refs.Reverse();
            for (int i = 0; i < refs.Count; i++)
                refs[i] = new EdgeRef(refs[i].EdgeId, !refs[i].Reversed);
        }

        private void ClaimLoop(List<EdgeRef> refs, int faceId)
        {
            foreach (var r in refs)
            {
                var edge = GetEdge(r.EdgeId);
                if (!r.Reversed)
                {
                    if (edge.LeftFaceId == SharedEdge.Unclaimed) edge.LeftFaceId = faceId;
                    else if (edge.LeftFaceId != faceId)
                        Diagnostic?.Invoke($"Face {faceId} claims the left side of edge {edge.EdgeId} already owned by face {edge.LeftFaceId} — regions overlap.");
                }
                else
                {
                    if (edge.RightFaceId == SharedEdge.Unclaimed) edge.RightFaceId = faceId;
                    else if (edge.RightFaceId != faceId)
                        Diagnostic?.Invoke($"Face {faceId} claims the right side of edge {edge.EdgeId} already owned by face {edge.RightFaceId} — regions overlap.");
                }
            }
        }

        private static string LoopSignature(List<EdgeRef> refs) =>
            string.Join(",", refs.Select(r => $"{r.EdgeId}{(r.Reversed ? "R" : "F")}")
                                 .OrderBy(s => s, StringComparer.Ordinal));

        // ------------------------------------------------------------ resolution

        /// <summary>Resolves a loop of edge references into a closed ring of exact points.</summary>
        public List<Pt2> ResolveLoop(IReadOnlyList<EdgeRef> refs)
        {
            var ring = new List<Pt2>();
            foreach (var r in refs)
            {
                var poly = GetEdge(r.EdgeId).Polyline;
                int count = poly.Count;
                for (int i = 0; i < count; i++)
                {
                    var p = r.Reversed ? poly[count - 1 - i] : poly[i];
                    if (ring.Count == 0 || ring[ring.Count - 1].DistanceTo(p) > 1e-9)
                        ring.Add(p);
                }
            }
            if (ring.Count > 1 && ring[0].DistanceTo(ring[ring.Count - 1]) <= 1e-9)
                ring.RemoveAt(ring.Count - 1);
            return ring;
        }

        /// <summary>
        /// Builds the serializable creation request for a face. Arc pieces are
        /// fitted per shared edge and cached, so adjacent faces resolve the
        /// identical curve geometry along common boundaries.
        /// </summary>
        public RegionPlan BuildPlan(int faceId)
        {
            var face = GetFace(faceId) ?? throw new ArgumentException($"No face {faceId} in session.", nameof(faceId));

            var plan = new RegionPlan
            {
                FaceId = faceId,
                ExtractionMode = face.ExtractionMode,
                OuterLoop = RemoveCollinear(ResolveLoop(face.OuterLoop)),
                OuterCurves = MergeLoopCollinearLines(ResolveCurves(face.OuterLoop)),
            };
            foreach (var hole in face.Holes)
            {
                plan.Holes.Add(RemoveCollinear(ResolveLoop(hole)));
                plan.HoleCurves.Add(MergeLoopCollinearLines(ResolveCurves(hole)));
            }
            plan.AreaSqPts = PolylineOps.Area(plan.OuterLoop) - plan.Holes.Sum(PolylineOps.Area);
            return plan;
        }

        /// <summary>
        /// Drops exactly-collinear pass-through vertices from a plan ring (e.g.
        /// the artificial midpoint where a new loop was halved into two edges).
        /// Pure cleanup — the boundary's point set is unchanged, so shared-edge
        /// identity between adjacent plans is unaffected.
        /// </summary>
        private static List<Pt2> RemoveCollinear(List<Pt2> ring)
        {
            bool changed = true;
            while (changed && ring.Count > 3)
            {
                changed = false;
                for (int i = 0; i < ring.Count; i++)
                {
                    Pt2 prev = ring[(i - 1 + ring.Count) % ring.Count];
                    Pt2 cur = ring[i];
                    Pt2 next = ring[(i + 1) % ring.Count];
                    var d1 = (cur - prev).Normalized();
                    var d2 = (next - cur).Normalized();
                    if (Math.Abs(d1.Cross(d2)) < 1e-9 && d1.Dot(d2) > 0)
                    {
                        ring.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }
            return ring;
        }

        /// <summary>Merges exactly-collinear consecutive Line pieces across edge boundaries, including the loop wrap.</summary>
        private static List<PlanCurve> MergeLoopCollinearLines(List<PlanCurve> curves)
        {
            bool Collinear(PlanCurve a, PlanCurve b)
            {
                if (a.Kind != PlanCurveKind.Line || b.Kind != PlanCurveKind.Line) return false;
                if (a.End.DistanceTo(b.Start) > 1e-9) return false;
                var d1 = (a.End - a.Start).Normalized();
                var d2 = (b.End - b.Start).Normalized();
                return Math.Abs(d1.Cross(d2)) < 1e-9 && d1.Dot(d2) > 0;
            }

            for (int i = curves.Count - 2; i >= 0; i--)
                if (Collinear(curves[i], curves[i + 1]))
                {
                    curves[i] = PlanCurve.Line(curves[i].Start, curves[i + 1].End);
                    curves.RemoveAt(i + 1);
                }

            while (curves.Count > 1 && Collinear(curves[curves.Count - 1], curves[0]))
            {
                curves[curves.Count - 1] = PlanCurve.Line(curves[curves.Count - 1].Start, curves[0].End);
                curves.RemoveAt(0);
            }
            return curves;
        }

        private List<PlanCurve> ResolveCurves(List<EdgeRef> refs)
        {
            var curves = new List<PlanCurve>();
            foreach (var r in refs)
            {
                if (!_arcCache.TryGetValue(r.EdgeId, out var pieces))
                {
                    pieces = ArcFitter.Fit(GetEdge(r.EdgeId).Polyline, Tol.ArcFitTol);
                    _arcCache[r.EdgeId] = pieces;
                }
                if (!r.Reversed)
                    curves.AddRange(pieces);
                else
                    for (int i = pieces.Count - 1; i >= 0; i--)
                        curves.Add(pieces[i].Reversed());
            }
            return curves;
        }

        // -------------------------------------------------------- status updates

        public void MarkElementCreated(int faceId, long elementIdValue)
        {
            var face = GetFace(faceId) ?? throw new ArgumentException($"No face {faceId} in session.", nameof(faceId));
            face.Status = FaceStatus.ElementCreated;
            face.ElementIdValue = elementIdValue;
        }

        /// <summary>
        /// Called when a session-created element disappears from the model
        /// (undo or manual delete). Returns the released face, or null when the
        /// element was not one of ours.
        /// </summary>
        public RegionFace? ReleaseByElement(long elementIdValue)
        {
            var face = _faces.FirstOrDefault(f =>
                f.Status == FaceStatus.ElementCreated && f.ElementIdValue == elementIdValue);
            if (face == null) return null;
            face.Status = FaceStatus.Available;
            face.ElementIdValue = 0;
            return face;
        }

        /// <summary>
        /// Removes a face from the session, unclaiming its edge sides. Edge
        /// geometry is kept (unclaimed) so a re-trace of the same area
        /// reconciles back onto identical boundaries.
        /// </summary>
        public bool RemoveFace(int faceId)
        {
            var face = GetFace(faceId);
            if (face == null) return false;

            UnclaimLoop(face.OuterLoop, faceId);
            foreach (var hole in face.Holes) UnclaimLoop(hole, faceId);
            _faces.Remove(face);
            return true;
        }

        private void UnclaimLoop(List<EdgeRef> refs, int faceId)
        {
            foreach (var r in refs)
            {
                if (!_edges.TryGetValue(r.EdgeId, out var edge)) continue;
                if (edge.LeftFaceId == faceId) edge.LeftFaceId = SharedEdge.Unclaimed;
                if (edge.RightFaceId == faceId) edge.RightFaceId = SharedEdge.Unclaimed;
            }
        }
    }
}
