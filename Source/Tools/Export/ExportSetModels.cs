using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;
using Autodesk.Revit.DB;

namespace LemoineTools.Tools.BulkExport
{
    /// <summary>
    /// How PDF output is granulated across the run's sets. Replaces the old
    /// <c>CombinePdf</c> bool, which could not express "one file per set" and was silently
    /// ignored whenever a print set was checked.
    /// </summary>
    public enum PdfGranularity
    {
        /// <summary>One PDF per sheet/view.</summary>
        PerSheet,
        /// <summary>One combined PDF per set.</summary>
        PerSet,
        /// <summary>One combined PDF over every enabled set, concatenated in set order.</summary>
        SingleFile,
    }

    /// <summary>
    /// One member of an <see cref="ExportSet"/>. Carries both the in-session element id and the
    /// durable <see cref="UniqueId"/>: the id is what the run resolves against, the UniqueId is
    /// what survives the model being copied or eTransmitted.
    /// </summary>
    public sealed class ExportSetMember
    {
        /// <summary>ElementId.Value of the sheet or view.</summary>
        [XmlAttribute("i")] public long IdValue { get; set; }

        /// <summary>Element.UniqueId — the persistence key, used when the id no longer resolves.</summary>
        [XmlAttribute("u")] public string UniqueId { get; set; } = "";

        /// <summary>Cached display label ("A101 — Ground Floor"), so a deleted member can still be named.</summary>
        [XmlAttribute("l")] public string Label { get; set; } = "";

        /// <summary>True when this member is a sheet rather than a view.</summary>
        [XmlAttribute("s")] public bool IsSheet { get; set; }

        /// <summary>
        /// Project Browser display position, refreshed from the captured tree each session.
        /// Not persisted — the browser can be reorganised between sessions, so a stale rank
        /// would silently order the export by a structure that no longer exists.
        /// </summary>
        [XmlIgnore] public int BrowserRank { get; set; }
    }

    /// <summary>
    /// A named, ordered group of sheets/views exported together. The list order IS the export
    /// order — for a combined PDF it is the page order.
    /// </summary>
    public sealed class ExportSet
    {
        [XmlAttribute("id")]      public string Id        { get; set; } = Guid.NewGuid().ToString("N");
        [XmlAttribute("name")]    public string Name      { get; set; } = "";
        [XmlAttribute("enabled")] public bool   Enabled   { get; set; } = true;

        /// <summary>
        /// Data colour (hex) identifying this set in the Step 1 target bar and on every tree-row
        /// badge. A data colour, not chrome — stored as hex like the legend/filter tools, and
        /// rendered through <c>BrushHelper.BrushFromHex</c>.
        /// </summary>
        [XmlAttribute("accent")]  public string AccentHex { get; set; } = "";

        [XmlArray("Members"), XmlArrayItem("M")]
        public List<ExportSetMember> Members { get; set; } = new List<ExportSetMember>();

        // ── Overrides — null means "inherit the tool's global setting" ────────
        [XmlAttribute("pattern")]   public string? PatternOverride   { get; set; }
        [XmlAttribute("subfolder")] public string? SubfolderOverride { get; set; }

        // bool? does not round-trip as an XmlAttribute, so the format overrides are elements.
        public bool? PdfOverride { get; set; }
        public bool? DwgOverride { get; set; }
        public bool? NwcOverride { get; set; }
        public bool? IfcOverride { get; set; }

        /// <summary>The default accent ramp, assigned round-robin as sets are created.</summary>
        public static readonly string[] AccentRamp =
        {
            "#4f8fc4", "#c98a3e", "#5aa469", "#a568b8",
            "#c05f5f", "#3fa6a6", "#8a8ac4", "#b0873f",
        };

        public static string AccentFor(int index)
            => AccentRamp[((index % AccentRamp.Length) + AccentRamp.Length) % AccentRamp.Length];

        public ExportSet Clone() => new ExportSet
        {
            Id                = Id,
            Name              = Name,
            Enabled           = Enabled,
            AccentHex         = AccentHex,
            Members           = Members.Select(m => new ExportSetMember
                                {
                                    IdValue     = m.IdValue,
                                    UniqueId    = m.UniqueId,
                                    Label       = m.Label,
                                    IsSheet     = m.IsSheet,
                                    BrowserRank = m.BrowserRank,
                                }).ToList(),
            PatternOverride   = PatternOverride,
            SubfolderOverride = SubfolderOverride,
            PdfOverride       = PdfOverride,
            DwgOverride       = DwgOverride,
            NwcOverride       = NwcOverride,
            IfcOverride       = IfcOverride,
        };
    }

