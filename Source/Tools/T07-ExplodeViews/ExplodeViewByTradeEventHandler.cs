using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Tools.ExplodeViews
{
    /// <summary>
    /// Explodes one source 3D view into multiple 3D views — one per selected AutoFilters
    /// trade — at the identical camera angle and section box. Each output view isolates its
    /// trade by toggling the selected trades' view filters (the trade's own filters visible,
    /// the other selected trades' filters hidden). Output views are ordered by each trade's
    /// median element elevation (host + linked) so they read as a vertical stack, and are
    /// number-prefixed so the Project Browser sorts them top → bottom.
    ///
    /// This tool is a pure consumer of the AutoFilters trades/filters — it never creates or
    /// edits filter definitions. A trade whose ParameterFilterElements do not yet exist in the
    /// project is skipped and logged (run AutoFilters → Create Filters first).
    /// </summary>
    public class ExplodeViewByTradeEventHandler : IExternalEventHandler
    {
        // ── Inputs (set by ViewModel before Raise()) ──────────────────────────
        public ElementId     SourceViewId      { get; set; } = ElementId.InvalidElementId;
        public List<string>  SelectedTradeIds  { get; set; } = new List<string>();
        public bool          OrderByElevation  { get; set; } = true;
        public bool          NumberPrefix       { get; set; } = true;
        public bool          ApplyColorOverride { get; set; } = true;
        public string        NamePattern        { get; set; } = "{nn}_{Source} - {Trade}";

        // ── Callbacks (BeginInvoke-wrapped by StepFlowWindow) ─────────────────
        public Action<string, string>?            PushLog       { get; set; }
        public Action<int, int, int, int>?        OnProgress    { get; set; }
        public Action<int, int, int>?             OnComplete    { get; set; }
        public Action<IReadOnlyList<ResultChip>>? OnResultChips { get; set; }

        private int _viewsCreated, _tradesSkipped;

        public string GetName() => "LemoineTools.Tools.ExplodeViews.ExplodeViewByTradeEventHandler";

        // Per-trade resolved plan: its existing filters, plus the elevation scan result.
        private sealed class TradePlan
        {
            public string Id    = "";
            public string Label = "";
            public List<ParameterFilterElement> Filters = new List<ParameterFilterElement>();
            public int    ElemCount;
            public double MedianZ;   // world feet
            public double MinZ, MaxZ;
            public bool   HasElevation;  // true when at least one element contributed
        }

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            int pass = 0, fail = 0, skip = 0;
            _viewsCreated = _tradesSkipped = 0;

            try
            {
                Run(doc, ref pass, ref fail, ref skip);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("ExplodeViewByTrade: run aborted", ex);
                Log($"Error: {ex.Message}", "fail");
                fail++;
            }

            Progress(100, pass, fail, skip);
            OnResultChips?.Invoke(new List<ResultChip>
            {
                new ResultChip("views",   _viewsCreated,  "LemoineGreen"),
                new ResultChip("skipped", _tradesSkipped, "LemoineTextDim"),
                new ResultChip("failed",  fail,           "LemoineRed"),
            });
            Complete(pass, fail, skip);

            // Session-long static handler — drop the run's payload (memory discipline).
            SourceViewId     = ElementId.InvalidElementId;
            SelectedTradeIds = new List<string>();
        }

        // ─────────────────────────────────────────────────────────────────────────
        private void Run(Document doc, ref int pass, ref int fail, ref int skip)
        {
            if (!(doc.GetElement(SourceViewId) is View3D source) || source.IsTemplate)
            {
                Log("Source view is not a 3D view (or no longer exists).", "fail");
                fail++; return;
            }
            if (SelectedTradeIds == null || SelectedTradeIds.Count == 0)
            {
                Log("No trades selected.", "fail");
                fail++; return;
            }

            // ── Resolve trades → existing ParameterFilterElements by name (0–15%) ──
            var filterByName = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var settings = AutoFiltersSettings.Instance;
            var plans    = new List<TradePlan>();

            foreach (var tradeId in SelectedTradeIds)
            {
                var trade = settings.Trades.FirstOrDefault(
                    t => string.Equals(t.Id, tradeId, StringComparison.OrdinalIgnoreCase));
                if (trade == null) continue;

                var filters = new List<ParameterFilterElement>();
                foreach (var rule in trade.Rules.Where(r => r.Enabled && AutoFiltersSettings.RuleProducesFilter(r)))
                {
                    string fname = AutoFiltersSettings.MakeFilterName(trade.Id, rule.Name);
                    if (filterByName.TryGetValue(fname, out var pfe))
                        filters.Add(pfe);
                }

                if (filters.Count == 0)
                {
                    Log($"Trade \"{trade.Label}\" has no created filters in this project — skipped. "
                        + "Run AutoFilters → Create Filters first.", "warn");
                    _tradesSkipped++; skip++;
                    continue;
                }

                plans.Add(new TradePlan { Id = trade.Id, Label = trade.Label, Filters = filters });
            }

            if (plans.Count == 0)
            {
                Log("None of the selected trades have filters created in this project — nothing to explode.", "fail");
                fail++; return;
            }

            Progress(15, pass, fail, skip);

            // ── Elevation scan per trade (15–55%) ─────────────────────────────────
            // Bound the scan to the source view's section box; a trade whose elements are
            // matched in the host view and/or every visible link contributes its world-Z
            // centroids. The median orders the output stack.
            bool hasBox = source.IsSectionBoxActive;
            BoundingBoxXYZ? worldBox = null;
            if (hasBox)
            {
                try
                {
                    var sb = source.GetSectionBox();
                    worldBox = WorldAabb(BoxCorners(sb.Min, sb.Max), sb.Transform);
                }
                catch (Exception ex)
                {
                    LemoineLog.Swallowed("ExplodeViewByTrade: read section box", ex);
                    hasBox = false;
                }
            }
            else
            {
                Log("Source view has no active section box — elevation scan covers the whole model "
                    + "for each trade (this may be slow on large links).", "info");
            }

            var links = new FilteredElementCollector(doc, source.Id)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .Where(li => li.GetLinkDocument() != null)
                .ToList();

            for (int i = 0; i < plans.Count; i++)
            {
                if (LemoineRun.CancelRequested)
                {
                    Log($"Stopped by user during elevation scan — {i} of {plans.Count} trade(s) scanned.", "warn");
                    break;
                }

                ScanTradeElevation(doc, source, plans[i], links, worldBox);

                var p = plans[i];
                if (p.ElemCount == 0)
                    Log($"No elements found for \"{p.Label}\" in view scope — it will stack at the bottom.", "info");
                else
                    Log($"Found {p.ElemCount} element(s) for \"{p.Label}\" — median {FormatFtIn(p.MedianZ)} "
                        + $"(range {FormatFtIn(p.MinZ)} … {FormatFtIn(p.MaxZ)}).", "info");

                Progress(15 + (int)((i + 1) * 40.0 / plans.Count), pass, fail, skip);
            }

            // ── Determine stack order (top → bottom) ──────────────────────────────
            List<TradePlan> ordered;
            if (OrderByElevation)
            {
                // Trades with elevation data first (highest median on top); trades with no
                // elements keep config order and sink to the bottom.
                var withElev = plans.Where(p => p.HasElevation)
                                    .OrderByDescending(p => p.MedianZ).ToList();
                var without  = plans.Where(p => !p.HasElevation).ToList();
                ordered = withElev.Concat(without).ToList();
            }
            else
            {
                ordered = plans;
            }

            Log("Stack order (top → bottom): "
                + string.Join("  ›  ", ordered.Select(p => p.Label)), "info");

            // ── Link display diagnostic (host filters need "By Host View") ────────
            ReportLinkDisplayModes(source, links);

            // ── Build color-override lookup maps (read-only) ──────────────────────
            ElementId solidFillId = GetSolidFillId(doc);
            ElementId solidLineId = GetSolidLineId();
            var fillPatternMap = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .GroupBy(fp => fp.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
            var linePatternMap = new FilteredElementCollector(doc)
                .OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
                .GroupBy(lp => lp.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

            // filterName → owning rule, for color overrides on the visible trade.
            var ruleByName = new Dictionary<string, FilterRuleConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var trade in settings.Trades)
                foreach (var rule in trade.Rules)
                {
                    string n = AutoFiltersSettings.MakeFilterName(trade.Id, rule.Name);
                    if (!ruleByName.ContainsKey(n)) ruleByName[n] = rule;
                }

            // All filters across the selected (resolved) trades — every output view carries
            // them all so isolation is a pure visibility toggle.
            var allFilters = plans
                .SelectMany(p => p.Filters)
                .GroupBy(f => f.Id.Value)
                .Select(g => g.First())
                .ToList();

            // Pre-seed the used-name set with every existing view name (View.Name is unique).
            var usedNames = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate).Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);

            ViewOrientation3D srcOrientation = source.GetOrientation();
            BoundingBoxXYZ?   srcSectionBox  = hasBox ? source.GetSectionBox() : null;
            string            srcName        = source.Name;

            // ── Create the exploded views (55–95%) ────────────────────────────────
            using (var tx = new Transaction(doc, "Explode 3D View by Trade"))
            {
                ConfigureFailures(tx);
                tx.Start();

                for (int i = 0; i < ordered.Count; i++)
                {
                    if (LemoineRun.CancelRequested)
                    {
                        Log($"Stopped by user — {i} of {ordered.Count} view(s) created; work so far preserved.", "warn");
                        break;
                    }

                    var plan = ordered[i];
                    try
                    {
                        ElementId dupId = source.Duplicate(ViewDuplicateOption.Duplicate);
                        if (!(doc.GetElement(dupId) is View3D dup))
                        {
                            Log($"Could not duplicate the source view for \"{plan.Label}\".", "fail");
                            fail++; continue;
                        }

                        // Belt-and-suspenders: a Duplicate already carries camera + section box,
                        // but re-assert both so the angle and crop are guaranteed identical.
                        try { dup.SetOrientation(srcOrientation); }
                        catch (Exception ex) { LemoineLog.Swallowed("ExplodeViewByTrade: set orientation", ex); }
                        if (srcSectionBox != null)
                        {
                            try { dup.SetSectionBox(srcSectionBox); dup.IsSectionBoxActive = true; }
                            catch (Exception ex) { LemoineLog.Swallowed("ExplodeViewByTrade: set section box", ex); }
                        }

                        // Name + number prefix; enforce View.Name uniqueness (it throws on a dup).
                        string nn       = NumberPrefix ? (i + 1).ToString("00") : "";
                        string baseName = (NamePattern ?? "{nn}_{Source} - {Trade}")
                            .Replace("{nn}",     nn)
                            .Replace("{Source}", srcName)
                            .Replace("{Trade}",  plan.Label)
                            .Trim()
                            .TrimStart('_', ' ', '-')
                            .Trim();
                        if (baseName.Length == 0) baseName = $"{srcName} - {plan.Label}";
                        string uniqueName = MakeUniqueName(baseName, usedNames);
                        try { dup.Name = uniqueName; }
                        catch (Exception ex)
                        {
                            Log($"Could not rename exploded view to \"{uniqueName}\": {ex.Message} "
                                + "(left with Revit's default name).", "warn");
                            LemoineLog.Swallowed("ExplodeViewByTrade: set view name", ex);
                        }

                        // Add every selected trade's filters; isolate this trade.
                        var thisTradeIds = new HashSet<long>(plan.Filters.Select(f => f.Id.Value));
                        var existing     = new HashSet<long>(dup.GetFilters().Select(id => id.Value));

                        foreach (var pfe in allFilters)
                        {
                            try
                            {
                                if (!existing.Contains(pfe.Id.Value))
                                {
                                    dup.AddFilter(pfe.Id);
                                    existing.Add(pfe.Id.Value);
                                }
                                dup.SetIsFilterEnabled(pfe.Id, true);

                                if (thisTradeIds.Contains(pfe.Id.Value))
                                {
                                    if (ApplyColorOverride && ruleByName.TryGetValue(pfe.Name, out var rule))
                                        AutoFiltersEventHandler.ApplyRuleOverride(
                                            dup, pfe.Id, rule, solidFillId, solidLineId,
                                            fillPatternMap, linePatternMap);
                                    // Force the isolated trade visible regardless of the rule's
                                    // own Visible flag — this view exists to show this trade.
                                    dup.SetFilterVisibility(pfe.Id, true);
                                }
                                else
                                {
                                    // Hide every other selected trade.
                                    dup.SetFilterVisibility(pfe.Id, false);
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"Filter '{pfe.Name}' on '{uniqueName}': {ex.Message}", "fail");
                            }
                        }

                        _viewsCreated++; pass++;
                        Log($"Created \"{uniqueName}\" isolating \"{plan.Label}\".", "pass");
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to create exploded view for \"{plan.Label}\": {ex.Message}", "fail");
                        LemoineLog.Error("ExplodeViewByTrade: create view", ex);
                        fail++;
                    }

                    Progress(55 + (int)((i + 1) * 40.0 / ordered.Count), pass, fail, skip);
                }

                tx.Commit();
            }

            Log($"Complete — {_viewsCreated} view(s) created from \"{srcName}\", "
                + $"{_tradesSkipped} trade(s) skipped.", "pass");
        }

        // ── Elevation scan ────────────────────────────────────────────────────────
        private void ScanTradeElevation(
            Document doc, View3D source, TradePlan plan,
            List<RevitLinkInstance> links, BoundingBoxXYZ? worldBox)
        {
            var zs = new List<double>();

            // Host elements visible in the source view (the view collector respects the
            // section box), matched by the trade's own filters.
            ElementFilter? union = BuildUnionFilter(plan.Filters, doc);
            if (union != null)
            {
                try
                {
                    foreach (Element el in new FilteredElementCollector(doc, source.Id)
                        .WhereElementIsNotElementType()
                        .WherePasses(union))
                    {
                        if (TryWorldZ(el, Transform.Identity, out double z)) zs.Add(z);
                    }
                }
                catch (Exception ex) { LemoineLog.Swallowed("ExplodeViewByTrade: host elevation scan", ex); }
            }

            // Linked elements — match in each link's own document, bounded to the section box.
            foreach (var link in links)
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                Transform xform = link.GetTotalTransform();

                ElementFilter? linkUnion = BuildUnionFilter(plan.Filters, linkDoc);
                if (linkUnion == null) continue;

                try
                {
                    var collector = new FilteredElementCollector(linkDoc).WhereElementIsNotElementType();
                    if (worldBox != null)
                    {
                        // Transform the world section-box AABB into link space (AABB of the
                        // re-projected corners — a slight over-estimate, fine for a scan).
                        var linkAabb = WorldAabb(
                            BoxCorners(worldBox.Min, worldBox.Max), xform.Inverse);
                        collector = collector.WherePasses(new BoundingBoxIntersectsFilter(
                            new Outline(linkAabb.Min, linkAabb.Max)));
                    }

                    foreach (Element el in collector.WherePasses(linkUnion))
                    {
                        if (TryWorldZ(el, xform, out double z)) zs.Add(z);
                    }
                }
                catch (Exception ex) { LemoineLog.Swallowed("ExplodeViewByTrade: linked elevation scan", ex); }
            }

            plan.ElemCount = zs.Count;
            if (zs.Count == 0) { plan.HasElevation = false; return; }

            zs.Sort();
            plan.MinZ        = UnitUtils.ConvertFromInternalUnits(zs[0], UnitTypeId.Feet);
            plan.MaxZ        = UnitUtils.ConvertFromInternalUnits(zs[zs.Count - 1], UnitTypeId.Feet);
            plan.MedianZ     = UnitUtils.ConvertFromInternalUnits(zs[zs.Count / 2], UnitTypeId.Feet);
            plan.HasElevation = true;
        }

        /// <summary>
        /// Builds a LogicalOr of each filter's (category AND rule) element filter, evaluated in
        /// <paramref name="targetDoc"/>. A rule-less whole-category filter contributes its
        /// category filter alone. Returns null when nothing usable resolves.
        /// </summary>
        private static ElementFilter? BuildUnionFilter(
            List<ParameterFilterElement> filters, Document targetDoc)
        {
            var parts = new List<ElementFilter>();
            foreach (var pfe in filters)
            {
                try
                {
                    var cats = pfe.GetCategories();
                    ElementFilter? catFilter =
                        (cats != null && cats.Count > 0) ? new ElementMulticategoryFilter(cats) : null;
                    ElementFilter? ruleFilter = pfe.GetElementFilter(); // null when rule-less

                    if (catFilter != null && ruleFilter != null)
                        parts.Add(new LogicalAndFilter(catFilter, ruleFilter));
                    else if (catFilter != null)
                        parts.Add(catFilter);
                    else if (ruleFilter != null)
                        parts.Add(ruleFilter);
                }
                catch (Exception ex) { LemoineLog.Swallowed("ExplodeViewByTrade: build union filter", ex); }
            }

            if (parts.Count == 0) return null;
            return parts.Count == 1 ? parts[0] : new LogicalOrFilter(parts);
        }

        private static bool TryWorldZ(Element el, Transform xform, out double z)
        {
            z = 0;
            BoundingBoxXYZ? bb = el.get_BoundingBox(null);
            if (bb == null) return false;
            var centroidLocal = new XYZ(
                (bb.Min.X + bb.Max.X) * 0.5,
                (bb.Min.Y + bb.Max.Y) * 0.5,
                (bb.Min.Z + bb.Max.Z) * 0.5);
            XYZ world = (xform == null || xform.IsIdentity) ? centroidLocal : xform.OfPoint(centroidLocal);
            z = world.Z;
            return true;
        }

        // ── Link display diagnostic (does not change link display) ─────────────────
        private void ReportLinkDisplayModes(View source, List<RevitLinkInstance> links)
        {
            foreach (var link in links)
            {
                try
                {
                    RevitLinkGraphicsSettings? gs = source.GetLinkOverrides(link.Id);
                    LinkVisibility mode = gs?.LinkVisibilityType ?? LinkVisibility.ByHostView;
                    if (mode != LinkVisibility.ByHostView)
                    {
                        Log($"Link \"{link.Name}\" is displayed \"{mode}\", not \"By Host View\" — "
                            + "the exploded views' filters will not hide/show its elements. "
                            + "Set it to \"By Host View\" in Visibility/Graphics.", "warn");
                        LemoineLog.Warn("ExplodeViewByTrade",
                            $"link '{link.Name}' display={mode}; host filters won't cascade.");
                    }
                }
                catch (Exception ex) { LemoineLog.Swallowed("ExplodeViewByTrade: read link display mode", ex); }
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────
        private static string MakeUniqueName(string baseName, HashSet<string> used)
        {
            string name = baseName;
            int n = 2;
            while (used.Contains(name))
                name = $"{baseName} ({n++})";
            used.Add(name);
            return name;
        }

        private static XYZ[] BoxCorners(XYZ min, XYZ max) => new[]
        {
            new XYZ(min.X, min.Y, min.Z), new XYZ(max.X, min.Y, min.Z),
            new XYZ(min.X, max.Y, min.Z), new XYZ(max.X, max.Y, min.Z),
            new XYZ(min.X, min.Y, max.Z), new XYZ(max.X, min.Y, max.Z),
            new XYZ(min.X, max.Y, max.Z), new XYZ(max.X, max.Y, max.Z),
        };

        private static BoundingBoxXYZ WorldAabb(XYZ[] pts, Transform tx)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var p in pts)
            {
                XYZ t = (tx == null || tx.IsIdentity) ? p : tx.OfPoint(p);
                if (t.X < minX) minX = t.X; if (t.X > maxX) maxX = t.X;
                if (t.Y < minY) minY = t.Y; if (t.Y > maxY) maxY = t.Y;
                if (t.Z < minZ) minZ = t.Z; if (t.Z > maxZ) maxZ = t.Z;
            }
            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };
        }

        private static string FormatFtIn(double valueFt)
        {
            int totalInches = (int)Math.Round(valueFt * 12.0);
            string sign     = totalInches < 0 ? "-" : "";
            int absInches   = Math.Abs(totalInches);
            int ft          = absInches / 12;
            int inches      = absInches % 12;
            return $"{sign}{ft}'-{inches}\"";
        }

        private static ElementId GetSolidFillId(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill)
                ?.Id ?? ElementId.InvalidElementId;
        }

        private static ElementId GetSolidLineId()
        {
            try { return LinePatternElement.GetSolidPatternId(); }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("ExplodeViewByTrade: resolve solid line pattern id", ex);
                return ElementId.InvalidElementId;
            }
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private void Log(string t, string s)               => PushLog?.Invoke(t, s);
        private void Progress(int p, int pa, int f, int s)  => OnProgress?.Invoke(p, pa, f, s);
        private void Complete(int p, int f, int s)          => OnComplete?.Invoke(p, f, s);
    }
}
