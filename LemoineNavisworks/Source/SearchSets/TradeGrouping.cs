using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LemoineNavisworks.SearchSets
{
    // =========================================================================
    // TradeGrouping — collapses loaded files that are the same trade split by
    // level/zone into one logical group (e.g. "DT-Mech-L01", "DT-Mech-L02",
    // "DT-Mech-Roof" -> "DT Mech"). Discovery then scans every file in a group
    // together, since same-trade files share most unique property values.
    //
    // Auto-derived from file names, then fully editable: each file's group name
    // is just a string, so re-assigning a file = changing its group string, and
    // two files sharing a string are in the same group.
    // =========================================================================
    internal sealed class TradeGrouping
    {
        // Tokens that indicate a level/zone split rather than the trade itself.
        private static readonly Regex LevelZoneToken = new Regex(
            @"^(l|lv|lvl|lev|level|fl|flr|floor|story|storey|"
          + @"roof|attic|basement|bsmt|ground|grd|gnd|underground|ug|slab|podium|"
          + @"mezz|mezzanine|penthouse|ph|cellar|sub|"
          + @"area|zone|z|sector|sec|phase|ph|bldg|building)"
          + @"[\s\-_]*[0-9a-z]{0,4}$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PureNumberOrLevelCode = new Regex(
            @"^[0-9]{1,3}[a-z]?$|^b[0-9]$|^[0-9]{1,2}(st|nd|rd|th)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly char[] Separators = { '-', '_', ' ', '.', '+' };

        // fileIndex -> group name
        public Dictionary<int, string> FileGroup { get; } = new Dictionary<int, string>();
        public IReadOnlyList<string> FileNames { get; }

        private TradeGrouping(IReadOnlyList<string> fileNames) { FileNames = fileNames; }

        public static TradeGrouping Derive(IReadOnlyList<string> fileNames)
        {
            var g = new TradeGrouping(fileNames);
            for (int i = 0; i < fileNames.Count; i++)
                g.FileGroup[i] = TradeKey(fileNames[i]);
            return g;
        }

        // Best-effort trade name from a file name: drop the extension, split on
        // separators, strip trailing level/zone/number tokens, keep the rest.
        public static string TradeKey(string fileName)
        {
            string baseName;
            try { baseName = System.IO.Path.GetFileNameWithoutExtension(fileName) ?? fileName; }
            catch { baseName = fileName ?? ""; }
            if (string.IsNullOrWhiteSpace(baseName)) return "Ungrouped";

            var tokens = baseName.Split(Separators, StringSplitOptions.RemoveEmptyEntries).ToList();

            // Strip level/zone/number tokens from the END only (prefix is the trade).
            int end = tokens.Count;
            while (end > 1)
            {
                string t = tokens[end - 1];
                if (LevelZoneToken.IsMatch(t) || PureNumberOrLevelCode.IsMatch(t)) end--;
                else break;
            }

            var kept = tokens.Take(end).ToList();
            if (kept.Count == 0) kept = tokens;   // never strip everything

            string key = string.Join(" ", kept).Trim();
            return string.IsNullOrWhiteSpace(key) ? baseName : key;
        }

        public void SetGroup(int fileIndex, string group)
        {
            FileGroup[fileIndex] = string.IsNullOrWhiteSpace(group) ? "Ungrouped" : group.Trim();
        }

        public List<string> Groups() =>
            FileGroup.Values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToList();

        public HashSet<int> FilesIn(string group)
        {
            var set = new HashSet<int>();
            foreach (var kv in FileGroup)
                if (string.Equals(kv.Value, group, StringComparison.OrdinalIgnoreCase))
                    set.Add(kv.Key);
            return set;
        }

        public string Summary()
        {
            var groups = Groups();
            if (groups.Count == 0) return "No files loaded.";
            var parts = groups.Select(g => $"{g} ({FilesIn(g).Count})");
            return $"{groups.Count} group(s): " + string.Join(", ", parts);
        }
    }
}
