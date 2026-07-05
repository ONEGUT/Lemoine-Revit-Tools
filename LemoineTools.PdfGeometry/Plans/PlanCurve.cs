using LemoineTools.PdfGeometry.Primitives;

namespace LemoineTools.PdfGeometry.Plans
{
    public enum PlanCurveKind { Line, Arc }

    /// <summary>
    /// One curve piece of a region boundary in PDF point space. Lines carry
    /// Start/End; arcs additionally carry a point on the arc (Mid), which maps
    /// 1:1 onto Revit's Arc.Create(start, end, pointOnArc).
    /// </summary>
    public readonly struct PlanCurve
    {
        public PlanCurveKind Kind { get; }
        public Pt2 Start { get; }
        public Pt2 End { get; }
        public Pt2 Mid { get; }

        private PlanCurve(PlanCurveKind kind, Pt2 start, Pt2 end, Pt2 mid)
        {
            Kind = kind; Start = start; End = end; Mid = mid;
        }

        public static PlanCurve Line(Pt2 start, Pt2 end) =>
            new PlanCurve(PlanCurveKind.Line, start, end, Pt2.Lerp(start, end, 0.5));

        public static PlanCurve Arc(Pt2 start, Pt2 end, Pt2 pointOnArc) =>
            new PlanCurve(PlanCurveKind.Arc, start, end, pointOnArc);

        /// <summary>The same piece traversed end→start (arc keeps its on-curve point).</summary>
        public PlanCurve Reversed() =>
            Kind == PlanCurveKind.Line ? Line(End, Start) : Arc(End, Start, Mid);

        public override string ToString() =>
            Kind == PlanCurveKind.Line ? $"L {Start}→{End}" : $"A {Start}→{End} via {Mid}";
    }
}
