using System.Collections.Generic;
using System.Collections.ObjectModel;
using Autodesk.Navisworks.Api;

namespace LemoineNavisworks.LevelModels
{
    // =========================================================================
    // Plain data types for the Level Models tool. No Navisworks calls here — the
    // level list, the per-level model assignment, and a cached per-item vertical
    // extent so the optional trim never re-reads geometry.
    // =========================================================================

    /// <summary>How an element crossing a level's band edge is treated. Only consulted for
    /// levels with <see cref="LevelDef.Trim"/> on.</summary>
    public enum StraddleRule
    {
        /// <summary>Keep it on every level whose band its extent overlaps — a column crossing a
        /// floor line appears on both neighbours.</summary>
        KeepOverlapping,

        /// <summary>Put it on exactly one level, chosen by its bounding-box centre Z.</summary>
        ByCentroid,
    }

    /// <summary>One appended model in the federation, as offered by the per-level picker.
    /// <see cref="Key"/> is what the picker stores and must be unique across the document;
    /// <see cref="Index"/> is the position in <c>doc.Models</c> that it resolves back to.</summary>
    public sealed class ModelRef
    {
        public int    Index      { get; set; }
        /// <summary>Unique display key. Equals <see cref="DisplayName"/> unless two models share
        /// a name, in which case it is disambiguated — the picker must never show one row that
        /// silently means two different models.</summary>
        public string Key        { get; set; } = "";
        public string DisplayName { get; set; } = "";
        /// <summary>Source filename, shown dimmed on the picker row. May be empty.</summary>
        public string SourceFile { get; set; } = "";
    }

    /// <summary>A level: a name, the models assigned to it, and an optional elevation band used
    /// to trim those models and to clip its saved viewpoint.</summary>
    public sealed class LevelDef
    {
        public string Name { get; set; } = "";

        /// <summary>Keys of the assigned models (see <see cref="ModelRef.Key"/>). A model may be
        /// assigned to any number of levels — a core/shell model belongs to all of them.</summary>
        public ObservableCollection<string> Models { get; } = new ObservableCollection<string>();

        /// <summary>Band low edge, in the document's display units.</summary>
        public double Bottom { get; set; }
        /// <summary>Band high edge, in the document's display units.</summary>
        public double Top    { get; set; }

        /// <summary>When true, items in the assigned models that fall outside
        /// [<see cref="Bottom"/>, <see cref="Top"/>] are hidden before the export.</summary>
        public bool Trim { get; set; }

        /// <summary>UI-only: whether the row's band panel is expanded. Kept on the model so a
        /// step rebuild does not collapse every row the user opened.</summary>
        public bool Expanded { get; set; }

        public bool HasBand => Top > Bottom;
    }

    /// <summary>A geometry item plus its cached vertical extent and owning model, gathered once
    /// so per-level classification is a pure numeric compare.</summary>
    internal struct ItemZ
    {
        public ModelItem Item;
        public int       ModelIndex;
        public double    MinZ;
        public double    MaxZ;

        public double CentreZ => (MinZ + MaxZ) * 0.5;
    }

    /// <summary>What one level's export actually did, for the run log.</summary>
    internal sealed class LevelOutcome
    {
        public string Level    = "";
        public int    Models;
        public int    Hidden;
        public bool   Trimmed;
        public bool   Clipped;
        public string File     = "";
        public bool   Written;
        public string Failure  = "";
    }

    /// <summary>Levels discovered from the model, before the user edits them.</summary>
    internal sealed class DiscoveredLevel
    {
        public string Name      = "";
        public double Elevation;
    }

    internal static class LevelDefaults
    {
        /// <summary>Cap on the property scan that discovers level names, so opening the tool on a
        /// huge federation cannot freeze the UI thread. A full rescan is a button.</summary>
        public const int DiscoverScanCap = 40000;
    }

    internal static class Extensions
    {
        public static IEnumerable<T> OrEmpty<T>(this IEnumerable<T>? src) => src ?? new List<T>();
    }
}
