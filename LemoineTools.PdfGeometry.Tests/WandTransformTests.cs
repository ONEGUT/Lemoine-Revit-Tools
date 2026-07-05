using System;
using LemoineTools.PdfGeometry.Primitives;
using LemoineTools.PdfGeometry.Transform;
using Xunit;

namespace LemoineTools.PdfGeometry.Tests
{
    public class WandTransformTests
    {
        private static WandTransform Make(double inchesPerFoot, double rotationDeg,
                                          double pageW = 2592, double pageH = 1728, // 36"×24" sheet
                                          double cx = 120, double cy = -45)
        {
            double ftPerPt = WandTransform.FtPerPtForScale(inchesPerFoot);
            return new WandTransform(pageW, pageH,
                placedWidthFt: pageW * ftPerPt,
                placedHeightFt: pageH * ftPerPt,
                centerModel: new Pt2(cx, cy),
                rotationRad: rotationDeg * Math.PI / 180);
        }

        [Theory]
        [InlineData(0.25, 0)]      // 1/4" = 1'-0", unrotated
        [InlineData(0.25, 30)]
        [InlineData(0.125, 90)]    // 1/8" = 1'-0", quarter turn
        [InlineData(0.125, -135)]
        [InlineData(1.0, 17.3)]    // 1" = 1'-0", arbitrary angle
        public void PdfModelRoundTrip_BothDirections(double inchesPerFoot, double rotationDeg)
        {
            var t = Make(inchesPerFoot, rotationDeg);
            var probes = new[]
            {
                new Pt2(0, 0), new Pt2(2592, 1728), new Pt2(36, 1700),
                new Pt2(1296, 864), new Pt2(2000.25, 13.7),
            };
            foreach (var pdf in probes)
            {
                var model = t.PdfToModel(pdf);
                var back = t.ModelToPdf(model);
                Assert.True(back.DistanceTo(pdf) < 1e-9, $"pdf→model→pdf drift {back.DistanceTo(pdf)} at {pdf}");

                var pdf2 = t.ModelToPdf(model);
                var model2 = t.PdfToModel(pdf2);
                Assert.True(model2.DistanceTo(model) < 1e-9, "model→pdf→model drift");
            }
        }

        [Fact]
        public void KnownPoint_QuarterInchScale_Unrotated()
        {
            // 1/4" = 1' ⇒ 18 pt per ft. One foot right of page center.
            var t = Make(0.25, 0, cx: 0, cy: 0);
            var model = t.PdfToModel(new Pt2(2592 / 2.0 + 18, 1728 / 2.0));
            Assert.Equal(1.0, model.X, 9);
            Assert.Equal(0.0, model.Y, 9);
        }

        [Fact]
        public void Rotation90_TurnsPageXIntoModelY()
        {
            var t = Make(0.25, 90, cx: 0, cy: 0);
            var model = t.PdfToModel(new Pt2(2592 / 2.0 + 18, 1728 / 2.0));
            Assert.Equal(0.0, model.X, 9);
            Assert.Equal(1.0, model.Y, 9);
        }

        [Fact]
        public void PixelRoundTrip_FlipsY()
        {
            var t = Make(0.25, 0);
            double dpi = 300;

            // Pixel origin is the page's top-left.
            var topLeftPdf = t.PixelToPdf(new Pt2(0, 0), dpi);
            Assert.Equal(0, topLeftPdf.X, 9);
            Assert.Equal(1728, topLeftPdf.Y, 9);

            var probes = new[] { new Pt2(0, 0), new Pt2(123.5, 456.25), new Pt2(9000, 7000) };
            foreach (var px in probes)
            {
                var back = t.PdfToPixel(t.PixelToPdf(px, dpi), dpi);
                Assert.True(back.DistanceTo(px) < 1e-9);
            }
        }

        [Fact]
        public void ImpliedScale_MatchesDeclaredScale()
        {
            var t = Make(0.25, 0);
            Assert.Equal(0.25, t.ImpliedInchesPerFoot, 9);
            Assert.True(t.ScaleMismatch < 1e-12);
        }

        [Fact]
        public void StretchedPlacement_ReportsScaleMismatch()
        {
            var t = new WandTransform(1000, 800, placedWidthFt: 100, placedHeightFt: 90,
                centerModel: Pt2.Zero, rotationRad: 0);
            Assert.True(t.ScaleMismatch > 0.1);
        }

        [Fact]
        public void AreaConversion_SquareFootPerSquarePoint()
        {
            var t = Make(0.25, 0); // 18 pt per ft ⇒ 324 pt² per ft²
            Assert.Equal(1.0 / 324.0, t.AreaFtPerPt, 12);
        }
    }
}
