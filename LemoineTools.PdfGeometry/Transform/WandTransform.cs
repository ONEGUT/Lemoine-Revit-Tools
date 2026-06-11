using System;
using LemoineTools.PdfGeometry.Primitives;

namespace LemoineTools.PdfGeometry.Transform
{
    /// <summary>
    /// Pure 2D mapping between PDF point space, raster pixel space, and model
    /// plan space (feet). Built by the Revit side from the ImageInstance
    /// placement; kept Revit-free so round trips are unit-testable.
    ///
    /// Conventions:
    ///   PDF points — origin bottom-left, Y up, 72 pts per inch.
    ///   Pixels     — origin top-left, Y down (rasterizer output).
    ///   Model      — plan XY in feet; the image is placed with its center at
    ///                <see cref="CenterModel"/>, rotated by <see cref="RotationRad"/>.
    /// </summary>
    public sealed class WandTransform
    {
        public double PageWidthPts { get; }
        public double PageHeightPts { get; }
        public double PlacedWidthFt { get; }
        public double PlacedHeightFt { get; }
        public Pt2 CenterModel { get; }
        public double RotationRad { get; }

        /// <summary>Model feet per PDF point (from the placed width).</summary>
        public double FtPerPt { get; }

        /// <summary>
        /// Relative width-vs-height scale disagreement of the placement. Should
        /// be ~0; a real value means the image was stretched non-uniformly and
        /// traced geometry will be off — surface it as a warning.
        /// </summary>
        public double ScaleMismatch { get; }

        private readonly double _cos;
        private readonly double _sin;

        public WandTransform(double pageWidthPts, double pageHeightPts,
                             double placedWidthFt, double placedHeightFt,
                             Pt2 centerModel, double rotationRad)
        {
            if (pageWidthPts <= 0 || pageHeightPts <= 0)
                throw new ArgumentOutOfRangeException(nameof(pageWidthPts), "Page size must be positive.");
            if (placedWidthFt <= 0 || placedHeightFt <= 0)
                throw new ArgumentOutOfRangeException(nameof(placedWidthFt), "Placed size must be positive.");

            PageWidthPts = pageWidthPts;
            PageHeightPts = pageHeightPts;
            PlacedWidthFt = placedWidthFt;
            PlacedHeightFt = placedHeightFt;
            CenterModel = centerModel;
            RotationRad = rotationRad;

            FtPerPt = placedWidthFt / pageWidthPts;
            double sH = placedHeightFt / pageHeightPts;
            ScaleMismatch = Math.Abs(sH - FtPerPt) / FtPerPt;

            _cos = Math.Cos(rotationRad);
            _sin = Math.Sin(rotationRad);
        }

        // ------------------------------------------------------------ pdf ↔ model

        public Pt2 PdfToModel(Pt2 pdf)
        {
            double lx = (pdf.X - PageWidthPts / 2) * FtPerPt;
            double ly = (pdf.Y - PageHeightPts / 2) * FtPerPt;
            return new Pt2(
                CenterModel.X + lx * _cos - ly * _sin,
                CenterModel.Y + lx * _sin + ly * _cos);
        }

        public Pt2 ModelToPdf(Pt2 model)
        {
            double dx = model.X - CenterModel.X;
            double dy = model.Y - CenterModel.Y;
            double lx = dx * _cos + dy * _sin;   // inverse rotation
            double ly = -dx * _sin + dy * _cos;
            return new Pt2(
                lx / FtPerPt + PageWidthPts / 2,
                ly / FtPerPt + PageHeightPts / 2);
        }

        // ------------------------------------------------------------ pdf ↔ pixel

        public Pt2 PdfToPixel(Pt2 pdf, double dpi)
        {
            double ppp = dpi / 72.0;
            return new Pt2(pdf.X * ppp, (PageHeightPts - pdf.Y) * ppp);
        }

        public Pt2 PixelToPdf(Pt2 pixel, double dpi)
        {
            double ppp = dpi / 72.0;
            return new Pt2(pixel.X / ppp, PageHeightPts - pixel.Y / ppp);
        }

        // -------------------------------------------------------- drawing scale

        /// <summary>ft² per pt², for area display.</summary>
        public double AreaFtPerPt => FtPerPt * FtPerPt;

        /// <summary>
        /// The paper scale implied by the image placement, as paper inches per
        /// model foot (e.g. 0.25 for 1/4" = 1'-0"). Compare with the user's
        /// declared drawing scale to warn about mis-sized underlays.
        /// </summary>
        public double ImpliedInchesPerFoot => 1.0 / (FtPerPt * 72.0);

        /// <summary>Model feet per PDF point for a declared paper scale (inches of paper per model foot).</summary>
        public static double FtPerPtForScale(double paperInchesPerFoot) =>
            1.0 / (paperInchesPerFoot * 72.0);
    }
}
