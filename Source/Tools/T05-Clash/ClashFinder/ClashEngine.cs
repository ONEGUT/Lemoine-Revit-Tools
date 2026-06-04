using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using LemoineTools.Tools.AutoFilters;

using RevitColor = Autodesk.Revit.DB.Color;

namespace LemoineTools.Tools.Clash
{
    /// <summary>Marking options for one clash definition (lifted from ClashDimensionSettings).</summary>
    public struct ClashMarkingOptions
    {
        public double ToleranceMm;
        public string FillStyle;          // "Solid" | "Outline"
        public string FallbackColorHex;   // colour for clashes matching no Auto Filter rule
        public string CrossLineTypeName;  // "" = default line style
        public string DimTarget;          // "Edge" | "Centre"
        public int    MaxClashes;
        public double StoreyMarginMm;     // depth below a level still counted as its storey (slabs/structure)
        public double RoundSizeMm;        // round marker diameter; 0 = auto-fit to the clash bbox
    }

    /// <summary>Aggregate result of one engine run.</summary>
    public struct ClashEngineResult
    {
        public int Markers;   // filled-region + cross-line markers placed
        public int Fails;     // per-view marker failures
        public int Clashes;   // distinct clashes detected
    }

    /// <summary>
    /// Detection + marking engine for the Clash Finder: scans two groups, finds solid
    /// intersections, and draws a coloured filled region + tagged cross lines per clash.
    /// Detection + marking only — all dimension placement lives in the AutoDimension engine.
    ///
    /// <see cref="Run"/> executes inside the caller's open transaction; it never opens or
    /// commits one. Markers are tagged via <see cref="ClashTagSchema"/>.
    /// </summary>
    public sealed class ClashEngine
    {
        private readonly ClashMarkingOptions     _opts;
        private readonly Action<string, string>  _log;

        public ClashEngine(ClashMarkingOptions opts, Action<string, string> log)
        {
            _opts = opts;
            _log  = log ?? ((a, b) => { });
        }

        private void Log(string text, string status) => _log(text, status);

        // ── BIP map for fast parameter resolution ─────────────────────────────
        private static readonly Dictionary<string, BuiltInParameter> BipMap =
            new Dictionary<string, BuiltInParameter>(StringComparer.Ordinal)
            {
                ["System Classification"] = BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM,
                ["Fabrication Service"]   = BuiltInParameter.FABRICATION_SERVICE_NAME,
                ["Type Name"]             = BuiltInParameter.ALL_MODEL_TYPE_NAME,
                ["Family Name"]           = BuiltInParameter.ELEM_FAMILY_PARAM,
                ["Structural Material"]   = BuiltInParameter.STRUCTURAL_MATERIAL_PARAM,
            };

        // ── Inner types ───────────────────────────────────────────────────────
        private class ClashElement
        {
            public Document           Doc          = null!;
            public RevitLinkInstance? LinkInstance;
            public Transform          HostTransform = Transform.Identity;
            public ElementId          Id            = ElementId.InvalidElementId;
            public string             Label         = "";
            public string             ColorHex      = "#888888";
            public bool               RuleColored   = true;
            public BoundingBoxXYZ     HostBBox      = null!;
            public Solid?             HostSolid;
            public bool               SolidTried;
        }

        private class ClashResult
        {
            public ClashElement   Group1      = null!;
            public ClashElement   Group2      = null!;
            public BoundingBoxXYZ OverlapBBox = null!;
        }

