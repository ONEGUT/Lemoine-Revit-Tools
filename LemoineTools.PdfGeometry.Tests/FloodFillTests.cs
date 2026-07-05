using LemoineTools.PdfGeometry.Engine;
using LemoineTools.PdfGeometry.Primitives;
using LemoineTools.PdfGeometry.Raster;
using Xunit;

namespace LemoineTools.PdfGeometry.Tests
{
    public class FloodFillTests
    {
        [Fact]
        public void RectangleRoom_FillsInteriorOnly()
        {
            var barriers = new BitGrid(200, 200);
            for (int x = 20; x <= 180; x++) { barriers[x, 20] = true; barriers[x, 120] = true; }
            for (int y = 20; y <= 120; y++) { barriers[20, y] = true; barriers[180, y] = true; }

            var result = ScanlineFloodFill.Fill(barriers, 100, 70, 1_000_000);

            Assert.True(result.Enclosed);
            int expected = (180 - 20 - 1) * (120 - 20 - 1); // open interior
            Assert.Equal(expected, result.PixelCount);
            Assert.Equal(21, result.MinX);
            Assert.Equal(179, result.MaxX);
            Assert.Equal(21, result.MinY);
            Assert.Equal(119, result.MaxY);
        }

        [Fact]
        public void SeedOnBarrier_Fails()
        {
            var barriers = new BitGrid(50, 50);
            barriers[10, 10] = true;
            var result = ScanlineFloodFill.Fill(barriers, 10, 10, 1000);
            Assert.False(result.Enclosed);
            Assert.Equal(FloodFailReason.SeedOnBarrier, result.FailReason);
        }

        [Fact]
        public void UnenclosedSeed_LeaksAndFailsGracefully()
        {
            // Rectangle with a gap in the top wall.
            var barriers = new BitGrid(200, 200);
            for (int x = 20; x <= 180; x++) { if (x < 90 || x > 110) barriers[x, 20] = true; barriers[x, 120] = true; }
            for (int y = 20; y <= 120; y++) { barriers[20, y] = true; barriers[180, y] = true; }

            var result = ScanlineFloodFill.Fill(barriers, 100, 70, int.MaxValue);
            Assert.False(result.Enclosed);
            Assert.Equal(FloodFailReason.NotEnclosed, result.FailReason);
        }

        [Fact]
        public void SizeCap_StopsRunawayFill()
        {
            // Fully enclosed but bigger than the cap — must fail as not enclosed.
            var barriers = new BitGrid(200, 200);
            for (int x = 5; x <= 195; x++) { barriers[x, 5] = true; barriers[x, 195] = true; }
            for (int y = 5; y <= 195; y++) { barriers[5, y] = true; barriers[195, y] = true; }

            var result = ScanlineFloodFill.Fill(barriers, 100, 100, 1000);
            Assert.False(result.Enclosed);
            Assert.Equal(FloodFailReason.NotEnclosed, result.FailReason);
        }

        [Fact]
        public void LShapeRoom_WandProducesSixCornerBoundary()
        {
            // L-shaped room: a 160×100 rect with its top-right 80×50 bitten out.
            int w = 220, h = 220;
            var g = Scenes.White(w, h);
            // Outline of the L polygon (pixel coords, y down):
            Scenes.HLine(g, w, 20, 100, 20);    // top edge of the tall part
            Scenes.VLine(g, w, 100, 20, 70);    // down to the notch
            Scenes.HLine(g, w, 100, 180, 70);   // notch bottom going right
            Scenes.VLine(g, w, 180, 70, 120);   // right edge (short part)
            Scenes.HLine(g, w, 20, 180, 120);   // bottom edge
            Scenes.VLine(g, w, 20, 20, 120);    // left edge

            var engine = Scenes.RasterEngine(g, w, h);
            var result = engine.Wand(Scenes.Pdf(60, 100, h));

            Assert.Equal(WandStatus.Ok, result.Status);
            Assert.NotNull(result.Plan);
            Assert.Equal(6, result.Plan!.OuterLoop.Count); // ortho-snapped L = 6 corners
            Assert.Empty(result.Plan.Holes);
        }

        [Fact]
        public void RoomWithColumn_HoleIsDetected()
        {
            int w = 220, h = 220;
            var g = Scenes.White(w, h);
            Scenes.RectOutline(g, w, 20, 20, 180, 120);
            Scenes.RectOutline(g, w, 90, 60, 110, 80); // column

            var engine = Scenes.RasterEngine(g, w, h);
            var result = engine.Wand(Scenes.Pdf(40, 40, h));

            Assert.Equal(WandStatus.Ok, result.Status);
            Assert.Single(result.Plan!.Holes);
            Assert.Equal(4, result.Plan.Holes[0].Count); // ortho-snapped square column

            double holeArea = PolylineOps.Area(result.Plan.Holes[0]);
            Assert.InRange(holeArea, 300, 600); // ~21×21 col outline footprint
        }
    }
}
