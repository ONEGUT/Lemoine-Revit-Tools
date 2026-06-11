using System;

namespace LemoineTools.PdfGeometry.Raster
{
    /// <summary>
    /// Dense 1-bit-per-pixel grid. Pixel space convention: origin top-left,
    /// X right, Y down (matches rasterizer output). Used both as the barrier
    /// mask (true = barrier/linework) and as the filled-region mask.
    /// </summary>
    public sealed class BitGrid
    {
        private readonly bool[] _bits;

        public int Width { get; }
        public int Height { get; }

        public BitGrid(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "BitGrid dimensions must be positive.");
            Width = width;
            Height = height;
            _bits = new bool[width * height];
        }

        public bool this[int x, int y]
        {
            get => _bits[y * Width + x];
            set => _bits[y * Width + x] = value;
        }

        public bool InBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;

        /// <summary>Out-of-bounds reads as false.</summary>
        public bool GetSafe(int x, int y) => InBounds(x, y) && _bits[y * Width + x];

        public BitGrid Clone()
        {
            var c = new BitGrid(Width, Height);
            Array.Copy(_bits, c._bits, _bits.Length);
            return c;
        }

        public int CountSet()
        {
            int n = 0;
            for (int i = 0; i < _bits.Length; i++) if (_bits[i]) n++;
            return n;
        }
    }
}
