using Autodesk.Navisworks.Api;

namespace LemoineNavisworks.FloorSplit
{
    // =========================================================================
    // Plain data types for the Floor Splitter tool. No Navisworks calls here —
    // just the level list, the derived floor bands, and a cached per-item
    // vertical extent so the hide/keep test never re-reads geometry.
    // =========================================================================

    /// <summary>How an element that straddles a floor line is assigned.</summary>
    public enum StraddleRule
    {
        /// <summary>Keep the element visible in every floor its extent overlaps
        /// (a riser/column crossing a floor line appears on both floors).</summary>
        KeepOverlapping,

        /// <summary>Assign the element to exactly one floor by its bounding-box
        /// centre Z (no duplication; a full-height riser lands on one floor).</summary>
        ByCentroid,
    }

    /// <summary>A user-selectable level: a name and an elevation in the model's
    /// bounding-box Z units. <see cref="UseAsBoundary"/> marks it as a floor cut.</summary>
    public sealed class LevelDef
    {
        public string Name;
        public double Elevation;
        public bool   UseAsBoundary;

        public LevelDef(string name, double elevation, bool useAsBoundary = false)
        {
            Name = name;
            Elevation = elevation;
            UseAsBoundary = useAsBoundary;
        }
    }

    /// <summary>A derived floor: [Low, High] in bounding-box Z units. The bottom
    /// floor's Low is −∞ and the top floor's High is +∞.</summary>
    public sealed class FloorBand
    {
        public string Name;
        public double Low;
        public double High;

        public FloorBand(string name, double low, double high)
        {
            Name = name;
            Low = low;
            High = high;
        }

        public bool LowOpen  => double.IsNegativeInfinity(Low);
        public bool HighOpen => double.IsPositiveInfinity(High);
    }

    /// <summary>A geometry item plus its cached vertical extent, gathered once so
    /// the per-floor classification is a pure numeric compare.</summary>
    internal struct ItemZ
    {
        public ModelItem Item;
        public double MinZ;
        public double MaxZ;

        public double CentreZ => (MinZ + MaxZ) * 0.5;
    }
}
