using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LemoineTools.Framework.Naming;
using LemoineTools.Tools.ScopeBoxes;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// Revit-free view model for the web Scope Box Manager (HTML analogue of
    /// <see cref="ScopeBoxManagerWindow"/>). Holds the latest <see cref="ManagerScanResult"/>
    /// plus the UI's selection / filter / checked state, builds the <c>init</c> payload the page
    /// renders from (sidebar, active-box editor, overlay option-data, labels from
    /// <see cref="AppStrings"/>), and resolves bulk-rename names via <see cref="TokenResolver"/>.
    /// It never touches the Revit API — <see cref="WebScopeBoxManagerWindow"/> owns the scan / run
    /// ExternalEvents and feeds results in via <see cref="SetScan"/>.
    /// </summary>
    public sealed class WebScopeBoxManager
    {
        internal const string RenameToolId        = "scopeBoxes.managerRename";
        internal const string RenameDefaultPattern = "{CurrentName}";

        private static readonly TokenDefinition[] RenameComputedTokens =
        {
            new TokenDefinition("Number",
                AppStrings.T("naming.computed.scopeBoxManagerRename.number.label"),
                TokenOrigin.Computed, TokenSubject.Target, TokenEntity.Any),
        };

        private ManagerScanResult? _scan;
        private long   _activeBoxId = -1;
        private string _filterMode  = "All";   // "All" | "Used" | "Unused" (logic tokens)
        private string _status      = "";

        private readonly HashSet<long> _checkedBoxes  = new HashSet<long>();
        private readonly HashSet<long> _checkedViews  = new HashSet<long>();
        private readonly HashSet<long> _checkedDatums = new HashSet<long>();

        private static string T(string key, params object[] args) =>
            AppStrings.T("scopeBoxes.manager." + key, args);

        // ── State fed from the window ─────────────────────────────────────────
        public void SetScan(ManagerScanResult result)
        {
            _scan = result;
            var known = new HashSet<long>(result.Boxes.Select(b => b.Id.Value));
            _checkedBoxes.RemoveWhere(id => !known.Contains(id));
            if (!known.Contains(_activeBoxId))
                _activeBoxId = result.Boxes.FirstOrDefault()?.Id.Value ?? -1;
            _checkedViews.Clear();
            _checkedDatums.Clear();
        }

        public void SetStatus(string status) => _status = status ?? "";

        // ── Pure UI mutations (window re-sends init after these) ──────────────
        public void SetFilter(string mode)
        {
            if (mode == "Used" || mode == "Unused") _filterMode = mode; else _filterMode = "All";
        }

        public void SelectBox(long id)
        {
            _activeBoxId = id;
            _checkedViews.Clear();
            _checkedDatums.Clear();
        }

        public void ToggleBox(long id, bool on)   { if (on) _checkedBoxes.Add(id);  else _checkedBoxes.Remove(id); }
        public void ToggleView(long id, bool on)  { if (on) _checkedViews.Add(id);  else _checkedViews.Remove(id); }
        public void ToggleDatum(long id, bool on) { if (on) _checkedDatums.Add(id); else _checkedDatums.Remove(id); }

        // ── Reads used by the window when configuring a run ───────────────────
        public long ActiveBoxId => _activeBoxId;
        public ScopeBoxUsage? ActiveBox() => _scan?.Boxes.FirstOrDefault(b => b.Id.Value == _activeBoxId);
        public IReadOnlyCollection<long> CheckedBoxIds => _checkedBoxes;

        public List<long> UnusedBoxIds() =>
            _scan?.Boxes.Where(b => b.IsUnused).Select(b => b.Id.Value).ToList() ?? new List<long>();

        // ── Bulk rename ───────────────────────────────────────────────────────
        public string RenamePattern => NamingPatternStore.Instance.GetOrDefault(RenameToolId, RenameDefaultPattern);
        public void SetRenamePattern(string pattern) => NamingPatternStore.Instance.Set(RenameToolId, pattern ?? "");

        /// <summary>Resolve the new name for every checked box, in list order (mirrors the WPF
        /// rename overlay: CurrentName + a 1-based two-digit Number, degenerate-guarded).</summary>
        public List<(long BoxId, string NewName)> ResolveRenameNames(string pattern)
        {
            var targets = CheckedBoxes();
            var pairs = new List<(long, string)>(targets.Count);
            for (int i = 0; i < targets.Count; i++)
            {
                var b = targets[i];
                var ctx = new TokenContext();
                ctx.Computed["CurrentName"] = b.Name;
                ctx.Computed["Number"]      = (i + 1).ToString("00", CultureInfo.InvariantCulture);
                string resolved = TokenResolver.Resolve(pattern ?? "", ctx);
                string name     = TokenResolver.GuardDegenerate(resolved, ctx, b.Name, null);
                pairs.Add((b.Id.Value, name));
            }
            return pairs;
        }

        private List<ScopeBoxUsage> CheckedBoxes() =>
            _scan?.Boxes.Where(b => _checkedBoxes.Contains(b.Id.Value)).ToList() ?? new List<ScopeBoxUsage>();

        private IEnumerable<ScopeBoxUsage> FilteredBoxes()
        {
            if (_scan == null) return Enumerable.Empty<ScopeBoxUsage>();
            switch (_filterMode)
            {
                case "Used":   return _scan.Boxes.Where(b => !b.IsUnused);
                case "Unused": return _scan.Boxes.Where(b => b.IsUnused);
                default:       return _scan.Boxes;
            }
        }

        // ── Payload ───────────────────────────────────────────────────────────
        public Dictionary<string, object?> BuildPayload()
        {
            int total  = _scan?.Boxes.Count ?? 0;
            int unused  = _scan?.Boxes.Count(b => b.IsUnused) ?? 0;
            var checkedCount = _checkedBoxes.Count;

            var boxes = FilteredBoxes().Select(b => new Dictionary<string, object?>
            {
                ["id"]      = b.Id.Value.ToString(CultureInfo.InvariantCulture),
                ["name"]    = b.Name,
                ["unused"]  = b.IsUnused,
                ["usage"]   = T("side.usage", b.Views.Count, b.Datums.Count),
                ["active"]  = b.Id.Value == _activeBoxId,
                ["checked"] = _checkedBoxes.Contains(b.Id.Value),
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["title"]            = T("toolbar.title"),
                ["labels"]           = Labels(),
                ["status"]           = _status,
                ["filterMode"]       = _filterMode,
                ["sideHeader"]       = T("side.header", total, unused),
                ["empty"]            = _scan == null || total == 0,
                ["boxes"]            = boxes,
                ["renameCount"]      = checkedCount,
                ["renameTitle"]      = T("overlay.renameTitle", checkedCount),
                ["renameTokenInput"] = BuildRenameTokenInput(),
                ["unusedIds"]        = UnusedBoxIds().Select(id => (object?)id.ToString(CultureInfo.InvariantCulture)).ToList(),
                ["unusedCount"]      = unused,
                ["deleteUnusedTitle"] = T("overlay.deleteUnusedTitle", unused),
                ["editor"]           = BuildEditor(),
            };
        }

        private Dictionary<string, object?>? BuildEditor()
        {
            var box = ActiveBox();
            if (box == null) return null;

            var views = box.Views
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .Select(v => new Dictionary<string, object?>
                {
                    ["id"]      = v.Id.Value.ToString(CultureInfo.InvariantCulture),
                    ["name"]    = v.Name,
                    ["tag"]     = v.TypeLabel,
                    ["checked"] = _checkedViews.Contains(v.Id.Value),
                }).ToList();

            var datums = box.Datums
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new Dictionary<string, object?>
                {
                    ["id"]      = d.Id.Value.ToString(CultureInfo.InvariantCulture),
                    ["name"]    = d.Name,
                    ["tag"]     = KindLabel(d.Kind),
                    ["checked"] = _checkedDatums.Contains(d.Id.Value),
                }).ToList();

            // Assign-views overlay: the project-browser tree pruned to croppable (eligible) views.
            object? treeInput = null;
            if (_scan?.Tree != null)
            {
                var eligible = new HashSet<long>(_scan.EligibleViews.Select(v => v.Id.Value));
                var pruned   = PruneTree(_scan.Tree, eligible);
                treeInput = WebInput.BrowserTree("assignViews", "", pruned,
                    selected: box.Views.Select(v => v.Id.Value)).ToPayload();
            }

            // Assign-datums overlay: only datums whose plane crosses the box (Revit rejects the
            // rest), grouped by kind. Keys embed the id so duplicate names stay distinct; the
            // page maps a selected key back to its id via assignDatumsMap.
            var eligibleDatums = _scan?.Datums.Where(d => d.IntersectsBox(box)).ToList()
                                 ?? new List<ManagerDatumRef>();
            var datumGroups = new Dictionary<string, List<string>>();
            var datumMap    = new Dictionary<string, object?>();
            var datumSelected = new List<string>();
            var selectedDatumIds = new HashSet<long>(box.Datums.Select(d => d.Id.Value));
            foreach (var d in eligibleDatums)
            {
                string grp = KindLabel(d.Kind);
                string key = d.Name + "  [" + d.Id.Value.ToString(CultureInfo.InvariantCulture) + "]";
                if (!datumGroups.TryGetValue(grp, out var list)) { list = new List<string>(); datumGroups[grp] = list; }
                list.Add(key);
                datumMap[key] = d.Id.Value.ToString(CultureInfo.InvariantCulture);
                if (selectedDatumIds.Contains(d.Id.Value)) datumSelected.Add(key);
            }
            object? datumsInput = eligibleDatums.Count == 0 ? null
                : WebInput.MultiSelectTabs("assignDatums", "", datumGroups, datumSelected).ToPayload();

            // Bind-sides overlay: grids classified by orientation (wide bbox = runs E-W, offered
            // for North/South; tall bbox = runs N-S, offered for East/West).
            var grids = _scan?.Datums.Where(d => d.Kind == "Grid" && d.HasBounds).ToList()
                        ?? new List<ManagerDatumRef>();
            var horizontal = grids.Where(d => (d.MaxX - d.MinX) >= (d.MaxY - d.MinY)).Select(GridEntry).ToList();
            var vertical   = grids.Where(d => (d.MaxX - d.MinX) <  (d.MaxY - d.MinY)).Select(GridEntry).ToList();

            // Split overlay: grids that cross the box, for the "at a gridline" mode.
            var crossing = (_scan?.Datums ?? new List<ManagerDatumRef>())
                .Where(d => d.Kind == "Grid" && d.IntersectsBox(box))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(GridEntry).ToList();

            return new Dictionary<string, object?>
            {
                ["id"]           = box.Id.Value.ToString(CultureInfo.InvariantCulture),
                ["name"]         = box.Name,
                ["size"]         = T("main.size", Fmt(box.WidthFt), Fmt(box.DepthFt), Fmt(box.HeightFt)),
                ["viewsHeader"]  = T("main.viewsHeader", box.Views.Count),
                ["datumsHeader"] = T("main.datumsHeader", box.Datums.Count),
                ["views"]        = views,
                ["datums"]       = datums,

                ["assignViewsTitle"]  = T("overlay.assignViewsTitle", box.Name),
                ["tree"]              = treeInput,
                ["assignDatumsTitle"] = T("overlay.assignDatumsTitle", box.Name),
                ["assignDatums"]      = datumsInput,
                ["assignDatumsMap"]   = datumMap,
                ["assignDatumsNone"]  = eligibleDatums.Count == 0,

                ["bindTitle"]         = T("overlay.bindSidesTitle", box.Name),
                ["bindHorizontal"]    = horizontal,
                ["bindVertical"]      = vertical,

                ["splitTitle"]        = T("overlay.splitTitle", box.Name),
                ["crossingGrids"]     = crossing,
                ["hasCrossingGrids"]  = crossing.Count > 0,

                ["deleteBoxTitle"]    = T("overlay.deleteBoxTitle", box.Name),
                ["deleteBoxLine"]     = box.IsUnused
                    ? T("overlay.deleteBoxUnused")
                    : T("overlay.deleteBoxInUse", box.Views.Count, box.Datums.Count),
            };
        }

        private static Dictionary<string, object?> GridEntry(ManagerDatumRef d) =>
            new Dictionary<string, object?>
            {
                ["id"]   = d.Id.Value.ToString(CultureInfo.InvariantCulture),
                ["name"] = d.Name,
            };

        private Dictionary<string, object?> BuildRenameTokenInput()
        {
            var first = CheckedBoxes().FirstOrDefault();
            var sample = new Dictionary<string, string>
            {
                ["CurrentName"] = first?.Name ?? "Scope Box",
                ["Number"]      = "01",
            };
            var tokens = NamingTokenRegistry.TokensFor(TokenEntity.Any, hasSource: false, RenameComputedTokens);
            return WebInput.TokenInput("renamePattern", "", RenamePattern, RenameDefaultPattern, tokens, sample).ToPayload();
        }

        private static string Fmt(double ft) => ft.ToString("0.#", CultureInfo.InvariantCulture);

        private static string KindLabel(string kind)
        {
            switch (kind)
            {
                case "Level":    return T("main.kindLevel");
                case "RefPlane": return T("main.kindRefPlane");
                default:         return T("main.kindGrid");
            }
        }

        // Prune the browser tree to leaves whose id is eligible (croppable views), keeping any
        // ancestor folder that still has a kept descendant. Mirrors WebToolBase.PruneTree.
        private static BrowserTree PruneTree(BrowserTree tree, HashSet<long> keepIds)
        {
            var pruned = new BrowserTree();
            foreach (var root in tree.Roots)
            {
                var copy = PruneNode(root, keepIds);
                if (copy != null) pruned.Roots.Add(copy);
            }
            return pruned;
        }

        private static BrowserNode? PruneNode(BrowserNode node, HashSet<long> keepIds)
        {
            var copy = new BrowserNode { Title = node.Title, Id = node.Id, IsSheet = node.IsSheet };
            foreach (var child in node.Children)
            {
                var kept = PruneNode(child, keepIds);
                if (kept != null) copy.Children.Add(kept);
            }
            bool selfEligible = node.Id.HasValue && keepIds.Contains(node.Id.Value);
            return (selfEligible || copy.Children.Count > 0) ? copy : null;
        }

        private Dictionary<string, object?> Labels() => new Dictionary<string, object?>
        {
            ["refresh"]        = T("toolbar.refresh"),
            ["refreshTip"]     = T("toolbar.refreshTooltip"),
            ["filterAll"]      = T("side.filterAll"),
            ["filterUsed"]     = T("side.filterUsed"),
            ["filterUnused"]   = T("side.filterUnused"),
            ["unusedBadge"]    = T("side.unusedBadge"),
            ["renameChecked"]  = T("side.renameChecked"),
            ["deleteUnused"]   = T("side.deleteUnused"),
            ["footNote"]       = T("side.footNote"),
            ["emptyBoxes"]     = T("side.empty"),

            ["noSelection"]    = T("main.noSelection"),
            ["deleteBox"]      = T("main.deleteBox"),
            ["duplicateBox"]   = T("main.duplicateBox"),
            ["bindSides"]      = T("main.bindSides"),
            ["splitBox"]       = T("main.splitBox"),
            ["assignViews"]    = T("main.assignViews"),
            ["clearChecked"]   = T("main.clearChecked"),
            ["viewsEmpty"]     = T("main.viewsEmpty"),
            ["viewsHint"]      = T("main.viewsHint"),
            ["assignDatums"]   = T("main.assignDatums"),
            ["datumsEmpty"]    = T("main.datumsEmpty"),
            ["datumsHint"]     = T("main.datumsHint"),

            ["cancel"]         = T("overlay.cancel"),
            ["applyAssign"]    = T("overlay.applyAssign"),
            ["applyRename"]    = T("overlay.applyRename"),
            ["applyDelete"]    = T("overlay.applyDelete"),
            ["applyBind"]      = T("overlay.applyBind"),
            ["applySplit"]     = T("overlay.applySplit"),
            ["assignDatumsHelp"] = T("overlay.assignDatumsHelp"),
            ["assignDatumsNone"] = T("overlay.assignDatumsNone"),
            ["bindNote"]       = T("overlay.bindNote"),
            ["sideNorth"]      = T("overlay.sideNorth"),
            ["sideSouth"]      = T("overlay.sideSouth"),
            ["sideEast"]       = T("overlay.sideEast"),
            ["sideWest"]       = T("overlay.sideWest"),
            ["sideKeep"]       = T("overlay.sideKeep"),
            ["splitNote"]      = T("overlay.splitNote"),
            ["splitModeGrid"]  = T("overlay.splitModeGrid"),
            ["splitModeMiddle"] = T("overlay.splitModeMiddle"),
            ["splitGridLabel"] = T("overlay.splitGridLabel"),
            ["splitAxisLabel"] = T("overlay.splitAxisLabel"),
            ["splitAxisNS"]    = T("overlay.splitAxisNS"),
            ["splitAxisEW"]    = T("overlay.splitAxisEW"),
            ["splitOverlap"]   = T("overlay.splitOverlap"),
            ["splitDeleteOriginal"] = T("overlay.splitDeleteOriginal"),
            ["splitNoGrid"]    = T("status.splitNoGrid"),
            ["nothingChecked"] = T("status.nothingChecked"),
            ["noUnused"]       = T("status.noUnused"),
        };
    }
}
