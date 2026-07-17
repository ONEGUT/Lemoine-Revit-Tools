using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.Tools.Dimensioning;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// Read-model + edit logic for the web Clash Definitions library (HTML analogue of
    /// <see cref="ClashDefinitionsWindow"/> + <see cref="ClashGroupEditor"/>). Holds a deep-copied,
    /// editable buffer of the saved definitions and applies every change straight onto it; the
    /// window persists on close via a dirty check (<see cref="IsDirty"/> / <see cref="Save"/>).
    /// The filter-rule and category display maps are built once (Revit-free, from
    /// <see cref="AutoFiltersSettings"/>) and shared by both groups, exactly as the WPF editor does.
    /// </summary>
    public sealed class WebClashDefinitions
    {
        private readonly List<ClashDocInfo> _docs;
        private readonly List<string>       _lineStyleNames;
        private readonly List<string>       _hostPhaseNames;

        private List<ClashDefinition> _defs;
        private string?               _activeId;
        private readonly string       _snapshot;

        // Filter rule maps (display <-> persist "{tradeId}::{ruleId}") and grouped-by-trade list.
        private readonly Dictionary<string, string>       _ruleDisplayToPersist = new Dictionary<string, string>();
        private readonly Dictionary<string, string>       _rulePersistToDisplay = new Dictionary<string, string>();
        private readonly Dictionary<string, List<string>> _ruleGroups           = new Dictionary<string, List<string>>();

        // Category maps (display <-> OST) and grouped-by-discipline list.
        private readonly Dictionary<string, string>       _catDisplayToOst = new Dictionary<string, string>();
        private readonly Dictionary<string, string>       _catOstToDisplay = new Dictionary<string, string>();
        private readonly Dictionary<string, List<string>> _catGroups       = new Dictionary<string, List<string>>();

        public WebClashDefinitions(List<string> lineStyleNames, List<ClashDocInfo> docs, List<string> hostPhaseNames)
        {
            _lineStyleNames = lineStyleNames ?? new List<string>();
            _docs           = docs ?? new List<ClashDocInfo>();
            _hostPhaseNames = hostPhaseNames ?? new List<string>();

            BuildRuleMaps();
            BuildCategoryMaps();

            _defs     = ClashDefinitionsSettings.DeepCopy(ClashDefinitionsSettings.Instance.Definitions);
            _snapshot = Serialize(_defs);
            _activeId = _defs.Count > 0 ? _defs[0].Id : null;
        }

        // -- Persistence (auto-save on close, dirty check) -----------------------
        public bool IsDirty => Serialize(_defs) != _snapshot;

        public void Save()
        {
            if (!IsDirty) return;
            ClashDefinitionsSettings.Instance.Definitions = _defs;
            ClashDefinitionsSettings.Instance.Save();
        }

        // -- Payload -------------------------------------------------------------
        public Dictionary<string, object?> BuildPayload()
        {
            var active = _defs.Find(d => d.Id == _activeId);
            return new Dictionary<string, object?>
            {
                ["title"]       = AppStrings.T("clashDefinitions.window.title"),
                ["addPill"]     = AppStrings.T("clashDefinitions.window.addPill"),
                ["definitions"] = _defs.Select(d => new Dictionary<string, object?>
                {
                    ["id"]   = d.Id,
                    ["name"] = string.IsNullOrWhiteSpace(d.Name) ? AppStrings.T("clashDefinitions.window.unnamed") : d.Name,
                }).ToList(),
                ["activeId"] = _activeId,
                ["editor"]   = active == null ? null : BuildEditor(active),
                ["labels"]   = Labels(),
            };
        }

        private Dictionary<string, object?> BuildEditor(ClashDefinition d) => new Dictionary<string, object?>
        {
            ["name"]    = d.Name,
            ["group1"]  = BuildGroup(d.Group1),
            ["group2"]  = BuildGroup(d.Group2),
            ["marking"] = new Dictionary<string, object?>
            {
                ["tolerance"]     = d.ToleranceMm,
                ["maxClashes"]    = d.MaxClashes,
                ["fillStyle"]     = d.FillStyle,
                ["dimTarget"]     = d.DimTarget,
                ["phaseMode"]     = d.PhaseMode,
                ["specificPhase"] = _hostPhaseNames.Contains(d.SpecificPhaseName)
                    ? d.SpecificPhaseName : (_hostPhaseNames.Count > 0 ? _hostPhaseNames[_hostPhaseNames.Count - 1] : ""),
                ["hostPhases"]    = _hostPhaseNames.ToList(),
                ["crossLine"]     = _lineStyleNames.Contains(d.CrossLineTypeName) ? d.CrossLineTypeName : "",
                ["lineStyles"]    = _lineStyleNames.ToList(),
                ["fallbackColor"] = d.FallbackColorHex,
            },
        };

        private Dictionary<string, object?> BuildGroup(ClashGroupSpec g)
        {
            // Selected docs: explicit SourceLinkIds, or all when unset ("scan all").
            var selected = (g.SourceLinkIds != null && g.SourceLinkIds.Count > 0)
                ? new HashSet<long>(g.SourceLinkIds)
                : new HashSet<long>(_docs.Select(x => x.LinkInstId));
            var excludedByDoc = (g.WorksetFilters ?? new List<ClashWorksetFilter>())
                .Where(f => f != null && f.ExcludedWorksetIds != null)
                .ToDictionary(f => f.LinkInstId, f => new HashSet<int>(f.ExcludedWorksetIds));

            var docs = _docs.Select(dc =>
            {
                bool docSel = selected.Contains(dc.LinkInstId);
                excludedByDoc.TryGetValue(dc.LinkInstId, out var ex);
                return new Dictionary<string, object?>
                {
                    ["linkId"]      = dc.LinkInstId.ToString(),
                    ["name"]        = dc.Name,
                    ["selected"]    = docSel,
                    ["hasWorksets"] = (dc.Worksets?.Count ?? 0) > 0,
                    ["worksets"]    = (dc.Worksets ?? new List<ClashWorksetInfo>()).Select(ws => new Dictionary<string, object?>
                    {
                        ["id"]       = ws.Id,
                        ["name"]     = ws.Name,
                        ["included"] = docSel && (ex == null || !ex.Contains(ws.Id)),
                        ["disabled"] = !docSel,
                    }).ToList(),
                };
            }).ToList();

            // Rules: persist keys -> display strings for the tab picker.
            var ruleSelected = (g.RuleKeys ?? new List<string>())
                .Select(pk => _rulePersistToDisplay.TryGetValue(pk, out var dk) ? dk : null)
                .Where(dk => dk != null).ToList();
            // Categories: OST -> display.
            var catSelected = (g.Categories ?? new List<string>())
                .Select(ost => _catOstToDisplay.TryGetValue(ost, out var dp) ? dp : null)
                .Where(dp => dp != null).ToList();

            return new Dictionary<string, object?>
            {
                ["mode"] = g.Mode,
                ["docs"] = docs,
                ["rules"] = new Dictionary<string, object?>
                {
                    ["groups"]   = _ruleGroups.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                    ["selected"] = ruleSelected,
                },
                ["categories"] = new Dictionary<string, object?>
                {
                    ["groups"]    = _catGroups.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                    ["hierarchy"] = AutoFiltersSettings.CategorySubcategories?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.ToList()),
                    ["selected"]  = catSelected,
                },
                ["elements"] = new Dictionary<string, object?> { ["count"] = (g.ElemIds ?? new List<long>()).Count },
            };
        }

        // -- Actions from the page -----------------------------------------------
        public void AddDefinition()
        {
            var def = ClashDefinition.NewBlank();
            _defs.Add(def);
            _activeId = def.Id;
        }

        public void DuplicateDefinition(string id)
        {
            var src = _defs.Find(d => d.Id == id);
            if (src == null) return;
            var copy = ClashDefinitionsSettings.DeepCopy(src);
            copy.Id   = "C" + Guid.NewGuid().ToString("N").Substring(0, 7);
            copy.Name = string.IsNullOrWhiteSpace(src.Name)
                ? AppStrings.T("clashDefinitions.window.copyDefault")
                : src.Name + AppStrings.T("clashDefinitions.window.copySuffix");
            int idx = _defs.FindIndex(d => d.Id == id);
            _defs.Insert(idx + 1, copy);
            _activeId = copy.Id;
        }

        public void DeleteDefinition(string id)
        {
            int idx = _defs.FindIndex(d => d.Id == id);
            if (idx < 0) return;
            _defs.RemoveAt(idx);
            if (_activeId == id)
                _activeId = _defs.Count > 0 ? _defs[Math.Min(idx, _defs.Count - 1)].Id : null;
        }

        public void SelectDefinition(string id) => _activeId = id;

        public void SetName(string text)
        {
            var d = Active(); if (d == null) return;
            d.Name = string.IsNullOrWhiteSpace(text) ? AppStrings.T("clashDefinitions.window.unnamed") : text.Trim();
        }

        public void SetGroupMode(int group, string mode)
        {
            var g = Group(group); if (g == null) return;
            g.Mode = mode == "Categories" ? "Categories" : mode == "Elements" ? "Elements" : "Rules";
        }

        public void SetGroupRules(int group, IEnumerable<string> displays)
        {
            var g = Group(group); if (g == null) return;
            g.RuleKeys = (displays ?? Enumerable.Empty<string>())
                .Select(dk => _ruleDisplayToPersist.TryGetValue(dk, out var pk) ? pk : null)
                .Where(pk => pk != null).Cast<string>().ToList();
        }

        public void SetGroupCategories(int group, IEnumerable<string> displays)
        {
            var g = Group(group); if (g == null) return;
            g.Categories = (displays ?? Enumerable.Empty<string>())
                .Select(dp => _catDisplayToOst.TryGetValue(dp, out var ost) ? ost : null)
                .Where(ost => ost != null).Cast<string>().ToList();
        }

        public void ToggleDoc(int group, long linkId, bool on)
        {
            var g = Group(group); if (g == null) return;
            var sel = new HashSet<long>((g.SourceLinkIds != null && g.SourceLinkIds.Count > 0)
                ? g.SourceLinkIds : _docs.Select(x => x.LinkInstId));
            if (on) sel.Add(linkId); else sel.Remove(linkId);
            g.SourceLinkIds = sel.OrderBy(x => x).ToList();
        }

        public void ToggleWorkset(int group, long linkId, int wsId, bool included)
        {
            var g = Group(group); if (g == null) return;
            var filters = g.WorksetFilters ?? new List<ClashWorksetFilter>();
            var f = filters.FirstOrDefault(x => x.LinkInstId == linkId);
            var set = f != null ? new HashSet<int>(f.ExcludedWorksetIds ?? new List<int>()) : new HashSet<int>();
            if (included) set.Remove(wsId); else set.Add(wsId);

            filters = filters.Where(x => x.LinkInstId != linkId).ToList();
            if (set.Count > 0)
                filters.Add(new ClashWorksetFilter { LinkInstId = linkId, ExcludedWorksetIds = set.OrderBy(i => i).ToList() });
            g.WorksetFilters = filters;
        }

        public void ClearElements(int group)
        {
            var g = Group(group); if (g == null) return;
            g.ElemIds = new List<long>();
            g.ElemLinkIds = new List<long>();
        }

        /// <summary>Append picked (linkId, elemId) pairs to the group's element list (dedup).</summary>
        public void AddPickedElements(int group, IEnumerable<(long linkId, long elemId)> picks)
        {
            var g = Group(group); if (g == null) return;
            var ids   = g.ElemIds     ?? new List<long>();
            var links = g.ElemLinkIds ?? new List<long>();
            foreach (var p in picks)
            {
                bool exists = false;
                for (int i = 0; i < ids.Count; i++)
                    if (ids[i] == p.elemId && (i < links.Count ? links[i] : 0L) == p.linkId) { exists = true; break; }
                if (!exists) { ids.Add(p.elemId); links.Add(p.linkId); }
            }
            g.ElemIds = ids; g.ElemLinkIds = links;
        }

        public void SetMarking(string field, object? value)
        {
            var d = Active(); if (d == null) return;
            switch (field)
            {
                case "tolerance":     d.ToleranceMm       = ToDouble(value); break;
                case "maxClashes":    d.MaxClashes        = (int)Math.Round(ToDouble(value)); break;
                case "fillStyle":     d.FillStyle         = ToStr(value); break;
                case "dimTarget":     d.DimTarget         = ToStr(value); break;
                case "phaseMode":     d.PhaseMode         = ToStr(value); break;
                case "specificPhase": d.SpecificPhaseName = ToStr(value); break;
                case "crossLine":     d.CrossLineTypeName = ToStr(value); break;
                case "fallbackColor": d.FallbackColorHex  = ToStr(value); break;
            }
        }

        // -- Mapping builders (ported from ClashGroupEditor) ---------------------
        private void BuildRuleMaps()
        {
            var trades = AutoFiltersSettings.Instance.Trades;
            var nameCount = new Dictionary<string, int>();
            foreach (var trade in trades)
                foreach (var rule in trade.Rules)
                    nameCount[rule.Name] = nameCount.TryGetValue(rule.Name, out var c) ? c + 1 : 1;

            foreach (var trade in trades)
            {
                var items = new List<string>();
                foreach (var rule in trade.Rules)
                {
                    string display = nameCount.TryGetValue(rule.Name, out int cnt) && cnt > 1
                        ? $"{trade.Label} " + char.ConvertFromUtf32(0x2014) + $" {rule.Name}" : rule.Name;
                    string persist = $"{trade.Id}::{rule.Id}";
                    _ruleDisplayToPersist[display] = persist;
                    _rulePersistToDisplay[persist] = display;
                    items.Add(display);
                }
                if (items.Count > 0) _ruleGroups[trade.Label] = items;
            }
        }

        private void BuildCategoryMaps()
        {
            foreach (var kv in AutoFiltersSettings.KnownCategoryMap)
            {
                string display = kv.Key, ost = kv.Value, disc = DisciplineOf(ost);
                if (!_catGroups.ContainsKey(disc)) _catGroups[disc] = new List<string>();
                _catGroups[disc].Add(display);
                _catDisplayToOst[display] = ost;
                _catOstToDisplay[ost]     = display;
            }
            foreach (var k in _catGroups.Keys.ToList())
                _catGroups[k].Sort(StringComparer.OrdinalIgnoreCase);
        }

        private static string DisciplineOf(string ost)
        {
            bool C(params string[] needles) => needles.Any(n => ost.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            if (C("Duct", "MechanicalEquipment", "FlexDuct", "AirTerminal")) return "Mechanical";
            if (C("Pipe", "Plumbing", "Sprinkler", "FlexPipe", "FabricationPipe",
                  "FabricationHangers", "FabricationContainment")) return "Piping";
            if (C("Cable", "Conduit", "Electrical", "Lighting", "Communication",
                  "FireAlarm", "Security", "Data", "Telephone", "NurseCall")) return "Electrical";
            if (C("Structural", "Rebar", "Reinforcement", "Fabric")) return "Structural";
            if (C("OST_Walls", "Floor", "Roof", "Ceiling", "OST_Doors", "OST_Windows",
                  "Stair", "Ramp", "OST_Columns", "OST_Rooms", "GenericModel",
                  "Furniture", "Casework", "Entourage", "Planting", "OST_Mass",
                  "Curtain", "OST_Parts", "OST_Assemblies", "SpecialityEquipment",
                  "Cornices", "EdgeSlab")) return "Architectural";
            return "Other";
        }

        // -- Helpers -------------------------------------------------------------
        private ClashDefinition? Active() => _defs.Find(d => d.Id == _activeId);
        private ClashGroupSpec?  Group(int group)
        {
            var d = Active(); if (d == null) return null;
            return group == 2 ? d.Group2 : d.Group1;
        }

        private static double ToDouble(object? v) =>
            v is double d ? d : v is long l ? l : v is int i ? i :
            v is string s && double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0.0;
        private static string ToStr(object? v) => v as string ?? v?.ToString() ?? "";

        private static string Serialize(List<ClashDefinition> defs)
        {
            if (defs == null) return "";
            try
            {
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(List<ClashDefinition>));
                using (var sw = new System.IO.StringWriter()) { xs.Serialize(sw, defs); return sw.ToString(); }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("WebClashDefinitions.Serialize", ex);
                return Guid.NewGuid().ToString(); // treat as dirty -> save
            }
        }

        private static Dictionary<string, object?> Labels() => new Dictionary<string, object?>
        {
            ["unnamed"]        = AppStrings.T("clashDefinitions.window.unnamed"),
            ["dupTooltip"]     = AppStrings.T("clashDefinitions.window.dupTooltip"),
            ["delTooltip"]     = AppStrings.T("clashDefinitions.window.delTooltip"),
            ["dupGlyph"]       = char.ConvertFromUtf32(0xE8C8), // Segoe MDL2: Copy
            ["delGlyph"]       = char.ConvertFromUtf32(0xE74D), // Segoe MDL2: Delete
            ["noSelection"]    = AppStrings.T("clashDefinitions.window.noSelection"),
            ["name"]           = AppStrings.T("clashDefinitions.labels.name"),
            ["group1Header"]   = AppStrings.T("clashDefinitions.labels.group1Header"),
            ["group2Header"]   = AppStrings.T("clashDefinitions.labels.group2Header"),
            ["markingSettings"]= AppStrings.T("clashDefinitions.labels.markingSettings"),
            ["tolerance"]      = AppStrings.T("clashDefinitions.labels.tolerance"),
            ["toleranceDesc"]  = AppStrings.T("clashDefinitions.labels.toleranceDesc"),
            ["maxClashes"]     = AppStrings.T("clashDefinitions.labels.maxClashes"),
            ["maxClashesDesc"] = AppStrings.T("clashDefinitions.labels.maxClashesDesc"),
            ["fillStyle"]      = AppStrings.T("clashDefinitions.labels.fillStyle"),
            ["markerReference"]= AppStrings.T("clashDefinitions.labels.markerReference"),
            ["phase"]          = AppStrings.T("clashDefinitions.labels.phase"),
            ["phaseDesc"]      = AppStrings.T("clashDefinitions.labels.phaseDesc"),
            ["hostPhase"]      = AppStrings.T("clashDefinitions.labels.hostPhase"),
            ["noHostPhases"]   = AppStrings.T("clashDefinitions.labels.noHostPhases"),
            ["crossLineStyle"] = AppStrings.T("clashDefinitions.labels.crossLineStyle"),
            ["lineDefault"]    = AppStrings.T("clashDefinitions.labels.lineDefault"),
            ["fallbackColor"]  = AppStrings.T("clashDefinitions.labels.fallbackColor"),
            ["modeLabel"]      = "Group definition mode",
            ["modeRules"]      = "Filter Rules",
            ["modeCategories"] = "Categories",
            ["modeElements"]   = "Select Elements",
            ["sourceDocs"]     = "Source documents (which models this group scans)",
            ["sourceDocsDesc"] = "Check a model to scan it. Expand its caret to include or exclude individual worksets.",
            ["noDocs"]         = "No documents available.",
            ["rulesEmpty"]     = "No Auto Filters rules configured. Switch to Categories or Select Elements, or set up Auto Filters first.",
            ["catsEmpty"]      = "No categories available.",
            ["pickHost"]       = "+ Pick host elements",
            ["pickLinks"]      = "+ Pick linked elements",
            ["clear"]          = "Clear",
            ["elemNone"]       = "No elements picked yet. Use the buttons above to pick in the model.",
            ["elemCount"]      = "{0} element(s) picked.",
            ["fillSolid"]      = "Solid",
            ["fillOutline"]    = "Outline",
            ["targetEdge"]     = "Edge",
            ["targetCentre"]   = "Centre",
            ["phaseAll"]       = AppStrings.T("clashDefinitions.phase.all"),
            ["phaseMatch"]     = AppStrings.T("clashDefinitions.phase.match"),
            ["phaseSpecific"]  = AppStrings.T("clashDefinitions.phase.specific"),
        };
    }
}