    /// <summary>
    /// The serialized root stored in the document's Extensible Storage (see
    /// <see cref="ExportSetStore"/>). Public because <c>XmlSerializer</c> refuses non-public
    /// types — an internal root throws at serializer construction and, since that call sits in a
    /// try/catch, every save and load then fails silently.
    /// </summary>
    [XmlRoot("ExportSetLayout")]
    public sealed class ExportSetLayout
    {
        /// <summary>Format version, so a future change can migrate rather than throw.</summary>
        [XmlAttribute("version")] public int Version { get; set; } = CurrentVersion;

        public const int CurrentVersion = 1;

        /// <summary>
        /// Persisted as a string rather than the enum: an unknown value written by a future
        /// build must degrade to a sensible default, and <c>XmlSerializer</c> throws on an
        /// unrecognised enum member instead.
        /// </summary>
        [XmlAttribute("granularity")] public string Granularity { get; set; } = nameof(PdfGranularity.PerSet);

        [XmlArray("Sets"), XmlArrayItem("Set")]
        public List<ExportSet> Sets { get; set; } = new List<ExportSet>();

        [XmlIgnore]
        public PdfGranularity GranularityValue
        {
            get => ParseGranularity(Granularity);
            set => Granularity = value.ToString();
        }

        /// <summary>Parses a persisted granularity token, falling back to <c>PerSet</c>.</summary>
        public static PdfGranularity ParseGranularity(string? token)
            => Enum.TryParse<PdfGranularity>(token, ignoreCase: true, out var g) ? g : PdfGranularity.PerSet;
    }

    /// <summary>
    /// One file the run intends to write, resolved before any export happens. The same list
    /// drives the already-exists pre-flight scan, the review summary, and the run itself — so
    /// what the user is shown is what executes, rather than a re-derivation that can drift.
    /// </summary>
    public sealed class PlannedOutput
    {
        public string          Format    { get; set; } = "";   // "PDF" | "DWG" | "NWC" | "IFC"
        public string          Directory { get; set; } = "";
        public string          BaseName  { get; set; } = "";   // no extension
        public string          SetName   { get; set; } = "";
        public List<ElementId> MemberIds { get; set; } = new List<ElementId>();

        /// <summary>Set by the pre-flight disk scan — a file at this path already exists.</summary>
        public bool AlreadyExists { get; set; }

        public int ItemCount => MemberIds.Count;
    }

    /// <summary>A ViewSheetSet captured from the document (main thread) — name + members.</summary>
    public sealed class PrintSetInfo
    {
        public ElementId       Id        { get; set; } = ElementId.InvalidElementId;
        public string          Name      { get; set; } = "";
        public List<ElementId> MemberIds { get; set; } = new List<ElementId>();
    }

    /// <summary>
    /// One print set chosen as an export group. Superseded by <see cref="ExportSetSpec"/> — kept
    /// only until the handler's two export paths are collapsed into one, so this commit still
    /// builds.
    /// </summary>
    public sealed class PrintSetExportSpec
    {
        public string          Name            { get; set; } = "";
        public List<ElementId> MemberIds       { get; set; } = new List<ElementId>();
        public string?         PatternOverride { get; set; }
        public bool?           PdfOverride     { get; set; }
        public bool?           DwgOverride     { get; set; }
    }

    /// <summary>
    /// One set handed to the export handler for a run: name, ordered members, and the resolved
    /// overrides. Built from an <see cref="ExportSet"/> by the ViewModel — the handler never sees
    /// the editable model, only the flattened run payload.
    /// </summary>
    public sealed class ExportSetSpec
    {
        public string          Name      { get; set; } = "";
        public List<ElementId> MemberIds { get; set; } = new List<ElementId>();
        public string?         PatternOverride   { get; set; }
        public string?         SubfolderOverride { get; set; }
        public bool?           PdfOverride { get; set; }
        public bool?           DwgOverride { get; set; }
        public bool?           NwcOverride { get; set; }
        public bool?           IfcOverride { get; set; }
    }
}
