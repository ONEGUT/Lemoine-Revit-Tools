using System;
using UglyToad.PdfPig;

namespace LemoineTools.PdfGeometry.Pdf
{
    public sealed class PdfPageProbe
    {
        public int PageCount { get; set; }
        public int PageNumber { get; set; }
        public double PageWidthPts { get; set; }
        public double PageHeightPts { get; set; }

        /// <summary>Number of drawable path commands (lines + beziers) on the page.</summary>
        public int PathCommandCount { get; set; }

        /// <summary>"Vector" when the page carries enough native linework to walk; otherwise "Raster".</summary>
        public string SuggestedMode { get; set; } = "Raster";
    }

    /// <summary>
    /// Inspects a PDF page with PdfPig and decides vector vs raster extraction:
    /// scanned drawings expose (almost) no path operations, native CAD exports
    /// expose thousands. The caller may always override the suggestion.
    /// </summary>
    public static class PdfContentProbe
    {
        /// <summary>Pages with fewer drawable path commands than this are treated as raster scans.</summary>
        public const int DefaultVectorThreshold = 200;

        public static PdfPageProbe Probe(string pdfPath, int pageNumber, int vectorThreshold = DefaultVectorThreshold)
        {
            using (var doc = PdfDocument.Open(pdfPath))
            {
                if (pageNumber < 1 || pageNumber > doc.NumberOfPages)
                    throw new ArgumentOutOfRangeException(nameof(pageNumber),
                        $"Page {pageNumber} out of range — document has {doc.NumberOfPages} page(s).");

                var page = doc.GetPage(pageNumber);
                int commands = 0;
                foreach (var path in page.Paths)
                {
                    if (path.IsClipping) continue;
                    foreach (var subpath in path)
                        foreach (var cmd in subpath.Commands)
                            if (cmd is UglyToad.PdfPig.Core.PdfSubpath.Line ||
                                cmd is UglyToad.PdfPig.Core.PdfSubpath.BezierCurve)
                                commands++;
                }

                return new PdfPageProbe
                {
                    PageCount = doc.NumberOfPages,
                    PageNumber = pageNumber,
                    PageWidthPts = page.Width,
                    PageHeightPts = page.Height,
                    PathCommandCount = commands,
                    SuggestedMode = commands >= vectorThreshold ? "Vector" : "Raster",
                };
            }
        }
    }
}
