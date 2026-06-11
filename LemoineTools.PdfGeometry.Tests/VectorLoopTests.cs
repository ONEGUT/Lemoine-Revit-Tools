using System.Collections.Generic;
using LemoineTools.PdfGeometry.Primitives;
using LemoineTools.PdfGeometry.Vector;
using Xunit;

namespace LemoineTools.PdfGeometry.Tests
{
    public class VectorLoopTests
    {
        private static List<Seg2> Rect(double x0, double y0, double x1, double y1, double gap = 0)
        {
            // Optional gap: shrink each segment at both ends to leave drafting gaps at corners.
            Seg2 Trim(Pt2 a, Pt2 b)
            {
                var d = (b - a).Normalized();
                return new Seg2(a + d * gap, b - d * gap);
            }
            return new List<Seg2>
            {
                Trim(new Pt2(x0, y0), new Pt2(x1, y0)),
                Trim(new Pt2(x1, y0), new Pt2(x1, y1)),
                Trim(new Pt2(x1, y1), new Pt2(x0, y1)),
                Trim(new Pt2(x0, y1), new Pt2(x0, y0)),
            };
        }

        [Fact]
        public void ClosedRectangle_FindsLoop()
        {
            var graph = SegmentGraph.Build(Rect(0, 0, 100, 60), joinTol: 1.0);
            var loop = LoopFinder.FindEnclosing(graph, new Pt2(50, 30), minArea: 10);

            Assert.NotNull(loop);
            Assert.Equal(4, loop!.Outer.Count);
            Assert.Equal(6000, PolylineOps.Area(loop.Outer), 0);
            Assert.Empty(loop.Holes);
        }

        [Fact]
        public void GappedEndpoints_AreClosedByJoinTolerance()
        {
            // 1.5 pt gaps at every corner; joinTol 2 must still close the loop.
            var graph = SegmentGraph.Build(Rect(0, 0, 100, 60, gap: 0.75), joinTol: 2.0);
            var loop = LoopFinder.FindEnclosing(graph, new Pt2(50, 30), minArea: 10);

            Assert.NotNull(loop);
            Assert.InRange(PolylineOps.Area(loop!.Outer), 5700, 6300);
        }

        [Fact]
        public void GapsBiggerThanTolerance_NoLoopFound()
        {
            var graph = SegmentGraph.Build(Rect(0, 0, 100, 60, gap: 3.0), joinTol: 1.0);
            var loop = LoopFinder.FindEnclosing(graph, new Pt2(50, 30), minArea: 10);
            Assert.Null(loop);
        }

        [Fact]
        public void NestedRooms_PicksSmallestEnclosingFace()
        {
            // A room subdivided by an interior wall: seed in the left half must
            // get the left half, not the whole room.
            var segs = Rect(0, 0, 200, 100);
            segs.Add(new Seg2(new Pt2(80, 0), new Pt2(80, 100))); // divider
            var graph = SegmentGraph.Build(segs, joinTol: 1.0);

            var left = LoopFinder.FindEnclosing(graph, new Pt2(40, 50), minArea: 10);
            Assert.NotNull(left);
            Assert.Equal(80 * 100, PolylineOps.Area(left!.Outer), 0);

            var right = LoopFinder.FindEnclosing(graph, new Pt2(140, 50), minArea: 10);
            Assert.NotNull(right);
            Assert.Equal(120 * 100, PolylineOps.Area(right!.Outer), 0);
        }

        [Fact]
        public void DisconnectedColumnInsideRoom_BecomesHole()
        {
            var segs = Rect(0, 0, 200, 100);
            segs.AddRange(Rect(90, 40, 110, 60)); // island column, touching nothing
            var graph = SegmentGraph.Build(segs, joinTol: 1.0);

            var loop = LoopFinder.FindEnclosing(graph, new Pt2(30, 50), minArea: 10);

            Assert.NotNull(loop);
            Assert.Equal(200 * 100, PolylineOps.Area(loop!.Outer), 0);
            Assert.Single(loop.Holes);
            Assert.Equal(20 * 20, PolylineOps.Area(loop.Holes[0]), 0);
        }

        [Fact]
        public void SeedOutsideEverything_ReturnsNull()
        {
            var graph = SegmentGraph.Build(Rect(0, 0, 100, 60), joinTol: 1.0);
            Assert.Null(LoopFinder.FindEnclosing(graph, new Pt2(500, 500), minArea: 10));
        }

        [Fact]
        public void DanglingAntennaSegments_DoNotBreakTheLoop()
        {
            var segs = Rect(0, 0, 100, 60);
            segs.Add(new Seg2(new Pt2(50, 0), new Pt2(50, 20))); // stub wall into the room
            var graph = SegmentGraph.Build(segs, joinTol: 1.0);

            var loop = LoopFinder.FindEnclosing(graph, new Pt2(20, 40), minArea: 10);
            Assert.NotNull(loop);
            Assert.Equal(6000, PolylineOps.Area(loop!.Outer), 0);
        }
    }
}
