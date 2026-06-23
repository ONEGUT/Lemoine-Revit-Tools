using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using LemoineTools.Lemoine;

namespace LemoineNavisworks.SearchSets
{
    // =========================================================================
    // NavisSearchSets — all Navisworks API for the Discover -> Search Sets tool
    // lives here, so the surface that needs verifying against the real DLL is in
    // one place. Calls marked "// ⚠ verify" are the ones whose exact name/shape
    // is most likely to need a tweak on first compile against Autodesk.Navisworks.Api.
    //
    // Everything runs on Navisworks' main thread (plugin/tool code does), so the
    // API can be called directly — no ExternalEvent equivalent is needed.
    // =========================================================================
    internal static class NavisSearchSets
    {
        public sealed class ValueCount
        {
            public string Value { get; }
            public int    Count { get; }
            public ValueCount(string value, int count) { Value = value; Count = count; }
        }

        public enum WriteResult { Created, Updated, Failed }

        // ── All model items (optionally filtered to a set of model display names) ──
        private static IEnumerable<ModelItem> AllItems(Document doc, ISet<string>? modelNames)
        {
            if (doc == null || doc.IsClear) yield break;

            for (int i = 0; i < doc.Models.Count; i++)
            {
                Model model = doc.Models[i];
                if (modelNames != null && !modelNames.Contains(ModelName(model, i))) continue;

                // DescendantsAndSelf enumerates the whole sub-tree of this model.   // ⚠ verify
                foreach (ModelItem item in model.RootItem.DescendantsAndSelf)
                    yield return item;
            }
        }

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

        // ── Sample the document for available property categories + properties ────
        // Capped so opening the tool stays responsive on large federated models.
        public static Dictionary<string, List<string>> DiscoverCategoryProperties(Document doc, int sampleCap = 4000)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (doc == null || doc.IsClear) return map;

            int seen = 0;
            foreach (ModelItem item in AllItems(doc, null))
            {
                if (++seen > sampleCap) break;
                try
                {
                    foreach (PropertyCategory cat in item.PropertyCategories)
                    {
                        string catName = cat.DisplayName ?? "";
                        if (string.IsNullOrWhiteSpace(catName)) continue;

                        if (!map.TryGetValue(catName, out var props))
                        {
                            props = new List<string>();
                            map[catName] = props;
                        }
                        foreach (DataProperty p in cat.Properties)
                        {
                            string pn = p.DisplayName ?? "";
                            if (!string.IsNullOrWhiteSpace(pn) && !props.Contains(pn))
                                props.Add(pn);
                        }
                    }
                }
                catch (Exception ex) { LemoineLog.Swallowed("NavisSearchSets: sample item props", ex); }
            }

            foreach (var key in map.Keys.ToList())
                map[key].Sort(StringComparer.OrdinalIgnoreCase);
            return map;
        }

        // ── Distinct display values of one category.property across scope ─────────
        public static List<ValueCount> ScanDistinctValues(
            Document doc, ISet<string> modelNames, string category, string property)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (ModelItem item in AllItems(doc, modelNames))
            {
                string? val = ReadPropertyDisplay(item, category, property);
                if (string.IsNullOrWhiteSpace(val)) continue;
                counts[val!] = counts.TryGetValue(val!, out int c) ? c + 1 : 1;
            }

            return counts
                .Select(kv => new ValueCount(kv.Key, kv.Value))
                .OrderByDescending(v => v.Count)
                .ThenBy(v => v.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string? ReadPropertyDisplay(ModelItem item, string category, string property)
        {
            // Find the category, then the property, by display name.               // ⚠ verify both finders
            PropertyCategory? cat = item.PropertyCategories.FindCategoryByDisplayName(category);
            if (cat == null) return null;

            DataProperty? prop = cat.Properties.FindPropertyByDisplayName(property);
            if (prop == null) return null;

            try { return prop.Value.ToDisplayString(); }                            // ⚠ verify (VariantData.ToDisplayString)
            catch (Exception ex) { LemoineLog.Swallowed("NavisSearchSets: read value", ex); return null; }
        }

        // ── Create or update a search set (update-by-name, never duplicate) ───────
        public static WriteResult CreateOrUpdateSearchSet(
            Document doc, string name, string category, string property, string value, bool contains)
        {
            try
            {
                var search = new Search();
                search.Selection.SelectAll();                                       // ⚠ verify (scope = whole doc)
                search.Locations = SearchLocations.DescendantsAndSelf;              // ⚠ verify enum

                SearchCondition cond = SearchCondition
                    .HasPropertyByDisplayName(category, property);                  // ⚠ verify
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

                doc.SelectionSets.AddCopy(set);                                     // ⚠ verify
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
            // Root-level saved items (v1 is flat; folder nesting comes later).
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
