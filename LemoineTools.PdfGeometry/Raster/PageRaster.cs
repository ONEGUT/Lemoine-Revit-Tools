using System;

namespace LemoineTools.PdfGeometry.Raster
{
    /// <summary>
    /// A grayscale rasterization of one PDF page. Produced by an
    /// <see cref="Pdf.IPageRasterizer"/> implementation (PDFium on the Revit
    /// side, synthetic buffers in tests). Pixel origin top-left, Y down;
    /// 0 = black, 255 = white.
    /// </summary>
    public sealed class PageRaster
    {
        public byte[] Gray { get; }
        public int Width { get; }
        public int Height { get; }
        public double Dpi { get; }

        public PageRaster(byte[] gray, int width, int height, double dpi)
        {
            if (gray.Length != width * height)
                throw new ArgumentException("Gray buffer length must equal width × height.", nameof(gray));
            if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));
            Gray = gray;
            Width = width;
            Height = height;
            Dpi = dpi;
        }

        public double PixelsPerPoint => Dpi / 72.0;
        public double PageWidthPts => Width / PixelsPerPoint;
        public double PageHeightPts => Height / PixelsPerPoint;

        /// <summary>
        /// Binarizes into a barrier mask: pixels darker than
        /// <paramref name="luminanceThreshold"/> (0..1, default ~0.5) become barriers.
        /// </summary>
        public BitGrid Binarize(double luminanceThreshold)
        {
            if (luminanceThreshold < 0 || luminanceThreshold > 1)
                throw new ArgumentOutOfRangeException(nameof(luminanceThreshold));
            byte cutoff = (byte)Math.Round(luminanceThreshold * 255.0);
            var grid = new BitGrid(Width, Height);
            for (int y = 0; y < Height; y++)
            {
                int row = y * Width;
                for (int x = 0; x < Width; x++)
                    if (Gray[row + x] < cutoff)
                        grid[x, y] = true;
            }
            return grid;
        }
    }
}
