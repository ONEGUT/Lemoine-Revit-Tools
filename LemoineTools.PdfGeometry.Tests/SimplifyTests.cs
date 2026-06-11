using System;
using System.Collections.Generic;
using LemoineTools.PdfGeometry.Primitives;
using LemoineTools.PdfGeometry.Simplify;
using Xunit;

namespace LemoineTools.PdfGeometry.Tests
{
    public class SimplifyTests
    {
        [Fact]
        public void DouglasPeucker_CollapsesCollinearRun()
        {
            var pts = new List<Pt2>();
            for (int i = 0; i <= 100; i++) pts.Add(new Pt2(i, 0));
            var simplified = DouglasPeucker.SimplifyOpen(pts, 0.5);
            Assert.Equal(2, simplified.Count);
        }

        [Fact]
        public void DouglasPeucker_KeepsRealCorner()
        {
            var pts = new List<Pt2>();
            for (int i = 0; i <= 50; i++) pts.Add(new Pt2(i, 0));
            for (int i = 1; i <= 50; i++) pts.Add(new Pt2(50, i));
            var simplified = DouglasPeucker.SimplifyOpen(pts, 0.5);
            Assert.Equal(3, simplified.Count);
            Assert.Equal(new Pt2(50, 0), simplified[1]);
        }

        [Fact]
        public void DouglasPeucker_ClosedRing_NoisyRectangleToFourCorners()
        {
            // Rectangle ring with ±0.2 jitter, vertices every 1 unit.
            var rnd = new Random(42);
            var ring = new List<Pt2>();
            double J() => (rnd.NextDouble() - 0.5) * 0.4;
            for (int x = 0; x < 100; x++) ring.Add(new Pt2(x, 0 + J()));
            for (int y = 0; y < 60; y++) ring.Add(new Pt2(100 + J(), y));
            for (int x = 100; x > 0; x--) ring.Add(new Pt2(x, 60 + J()));
            for (int y = 60; y > 0; y--) ring.Add(new Pt2(0 + J(), y));

            var simplified = DouglasPeucker.SimplifyClosed(ring, 1.0);
            Assert.InRange(simplified.Count, 3, 8); // jitter gone, corners kept
        }

        [Fact]
        public void OrthoSnap_SnapsNearOrthoRectangleExactly()
        {
            // Rectangle whose edges are ~1° off axis.
            double skew = Math.Tan(1.0 * Math.PI / 180);
            var ring = new List<Pt2>
            {
                new Pt2(0, 0),
                new Pt2(100, 100 * skew),
                new Pt2(100 - 60 * skew, 100 * skew + 60),
                new Pt2(-60 * skew + 0, 60),
            };

            var snapped = OrthoSnap.Apply(ring, 2.0, 1.0);

            Assert.Equal(4, snapped.Count);
            foreach (var (a, b) in Pairs(snapped))
            {
                bool horizontal = Math.Abs(a.Y - b.Y) < 1e-9;
                bool vertical = Math.Abs(a.X - b.X) < 1e-9;
                Assert.True(horizontal || vertical, $"segment {a}→{b} is not ortho");
            }
        }

        [Fact]
        public void OrthoSnap_LeavesTrueDiagonalAlone()
        {
            var ring = new List<Pt2> { new Pt2(0, 0), new Pt2(100, 0), new Pt2(100, 100) };
            var snapped = OrthoSnap.Apply(ring, 2.0, 1.0);

            Assert.Equal(3, snapped.Count);
            // The hypotenuse (100,100)→(0,0) must survive at 45°.
            bool hasDiagonal = false;
            foreach (var (a, b) in Pairs(snapped))
                if (Math.Abs(Math.Abs(b.X - a.X) - Math.Abs(b.Y - a.Y)) < 1e-6 && Math.Abs(b.X - a.X) > 1)
                    hasDiagonal = true;
            Assert.True(hasDiagonal);
        }

        [Fact]
        public void OrthoSnap_MergesJoggedWallIntoOneRun()
        {
            // A "horizontal" wall traced as two H runs with a 0.5 jog between them.
            var ring = new List<Pt2>
            {
                new Pt2(0, 0), new Pt2(50, 0), new Pt2(50, 0.5), new Pt2(100, 0.5),
                new Pt2(100, 60), new Pt2(0, 60),
            };
            var snapped = OrthoSnap.Apply(ring, 2.0, 1.0);
            Assert.Equal(4, snapped.Count); // jog merged — clean rectangle
        }

        private static IEnumerable<(Pt2 a, Pt2 b)> Pairs(IReadOnlyList<Pt2> ring)
        {
            for (int i = 0; i < ring.Count; i++)
                yield return (ring[i], ring[(i + 1) % ring.Count]);
        }
    }
}