        // ── Entry point ───────────────────────────────────────────────────────
        public ClashEngineResult Run(
            Document doc, IList<ElementId> viewIds,
            ClashGroupSpec group1Spec, ClashGroupSpec group2Spec)
        {
            var result = new ClashEngineResult();

            // 1. Source documents (host + all loaded links)
            var sources = new List<(Document doc, RevitLinkInstance? link, Transform tx)>
            {
                (doc, null, Transform.Identity)
            };
            foreach (var li in new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = li.GetLinkDocument();
                if (ld == null) continue;
                sources.Add((ld, li, li.GetTotalTransform()));
            }

            // 2. Scan each group per its mode
            var group1Elements = ScanGroupSpec(group1Spec, sources, "Group 1");
            var group2Elements = ScanGroupSpec(group2Spec, sources, "Group 2");
            Log($"Group 1: {group1Elements.Count} element(s)   Group 2: {group2Elements.Count} element(s)", "info");

            if (group1Elements.Count == 0)
            {
                Log("Group 1 produced no elements — check its mode, selection, and source documents.", "fail");
                result.Fails++; return result;
            }
            if (group2Elements.Count == 0)
            {
                Log("Group 2 produced no elements — check its mode, selection, and source documents.", "fail");
                result.Fails++; return result;
            }

            // 3. Find clashes
            int maxClashes = _opts.MaxClashes > 0 ? _opts.MaxClashes : 500;
            double toleranceFt = _opts.ToleranceMm / 304.8;
            var clashes  = FindClashes(group1Elements, group2Elements, maxClashes);
            bool hitLimit = clashes.Count >= maxClashes;
            result.Clashes = clashes.Count;

            if (clashes.Count == 0)
            {
                Log("No solid intersections detected between the two groups.", "info");
                return result;
            }

            Log(hitLimit
                ? $"Found {clashes.Count} clash(es) — limit of {maxClashes} reached. Increase Max Clashes to detect more."
                : $"Found {clashes.Count} clash(es).", "info");

            int unruled = clashes.Count(c => !c.Group1.RuleColored);
            if (unruled > 0)
                Log($"{unruled} clash(es) matched no Auto Filter rule — shown in fallback colour {_opts.FallbackColorHex} with a solid fill.", "info");

            // 4. Place markers (inside the caller's transaction)
            ElementId lineStyleId = ResolveLineStyleId(doc);
            var regionTypeCache = new Dictionary<string, ElementId?>();

            // Per-view world box, computed once. A clash is drawn in a view only when its overlap
            // region falls inside that view's volume — crop box in XY, a storey-height band in Z —
            // so other levels' clashes (common in identical stacked-level models) don't bleed onto
            // this view. Overlap bboxes are already in host world coordinates (SolidWorldBBox), so
            // the gate is link-agnostic. Levels are resolved once for the storey-band lookup.
            var levelElevs = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .Select(l => l.Elevation).OrderBy(z => z).ToList();
            double storeyMarginFt = Math.Max(0.0, _opts.StoreyMarginMm) / 304.8;

            var viewBoxes     = new Dictionary<ElementId, (BoundingBoxXYZ box, bool gated)>();
            var skippedByView = new Dictionary<ElementId, int>();
            foreach (var viewId in viewIds)
            {
                if (!(doc.GetElement(viewId) is View v)) continue;
                bool gated = TryGetViewWorldBox(v, levelElevs, storeyMarginFt, out BoundingBoxXYZ box);
                viewBoxes[viewId] = (box, gated);
            }

            Log($"Placing markers for {clashes.Count} clash(es) across {viewIds.Count} view(s)…", "info");
            int markedClash = 0;
            foreach (var clash in clashes)
            {
                if (++markedClash % 200 == 0)
                    Log($"  …{markedClash}/{clashes.Count} clash(es) marked", "info");

                foreach (var viewId in viewIds)
                {
                    var view = doc.GetElement(viewId) as View;
                    if (view == null) continue;

                    if (viewBoxes.TryGetValue(viewId, out var vb) && vb.gated
                        && !BBoxOverlapTol(clash.OverlapBBox, vb.box, toleranceFt))
                    {
                        skippedByView.TryGetValue(viewId, out int s);
                        skippedByView[viewId] = s + 1;
                        continue;
                    }

                    try
                    {
                        if (CreateClashGraphics(doc, view, clash, lineStyleId, toleranceFt, regionTypeCache))
                            result.Markers++;
                    }
                    catch (Exception ex)
                    {
                        Log($"Error in '{view.Name}': {ex.Message}", "fail");
                        result.Fails++;
                    }
                }
            }

            foreach (var kv in skippedByView)
            {
                if (kv.Value <= 0) continue;
                var view = doc.GetElement(kv.Key) as View;
                Log($"View '{view?.Name ?? kv.Key.ToString()}': {kv.Value} clash(es) outside the view volume — skipped.", "info");
            }

            return result;
        }

