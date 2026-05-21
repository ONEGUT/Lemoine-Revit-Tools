using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace LemoineTools.Tools.Testing.CoordSet
{
    // ── Phase tracking ────────────────────────────────────────────────────────
    /// <summary>
    /// Identifies the last successfully completed phase of a CoordSet run,
    /// allowing the workflow to resume from a checkpoint after a partial failure.
    /// </summary>
    public enum CoordSetRunPhase { None, A, B, C, D, E, F, G }

    // ── Legend data model ─────────────────────────────────────────────────────
    /// <summary>
    /// Defines a single entry within a legend group, binding a filter rule
    /// to a visual shape used when rendering the coordination legend.
    /// </summary>
    public class LegendEntryConfig
    {
        /// <summary>
        /// The identifier of the filter rule that this legend entry represents.
        /// </summary>
        [XmlAttribute] public string FilterRuleId { get; set; } = "";

        /// <summary>
        /// The shape used to render this entry in the legend.
        /// Accepted values are <c>"Rect"</c> (rectangle) or <c>"Circle"</c>.
        /// </summary>
        [XmlAttribute] public string Shape        { get; set; } = "Rect"; // "Rect" | "Circle"
    }

    /// <summary>
    /// Represents a named group of legend entries that are displayed together
    /// in the coordination set legend.
    /// </summary>
    public class LegendGroupConfig
    {
        /// <summary>
        /// The unique identifier for this legend group.
        /// </summary>
        [XmlAttribute] public string Id    { get; set; } = "";

        /// <summary>
        /// The display label shown as the group heading in the legend.
        /// </summary>
        [XmlAttribute] public string Label { get; set; } = "New Group";

        /// <summary>
        /// The list of individual legend entries belonging to this group.
        /// </summary>
        [XmlArray("Entries")]
        [XmlArrayItem("Entry")]
        public List<LegendEntryConfig> Entries { get; set; } = new List<LegendEntryConfig>();
    }

    // ── Grid bubble settings ──────────────────────────────────────────────────
    /// <summary>
    /// Controls which ends of grid lines display a bubble annotation
    /// on the generated coordination sheets.
    /// </summary>
    public class GridBubbleSettings
    {
        /// <summary>Gets or sets whether grid bubbles are shown at the top end of vertical grids.</summary>
        [XmlAttribute] public bool ShowTop    { get; set; } = true;

        /// <summary>Gets or sets whether grid bubbles are shown at the bottom end of vertical grids.</summary>
        [XmlAttribute] public bool ShowBottom { get; set; } = true;

        /// <summary>Gets or sets whether grid bubbles are shown at the left end of horizontal grids.</summary>
        [XmlAttribute] public bool ShowLeft   { get; set; } = true;

        /// <summary>Gets or sets whether grid bubbles are shown at the right end of horizontal grids.</summary>
        [XmlAttribute] public bool ShowRight  { get; set; } = true;
    }

    // ── Main settings singleton ───────────────────────────────────────────────
    /// <summary>
    /// Persistent, XML-serialized settings for the CoordSet tool.
    /// Exposes a lazy singleton via <see cref="Instance"/> that is loaded from
    /// <c>%AppData%\LemoineTools\LemoineCoordSetSettings.xml</c> on first access
    /// and created with defaults when no file exists.
    /// </summary>
    [XmlRoot("LemoineCoordSet")]
    public sealed class CoordSetSettings
    {
        private static CoordSetSettings? _instance;

        /// <summary>
        /// Gets the shared <see cref="CoordSetSettings"/> instance, loading it
        /// from disk the first time it is accessed.
        /// </summary>
        public static CoordSetSettings Instance => _instance ?? (_instance = Load());

        /// <summary>
        /// Initializes a new <see cref="CoordSetSettings"/> with default values.
        /// Required by <see cref="System.Xml.Serialization.XmlSerializer"/>.
        /// </summary>
        public CoordSetSettings() { }

        // Drawing type flags
        /// <summary>Gets or sets whether a Coordination drawing set should be created.</summary>
        [XmlAttribute] public bool CreateCoordination { get; set; } = true;

        /// <summary>Gets or sets whether a Mechanical drawing set should be created.</summary>
        [XmlAttribute] public bool CreateMechanical   { get; set; } = true;

        /// <summary>Gets or sets whether a Plumbing drawing set should be created.</summary>
        [XmlAttribute] public bool CreatePlumbing     { get; set; } = true;

        /// <summary>Gets or sets whether a Fire Protection drawing set should be created.</summary>
        [XmlAttribute] public bool CreateFire         { get; set; } = true;

        /// <summary>Gets or sets whether an Electrical drawing set should be created.</summary>
        [XmlAttribute] public bool CreateElectrical   { get; set; } = true;

        /// <summary>Gets or sets whether an Other discipline drawing set should be created.</summary>
        [XmlAttribute] public bool CreateOther        { get; set; } = false;

        /// <summary>Gets or sets whether a Reflected Ceiling Plan (RCP) drawing set should be created.</summary>
        [XmlAttribute] public bool CreateRCP          { get; set; } = false;

        // Selected trades (persisted for restore on reopen)
        /// <summary>
        /// The set of trade identifiers the user last selected, persisted so
        /// the UI can restore the selection when the tool is reopened.
        /// </summary>
        [XmlArray("SelectedTrades")]
        [XmlArrayItem("Trade")]
        public List<string> SelectedTradeIds { get; set; } = new List<string>();

        // Filter visibility
        /// <summary>
        /// When <see langword="true"/>, trades that do not match the current filter
        /// criteria are hidden in the selection list.
        /// </summary>
        [XmlAttribute] public bool HideNonMatchingTrades { get; set; } = true;

        // Sheet layout
        /// <summary>The Revit family name of the title block to place on generated sheets.</summary>
        [XmlAttribute] public string TitleBlockFamilyName { get; set; } = "";

        /// <summary>The default sheet number prefix applied to all generated sheets (e.g. <c>"C-"</c>).</summary>
        [XmlAttribute] public string SheetNumberPrefix    { get; set; } = "C-";

        /// <summary>The sheet number assigned to the cover sheet (e.g. <c>"C-000"</c>).</summary>
        [XmlAttribute] public string CoverSheetNumber     { get; set; } = "C-000";

        /// <summary>The name/title displayed on the cover sheet.</summary>
        [XmlAttribute] public string CoverSheetName       { get; set; } = "Cover Sheet";

        // Per-discipline sheet prefixes
        /// <summary>
        /// Per-discipline sheet number prefixes used when generating sheets.
        /// Each entry maps a discipline name to its prefix string (e.g. Mechanical → <c>"M-"</c>).
        /// </summary>
        [XmlArray("DisciplinePrefixes")]
        [XmlArrayItem("Prefix")]
        public List<DisciplinePrefixEntry> DisciplinePrefixes { get; set; } = new List<DisciplinePrefixEntry>
        {
            new DisciplinePrefixEntry { Discipline = "Coordination", Prefix = "C-" },
            new DisciplinePrefixEntry { Discipline = "Mechanical",   Prefix = "M-" },
            new DisciplinePrefixEntry { Discipline = "Plumbing",     Prefix = "P-" },
            new DisciplinePrefixEntry { Discipline = "Fire",         Prefix = "FP-" },
            new DisciplinePrefixEntry { Discipline = "Electrical",   Prefix = "E-" },
            new DisciplinePrefixEntry { Discipline = "Other",        Prefix = "O-" },
            new DisciplinePrefixEntry { Discipline = "RCP",          Prefix = "RC-" },
        };

        // Cover sheet project info
        /// <summary>The project name printed on the cover sheet.</summary>
        [XmlAttribute] public string ProjectName   { get; set; } = "";

        /// <summary>The project number printed on the cover sheet.</summary>
        [XmlAttribute] public string ProjectNumber { get; set; } = "";

        /// <summary>The project date string printed on the cover sheet.</summary>
        [XmlAttribute] public string ProjectDate   { get; set; } = "";

        // Cover sheet toggle
        /// <summary>
        /// When <see langword="true"/>, a cover sheet is generated as part of the coordination set.
        /// </summary>
        [XmlAttribute] public bool CreateCoverSheet { get; set; } = false;

        // Legend groups
        /// <summary>
        /// The list of named legend groups, each containing one or more
        /// <see cref="LegendEntryConfig"/> items, used to build the coordination legend.
        /// </summary>
        [XmlArray("LegendGroups")]
        [XmlArrayItem("Group")]
        public List<LegendGroupConfig> LegendGroups { get; set; } = new List<LegendGroupConfig>();

        // Grid bubble settings
        /// <summary>
        /// Controls which sides of grid lines display annotation bubbles on
        /// the generated coordination sheets.
        /// </summary>
        public GridBubbleSettings GridBubbles { get; set; } = new GridBubbleSettings();

        // Resumable phase state
        /// <summary>
        /// The last phase of the run workflow that completed successfully.
        /// Used to resume a partially completed run without repeating earlier phases.
        /// </summary>
        [XmlAttribute] public CoordSetRunPhase LastCompletedPhase { get; set; } = CoordSetRunPhase.None;

        /// <summary>
        /// Key/value pairs produced by earlier run phases (e.g. element IDs, sheet IDs)
        /// that later phases need in order to resume a partial run.
        /// </summary>
        [XmlArray("PhaseArtifacts")]
        [XmlArrayItem("Artifact")]
        public List<PhaseArtifactEntry> PhaseArtifacts { get; set; } = new List<PhaseArtifactEntry>();

        // ── Persistence ───────────────────────────────────────────────────────
        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "LemoineCoordSetSettings.xml");
            }
        }

        /// <summary>
        /// Serializes the current settings to
        /// <c>%AppData%\LemoineTools\LemoineCoordSetSettings.xml</c>.
        /// Failures are silently swallowed to avoid interrupting the Revit workflow.
        /// </summary>
        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(CoordSetSettings));
                using (var w = new StreamWriter(FilePath)) xs.Serialize(w, this);
            }
            catch { }
        }

        private static CoordSetSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var xs = new XmlSerializer(typeof(CoordSetSettings));
                    using (var r = new StreamReader(FilePath))
                        return (CoordSetSettings)xs.Deserialize(r)!;
                }
            }
            catch { }
            var fresh = new CoordSetSettings();
            fresh.Save();
            return fresh;
        }

        /// <summary>
        /// Clears the resumable run state by resetting <see cref="LastCompletedPhase"/>
        /// to <see cref="CoordSetRunPhase.None"/> and removing all phase artifacts,
        /// then persists the change to disk.
        /// </summary>
        public void ResetPhase()
        {
            LastCompletedPhase = CoordSetRunPhase.None;
            PhaseArtifacts.Clear();
            Save();
        }

        /// <summary>
        /// Returns the value of a phase artifact by key, or <c>-1</c> if no
        /// artifact with that key has been stored.
        /// </summary>
        /// <param name="key">The artifact key to look up.</param>
        /// <returns>The stored <see cref="long"/> value, or <c>-1</c> if not found.</returns>
        public long GetArtifact(string key)
        {
            foreach (var e in PhaseArtifacts)
                if (e.Key == key) return e.Value;
            return -1L;
        }

        /// <summary>
        /// Stores or updates a phase artifact value by key.  If an entry with the
        /// given key already exists its value is updated in place; otherwise a new
        /// entry is appended to <see cref="PhaseArtifacts"/>.
        /// </summary>
        /// <param name="key">The artifact key to store.</param>
        /// <param name="value">The value to associate with <paramref name="key"/>.</param>
        public void SetArtifact(string key, long value)
        {
            foreach (var e in PhaseArtifacts)
            {
                if (e.Key == key) { e.Value = value; return; }
            }
            PhaseArtifacts.Add(new PhaseArtifactEntry { Key = key, Value = value });
        }
    }

    /// <summary>
    /// Maps a discipline name to its sheet number prefix for use when
    /// generating coordination sheets.
    /// </summary>
    public class DisciplinePrefixEntry
    {
        /// <summary>The discipline name (e.g. <c>"Mechanical"</c>, <c>"Plumbing"</c>).</summary>
        [XmlAttribute] public string Discipline { get; set; } = "";

        /// <summary>The sheet number prefix applied to sheets for this discipline (e.g. <c>"M-"</c>).</summary>
        [XmlAttribute] public string Prefix     { get; set; } = "";
    }

    /// <summary>
    /// A serializable key/value pair used to persist intermediate data produced
    /// by one phase of a CoordSet run so that subsequent phases can reference it.
    /// </summary>
    public class PhaseArtifactEntry
    {
        /// <summary>The unique key identifying this artifact (e.g. a sheet ElementId).</summary>
        [XmlAttribute] public string Key   { get; set; } = "";

        /// <summary>The artifact value, typically a Revit ElementId cast to <see cref="long"/>.</summary>
        [XmlAttribute] public long   Value { get; set; } = -1L;
    }
}
