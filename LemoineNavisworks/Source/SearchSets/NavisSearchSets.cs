using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using LemoineTools.Lemoine;

namespace LemoineNavisworks.SearchSets
{
    // =========================================================================
    // NavisSearchSets — all Navisworks API for Discover -> Search Sets.
    //
    // Key design decisions (driven by Navisworks' deep, heterogeneous trees):
    //   • Discover from GEOMETRY items (ModelItem.HasGeometry), not branch nodes —
    //     the real element/composite properties live at the bottom of the tree.
    //   • Key categories/properties on their INTERNAL names (NamedConstant.Name),
    //     which are stable across file types; display names are labels only.
    //   • Operate per set of model indices (a "trade group" = several files).
    //
    // Calls most likely to need a tweak against the real DLL are tagged "// ⚠ verify".
    // Everything runs on Navisworks' main thread, so the API is called directly.
    // =========================================================================
    internal static class NavisSearchSets
    {
        // ── Result/DTO types ────────────────────────────────────────────────────
        public sealed class ValueCount
        {
            public string Value { get; }
            public int    Count { get; }
            public ValueCount(string value, int count) { Value = value; Count = count; }
        }

        public sealed class PropertyRef
        {
            public string CategoryInternal { get; }
            public string CategoryDisplay  { get; }
            public string PropertyInternal { get; }
            public string PropertyDisplay  { get; }

            public PropertyRef(string ci, string cd, string pi, string pd)
            { CategoryInternal = ci; CategoryDisplay = cd; PropertyInternal = pi; PropertyDisplay = pd; }

            public string Key => CategoryInternal + "	" + PropertyInternal;
        }

        public enum WriteResult { Created, Updated, Failed }

        // ── Model names ─────────────────────────────────────────────────────────
        public static string ModelName(Model model, int index)
        {
            try
            {
                string n = model.FileName;                                          // ⚠ verify (Model.FileName)
                if (!string.IsNullOrWhiteSpace(n))
                    return System.IO.Path.GetFileName(n);
            }
            catch (Exception ex) { LemoineLog.Swallowed("NavisSearchSets: model name", ex); }
            return "Model " + (index + 1);
        }

        public static List<string> ModelNames(Document doc)
        {
            var names = new List<string>();
            if (doc == null || doc.IsClear) return names;
            for (int i = 0; i < doc.Models.Count; i++)
                names.Add(ModelName(doc.Models[i], i));
            return names;
        }

        // ── Geometry items across the chosen models ──────────────────────────────
        private static IEnumerable<ModelItem> GeometryItems(Document doc, ISet<int>? modelIndices)
        {
            if (doc == null || doc.IsClear) yield break;

            for (int i = 0; i < doc.Models.Count; i++)
            {
                if (modelIndices != null && !modelIndices.Contains(i)) continue;
                Model model = doc.Models[i];

                foreach (ModelItem item in model.RootItem.DescendantsAndSelf)       // ⚠ verify
                {
                    if (SafeHasGeometry(item)) yield return item;
                }
            }
        }

        private static bool SafeHasGeometry(ModelItem item)
        {
            try { return item.HasGeometry; }                                        // ⚠ verify (ModelItem.HasGeometry)
            catch { return false; }
        }

