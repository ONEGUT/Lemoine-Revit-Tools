using System;
using System.Collections.Generic;
using LemoineTools.Framework.Naming;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // ZoneNamingTokens — the per-run values a zone contributes to a name.
    //
    // These are TokenOrigin.Computed definitions declared beside the tools that
    // use them and passed to NamingTokenRegistry.TokensFor as extraComputed —
    // deliberately NOT global registry entries, because a tool that has no zone
    // context must never be offered a token it cannot resolve.
    //
    // {SheetSize} and {SheetSuffix} exist for a specific, load-bearing reason.
    // A view can be placed on exactly ONE sheet in Revit, so documenting the
    // same area at two sheet sizes needs TWO views — and View.Name is unique
    // with a setter that THROWS on a duplicate. Without a token that varies by
    // layout, generating an A1 and an A3 set from one view def produces two views
    // wanting one name and the second one fails. ValidateDistinctAcrossLayouts
    // below is what turns that into a pre-run check instead of a mid-run throw.
    // =========================================================================
    public static class ZoneNamingTokens
    {
        public const string KeyBuilding    = "Building";
        public const string KeyLevel       = "Level";
        public const string KeyArea        = "Area";
        public const string KeyZoneCode    = "ZoneCode";
        public const string KeySheetSize   = "SheetSize";
        public const string KeySheetSuffix = "SheetSuffix";
        public const string KeyView        = "View";

        /// <summary>Zone tokens for a picker naming <paramref name="entity"/>.</summary>
        public static List<TokenDefinition> For(TokenEntity entity) => new List<TokenDefinition>
        {
            new TokenDefinition(KeyBuilding, "Building", TokenOrigin.Computed, TokenSubject.Target, entity,
                "The zone building's name."),
            new TokenDefinition(KeyLevel, "Level", TokenOrigin.Computed, TokenSubject.Target, entity,
                "The zone level's name."),
            new TokenDefinition(KeyArea, "Area", TokenOrigin.Computed, TokenSubject.Target, entity,
                "The zone area's name."),
            new TokenDefinition(KeyZoneCode, "Zone code", TokenOrigin.Computed, TokenSubject.Target, entity,
                "Building, level and area codes joined — the short form of the zone's address."),
            new TokenDefinition(KeySheetSize, "Sheet size", TokenOrigin.Computed, TokenSubject.Target, entity,
                "The layout's title block type. Include this when generating for more than one " +
                "sheet size, or the two runs produce views wanting the same name."),
            new TokenDefinition(KeySheetSuffix, "Sheet suffix", TokenOrigin.Computed, TokenSubject.Target, entity,
                "The sheet group's suffix, when a level needs more than one sheet at this size."),
            new TokenDefinition(KeyView, "View", TokenOrigin.Computed, TokenSubject.Target, entity,
                "The level view's name, e.g. Floor Plan or RCP."),
        };

        /// <summary>
        /// The values for one zone cell, ready to hand to <c>TokenResolver</c>.
        /// </summary>
        public static Dictionary<string, string> ValuesFor(
            ZoneLibrary? library, ZoneBuilding? building, ZoneLevel? level, ZoneArea? area,
            ZoneViewDef? view = null, ZoneSheetSet? layout = null, ZoneSheetGroup? group = null)
        {
            var v = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KeyBuilding]    = building?.Name ?? "",
                [KeyLevel]       = level?.Name    ?? "",
                [KeyArea]        = area?.Name     ?? "",
                [KeyZoneCode]    = ZoneCode(building, level, area),
                [KeySheetSize]   = layout?.TitleBlockTypeName ?? "",
                [KeySheetSuffix] = group?.Suffix  ?? "",
                [KeyView]        = view?.Name     ?? "",
            };
            return v;
        }

        /// <summary>
        /// Building + level + area codes, joined with "-", skipping any that are blank so a
        /// missing code never leaves a dangling separator. Falls back to a name when no code
        /// is set, because an empty zone code in a view name is worse than a long one.
        /// </summary>
        public static string ZoneCode(ZoneBuilding? building, ZoneLevel? level, ZoneArea? area)
        {
            var parts = new List<string>();
            void Add(string? code, string? name)
            {
                string s = !string.IsNullOrWhiteSpace(code) ? code! : (name ?? "");
                if (!string.IsNullOrWhiteSpace(s)) parts.Add(s.Trim());
            }
            Add(building?.Code, building?.Name);
            Add(level?.Code,    level?.Name);
            Add(area?.Code,     area?.Name);
            return string.Join("-", parts);
        }

        /// <summary>
        /// The name a view for this (level, area, view def, layout, group) gets.
        ///
        /// SHARED DELIBERATELY. The sheet builder finds the views to place BY NAME, so if it
        /// resolved names even slightly differently from the view builder it would silently
        /// find nothing and report every sheet as empty. One function, both callers.
        /// </summary>
        public static string ResolveViewName(
            ZoneViewDef view, ZoneBuilding? building, ZoneLevel level, ZoneArea area,
            ZoneSheetSet? layout, ZoneSheetGroup? group, Action<string>? onWarn = null)
        {
            var ctx = new TokenContext();
            foreach (var kv in ValuesFor(null, building, level, area, view, layout, group))
                ctx.Computed[kv.Key] = kv.Value;

            string pattern = string.IsNullOrWhiteSpace(view.NamePattern)
                ? "{Level} - {Area} - {View}"
                : view.NamePattern;

            string resolved = TokenResolver.Resolve(pattern, ctx, onWarn);
            string fallback = $"{level.Name} - {area.Name} - {view.Name}";
            return TokenResolver.GuardDegenerate(resolved, ctx, fallback, onWarn);
        }

        /// <summary>
        /// Sheet number and name for one (level, layout, group). Same sharing rationale as
        /// <see cref="ResolveViewName"/>: a sheet number is checked for uniqueness before the
        /// run starts, so the check and the write must agree exactly.
        /// </summary>
        public static void ResolveSheetName(
            ZoneSheetSet layout, ZoneBuilding? building, ZoneLevel level, ZoneSheetGroup? group,
            out string number, out string name, Action<string>? onWarn = null)
        {
            var ctx = new TokenContext();
            foreach (var kv in ValuesFor(null, building, level, null, null, layout, group))
                ctx.Computed[kv.Key] = kv.Value;

            string numPattern  = string.IsNullOrWhiteSpace(layout.SheetNumberPattern)
                ? "{ZoneCode}{SheetSuffix}" : layout.SheetNumberPattern;
            string namePattern = string.IsNullOrWhiteSpace(layout.SheetNamePattern)
                ? "{Level} - {Area}" : layout.SheetNamePattern;

            number = TokenResolver.GuardDegenerate(
                TokenResolver.Resolve(numPattern, ctx, onWarn), ctx,
                $"{level.Name}{group?.Suffix ?? ""}", onWarn);

            name = TokenResolver.GuardDegenerate(
                TokenResolver.Resolve(namePattern, ctx, onWarn), ctx,
                level.Name, onWarn);
        }

        /// <summary>One name a run intends to create, and where it came from.</summary>
        public sealed class PlannedName
        {
            public string Name     = "";
            public string AreaId   = "";
            public string LevelId  = "";
            public string ViewDefId = "";
            public string LayoutId = "";
            public string Describe => $"{Name}";
        }

        /// <summary>
        /// Checks that a run's intended view names are all distinct BEFORE anything is written.
        ///
        /// This exists because View.Name is unique in Revit and its setter throws on a
        /// duplicate. Generating the same area for two sheet sizes from a pattern with no
        /// distinguishing token yields two identical names, and without this check the failure
        /// arrives mid-run with a half-built set of views and sheets behind it.
        ///
        /// Returns the colliding groups; empty means the run is safe to start.
        /// </summary>
        public static List<List<PlannedName>> FindCollisions(IEnumerable<PlannedName>? planned)
        {
            var byName = new Dictionary<string, List<PlannedName>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in planned ?? new List<PlannedName>())
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Name)) continue;
                if (!byName.TryGetValue(p.Name, out var list))
                {
                    list = new List<PlannedName>();
                    byName[p.Name] = list;
                }
                list.Add(p);
            }

            var collisions = new List<List<PlannedName>>();
            foreach (var kv in byName)
                if (kv.Value.Count > 1) collisions.Add(kv.Value);
            return collisions;
        }

        /// <summary>
        /// True when <paramref name="pattern"/> carries something that varies per layout. A run
        /// spanning several sheet sizes needs one, or every layout produces the same names.
        /// </summary>
        public static bool VariesByLayout(string? pattern)
            => !string.IsNullOrEmpty(pattern) &&
               (pattern.IndexOf("{" + KeySheetSize + "}",   StringComparison.Ordinal) >= 0 ||
                pattern.IndexOf("{" + KeySheetSuffix + "}", StringComparison.Ordinal) >= 0);
    }
}
