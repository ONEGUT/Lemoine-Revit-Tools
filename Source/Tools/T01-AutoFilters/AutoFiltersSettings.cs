using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine.Templates;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.AutoFilters
{
    // =========================================================================
    // Domain model — Trade → Category → Rule  (V2 schema)
    //
    // Replaces the old flat Disciplines / CategoryMap / ColorMap lists.
    // Existing LemoineAutoFilters.xml (V1) is not migrated — start fresh.
    // =========================================================================

    /// <summary>
    /// Top-level trade grouping (e.g. Mechanical Duct, Plumbing, Electrical).
    /// Each trade owns a list of category configs that define which Revit categories
    /// and filter rules belong to it.
    /// </summary>
    public class FilterTradeConfig
    {
        /// <summary>Short identifier used as a key in filter names (e.g. "MD", "EL").</summary>
        [XmlAttribute] public string Id    { get; set; } = "";
        /// <summary>Human-readable display name shown in the UI (e.g. "Mechanical Duct").</summary>
        [XmlAttribute] public string Label { get; set; } = "";
        /// <summary>Hex color representing the trade in the legend and default rule overrides.</summary>
        [XmlAttribute] public string Color { get; set; } = "#888888";

        /// <summary>
        /// When <see langword="true"/>, this trade's filters are created and maintained by
        /// another tool (e.g. the Ceiling Heatmap) using a non-keyword match. The generic
        /// "Create Filters" engine skips these trades — it cannot regenerate their filters —
        /// but they still appear in the rules list and group correctly in the filter pickers.
        /// </summary>
        [XmlAttribute] public bool ExternallyManaged { get; set; } = false;

        /// <summary>
        /// V3+: Rules live directly on the trade (no category wrapper).
        /// </summary>
        [XmlArray("Rules")]
        [XmlArrayItem("Rule")]
        public List<FilterRuleConfig> Rules { get; set; } = new List<FilterRuleConfig>();

        /// <summary>
        /// V2 migration shim: Categories are no longer used in V3+.
        /// Kept for XML deserialisation of old files only — do not reference in new code.
        /// </summary>
        [XmlArray("Categories")]
        [XmlArrayItem("Category")]
        public List<FilterCategoryConfig> Categories { get; set; } = new List<FilterCategoryConfig>();

        /// <summary>
        /// Creates a new <see cref="FilterTradeConfig"/> with a unique Id,
        /// a default label, and a placeholder color.
        /// </summary>
        /// <returns>A blank <see cref="FilterTradeConfig"/> ready for user editing.</returns>
        public static FilterTradeConfig NewBlank() => new FilterTradeConfig
        {
            Id    = NewTradeId(),
            Label = "New Trade",
            Color = "#569cd6",
        };

        /// <summary>
        /// Generates a short, collision-resistant trade Id (e.g. "T3F9A1").
        /// GUID-derived rather than time-derived: the Discover tool commits many trades
        /// in one tight loop, and the previous <c>Ticks.Substring(11,3)</c> scheme produced
        /// identical Ids within the same millisecond. Because the rules editor identifies
        /// the open trade solely by Id, duplicate Ids made several trades open as one.
        /// </summary>
        internal static string NewTradeId()
            => "T" + Guid.NewGuid().ToString("N").Substring(0, 5).ToUpperInvariant();
    }

    /// <summary>
    /// A category within a trade that maps one or more Revit BuiltInCategory strings
    /// to a set of filter rules and optional sub-categories.
    /// </summary>
    public class FilterCategoryConfig
    {
        /// <summary>Short unique identifier for this category (e.g. "MD-DUCT").</summary>
        [XmlAttribute] public string Id        { get; set; } = "";
        /// <summary>Human-readable display name shown in the UI (e.g. "Ductwork").</summary>
        [XmlAttribute] public string Label     { get; set; } = "";
        /// <summary>
        /// The Revit element parameter used as the filter criterion (e.g. "System Classification").
        /// </summary>
        [XmlAttribute] public string Parameter { get; set; } = "System Classification";

        // Multiple Revit BuiltInCategory strings — replaces the old single BuiltInCategory attribute.
        /// <summary>
        /// One or more OST_* Revit BuiltInCategory strings that this category targets.
        /// Replaces the old single <c>BuiltInCategory</c> attribute from the V1 schema.
        /// </summary>
        [XmlArray("BuiltInCategories")]
        [XmlArrayItem("Category")]
        public List<string> BuiltInCategories { get; set; } = new List<string>();

        // Backward-compat shim: old XML with a single BuiltInCategory="" attribute populates the list.
        /// <summary>
        /// Backward-compatibility shim for V1 XML that stored a single <c>BuiltInCategory</c>
        /// attribute. On deserialization the value is appended to <see cref="BuiltInCategories"/>;
        /// this property always returns <see langword="null"/> so it is never written on save.
        /// </summary>
        [XmlAttribute("BuiltInCategory")]
        public string? BuiltInCategoryLegacy
        {
            get => null; // never written
            set
            {
                if (!string.IsNullOrEmpty(value) && !BuiltInCategories.Contains(value))
                    BuiltInCategories.Add(value);
            }
        }

        /// <summary>
        /// Single-value accessor for the UI. Gets/sets the first entry in BuiltInCategories.
        /// Marked XmlIgnore so it does not conflict with the legacy BuiltInCategory attribute.
        /// </summary>
        [XmlIgnore]
        public string BuiltInCategory
        {
            get => BuiltInCategories.Count > 0 ? BuiltInCategories[0] : "";
            set
            {
                BuiltInCategories.Clear();
                if (!string.IsNullOrEmpty(value))
                    BuiltInCategories.Add(value);
            }
        }

        /// <summary>Optional nested sub-categories, allowing a two-level category hierarchy.</summary>
        [XmlArray("SubCategories")]
        [XmlArrayItem("SubCategory")]
        public List<FilterCategoryConfig> SubCategories { get; set; } = new List<FilterCategoryConfig>();

        /// <summary>Ordered list of filter rules that belong to this category.</summary>
        [XmlArray("Rules")]
        [XmlArrayItem("Rule")]
        public List<FilterRuleConfig> Rules { get; set; } = new List<FilterRuleConfig>();

        /// <summary>
        /// Creates a new <see cref="FilterCategoryConfig"/> with a random short Id,
        /// a default label, and "System Classification" as the filter parameter.
        /// </summary>
        /// <returns>A blank <see cref="FilterCategoryConfig"/> ready for user editing.</returns>
        public static FilterCategoryConfig NewBlank() => new FilterCategoryConfig
        {
            Id        = Guid.NewGuid().ToString("N").Substring(0, 6),
            Label     = "New Category",
            Parameter = "System Classification",
        };
    }

    /// <summary>
    /// A single filter rule that defines the graphic overrides and keyword matching
    /// criteria applied to Revit elements within a <see cref="FilterTradeConfig"/> (V3+).
    /// In V3 the rule owns its own category and parameter list directly;
    /// the <see cref="FilterCategoryConfig"/> wrapper has been removed.
    /// </summary>
    public class FilterRuleConfig
    {
        /// <summary>Unique identifier for this rule (8-character hex string).</summary>
        [XmlAttribute] public string Id           { get; set; } = "";
        /// <summary>When <see langword="false"/> the rule is skipped during filter application.</summary>
        [XmlAttribute] public bool   Enabled      { get; set; } = true;
        /// <summary>Human-readable display name for this rule (e.g. "Supply Air").</summary>
        [XmlAttribute] public string Name         { get; set; } = "New Rule";
        /// <summary>
        /// Keyword matching mode: <c>"contains"</c>, <c>"equals"</c>, or <c>"all"</c>
        /// (match every element regardless of parameter value).
        /// </summary>
        [XmlAttribute] public string MatchType    { get; set; } = "contains"; // "contains" | "equals" | "all"

        // ── V3 fields: moved up from FilterCategoryConfig ─────────────────────

        /// <summary>
        /// The Revit element parameter used as the filter criterion (e.g. "System Type").
        /// Moved from <see cref="FilterCategoryConfig.Parameter"/> in V3.
        /// </summary>
        [XmlAttribute] public string Parameter { get; set; } = "System Type";

        /// <summary>
        /// One or more OST_* Revit BuiltInCategory strings that this rule targets.
        /// Moved from <see cref="FilterCategoryConfig.BuiltInCategories"/> in V3.
        /// </summary>
        [XmlArray("RuleCategories")]
        [XmlArrayItem("Category")]
        public List<string> BuiltInCategories { get; set; } = new List<string>();
        /// <summary>Hex color applied to cut-plane graphic overrides (e.g. "#005EBD").</summary>
        [XmlAttribute] public string CutColor     { get; set; } = "#888888";
        /// <summary>Fill pattern name applied to cut-plane overrides (empty = solid fill).</summary>
        [XmlAttribute] public string CutPattern   { get; set; } = "";
        /// <summary>When <see langword="true"/> the cut color override is applied to matching elements.</summary>
        [XmlAttribute] public bool   OverrideCut  { get; set; } = true;
        /// <summary>Hex color applied to surface/projection graphic overrides.</summary>
        [XmlAttribute] public string SurfColor    { get; set; } = "#aaaaaa";
        /// <summary>Fill pattern name applied to surface/projection overrides (empty = solid fill).</summary>
        [XmlAttribute] public string SurfPattern  { get; set; } = "";
        /// <summary>When <see langword="true"/> the surface color override is applied to matching elements.</summary>
        [XmlAttribute] public bool   OverrideSurf { get; set; } = true;
        /// <summary>Hex color applied to projection and cut line overrides.</summary>
        [XmlAttribute] public string LineColor    { get; set; } = "#888888";
        /// <summary>When <see langword="true"/> the line color/weight override is applied to matching elements.</summary>
        [XmlAttribute] public bool   OverrideLine { get; set; } = true;
        /// <summary>Line pattern name applied to matching elements (e.g. "Solid", "Dash").</summary>
        [XmlAttribute] public string LinePattern  { get; set; } = "Solid";
        /// <summary>Line weight (pen weight index) applied to matching elements.</summary>
        [XmlAttribute] public int    LineWeight   { get; set; } = 1;
        /// <summary>When <see langword="true"/> the halftone override is applied to matching elements.</summary>
        [XmlAttribute] public bool   Halftone     { get; set; } = false;
        /// <summary>Surface transparency percentage (0–100) applied to matching elements.</summary>
        [XmlAttribute] public int    Transparency { get; set; } = 0;    // 0–100 %
        /// <summary>When <see langword="false"/> matching elements are hidden in the view.</summary>
        [XmlAttribute] public bool   Visible      { get; set; } = true; // element visibility in view
        /// <summary>When <see langword="false"/> the filter itself is disabled in the view filter list.</summary>
        [XmlAttribute] public bool   FilterOn     { get; set; } = true; // filter active in view
        /// <summary>Optional free-text notes for this rule, not used during filter application.</summary>
        [XmlAttribute] public string Notes        { get; set; } = "";

        /// <summary>
        /// Keywords tested against the element's filter parameter value.
        /// Matching behaviour is controlled by <see cref="MatchType"/>.
        /// </summary>
        [XmlArray("Match")]
        [XmlArrayItem("Keyword")]
        public List<string> Match { get; set; } = new List<string>();

        /// <summary>
        /// Creates a new <see cref="FilterRuleConfig"/> with a random Id and sensible defaults
        /// (enabled, contains-matching, solid grey line, weight 1, visible, filter on).
        /// </summary>
        /// <returns>A blank <see cref="FilterRuleConfig"/> ready for user editing.</returns>
        public static FilterRuleConfig NewBlank() => new FilterRuleConfig
        {
            Id                = Guid.NewGuid().ToString("N").Substring(0, 8),
            Name              = "New Rule",
            Enabled           = true,
            MatchType         = "contains",
            Parameter         = "System Type",
            BuiltInCategories = new List<string>(),
            CutColor          = "#888888",
            OverrideCut       = true,
            SurfColor         = "#aaaaaa",
            OverrideSurf      = true,
            LineColor         = "#888888",
            OverrideLine      = true,
            LinePattern       = "Solid",
            LineWeight        = 1,
            Visible           = true,
            FilterOn          = true,
        };
    }

    // =========================================================================
    // AutoFiltersSettings — singleton, XML-backed (V2)
    // Stored in %AppData%\LemoineTools\LemoineAutoFiltersV2.xml
    // =========================================================================

    /// <summary>
    /// Singleton settings root for the AutoFilters tool (V2 schema).
    /// Serialized to and from <c>%AppData%\LemoineTools\LemoineAutoFiltersV2.xml</c>.
    /// Owns the full Trade → Category → Rule hierarchy and provides static lookup
    /// tables used by the filter-rule editor UI.
    /// </summary>
    [XmlRoot("LemoineAutoFilters")]
    public sealed class AutoFiltersSettings
    {
        private static AutoFiltersSettings? _instance;
        /// <summary>
        /// Gets the singleton instance, loading from disk on first access.
        /// If no saved file exists a default configuration is created and saved.
        /// </summary>
        public static AutoFiltersSettings Instance => _instance ?? (_instance = Load());

        /// <summary>Parameterless constructor required by <see cref="XmlSerializer"/>.</summary>
        public AutoFiltersSettings() { }

        /// <summary>Schema version stored in the XML root attribute. Current version is 3.</summary>
        [XmlAttribute] public int    Version                { get; set; } = 3;
        /// <summary>ISO-8601 timestamp written by <see cref="Save"/> to record when the file was last saved.</summary>
        [XmlAttribute] public string? Exported              { get; set; }

        // Run-time defaults persisted with the config so the last-used settings are remembered.
        /// <summary>
        /// When <see langword="true"/>, existing graphic overrides on a view are kept when
        /// applying filters; only missing overrides are added.
        /// </summary>
        [XmlAttribute] public bool   KeepExistingOverrides    { get; set; } = false;
        /// <summary>
        /// When <see langword="true"/>, an existing Revit filter definition with the same name
        /// is overwritten rather than reused during filter application.
        /// </summary>
        [XmlAttribute] public bool   OverwriteFilterDefinition { get; set; } = false;

        /// <summary>The ordered list of trade configurations that make up the full filter schema.</summary>
        [XmlArray("Trades")]
        [XmlArrayItem("Trade")]
        public List<FilterTradeConfig> Trades { get; set; } = new List<FilterTradeConfig>();

        /// <summary>
        /// Names of ParameterFilterElements last created by "Create Filters".
        /// Used to detect and delete orphans when trades or rules are removed.
        /// </summary>
        [XmlArray("CreatedFilters")]
        [XmlArrayItem("Filter")]
        public List<string> CreatedFilterNames { get; set; } = new List<string>();

        /// <summary>
        /// Guarantees every trade has a non-empty, unique Id, rewriting any empty or
        /// duplicate Id to a fresh unique value. Trades the Discover tool created in bulk
        /// previously shared a time-derived Id, which made the rules editor treat them as a
        /// single trade (clicking one opened both, and only the top one was viewable).
        /// User-set unique Ids (e.g. "MD", "EL") are left untouched.
        /// Returns <see langword="true"/> if any Id was changed.
        /// </summary>
        public static bool EnsureUniqueTradeIds(List<FilterTradeConfig> trades)
        {
            if (trades == null) return false;

            var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool changed = false;

            foreach (var t in trades)
            {
                string id = (t.Id ?? "").Trim();
                if (id.Length == 0 || seen.Contains(id))
                {
                    string fresh;
                    do { fresh = FilterTradeConfig.NewTradeId(); } while (seen.Contains(fresh));
                    t.Id    = fresh;
                    id      = fresh;
                    changed = true;
                }
                seen.Add(id);
            }
            return changed;
        }

        // ── Filter naming + grouping helpers ──────────────────────────────────

        /// <summary>
        /// Builds the canonical ParameterFilterElement name for a rule:
        /// <c>{tradeId}_{RULE_NAME}</c> with spaces replaced by underscores and uppercased.
        /// Single source of truth for the convention used by the filter engine, the
        /// Ceiling Heatmap tool, and the filter pickers.
        ///
        /// The result is sanitised of characters Revit prohibits in element names
        /// (e.g. the ':' in a system name like "HVAC: EXHAUST AIR"), which otherwise make
        /// <c>ParameterFilterElement.Create</c> throw "name cannot include prohibited
        /// characters". Because every caller routes through this method the sanitised name
        /// stays consistent across creation, orphan cleanup, colour matching, and grouping.
        /// </summary>
        public static string MakeFilterName(string tradeId, string ruleName)
            => SanitizeFilterName(
                   (tradeId ?? "") + "_"
                   + (ruleName ?? "").Trim().Replace(" ", "_").ToUpperInvariant());

        /// <summary>
        /// True when a rule yields a ParameterFilterElement: it matches the whole category,
        /// uses a value predicate (has / has-no value), or carries at least one keyword.
        /// Single source of truth shared by the filter engine and the auto-create-on-close
        /// change detection.
        /// </summary>
        public static bool RuleProducesFilter(FilterRuleConfig r)
        {
            if (r == null) return false;
            string mt = (r.MatchType ?? "contains").ToLowerInvariant();
            return mt == "all" || mt == "has a value" || mt == "has no value" || r.Match.Count > 0;
        }

        /// <summary>
        /// Builds the set of filter names the current trade configuration is expected to own:
        /// every enabled, filter-producing rule of every non-externally-managed trade.
        /// Used both to drive orphan cleanup and to decide whether the auto-create-on-close
        /// pass has anything to do.
        /// </summary>
        public static HashSet<string> ComputeExpectedFilterNames(IEnumerable<FilterTradeConfig> trades)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (trades == null) return names;
            foreach (var t in trades)
            {
                if (t == null || t.ExternallyManaged) continue;
                foreach (var r in t.Rules)
                {
                    if (!r.Enabled || !RuleProducesFilter(r)) continue;
                    names.Add(MakeFilterName(t.Id, r.Name));
                }
            }
            return names;
        }

        /// <summary>
        /// Canonical signature of the part of a rule that is written to the Revit
        /// ParameterFilterElement <em>definition</em> (categories + parameter + match type +
        /// keywords). Graphic overrides and view flags are deliberately excluded — they are
        /// not part of the definition that the create-on-close pass rewrites, so changing a
        /// colour must not count as a definition change. Categories and keywords are compared
        /// as order-insensitive sets so a pure reorder is not treated as a change.
        /// </summary>
        private static string RuleDefinitionSignature(FilterRuleConfig r)
        {
            if (r == null) return "";
            string cats = string.Join(",",
                (r.BuiltInCategories ?? new List<string>())
                    .Where(c => !string.IsNullOrEmpty(c))
                    .Select(c => c.Trim())
                    .OrderBy(c => c, StringComparer.Ordinal));
            string kws = string.Join("",
                (r.Match ?? new List<string>())
                    .OrderBy(k => k ?? "", StringComparer.Ordinal));
            string mt = (r.MatchType ?? "contains").ToLowerInvariant();
            string param = r.Parameter ?? "";
            return $"{cats}{param}{mt}{kws}";
        }

        /// <summary>
        /// Returns the filter names whose <em>definition</em> changed between the
        /// <paramref name="before"/> and <paramref name="after"/> trade buffers — i.e. every
        /// enabled, filter-producing, non-externally-managed rule in <paramref name="after"/>
        /// whose category/parameter/match-type/keyword definition differs from the rule of the
        /// same filter name in <paramref name="before"/>, or that had no counterpart in
        /// <paramref name="before"/>.
        ///
        /// Used by the auto-create-on-close pass so it only rewrites the definitions of rules
        /// the user actually changed in the menu — leaving filters that were edited outside the
        /// menu (e.g. in Revit's own filter editor) untouched.
        /// </summary>
        public static HashSet<string> ComputeChangedFilterNames(
            IEnumerable<FilterTradeConfig> before, IEnumerable<FilterTradeConfig> after)
        {
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // before-name → signature. A duplicate name keeps the first signature seen;
            // any mismatch against the "after" rule still flags it as changed.
            var beforeSig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in before ?? new List<FilterTradeConfig>())
            {
                if (t == null || t.ExternallyManaged) continue;
                foreach (var r in t.Rules ?? new List<FilterRuleConfig>())
                {
                    if (r == null) continue;
                    string name = MakeFilterName(t.Id, r.Name);
                    if (!beforeSig.ContainsKey(name))
                        beforeSig[name] = RuleDefinitionSignature(r);
                }
            }

            foreach (var t in after ?? new List<FilterTradeConfig>())
            {
                if (t == null || t.ExternallyManaged) continue;
                foreach (var r in t.Rules ?? new List<FilterRuleConfig>())
                {
                    if (r == null || !r.Enabled || !RuleProducesFilter(r)) continue;
                    string name = MakeFilterName(t.Id, r.Name);
                    if (!beforeSig.TryGetValue(name, out var prevSig)
                        || prevSig != RuleDefinitionSignature(r))
                        changed.Add(name);
                }
            }
            return changed;
        }

        /// <summary>
        /// Characters Revit forbids in element names. Sourced from the Revit API error
        /// "name cannot include prohibited characters, such as { } [ ] | ; &lt; &gt; ? ` ~"
        /// plus backslash and colon, which are also rejected.
        /// </summary>
        private static readonly char[] ProhibitedNameChars =
            { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        /// <summary>
        /// Replaces every Revit-prohibited character with '_', collapses runs of
        /// underscores, and trims leading/trailing underscores so the result is a legal,
        /// stable element name. Deterministic so it can be recomputed for name matching.
        /// </summary>
        public static string SanitizeFilterName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";

            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(ProhibitedNameChars, chars[i]) >= 0)
                    chars[i] = '_';

            // Collapse consecutive underscores so "HVAC:_EXHAUST" → "HVAC_EXHAUST"
            // (rather than a double underscore) and trim the edges.
            var sb = new System.Text.StringBuilder(chars.Length);
            bool prevUnderscore = false;
            foreach (char c in chars)
            {
                if (c == '_')
                {
                    if (!prevUnderscore) sb.Append(c);
                    prevUnderscore = true;
                }
                else
                {
                    sb.Append(c);
                    prevUnderscore = false;
                }
            }
            return sb.ToString().Trim('_');
        }

        /// <summary>
        /// Groups the supplied Revit filter names by the trade that owns the rule which
        /// produced each filter. Filters that map to no current rule are collected into a
        /// single <c>"Others"</c> group. Trade groups are emitted in config order with
        /// <c>"Others"</c> always last, so the picker tab order is deterministic.
        /// </summary>
        public static Dictionary<string, List<string>> GroupFilterNamesByTrade(
            IReadOnlyList<string> filterNames)
        {
            const string OthersKey = "Others";

            // Expected filter name → trade label, computed from the current rules.
            var nameToTrade = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var trade in Instance.Trades ?? new List<FilterTradeConfig>())
                foreach (var rule in trade.Rules ?? new List<FilterRuleConfig>())
                    nameToTrade[MakeFilterName(trade.Id, rule.Name)] = trade.Label;

            // Bin each filter under its trade label, or "Others" if unmatched.
            var unordered = new Dictionary<string, List<string>>();
            foreach (var name in filterNames ?? new List<string>())
            {
                string key = nameToTrade.TryGetValue(name, out var label) && !string.IsNullOrEmpty(label)
                    ? label
                    : OthersKey;
                if (!unordered.TryGetValue(key, out var list))
                    unordered[key] = list = new List<string>();
                list.Add(name);
            }

            // Re-emit in trade config order, then "Others" last.
            var ordered = new Dictionary<string, List<string>>();
            foreach (var trade in Instance.Trades ?? new List<FilterTradeConfig>())
            {
                if (!ordered.ContainsKey(trade.Label)
                    && unordered.TryGetValue(trade.Label, out var list))
                    ordered[trade.Label] = list;
            }
            if (unordered.TryGetValue(OthersKey, out var others))
                ordered[OthersKey] = others;
            return ordered;
        }

        // ── Known lookup values ───────────────────────────────────────────────

        /// <summary>
        /// Full set of OST_* Revit BuiltInCategory strings recognized by the AutoFilters tool,
        /// spanning MEP (duct, pipe, electrical), structural, and architectural categories.
        /// Used to populate category pickers in the filter editor.
        /// </summary>
        public static readonly string[] KnownBuiltInCategories =
        {
            // ── Mechanical / HVAC ──────────────────────────────────────────
            "OST_DuctCurves","OST_DuctFitting","OST_DuctAccessory",
            "OST_DuctInsulations","OST_DuctLinings",
            "OST_FlexDuctCurves","OST_DuctTerminal","OST_MechanicalEquipment",
            "OST_FabricationDuctwork",
            // ── Piping / Plumbing ──────────────────────────────────────────
            "OST_PipeCurves","OST_PipeFitting","OST_PipeAccessory",
            "OST_PipeInsulations","OST_PipeLinings",
            "OST_FlexPipeCurves","OST_PlumbingFixtures","OST_Sprinklers",
            "OST_FabricationPipework","OST_FabricationHangers","OST_FabricationContainment",
            // ── Electrical ─────────────────────────────────────────────────
            "OST_CableTray","OST_CableTrayFitting","OST_Conduit","OST_ConduitFitting",
            "OST_ElectricalEquipment","OST_ElectricalFixtures",
            "OST_LightingFixtures","OST_LightingDevices",
            "OST_CommunicationDevices","OST_FireAlarmDevices","OST_SecurityDevices",
            "OST_DataDevices","OST_TelephoneDevices","OST_NurseCallDevices",
            // ── Structural ─────────────────────────────────────────────────
            "OST_StructuralFraming","OST_StructuralColumns","OST_StructuralFoundation",
            "OST_StructuralTruss","OST_StructuralStiffener","OST_StructuralConnectionHandlers",
            "OST_Rebar","OST_AreaReinforcement","OST_PathReinforcement",
            "OST_FabricReinforcement","OST_FabricArea",
            // ── Architectural ──────────────────────────────────────────────
            "OST_Walls","OST_Floors","OST_Roofs","OST_Ceilings",
            "OST_Doors","OST_Windows","OST_Stairs","OST_Ramps","OST_Railings",
            "OST_Columns","OST_GenericModel","OST_Furniture","OST_FurnitureSystems",
            "OST_Casework","OST_SpecialityEquipment",
            "OST_CurtainWallPanels","OST_CurtainWallMullions",
            "OST_Mass","OST_Entourage","OST_Planting","OST_Site","OST_Topography",
            "OST_Rooms","OST_Areas","OST_MEPSpaces",
        };

        /// <summary>
        /// Display names of the Revit line patterns available for selection in the rule editor.
        /// Values correspond to built-in Revit line pattern names.
        /// </summary>
        public static readonly string[] KnownLinePatterns =
        {
            "Solid","Dash","Dash dot","Dash dot dot","Dot","Long dash","Center","Hidden",
        };

        /// <summary>
        /// Parameter display names backed by a BuiltInParameter, so a host view filter
        /// using them reliably matches elements inside LINKED models (universal ids /
        /// duct-pipe System Type). Other parameters depend on shared-parameter bindings
        /// and may not affect linked elements — the rule editor flags them.
        /// </summary>
        public static readonly HashSet<string> LinkSafeParameters =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "System Classification", "System Name", "System Type",
                "Fabrication Service", "Type Name", "Family Name",
                "Structural Material", "Mark", "Comments",
            };

        /// <summary>True when <paramref name="paramName"/> reliably matches linked elements.</summary>
        public static bool IsLinkSafeParameter(string? paramName) =>
            !string.IsNullOrEmpty(paramName) && LinkSafeParameters.Contains(paramName!);

        /// <summary>All parameters available across all categories (union set).</summary>
        public static readonly string[] KnownParameters =
        {
            // General
            "Type Name","Family Name","Mark","Comments","Description","Level",
            "Phase Created","Phase Demolished","Workset",
            // MEP shared
            "System Classification","System Type","System Name","Service Name",
            "Flow","Velocity","Pressure Drop","Size",
            // Duct/pipe specific
            "Duct Service Type","Insulation Type","Lining Type",
            "Connection Type","Shape","Width","Height","Diameter","Radius",
            // Fabrication
            "Fabrication Service","Fabrication Service Type","Fabrication Part",
            "Service Abbreviation","CID",
            // Electrical
            "Panel","Circuit Number","Load Classification","Voltage","Poles",
            "Apparent Load","True Load","Power Factor",
            // Structural
            "Structural Material","Structural Usage","Cross-Section Shape",
            "Cut Length","Volume","Length","Area","Slope",
            // Space / room
            "Room Name","Room Number","Space Name","Space Number",
            "Occupancy","Department",
        };

        /// <summary>
        /// Per-category curated parameter lists. When a category is selected in the
        /// rule editor the UI surfaces these first, with the full list available below.
        /// Key = OST_* string (or category group prefix like "OST_Duct*").
        /// </summary>
        public static readonly Dictionary<string, string[]> ParametersForCategory =
            new Dictionary<string, string[]>
        {
            // Duct group
            { "OST_DuctCurves",          new[]{ "System Classification","System Type","System Name","Size","Width","Height","Diameter","Shape","Velocity","Flow","Pressure Drop","Insulation Type","Lining Type","Service Name","Type Name","Family Name","Mark","Comments" } },
            { "OST_DuctFitting",         new[]{ "System Classification","System Type","System Name","Size","Connection Type","Type Name","Family Name","Mark","Comments" } },
            { "OST_DuctAccessory",       new[]{ "System Classification","System Type","System Name","Size","Type Name","Family Name","Mark","Comments" } },
            { "OST_DuctInsulations",     new[]{ "System Classification","System Type","Insulation Type","Type Name","Family Name","Mark" } },
            { "OST_DuctLinings",         new[]{ "System Classification","System Type","Lining Type","Type Name","Family Name","Mark" } },
            { "OST_FlexDuctCurves",      new[]{ "System Classification","System Type","System Name","Width","Height","Diameter","Type Name","Family Name","Mark" } },
            { "OST_DuctTerminal",        new[]{ "System Classification","System Type","System Name","Flow","Velocity","Type Name","Family Name","Mark","Comments" } },
            { "OST_MechanicalEquipment", new[]{ "System Classification","System Type","System Name","Service Name","Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_FabricationDuctwork", new[]{ "Fabrication Service","Fabrication Service Type","Fabrication Part","Service Abbreviation","CID","Size","Type Name","Mark" } },
            // Pipe group
            { "OST_PipeCurves",          new[]{ "System Classification","System Type","System Name","Size","Diameter","Velocity","Flow","Pressure Drop","Insulation Type","Service Name","Type Name","Family Name","Mark","Comments" } },
            { "OST_PipeFitting",         new[]{ "System Classification","System Type","System Name","Size","Connection Type","Type Name","Family Name","Mark","Comments" } },
            { "OST_PipeAccessory",       new[]{ "System Classification","System Type","System Name","Size","Type Name","Family Name","Mark","Comments" } },
            { "OST_PipeInsulations",     new[]{ "System Classification","System Type","Insulation Type","Type Name","Family Name","Mark" } },
            { "OST_PipeLinings",         new[]{ "System Classification","System Type","Lining Type","Type Name","Family Name","Mark" } },
            { "OST_FlexPipeCurves",      new[]{ "System Classification","System Type","System Name","Diameter","Type Name","Family Name","Mark" } },
            { "OST_PlumbingFixtures",    new[]{ "System Classification","System Type","System Name","Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_Sprinklers",          new[]{ "System Classification","System Type","System Name","Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_FabricationPipework", new[]{ "Fabrication Service","Fabrication Service Type","Fabrication Part","Service Abbreviation","CID","Size","Type Name","Mark" } },
            { "OST_FabricationHangers",  new[]{ "Fabrication Service","Service Abbreviation","CID","Type Name","Mark" } },
            { "OST_FabricationContainment",new[]{ "Fabrication Service","Fabrication Service Type","Service Abbreviation","CID","Size","Type Name","Mark" } },
            // Electrical group
            { "OST_CableTray",           new[]{ "System Type","System Name","Size","Width","Type Name","Family Name","Mark","Comments" } },
            { "OST_CableTrayFitting",    new[]{ "System Type","System Name","Size","Type Name","Family Name","Mark" } },
            { "OST_Conduit",             new[]{ "System Type","System Name","Size","Diameter","Type Name","Family Name","Mark","Comments" } },
            { "OST_ConduitFitting",      new[]{ "System Type","System Name","Size","Type Name","Family Name","Mark" } },
            { "OST_ElectricalEquipment", new[]{ "Panel","Voltage","Load Classification","Apparent Load","Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_ElectricalFixtures",  new[]{ "Panel","Circuit Number","Load Classification","Voltage","Poles","Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_LightingFixtures",    new[]{ "Panel","Circuit Number","Load Classification","Voltage","Poles","Apparent Load","Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_LightingDevices",     new[]{ "Panel","Circuit Number","Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_CommunicationDevices",new[]{ "Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_FireAlarmDevices",    new[]{ "Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_SecurityDevices",     new[]{ "Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_DataDevices",         new[]{ "Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_TelephoneDevices",    new[]{ "Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_NurseCallDevices",    new[]{ "Type Name","Family Name","Mark","Comments","Level" } },
            // Structural group
            { "OST_StructuralFraming",   new[]{ "Structural Material","Structural Usage","Cross-Section Shape","Cut Length","Volume","Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_StructuralColumns",   new[]{ "Structural Material","Structural Usage","Cross-Section Shape","Length","Volume","Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_StructuralFoundation",new[]{ "Structural Material","Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_StructuralTruss",     new[]{ "Structural Material","Type Name","Family Name","Mark","Comments","Level" } },
            { "OST_Rebar",               new[]{ "Structural Material","Structural Usage","Bar Diameter","Type Name","Family Name","Mark","Comments","Level" } },
            // Architectural group
            { "OST_Walls",               new[]{ "Type Name","Family Name","Mark","Comments","Level","Phase Created","Unconnected Height","Base Constraint","Top Constraint" } },
            { "OST_Floors",              new[]{ "Type Name","Family Name","Mark","Comments","Level","Phase Created","Slope","Thickness" } },
            { "OST_Roofs",               new[]{ "Type Name","Family Name","Mark","Comments","Level","Phase Created","Slope","Thickness" } },
            { "OST_Ceilings",            new[]{ "Type Name","Family Name","Mark","Comments","Level","Phase Created","Slope","Height Offset from Level" } },
            { "OST_Doors",               new[]{ "Type Name","Family Name","Mark","Comments","Level","Phase Created","Width","Height","Fire Rating" } },
            { "OST_Windows",             new[]{ "Type Name","Family Name","Mark","Comments","Level","Phase Created","Width","Height","Sill Height" } },
            { "OST_Stairs",              new[]{ "Type Name","Family Name","Mark","Comments","Level","Phase Created","Desired Number of Risers" } },
            { "OST_Rooms",               new[]{ "Room Name","Room Number","Occupancy","Department","Level","Phase","Area","Volume","Comments" } },
            { "OST_Areas",               new[]{ "Area","Occupancy","Department","Level","Phase","Comments","Type Name" } },
            { "OST_MEPSpaces",           new[]{ "Space Name","Space Number","Occupancy","Level","Phase","Area","Volume","Type Name" } },
            { "OST_GenericModel",        new[]{ "Type Name","Family Name","Mark","Comments","Level","Description" } },
            { "OST_Furniture",           new[]{ "Type Name","Family Name","Mark","Comments","Level","Phase Created","Department","Occupancy" } },
            { "OST_SpecialityEquipment", new[]{ "Type Name","Family Name","Mark","Comments","Level","Phase Created" } },
        };

        /// <summary>
        /// Returns the parameter list to show for a given OST_* category string.
        /// Falls back to KnownParameters if no specific mapping exists.
        /// </summary>
        public static string[] GetParametersFor(string ostCategory)
        {
            if (!string.IsNullOrEmpty(ostCategory) &&
                ParametersForCategory.TryGetValue(ostCategory, out var specific))
                return specific;
            return KnownParameters;
        }

        /// <summary>
        /// Hardcoded fallback map of Revit category display name → OST_* BuiltInCategory
        /// string. Used only when no live document has been captured (e.g. the standalone
        /// preview app). When a document is open, <see cref="CaptureFilterableCategories"/>
        /// replaces this with the exact list Revit reports via
        /// <c>ParameterFilterUtilities.GetAllFilterableCategories</c>.
        /// </summary>
        private static readonly Dictionary<string, string> DefaultKnownCategoryMap =
            new Dictionary<string, string>
        {
            // ── Mechanical / HVAC ──────────────────────────────────────────
            { "Ducts",                    "OST_DuctCurves" },
            { "Duct Fittings",            "OST_DuctFitting" },
            { "Duct Accessories",         "OST_DuctAccessory" },
            { "Duct Insulation",          "OST_DuctInsulations" },
            { "Duct Linings",             "OST_DuctLinings" },
            { "Air Terminals",            "OST_DuctTerminal" },
            { "Flex Ducts",               "OST_FlexDuctCurves" },
            { "Mechanical Equipment",     "OST_MechanicalEquipment" },
            { "Fabrication Ductwork",     "OST_FabricationDuctwork" },
            // ── Piping / Plumbing ──────────────────────────────────────────
            { "Pipes",                    "OST_PipeCurves" },
            { "Pipe Fittings",            "OST_PipeFitting" },
            { "Pipe Accessories",         "OST_PipeAccessory" },
            { "Pipe Insulation",          "OST_PipeInsulations" },
            { "Pipe Linings",             "OST_PipeLinings" },
            { "Flex Pipes",               "OST_FlexPipeCurves" },
            { "Plumbing Fixtures",        "OST_PlumbingFixtures" },
            { "Sprinklers",               "OST_Sprinklers" },
            { "Fabrication Pipework",     "OST_FabricationPipework" },
            { "Fabrication Hangers",      "OST_FabricationHangers" },
            { "Fabrication Containment",  "OST_FabricationContainment" },
            // ── Electrical ─────────────────────────────────────────────────
            { "Cable Trays",              "OST_CableTray" },
            { "Cable Tray Fittings",      "OST_CableTrayFitting" },
            { "Conduits",                 "OST_Conduit" },
            { "Conduit Fittings",         "OST_ConduitFitting" },
            { "Electrical Equipment",     "OST_ElectricalEquipment" },
            { "Electrical Fixtures",      "OST_ElectricalFixtures" },
            { "Lighting Fixtures",        "OST_LightingFixtures" },
            { "Lighting Devices",         "OST_LightingDevices" },
            { "Communication Devices",    "OST_CommunicationDevices" },
            { "Fire Alarm Devices",       "OST_FireAlarmDevices" },
            { "Security Devices",         "OST_SecurityDevices" },
            { "Data Devices",             "OST_DataDevices" },
            { "Telephone Devices",        "OST_TelephoneDevices" },
            { "Nurse Call Devices",       "OST_NurseCallDevices" },
            // ── Structural ─────────────────────────────────────────────────
            { "Structural Framing",       "OST_StructuralFraming" },
            { "Structural Columns",       "OST_StructuralColumns" },
            { "Structural Foundations",   "OST_StructuralFoundation" },
            { "Structural Trusses",       "OST_StructuralTruss" },
            { "Structural Stiffeners",    "OST_StructuralStiffener" },
            { "Structural Connections",   "OST_StructuralConnectionHandlers" },
            { "Rebar",                    "OST_Rebar" },
            { "Area Reinforcement",       "OST_AreaReinforcement" },
            { "Path Reinforcement",       "OST_PathReinforcement" },
            { "Fabric Reinforcement",     "OST_FabricReinforcement" },
            { "Fabric Area",              "OST_FabricArea" },
            // ── Architectural ──────────────────────────────────────────────
            { "Walls",                    "OST_Walls" },
            { "Floors",                   "OST_Floors" },
            { "Roofs",                    "OST_Roofs" },
            { "Ceilings",                 "OST_Ceilings" },
            { "Doors",                    "OST_Doors" },
            { "Windows",                  "OST_Windows" },
            { "Stairs",                   "OST_Stairs" },
            { "Ramps",                    "OST_Ramps" },
            { "Railings",                 "OST_Railings" },
            { "Columns",                  "OST_Columns" },
            { "Generic Models",           "OST_GenericModel" },
            { "Furniture",                "OST_Furniture" },
            { "Furniture Systems",        "OST_FurnitureSystems" },
            { "Casework",                 "OST_Casework" },
            { "Specialty Equipment",      "OST_SpecialityEquipment" },
            { "Curtain Wall Panels",      "OST_CurtainWallPanels" },
            { "Curtain Wall Mullions",    "OST_CurtainWallMullions" },
            { "Mass",                     "OST_Mass" },
            { "Entourage",                "OST_Entourage" },
            { "Planting",                 "OST_Planting" },
            { "Site",                     "OST_Site" },
            { "Topography",               "OST_Topography" },
            { "Rooms",                    "OST_Rooms" },
            { "Areas",                    "OST_Areas" },
            { "Spaces",                   "OST_MEPSpaces" },
            // ── Architectural — additional model categories / subcategories ──
            { "Curtain Systems",          "OST_Curtain_Systems" },
            { "Wall Sweeps",              "OST_Cornices" },
            { "Slab Edges",               "OST_EdgeSlab" },
            { "Gutters",                  "OST_Gutter" },
            { "Fascias",                  "OST_Fascia" },
            { "Roof Soffits",             "OST_RoofSoffit" },
            { "Mass Floors",              "OST_MassFloor" },
            { "Parts",                    "OST_Parts" },
            { "Assemblies",               "OST_Assemblies" },
            { "Signage",                  "OST_Signage" },
            { "Vertical Circulation",     "OST_VerticalCirculation" },
            { "Audio Visual Devices",     "OST_AudioVisualDevices" },
            { "Medical Equipment",        "OST_MedicalEquipment" },
            { "Food Service Equipment",   "OST_FoodServiceEquipment" },
            { "Temporary Structures",     "OST_TemporaryStructure" },
            // ── Site / civil ───────────────────────────────────────────────
            { "Parking",                  "OST_Parking" },
            { "Roads",                    "OST_Roads" },
            { "Hardscape",                "OST_Hardscape" },
            // ── Mechanical / Plumbing — additional ─────────────────────────
            { "Plumbing Equipment",       "OST_PlumbingEquipment" },
            { "Mechanical Control Devices","OST_MechanicalControlDevices" },
            // ── Electrical — additional ────────────────────────────────────
            { "Wires",                    "OST_Wire" },
            { "Fire Protection",          "OST_FireProtection" },
            // ── Structural — additional ────────────────────────────────────
            { "Structural Rebar Couplers","OST_Coupler" },
        };

        /// <summary>Sorted fallback display names for the category checklist.</summary>
        private static readonly string[] DefaultKnownCategoryDisplayNames;

        static AutoFiltersSettings()
        {
            // Only surface categories whose OST_* string resolves to a real BuiltInCategory in
            // the running Revit. This keeps the picker version-safe: a name that doesn't exist
            // in this Revit (e.g. a category added in a later release) is silently dropped rather
            // than shown as a dead entry that would never match any element.
            var names = DefaultKnownCategoryMap
                .Where(kv => IsResolvableBuiltInCategory(kv.Value))
                .Select(kv => kv.Key)
                .ToList();
            names.Sort(StringComparer.OrdinalIgnoreCase);
            DefaultKnownCategoryDisplayNames = names.ToArray();
        }

        // ── Runtime (document-driven) category snapshot ───────────────────────
        // Populated by CaptureFilterableCategories on the Revit main thread so the
        // pickers list exactly what Revit's "Edit Filters → Categories" dialog shows.
        // Null until a document is captured; the public accessors fall back to the
        // hardcoded defaults so the standalone preview app still works.
        private static Dictionary<string, string>? _runtimeCategoryMap;
        private static string[]? _runtimeDisplayNames;
        private static Dictionary<string, IReadOnlyList<string>>? _runtimeSubcategories;

        /// <summary>
        /// Maps Revit category display name → OST_* BuiltInCategory string. Returns the
        /// document-captured set when available (exact parity with Revit's filterable
        /// category list), otherwise the hardcoded fallback.
        /// </summary>
        public static IReadOnlyDictionary<string, string> KnownCategoryMap =>
            _runtimeCategoryMap ?? (IReadOnlyDictionary<string, string>)DefaultKnownCategoryMap;

        /// <summary>Sorted display names for the category checklist (document-captured when available).</summary>
        public static IReadOnlyList<string> KnownCategoryDisplayNames =>
            _runtimeDisplayNames ?? (IReadOnlyList<string>)DefaultKnownCategoryDisplayNames;

        // Reverse lookup (OST_* → display name), cached and rebuilt whenever the
        // underlying KnownCategoryMap reference changes (i.e. when a document snapshot
        // is captured). Replaces the O(n) FirstOrDefault(kv => kv.Value == ost) scans
        // that several call sites ran per row.
        private static Dictionary<string, string>? _ostToDisplay;
        private static IReadOnlyDictionary<string, string>? _ostToDisplaySource;

        /// <summary>
        /// Returns the Revit category display name for an OST_* BuiltInCategory string,
        /// or the OST string itself when unmapped. Backed by a cached reverse map of
        /// <see cref="KnownCategoryMap"/>; the cache rebuilds when the map is replaced by
        /// a document capture, so callers always see the current names without re-scanning.
        /// </summary>
        public static string DisplayNameForOst(string ost)
        {
            if (string.IsNullOrEmpty(ost)) return ost ?? "";
            var src = KnownCategoryMap;
            if (_ostToDisplay == null || !ReferenceEquals(src, _ostToDisplaySource))
            {
                var rev = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in src)
                    if (!rev.ContainsKey(kv.Value)) rev[kv.Value] = kv.Key;
                _ostToDisplay       = rev;
                _ostToDisplaySource = src;
            }
            return _ostToDisplay.TryGetValue(ost, out var name) ? name : ost;
        }

        /// <summary>
        /// Reads the exact filterable-category list from the open document and stores it as the
        /// runtime snapshot the category pickers read from, mirroring what Revit shows in its own
        /// "Edit Filters → Categories" tree: real display names, and real parent→sub-category
        /// nesting taken from each <see cref="Category.Parent"/> (not a hand-curated grouping).
        ///
        /// Must be called on the Revit main thread (it queries the Revit API) before the window
        /// thread that builds the picker starts. Only BuiltInCategory-backed categories are
        /// captured — the filter engine persists categories as OST_* strings, so non-builtin
        /// custom subcategories (which it cannot store) are skipped. On any failure the snapshot
        /// is left untouched and the hardcoded fallback continues to be used.
        /// </summary>
        public static void CaptureFilterableCategories(Document doc)
        {
            if (doc == null) return;
            try
            {
                var ids = ParameterFilterUtilities.GetAllFilterableCategories();

                // The set of filterable categories that are BuiltInCategory-backed (negative id).
                // Parent linkage is only honoured when the parent is itself in this set.
                var filterable = new HashSet<long>();
                foreach (var id in ids)
                {
                    long raw = id.Value;
                    if (raw < 0 && (BuiltInCategory)(int)raw != BuiltInCategory.INVALID)
                        filterable.Add(raw);
                }
                if (filterable.Count == 0) return;

                // Resolve each filterable category to its real Revit name and real parent.
                var nodes      = new List<CategoryNode>();
                var nameByRaw  = new Dictionary<long, string>();
                foreach (var id in ids)
                {
                    long raw = id.Value;
                    if (!filterable.Contains(raw)) continue;

                    Category? cat = null;
                    try { cat = Category.GetCategory(doc, id); }
                    catch (Exception __cex) { LemoineLog.Swallowed("CaptureFilterableCategories.GetCategory", __cex); }
                    if (cat == null) continue;
                    if (cat.CategoryType != CategoryType.Model) continue;

                    string name = cat.Name;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    long? parentRaw = null;
                    try
                    {
                        var p = cat.Parent;
                        if (p != null) parentRaw = p.Id.Value;
                    }
                    catch (Exception __pex) { LemoineLog.Swallowed("CaptureFilterableCategories.Parent", __pex); }
                    // Only nest under a parent that is itself a filterable, builtin category.
                    if (parentRaw.HasValue && !filterable.Contains(parentRaw.Value)) parentRaw = null;

                    nodes.Add(new CategoryNode(raw, ((BuiltInCategory)(int)raw).ToString(), name, parentRaw));
                    nameByRaw[raw] = name;
                }
                if (nodes.Count == 0) return;

                // Disambiguate duplicate Revit names (e.g. an "Insulation" sub-category under two
                // different parents) by qualifying with the parent name, so each display token maps
                // to exactly one OST string. Names that are unique stay verbatim.
                var nameCounts = nodes.GroupBy(n => n.Name, StringComparer.Ordinal)
                                      .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                var displayByRaw = new Dictionary<long, string>();
                var map          = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var n in nodes)
                {
                    string display = n.Name;
                    if (nameCounts[n.Name] > 1 && n.ParentRaw.HasValue &&
                        nameByRaw.TryGetValue(n.ParentRaw.Value, out var pn))
                        display = n.Name + " (" + pn + ")";

                    // Final guard against any residual collision so map keys stay unique.
                    if (map.ContainsKey(display) && map[display] != n.Ost)
                        display = display + " [" + n.Ost + "]";

                    displayByRaw[n.Raw] = display;
                    if (!map.ContainsKey(display)) map[display] = n.Ost;
                }

                // Real parent → children nesting for the picker carets.
                var subs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var n in nodes)
                {
                    if (!n.ParentRaw.HasValue) continue;
                    if (!displayByRaw.TryGetValue(n.ParentRaw.Value, out var parentDisplay)) continue;
                    if (!subs.TryGetValue(parentDisplay, out var kids))
                        subs[parentDisplay] = kids = new List<string>();
                    kids.Add(displayByRaw[n.Raw]);
                }
                foreach (var kids in subs.Values)
                    kids.Sort(StringComparer.OrdinalIgnoreCase);

                var names = displayByRaw.Values.ToList();
                names.Sort(StringComparer.OrdinalIgnoreCase);

                _runtimeCategoryMap   = map;
                _runtimeDisplayNames  = names.ToArray();
                _runtimeSubcategories = subs.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<string>)kv.Value,
                    StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("AutoFiltersSettings.CaptureFilterableCategories", ex);
            }
        }

        /// <summary>One filterable Revit category resolved from the open document.</summary>
        private readonly struct CategoryNode
        {
            public CategoryNode(long raw, string ost, string name, long? parentRaw)
            {
                Raw = raw; Ost = ost; Name = name; ParentRaw = parentRaw;
            }
            public long    Raw       { get; }
            public string  Ost       { get; }
            public string  Name      { get; }
            public long?   ParentRaw { get; }
        }


        /// <summary>
        /// True when <paramref name="ost"/> parses to a valid, non-INVALID
        /// <see cref="BuiltInCategory"/> in the running Revit version.
        /// </summary>
        private static bool IsResolvableBuiltInCategory(string ost)
            => !string.IsNullOrEmpty(ost)
               && Enum.TryParse<BuiltInCategory>(ost, false, out var bic)
               && bic != BuiltInCategory.INVALID;

        /// <summary>
        /// Parent category display name → the related sub-category display names nested beneath
        /// it in the category picker. Both parent and children are real, filterable categories
        /// already present in <see cref="KnownCategoryMap"/>; children are hidden from the flat
        /// top level and surfaced under the parent's expand caret. Revit's internal V/G
        /// sub-categories are not filterable, so only real model categories are grouped here.
        /// A child whose OST string doesn't resolve in the running Revit is dropped by the
        /// picker (it only renders children present in its ItemsSource).
        /// </summary>
        private static readonly Dictionary<string, IReadOnlyList<string>> DefaultCategorySubcategories =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            // ── Mechanical ─────────────────────────────────────────────────
            { "Ducts",                new[]{ "Duct Fittings", "Duct Accessories", "Duct Insulation", "Duct Linings", "Flex Ducts", "Air Terminals" } },
            { "Mechanical Equipment", new[]{ "Mechanical Control Devices" } },
            { "Fabrication Ductwork", new[]{ "Fabrication Hangers", "Fabrication Containment" } },
            // ── Piping / Plumbing ──────────────────────────────────────────
            { "Pipes",                new[]{ "Pipe Fittings", "Pipe Accessories", "Pipe Insulation", "Pipe Linings", "Flex Pipes" } },
            { "Fabrication Pipework", new[]{ "Fabrication Hangers", "Fabrication Containment" } },
            { "Plumbing Fixtures",    new[]{ "Plumbing Equipment" } },
            // ── Electrical ─────────────────────────────────────────────────
            { "Cable Trays",          new[]{ "Cable Tray Fittings" } },
            { "Conduits",             new[]{ "Conduit Fittings" } },
            { "Lighting Fixtures",    new[]{ "Lighting Devices" } },
            { "Electrical Equipment", new[]{ "Electrical Fixtures", "Wires" } },
            // ── Architectural ──────────────────────────────────────────────
            { "Walls",                new[]{ "Curtain Wall Panels", "Curtain Wall Mullions", "Curtain Systems", "Wall Sweeps" } },
            { "Roofs",                new[]{ "Fascias", "Gutters", "Roof Soffits" } },
            { "Floors",               new[]{ "Slab Edges" } },
            // ── Structural ─────────────────────────────────────────────────
            { "Structural Framing",   new[]{ "Structural Trusses", "Structural Stiffeners", "Structural Connections", "Structural Rebar Couplers" } },
            { "Rebar",                new[]{ "Area Reinforcement", "Path Reinforcement", "Fabric Reinforcement", "Fabric Area" } },
            // ── Site ───────────────────────────────────────────────────────
            { "Site",                 new[]{ "Topography", "Planting", "Parking", "Roads", "Hardscape" } },
        };

        /// <summary>
        /// Parent category display name → nested sub-category display names for the picker carets.
        /// Returns the document-captured grouping (curated grouping intersected with the real
        /// filterable set) when available, otherwise the hardcoded fallback.
        /// </summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> CategorySubcategories =>
            _runtimeSubcategories ?? (IReadOnlyDictionary<string, IReadOnlyList<string>>)DefaultCategorySubcategories;

        // ── Persistence ───────────────────────────────────────────────────────

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch (Exception __lex) { LemoineLog.Swallowed("AutoFiltersSettings: create config directory", __lex); }
                return Path.Combine(dir, "LemoineAutoFiltersV2.xml");
            }
        }

        /// <summary>
        /// Raised after a successful <see cref="Save"/>. Subscribers (e.g. the
        /// Legend Creator tab) use this to refresh views that mirror filter data.
        /// </summary>
        public static event Action? Saved;

        /// <summary>
        /// Serializes the current settings to <c>LemoineAutoFiltersV2.xml</c> in the
        /// LemoineTools application-data folder. Updates <see cref="Exported"/> with the
        /// current timestamp before writing. Silently swallows any I/O exceptions.
        /// </summary>
        public void Save()
        {
            try
            {
                Exported = DateTime.Now.ToString("O");
                var xs = new XmlSerializer(typeof(AutoFiltersSettings));
                using (var w = new StreamWriter(FilePath)) xs.Serialize(w, this);
                RaiseSaved();
            }
            catch (Exception __lex) { LemoineLog.Swallowed("AutoFiltersSettings.Save", __lex); }
        }

        // Fires Saved on each subscriber's OWN thread. Tool windows live on separate STA
        // threads, so invoking their handlers directly from whatever thread called Save()
        // throws "the calling thread cannot access this object because a different thread
        // owns it" (seen in diagnostics.log). Marshal to each subscriber's Dispatcher.
        private static void RaiseSaved()
        {
            var handler = Saved;
            if (handler == null) return;
            foreach (var d in handler.GetInvocationList())
            {
                var action = (Action)d;
                try
                {
                    if (action.Target is System.Windows.Threading.DispatcherObject dobj &&
                        dobj.Dispatcher != null && !dobj.Dispatcher.CheckAccess())
                        dobj.Dispatcher.BeginInvoke(action);
                    else
                        action();
                }
                catch (Exception ex) { LemoineLog.Swallowed("AutoFiltersSettings.Saved subscriber", ex); }
            }
        }

        private static AutoFiltersSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var xs = new XmlSerializer(typeof(AutoFiltersSettings));
                    using (var r = new StreamReader(FilePath))
                    {
                        var s = (AutoFiltersSettings)xs.Deserialize(r)!;
                        if (s.Trades == null) s.Trades = new List<FilterTradeConfig>();
                        MigrateToV3(s);
                        // Repair any duplicate/empty trade Ids left by older bulk-create runs.
                        if (EnsureUniqueTradeIds(s.Trades)) s.Save();
                        return s;
                    }
                }
            }
            catch (Exception __lex) { LemoineLog.Swallowed("AutoFiltersSettings.Load", __lex); }
            var fresh = new AutoFiltersSettings { Trades = BuildDefaultTrades() };
            fresh.Save();
            return fresh;
        }

        /// <summary>
        /// One-time migration from V2 (Trade → Category → Rule) to V3 (Trade → Rule).
        /// Copies each category's BuiltInCategories and Parameter onto its child rules,
        /// appends those rules directly to the trade, then clears the category list.
        /// </summary>
        private static void MigrateToV3(AutoFiltersSettings s)
        {
            if (s.Version >= 3) return;

            foreach (var trade in s.Trades)
            {
                if (trade.Categories == null || trade.Categories.Count == 0) continue;

                foreach (var cat in trade.Categories)
                {
                    foreach (var rule in cat.Rules ?? new List<FilterRuleConfig>())
                    {
                        // Promote category-level fields onto the rule if not already set
                        if (rule.BuiltInCategories == null || rule.BuiltInCategories.Count == 0)
                            rule.BuiltInCategories = new List<string>(cat.BuiltInCategories ?? new List<string>());

                        // V2 stored the filter parameter at the category level; rules never
                        // carried their own. This migration is version-gated (runs once, only
                        // for V2 data), so the category parameter is authoritative — promote it
                        // unconditionally. The old value-sniff (== "System Type", the rule
                        // default) could not tell an unset default from a deliberate choice and
                        // would clobber a real "System Type" if ever run on migrated data.
                        rule.Parameter = cat.Parameter ?? "System Type";

                        trade.Rules.Add(rule);
                    }
                }
                trade.Categories.Clear();
            }

            s.Version = 3;
            s.Save();
        }

        /// <summary>
        /// Attempts to deserialize an <see cref="AutoFiltersSettings"/> XML file from
        /// an arbitrary path and make it the active singleton instance, saving it to the
        /// standard app-data location immediately.
        /// </summary>
        /// <param name="path">Full path to the source XML file to import.</param>
        /// <param name="error">
        /// When this method returns <see langword="false"/>, contains the exception message
        /// describing why the import failed; otherwise <see langword="null"/>.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the file was successfully deserialized and saved;
        /// <see langword="false"/> if an exception occurred.
        /// </returns>
        public static bool TryImportFrom(string path, out string? error)
        {
            error = null;
            try
            {
                var xs = new XmlSerializer(typeof(AutoFiltersSettings));
                using (var r = new StreamReader(path))
                {
                    var s = (AutoFiltersSettings)xs.Deserialize(r)!;
                    if (s.Trades == null) s.Trades = new List<FilterTradeConfig>();
                    MigrateToV3(s);
                    EnsureUniqueTradeIds(s.Trades);
                    _instance = s;
                    _instance.Save();
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>
        /// Serializes the supplied trade list to an XML file at the specified path.
        /// The output is a self-contained <c>LemoineAutoFilters</c> document that can
        /// be re-imported via <see cref="TryImportFrom"/>.
        /// </summary>
        /// <param name="path">Full destination file path for the exported XML.</param>
        /// <param name="trades">The trade list to export.</param>
        public static void ExportTo(string path, List<FilterTradeConfig> trades)
        {
            var s = new AutoFiltersSettings { Trades = trades };
            var xs = new XmlSerializer(typeof(AutoFiltersSettings));
            using (var w = new StreamWriter(path)) xs.Serialize(w, s);
        }

        // ── Template store ────────────────────────────────────────────────────

        private static LemoineTemplateStore<List<FilterTradeConfig>>? _templateStore;

        /// <summary>
        /// Reusable template store for AutoFilters.
        /// Stores named snapshots of the full trade list in
        /// <c>%AppData%\LemoineTools\Templates\AutoFilters\</c>.
        /// </summary>
        public static LemoineTemplateStore<List<FilterTradeConfig>> Templates =>
            _templateStore ?? (_templateStore =
                new LemoineTemplateStore<List<FilterTradeConfig>>(
                    toolId:      "AutoFilters",
                    serialize:   (trades, path) => ExportTo(path, trades),
                    deserialize: path => TryLoadTrades(path)));

        /// <summary>
        /// Deserializes just the <see cref="FilterTradeConfig"/> list from an XML file
        /// written by <see cref="ExportTo"/>.  Returns <see langword="null"/> on failure.
        /// Applies the V2→V3 migration so old files work as templates too.
        /// </summary>
        public static List<FilterTradeConfig>? TryLoadTrades(string path)
        {
            var xs = new XmlSerializer(typeof(AutoFiltersSettings));
            using (var r = new StreamReader(path))
            {
                var s = (AutoFiltersSettings)xs.Deserialize(r)!;
                if (s.Trades == null) s.Trades = new List<FilterTradeConfig>();
                MigrateToV3(s);
                return s.Trades;
            }
        }

        /// <summary>Deep-copies the trade list via XML round-trip for safe live editing.</summary>
        public static List<FilterTradeConfig> DeepCopy(List<FilterTradeConfig> src)
        {
            if (src == null || src.Count == 0) return new List<FilterTradeConfig>();
            var xs = new XmlSerializer(typeof(AutoFiltersSettings));
            using (var ms = new System.IO.MemoryStream())
            {
                xs.Serialize(ms, new AutoFiltersSettings { Trades = src });
                ms.Position = 0;
                return ((AutoFiltersSettings)xs.Deserialize(ms)!).Trades
                    ?? new List<FilterTradeConfig>();
            }
        }

        /// <summary>
        /// Builds the default Trade → Category → Rule hierarchy.
        /// Colors and keywords match MepColorMap exactly.
        /// Trades: MD, MP, PL, MG, PT, EL, FP, ST.
        /// </summary>
        public static List<FilterTradeConfig> BuildDefaultTrades()
        {
            FilterRuleConfig R(string name, string[] match, string cut, string? surf,
                               string[] osts, string param, string matchType = "contains") =>
                new FilterRuleConfig
                {
                    Id                = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Enabled           = true,
                    Name              = name,
                    MatchType         = matchType,
                    Match             = new List<string>(match),
                    CutColor          = cut,
                    SurfColor         = surf ?? cut,
                    LinePattern       = "Solid",
                    LineWeight        = 1,
                    Visible           = true,
                    FilterOn          = true,
                    BuiltInCategories = new List<string>(osts),
                    Parameter         = param,
                };

            return new List<FilterTradeConfig>
            {
                // ── MD — Mechanical Duct ─────────────────────────────────────
                new FilterTradeConfig { Id="MD", Label="Mechanical Duct", Color="#005EBD",
                    Rules = new List<FilterRuleConfig>
                    {
                        R("Supply Air",   new[]{"Supply Air","Supply","SA"},       "#005EBD", null, new[]{"OST_DuctCurves","OST_DuctFitting","OST_DuctAccessory"}, "System Classification"),
                        R("Return Air",   new[]{"Return Air","Return","RA"},       "#FFAAAA", null, new[]{"OST_DuctCurves","OST_DuctFitting","OST_DuctAccessory"}, "System Classification"),
                        R("Outside Air",  new[]{"Outside Air","Outdoor Air","OA"}, "#E78F36", null, new[]{"OST_DuctCurves","OST_DuctFitting","OST_DuctAccessory"}, "System Classification"),
                        R("Exhaust Air",  new[]{"Exhaust Air","Exhaust","EA"},     "#815681", null, new[]{"OST_DuctCurves","OST_DuctFitting","OST_DuctAccessory"}, "System Classification"),
                        R("Fire Dampers", new[]{"Damper","Fire Damper"},           "#C81E1E", null, new[]{"OST_DuctAccessory"}, "System Classification"),
                        R("Duct Insulation", new[]{"Insulation","Insulated","INSUL"}, "#FFFFFF", null, new[]{"OST_DuctInsulations"}, "System Classification"),
                        R("VAV",      new[]{"VAV","Variable Air Volume","Variable Air"}, "#0084BD", null, new[]{"OST_MechanicalEquipment"}, "System Classification"),
                        R("Fan Coil", new[]{"Fan Coil","Cassette","FCU","AHU"},          "#53CFDF", null, new[]{"OST_MechanicalEquipment"}, "System Classification"),
                        R("Equipment",new[]{"Equipment","Equip"},                        "#B3B3B3", null, new[]{"OST_MechanicalEquipment"}, "System Classification"),
                    }
                },

                // ── MP — Mechanical Pipe ─────────────────────────────────────
                new FilterTradeConfig { Id="MP", Label="Mechanical Pipe", Color="#AAD4FF",
                    Rules = new List<FilterRuleConfig>
                    {
                        R("Chilled Water",   new[]{"Chilled Water","Chilled","CW","CHW"},   "#AAD4FF", null, new[]{"OST_PipeCurves","OST_PipeFitting","OST_PipeAccessory"}, "System Classification"),
                        R("Hydronic Supply", new[]{"Hydronic Supply","HW Supply","HWS"},    "#DC7832", null, new[]{"OST_PipeCurves","OST_PipeFitting","OST_PipeAccessory"}, "System Classification"),
                        R("Hydronic Return", new[]{"Hydronic Return","HW Return","HWR"},    "#78C8FF", null, new[]{"OST_PipeCurves","OST_PipeFitting","OST_PipeAccessory"}, "System Classification"),
                        R("Heated Water",    new[]{"Heated Water","Hot Water","HW"},        "#FFAABF", null, new[]{"OST_PipeCurves","OST_PipeFitting","OST_PipeAccessory"}, "System Classification"),
                        R("Refrigerant",     new[]{"Refrigerant","Refrigeration","REFR"},   "#FFAA55", null, new[]{"OST_PipeCurves","OST_PipeFitting","OST_PipeAccessory"}, "System Classification"),
                        R("Condensate",      new[]{"Condensate","Cond","CD"},               "#808080", null, new[]{"OST_PipeCurves","OST_PipeFitting","OST_PipeAccessory"}, "System Classification"),
                        R("Pipe Insulation", new[]{"Insulation","Insulated","INSUL"},       "#FFFFFF", null, new[]{"OST_PipeInsulations"}, "System Classification"),
                    }
                },

                // ── PL — Plumbing ────────────────────────────────────────────
                new FilterTradeConfig { Id="PL", Label="Plumbing", Color="#3FB3D9",
                    Rules = new List<FilterRuleConfig>
                    {
                        R("Dom Water Cold",  new[]{"Domestic Cold Water","Cold Water","DCW","DWS C"},            "#3FB3D9", null, new[]{"OST_PipeCurves","OST_PipeFitting"}, "System Classification"),
                        R("Dom Water Hot",   new[]{"Domestic Hot Water","Hot Water","DHW","DWS H"},              "#D93F3F", null, new[]{"OST_PipeCurves","OST_PipeFitting"}, "System Classification"),
                        R("Dom Water",       new[]{"Domestic Water","Domestic","DWS"},                          "#C68C53", null, new[]{"OST_PipeCurves","OST_PipeFitting"}, "System Classification"),
                        R("Grease Waste",    new[]{"Grease Waste","Grease Sewer","GW"},                         "#36E600", null, new[]{"OST_PipeCurves","OST_PipeFitting"}, "System Classification"),
                        R("Vent Pipe",       new[]{"Sanitary Vent","Grease Vent","Vent Pipe","Vent","PV"},       "#94A857", null, new[]{"OST_PipeCurves","OST_PipeFitting"}, "System Classification"),
                        R("Sanitary",        new[]{"Sanitary Waste","Sanitary Sewer","Sanitary","Sewer","Sump"}, "#1F8100", null, new[]{"OST_PipeCurves","OST_PipeFitting"}, "System Classification"),
                        R("Storm Primary",   new[]{"Storm Primary","Primary Drain","Storm Drain","Roof Drain","RD P","RD"}, "#EAAAFF", null, new[]{"OST_PipeCurves","OST_PipeFitting"}, "System Classification"),
                        R("Storm Secondary", new[]{"Storm Secondary","Secondary Drain","Overflow","RD S"},       "#BD7EAD", null, new[]{"OST_PipeCurves","OST_PipeFitting"}, "System Classification"),
                    }
                },

                // ── MG — Med & Natural Gas ───────────────────────────────────
                new FilterTradeConfig { Id="MG", Label="Medical & Natural Gas", Color="#00BD8D",
                    Rules = new List<FilterRuleConfig>
                    {
                        R("Med Gas",     new[]{"Medical Gas","Med Gas","Medical Air","Medical Oxygen","MA","MO"}, "#00BD8D", null, new[]{"OST_PipeCurves"}, "System Classification"),
                        R("Natural Gas", new[]{"Natural Gas","Gas Line","Gas","NAT GAS"},                         "#FFC61A", null, new[]{"OST_PipeCurves"}, "System Classification"),
                    }
                },

                // ── PT — Pneumatic Tube ──────────────────────────────────────
                new FilterTradeConfig { Id="PT", Label="Pneumatic Tube", Color="#96D9BA",
                    Rules = new List<FilterRuleConfig>
                    {
                        R("Pneumatic Tube", new[]{"Pneumatic","Pneumatic Tube","Tube"}, "#96D9BA", null, new[]{"OST_PipeCurves"}, "System Classification"),
                    }
                },

                // ── EL — Electrical ──────────────────────────────────────────
                new FilterTradeConfig { Id="EL", Label="Electrical", Color="#F4F406",
                    Rules = new List<FilterRuleConfig>
                    {
                        // Cable tray / conduit carry no reliably-resolvable system parameter,
                        // so colour the whole category ("all") rather than by keyword.
                        R("Cable Tray",    System.Array.Empty<string>(), "#F4F406", null, new[]{"OST_CableTray","OST_CableTrayFitting"}, "Type Name", "all"),
                        R("Conduit",       System.Array.Empty<string>(), "#F4F406", null, new[]{"OST_Conduit","OST_ConduitFitting"}, "Type Name", "all"),
                        R("Equipment",     new[]{"ELEC","Electrical","Panel","Switchboard"},        "#F4F406", null, new[]{"OST_ElectricalEquipment"}, "System Classification"),
                        R("Lighting",      new[]{"Lighting","Light","ELEC"},                        "#F4F406", null, new[]{"OST_LightingFixtures"}, "System Classification"),
                    }
                },

                // ── FP — Fire Protection ─────────────────────────────────────
                new FilterTradeConfig { Id="FP", Label="Fire Protection", Color="#C81414",
                    Rules = new List<FilterRuleConfig>
                    {
                        R("Sprinkler", new[]{"Fire Protection","Sprinkler","FIRE","Wet","Dry"}, "#C81414", null, new[]{"OST_PipeCurves","OST_Sprinklers"}, "System Classification"),
                    }
                },

                // ── ST — Structural ──────────────────────────────────────────
                new FilterTradeConfig { Id="ST", Label="Structural", Color="#9F382D",
                    Rules = new List<FilterRuleConfig>
                    {
                        R("Steel Framing",    new[]{"Steel","Structural Steel","Metal"},  "#9F382D", null, new[]{"OST_StructuralFraming","OST_StructuralColumns"}, "Structural Material"),
                        R("Concrete Framing", new[]{"Concrete","CMU","CONC"},             "#B9B9B9", null, new[]{"OST_StructuralFraming","OST_StructuralColumns"}, "Structural Material"),
                        R("Wood Framing",     new[]{"Wood","Lumber","Timber"},             "#C49C66", null, new[]{"OST_StructuralFraming","OST_StructuralColumns"}, "Structural Material"),
                    }
                },
            };
        }

    }
}
