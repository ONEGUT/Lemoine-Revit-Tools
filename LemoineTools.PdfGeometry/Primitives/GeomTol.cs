namespace LemoineTools.PdfGeometry.Primitives
{
    /// <summary>
    /// Tolerance bundle for the wand pipeline. All values are PDF points
    /// (1/72") unless stated otherwise — the Revit side converts user
    /// settings (model units at drawing scale) into points before handing
    /// them to the core.
    /// </summary>
    public sealed class GeomTol
    {
        /// <summary>Douglas-Peucker simplification tolerance.</summary>
        public double SimplifyTol { get; set; } = 1.5;

        /// <summary>Max angular deviation from horizontal/vertical that still snaps to ortho, degrees.</summary>
        public double OrthoSnapAngleDeg { get; set; } = 2.0;

        /// <summary>
        /// Distance within which a traced boundary is considered to run along an
        /// existing shared edge (reconciliation match distance).
        /// </summary>
        public double ReconcileTol { get; set; } = 3.0;

        /// <summary>Endpoint clustering distance for the vector segment graph (gap closing).</summary>
        public double VectorJoinTol { get; set; } = 2.0;

        /// <summary>Chord tolerance for flattening beziers / arcs into polylines.</summary>
        public double ChordTol { get; set; } = 0.25;

        /// <summary>Max radial deviation for arc reconstruction from a polyline.</summary>
        public double ArcFitTol { get; set; } = 0.75;

        /// <summary>Minimum face area; traced loops below this are rejected as noise. (pt²)</summary>
        public double MinFaceArea { get; set; } = 36.0;
    }
}
