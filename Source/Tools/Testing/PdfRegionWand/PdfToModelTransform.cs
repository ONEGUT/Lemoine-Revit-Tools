using System;
using Autodesk.Revit.DB;
using LemoineTools.PdfGeometry.Primitives;
using LemoineTools.PdfGeometry.Transform;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.PdfRegionWand
{
    /// <summary>
    /// Pure data captured from an ImageInstance on the Revit thread, so the
    /// transform can be assembled later (off-thread) once the PDF page size is
    /// known from the PdfPig probe.
    /// </summary>
    public sealed class ImagePlacement
    {
        public double WidthFt { get; set; }
        public double HeightFt { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double CenterZ { get; set; }
        public double RotationRad { get; set; }

        /// <summary>True when the size came from the bbox of a rotated instance (inexact).</summary>
        public bool SizeFromRotatedBbox { get; set; }
    }

    /// <summary>
    /// Builds the Revit-free <see cref="WandTransform"/> from a captured image
    /// placement and exposes XYZ-flavored conversions. All math lives in the
    /// core (unit-tested); this class only adapts coordinate types.
    /// </summary>
    public sealed class PdfToModelTransform
    {
        public WandTransform Core { get; }

        /// <summary>Z used for created geometry and for flattening picked points.</summary>
        public double PlanZ { get; }

        private PdfToModelTransform(WandTransform core, double planZ)
        {
            Core = core;
            PlanZ = planZ;
        }

        public static PdfToModelTransform? FromPlacement(
            ImagePlacement placement,
            double pageWidthPts, double pageHeightPts,
            Action<string, string>? log)
        {
            if (placement.WidthFt <= 0 || placement.HeightFt <= 0 ||
                pageWidthPts <= 0 || pageHeightPts <= 0)
            {
                log?.Invoke(LemoineStrings.T("pdfRegionWand.transform.noSize"), "fail");
                return null;
            }
            if (placement.SizeFromRotatedBbox)
                log?.Invoke(LemoineStrings.T("pdfRegionWand.transform.rotatedInstance"), "warn");

            var core = new WandTransform(
                pageWidthPts, pageHeightPts,
                placement.WidthFt, placement.HeightFt,
                new Pt2(placement.CenterX, placement.CenterY),
                placement.RotationRad);

            if (core.ScaleMismatch > 0.01)
                log?.Invoke(LemoineStrings.T("pdfRegionWand.transform.stretched", core.ScaleMismatch), "warn");

            return new PdfToModelTransform(core, placement.CenterZ);
        }

        public XYZ PdfToModel(Pt2 pdf)
        {
            var m = Core.PdfToModel(pdf);
            return new XYZ(m.X, m.Y, PlanZ);
        }

        public Pt2 ModelToPdf(XYZ model) => Core.ModelToPdf(new Pt2(model.X, model.Y));

        /// <summary>Converts a tolerance given in model feet into PDF points.</summary>
        public double ModelFtToPts(double feet) => feet / Core.FtPerPt;

        public double AreaPtsToFt2(double pts2) => pts2 * Core.AreaFtPerPt;
    }
}
