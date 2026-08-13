using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Framework;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Tools.FiltersLegends.LegendCreator
{
    // =========================================================================
    // SmartLegendScope — "which of this legend's colours are actually used in the
    // view(s) this legend serves?"
    //
    // A project can carry 30+ ceiling-colour filters, all applied to every RCP via
    // the view template, while any one area shows only three of them. "Applied to
    // the view" is therefore NOT the test — "at least one element visible in that
    // view is matched by that filter" is.
    //
    // Everything here is READ-ONLY: no transaction, no doc.Regenerate(). The caller
    // runs it BEFORE opening its transaction so a run that resolves to nothing can
    // abort without having cleared an existing legend.
    //
    // THE FAIL-SAFE RULE, which every branch below obeys:
    //
    //     A legend row is hidden only when its absence is POSITIVELY PROVEN.
    //     Anything that cannot be evaluated stays visible and is reported.
    //
    // A smart legend that silently drops a colour which IS on the sheet is far worse
    // than one that shows an extra row, so every exception path adds the filter to
    // Live (and to Unprovable, so the run log says why).
    // =========================================================================

    /// <summary>How the target view(s) for a smart legend were determined.</summary>
    public enum SmartScopeSource
    {
        /// <summary>No scope could be resolved — smart filtering must be skipped.</summary>
        None = 0,
        /// <summary>The legend entry names its own target views.</summary>
        Manual,
        /// <summary>Derived from the sheet(s) the legend view is placed on.</summary>
        Sheet,
    }

    /// <summary>Why a candidate filter was not drawn. Formatted by the caller via AppStrings.</summary>
    public enum SmartHideReason
    {
        None = 0,
        /// <summary>No ParameterFilterElement of that name exists in this project.</summary>
        NoFilterElement,
        /// <summary>The filter exists but is not applied to any target view.</summary>
        NotApplied,
        /// <summary>Applied but switched off in the view — it overrides nothing.</summary>
        Disabled,
        /// <summary>Applied with visibility OFF — its elements are hidden, so nothing is coloured.</summary>
        HiddenByFilter,
        /// <summary>Applied and active, but no element it matches is visible in the view.</summary>
        NoMatch,
    }

    /// <summary>The target views a smart legend will be tested against.</summary>
    public sealed class SmartLegendScopeResult
    {
        public SmartScopeSource Source { get; set; } = SmartScopeSource.None;

        /// <summary>Views to test. Empty when <see cref="Source"/> is None.</summary>
        public List<ElementId> ViewIds { get; set; } = new List<ElementId>();

        /// <summary>"A-101 — FIRST FLOOR RCP" for each sheet the legend sits on.</summary>
        public List<string> SheetLabels { get; set; } = new List<string>();

        /// <summary>Names from the entry that no view in this document carries.</summary>
        public List<string> UnresolvedViewNames { get; set; } = new List<string>();

        /// <summary>True when explicit picks were given but none resolved, so auto-detect ran instead.</summary>
        public bool FellBackFromManual { get; set; }

        public bool HasScope => Source != SmartScopeSource.None && ViewIds.Count > 0;
    }

    /// <summary>Which candidate filters survive the liveness test, and why the rest did not.</summary>
    public sealed class SmartLegendUsage
    {
        /// <summary>Filter names to DRAW — proven live, or kept because they could not be tested.</summary>
        public HashSet<string> Live { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Filter name → why it is not drawn. Never contains a name in <see cref="Live"/>.</summary>
        public Dictionary<string, SmartHideReason> Hidden { get; } =
            new Dictionary<string, SmartHideReason>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Names kept visible because the test could not be completed (see fail-safe rule).</summary>
        public HashSet<string> Unprovable { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Links skipped because they are not displayed "By Host View" in a target view.</summary>
        public List<string> NonCascadingLinks { get; } = new List<string>();

        /// <summary>True when the pass stopped early on a cancel request — results are partial.</summary>
        public bool Cancelled { get; set; }

        public int CandidateCount { get; set; }
    }

    public static class SmartLegendScope
    {
        // ─────────────────────────────────────────────────────────────────────
        // Block → filter name
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the block → ParameterFilterElement-name resolver, reading the trade library
        /// once. Returns null for a block with no source rule — a custom swatch cannot be
        /// tested and is therefore always drawn.
        ///
        /// The filter name is built from the trade that actually OWNS the rule, not the
        /// block's <c>SourceTradeId</c>, which goes stale when a rule moves between trades.
        ///
        /// Shared by the run handler and the window's preview so a block can never resolve
        /// to one filter when drawing and a different one when previewing.
        /// </summary>
        public static Func<LegendBlockConfig, string?> BuildFilterNameResolver()
        {
            var ruleName  = new Dictionary<string, string>(StringComparer.Ordinal);
            var ruleTrade = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var trade in AutoFiltersSettings.Instance.Trades)
                {
                    if (trade?.Rules == null) continue;
                    foreach (var rule in trade.Rules)
                    {
                        if (rule == null || string.IsNullOrEmpty(rule.Id)) continue;
                        if (ruleName.ContainsKey(rule.Id)) continue;
                        ruleName[rule.Id]  = rule.Name ?? "";
                        ruleTrade[rule.Id] = trade.Id ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("SmartLegendScope: read trade library for filter names", ex);
            }

            return blk =>
            {
                if (blk == null || string.IsNullOrEmpty(blk.SourceRuleId)) return null;
                if (!ruleName.TryGetValue(blk.SourceRuleId, out string name)) return null;
                if (!ruleTrade.TryGetValue(blk.SourceRuleId, out string tradeId)) return null;
                return AutoFiltersSettings.MakeFilterName(tradeId, name);
            };
        }

        /// <summary>Filter names behind every currently-visible block in a legend.</summary>
        public static HashSet<string> CollectCandidates(
            IEnumerable<LegendRowConfig>? rows, Func<LegendBlockConfig, string?> filterNameForBlock)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rows == null) return set;
            foreach (var row in rows)
                foreach (var grp in row?.Groups ?? new List<LegendGroupConfig>())
                    foreach (var blk in grp?.Blocks ?? new List<LegendBlockConfig>())
                    {
                        if (blk == null || !blk.Visible) continue;
                        string? fn = filterNameForBlock(blk);
                        if (fn != null) set.Add(fn);
                    }
            return set;
        }

        /// <summary>A one-line, user-facing description of a resolved scope.</summary>
        public static string DescribeScope(SmartLegendScopeResult scope)
        {
            if (scope == null || !scope.HasScope)
                return AppStrings.T("testing.legendCreator.builder.window.smart.scopeNone");
            if (scope.Source == SmartScopeSource.Manual)
                return AppStrings.T("testing.legendCreator.builder.window.smart.scopeManual", scope.ViewIds.Count);
            if (scope.SheetLabels.Count <= 1)
                return AppStrings.T("testing.legendCreator.builder.window.smart.scopeSheet",
                    scope.SheetLabels.Count > 0 ? scope.SheetLabels[0] : "", scope.ViewIds.Count);
            return AppStrings.T("testing.legendCreator.builder.window.smart.scopeSheets",
                scope.SheetLabels.Count, scope.ViewIds.Count);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Scope resolution — the "hybrid": explicit picks, else the sheet(s) the
        // legend is placed on, else nothing.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the views a legend should be tested against.
        ///
        /// Order: the entry's own view names → the sheet(s) carrying the legend view →
        /// no scope at all. A legend that has never been created has no bound view and
        /// therefore no sheet, which is why "no scope" must degrade to drawing everything
        /// rather than dead-ending the run.
        /// </summary>
        public static SmartLegendScopeResult ResolveScope(
            Document doc, ElementId? legendViewId, IReadOnlyList<string>? targetViewNames)
        {
            var result = new SmartLegendScopeResult();
            if (doc == null) return result;

            // ── 1. Explicit picks, stored as NAMES ────────────────────────────
            // Names, not ElementIds: a legend entry is serialized into the .rvt and into
            // the shared seed library, where an ElementId means nothing (the mistake
            // LegendEntry.RevitViewId was removed for). Revit enforces View.Name
            // uniqueness, so a name resolves deterministically inside a document.
            if (targetViewNames != null && targetViewNames.Count > 0)
            {
                var byName = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (View v in new FilteredElementCollector(doc)
                        .OfClass(typeof(View)).Cast<View>())
                    {
                        if (v.IsTemplate) continue;
                        string n = SafeViewName(v);
                        if (n.Length > 0 && !byName.ContainsKey(n)) byName[n] = v.Id;
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("SmartLegendScope: collect views by name", ex);
                }

                foreach (string name in targetViewNames)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (byName.TryGetValue(name.Trim(), out ElementId id)) result.ViewIds.Add(id);
                    else result.UnresolvedViewNames.Add(name);   // reported, never silently dropped
                }

                if (result.ViewIds.Count > 0)
                {
                    result.Source = SmartScopeSource.Manual;
                    return result;
                }

                // Every pick was unresolvable — most likely this library travelled to a
                // different project. Fall through to auto-detect rather than reporting
                // "no scope", and say so.
                result.FellBackFromManual = true;
            }

            // ── 2. The sheet(s) the legend view is placed on ──────────────────
            if (legendViewId == null || legendViewId == ElementId.InvalidElementId)
                return result;

            var sheetIds  = new List<ElementId>();
            var viewIds   = new List<ElementId>();
            var seenViews = new HashSet<long>();

            try
            {
                // One document-wide Viewport pass. A legend is the only view type that can
                // sit on several sheets at once, so this naturally covers the multi-sheet
                // case; ViewSheet.GetAllPlacedViews' treatment of legends is not documented
                // clearly enough to rely on.
                var viewports = new FilteredElementCollector(doc)
                    .OfClass(typeof(Viewport)).Cast<Viewport>().ToList();

                foreach (Viewport vp in viewports)
                    if (vp.ViewId == legendViewId) sheetIds.Add(vp.SheetId);

                var sheetSet = new HashSet<long>(sheetIds.Select(s => s.Value));
                foreach (Viewport vp in viewports)
                {
                    if (!sheetSet.Contains(vp.SheetId.Value)) continue;
                    if (vp.ViewId == legendViewId) continue;
                    if (!seenViews.Add(vp.ViewId.Value)) continue;

                    // Legends and schedules carry no model filters; skip them so the log's
                    // view count reflects what is actually testable.
                    if (doc.GetElement(vp.ViewId) is View v &&
                        v.ViewType != ViewType.Legend && v.ViewType != ViewType.Schedule)
                        viewIds.Add(vp.ViewId);
                }

                foreach (ElementId sid in sheetIds.Distinct())
                    if (doc.GetElement(sid) is ViewSheet sh)
                        result.SheetLabels.Add($"{sh.SheetNumber} — {SafeViewName(sh)}");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("SmartLegendScope: resolve sheets for legend view", ex);
                return result;
            }

            if (viewIds.Count == 0) return result;   // legend sheet with no model views

            result.ViewIds = viewIds;
            result.Source  = SmartScopeSource.Sheet;
            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Liveness
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Tests each candidate filter name against the target views and reports which
        /// ones actually colour something visible there.
        /// </summary>
        /// <param name="candidateFilterNames">
        /// ParameterFilterElement names behind the legend's rule-backed blocks.
        /// </param>
        /// <param name="cancelRequested">
        /// Optional cooperative cancel probe. The run handler passes RunState; the
        /// window's advisory preview passes null so a live run's flag cannot stop it.
        /// </param>
        public static SmartLegendUsage ComputeUsage(
            Document doc,
            IReadOnlyList<ElementId> viewIds,
            IReadOnlyCollection<string> candidateFilterNames,
            Func<bool>? cancelRequested = null)
        {
            var usage = new SmartLegendUsage
            {
                CandidateCount = candidateFilterNames?.Count ?? 0,
            };
            if (doc == null || viewIds == null || candidateFilterNames == null ||
                candidateFilterNames.Count == 0)
                return usage;

            var candidates = new HashSet<string>(candidateFilterNames, StringComparer.OrdinalIgnoreCase);

            // ── Every ParameterFilterElement in the document, by name ─────────
            var pfeByName = new Dictionary<string, ParameterFilterElement>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (ParameterFilterElement pfe in new FilteredElementCollector(doc)
                    .OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>())
                {
                    string n = SafeViewName(pfe);
                    if (n.Length > 0 && !pfeByName.ContainsKey(n)) pfeByName[n] = pfe;
                }
            }
            catch (Exception ex)
            {
                // Without the filter list nothing can be proven absent — keep every row.
                DiagnosticsLog.Swallowed("SmartLegendScope: collect parameter filters", ex);
                foreach (string n in candidates) { usage.Live.Add(n); usage.Unprovable.Add(n); }
                return usage;
            }

            // A name with no filter element behind it cannot colour anything in any view:
            // provable absence, not an unknown.
            var testable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in candidates)
            {
                if (pfeByName.ContainsKey(n)) testable.Add(n);
                else usage.Hidden[n] = SmartHideReason.NoFilterElement;
            }
            if (testable.Count == 0) return usage;

            var reasons     = new Dictionary<string, SmartHideReason>(StringComparer.OrdinalIgnoreCase);
            var linkWarned  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ElementId viewId in viewIds)
            {
                if (cancelRequested != null && cancelRequested())
                {
                    usage.Cancelled = true;
                    break;
                }

                if (!(doc.GetElement(viewId) is View view)) continue;

                foreach (ElementId fid in EffectiveFilterIds(doc, view))
                {
                    if (!(doc.GetElement(fid) is ParameterFilterElement pfe)) continue;
                    string name = SafeViewName(pfe);
                    if (name.Length == 0 || !testable.Contains(name)) continue;
                    if (usage.Live.Contains(name)) continue;   // already proven in an earlier view

                    // A filter switched off, or set to hide its elements, colours nothing.
                    bool enabled = true, visible = true;
                    try { enabled = view.GetIsFilterEnabled(fid); }
                    catch (Exception ex) { DiagnosticsLog.Swallowed($"SmartLegendScope: read enabled state of '{name}'", ex); }
                    try { visible = view.GetFilterVisibility(fid); }
                    catch (Exception ex) { DiagnosticsLog.Swallowed($"SmartLegendScope: read visibility of '{name}'", ex); }

                    if (!enabled) { RecordReason(reasons, name, SmartHideReason.Disabled); continue; }
                    if (!visible) { RecordReason(reasons, name, SmartHideReason.HiddenByFilter); continue; }

                    bool unprovable;
                    if (MatchesInView(doc, view, pfe, usage, linkWarned, out unprovable))
                    {
                        usage.Live.Add(name);
                    }
                    else if (unprovable)
                    {
                        // Fail-safe: an untestable filter is drawn, and the log says why.
                        usage.Live.Add(name);
                        usage.Unprovable.Add(name);
                    }
                    else
                    {
                        RecordReason(reasons, name, SmartHideReason.NoMatch);
                    }
                }
            }

            // A cancelled pass has tested only some views, so an untested filter must not be
            // reported as absent — keep everything still unproven.
            if (usage.Cancelled)
            {
                foreach (string n in testable)
                    if (!usage.Live.Contains(n)) { usage.Live.Add(n); usage.Unprovable.Add(n); }
                return usage;
            }

            foreach (string n in testable)
            {
                if (usage.Live.Contains(n)) continue;
                usage.Hidden[n] = reasons.TryGetValue(n, out SmartHideReason r)
                    ? r
                    : SmartHideReason.NotApplied;   // never seen on any target view
            }
            return usage;
        }

        // Keeps the most informative reason when a filter is dead in several views for
        // different reasons: "applied but empty" beats "not applied here" for the user.
        private static void RecordReason(
            Dictionary<string, SmartHideReason> map, string name, SmartHideReason reason)
        {
            if (map.TryGetValue(name, out SmartHideReason existing) &&
                Rank(existing) >= Rank(reason)) return;
            map[name] = reason;
        }

        private static int Rank(SmartHideReason r)
        {
            switch (r)
            {
                case SmartHideReason.NoMatch:        return 3;
                case SmartHideReason.HiddenByFilter: return 2;
                case SmartHideReason.Disabled:       return 1;
                default:                             return 0;
            }
        }

        /// <summary>
        /// The filter ids in force in a view: its own, union'd with its view template's.
        ///
        /// GetFilters() is expected to report template-supplied filters already, but that
        /// cannot be verified without a Windows/Revit run. The union can only over-report
        /// (a filter listed here still has to match an element to count as live), so it
        /// errs in the fail-safe direction.
        /// </summary>
        private static IEnumerable<ElementId> EffectiveFilterIds(Document doc, View view)
        {
            var ids  = new List<ElementId>();
            var seen = new HashSet<long>();

            try
            {
                foreach (ElementId id in view.GetFilters())
                    if (seen.Add(id.Value)) ids.Add(id);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"SmartLegendScope: read filters of view {view.Id}", ex);
            }

            try
            {
                if (view.ViewTemplateId != ElementId.InvalidElementId &&
                    doc.GetElement(view.ViewTemplateId) is View tpl)
                {
                    foreach (ElementId id in tpl.GetFilters())
                        if (seen.Add(id.Value)) ids.Add(id);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"SmartLegendScope: read template filters of view {view.Id}", ex);
            }

            return ids;
        }

        /// <summary>
        /// True when at least one element the filter matches is visible in the view —
        /// in the host model or in a link that the host's filters cascade onto.
        /// </summary>
        /// <param name="unprovable">
        /// Set when the test could not be completed. The caller must then DRAW the row:
        /// "could not test" is not "not present".
        /// </param>
        private static bool MatchesInView(
            Document doc, View view, ParameterFilterElement pfe,
            SmartLegendUsage usage, HashSet<string> linkWarned, out bool unprovable)
        {
            unprovable = false;

            ICollection<ElementId>? cats = null;
            try { cats = pfe.GetCategories(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed($"SmartLegendScope: read categories of '{SafeViewName(pfe)}'", ex); }

            // A rule-less whole-category filter reports a null element filter — the
            // category test alone is then the whole test.
            ElementFilter? rules = null;
            try { rules = pfe.GetElementFilter(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed($"SmartLegendScope: read rules of '{SafeViewName(pfe)}'", ex); }

            // ── Host ──────────────────────────────────────────────────────────
            // A view-scoped collector already honours crop, view range, hidden categories
            // and hidden elements, so "visible in this view" needs nothing extra.
            try
            {
                var col = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
                col = ApplyCategories(col, cats);
                if (rules != null) col = col.WherePasses(rules);
                if (col.FirstElementId() != ElementId.InvalidElementId) return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed(
                    $"SmartLegendScope: test '{SafeViewName(pfe)}' in view {view.Id}", ex);
                unprovable = true;
                return false;
            }

            // ── Links ─────────────────────────────────────────────────────────
            // The host-view collector never returns elements living inside a link, and on
            // these projects ceilings usually DO live in links — a host-only test would
            // report every ceiling filter as dead.
            try
            {
                foreach (RevitLinkInstance link in new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    Document? linkDoc = link.GetLinkDocument();
                    if (linkDoc == null) continue;

                    // Host view filters only reach a link's elements when it is displayed
                    // "By Host View" — anything else is not colour-cascaded at all.
                    if (!IsByHostView(view, link))
                    {
                        string label = SafeViewName(link);
                        if (linkWarned.Add(label + "|" + view.Id.Value))
                            usage.NonCascadingLinks.Add($"{label} ({SafeViewName(view)})");
                        continue;
                    }

                    ElementFilter? bounds = null;
                    try { bounds = GetLinkBoundsFilter(view, link.GetTotalTransform().Inverse); }
                    catch (Exception ex) { DiagnosticsLog.Swallowed("SmartLegendScope: build link bounds", ex); }

                    var col = new FilteredElementCollector(linkDoc).WhereElementIsNotElementType();
                    col = ApplyCategories(col, cats);
                    if (bounds != null) col = col.WherePasses(bounds);
                    // A rule comparing an ElementId-valued parameter is meaningless across
                    // documents; if Revit rejects it, the catch below keeps the row.
                    if (rules != null) col = col.WherePasses(rules);
                    if (col.FirstElementId() != ElementId.InvalidElementId) return true;
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed(
                    $"SmartLegendScope: test '{SafeViewName(pfe)}' against links in view {view.Id}", ex);
                unprovable = true;
                return false;
            }

            return false;
        }

        // A quick category filter in front of the (slow) rule filter. An empty or
        // unusable category set simply means no pre-filter — never a dropped test.
        private static FilteredElementCollector ApplyCategories(
            FilteredElementCollector col, ICollection<ElementId>? cats)
        {
            if (cats == null || cats.Count == 0) return col;
            try { return col.WherePasses(new ElementMulticategoryFilter(cats)); }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("SmartLegendScope: build category pre-filter", ex);
                return col;
            }
        }

        private static bool IsByHostView(View view, RevitLinkInstance link)
        {
            try
            {
                // Null overrides mean the link was never customised in this view, i.e. the
                // default By Host View.
                RevitLinkGraphicsSettings? gs = view.GetLinkOverrides(link.Id);
                return (gs?.LinkVisibilityType ?? LinkVisibility.ByHostView) == LinkVisibility.ByHostView;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"SmartLegendScope: read link display mode for {link.Id}", ex);
                return true;   // unknown → assume it cascades, so the row survives
            }
        }

        /// <summary>
        /// A world-space bound for scanning a link, expressed in link coordinates.
        /// Returns null when the view is uncropped — an unbounded scan can only find MORE
        /// matches, which is the safe direction.
        ///
        /// A ViewPlan is floored at its own level so ceilings on the storeys below cannot
        /// count. There is deliberately NO upper cap: capping at "the next level up" has
        /// already been shown here to exclude real ceilings in double-height spaces.
        /// </summary>
        private static ElementFilter? GetLinkBoundsFilter(View view, Transform invLinkXform)
        {
            double zMinWorld = -1e6, zMaxWorld = 1e6;
            if (view is ViewPlan vp)
            {
                double levelElev = vp.GenLevel?.Elevation ?? 0.0;
                zMinWorld = levelElev - 1.0;
                zMaxWorld = levelElev + 1e6;
            }

            double zA = invLinkXform.OfPoint(new XYZ(0, 0, zMinWorld)).Z;
            double zB = invLinkXform.OfPoint(new XYZ(0, 0, zMaxWorld)).Z;
            double zMin = Math.Min(zA, zB), zMax = Math.Max(zA, zB);

            if (!view.CropBoxActive)
            {
                return new BoundingBoxIntersectsFilter(new Outline(
                    new XYZ(-1e6, -1e6, zMin), new XYZ(1e6, 1e6, zMax)));
            }

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            bool got = false;

            try
            {
                foreach (CurveLoop loop in view.GetCropRegionShapeManager().GetCropShape())
                    foreach (Curve curve in loop)
                        foreach (XYZ pt in curve.Tessellate())
                        {
                            if (pt.X < minX) minX = pt.X;
                            if (pt.Y < minY) minY = pt.Y;
                            if (pt.X > maxX) maxX = pt.X;
                            if (pt.Y > maxY) maxY = pt.Y;
                            got = true;
                        }
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("SmartLegendScope: read crop shape", ex); }

            if (!got)
            {
                try
                {
                    BoundingBoxXYZ cb = view.CropBox;
                    Transform t = cb.Transform;
                    foreach (XYZ local in new[]
                    {
                        new XYZ(cb.Min.X, cb.Min.Y, cb.Min.Z), new XYZ(cb.Max.X, cb.Min.Y, cb.Min.Z),
                        new XYZ(cb.Max.X, cb.Max.Y, cb.Min.Z), new XYZ(cb.Min.X, cb.Max.Y, cb.Min.Z),
                        new XYZ(cb.Min.X, cb.Min.Y, cb.Max.Z), new XYZ(cb.Max.X, cb.Min.Y, cb.Max.Z),
                        new XYZ(cb.Max.X, cb.Max.Y, cb.Max.Z), new XYZ(cb.Min.X, cb.Max.Y, cb.Max.Z),
                    })
                    {
                        XYZ w = t.OfPoint(local);
                        if (w.X < minX) minX = w.X;
                        if (w.Y < minY) minY = w.Y;
                        if (w.X > maxX) maxX = w.X;
                        if (w.Y > maxY) maxY = w.Y;
                        got = true;
                    }
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed("SmartLegendScope: read crop box", ex); }
            }

            // Crop is active but its extent could not be read — scan unbounded rather than
            // guessing a box that might exclude a real match.
            if (!got)
            {
                return new BoundingBoxIntersectsFilter(new Outline(
                    new XYZ(-1e6, -1e6, zMin), new XYZ(1e6, 1e6, zMax)));
            }

            XYZ p1 = invLinkXform.OfPoint(new XYZ(minX, minY, 0));
            XYZ p2 = invLinkXform.OfPoint(new XYZ(maxX, maxY, 0));

            return new BoundingBoxIntersectsFilter(new Outline(
                new XYZ(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), zMin),
                new XYZ(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y), zMax)));
        }

        // Element.Name throws for a few element kinds; a legend row must never be lost to that.
        private static string SafeViewName(Element el)
        {
            try { return el?.Name ?? ""; }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("SmartLegendScope: read element name", ex);
                return "";
            }
        }
    }
}
