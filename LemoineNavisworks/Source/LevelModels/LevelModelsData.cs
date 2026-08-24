using System.Collections.Generic;
using System.Collections.ObjectModel;
using Autodesk.Navisworks.Api;

namespace LemoineNavisworks.LevelModels
{
    // =========================================================================
    // Plain data types for the Level Models tool. No Navisworks calls here — the
    // level list, the per-level model assignment, and a cached per-item vertical
    // extent so the trim never re-reads geometry.
    // =========================================================================

    /// <summary>How an element crossing a level's band edge is treated.</summary>
    public enum StraddleRule
    {
        /// <summary>Keep it on every level whose band its extent overlaps — a column crossing a
        /// floor line appears on both neighbours. THE DEFAULT: if any part of an element falls in
        /// the band, the element stays.</summary>
        KeepOverlapping,

        /// <summary>Put it on exactly one level, chosen by its bounding-box centre Z.</summary>
        ByCentroid,
    }

    /// <summary>One appended model in the federation, as offered by the per-level picker.
    /// <see cref="Key"/> is what the picker stores and must be unique across the document;
    /// <see cref="Index"/> is the position in <c>doc.Models</c> that it resolves back to.</summary>
    public sealed class ModelRef
    {
        public int    Index       { get; set; }
        /// <summary>Unique display key. Equals <see cref="DisplayName"/> unless two models share
        /// a name, in which case it is disambiguated — the picker must never show one row that
        /// silently means two different models.</summary>
        public string Key         { get; set; } = "";
        public string DisplayName { get; set; } = "";
        /// <summary>Source filename, shown dimmed on the picker row. May be empty.</summary>
        public string SourceFile  { get; set; } = "";
    }

    /// <summary>A level: a name, the models assigned to it, and an elevation band used to trim
    /// those models and to clip its saved viewpoint.
    ///
    /// <para>Bands are held in FEET regardless of the document's own units, because that is what
    /// the UI states and what the user types. Conversion to document units happens at the point
    /// geometry is compared, never in storage.</para></summary>
    public sealed class LevelDef
    {
        public string Name { get; set; } = "";

        /// <summary>Keys of the assigned models (see <see cref="ModelRef.Key"/>). A model may be
        /// assigned to any number of levels — a core/shell model belongs to all of them.</summary>
        public ObservableCollection<string> Models { get; } = new ObservableCollection<string>();

        /// <summary>Band low edge, in FEET.</summary>
        public double Bottom { get; set; }
        /// <summary>Band high edge, in FEET.</summary>
        public double Top    { get; set; }

        /// <summary>Set once the user renames the level, edits its band, or changes its models.
        /// A rescan refreshes only levels where this is false, so hand-tuning survives.</summary>
        public bool UserEdited { get; set; }

        /// <summary>UI-only: whether the row's band panel is expanded. Kept on the model so a
        /// step rebuild does not collapse every row the user opened.</summary>
        public bool Expanded { get; set; }

        /// <summary>A band only trims when it has real height. Trim itself is always on — there
        /// is no per-level toggle any more.</summary>
        public bool HasBand => Top > Bottom;
    }

    /// <summary>A geometry item plus its cached vertical extent and owning model, gathered once
    /// so per-level classification is a pure numeric compare. MinZ/MaxZ are in FEET.</summary>
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
        /// <summary>A viewpoint was saved for this level.</summary>
        public bool   Viewpoint;
        /// <summary>That viewpoint is actually CLIPPED to the band. Separate from
        /// <see cref="Viewpoint"/> on purpose — inferring one from the other reported clips that
        /// never happened.</summary>
        public bool   Clipped;
        public string File     = "";
        public bool   Written;
        public string Failure  = "";
    }

    /// <summary>A level as found in a model's tree, before the user edits it. Elevation in FEET.</summary>
    internal sealed class DiscoveredLevel
    {
        public string Name      = "";
        public double Elevation;
        /// <summary>Top of this level's own geometry, used to seed the band when the next level
        /// up is unknown (the topmost row).</summary>
        public double Top;
    }

    internal static class Extensions
    {
        public static IEnumerable<T> OrEmpty<T>(this IEnumerable<T>? src) => src ?? new List<T>();
    }
}
