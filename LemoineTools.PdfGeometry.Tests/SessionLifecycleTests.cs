using System.Collections.Generic;
using System.Linq;
using LemoineTools.PdfGeometry.Engine;
using LemoineTools.PdfGeometry.Graph;
using LemoineTools.PdfGeometry.Plans;
using LemoineTools.PdfGeometry.Primitives;
using Xunit;

namespace LemoineTools.PdfGeometry.Tests
{
    public class SessionLifecycleTests
    {
        private static RegionWandEngine RoomEngine(out int h)
        {
            int w = 220; h = 220;
            var g = Scenes.White(w, h);
            Scenes.RectOutline(g, w, 20, 20, 180, 120);
            return Scenes.RasterEngine(g, w, h);
        }

        [Fact]
        public void CreateUndoRecreate_StatusRoundTrip()
        {
            var engine = RoomEngine(out int h);
            var result = engine.Wand(Scenes.Pdf(100, 70, h));
            var face = result.Face!;
            Assert.Equal(FaceStatus.Traced, face.Status);

            engine.Session.MarkElementCreated(face.FaceId, 777);
            Assert.Equal(FaceStatus.ElementCreated, face.Status);
            Assert.Equal(777, face.ElementIdValue);

            // Undo / external delete: element id disappears from the model.
            var released = engine.Session.ReleaseByElement(777);
            Assert.Same(face, released);
            Assert.Equal(FaceStatus.Available, face.Status);
            Assert.Equal(0, face.ElementIdValue);

            // Unknown element ids are not ours.
            Assert.Null(engine.Session.ReleaseByElement(99999));

            // Re-creating uses the same registered boundary.
            var plan = engine.Session.BuildPlan(face.FaceId);
            Assert.True(plan.OuterLoop.Count >= 4);
            engine.Session.MarkElementCreated(face.FaceId, 888);
            Assert.Equal(FaceStatus.ElementCreated, face.Status);
        }

        [Fact]
        public void RemoveFace_UnclaimsEdgesAndKeepsGeometryForRetrace()
        {
            var engine = RoomEngine(out int h);
            var result = engine.Wand(Scenes.Pdf(100, 70, h));
            int faceId = result.Face!.FaceId;
            int edgeCount = engine.Session.EdgeCount;

            Assert.True(engine.Session.RemoveFace(faceId));
            Assert.Empty(engine.Session.Faces);
            Assert.Equal(edgeCount, engine.Session.EdgeCount); // geometry kept
            Assert.All(engine.Session.EdgesSnapshot(),
                e => Assert.True(e.LeftFaceId == SharedEdge.Unclaimed && e.RightFaceId == SharedEdge.Unclaimed));

            // Re-wand reconciles onto the kept edges instead of duplicating them.
            var again = engine.Wand(Scenes.Pdf(100, 70, h));
            Assert.Equal(WandStatus.Ok, again.Status);
            Assert.Equal(edgeCount, engine.Session.EdgeCount);
        }

        [Fact]
        public void BuildPlan_OuterIsCcwHolesAreCwAndAreaIsNet()
        {
            int w = 220, h = 220;
            var g = Scenes.White(w, h);
            Scenes.RectOutline(g, w, 20, 20, 180, 120);
            Scenes.RectOutline(g, w, 90, 60, 110, 80);

            var engine = Scenes.RasterEngine(g, w, h);
            var result = engine.Wand(Scenes.Pdf(40, 40, h));
            var plan = result.Plan!;

            Assert.True(PolylineOps.SignedArea(plan.OuterLoop) > 0, "outer loop must be CCW");
            Assert.Single(plan.Holes);
            Assert.True(PolylineOps.SignedArea(plan.Holes[0]) < 0, "hole must be CW");
            Assert.Equal(
                PolylineOps.Area(plan.OuterLoop) - PolylineOps.Area(plan.Holes[0]),
                plan.AreaSqPts, 6);
        }

        [Fact]
        public void BuildPlan_CurvesChainContiguouslyAroundEachLoop()
        {
            var engine = RoomEngine(out int h);
            var result = engine.Wand(Scenes.Pdf(100, 70, h));
            var curves = result.Plan!.OuterCurves;

            Assert.True(curves.Count >= 3);
            for (int i = 0; i < curves.Count; i++)
            {
                var next = curves[(i + 1) % curves.Count];
                Assert.True(curves[i].End.DistanceTo(next.Start) < 1e-9,
                    $"curve {i} end does not meet curve {i + 1} start");
            }
        }

        [Fact]
        public void AdjacentFaces_PlanCurvesAgreeExactlyAlongSharedBoundary()
        {
            int w = 320, h = 220;
            var g = Scenes.White(w, h);
            Scenes.RectOutline(g, w, 20, 20, 300, 120);
            Scenes.VLine(g, w, 160, 20, 120);

            var engine = Scenes.RasterEngine(g, w, h);
            var a = engine.Wand(Scenes.Pdf(90, 70, h));
            var b = engine.Wand(Scenes.Pdf(230, 70, h));
            Assert.Equal(WandStatus.Ok, a.Status);
            Assert.Equal(WandStatus.Ok, b.Status);

            var sharedIds = a.Face!.OuterLoop.Select(r => r.EdgeId)
                .Intersect(b.Face!.OuterLoop.Select(r => r.EdgeId))
                .ToHashSet();
            Assert.NotEmpty(sharedIds);

            // Curve pieces over shared edges must be the same geometry in both
            // plans (one side traverses them reversed).
            var planA = engine.Session.BuildPlan(a.Face.FaceId);
            var planB = engine.Session.BuildPlan(b.Face.FaceId);

            List<(Pt2, Pt2, Pt2)> Normalized(IEnumerable<PlanCurve> curves) =>
                curves.Select(c =>
                {
                    var lo = Less(c.Start, c.End) ? c.Start : c.End;
                    var hi = Less(c.Start, c.End) ? c.End : c.Start;
                    return (lo, hi, c.Mid);
                }).ToList();

            // Collect each plan's curve pieces whose endpoints both lie on a shared edge.
            var sharedEdgePolys = sharedIds
                .Select(id => engine.Session.EdgesSnapshot().First(e => e.EdgeId == id).Polyline)
                .ToList();
            bool OnShared(Pt2 p) => sharedEdgePolys.Any(poly => PolylineOps.ClosestParam(poly, p, out var d) >= 0 && d < 1e-6);

            var piecesA = Normalized(planA.OuterCurves.Where(c => OnShared(c.Start) && OnShared(c.End)));
            var piecesB = Normalized(planB.OuterCurves.Where(c => OnShared(c.Start) && OnShared(c.End)));

            Assert.NotEmpty(piecesA);
            Assert.Equal(piecesA.Count, piecesB.Count);
            foreach (var pa in piecesA)
                Assert.Contains(piecesB, pb =>
                    pb.Item1.DistanceTo(pa.Item1) < 1e-9 &&
                    pb.Item2.DistanceTo(pa.Item2) < 1e-9 &&
                    pb.Item3.DistanceTo(pa.Item3) < 1e-9);
        }

        private static bool Less(Pt2 a, Pt2 b) => a.X < b.X || (a.X == b.X && a.Y < b.Y);
    }
}
