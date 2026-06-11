using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using LemoineTools.Lemoine;
using LemoineTools.PdfGeometry.Pdf;
using LemoineTools.PdfGeometry.Raster;

namespace LemoineTools.Tools.Testing.PdfRegionWand
{
    /// <summary>
    /// PDFium-backed implementation of the geometry core's rasterizer seam.
    /// Renders a page to grayscale at the requested DPI via PdfiumViewer.
    /// </summary>
    public sealed class PdfiumPageRasterizer : IPageRasterizer
    {
        public PageRaster Rasterize(string pdfPath, int pageNumber, int dpi)
        {
            PdfiumNativeLoader.EnsureLoaded();

            using (var doc = PdfiumViewer.PdfDocument.Load(pdfPath))
            {
                int pageIndex = pageNumber - 1;
                if (pageIndex < 0 || pageIndex >= doc.PageCount)
                    throw new ArgumentOutOfRangeException(nameof(pageNumber),
                        $"Page {pageNumber} out of range — document has {doc.PageCount} page(s).");

                var sizePts = doc.PageSizes[pageIndex]; // PdfiumViewer reports points
                int wPx = Math.Max(1, (int)Math.Round(sizePts.Width * dpi / 72.0));
                int hPx = Math.Max(1, (int)Math.Round(sizePts.Height * dpi / 72.0));

                using (var img = doc.Render(pageIndex, wPx, hPx, dpi, dpi,
                                            PdfiumViewer.PdfRenderFlags.Grayscale))
                using (var bmp = new Bitmap(img))
                {
                    return ToGray(bmp, dpi);
                }
            }
        }

        private static PageRaster ToGray(Bitmap bmp, int dpi)
        {
            int w = bmp.Width, h = bmp.Height;
            var gray = new byte[w * h];

            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                var row = new byte[stride];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(data.Scan0 + y * stride, row, 0, stride);
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int p = x * 4;
                        // BGRA → luminance (Rec. 601)
                        gray[o + x] = (byte)((row[p] * 114 + row[p + 1] * 587 + row[p + 2] * 299) / 1000);
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return new PageRaster(gray, w, h, dpi);
        }
    }

    /// <summary>
    /// Loads the native pdfium.dll explicitly from the addin folder before
    /// PdfiumViewer first touches it. Revit's working directory is not the
    /// addin directory, so the default Win32 search would miss the dll that
    /// the NuGet native package deploys beside LemoineTools.dll.
    /// </summary>
    internal static class PdfiumNativeLoader
    {
        private static bool _loaded;
        private static readonly object _gate = new object();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_gate)
            {
                if (_loaded) return;

                var addinDir = Path.GetDirectoryName(typeof(PdfiumNativeLoader).Assembly.Location) ?? "";
                // PdfiumViewer.Native.x86_64.v8-xfa deploys to x64\pdfium.dll;
                // probe the flat layout too in case the copy step flattens it.
                var candidates = new[]
                {
                    Path.Combine(addinDir, "x64", "pdfium.dll"),
                    Path.Combine(addinDir, "pdfium.dll"),
                };

                foreach (var path in candidates)
                {
                    if (!File.Exists(path)) continue;
                    var handle = LoadLibrary(path);
                    if (handle != IntPtr.Zero)
                    {
                        LemoineLog.Info("PdfiumNativeLoader", $"Loaded native pdfium from {path}");
                        _loaded = true;
                        return;
                    }
                    LemoineLog.Warn("PdfiumNativeLoader",
                        $"LoadLibrary failed for {path} (error {Marshal.GetLastWin32Error()})");
                }

                // Not fatal yet — PdfiumViewer may still resolve it through its own
                // probing. Record the situation so a raster-mode failure is explainable.
                LemoineLog.Warn("PdfiumNativeLoader",
                    $"pdfium.dll not found beside the addin ({addinDir}); raster mode will fail if PdfiumViewer cannot resolve it.");
                _loaded = true; // don't retry on every call
            }
        }
    }
}
