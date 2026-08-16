using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // Zone data model — "view templates for 3D space".
    //
    // A Zone describes a chunk of the building AND the documentation conventions
    // that go with it. It fills the gap between the three things Revit already
    // has, none of which carry the whole story:
    //
    //   view template → graphics, but no position/extent/scale/sheet location
    //   scope box     → an extent, but no view range/scale/naming/sheet position
    //   title block   → a paper size, but no relationship to a place
    //
    // Two axes with a sparse override matrix (see the plan's §2):
    //
    //   Building ─┬─ Level  (Z)     "L01", "L02", "Roof"
    //             └─ Area   (XY)    "Area 1", "East Wing"
    //   Cell = (Level, Area), stored ONLY where something genuinely differs.
    //
    // Persistence rules this file exists to enforce:
    //
    //   • Every type here is PUBLIC. XmlSerializer refuses to construct for a
    //     non-public root and the failure is silent inside the usual try/catch —
    //     that is what left theme/UI settings stuck on defaults once already.
    //   • NO ElementId is ever stored. Levels and scope boxes are keyed by NAME,
    //     because a settings payload that names an element inside one specific
    //     model has shipped as a bug four times in this repo, once destructively.
    //   • Mode fields are STRING tokens, not enums. XmlSerializer throws on an
    //     enum value it does not recognise, so a library written by a newer build
    //     would fail to load wholesale on an older one; an unknown string simply
    //     falls through to the documented default.
    // =========================================================================

    /// <summary>Extent-definition tokens for <see cref="ZoneArea.Definition"/>.</summary>
    public static class ZoneExtentMode
    {
        /// <summary>Adopt an existing scope box's extents. The default.</summary>
        public const string ScopeBox    = "ScopeBox";
        /// <summary>Solve from named grid bubbles plus a margin.</summary>
        public const string Grids       = "Grids";
        /// <summary>Union of a room cluster discovered from a link.</summary>
        public const string RoomCluster = "RoomCluster";
        /// <summary>Typed or picked coordinates.</summary>
        public const string Manual      = "Manual";
    }

    /// <summary>Anchor-mode tokens for <see cref="ZoneArea.AnchorMode"/>.</summary>
    /// <remarks>
    /// Deliberately independent of <see cref="ZoneExtentMode"/>. If the anchor is the
    /// extents centre, resizing an adopted scope box by a foot silently moves every sheet
    /// placement derived from that area — drawings correct yesterday shift with no error
    /// anywhere. A named grid intersection does not move when extents change, so it stays
    /// available whatever the extents came from.
    /// </remarks>
    public static class ZoneAnchorMode
    {
        public const string ExtentsCentre    = "ExtentsCentre";
        public const string GridIntersection = "GridIntersection";
        public const string Manual           = "Manual";
    }

    /// <summary>View-kind tokens for <see cref="ZoneViewRecipe.Kind"/>. Map onto <c>ViewFamily</c>.</summary>
    public static class ZoneViewKind
    {
        public const string FloorPlan   = "FloorPlan";
        public const string CeilingPlan = "CeilingPlan";
        public const string ThreeD      = "ThreeD";
        public const string Section     = "Section";
        public const string AreaPlan    = "AreaPlan";

        /// <summary>True for the kinds that carry a plan view range.</summary>
        public static bool IsPlan(string? kind)
            => kind == FloorPlan || kind == CeilingPlan || kind == AreaPlan;
    }

    /// <summary>Scale-mode tokens for <see cref="ZoneViewRecipe.ScaleMode"/>.</summary>
    public static class ZoneScaleMode
    {
        /// <summary>Use <see cref="ZoneViewRecipe.Scale"/> verbatim.</summary>
        public const string Fixed            = "Fixed";
        /// <summary>Solve the largest standard scale that fits the title block.</summary>
        public const string FitToTitleBlock  = "FitToTitleBlock";
    }

    /// <summary>How a group's views are arranged on one sheet.</summary>
    public static class ZoneComposition
    {
        /// <summary>
        /// True world-relative offsets at one shared scale. Matchlines meet exactly and
        /// views cannot overlap, because the areas do not overlap in the world. The default.
        /// </summary>
        public const string Continuous = "Continuous";
        /// <summary>
        /// Ordered by world position with a paper gap inserted. Breaks matchline continuity;
        /// exists for scope boxes that deliberately overlap for context.
        /// </summary>
        public const string Packed     = "Packed";
    }

    /// <summary>How a second view of the same area is produced for another sheet size.</summary>
    public static class ZoneDuplicateMode
    {
        /// <summary>Graphics stay in sync with the primary. The default.</summary>
        public const string AsDependent   = "AsDependent";
        public const string WithDetailing = "WithDetailing";
        public const string Plain         = "Plain";
    }

    /// <summary>Where a stored sheet placement came from.</summary>
    public static class ZonePlacementSource
    {
        public const string Solved   = "Solved";
        public const string Captured = "Captured";
    }

    /// <summary>
    /// Level-reference tokens for one plane of a <see cref="ZoneViewRange"/>. Anything that is
    /// not one of these four is treated as a level NAME.
    /// </summary>
    public static class ZoneLevelRef
    {
        public const string Current   = "Current";
        public const string Above     = "Above";
        public const string Below     = "Below";
        public const string Unlimited = "Unlimited";
    }

    // ── Buildings ────────────────────────────────────────────────────────────

    public sealed class ZoneBuilding
    {
        [XmlAttribute("id")]   public string Id   { get; set; } = "";
        [XmlAttribute("name")] public string Name { get; set; } = "";
        /// <summary>Short code for naming patterns (e.g. "A"). May be empty.</summary>
        [XmlAttribute("code")] public string Code { get; set; } = "";
        [XmlAttribute("sort")] public int    SortIndex { get; set; }
    }

    // ── Levels (the Z axis) ──────────────────────────────────────────────────

    public sealed class ZoneLevel
    {
        [XmlAttribute("id")]   public string Id   { get; set; } = "";
        [XmlAttribute("name")] public string Name { get; set; } = "";
        [XmlAttribute("code")] public string Code { get; set; } = "";
        [XmlAttribute("building")] public string BuildingId { get; set; } = "";

        /// <summary>
        /// Name of the host level this zone level maps to. A NAME, never an id — cross-document
        /// level identity is the name, with elevation only as a fallback, because a linked
        /// model's level is a different element in a different document.
        /// </summary>
        [XmlAttribute("hostLevel")] public string HostLevelName { get; set; } = "";

        /// <summary>Document key of the link this level was discovered from. Provenance only.</summary>
        [XmlAttribute("srcLink")]  public string SourceLinkKey   { get; set; } = "";
        [XmlAttribute("srcLevel")] public string SourceLevelName { get; set; } = "";

        /// <summary>World elevation in feet, recorded for the elevation fallback match and the UI.</summary>
        [XmlAttribute("elev")] public double ElevationFt { get; set; }

        /// <summary>Bottom of this level's 3D band, relative to the level (feet). Usually negative.</summary>
        [XmlAttribute("bandBase")] public double BandBaseOffsetFt { get; set; } = -2.0;
        /// <summary>Top of this level's 3D band, relative to the level (feet).</summary>
        [XmlAttribute("bandTop")]  public double BandTopOffsetFt  { get; set; } = 14.0;

        [XmlAttribute("sort")] public int SortIndex { get; set; }
    }

    // ── Areas (the XY axis) ──────────────────────────────────────────────────

    public sealed class ZoneArea
    {
        [XmlAttribute("id")]   public string Id   { get; set; } = "";
        [XmlAttribute("name")] public string Name { get; set; } = "";
        [XmlAttribute("code")] public string Code { get; set; } = "";
        [XmlAttribute("building")] public string BuildingId { get; set; } = "";

        /// <summary>One of <see cref="ZoneExtentMode"/>. Unknown values fall back to ScopeBox.</summary>
        [XmlAttribute("def")] public string Definition { get; set; } = ZoneExtentMode.ScopeBox;

        /// <summary>
        /// Scope box this area adopts or drives. The NAME is the key across sessions; the
        /// element itself is additionally stamped so the document stays authoritative.
        /// </summary>
        [XmlAttribute("box")] public string ScopeBoxName { get; set; } = "";

        // Grid definition (Definition = Grids)
        [XmlAttribute("gxMin")] public string GridXMin { get; set; } = "";
        [XmlAttribute("gxMax")] public string GridXMax { get; set; } = "";
        [XmlAttribute("gyMin")] public string GridYMin { get; set; } = "";
        [XmlAttribute("gyMax")] public string GridYMax { get; set; } = "";
        [XmlAttribute("gMargin")] public double GridMarginFt { get; set; } = 5.0;

        /// <summary>Resolved world extents (feet), cached from the last solve.</summary>
        [XmlAttribute("minX")] public double MinX { get; set; }
        [XmlAttribute("minY")] public double MinY { get; set; }
        [XmlAttribute("maxX")] public double MaxX { get; set; }
        [XmlAttribute("maxY")] public double MaxY { get; set; }
        /// <summary>False until extents have ever been solved — distinguishes "unset" from "zero".</summary>
        [XmlAttribute("hasExtents")] public bool HasExtents { get; set; }

        /// <summary>One of <see cref="ZoneAnchorMode"/>.</summary>
        [XmlAttribute("anchorMode")] public string AnchorMode { get; set; } = ZoneAnchorMode.ExtentsCentre;
        [XmlAttribute("anchorGX")] public string AnchorGridX { get; set; } = "";
        [XmlAttribute("anchorGY")] public string AnchorGridY { get; set; } = "";
        /// <summary>Resolved world anchor (feet). Meaningful once <see cref="HasAnchor"/> is true.</summary>
        [XmlAttribute("anchorX")] public double AnchorX { get; set; }
        [XmlAttribute("anchorY")] public double AnchorY { get; set; }
        [XmlAttribute("hasAnchor")] public bool HasAnchor { get; set; }

        /// <summary>Levels this area exists on. Empty means "every level of its building".</summary>
        [XmlArray("Levels"), XmlArrayItem("L")]
        public List<string> AppliesToLevelIds { get; set; } = new List<string>();

        [XmlAttribute("sort")] public int SortIndex { get; set; }

        public double WidthFt => MaxX - MinX;
        public double DepthFt => MaxY - MinY;
    }

    /// <summary>
    /// Sparse override for one (Area, Level) pair. Stored ONLY where something differs — a
    /// building with a uniform footprint carries none of these at all.
    /// </summary>
    public sealed class ZoneCell
    {
        [XmlAttribute("area")]  public string AreaId  { get; set; } = "";
        [XmlAttribute("level")] public string LevelId { get; set; } = "";

        /// <summary>True to exclude this pair entirely (the area does not exist on that level).</summary>
        [XmlAttribute("excluded")] public bool Excluded { get; set; }

        /// <summary>Scope box override for this pair. Empty = use the area's.</summary>
        [XmlAttribute("box")] public string ScopeBoxName { get; set; } = "";

        /// <summary>Extents override. Only honoured when <see cref="HasExtents"/> is true.</summary>
        [XmlAttribute("minX")] public double MinX { get; set; }
        [XmlAttribute("minY")] public double MinY { get; set; }
        [XmlAttribute("maxX")] public double MaxX { get; set; }
        [XmlAttribute("maxY")] public double MaxY { get; set; }
        [XmlAttribute("hasExtents")] public bool HasExtents { get; set; }
    }

    // ── View recipes ─────────────────────────────────────────────────────────

    /// <summary>One plane of a plan view range: a level reference plus an offset.</summary>
    public sealed class ZoneViewRangePlane
    {
        /// <summary>One of <see cref="ZoneLevelRef"/>, or a level NAME.</summary>
        [XmlAttribute("ref")]    public string LevelRef { get; set; } = ZoneLevelRef.Current;
        [XmlAttribute("offset")] public double OffsetFt { get; set; }

        public ZoneViewRangePlane() { }
        public ZoneViewRangePlane(string levelRef, double offsetFt)
        {
            LevelRef = levelRef;
            OffsetFt = offsetFt;
        }
    }

    /// <summary>
    /// A plan view range, mapping one-to-one onto Revit's <c>PlanViewRange</c> /
    /// <c>PlanViewPlane</c>. <c>UnderlayBottom</c> is deliberately omitted: it is part of the
    /// underlay, not the view range proper, and writing it would silently change a setting the
    /// user never asked this tool to touch.
    /// </summary>
    public sealed class ZoneViewRange
    {
        public ZoneViewRangePlane Top       { get; set; } = new ZoneViewRangePlane(ZoneLevelRef.Above,   0.0);
        public ZoneViewRangePlane CutPlane  { get; set; } = new ZoneViewRangePlane(ZoneLevelRef.Current, 4.0);
        public ZoneViewRangePlane Bottom    { get; set; } = new ZoneViewRangePlane(ZoneLevelRef.Current, 0.0);
        public ZoneViewRangePlane ViewDepth { get; set; } = new ZoneViewRangePlane(ZoneLevelRef.Current, 0.0);

        /// <summary>A conventional floor plan: cut at 4', top at the level above.</summary>
        public static ZoneViewRange FloorPlanDefault() => new ZoneViewRange();

        /// <summary>
        /// A conventional RCP: cut high (7'-6") and looking up. Revit inverts the view direction
        /// for a ceiling plan itself; the range still reads bottom-up here.
        /// </summary>
        public static ZoneViewRange CeilingPlanDefault() => new ZoneViewRange
        {
            Top       = new ZoneViewRangePlane(ZoneLevelRef.Above,   0.0),
            CutPlane  = new ZoneViewRangePlane(ZoneLevelRef.Current, 7.5),
            Bottom    = new ZoneViewRangePlane(ZoneLevelRef.Current, 0.0),
            ViewDepth = new ZoneViewRangePlane(ZoneLevelRef.Current, 0.0),
        };

        public ZoneViewRange Clone() => new ZoneViewRange
        {
            Top       = new ZoneViewRangePlane(Top.LevelRef,       Top.OffsetFt),
            CutPlane  = new ZoneViewRangePlane(CutPlane.LevelRef,  CutPlane.OffsetFt),
            Bottom    = new ZoneViewRangePlane(Bottom.LevelRef,    Bottom.OffsetFt),
            ViewDepth = new ZoneViewRangePlane(ViewDepth.LevelRef, ViewDepth.OffsetFt),
        };
    }

    /// <summary>
    /// What a zone produces for one view type: the family type and template to use, the scale,
    /// the view range, and how the view is named. This is the "view template for 3D space"
    /// payload — it REFERENCES a Revit view template for graphics rather than replacing it.
    /// </summary>
    public sealed class ZoneViewRecipe
    {
        [XmlAttribute("id")]   public string Id   { get; set; } = "";
        [XmlAttribute("name")] public string Name { get; set; } = "";
        /// <summary>One of <see cref="ZoneViewKind"/>.</summary>
        [XmlAttribute("kind")] public string Kind { get; set; } = ZoneViewKind.FloorPlan;

        /// <summary>Names, resolved to ids at run time — never persisted as ElementIds.</summary>
        [XmlAttribute("vft")]      public string ViewFamilyTypeName { get; set; } = "";
        [XmlAttribute("template")] public string ViewTemplateName   { get; set; } = "";

        /// <summary>One of <see cref="ZoneScaleMode"/>.</summary>
        [XmlAttribute("scaleMode")] public string ScaleMode { get; set; } = ZoneScaleMode.FitToTitleBlock;
        /// <summary>View scale denominator (<c>View.Scale</c>) when ScaleMode is Fixed.</summary>
        [XmlAttribute("scale")] public int Scale { get; set; } = 96;

        /// <summary>Plan kinds only. Null leaves the view's own range untouched.</summary>
        public ZoneViewRange? ViewRange { get; set; } = new ZoneViewRange();

        [XmlAttribute("discipline")] public string Discipline  { get; set; } = "";
        [XmlAttribute("detail")]     public string DetailLevel { get; set; } = "";
        [XmlAttribute("phase")]      public string PhaseName   { get; set; } = "";

        /// <summary>Annotation-crop gap in PAPER feet; converted with the view's scale on write.</summary>
        [XmlAttribute("annoPaper")] public double AnnotationCropPaperFt { get; set; }
        [XmlAttribute("annoOn")]    public bool   AnnotationCropEnabled { get; set; }

        /// <summary>3D only: take the section box Z from the level's band.</summary>
        [XmlAttribute("boxFromBand")] public bool SectionBoxFromBand { get; set; } = true;

        /// <summary>
        /// TokenInput pattern. Must resolve DISTINCTLY across the layouts selected in a run —
        /// a view can only sit on one sheet, so two sheet sizes need two views, and View.Name
        /// is unique with a setter that throws on a duplicate.
        /// </summary>
        [XmlAttribute("pattern")] public string NamePattern { get; set; } = "";

        /// <summary>One of <see cref="ZoneDuplicateMode"/>, used for the 2nd+ sheet size.</summary>
        [XmlAttribute("dupMode")] public string DuplicateMode { get; set; } = ZoneDuplicateMode.AsDependent;

        [XmlAttribute("sort")] public int SortIndex { get; set; }
    }

    // ── Sheet layouts and groups ─────────────────────────────────────────────

    /// <summary>
    /// One sheet's worth of areas. A single-area group is an ordinary sheet; a multi-area group
    /// is a composite sheet carrying several views of one level.
    /// </summary>
    public sealed class ZoneSheetGroup
    {
        [XmlAttribute("id")] public string Id { get; set; } = "";
        /// <summary>Feeds the {SheetSuffix} naming token. Empty when a level needs only one sheet.</summary>
        [XmlAttribute("suffix")] public string Suffix { get; set; } = "";

        [XmlArray("Areas"), XmlArrayItem("A")]
        public List<string> AreaIds { get; set; } = new List<string>();

        /// <summary>0 = solve the scale for the group as a whole.</summary>
        [XmlAttribute("scale")] public int ScaleOverride { get; set; }

        [XmlAttribute("sort")] public int SortIndex { get; set; }
    }

    /// <summary>
    /// How a building's areas map onto sheets FOR ONE SHEET SIZE. Keyed by title block type
    /// because the answer genuinely differs by size: two areas may share an A1 and need two
    /// separate A3s.
    /// </summary>
    public sealed class ZoneSheetLayout
    {
        [XmlAttribute("id")]   public string Id   { get; set; } = "";
        [XmlAttribute("name")] public string Name { get; set; } = "";
        [XmlAttribute("building")] public string BuildingId { get; set; } = "";

        /// <summary>The title block type this layout is for. A NAME, never an id.</summary>
        [XmlAttribute("tb")] public string TitleBlockTypeName { get; set; } = "";

        /// <summary>One of <see cref="ZoneComposition"/>.</summary>
        [XmlAttribute("composition")] public string Composition { get; set; } = ZoneComposition.Continuous;
        /// <summary>Paper-feet gap between views. Packed composition only.</summary>
        [XmlAttribute("gap")] public double GapPaperFt { get; set; } = 0.05;

        /// <summary>Margins from the title block edge to the usable drawing area, in paper feet.</summary>
        [XmlAttribute("mL")] public double MarginLeftFt   { get; set; } = 0.05;
        [XmlAttribute("mR")] public double MarginRightFt  { get; set; } = 0.05;
        [XmlAttribute("mT")] public double MarginTopFt    { get; set; } = 0.05;
        [XmlAttribute("mB")] public double MarginBottomFt { get; set; } = 0.05;

        /// <summary>Recorded from the title block type, to detect one that changed under us.</summary>
        [XmlAttribute("w")] public double SheetWidthFt  { get; set; }
        [XmlAttribute("h")] public double SheetHeightFt { get; set; }

        [XmlArray("Groups"), XmlArrayItem("G")]
        public List<ZoneSheetGroup> Groups { get; set; } = new List<ZoneSheetGroup>();

        /// <summary>Sheet number / name TokenInput patterns.</summary>
        [XmlAttribute("numPattern")]  public string SheetNumberPattern { get; set; } = "";
        [XmlAttribute("namePattern")] public string SheetNamePattern   { get; set; } = "";

        [XmlAttribute("sort")] public int SortIndex { get; set; }
    }

    // ── Sheet placements ─────────────────────────────────────────────────────

    /// <summary>
    /// Where one area lands on a sheet, in exact sheet coordinates. Keyed by
    /// (Area, TitleBlockType, Group).
    ///
    /// The group is part of the key because a position is only meaningful in its context: an
    /// area placed left-of-centre so a neighbour fits beside it would sit absurdly off-centre
    /// if that same record were reused for a solo sheet. An empty GroupId IS the solo record.
    /// </summary>
    public sealed class ZoneSheetPlacement
    {
        [XmlAttribute("area")]  public string AreaId             { get; set; } = "";
        [XmlAttribute("tb")]    public string TitleBlockTypeName { get; set; } = "";
        [XmlAttribute("group")] public string GroupId            { get; set; } = "";

        /// <summary>Recorded to detect a title block whose size changed under a stored placement.</summary>
        [XmlAttribute("sw")] public double SheetWidthFt  { get; set; }
        [XmlAttribute("sh")] public double SheetHeightFt { get; set; }

        /// <summary>The area's world anchor at the time this placement was made.</summary>
        [XmlAttribute("awX")] public double AnchorWorldX { get; set; }
        [XmlAttribute("awY")] public double AnchorWorldY { get; set; }

        /// <summary>The sheet coordinate that world point must occupy. The whole point of this record.</summary>
        [XmlAttribute("asX")] public double AnchorSheetX { get; set; }
        [XmlAttribute("asY")] public double AnchorSheetY { get; set; }

        /// <summary>The view scale this anchor pair was solved or captured at.</summary>
        [XmlAttribute("scale")] public int Scale { get; set; } = 96;

        /// <summary>One of <see cref="ZonePlacementSource"/>.</summary>
        [XmlAttribute("src")] public string Source { get; set; } = ZonePlacementSource.Solved;
        [XmlAttribute("fromSheet")] public string CapturedFromSheetNumber { get; set; } = "";
        [XmlAttribute("capturedUtc")] public long CapturedUtcTicks { get; set; }

        /// <summary>Identity key used to find this record. Group "" = solo.</summary>
        public static string KeyOf(string? areaId, string? titleBlockTypeName, string? groupId)
            => (areaId ?? "") + "::" + (titleBlockTypeName ?? "") + "::" + (groupId ?? "");

        public string Key => KeyOf(AreaId, TitleBlockTypeName, GroupId);
    }

    // ── The library root ─────────────────────────────────────────────────────

    /// <summary>
    /// Everything a project's zones consist of. Serialized into the .rvt via
    /// <c>ProjectLibraryStore</c>'s "Zones" section, never into %AppData%.
    /// </summary>
    [XmlRoot("ZoneLibrary")]
    public sealed class ZoneLibrary
    {
        public const int CurrentVersion = 1;

        [XmlAttribute("version")] public int SchemaVersion { get; set; } = CurrentVersion;

        [XmlArray("Buildings"),  XmlArrayItem("B")] public List<ZoneBuilding>       Buildings  { get; set; } = new List<ZoneBuilding>();
        [XmlArray("Levels"),     XmlArrayItem("L")] public List<ZoneLevel>          Levels     { get; set; } = new List<ZoneLevel>();
        [XmlArray("Areas"),      XmlArrayItem("A")] public List<ZoneArea>           Areas      { get; set; } = new List<ZoneArea>();
        [XmlArray("Cells"),      XmlArrayItem("C")] public List<ZoneCell>           Cells      { get; set; } = new List<ZoneCell>();
        [XmlArray("Recipes"),    XmlArrayItem("R")] public List<ZoneViewRecipe>     Recipes    { get; set; } = new List<ZoneViewRecipe>();
        [XmlArray("Layouts"),    XmlArrayItem("Y")] public List<ZoneSheetLayout>    Layouts    { get; set; } = new List<ZoneSheetLayout>();
        [XmlArray("Placements"), XmlArrayItem("P")] public List<ZoneSheetPlacement> Placements { get; set; } = new List<ZoneSheetPlacement>();

        public bool IsEmpty
            => Buildings.Count == 0 && Levels.Count == 0 && Areas.Count == 0 &&
               Recipes.Count == 0 && Layouts.Count == 0;

        // ── Lookups ──────────────────────────────────────────────────────────

        public ZoneBuilding?    Building(string? id) => Find(Buildings, b => b.Id, id);
        public ZoneLevel?       Level(string? id)    => Find(Levels,    l => l.Id, id);
        public ZoneArea?        Area(string? id)     => Find(Areas,     a => a.Id, id);
        public ZoneViewRecipe?  Recipe(string? id)   => Find(Recipes,   r => r.Id, id);
        public ZoneSheetLayout? Layout(string? id)   => Find(Layouts,   y => y.Id, id);

        private static T? Find<T>(List<T> list, Func<T, string> idOf, string? id) where T : class
        {
            if (list == null || string.IsNullOrEmpty(id)) return null;
            foreach (var item in list)
                if (item != null && string.Equals(idOf(item), id, StringComparison.Ordinal))
                    return item;
            return null;
        }

        /// <summary>The sparse override for one pair, or null when nothing differs there.</summary>
        public ZoneCell? Cell(string? areaId, string? levelId)
        {
            if (Cells == null || string.IsNullOrEmpty(areaId) || string.IsNullOrEmpty(levelId)) return null;
            foreach (var c in Cells)
                if (c != null &&
                    string.Equals(c.AreaId,  areaId,  StringComparison.Ordinal) &&
                    string.Equals(c.LevelId, levelId, StringComparison.Ordinal))
                    return c;
            return null;
        }

        /// <summary>
        /// True when this area is documented on this level. An area with an empty level list
        /// applies to every level of its building; an explicit cell can still exclude a pair.
        /// </summary>
        public bool AreaAppliesTo(ZoneArea? area, ZoneLevel? level)
        {
            if (area == null || level == null) return false;
            if (!string.IsNullOrEmpty(area.BuildingId) &&
                !string.IsNullOrEmpty(level.BuildingId) &&
                !string.Equals(area.BuildingId, level.BuildingId, StringComparison.Ordinal))
                return false;
            if (Cell(area.Id, level.Id)?.Excluded == true) return false;
            if (area.AppliesToLevelIds == null || area.AppliesToLevelIds.Count == 0) return true;
            return area.AppliesToLevelIds.Contains(level.Id);
        }

        /// <summary>The scope box governing one (Area, Level) pair — the cell override wins.</summary>
        public string ScopeBoxFor(ZoneArea? area, ZoneLevel? level)
        {
            var cell = Cell(area?.Id, level?.Id);
            if (cell != null && !string.IsNullOrEmpty(cell.ScopeBoxName)) return cell.ScopeBoxName;
            return area?.ScopeBoxName ?? "";
        }

        /// <summary>
        /// The world extents governing one (Area, Level) pair — the cell override wins.
        /// Returns false when nothing has been solved yet, so a caller never mistakes an
        /// unset extent for a zero-sized one.
        /// </summary>
        public bool ExtentsFor(ZoneArea? area, ZoneLevel? level,
                               out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = maxX = maxY = 0;
            var cell = Cell(area?.Id, level?.Id);
            if (cell != null && cell.HasExtents)
            {
                minX = cell.MinX; minY = cell.MinY; maxX = cell.MaxX; maxY = cell.MaxY;
                return true;
            }
            if (area == null || !area.HasExtents) return false;
            minX = area.MinX; minY = area.MinY; maxX = area.MaxX; maxY = area.MaxY;
            return true;
        }

        /// <summary>Stored placement for an area on a sheet size, in a group ("" = solo).</summary>
        public ZoneSheetPlacement? Placement(string? areaId, string? titleBlockTypeName, string? groupId)
        {
            if (Placements == null) return null;
            string key = ZoneSheetPlacement.KeyOf(areaId, titleBlockTypeName, groupId);
            foreach (var p in Placements)
                if (p != null && string.Equals(p.Key, key, StringComparison.Ordinal))
                    return p;
            return null;
        }

        /// <summary>Adds or replaces a placement, keyed on (Area, TitleBlockType, Group).</summary>
        public void SetPlacement(ZoneSheetPlacement placement)
        {
            if (placement == null) return;
            if (Placements == null) Placements = new List<ZoneSheetPlacement>();
            string key = placement.Key;
            for (int i = 0; i < Placements.Count; i++)
                if (Placements[i] != null && string.Equals(Placements[i].Key, key, StringComparison.Ordinal))
                {
                    Placements[i] = placement;
                    return;
                }
            Placements.Add(placement);
        }

        /// <summary>The group in a layout that carries this area, or null when it is solo.</summary>
        public ZoneSheetGroup? GroupFor(ZoneSheetLayout? layout, string? areaId)
        {
            if (layout?.Groups == null || string.IsNullOrEmpty(areaId)) return null;
            foreach (var g in layout.Groups)
                if (g?.AreaIds != null && g.AreaIds.Contains(areaId))
                    return g;
            return null;
        }
    }
}
