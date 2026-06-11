using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.PdfGeometry.Arcs;
using LemoineTools.PdfGeometry.Plans;
using LemoineTools.PdfGeometry.Primitives;
using Xunit;

namespace LemoineTools.PdfGeometry.Tests
{
    public class ArcFitterTests
    {
        private static List<Pt2> ArcPoints(Pt2 center, double r, double a0Deg, double a1Deg, double stepDeg)
        {
            var pts = new List<Pt2>();
            double sign = a1Deg >= a0Deg ? 1 : -1;
            for (double a = a0Deg; sign > 0 ? a <= a1Deg + 1e-9 : a >= a1Deg - 1e-9; a += sign * stepDeg)
            {
                double rad = a * Math.PI / 180;
                pts.Add(center + new Pt2(Math.Cos(rad), Math.Sin(rad)) * r);
            }
            return pts;
        }

        [Fact]
        public void Semicircle_BecomesOneArc()
        {
            var pts = ArcPoints(new Pt2(0, 0), 50, 0, 180, 5);
            var pieces = ArcFitter.Fit(pts, tol: 0.5);

            Assert.Single(pieces);
            var arc = pieces[0];
            Assert.Equal(PlanCurveKind.Arc, arc.Kind);
            Assert.Equal(pts[0], arc.Start);
            Assert.Equal(pts[pts.Count - 1], arc.End);
            // The on-arc point sits on the fitted circle, near the top.
            Assert.InRange(arc.Mid.DistanceTo(new Pt2(0, 0)), 49.5, 50.5);
            Assert.InRange(arc.Mid.Y, 45, 51);
        }

        [Fact]
        public void StraightPolyline_BecomesOneLine()
        {
            var pts = new List<Pt2>();
            for (int i = 0; i <= 20; i++) pts.Add(new Pt2(i * 5, 0));
            var pieces = ArcFitter.Fit(pts, tol: 0.5);

            Assert.Single(pieces);
            Assert.Equal(PlanCurveKind.Line, pieces[0].Kind);
            Assert.Equal(new Pt2(0, 0), pieces[0].Start);
            Assert.Equal(new Pt2(100, 0), pieces[0].End);
        }

        [Fact]
        public void LineArcLine_SplitsIntoThreePieces()
        {
            // Straight in, quarter arc (r=30 around (60,30)), straight up.
            var pts = new List<Pt2>();
            for (int i = 0; i <= 6; i++) pts.Add(new Pt2(i * 10, 0));
            pts.AddRange(ArcPoints(new Pt2(60, 30), 30, -90, 0, 5).Skip(1));
            for (int i = 1; i <= 6; i++) pts.Add(new Pt2(90, 30 + i * 10));

            var pieces = ArcFitter.Fit(pts, tol: 0.5);

            Assert.Contains(pieces, p => p.Kind == PlanCurveKind.Arc);
            Assert.True(pieces.Count <= 5, $"expected ≤5 pieces, got {pieces.Count}");
            Assert.Equal(PlanCurveKind.Line, pieces[0].Kind);
            Assert.Equal(PlanCurveKind.Line, pieces[pieces.Count - 1].Kind);

            // Piece chain remains contiguous from start to end.
            Assert.Equal(pts[0], pieces[0].Start);
            Assert.Equal(pts[pts.Count - 1], pieces[pieces.Count - 1].End);
            for (int i = 1; i < pieces.Count; i++)
                Assert.True(pieces[i - 1].End.DistanceTo(pieces[i].Start) < 1e-9);
        }

        [Fact]
        public void Deterministic_SameInputSamePieces()
        {
            var pts = ArcPoints(new Pt2(10, 20), 40, 30, 200, 4);
            var p1 = ArcFitter.Fit(pts, 0.5);
            var p2 = ArcFitter.Fit(pts, 0.5);

            Assert.Equal(p1.Count, p2.Count);
            for (int i = 0; i < p1.Count; i++)
            {
                Assert.Equal(p1[i].Kind, p2[i].Kind);
                Assert.Equal(p1[i].Start, p2[i].Start);
                Assert.Equal(p1[i].End, p2[i].End);
                Assert.Equal(p1[i].Mid, p2[i].Mid);
            }
        }

        [Fact]
        public void ReversedPieces_KeepIdenticalArcGeometry()
        {
            var pts = ArcPoints(new Pt2(0, 0), 25, 0, 120, 5);
            var pieces = ArcFitter.Fit(pts, 0.5);
            Assert.Single(pieces);

            var rev = pieces[0].Reversed();
            Assert.Equal(pieces[0].Start, rev.End);
            Assert.Equal(pieces[0].End, rev.Start);
            Assert.Equal(pieces[0].Mid, rev.Mid); // same circle, same on-arc point
        }

        [Fact]
        public void BarelyBowedLine_StaysLines()
        {
            // Deviation under the minimum sweep — must not become a giant arc.
            var pts = new List<Pt2>();
            for (int i = 0; i <= 10; i++)
            {
                double x = i * 10;
                pts.Add(new Pt2(x, 0.4 * Math.Sin(Math.PI * x / 100)));
            }
            var pieces = ArcFitter.Fit(pts, tol: 0.5);
            Assert.All(pieces, p => Assert.Equal(PlanCurveKind.Line, p.Kind));
        }
    }
}