        // ── View-volume gate (keeps other levels' / off-crop clashes off this view) ───
        private const double DefaultStoreyFt = 14.0;  // top-most level (no level above) fallback height

        /// <summary>
        /// True, with the view's world-space box (unbounded axes stay at double.Max/MinValue), when the view can be
        /// scoped: plan views get crop-box XY (when cropped) and a storey-height Z band from their
        /// level to the next level up; 3D views use an active section box. Returns false (no gate)
        /// for un-scopable views — an uncropped section/elevation — so they are never over-filtered.
        /// </summary>
        private static bool TryGetViewWorldBox(View view, List<double> sortedLevelElevs, double storeyMarginFt, out BoundingBoxXYZ box)
        {
            // double.Max/MinValue stand in for "unbounded" on un-scoped axes (XYZ rejects non-finite).
            box = new BoundingBoxXYZ
            {
                Min = new XYZ(double.MinValue, double.MinValue, double.MinValue),
                Max = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue),
            };

            if (view is View3D v3 && v3.IsSectionBoxActive)
            {
                try
                {
                    var sb = v3.GetSectionBox();
                    box = WorldAabb(BoxCorners(sb.Min, sb.Max), sb.Transform);
                    return true;
                }
                catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: get 3D section box", ex); return false; }
            }

            bool bounded = false;

