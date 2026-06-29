using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LemoineTools.Tools.ModifyElements
{
    internal static class CategoryDisciplineHelper
    {
        private static readonly string[] _order =
            { "Architecture", "Structure", "Mechanical", "Electrical", "Plumbing", "Site", "Other", "Annotation" };

        private static readonly Dictionary<int, string> _map = new Dictionary<int, string>
        {
            // Architecture
            { (int)BuiltInCategory.OST_Walls,                "Architecture" },
            { (int)BuiltInCategory.OST_Doors,                "Architecture" },
            { (int)BuiltInCategory.OST_Windows,              "Architecture" },
            { (int)BuiltInCategory.OST_Floors,               "Architecture" },
            { (int)BuiltInCategory.OST_Ceilings,             "Architecture" },
            { (int)BuiltInCategory.OST_Roofs,                "Architecture" },
            { (int)BuiltInCategory.OST_Stairs,               "Architecture" },
            { (int)BuiltInCategory.OST_StairsRailing,        "Architecture" },
            { (int)BuiltInCategory.OST_Ramps,                "Architecture" },
            { (int)BuiltInCategory.OST_Columns,              "Architecture" },
            { (int)BuiltInCategory.OST_Rooms,                "Architecture" },
            { (int)BuiltInCategory.OST_GenericModel,         "Architecture" },
            { (int)BuiltInCategory.OST_Furniture,            "Architecture" },
            { (int)BuiltInCategory.OST_FurnitureSystems,     "Architecture" },
            { (int)BuiltInCategory.OST_Casework,             "Architecture" },
            { (int)BuiltInCategory.OST_Entourage,            "Architecture" },
            { (int)BuiltInCategory.OST_Planting,             "Architecture" },
            { (int)BuiltInCategory.OST_Mass,                 "Architecture" },
            { (int)BuiltInCategory.OST_CurtainWallPanels,    "Architecture" },
            { (int)BuiltInCategory.OST_CurtainWallMullions,  "Architecture" },
            { (int)BuiltInCategory.OST_Parts,                "Architecture" },
            { (int)BuiltInCategory.OST_Assemblies,           "Architecture" },
            { (int)BuiltInCategory.OST_SpecialityEquipment,  "Architecture" },

            // Structure
            { (int)BuiltInCategory.OST_StructuralColumns,    "Structure" },
            { (int)BuiltInCategory.OST_StructuralFraming,    "Structure" },
            { (int)BuiltInCategory.OST_StructuralFoundation, "Structure" },
            { (int)BuiltInCategory.OST_StructuralTruss,      "Structure" },
            { (int)BuiltInCategory.OST_StructuralStiffener,  "Structure" },
            { (int)BuiltInCategory.OST_Rebar,                "Structure" },
            { (int)BuiltInCategory.OST_FabricReinforcement,  "Structure" },
            { (int)BuiltInCategory.OST_AreaRein,             "Structure" },
            { (int)BuiltInCategory.OST_PathRein,             "Structure" },

            // Mechanical
            { (int)BuiltInCategory.OST_MechanicalEquipment,  "Mechanical" },
            { (int)BuiltInCategory.OST_DuctCurves,           "Mechanical" },
            { (int)BuiltInCategory.OST_DuctFitting,          "Mechanical" },
            { (int)BuiltInCategory.OST_DuctAccessory,        "Mechanical" },
            { (int)BuiltInCategory.OST_FlexDuctCurves,       "Mechanical" },
            { (int)BuiltInCategory.OST_DuctInsulations,      "Mechanical" },
            { (int)BuiltInCategory.OST_DuctLinings,          "Mechanical" },

            // Electrical
            { (int)BuiltInCategory.OST_ElectricalEquipment,  "Electrical" },
            { (int)BuiltInCategory.OST_ElectricalFixtures,   "Electrical" },
            { (int)BuiltInCategory.OST_CableTray,            "Electrical" },
            { (int)BuiltInCategory.OST_CableTrayFitting,     "Electrical" },
            { (int)BuiltInCategory.OST_Conduit,              "Electrical" },
            { (int)BuiltInCategory.OST_ConduitFitting,       "Electrical" },
            { (int)BuiltInCategory.OST_LightingFixtures,     "Electrical" },
            { (int)BuiltInCategory.OST_LightingDevices,      "Electrical" },
            { (int)BuiltInCategory.OST_DataDevices,          "Electrical" },
            { (int)BuiltInCategory.OST_FireAlarmDevices,     "Electrical" },
            { (int)BuiltInCategory.OST_NurseCallDevices,     "Electrical" },
            { (int)BuiltInCategory.OST_SecurityDevices,      "Electrical" },
            { (int)BuiltInCategory.OST_TelephoneDevices,     "Electrical" },
            { (int)BuiltInCategory.OST_CommunicationDevices, "Electrical" },

            // Plumbing
            { (int)BuiltInCategory.OST_PipeAccessory,        "Plumbing" },
            { (int)BuiltInCategory.OST_PipeCurves,           "Plumbing" },
            { (int)BuiltInCategory.OST_PipeFitting,          "Plumbing" },
            { (int)BuiltInCategory.OST_PlumbingFixtures,     "Plumbing" },
            { (int)BuiltInCategory.OST_PipeInsulations,      "Plumbing" },
            { (int)BuiltInCategory.OST_FlexPipeCurves,       "Plumbing" },
            { (int)BuiltInCategory.OST_Sprinklers,           "Plumbing" },

            // Site
            { (int)BuiltInCategory.OST_Site,                 "Site" },
            { (int)BuiltInCategory.OST_Topography,           "Site" },
            { (int)BuiltInCategory.OST_Roads,                "Site" },
            { (int)BuiltInCategory.OST_Parking,              "Site" },
        };

        internal static string GetModelDiscipline(int bicValue) =>
            _map.TryGetValue(bicValue, out string? d) ? d : "Other";

        /// <summary>
        /// Groups elements by discipline. Annotation CategoryType → "Annotation" bucket.
        /// Result is ordered Architecture → Structure → Mechanical → Electrical → Plumbing → Site → Other → Annotation.
        /// Only populated groups are included.
        /// </summary>
        internal static Dictionary<string, List<string>> GroupByDiscipline(IEnumerable<Element> elements)
        {
            var raw = new Dictionary<string, HashSet<string>>(System.StringComparer.Ordinal);

            foreach (var el in elements)
            {
                var cat = el.Category;
                if (cat?.Name == null) continue;

                string group = cat.CategoryType == CategoryType.Annotation
                    ? "Annotation"
                    : GetModelDiscipline((int)cat.Id.Value);

                if (!raw.ContainsKey(group))
                    raw[group] = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                raw[group].Add(cat.Name);
            }

            var result = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);
            foreach (string discipline in _order)
            {
                if (raw.TryGetValue(discipline, out var names))
                    result[discipline] = names.OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase).ToList();
            }
            // Safety net for any group not in _order
            foreach (var kvp in raw)
            {
                if (!result.ContainsKey(kvp.Key))
                    result[kvp.Key] = kvp.Value.OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase).ToList();
            }

            return result;
        }
    }
}
