using System.Collections.Generic;
using System.Linq;
using LemoineTools.PdfGeometry.Engine;
using LemoineTools.PdfGeometry.Graph;
using LemoineTools.PdfGeometry.Primitives;
using Xunit;

namespace LemoineTools.PdfGeometry.Tests
{
    public class SplitLineTests
    {
        /// <summary>
        /// One room divided in two by a drawn split line: both resulting faces
        /// must reference the identical split-line SharedEdge geometry.
        /// </summary>
        [Fact]
        public void SplitRoom_BothFacesReferenceTheSameSplitLineEdge()
        {
            int w = 320, h = 220;
            var g = Scenes.White(w, h);
            Scenes.RectOutline(g, w, 20, 20, 300, 120);

            var engine = Scenes.RasterEngine(g, w, h);

            // Vertical split line wall-to-wall at x=160 (PDF points, exact).
            var split = new List<Pt2> { Scenes.Pdf(160, 20, h), Scenes.Pdf(160, 120, h) };
            var splitResult = engine.AddSplitLine(split);
            Assert.Empty(splitResult.InvalidatedFaceIds);

            var a = engine.Wand(Scenes.Pdf(90, 70, h));
            Assert.Equal(WandStatus.Ok, a.Status);

            var b = engine.Wand(Scenes.Pdf(230, 70, h));
            Assert.Equal(WandStatus.Ok, b.Status);

            // Find split-line edges referenced by each face — they must be the
            // exact same records, claimed on opposite sides.
            var splitIdsA = SplitLineEdgeIds(engine.Session, a.Face!);
            var splitIdsB = SplitLineEdgeIds(engine.Session, b.Face!);
            Assert.NotEmpty(splitIdsA);
            Assert.Equal(splitIdsA, splitIdsB);

            foreach (var id in splitIdsA)
            {
                var edge = engine.Session.EdgesSnapshot().First(e => e.EdgeId == id);
                Assert.True(edge.IsSplitLine);
                var sides = new[] { edge.LeftFaceId, edge.RightFaceId }.OrderBy(x => x).ToArray();
                var faces = new[] { a.Face!.FaceId, b.Face!.FaceId }.OrderBy(x => x).ToArray();
                Assert.Equal(faces, sides);
            }

            // The shared boundary is the split line's exact vector geometry —
            // both resolved rings contain its exact endpoints.
            var edge0 = engine.Session.EdgesSnapshot().First(e => e.EdgeId == splitIdsA[0]);
            var ringA = engine.Session.ResolveLoop(a.Face!.OuterLoop);
            var ringB = engine.Session.ResolveLoop(b.Face!.OuterLoop);
            foreach (var p in new[] { edge0.Start, edge0.End })
            {
                Assert.Contains(ringA, q => q.DistanceTo(p) < 1e-9);
                Assert.Contains(ringB, q => q.DistanceTo(p) < 1e-9);
            }
        }

        [Fact]
        public void SplitLine_InvalidatesTracedButUncreatedFace()
        {
            int w = 320, h = 220;
            var g = Scenes.White(w, h);
            Scenes.RectOutline(g, w, 20, 20, 300, 120);

            var engine = Scenes.RasterEngine(g, w, h);
            var traced = engine.Wand(Scenes.Pdf(160, 70, h));
            Assert.Equal(WandStatus.Ok, traced.Status);
            Assert.Equal(FaceStatus.Traced, traced.Face!.Status);

            var splitResult = engine.AddSplitLine(new List<Pt2> { Scenes.Pdf(160, 20, h), Scenes.Pdf(160, 120, h) });

            Assert.Contains(traced.Face.FaceId, splitResult.InvalidatedFaceIds);
            Assert.Equal(FaceStatus.Invalidated, traced.Face.Status);
        }

        [Fact]
        public void SplitLine_DoesNotInvalidateCreatedFace()
        {
            int w = 320, h = 220;
            var g = Scenes.White(w, h);
            Scenes.RectOutline(g, w, 20, 20, 300, 120);

            var engine = Scenes.RasterEngine(g, w, h);
            var traced = engine.Wand(Scenes.Pdf(160, 70, h));
            engine.Session.MarkElementCreated(traced.Face!.FaceId, 4242);

            var splitResult = engine.AddSplitLine(new List<Pt2> { Scenes.Pdf(160, 20, h), Scenes.Pdf(160, 120, h) });

            Assert.Empty(splitResult.InvalidatedFaceIds);
            Assert.Equal(FaceStatus.ElementCreated, traced.Face.Status);
        }

        [Fact]
        public void SplitLine_OutsideFace_DoesNotInvalidate()
        {
            int w = 320, h = 220;
            var g = Scenes.White(w, h);
            Scenes.RectOutline(g, w, 20, 20, 160, 120);

            var engine = Scenes.RasterEngine(g, w, h);
            var traced = engine.Wand(Scenes.Pdf(90, 70, h));
            Assert.Equal(WandStatus.Ok, traced.Status);

            var splitResult = engine.AddSplitLine(new List<Pt2> { Scenes.Pdf(220, 20, h), Scenes.Pdf(220, 120, h) });

            Assert.Empty(splitResult.InvalidatedFaceIds);
            Assert.Equal(FaceStatus.Traced, traced.Face!.Status);
        }

        private static List<int> SplitLineEdgeIds(RegionSession session, RegionFace face)
        {
            var splitIds = session.SplitLineEdges.Select(e => e.EdgeId).ToHashSet();
            return face.OuterLoop.Where(r => splitIds.Contains(r.EdgeId))
                                 .Select(r => r.EdgeId)
                                 .OrderBy(id => id)
                                 .ToList();
        }
    }
}