            // XY from the crop box (corners → world) when the view is cropped.
            try
            {
                if (view.CropBoxActive && view.CropBox != null)
                {
                    var cb    = view.CropBox;
                    var world = WorldAabb(BoxCorners(cb.Min, cb.Max), cb.Transform);
                    box.Min = new XYZ(world.Min.X, world.Min.Y, box.Min.Z);
                    box.Max = new XYZ(world.Max.X, world.Max.Y, box.Max.Z);
                    bounded = true;
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read view crop box", ex); }

            // Z from the storey band [Li - margin, Lnext - margin) for plan views.
            if (view is ViewPlan plan && TryGetStoreyZBand(plan, sortedLevelElevs, storeyMarginFt, out double zMin, out double zMax))
            {
                box.Min = new XYZ(box.Min.X, box.Min.Y, zMin);
                box.Max = new XYZ(box.Max.X, box.Max.Y, zMax);
                bounded = true;
            }

            return bounded;
        }

        /// <summary>Storey-height world-Z band for a plan view: from its level (less a margin for
        /// sub-floor structure) up to the next level above, or a default height when it is the top
        /// level. False when the plan has no associated level.</summary>
        private static bool TryGetStoreyZBand(ViewPlan plan, List<double> sortedLevelElevs, double storeyMarginFt, out double zMin, out double zMax)
        {
            zMin = double.NegativeInfinity;
            zMax = double.PositiveInfinity;

            Level? genLevel;
            try { genLevel = plan.GenLevel; }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read plan GenLevel", ex); return false; }
            if (genLevel == null) return false;

            double baseElev = genLevel.Elevation;
            double? nextAbove = sortedLevelElevs
                .Where(e => e > baseElev + 1e-6)
                .Select(e => (double?)e)
                .FirstOrDefault();

            zMin = baseElev - storeyMarginFt;
            zMax = (nextAbove ?? baseElev + DefaultStoreyFt) - storeyMarginFt;
            return true;
        }

        /// <summary>AABB overlap test tolerant of an inflation on each axis; double.Max/MinValue
        /// bounds (unbounded box axes) always pass — the ± tol never overflows them.</summary>
        private static bool BBoxOverlapTol(BoundingBoxXYZ a, BoundingBoxXYZ b, double tol)
        {
            return a.Min.X <= b.Max.X + tol && a.Max.X >= b.Min.X - tol
                && a.Min.Y <= b.Max.Y + tol && a.Max.Y >= b.Min.Y - tol
                && a.Min.Z <= b.Max.Z + tol && a.Max.Z >= b.Min.Z - tol;
        }

        // ── Group scanning (mode-aware) ───────────────────────────────────────
        private List<ClashElement> ScanGroupSpec(
            ClashGroupSpec spec,
            List<(Document doc, RevitLinkInstance? link, Transform tx)> allSources,
            string label)
        {
            switch (spec.Mode)
            {
                case "Categories":
                    return ScanCategories(spec.Categories, FilterSources(allSources, spec.SourceLinkIds));
                case "Elements":
                    return ScanElements(spec.ElemIds, spec.ElemLinkIds, allSources);
                default:
                    var rules = ResolveRules(spec.RuleKeys);
                    if (rules.Count == 0)
                        Log($"{label}: no filter rules resolved — check Auto Filters or switch mode.", "info");
                    return ScanRules(rules, FilterSources(allSources, spec.SourceLinkIds));
            }
        }

        private static List<(Document doc, RevitLinkInstance? link, Transform tx)> FilterSources(
            List<(Document doc, RevitLinkInstance? link, Transform tx)> all, List<long> sourceLinkIds)
        {
            if (sourceLinkIds == null || sourceLinkIds.Count == 0) return all;
            var set = new HashSet<long>(sourceLinkIds);
            return all.Where(s => set.Contains(s.link?.Id.Value ?? 0L)).ToList();
        }

        private List<ClashElement> ScanRules(
            List<(FilterTradeConfig trade, FilterRuleConfig rule)> rules,
            List<(Document doc, RevitLinkInstance? link, Transform tx)> sources)
        {
            var result = new List<ClashElement>();

            foreach (var (trade, rule) in rules)
            {
                if (!rule.Enabled) continue;

                var catIds = new List<BuiltInCategory>();
                foreach (var bicStr in rule.BuiltInCategories ?? new List<string>())
                    if (Enum.TryParse<BuiltInCategory>(bicStr, false, out var bic))
                        catIds.Add(bic);

                if (catIds.Count == 0)
                {
                    Log($"Rule '{rule.Name}' has no categories configured — skipped.", "info");
                    continue;
                }

                int ruleTotal = 0;
                foreach (var (srcDoc, link, tx) in sources)
                {
                    int srcCount = 0;
                    foreach (var bic in catIds)
                    {
                        IEnumerable<Element> elems;
                        try
                        {
                            elems = new FilteredElementCollector(srcDoc)
                                .OfCategory(bic).WhereElementIsNotElementType().ToElements();
                        }
                        catch { continue; }

                        foreach (var el in elems)
                        {
                            if (!MatchesRule(el, rule)) continue;
                            var bb = GetHostBBox(el, tx);
                            if (bb == null) continue;

                            result.Add(new ClashElement
                            {
                                Doc           = srcDoc,
                                LinkInstance  = link,
                                HostTransform = tx,
                                Id            = el.Id,
                                Label         = rule.Name,
                                ColorHex      = rule.SurfColor ?? "#888888",
                                HostBBox      = bb,
                            });
                            srcCount++;
                        }
                    }
                    if (srcCount > 0 || link != null)
                        Log($"  [{srcDoc.Title}] '{rule.Name}': {srcCount} element(s)", "info");
                    ruleTotal += srcCount;
                }
                if (ruleTotal == 0)
                    Log($"  Rule '{rule.Name}': 0 matching elements — check categories/match criteria/source docs.", "info");
            }

            return result;
        }

        private List<ClashElement> ScanCategories(
            List<string> osts,
            List<(Document doc, RevitLinkInstance? link, Transform tx)> sources)
        {
            var result = new List<ClashElement>();
            foreach (var ostStr in osts ?? new List<string>())
            {
                if (!Enum.TryParse<BuiltInCategory>(ostStr, false, out var bic)) continue;

                int catTotal = 0;
                foreach (var (srcDoc, link, tx) in sources)
                {
                    int srcCount = 0;
                    IEnumerable<Element> elems;
                    try
                    {
                        elems = new FilteredElementCollector(srcDoc)
                            .OfCategory(bic).WhereElementIsNotElementType().ToElements();
                    }
                    catch { continue; }

                    foreach (var el in elems)
                    {
                        var bb = GetHostBBox(el, tx);
                        if (bb == null) continue;
                        string? ruleColor = ResolveRuleColor(el);
                        result.Add(new ClashElement
                        {
                            Doc           = srcDoc,
                            LinkInstance  = link,
                            HostTransform = tx,
                            Id            = el.Id,
                            Label         = ostStr,
                            ColorHex      = ruleColor ?? _opts.FallbackColorHex,
                            RuleColored   = ruleColor != null,
                            HostBBox      = bb,
                        });
                        srcCount++;
                    }
                    catTotal += srcCount;
                }
                Log($"  Category {ostStr}: {catTotal} element(s)", "info");
            }
            return result;
        }

        private List<ClashElement> ScanElements(
            List<long> elemIds, List<long> elemLinkIds,
            List<(Document doc, RevitLinkInstance? link, Transform tx)> allSources)
        {
            var result = new List<ClashElement>();
            var byLink = new Dictionary<long, (Document doc, RevitLinkInstance? link, Transform tx)>();
            foreach (var s in allSources) byLink[s.link?.Id.Value ?? 0L] = s;

            for (int i = 0; i < elemIds.Count; i++)
            {
                long lnk = (i < elemLinkIds.Count) ? elemLinkIds[i] : 0L;
                if (!byLink.TryGetValue(lnk, out var src)) continue;

                var el = src.doc.GetElement(new ElementId(elemIds[i]));
                if (el == null) continue;
                var bb = GetHostBBox(el, src.tx);
                if (bb == null) continue;

                string? ruleColor = ResolveRuleColor(el);
                result.Add(new ClashElement
                {
                    Doc           = src.doc,
                    LinkInstance  = src.link,
                    HostTransform = src.tx,
                    Id            = el.Id,
                    Label         = el.Name ?? "(element)",
                    ColorHex      = ruleColor ?? _opts.FallbackColorHex,
                    RuleColored   = ruleColor != null,
                    HostBBox      = bb,
                });
            }
            Log($"  Picked elements resolved: {result.Count}", "info");
            return result;
        }

        // ── Rule resolution & matching ────────────────────────────────────────
        private static List<(FilterTradeConfig trade, FilterRuleConfig rule)> ResolveRules(List<string> persistKeys)
        {
            var result = new List<(FilterTradeConfig, FilterRuleConfig)>();
            var keySet = new HashSet<string>(persistKeys ?? new List<string>());
            foreach (var trade in AutoFiltersSettings.Instance.Trades)
                foreach (var rule in trade.Rules)
                    if (keySet.Contains($"{trade.Id}::{rule.Id}"))
                        result.Add((trade, rule));
            return result;
        }

        private static string? ResolveRuleColor(Element el)
        {
            string? bic = ElementBicName(el);
            if (bic == null) return null;

            foreach (var trade in AutoFiltersSettings.Instance.Trades)
            {
                if (trade?.Rules == null) continue;
                foreach (var rule in trade.Rules)
                {
                    if (rule == null || !rule.Enabled) continue;
                    if (rule.BuiltInCategories == null || !rule.BuiltInCategories.Contains(bic)) continue;
                    if (!MatchesRule(el, rule)) continue;
                    if (string.IsNullOrEmpty(rule.SurfColor)) continue;
                    return rule.SurfColor;
                }
            }
            return null;
        }

        private static string? ElementBicName(Element el)
        {
            var cat = el.Category;
            if (cat == null) return null;
            try { return ((BuiltInCategory)cat.Id.Value).ToString(); }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: resolve element category", ex); return null; }
        }

        private static bool MatchesRule(Element el, FilterRuleConfig rule)
        {
            string matchType = (rule.MatchType ?? "contains").ToLowerInvariant();
            if (matchType == "all") return true;
            if (rule.Match == null || rule.Match.Count == 0) return false;

            string? pv = ReadParamValue(el, rule.Parameter);
            if (pv == null) return false;
            string pvLow = pv.ToLowerInvariant();

            foreach (var kw in rule.Match)
            {
                string kwLow = kw.ToLowerInvariant();
                if (matchType == "equals" ? pvLow == kwLow : pvLow.Contains(kwLow))
                    return true;
            }
            return false;
        }

        private static string? ReadParamValue(Element el, string paramName)
        {
            if (paramName == "Type Name")
            {
                try
                {
                    var t = el.Document.GetElement(el.GetTypeId());
                    if (t != null && !string.IsNullOrEmpty(t.Name)) return t.Name;
                }
                catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read Type Name parameter", ex); }
            }

            try
            {
                var p = el.LookupParameter(paramName);
                if (p != null)
                {
                    string? v = p.AsValueString();
                    if (!string.IsNullOrEmpty(v)) return v;
                    v = p.AsString();
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read parameter value", ex); }

            if (BipMap.TryGetValue(paramName, out var bip))
            {
                try
                {
                    var p = el.get_Parameter(bip);
                    if (p != null)
                    {
                        string? v = p.AsValueString();
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                }
                catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: read built-in parameter value", ex); }
            }
            return null;
        }

        // ── BBox helpers ──────────────────────────────────────────────────────
        private static BoundingBoxXYZ? GetHostBBox(Element el, Transform hostTransform)
        {
            BoundingBoxXYZ? bb;
            try { bb = el.get_BoundingBox(null); }
            catch { return null; }
            if (bb == null) return null;

            var pts = BoxCorners(bb.Min, bb.Max);
            return WorldAabb(pts, hostTransform);
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

        // ── Clash detection ───────────────────────────────────────────────────
        private List<ClashResult> FindClashes(
            List<ClashElement> group1, List<ClashElement> group2, int maxClashes)
        {
            const double eps = 1e-6;
            var results = new List<ClashResult>();
            int booleanFails = 0;

            foreach (var g1 in group1)
            {
                var b1 = g1.HostBBox;
                foreach (var g2 in group2)
                {
                    var b2 = g2.HostBBox;
                    if (!BBoxOverlap(b1, b2)) continue;

                    var s1 = EnsureSolid(g1);
                    var s2 = EnsureSolid(g2);

                    BoundingBoxXYZ? overlap = null;
                    if (s1 != null && s2 != null)
                    {
                        Solid? inter = null;
                        try
                        {
                            inter = BooleanOperationsUtils.ExecuteBooleanOperation(
                                s1, s2, BooleanOperationsType.Intersect);
                        }
                        catch { inter = null; booleanFails++; }

                        if (inter != null && inter.Volume > eps)
                            overlap = SolidWorldBBox(inter);
                        else if (inter != null)
                            continue;
                        else
                            overlap = BBoxOverlapRegion(b1, b2);
                    }
                    else
                    {
                        overlap = BBoxOverlapRegion(b1, b2);
                    }

                    if (overlap == null) continue;
                    results.Add(new ClashResult { Group1 = g1, Group2 = g2, OverlapBBox = overlap });
                    if (results.Count >= maxClashes)
                    {
                        if (booleanFails > 0) Log($"  ({booleanFails} boolean op fallback(s) to bbox)", "info");
                        return results;
                    }
                }
            }
            if (booleanFails > 0) Log($"  ({booleanFails} boolean op fallback(s) to bbox)", "info");
            return results;
        }

        private static bool BBoxOverlap(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            return a.Min.X <= b.Max.X && a.Max.X >= b.Min.X
                && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y
                && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;
        }

        private static BoundingBoxXYZ BBoxOverlapRegion(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            return new BoundingBoxXYZ
            {
                Min = new XYZ(Math.Max(a.Min.X, b.Min.X), Math.Max(a.Min.Y, b.Min.Y), Math.Max(a.Min.Z, b.Min.Z)),
                Max = new XYZ(Math.Min(a.Max.X, b.Max.X), Math.Min(a.Max.Y, b.Max.Y), Math.Min(a.Max.Z, b.Max.Z)),
            };
        }

        private Solid? EnsureSolid(ClashElement e)
        {
            if (e.SolidTried) return e.HostSolid;
            e.SolidTried = true;
            var el = e.Doc.GetElement(e.Id);
            if (el != null) e.HostSolid = GetUnionSolidHost(el, e.HostTransform);
            return e.HostSolid;
        }

        private static Solid? GetUnionSolidHost(Element el, Transform tx)
        {
            GeometryElement? ge;
            try
            {
                ge = el.get_Geometry(new Options
                {
                    ComputeReferences        = false,
                    IncludeNonVisibleObjects = false,
                    DetailLevel              = ViewDetailLevel.Medium,
                });
            }
            catch { return null; }
            if (ge == null) return null;
            return AccumulateSolids(ge, tx, null);
        }

        private static Solid? AccumulateSolids(GeometryElement ge, Transform tx, Solid? acc)
        {
            foreach (GeometryObject obj in ge)
            {
                if (obj is Solid s && s.Volume > 1e-6)
                {
                    Solid hs;
                    try { hs = (tx == null || tx.IsIdentity) ? s : SolidUtils.CreateTransformed(s, tx); }
                    catch { continue; }
                    acc = Combine(acc, hs);
                }
                else if (obj is GeometryInstance gi)
                {
                    GeometryElement? ige = null;
                    try { ige = gi.GetInstanceGeometry(); }
                    catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: extract instance geometry", ex); }
                    if (ige != null) acc = AccumulateSolids(ige, tx, acc);
                }
            }
            return acc;
        }

        private static Solid? Combine(Solid? acc, Solid s)
        {
            if (acc == null) return s;
            try { return BooleanOperationsUtils.ExecuteBooleanOperation(acc, s, BooleanOperationsType.Union); }
            catch { return acc; }
        }

        private static BoundingBoxXYZ SolidWorldBBox(Solid solid)
        {
            var bb = solid.GetBoundingBox();
            var pts = BoxCorners(bb.Min, bb.Max);
            return WorldAabb(pts, bb.Transform);
        }

        // ── Marker creation (filled region + tagged cross lines) ──────────────
        private bool CreateClashGraphics(
            Document doc, View view, ClashResult clash,
            ElementId lineStyleId, double toleranceFt, Dictionary<string, ElementId?> regionTypeCache)
        {
            var zone = clash.OverlapBBox;
            double minX = zone.Min.X - toleranceFt;
            double maxX = zone.Max.X + toleranceFt;
            double minY = zone.Min.Y - toleranceFt;
            double maxY = zone.Max.Y + toleranceFt;

            if (maxX - minX < 0.001 || maxY - minY < 0.001) return false;

            double cx     = (minX + maxX) / 2.0;
            double cy     = (minY + maxY) / 2.0;
            double halfW  = (maxX - minX) / 2.0;
            double halfH  = (maxY - minY) / 2.0;
            // Round marker: a fixed per-run diameter when set, else auto-fit (circumscribe the clash).
            double radius = _opts.RoundSizeMm > 0
                ? (_opts.RoundSizeMm / 2.0) / 304.8
                : Math.Max(0.25, Math.Max(halfW, halfH));
            double armLen = Math.Max(0.5, Math.Min(radius * 1.5, 3.0));

            // One id shared by this clash's region + every cross line, so the dimension pass can
            // re-group the 2–4 lines back into a single clash and dimension it once (not per line).
            string clashGroup = Guid.NewGuid().ToString("N");

            // ── FilledRegion (circular) ───────────────────────────────────────
            bool fallback   = !clash.Group1.RuleColored;
            string hexColor = (clash.Group1.ColorHex ?? "#888888").TrimStart('#').ToUpperInvariant();
            string cacheKey = fallback ? $"{hexColor}_FB" : $"{hexColor}_{_opts.FillStyle}";
            if (!regionTypeCache.TryGetValue(cacheKey, out ElementId? typeId))
            {
                typeId = GetOrCreateFilledRegionType(doc, hexColor, fallback);
                regionTypeCache[cacheKey] = typeId;
                if (typeId == null || typeId == ElementId.InvalidElementId)
                    Log($"Clash marker: no filled region type for #{hexColor} — circles skipped.", "info");
            }

            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                try
                {
                    var ctr  = new XYZ(cx, cy, 0);
                    var arc1 = Arc.Create(ctr, radius, 0,        Math.PI,     XYZ.BasisX, XYZ.BasisY);
                    var arc2 = Arc.Create(ctr, radius, Math.PI,  2 * Math.PI, XYZ.BasisX, XYZ.BasisY);
                    var loop = new CurveLoop();
                    loop.Append(arc1);
                    loop.Append(arc2);
                    var fr = FilledRegion.Create(doc, typeId, view.Id, new List<CurveLoop> { loop });
                    ClashTagSchema.StampTag(fr, clashGroup);
                }
                catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: create clash filled region", ex); }
            }

            // ── Cross lines (tagged so the discovery pass can re-find them) ────
            if (_opts.DimTarget == "Centre")
            {
                var hLeft  = CreateLine(doc, view, lineStyleId, new XYZ(cx - armLen, cy, 0), new XYZ(cx,       cy, 0), clashGroup);
                var hRight = CreateLine(doc, view, lineStyleId, new XYZ(cx,       cy, 0), new XYZ(cx + armLen, cy, 0), clashGroup);
                var vBot   = CreateLine(doc, view, lineStyleId, new XYZ(cx, cy - armLen, 0), new XYZ(cx, cy,       0), clashGroup);
                var vTop   = CreateLine(doc, view, lineStyleId, new XYZ(cx, cy,       0), new XYZ(cx, cy + armLen, 0), clashGroup);
                return hLeft != null && hRight != null && vBot != null && vTop != null;
            }
            else
            {
                var hLine = CreateLine(doc, view, lineStyleId, new XYZ(cx - armLen, cy, 0), new XYZ(cx + armLen, cy, 0), clashGroup);
                var vLine = CreateLine(doc, view, lineStyleId, new XYZ(cx, cy - armLen, 0), new XYZ(cx, cy + armLen, 0), clashGroup);
                return hLine != null && vLine != null;
            }
        }

        // ── FilledRegionType management ───────────────────────────────────────
        private ElementId? GetOrCreateFilledRegionType(Document doc, string hexColor, bool fallback)
        {
            string suffix   = fallback ? "FB" : (_opts.FillStyle == "Solid" ? "S" : "O");
            string typeName = $"LemoineClash_{hexColor}_{suffix}";

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                .FirstOrDefault(t => t.Name == typeName);
            if (existing != null) return existing.Id;

            var template = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                .FirstOrDefault();
            if (template == null) return null;

            var newType = template.Duplicate(typeName) as FilledRegionType;
            if (newType == null) return null;

            newType.IsMasking = false;

            var clr = ParseHexColor(hexColor);
            if (clr != null)
                newType.ForegroundPatternColor = clr;

            if (fallback)
            {
                var solidId = GetSolidFillId(doc);
                if (solidId != ElementId.InvalidElementId)
                    newType.ForegroundPatternId = solidId;
            }
            else if (_opts.FillStyle == "Solid")
            {
                var solidId = GetSolidFillId(doc);
                if (solidId != ElementId.InvalidElementId)
                    newType.ForegroundPatternId = solidId;
            }
            else
            {
                newType.ForegroundPatternId = ElementId.InvalidElementId;
            }

            return newType.Id;
        }

        // ── Detail line creation (tagged) ─────────────────────────────────────
        private static DetailCurve? CreateLine(Document doc, View view, ElementId lineStyleId, XYZ start, XYZ end, string group)
        {
            var line = Line.CreateBound(start, end);
            var dc   = doc.Create.NewDetailCurve(view, line);
            ClashTagSchema.StampTag(dc, group);

            if (lineStyleId != ElementId.InvalidElementId)
            {
                var gs = doc.GetElement(lineStyleId) as GraphicsStyle;
                if (gs != null) try { dc.LineStyle = gs; }
                    catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: apply detail-curve line style", ex); }
            }
            return dc;
        }

        // ── Helper utilities ──────────────────────────────────────────────────
        private ElementId ResolveLineStyleId(Document doc)
        {
            if (string.IsNullOrEmpty(_opts.CrossLineTypeName)) return ElementId.InvalidElementId;
            try
            {
                var linesCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                if (linesCat == null) return ElementId.InvalidElementId;
                foreach (Category sub in linesCat.SubCategories)
                {
                    if (sub.Name == _opts.CrossLineTypeName)
                    {
                        var gs = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                        return gs?.Id ?? ElementId.InvalidElementId;
                    }
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: resolve cross-line graphics style", ex); }
            return ElementId.InvalidElementId;
        }

        private static ElementId GetSolidFillId(Document doc)
        {
            foreach (var fp in new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
            {
                try { if (fp.GetFillPattern().IsSolidFill) return fp.Id; }
                catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: inspect fill pattern", ex); }
            }
            return ElementId.InvalidElementId;
        }

        private static RevitColor? ParseHexColor(string hex)
        {
            try
            {
                string h = hex.TrimStart('#');
                if (h.Length == 6)
                {
                    int v = Convert.ToInt32(h, 16);
                    return new RevitColor((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("ClashEngine: parse hex colour", ex); }
            return null;
        }
    }
}
