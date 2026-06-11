using LemoineTools.PdfGeometry.Raster;

namespace LemoineTools.PdfGeometry.Pdf
{
    /// <summary>
    /// Seam between the managed geometry core and whatever renders PDF pages
    /// to pixels. The Revit addin implements this with PDFium (native dll);
    /// tests feed synthetic <see cref="PageRaster"/> buffers instead, so the
    /// core never takes a native dependency.
    /// </summary>
    public interface IPageRasterizer
    {
        /// <summary>Renders one page (1-based) to grayscale at the given DPI.</summary>
        PageRaster Rasterize(string pdfPath, int pageNumber, int dpi);
    }
}
