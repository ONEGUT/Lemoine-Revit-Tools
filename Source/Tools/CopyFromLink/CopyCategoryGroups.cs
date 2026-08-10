using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Framework;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Tools.CopyFromLink
{
    /// <summary>
    /// One source of truth for how the copy tools group Revit categories into discipline tabs.
    /// Replaces the two byte-identical <c>DisciplineOf</c> / <c>BuildCategoryGroups</c> pairs that
    /// lived in <see cref="CopyFromLinkViewModel"/> and <see cref="CopyLinearViewModel"/>.
    ///
    /// The old classifier tested four narrow substring lists and returned "Architectural / Other"
    /// for everything else, so the Architectural tab became a catch-all — 49 of the 95 fallback
    /// categories landed there, and with a document open (where the picker loads every filterable
    /// Model category Revit reports, bridge/infrastructure families included) it grew past 100.
    /// It also mis-routed: <c>OST_BridgeCables</c> matched the bare "Cable" needle and was filed
    /// under Electrical.
    ///
    /// Here the mapping is an explicit OST_* allowlist per discipline, with an ordered substring
    /// pass as a *secondary* fallback so an uncatalogued category still lands sensibly. Anything
    /// still unmatched goes to "Other" — deliberately, <b>Architectural has no catch-all</b>.
    /// </summary>
    public static class CopyCategoryGroups
    {
        // ── Tab labels ────────────────────────────────────────────────────────
        // Hardcoded, not AppStrings: these key the grouping dictionary and the selection
        // round-trip, so they are logic tokens rather than display-only text (CLAUDE.md).
        private const string GArch    = "Architectural";
        private const string GStruct  = "Structural";
        private const string GMech    = "Mechanical";
        private const string GPipe    = "Piping";
        private const string GElec    = "Electrical";
        private const string GSite    = "Site & Civil";
        private const string GSpatial = "Spatial";
        private const string GBridge  = "Bridge & Infrastructure";
        private const string GDatum   = "Datums & Reference";
        private const string GOther   = "Other";

        /// <summary>
        /// Non-Model categories the copy tools surface on top of Revit's Model set. Kept here
        /// rather than widened into <c>AutoFiltersSettings.AllowedNonModelCategories</c>, so Auto
        /// Filters' model-element pickers are unaffected (CLAUDE.md forbids widening that list
        /// except on an explicit request — this request covered the copy tools only).
        ///
        /// Scope Boxes and Reference Planes are non-view-specific model-extent elements, so they
        /// copy through the same document→document <c>CopyElements</c> overload the tools already
        /// use. Note there is no <c>OST_ReferencePlanes</c> — reference planes are OST_CLines.
        /// </summary>
        private static readonly BuiltInCategory[] ExtraNonModelCategories =
        {
            BuiltInCategory.OST_VolumeOfInterest,   // Scope Boxes
            BuiltInCategory.OST_CLines,             // Reference Planes
        };

        // Display names for the two extras in the no-document fallback map. With a document open
        // the capture reads Revit's own Category.Name and these are not used.
        private static readonly Dictionary<string, string> ExtraFallbackEntries =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Scope Boxes",      "OST_VolumeOfInterest" },
            { "Reference Planes", "OST_CLines" },
        };

        // ── Document snapshot ─────────────────────────────────────────────────
        private static IReadOnlyDictionary<string, string>? _snapshot;

        /// <summary>
        /// Reads the filterable-category list for the copy tools' pickers. Must be called on the
        /// Revit main thread (the launch command) before the window's STA thread builds the step
        /// content — same capture pattern as <c>BrowserTreeCapture</c>. On failure the snapshot is
        /// left alone and <see cref="CategoryMap"/> keeps returning the hardcoded fallback.
        /// </summary>
        public static void Capture(Document doc)
        {
            if (doc == null) return;
            var map = AutoFiltersSettings.CaptureCategoryMap(doc, ExtraNonModelCategories);
            if (map == null || map.Count == 0)
            {
                DiagnosticsLog.Warn("CopyCategoryGroups.Capture",
                    "No filterable categories captured — the picker falls back to the hardcoded list.");
                return;
            }
            _snapshot = map;
        }

        /// <summary>
        /// Category display name → OST_* string for the copy pickers. The document-captured
        /// snapshot when available, otherwise the shared fallback plus the two extra datum
        /// categories.
        /// </summary>
        public static IReadOnlyDictionary<string, string> CategoryMap
        {
            get
            {
                if (_snapshot != null) return _snapshot;
                var merged = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in AutoFiltersSettings.KnownCategoryMap) merged[kv.Key] = kv.Value;
                foreach (var kv in ExtraFallbackEntries)
                    if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
                return merged;
            }
        }

        /// <summary>
        /// Discipline tab → sorted category display names, for <c>MultiSelectTabs.SetGroups</c>.
        /// Empty disciplines are omitted so the picker never shows a tab with nothing in it.
        /// (SetGroups auto-sorts the tabs alphabetically and pins "Other" last.)
        /// </summary>
        public static Dictionary<string, List<string>> BuildGroups()
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var kv in CategoryMap)
            {
                string disc = DisciplineOf(kv.Value);
                if (!groups.TryGetValue(disc, out var list)) groups[disc] = list = new List<string>();
                list.Add(kv.Key);
            }
            foreach (var list in groups.Values)
                list.Sort(NaturalOrderComparer.OrdinalIgnoreCase);
            return groups;
        }

        /// <summary>
        /// The discipline tab an OST_* category belongs to. Explicit allowlist first, ordered
        /// substring rules second, <see cref="GOther"/> last — never Architectural by default.
        /// </summary>
        public static string DisciplineOf(string ost)
        {
            if (string.IsNullOrEmpty(ost)) return GOther;
            if (Explicit.TryGetValue(ost, out var disc)) return disc;

            bool C(params string[] needles)
                => needles.Any(n => ost.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);

            // Ordered — earlier rules win. Bridge runs first so OST_BridgeCables can never be
            // pulled into Electrical, and the fabrication families are claimed by Mechanical /
            // Piping before "Fabric*" could read as reinforcement.
            if (C("Bridge", "Abutment", "Pier", "ApproachSlab"))                    return GBridge;
            if (C("Duct", "MechanicalEquipment", "MechanicalControl",
                  "AirTerminal", "HVAC"))                                           return GMech;
            if (C("Pipe", "Plumbing", "Sprinkler", "FireProtection",
                  "FabricationHangers", "FabricationContainment"))                  return GPipe;
            if (C("CableTray", "Conduit", "Electrical", "Lighting", "Wire",
                  "Communication", "FireAlarm", "SecurityDevice", "DataDevice",
                  "TelephoneDevice", "NurseCall", "AudioVisual"))                   return GElec;
            if (C("Structural", "StructConnection", "Rebar", "Rein", "Tendon",
                  "Coupler"))                                                       return GStruct;
            if (C("Site", "Topo", "Planting", "Parking", "Road", "Hardscape",
                  "BuildingPad", "Entourage"))                                      return GSite;
            if (C("Room", "MEPSpace", "Zone"))                                      return GSpatial;

            // No Architectural fallback on purpose — an unrecognised category is "Other", which is
            // honest, instead of silently inflating the Architectural tab.
            return GOther;
        }

        // ── Explicit allowlist ────────────────────────────────────────────────
        // Every OST_* string below was validated against the BuiltInCategory enum read from
        // libs/RevitAPI.dll metadata — not string-searched (CLAUDE.md Research Discipline).
        private static readonly Dictionary<string, string> Explicit = Build();

        private static Dictionary<string, string> Build()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(string disc, params string[] osts)
            {
                foreach (var o in osts) d[o] = disc;
            }

            Add(GArch,
                "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings", "OST_Doors", "OST_Windows",
                "OST_Stairs", "OST_StairsRailing", "OST_Ramps", "OST_Railings", "OST_Columns",
                "OST_CurtainWallPanels", "OST_CurtainWallMullions", "OST_Curtain_Systems",
                "OST_Cornices", "OST_EdgeSlab", "OST_Gutter", "OST_Fascia", "OST_RoofSoffit",
                "OST_Furniture", "OST_FurnitureSystems", "OST_Casework", "OST_SpecialityEquipment",
                "OST_GenericModel", "OST_Mass", "OST_MassFloor", "OST_Parts", "OST_Assemblies",
                "OST_Signage", "OST_VerticalCirculation", "OST_MedicalEquipment",
                "OST_FoodServiceEquipment", "OST_TemporaryStructure", "OST_ShaftOpening");

            Add(GStruct,
                "OST_StructuralFraming", "OST_StructuralColumns", "OST_StructuralFoundation",
                "OST_StructuralTruss", "OST_StructuralStiffener", "OST_StructConnections",
                "OST_Rebar", "OST_AreaRein", "OST_PathRein", "OST_FabricReinforcement",
                "OST_FabricAreas", "OST_Coupler", "OST_StructuralTendons");

            Add(GMech,
                "OST_DuctCurves", "OST_DuctFitting", "OST_DuctAccessory", "OST_DuctInsulations",
                "OST_DuctLinings", "OST_DuctTerminal", "OST_FlexDuctCurves",
                "OST_MechanicalEquipment", "OST_MechanicalControlDevices",
                "OST_FabricationDuctwork", "OST_HVAC_Zones");

            Add(GPipe,
                "OST_PipeCurves", "OST_PipeFitting", "OST_PipeAccessory", "OST_PipeInsulations",
                "OST_FlexPipeCurves", "OST_PlumbingFixtures", "OST_PlumbingEquipment",
                "OST_Sprinklers", "OST_FireProtection", "OST_FabricationPipework",
                "OST_FabricationHangers", "OST_FabricationContainment");

            Add(GElec,
                "OST_CableTray", "OST_CableTrayFitting", "OST_Conduit", "OST_ConduitFitting",
                "OST_ElectricalEquipment", "OST_ElectricalFixtures", "OST_LightingFixtures",
                "OST_LightingDevices", "OST_Wire", "OST_AudioVisualDevices",
                "OST_CommunicationDevices", "OST_DataDevices", "OST_FireAlarmDevices",
                "OST_SecurityDevices", "OST_TelephoneDevices", "OST_NurseCallDevices");

            Add(GSite,
                "OST_Site", "OST_Topography", "OST_Toposolid", "OST_Planting", "OST_Entourage",
                "OST_Parking", "OST_Roads", "OST_Hardscape", "OST_BuildingPad", "OST_SiteProperty",
                "OST_Alignments");

            Add(GSpatial, "OST_Rooms", "OST_Areas", "OST_MEPSpaces");

            Add(GDatum, "OST_Grids", "OST_Levels", "OST_VolumeOfInterest", "OST_CLines");

            return d;
        }
    }
}