        // ── Discover available properties (internal-name keyed) ──────────────────
        public static List<PropertyRef> DiscoverProperties(Document doc, ISet<int> modelIndices, int cap = 6000)
        {
            var byKey = new Dictionary<string, PropertyRef>(StringComparer.Ordinal);
            int seen = 0;

            foreach (ModelItem item in GeometryItems(doc, modelIndices))
            {
                if (++seen > cap) break;
                try
                {
                    foreach (PropertyCategory cat in item.PropertyCategories)
                    {
                        string ci = cat.Name?.Name ?? "";                           // ⚠ verify (PropertyCategory.Name -> NamedConstant.Name)
                        if (string.IsNullOrEmpty(ci)) continue;
                        string cd = cat.DisplayName ?? ci;

                        foreach (DataProperty p in cat.Properties)
                        {
                            string pi = p.Name?.Name ?? "";                         // ⚠ verify (DataProperty.Name -> NamedConstant.Name)
                            if (string.IsNullOrEmpty(pi)) continue;
                            string pd = p.DisplayName ?? pi;

                            string key = ci + "	" + pi;
                            if (!byKey.ContainsKey(key))
                                byKey[key] = new PropertyRef(ci, cd, pi, pd);
                        }
                    }
                }
                catch (Exception ex) { LemoineLog.Swallowed("NavisSearchSets: discover item props", ex); }
            }

            return byKey.Values
                .OrderBy(p => p.CategoryDisplay, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.PropertyDisplay, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ── Distinct display values of one property across the chosen models ─────
        public static List<ValueCount> ScanDistinctValues(
            Document doc, ISet<int> modelIndices, PropertyRef prop, int cap = 400000)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int seen = 0;

            foreach (ModelItem item in GeometryItems(doc, modelIndices))
            {
                if (++seen > cap) break;
                string? v = ReadValueInternal(item, prop.CategoryInternal, prop.PropertyInternal);
                if (string.IsNullOrWhiteSpace(v)) continue;
                counts[v!] = counts.TryGetValue(v!, out int c) ? c + 1 : 1;
            }

            return counts
                .Select(kv => new ValueCount(kv.Key, kv.Value))
                .OrderByDescending(v => v.Count)
                .ThenBy(v => v.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Read a property value by INTERNAL category/property name (manual match —
        // avoids relying on a specific finder overload).
        private static string? ReadValueInternal(ModelItem item, string catInternal, string propInternal)
        {
            try
            {
                foreach (PropertyCategory cat in item.PropertyCategories)
                {
                    if (!string.Equals(cat.Name?.Name, catInternal, StringComparison.Ordinal)) continue;
                    foreach (DataProperty p in cat.Properties)
                    {
                        if (!string.Equals(p.Name?.Name, propInternal, StringComparison.Ordinal)) continue;
                        try { return p.Value.ToDisplayString(); }                   // ⚠ verify (VariantData.ToDisplayString)
                        catch (Exception ex) { LemoineLog.Swallowed("NavisSearchSets: value", ex); return null; }
                    }
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("NavisSearchSets: read item", ex); }
            return null;
        }

        // ── Create or update a search set (update-by-name, never duplicate) ──────
        public static WriteResult CreateOrUpdateSearchSet(
            Document doc, string name, PropertyRef prop, string value, bool contains)
        {
            try
            {
                var search = new Search();
                search.Selection.SelectAll();                                       // ⚠ verify (scope = whole doc)
                search.Locations = SearchLocations.DescendantsAndSelf;              // ⚠ verify enum

                var catNC  = new NamedConstant(prop.CategoryInternal, prop.CategoryDisplay);  // ⚠ verify ctor
                var propNC = new NamedConstant(prop.PropertyInternal, prop.PropertyDisplay);
                SearchCondition cond = SearchCondition.HasPropertyByName(catNC, propNC);      // ⚠ verify
                cond = contains
                    ? cond.DisplayStringContains(value)                            // ⚠ verify
                    : cond.EqualValue(VariantData.FromDisplayString(value));       // ⚠ verify
                search.SearchConditions.Add(cond);

                var set = new SelectionSet(search) { DisplayName = name };

                int existing = FindRootIndexByName(doc, name);
                if (existing >= 0)
                {
                    doc.SelectionSets.ReplaceWithCopy(existing, set);              // ⚠ verify signature
                    return WriteResult.Updated;
                }

                doc.SelectionSets.AddCopy(set);
                return WriteResult.Created;
            }
            catch (Exception ex)
            {
                LemoineLog.Error("NavisSearchSets: create/update '" + name + "'", ex);
                return WriteResult.Failed;
            }
        }

        private static int FindRootIndexByName(Document doc, string name)
        {
            // DocumentSelectionSets exposes the root collection as .Value.
            var roots = doc.SelectionSets.Value;
            for (int i = 0; i < roots.Count; i++)
            {
                if (string.Equals(roots[i].DisplayName, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }
    }
}
