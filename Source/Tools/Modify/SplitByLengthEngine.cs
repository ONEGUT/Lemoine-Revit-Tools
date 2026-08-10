using System;
using System.Collections.Generic;
using LemoineTools.Tools.CopyFromLink;

namespace LemoineTools.Tools.ModifyElements
{
    /// <summary>
    /// Revit-free planning for the Split by Length tool: turns a run's length into the stations
    /// where it should be cut.
    ///
    /// The station maths itself is NOT reimplemented here — it is
    /// <see cref="CopyLinearEngine.SplitStations"/>, written for Copy Linear Elements' Split mode
    /// and reused verbatim. This class only adds the two things a native (already-in-host) split
    /// needs on top of it: the "even lengths" remainder mode, and a sliver guard so a rounding
    /// tail can never become a piece of its own.
    ///
    /// Everything here is pure arithmetic on doubles — no Revit types, no transaction — so the
    /// cut positions stay trivially reviewable. The handler owns all element mutation.
    /// </summary>
    public static class SplitByLengthEngine
    {
        /// <summary>
        /// Shortest piece the tool will ever produce (feet ≈ 1/8"). A cell or cut station closer
        /// than this to a run's end is a rounding artefact, not a piece the user asked for: it is
        /// dropped, so the material simply stays on the neighbouring piece.
        /// </summary>
        public const double MinPieceFeet = 0.01;

        /// <summary>
        /// The length each piece is actually cut to.
        ///
        /// "Offcut at end" uses the requested length as-is, so every piece is exactly that long and
        /// the tail is whatever remains. "Even lengths" divides the run into the fewest pieces that
        /// keeps each one at or under the requested length, so a 34 ft run at 10 ft becomes 4 × 8.5 ft
        /// instead of 10 + 10 + 10 + 4.
        /// </summary>
        public static double EffectiveSegment(double totalLen, double segLenFeet, bool evenLengths)
        {
            if (!evenLengths || totalLen <= MinPieceFeet || segLenFeet <= MinPieceFeet)
                return segLenFeet;

            // The −MinPieceFeet keeps a run of exactly N whole segments at N pieces rather than
            // letting floating-point noise round it up to N+1 (and so shorten every piece).
            int n = (int)Math.Ceiling((totalLen - MinPieceFeet) / segLenFeet);
            return n < 1 ? segLenFeet : totalLen / n;
        }

        /// <summary>
        /// The pieces a run is divided into, as [start,end] station pairs measured from its start.
        /// A <paramref name="gapFeet"/> above zero is taken off the interior cut faces only, so the
        /// run's two outer ends stay exactly where they were.
        ///
        /// Returns a single cell (or none) when there is nothing to do — the caller treats that as
        /// "skip this element", never as a split.
        /// </summary>
        public static List<(double Start, double End)> PlanCells(
            double totalLen, double segLenFeet, double gapFeet, bool evenLengths)
        {
            double eff = EffectiveSegment(totalLen, segLenFeet, evenLengths);

            // keepRemainder: true is the only correct value for a native split — the "drop it"
            // variant Copy Linear offers merely skips CREATING a piece, whereas here the run
            // already exists and dropping the tail would delete modelled ductwork.
            var cells = CopyLinearEngine.SplitStations(totalLen, eff, gapFeet, keepRemainder: true);

            // Sliver guard: a final cell shorter than MinPieceFeet is a rounding tail, not a piece.
            while (cells.Count > 1 && cells[cells.Count - 1].End - cells[cells.Count - 1].Start < MinPieceFeet)
                cells.RemoveAt(cells.Count - 1);

            return cells;
        }

        /// <summary>
        /// Interior positions (feet from the run's start, ascending) at which to cut a run that is
        /// to stay connected — the boundaries between consecutive pieces, with no gap. Stations
        /// within <see cref="MinPieceFeet"/> of either end are excluded, so a cut can never leave a
        /// sliver piece behind.
        /// </summary>
        public static List<double> CutStations(double totalLen, double segLenFeet, bool evenLengths)
        {
            var stations = new List<double>();
            var cells = PlanCells(totalLen, segLenFeet, 0.0, evenLengths);

            for (int i = 0; i < cells.Count - 1; i++)
            {
                double s = cells[i].End;
                if (s > MinPieceFeet && s < totalLen - MinPieceFeet) stations.Add(s);
            }
            return stations;
        }
    }
}
