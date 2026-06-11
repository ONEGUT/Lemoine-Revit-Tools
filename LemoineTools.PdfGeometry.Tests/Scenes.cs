using System;
using LemoineTools.PdfGeometry.Engine;
using LemoineTools.PdfGeometry.Primitives;
using LemoineTools.PdfGeometry.Raster;

namespace LemoineTools.PdfGeometry.Tests
{
    /// <summary>
    /// Synthetic raster scenes. All scenes use 72 dpi so 1 pixel == 1 PDF point
    /// and the only pdf↔pixel difference is the Y flip (pdfY = height − pixelY).
    /// </summary>
    internal static class Scenes
    {
        public static byte[] White(int w, int h)
        {
            var g = new byte[w * h];
            for (int i = 0; i < g.Length; i++) g[i] = 255;
            return g;
        }

        public static void HLine(byte[] g, int w, int x0, int x1, int y, int thick = 2)
        {
            for (int t = 0; t < thick; t++)
                for (int x = x0; x <= x1; x++)
                    g[(y + t) * w + x] = 0;
        }

        public static void VLine(byte[] g, int w, int x, int y0, int y1, int thick = 2)
        {
            for (int t = 0; t < thick; t++)
                for (int y = y0; y <= y1; y++)
                    g[y * w + x + t] = 0;
        }

        public static void RectOutline(byte[] g, int w, int x0, int y0, int x1, int y1, int thick = 2)
        {
            HLine(g, w, x0, x1, y0, thick);
            HLine(g, w, x0, x1, y1, thick);
            VLine(g, w, x0, y0, y1 + thick - 1, thick);
            VLine(g, w, x1, y0, y1 + thick - 1, thick);
        }

        public static RegionWandEngine RasterEngine(byte[] g, int w, int h, Action<WandConfig>? mutate = null)
        {
            var cfg = new WandConfig();
            mutate?.Invoke(cfg);
            var engine = new RegionWandEngine(cfg) { Mode = ExtractionMode.Raster };
            engine.LoadRaster(new PageRaster(g, w, h, 72));
            return engine;
        }

        /// <summary>Pixel coordinates → PDF points for a 72 dpi scene of the given pixel height.</summary>
        public static Pt2 Pdf(double xPx, double yPx, int heightPx) => new Pt2(xPx, heightPx - yPx);
    }
}
