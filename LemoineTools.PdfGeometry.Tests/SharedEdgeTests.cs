using System.Collections.Generic;
using System.Linq;
using LemoineTools.PdfGeometry.Engine;
using LemoineTools.PdfGeometry.Graph;
using LemoineTools.PdfGeometry.Primitives;
using Xunit;

namespace LemoineTools.PdfGeometry.Tests
{
    public class SharedEdgeTests
    {
        /// <summary>
        /// Two rooms separated by one wall: the second wand must reference the
        /// first face's boundary edges along the wall — not re-trace its own.
        /// </summary>
        [Fact]
        public void AdjacentRooms_ShareTheCommonBoundaryEdge()
        {
            int w = 320, h = 220;
            var g = Scenes.White(w, h);
            // Two rooms side by side: outer 20..300 × 20..120, middle wall at x=160.
            Scenes.RectOutline(g, w, 20, 20, 300, 120);
            Scenes.VLine(g, w, 160, 20, 120);

            var engine = Scenes.RasterEngine(g, w, h);

            var a = engine.Wand(Scenes.Pdf(90, 70, h));
            Assert.Equal(WandStatus.Ok, a.Status);

            var b = engine.Wand(Scenes.Pdf(230, 70, h));
            Assert.Equal(WandStatus.Ok, b.Status);

            var shared = SharedEdgesBetween(engine.Session, a.Face!, b.Face!);
            Assert.NotEmpty(shared);

            // The shared edges span (roughly) the full height of the middle wall.
            double sharedLen = shared.Sum(e => e.Length);
            Assert.InRange(sharedLen, 80, 110); // wall open height ≈ 100 pts

            // And both faces reference those edges in their loops — identical
            // geometry by reference, not by coincidence.
            foreach (var e in shared)
            {
                Assert.Contains(a.Face!.OuterLoop, r => r.EdgeId == e.EdgeId);
                Assert.Contains(b.Face!.OuterLoop, r => r.EdgeId == e.EdgeId);
            }
        }

        /// <summary>
        /// Partial overlap: room B shares only the lower half of room A's right
        /// wall. A's original edge must be split and the common child reused.
        /// </summary>
        [Fact]
        public void PartialOverlap_SplitsExistingEdgeAndSharesThePortion()
        {
            int w = 320, h = 320;
            var g = Scenes.White(w, h);
            // Room A: tall rect 20..160 × 20..220.
            Scenes.RectOutline(g, w, 20, 20, 160, 220);
            // Room B: shorter rect to the right sharing A's wall from y=120..220.
            Scenes.HLine(g, w, 160, 300, 120);
            Scenes.VLine(g, w, 300, 120, 220);
            Scenes.HLine(g, w, 160, 300, 220);

            var engine = Scenes.RasterEngine(g, w, h);

            var a = engine.Wand(Scenes.Pdf(90, 120, h));
            Assert.Equal(WandStatus.Ok, a.Status);
            int edgesAfterA = engine.Session.EdgeCount;

            var b = engine.Wand(Scenes.Pdf(230, 170, h));
            Assert.Equal(WandStatus.Ok, b.Status);

            // A's wall edge was split — the session has more edges than A's
            // original two plus B's additions would suggest, and at least one
            // edge is claimed by both faces.
            Assert.True(engine.Session.EdgeCount > edgesAfterA);

            var shared = SharedEdgesBetween(engine.Session, a.Face!, b.Face!);
            Assert.NotEmpty(shared);
            double sharedLen = shared.Sum(e => e.Length);
            Assert.InRange(sharedLen, 75, 115); // shared wall portion ≈ 100 pts

            // A's loop still chains contiguously after the split rewrite.
            var ringA = engine.Session.ResolveLoop(a.Face!.OuterLoop);
            Assert.True(ringA.Count >= 4);
            Assert.True(PolylineOps.IsSimpleRing(ringA, 1e-9));
        }

        [Fact]
        public void RewandingSameRegion_ReportsDuplicate()
        {
            int w = 220, h = 220;
            var g = Scenes.White(w, h);
            Scenes.RectOutline(g, w, 20, 20, 180, 120);

            var engine = Scenes.RasterEngine(g, w, h);
            var first = engine.Wand(Scenes.Pdf(100, 70, h));
            Assert.Equal(WandStatus.Ok, first.Status);

            var second = engine.Wand(Scenes.Pdf(60, 50, h));
            Assert.Equal(WandStatus.Duplicate, second.Status);
            Assert.Equal(first.Face!.FaceId, second.Face!.FaceId);
            Assert.Single(engine.Session.Faces);
        }

        private static List<SharedEdge> SharedEdgesBetween(RegionSession session, RegionFace a, RegionFace b)
        {
            var idsA = a.OuterLoop.Select(r => r.EdgeId).ToHashSet();
            var idsB = b.OuterLoop.Select(r => r.EdgeId).ToHashSet();
            idsA.IntersectWith(idsB);
            return idsA.Select(id => session.EdgesSnapshot().First(e => e.EdgeId == id)).ToList();
        }
    }
}
