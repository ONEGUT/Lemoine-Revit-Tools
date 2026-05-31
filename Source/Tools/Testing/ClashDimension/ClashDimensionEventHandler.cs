using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Tools.AutoFilters;

using RevitColor = Autodesk.Revit.DB.Color;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing
{
    public class ClashDimensionEventHandler : IExternalEventHandler
    {
        // ── Input properties (set by ViewModel before Raise()) ────────────────
        public List<ElementId> ViewIds           { get; set; } = new List<ElementId>();
        public ClashGroupSpec  Group1Spec        { get; set; } = new ClashGroupSpec();
        public ClashGroupSpec  Group2Spec        { get; set; } = new ClashGroupSpec();
        public List<long>      GridIds           { get; set; } = new List<long>();
        public List<long>      GridLinkIds       { get; set; } = new List<long>();   // parallel to GridIds; 0 = host doc
        public List<long>      FloorIds          { get; set; } = new List<long>();
        public List<long>      FloorLinkIds      { get; set; } = new List<long>();   // parallel to FloorIds; 0 = host doc
        public double          ToleranceMm       { get; set; } = 25.4;
        public string          DimStyleName      { get; set; } = "";
        public double          DimLineOffsetMm   { get; set; } = 100.0;
        public double          GroupToleranceMm  { get; set; } = 50.0;
        // Along-edge gap that splits a parallel run (mm); 0 = never split.
        public double          ClusterGapMm      { get; set; } = 1000.0;
        // Colour for clashes matching no Auto Filter rule (hex, rendered with a hatch fill).
        public string          FallbackColorHex  { get; set; } = "#FF00FF";
        public string          DimTarget         { get; set; } = "Edge";
        public string          FillStyle         { get; set; } = "Solid";
        public string          CrossLineTypeName { get; set; } = "";
        public bool            ClearPrevious     { get; set; } = true;
        public int             MaxClashes        { get; set; } = 500;

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Testing.ClashDimensionEventHandler";

        // Smallest dimension segment we will attempt (~3 mm); below this Revit rejects
        // the dimension as zero-length, so coincident/on-edge references are dropped.
        private const double MinSegFt = 0.01;

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
            public bool               RuleColored   = true;   // false → coloured by fallback, not a rule
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

        // ── IExternalEventHandler ─────────────────────────────────────────────
        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                Run(doc, ref pass, ref fail, ref skip);
            }
            catch (Exception ex)
            {
                Log($"Fatal: {ex.Message}", "fail");
                fail++;
            }

            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        // ── Core logic ────────────────────────────────────────────────────────
        private void Run(Document doc, ref int pass, ref int fail, ref int skip)
        {
            // 1. Collect source documents (host + all loaded links)
            var sources = new List<(Document doc, RevitLinkInstance? link, Transform tx)>
            {
                (doc, null, Transform.Identity)
            };
            foreach (var li in new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>())
            {
                var ld = li.GetLinkDocument();
                if (ld == null) continue;
                sources.Add((ld, li, li.GetTotalTransform()));
            }

            Log($"Scanning {sources.Count} document(s)…", "info");
            Progress(10, pass, fail, skip);

            // 2. Scan each group per its mode
            var group1Elements = ScanGroupSpec(doc, Group1Spec, sources, "Group 1");
            var group2Elements = ScanGroupSpec(doc, Group2Spec, sources, "Group 2");
            Log($"Group 1: {group1Elements.Count} element(s)   Group 2: {group2Elements.Count} element(s)", "info");

            if (group1Elements.Count == 0)
            {
                Log("Group 1 produced no elements — check its mode, selection, and source documents in Step 2.", "fail");
                fail++; return;
            }
            if (group2Elements.Count == 0)
            {
                Log("Group 2 produced no elements — check its mode, selection, and source documents in Step 3.", "fail");
                fail++; return;
            }

            Progress(30, pass, fail, skip);

            // 3. Find clashes — bbox pre-screen + real solid intersection
            double toleranceFt = ToleranceMm / 304.8;
            var clashes = FindClashes(group1Elements, group2Elements, MaxClashes);
            bool hitLimit = clashes.Count >= MaxClashes;

            if (clashes.Count == 0)
            {
                Log("No solid intersections detected between the two groups.", "pass");
                var b1 = group1Elements[0].HostBBox;
                var b2 = group2Elements[0].HostBBox;
                Log($"  Diag G1[0] ({group1Elements[0].Label}): " +
                    $"X[{b1.Min.X:F1},{b1.Max.X:F1}] Y[{b1.Min.Y:F1},{b1.Max.Y:F1}] Z[{b1.Min.Z:F1},{b1.Max.Z:F1}]", "info");
                Log($"  Diag G2[0] ({group2Elements[0].Label}): " +
                    $"X[{b2.Min.X:F1},{b2.Max.X:F1}] Y[{b2.Min.Y:F1},{b2.Max.Y:F1}] Z[{b2.Min.Z:F1},{b2.Max.Z:F1}]", "info");
                pass = 1; return;
            }

            Log(hitLimit
                ? $"Found {clashes.Count} clash(es) — limit of {MaxClashes} reached. Increase Max Clashes to detect more."
                : $"Found {clashes.Count} clash(es).", "info");

            int unruled = clashes.Count(c => !c.Group1.RuleColored);
            if (unruled > 0)
                Log($"{unruled} clash(es) matched no Auto Filter rule — shown in fallback colour {FallbackColorHex} with a hatch fill.", "info");

            Progress(40, pass, fail, skip);

            // 4. Resolve dimension type
            DimensionType? dimType = ResolveDimType(doc);

            // 5. Create annotations in a single transaction
            using (var tx = new Transaction(doc, "Lemoine — Clash Dimension"))
            {
                tx.Start();
                ConfigureFailures(tx);

                if (ClearPrevious) ClearPreviousAnnotations(doc);

                ElementId lineStyleId = ResolveLineStyleId(doc);
                var regionTypeCache = new Dictionary<string, ElementId?>();

                // Filled regions + cross lines are placed per clash (unchanged); the
                // returned markers feed the grouped dimension pass that follows.
                var markersByView = new Dictionary<ElementId, List<ClashMarker>>();
                foreach (var viewId in ViewIds)
                    markersByView[viewId] = new List<ClashMarker>();

                int done = 0;
                foreach (var clash in clashes)
                {
                    foreach (var viewId in ViewIds)
                    {
                        var view = doc.GetElement(viewId) as View;
                        if (view == null) continue;

                        try
                        {
                            var marker = CreateClashMarker(doc, view, clash,
                                lineStyleId, toleranceFt, regionTypeCache);
                            if (marker != null) markersByView[viewId].Add(marker.Value);
                            pass++;
                        }
                        catch (Exception ex)
                        {
                            Log($"Error in '{view.Name}': {ex.Message}", "fail");
                            fail++;
                        }
                    }

                    done++;
                    Progress(40 + (int)(done * 50.0 / clashes.Count), pass, fail, skip);
                }

                // Grouped dimension placement (one pass per view, after all markers exist).
                double dimLineOffsetFt = DimLineOffsetMm / 304.8;
                double groupTolFt      = GroupToleranceMm / 304.8;
                int    dimCount        = 0;
                foreach (var viewId in ViewIds)
                {
                    var view = doc.GetElement(viewId) as View;
                    if (view == null) continue;

                    try
                    {
                        dimCount += PlaceGroupedDimensions(doc, view, markersByView[viewId],
                            dimType, dimLineOffsetFt, groupTolFt);
                    }
                    catch (Exception ex)
                    {
                        Log($"Dimensions in '{view.Name}': {ex.Message}", "fail");
                        fail++;
                    }
                }
                Progress(95, pass, fail, skip);

                tx.Commit();

                Log($"Done — {pass} marker(s), {dimCount} dimension(s) placed, {fail} failed.",
                    pass > 0 ? "pass" : "fail");
            }
        }

        // ── Group scanning (mode-aware) ───────────────────────────────────────

        private List<ClashElement> ScanGroupSpec(
            Document hostDoc, ClashGroupSpec spec,
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
                                .OfCategory(bic)
                                .WhereElementIsNotElementType()
                                .ToElements();
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
                            .OfCategory(bic)
                            .WhereElementIsNotElementType()
                            .ToElements();
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
                            ColorHex      = ruleColor ?? FallbackColorHex,
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
            // Map linkInstId → (doc, link, tx)
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
                    ColorHex      = ruleColor ?? FallbackColorHex,
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
            var keySet = new HashSet<string>(persistKeys);
            foreach (var trade in AutoFiltersSettings.Instance.Trades)
                foreach (var rule in trade.Rules)
                    if (keySet.Contains($"{trade.Id}::{rule.Id}"))
                        result.Add((trade, rule));
            return result;
        }

        // Resolves the surface colour an element would receive from the Auto Filter rules,
        // regardless of how its group was defined (Rules, Categories, or direct Elements).
        // Returns null when the element matches no enabled rule → caller uses the fallback.
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

        // Returns the element's category as its OST_* enum name (e.g. "OST_PipeCurves"),
        // matching how rules store their categories. Null for elements without a category.
        private static string? ElementBicName(Element el)
        {
            var cat = el.Category;
            if (cat == null) return null;
            try { return ((BuiltInCategory)cat.Id.Value).ToString(); }
            catch (Exception __lex) { LemoineLog.Swallowed("ClashDimension: resolve element category", __lex); return null; }
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
                catch (Exception __lex) { LemoineLog.Swallowed("ClashDimension: read Type Name parameter", __lex); }
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
            catch (Exception __lex) { LemoineLog.Swallowed("ClashDimension: read parameter value", __lex); }

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
                catch (Exception __lex) { LemoineLog.Swallowed("ClashDimension: read built-in parameter value", __lex); }
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

        // ── Clash detection (bbox pre-screen + solid intersection) ────────────

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
                    if (!BBoxOverlap(b1, b2)) continue;   // fast reject

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
                            continue;   // solids do not actually intersect
                        else
                            overlap = BBoxOverlapRegion(b1, b2);   // boolean failed → fall back to bbox
                    }
                    else
                    {
                        // No usable solids (e.g. annotation-like elements) → bbox overlap
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
                    ComputeReferences      = false,
                    IncludeNonVisibleObjects = false,
                    DetailLevel            = ViewDetailLevel.Medium,
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
                    try { ige = gi.GetInstanceGeometry(); } catch (Exception __lex) { LemoineLog.Swallowed("ClashDimension: extract instance geometry", __lex); }
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
            var bb = solid.GetBoundingBox();   // local coords + Transform to world
            var pts = BoxCorners(bb.Min, bb.Max);
            return WorldAabb(pts, bb.Transform);
        }

        // ── Annotation creation ───────────────────────────────────────────────

        // A placed clash marker (filled region + cross lines). Carries the cross-arm
        // references used when dimensioning this clash to grids/slab edges:
        //   HRef → used by X-measuring (horizontal) dimensions
        //   VRef → used by Y-measuring (vertical) dimensions
        private struct ClashMarker
        {
            public double Cx;
            public double Cy;
            public Reference HRef;
            public Reference VRef;
        }

        // Places the filled region + cross lines for one clash and returns its marker.
        // Returns null if the clash zone is degenerate or the cross lines failed.
        private ClashMarker? CreateClashMarker(
            Document doc, View view, ClashResult clash,
            ElementId lineStyleId,
            double toleranceFt, Dictionary<string, ElementId?> regionTypeCache)
        {
            var zone = clash.OverlapBBox;
            double minX = zone.Min.X - toleranceFt;
            double maxX = zone.Max.X + toleranceFt;
            double minY = zone.Min.Y - toleranceFt;
            double maxY = zone.Max.Y + toleranceFt;

            if (maxX - minX < 0.001 || maxY - minY < 0.001) return null;

            double cx     = (minX + maxX) / 2.0;
            double cy     = (minY + maxY) / 2.0;
            double halfW  = (maxX - minX) / 2.0;
            double halfH  = (maxY - minY) / 2.0;
            // Radius circumscribes the clash bbox; arm extends just past the circle, capped at 3 ft
            double radius = Math.Max(0.25, Math.Max(halfW, halfH));
            double armLen = Math.Max(0.5, Math.Min(radius * 1.5, 3.0));

            // ── FilledRegion (circular) ───────────────────────────────────────
            bool fallback   = !clash.Group1.RuleColored;
            string hexColor = (clash.Group1.ColorHex ?? "#888888").TrimStart('#').ToUpperInvariant();
            string cacheKey = fallback ? $"{hexColor}_FB" : $"{hexColor}_{FillStyle}";
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
                    // Two semicircular arcs form a closed circular loop
                    var ctr  = new XYZ(cx, cy, 0);
                    var arc1 = Arc.Create(ctr, radius, 0,        Math.PI, XYZ.BasisX, XYZ.BasisY);
                    var arc2 = Arc.Create(ctr, radius, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY);
                    var loop = new CurveLoop();
                    loop.Append(arc1);
                    loop.Append(arc2);
                    var fr = FilledRegion.Create(doc, typeId, view.Id, new List<CurveLoop> { loop });
                    fr.LookupParameter("Mark")?.Set("LemoineCD");
                }
                catch (Exception __lex) { LemoineLog.Swallowed("ClashDimension: create clash filled region", __lex); }
            }

            // ── Cross lines ───────────────────────────────────────────────────
            Reference? hRef = null;
            Reference? vRef = null;

            if (DimTarget == "Centre")
            {
                var hLeft  = CreateLine(doc, view, lineStyleId, new XYZ(cx - armLen, cy, 0), new XYZ(cx,       cy, 0));
                var hRight = CreateLine(doc, view, lineStyleId, new XYZ(cx,       cy, 0), new XYZ(cx + armLen, cy, 0));
                var vBot   = CreateLine(doc, view, lineStyleId, new XYZ(cx, cy - armLen, 0), new XYZ(cx, cy,       0));
                var vTop   = CreateLine(doc, view, lineStyleId, new XYZ(cx, cy,       0), new XYZ(cx, cy + armLen, 0));

                if (hLeft != null && hRight != null && vBot != null && vTop != null)
                {
                    hRef = hLeft.GeometryCurve.GetEndPointReference(1);
                    vRef = vBot.GeometryCurve.GetEndPointReference(1);
                }
            }
            else
            {
                var hLine = CreateLine(doc, view, lineStyleId, new XYZ(cx - armLen, cy, 0), new XYZ(cx + armLen, cy, 0));
                var vLine = CreateLine(doc, view, lineStyleId, new XYZ(cx, cy - armLen, 0), new XYZ(cx, cy + armLen, 0));

                if (hLine != null && vLine != null)
                {
                    hRef = hLine.GeometryCurve.GetEndPointReference(1);
                    vRef = vLine.GeometryCurve.GetEndPointReference(1);
                }
            }

            if (hRef == null || vRef == null) return null;
            return new ClashMarker { Cx = cx, Cy = cy, HRef = hRef, VRef = vRef };
        }

        // ── Grouped dimension placement ───────────────────────────────────────

        // One clash reference + its centre, queued for dimensioning to a single edge.
        private struct DimItem
        {
            public Reference Ref;   // HRef (X-measuring) or VRef (Y-measuring)
            public double    Mx;
            public double    My;
        }

        // Selects the single nearest X-measuring reference (vertical grid / X-facing slab face)
        // and the single nearest Y-measuring reference (horizontal grid / Y-facing slab face)
        // across all selected grids and floors, then places at most 2 dimensions — one per axis.
        private int PlaceGroupedDimensions(
            Document doc, View view, List<ClashMarker> markers,
            DimensionType? dimType, double dimLineOffsetFt, double groupTolFt)
        {
            if (dimType == null)
            {
                if (markers.Count > 0)
                    Log("No dimension type available — dimensions skipped.", "info");
                return 0;
            }
            if (markers.Count == 0) return 0;

            // Marker centroid — used to choose the nearest reference per axis.
            double centX = markers.Average(m => m.Cx);
            double centY = markers.Average(m => m.Cy);

            var xCandidates = new List<(Reference r, double edgeCoord, string label)>();
            var yCandidates = new List<(Reference r, double edgeCoord, string label)>();

            // ── Grids ─────────────────────────────────────────────────────────
            for (int i = 0; i < GridIds.Count; i++)
            {
                long linkId = (i < GridLinkIds.Count) ? GridLinkIds[i] : 0L;
                RevitLinkInstance? linkInst = null;
                Grid? grid;
                if (linkId == 0)
                    grid = doc.GetElement(new ElementId(GridIds[i])) as Grid;
                else
                {
                    linkInst = doc.GetElement(new ElementId(linkId)) as RevitLinkInstance;
                    var ld   = linkInst?.GetLinkDocument();
                    grid     = ld?.GetElement(new ElementId(GridIds[i])) as Grid;
                }
                if (grid == null) continue;

                var gridCurve = grid.Curve as Line;
                if (gridCurve == null) continue;
                var tx  = linkInst?.GetTotalTransform() ?? Transform.Identity;
                XYZ dir = tx.OfVector(gridCurve.Direction).Normalize();
                bool mX = Math.Abs(dir.Y) > Math.Abs(dir.X);   // vertical grid → measure X
                XYZ gp  = tx.OfPoint(gridCurve.GetEndPoint(0));

                Reference gridRef = new Reference(grid);
                if (linkInst != null) gridRef = gridRef.CreateLinkReference(linkInst);

                if (mX) xCandidates.Add((gridRef, gp.X, $"Grid {grid.Name}"));
                else    yCandidates.Add((gridRef, gp.Y, $"Grid {grid.Name}"));
            }

            // ── Floors ────────────────────────────────────────────────────────
            for (int i = 0; i < FloorIds.Count; i++)
            {
                long linkId = (i < FloorLinkIds.Count) ? FloorLinkIds[i] : 0L;
                RevitLinkInstance? linkInst = null;
                Floor? floor;
                if (linkId == 0)
                    floor = doc.GetElement(new ElementId(FloorIds[i])) as Floor;
                else
                {
                    linkInst = doc.GetElement(new ElementId(linkId)) as RevitLinkInstance;
                    var ld   = linkInst?.GetLinkDocument();
                    floor    = ld?.GetElement(new ElementId(FloorIds[i])) as Floor;
                }
                if (floor == null) continue;

                try
                {
                    var geomOpts = new Options { ComputeReferences = true };
                    var geom = floor.get_Geometry(geomOpts);
                    var tx   = linkInst?.GetTotalTransform() ?? Transform.Identity;

                    foreach (GeometryObject gobj in geom)
                    {
                        if (!(gobj is Solid solid)) continue;
                        foreach (Face face in solid.Faces)
                        {
                            if (!(face is PlanarFace pf)) continue;
                            if (Math.Abs(pf.FaceNormal.Z) > 0.1) continue;
                            var faceRef = pf.Reference;
                            if (faceRef == null) continue;
                            if (linkInst != null) faceRef = faceRef.CreateLinkReference(linkInst);
                            XYZ faceN = tx.OfVector(pf.FaceNormal).Normalize();
                            XYZ o     = tx.OfPoint(pf.Origin);
                            if (Math.Abs(faceN.Y) > Math.Abs(faceN.X))
                                yCandidates.Add((faceRef, o.Y, $"Floor {floor.Name} (Y)"));
                            else
                                xCandidates.Add((faceRef, o.X, $"Floor {floor.Name} (X)"));
                        }
                    }
                }
                catch (Exception ex) { Log($"Floor candidates ({floor.Name}): {ex.Message}", "info"); }
            }

            if (xCandidates.Count == 0 && yCandidates.Count == 0) return 0;

            // Pick the single nearest candidate per axis to the marker centroid.
            (Reference r, double edgeCoord, string label)? bestX = null, bestY = null;
            double dBX = double.MaxValue, dBY = double.MaxValue;
            foreach (var c in xCandidates) { double d = Math.Abs(centX - c.edgeCoord); if (d < dBX) { dBX = d; bestX = c; } }
            foreach (var c in yCandidates) { double d = Math.Abs(centY - c.edgeCoord); if (d < dBY) { dBY = d; bestY = c; } }

            int placed = 0;
            if (bestX != null)
            {
                var xItems = markers.Select(m => new DimItem { Ref = m.HRef, Mx = m.Cx, My = m.Cy }).ToList();
                placed += EmitEdgeDimensions(doc, view, dimType, bestX.Value.r, true, bestX.Value.edgeCoord,
                    xItems, dimLineOffsetFt, groupTolFt, bestX.Value.label);
            }
            if (bestY != null)
            {
                var yItems = markers.Select(m => new DimItem { Ref = m.VRef, Mx = m.Cx, My = m.Cy }).ToList();
                placed += EmitEdgeDimensions(doc, view, dimType, bestY.Value.r, false, bestY.Value.edgeCoord,
                    yItems, dimLineOffsetFt, groupTolFt, bestY.Value.label);
            }
            return placed;
        }

        // Places dimensions from one edge to a set of clash references, grouping clashes
        // that line up with the edge so they share a single dimension instead of stacking:
        //   • Run PARALLEL to the edge (same perpendicular distance, spread along it)
        //       → one single dimension for the group.
        //   • Run PERPENDICULAR to the edge (one line, varying distance)
        //       → one chained dimension (edge → each clash).
        //   • Anything else (skew / isolated) → a plain single dimension per clash.
        // measureIsX selects a horizontal dimension line (measuring X to a constant-X edge)
        // vs a vertical one (measuring Y to a constant-Y edge). edgeCoord is the edge's
        // coordinate on the measured axis; signed distance to it keeps opposite sides apart.
        // Returns the number of dimensions actually placed.
        private int EmitEdgeDimensions(
            Document doc, View view, DimensionType dimType,
            Reference edgeRef, bool measureIsX, double edgeCoord,
            List<DimItem> items, double dimLineOffsetFt, double groupTolFt, string ctx)
        {
            int n = items.Count;
            if (n == 0) return 0;

            // measure = signed distance to the edge; along = position parallel to the edge.
            var measure = new double[n];
            var along   = new double[n];
            for (int i = 0; i < n; i++)
            {
                measure[i] = (measureIsX ? items[i].Mx : items[i].My) - edgeCoord;
                along[i]   =  measureIsX ? items[i].My : items[i].Mx;
            }

            var used   = new bool[n];
            int placed = 0;
            // Along-edge gap that breaks a parallel run; 0 (or less) = never split.
            double clusterBreakFt = ClusterGapMm > 0 ? ClusterGapMm / 304.8 : double.MaxValue;

            if (groupTolFt > 0)
            {
                // PARALLEL groups: same signed distance band, split into clusters along the
                // edge so far-apart rows do not collapse into one dimension.
                foreach (var g in Enumerable.Range(0, n)
                             .GroupBy(i => (int)Math.Round(measure[i] / groupTolFt)))
                {
                    var band = g.ToList();
                    if (band.Count < 2) continue;
                    band.Sort((a, b) => along[a].CompareTo(along[b]));

                    int start = 0;
                    for (int k = 1; k <= band.Count; k++)
                    {
                        bool boundary = k == band.Count
                                     || along[band[k]] - along[band[k - 1]] > clusterBreakFt;
                        if (!boundary) continue;

                        int cnt = k - start;
                        if (cnt >= 2 && along[band[k - 1]] - along[band[start]] > groupTolFt)
                        {
                            int rep = band[start + cnt / 2];   // representative near the cluster's middle
                            placed += EmitSingle(doc, view, dimType, edgeRef, items[rep], measure[rep],
                                                 measureIsX, dimLineOffsetFt, ctx);
                            for (int j = start; j < k; j++) used[band[j]] = true;
                        }
                        start = k;
                    }
                }

                // PERPENDICULAR groups: same along-edge band, varying distance → chain.
                foreach (var g in Enumerable.Range(0, n).Where(i => !used[i])
                             .GroupBy(i => (int)Math.Round(along[i] / groupTolFt)))
                {
                    var idx = g.ToList();
                    if (idx.Count < 2) continue;
                    idx.Sort((a, b) => measure[a].CompareTo(measure[b]));

                    // Drop references on the edge (zero gap) and collapse near-coincident
                    // positions so Revit does not see a zero-length segment.
                    var chain = new List<int>();
                    double last = double.NaN;
                    foreach (var i in idx)
                    {
                        if (Math.Abs(measure[i]) < MinSegFt) continue;
                        if (!double.IsNaN(last) && Math.Abs(measure[i] - last) < MinSegFt) continue;
                        chain.Add(i);
                        last = measure[i];
                    }
                    if (chain.Count < 2) continue;
                    if (measure[chain[chain.Count - 1]] - measure[chain[0]] <= groupTolFt) continue;

                    var refs = new ReferenceArray();
                    refs.Append(edgeRef);
                    foreach (var i in chain) refs.Append(items[i].Ref);

                    double alongAbs   = chain.Average(i => along[i]);
                    double measureAbs = edgeCoord + chain.Average(i => measure[i]);
                    int got = EmitChain(doc, view, dimType, refs, measureIsX, alongAbs, measureAbs, dimLineOffsetFt, ctx);
                    placed += got;
                    if (got > 0) foreach (var i in idx) used[i] = true;
                }
            }

            // Remaining clashes (skew / isolated, or grouping disabled): one dim each.
            for (int i = 0; i < n; i++)
                if (!used[i])
                    placed += EmitSingle(doc, view, dimType, edgeRef, items[i], measure[i],
                                         measureIsX, dimLineOffsetFt, ctx);

            return placed;
        }

        // Returns 1 if a dimension was placed, 0 otherwise.
        private int EmitSingle(
            Document doc, View view, DimensionType dimType,
            Reference edgeRef, DimItem it, double measureSigned, bool measureIsX, double dimLineOffsetFt, string ctx)
        {
            if (Math.Abs(measureSigned) < MinSegFt) return 0;   // clash sits on the edge → no measurable gap
            try
            {
                var refs = new ReferenceArray();
                refs.Append(it.Ref);
                refs.Append(edgeRef);
                double alongFixed    = (measureIsX ? it.My : it.Mx) + dimLineOffsetFt;
                double measureCentre =  measureIsX ? it.Mx : it.My;
                var dimLine = MakeDimLine(measureIsX, alongFixed, measureCentre);
                var dim = doc.Create.NewDimension(view, dimLine, refs, dimType);
                if (dim == null) { Log($"{ctx}: dimension not created (null result).", "info"); return 0; }
                dim.LookupParameter("Mark")?.Set("LemoineCD");
                return 1;
            }
            catch (Exception ex) { Log($"{ctx}: {ex.Message}", "info"); return 0; }
        }

        // Returns 1 if a dimension was placed, 0 otherwise.
        private int EmitChain(
            Document doc, View view, DimensionType dimType,
            ReferenceArray refs, bool measureIsX,
            double alongAbs, double measureCentre, double dimLineOffsetFt, string ctx)
        {
            try
            {
                double alongFixed = alongAbs + dimLineOffsetFt;
                var dimLine = MakeDimLine(measureIsX, alongFixed, measureCentre);
                var dim = doc.Create.NewDimension(view, dimLine, refs, dimType);
                if (dim == null) { Log($"{ctx} (chain): dimension not created (null result).", "info"); return 0; }
                dim.LookupParameter("Mark")?.Set("LemoineCD");
                return 1;
            }
            catch (Exception ex) { Log($"{ctx} (chain): {ex.Message}", "info"); return 0; }
        }

        // Builds the dimension line. Horizontal (measures X) sits at a fixed Y; vertical
        // (measures Y) sits at a fixed X. The line is run long so it spans all references.
        private static Line MakeDimLine(bool measureIsX, double alongFixed, double measureCentre)
        {
            const double extent = 1000.0;
            return measureIsX
                ? Line.CreateBound(new XYZ(measureCentre - extent, alongFixed, 0), new XYZ(measureCentre + extent, alongFixed, 0))
                : Line.CreateBound(new XYZ(alongFixed, measureCentre - extent, 0), new XYZ(alongFixed, measureCentre + extent, 0));
        }

        // ── FilledRegionType management ───────────────────────────────────────

        // fallback = element matched no rule: force a hatch pattern (not solid/outline) so
        // unruled clashes are obvious even at a glance, independent of the FillStyle setting.
        private ElementId? GetOrCreateFilledRegionType(Document doc, string hexColor, bool fallback)
        {
            string suffix   = fallback ? "FB" : (FillStyle == "Solid" ? "S" : "O");
            string typeName = $"LemoineClash_{hexColor}_{suffix}";

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>()
                .FirstOrDefault(t => t.Name == typeName);
            if (existing != null) return existing.Id;

            var template = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>()
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
                // Hatch if the document has one; otherwise solid so the colour still reads.
                var hatchId = GetHatchFillId(doc);
                newType.ForegroundPatternId = hatchId != ElementId.InvalidElementId ? hatchId : GetSolidFillId(doc);
            }
            else if (FillStyle == "Solid")
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

        // ── Detail line creation ──────────────────────────────────────────────

        private static DetailCurve? CreateLine(Document doc, View view, ElementId lineStyleId, XYZ start, XYZ end)
        {
            var line = Line.CreateBound(start, end);
            var dc   = doc.Create.NewDetailCurve(view, line);
            dc.LookupParameter("Mark")?.Set("LemoineCD");

            if (lineStyleId != ElementId.InvalidElementId)
            {
                var gs = doc.GetElement(lineStyleId) as GraphicsStyle;
                if (gs != null) try { dc.LineStyle = gs; } catch (Exception __lex) { LemoineLog.Swallowed("ClashDimension: apply detail-curve line style", __lex); }
            }
            return dc;
        }

        // ── Cleanup ───────────────────────────────────────────────────────────

        private void ClearPreviousAnnotations(Document doc)
        {
            foreach (var viewId in ViewIds)
            {
                var toDelete = new List<ElementId>();

                try
                {
                    toDelete.AddRange(new FilteredElementCollector(doc, viewId)
                        .OfCategory(BuiltInCategory.OST_Lines)
                        .WhereElementIsNotElementType()
                        .ToElements()
                        .Where(e => e.LookupParameter("Mark")?.AsString() == "LemoineCD")
                        .Select(e => e.Id));
                }
                catch (Exception __lex) { LemoineLog.Swallowed("ClashDimension: collect tagged lines for cleanup", __lex); }

                try
                {
                    toDelete.AddRange(new FilteredElementCollector(doc, viewId)
                        .OfClass(typeof(FilledRegion))
                        .ToElements()
                        .Where(e => e.LookupParameter("Mark")?.AsString() == "LemoineCD")
                        .Select(e => e.Id));
                }
                catch (Exception __lex) { LemoineLog.Swallowed("ClashDimension: collect tagged filled regions for cleanup", __lex); }

                try
                {
                    toDelete.AddRange(new FilteredElementCollector(doc, viewId)
                        .OfClass(typeof(Dimension))
                        .ToElements()
                        .Where(e => e.LookupParameter("Mark")?.AsString() == "LemoineCD")
                        .Select(e => e.Id));
                }
                catch (Exception __lex) { LemoineLog.Swallowed("ClashDimension: collect tagged dimensions for cleanup", __lex); }

                foreach (var id in toDelete)
                    try { doc.Delete(id); } catch (Exception __lex) { LemoineLog.Swallowed("ClashDimension: delete tagged annotation", __lex); }
            }
        }

        // ── Helper utilities ──────────────────────────────────────────────────

        private DimensionType? ResolveDimType(Document doc)
        {
            DimensionType? dimType = null;
            if (!string.IsNullOrEmpty(DimStyleName))
            {
                dimType = new FilteredElementCollector(doc)
                    .OfClass(typeof(DimensionType))
                    .Cast<DimensionType>()
                    .FirstOrDefault(dt => dt.Name == DimStyleName
                                      && dt.StyleType == DimensionStyleType.Linear);
                if (dimType == null)
                    Log($"Dimension style '{DimStyleName}' not found or is not a linear type — using project default.", "info");
            }
            if (dimType == null)
            {
                var defId = doc.GetDefaultElementTypeId(ElementTypeGroup.LinearDimensionType);
                if (defId != null && defId != ElementId.InvalidElementId)
                    dimType = doc.GetElement(defId) as DimensionType;
            }
            // Final fallback: first linear type in the document
            if (dimType == null)
            {
                dimType = new FilteredElementCollector(doc)
                    .OfClass(typeof(DimensionType))
                    .Cast<DimensionType>()
                    .FirstOrDefault(dt => dt.StyleType == DimensionStyleType.Linear);
            }
            if (dimType == null)
                Log("No linear dimension type found in document — dimensions will be skipped.", "fail");
            return dimType;
        }

        private ElementId ResolveLineStyleId(Document doc)
        {
            if (string.IsNullOrEmpty(CrossLineTypeName)) return ElementId.InvalidElementId;
            try
            {
                var linesCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                if (linesCat == null) return ElementId.InvalidElementId;
                foreach (Category sub in linesCat.SubCategories)
                {
                    if (sub.Name == CrossLineTypeName)
                    {
                        var gs = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                        return gs?.Id ?? ElementId.InvalidElementId;
                    }
                }
            }
            catch (Exception __lex) { LemoineLog.Swallowed("ClashDimension: resolve cross-line graphics style", __lex); }
            return ElementId.InvalidElementId;
        }

        private static ElementId GetSolidFillId(Document doc)
        {
            foreach (var fp in new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>())
            {
                try { if (fp.GetFillPattern().IsSolidFill) return fp.Id; }
                catch (Exception __lex) { LemoineLog.Swallowed("ClashDimension: inspect fill pattern", __lex); }
            }
            return ElementId.InvalidElementId;
        }

        // First drafting, non-solid fill pattern — used to mark unruled (fallback) clashes.
        private static ElementId GetHatchFillId(Document doc)
        {
            foreach (var fp in new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>())
            {
                try
                {
                    var pat = fp.GetFillPattern();
                    if (!pat.IsSolidFill && pat.Target == FillPatternTarget.Drafting) return fp.Id;
                }
                catch (Exception __lex) { LemoineLog.Swallowed("ClashDimension: inspect hatch fill pattern", __lex); }
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
            catch (Exception __lex) { LemoineLog.Swallowed("ClashDimension: parse hex colour", __lex); }
            return null;
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private void Log(string text, string status) => PushLog?.Invoke(text, status);
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
