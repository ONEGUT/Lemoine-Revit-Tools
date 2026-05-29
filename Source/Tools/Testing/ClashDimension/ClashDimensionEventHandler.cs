using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Tools.AutoFilters;

using RevitColor = Autodesk.Revit.DB.Color;

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
        public string          DimTarget         { get; set; } = "Edge";
        public string          FillStyle         { get; set; } = "Solid";
        public string          CrossLineTypeName { get; set; } = "";
        public bool            ClearPrevious     { get; set; } = true;
        public int             MaxClashes        { get; set; } = 500;

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Testing.ClashDimensionEventHandler";

        // Default colours for Categories / Elements modes (no owning filter rule).
        private const string DefaultColor1 = "#E78F36";
        private const string DefaultColor2 = "#3FB3D9";

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
            var group1Elements = ScanGroupSpec(doc, Group1Spec, sources, DefaultColor1, "Group 1");
            var group2Elements = ScanGroupSpec(doc, Group2Spec, sources, DefaultColor2, "Group 2");
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

                int done = 0;
                foreach (var clash in clashes)
                {
                    foreach (var viewId in ViewIds)
                    {
                        var view = doc.GetElement(viewId) as View;
                        if (view == null) continue;

                        try
                        {
                            CreateClashAnnotation(doc, view, clash, dimType,
                                lineStyleId, toleranceFt, regionTypeCache);
                            pass++;
                        }
                        catch (Exception ex)
                        {
                            Log($"Error in '{view.Name}': {ex.Message}", "fail");
                            fail++;
                        }
                    }

                    done++;
                    Progress(40 + (int)(done * 55.0 / clashes.Count), pass, fail, skip);
                }

                tx.Commit();
            }

            Log($"Done — {pass} annotation(s) placed, {fail} failed.", pass > 0 ? "pass" : "fail");
        }

        // ── Group scanning (mode-aware) ───────────────────────────────────────

        private List<ClashElement> ScanGroupSpec(
            Document hostDoc, ClashGroupSpec spec,
            List<(Document doc, RevitLinkInstance? link, Transform tx)> allSources,
            string defaultColor, string label)
        {
            switch (spec.Mode)
            {
                case "Categories":
                    return ScanCategories(spec.Categories, FilterSources(allSources, spec.SourceLinkIds), defaultColor);
                case "Elements":
                    return ScanElements(spec.ElemIds, spec.ElemLinkIds, allSources, defaultColor);
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
            List<(Document doc, RevitLinkInstance? link, Transform tx)> sources,
            string color)
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
                        result.Add(new ClashElement
                        {
                            Doc           = srcDoc,
                            LinkInstance  = link,
                            HostTransform = tx,
                            Id            = el.Id,
                            Label         = ostStr,
                            ColorHex      = color,
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
            List<(Document doc, RevitLinkInstance? link, Transform tx)> allSources,
            string color)
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

                result.Add(new ClashElement
                {
                    Doc           = src.doc,
                    LinkInstance  = src.link,
                    HostTransform = src.tx,
                    Id            = el.Id,
                    Label         = el.Name ?? "(element)",
                    ColorHex      = color,
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
                catch { }
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
            catch { }

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
                catch { }
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
                    try { ige = gi.GetInstanceGeometry(); } catch { }
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

        private void CreateClashAnnotation(
            Document doc, View view, ClashResult clash,
            DimensionType? dimType, ElementId lineStyleId,
            double toleranceFt, Dictionary<string, ElementId?> regionTypeCache)
        {
            var zone = clash.OverlapBBox;
            double minX = zone.Min.X - toleranceFt;
            double maxX = zone.Max.X + toleranceFt;
            double minY = zone.Min.Y - toleranceFt;
            double maxY = zone.Max.Y + toleranceFt;

            if (maxX - minX < 0.001 || maxY - minY < 0.001) return;

            double cx     = (minX + maxX) / 2.0;
            double cy     = (minY + maxY) / 2.0;
            double halfW  = (maxX - minX) / 2.0;
            double halfH  = (maxY - minY) / 2.0;
            double armLen = Math.Max(0.5, Math.Max(halfW, halfH) * 1.5);

            // ── FilledRegion ─────────────────────────────────────────────────
            string hexColor = (clash.Group1.ColorHex ?? "#888888").TrimStart('#').ToUpperInvariant();
            string cacheKey = $"{hexColor}_{FillStyle}";
            if (!regionTypeCache.TryGetValue(cacheKey, out ElementId? typeId))
            {
                typeId = GetOrCreateFilledRegionType(doc, hexColor);
                regionTypeCache[cacheKey] = typeId;
            }

            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                try
                {
                    var loop = new CurveLoop();
                    loop.Append(Line.CreateBound(new XYZ(minX, minY, 0), new XYZ(maxX, minY, 0)));
                    loop.Append(Line.CreateBound(new XYZ(maxX, minY, 0), new XYZ(maxX, maxY, 0)));
                    loop.Append(Line.CreateBound(new XYZ(maxX, maxY, 0), new XYZ(minX, maxY, 0)));
                    loop.Append(Line.CreateBound(new XYZ(minX, maxY, 0), new XYZ(minX, minY, 0)));
                    var fr = FilledRegion.Create(doc, typeId, view.Id, new List<CurveLoop> { loop });
                    fr.LookupParameter("Mark")?.Set("LemoineCD");
                }
                catch { }
            }

            // ── Cross lines + dimensions ─────────────────────────────────────
            double dimLineOffsetFt = DimLineOffsetMm / 304.8;

            if (DimTarget == "Centre")
            {
                var hLeft  = CreateLine(doc, view, lineStyleId, new XYZ(cx - armLen, cy, 0), new XYZ(cx,       cy, 0));
                var hRight = CreateLine(doc, view, lineStyleId, new XYZ(cx,       cy, 0), new XYZ(cx + armLen, cy, 0));
                var vBot   = CreateLine(doc, view, lineStyleId, new XYZ(cx, cy - armLen, 0), new XYZ(cx, cy,       0));
                var vTop   = CreateLine(doc, view, lineStyleId, new XYZ(cx, cy,       0), new XYZ(cx, cy + armLen, 0));

                if (hLeft != null && hRight != null && vBot != null && vTop != null)
                {
                    var hCentreRef = hLeft.GeometryCurve.GetEndPointReference(1);
                    var vCentreRef = vBot.GeometryCurve.GetEndPointReference(1);
                    CreateGridAndFloorDimensions(doc, view, hCentreRef, vCentreRef,
                        cx, cy, dimLineOffsetFt, dimType, isFromCenter: true);
                }
            }
            else
            {
                var hLine = CreateLine(doc, view, lineStyleId, new XYZ(cx - armLen, cy, 0), new XYZ(cx + armLen, cy, 0));
                var vLine = CreateLine(doc, view, lineStyleId, new XYZ(cx, cy - armLen, 0), new XYZ(cx, cy + armLen, 0));

                if (hLine != null && vLine != null)
                {
                    var hRightRef = hLine.GeometryCurve.GetEndPointReference(1);
                    var vTopRef   = vLine.GeometryCurve.GetEndPointReference(1);
                    CreateGridAndFloorDimensions(doc, view, hRightRef, vTopRef,
                        cx, cy, dimLineOffsetFt, dimType, isFromCenter: false);
                }
            }
        }

        private void CreateGridAndFloorDimensions(
            Document doc, View view,
            Reference hRef, Reference vRef,
            double cx, double cy, double dimLineOffsetFt,
            DimensionType? dimType, bool isFromCenter)
        {
            for (int i = 0; i < GridIds.Count; i++)
            {
                long linkId = (i < GridLinkIds.Count) ? GridLinkIds[i] : 0L;
                RevitLinkInstance? linkInst = null;
                Grid? grid;

                if (linkId == 0)
                {
                    grid = doc.GetElement(new ElementId(GridIds[i])) as Grid;
                }
                else
                {
                    linkInst = doc.GetElement(new ElementId(linkId)) as RevitLinkInstance;
                    var ld   = linkInst?.GetLinkDocument();
                    grid     = ld?.GetElement(new ElementId(GridIds[i])) as Grid;
                }

                if (grid == null) continue;
                CreateGridDimension(doc, view, grid, linkInst, hRef, vRef, cx, cy, dimLineOffsetFt, dimType);
            }

            for (int i = 0; i < FloorIds.Count; i++)
            {
                long linkId = (i < FloorLinkIds.Count) ? FloorLinkIds[i] : 0L;
                RevitLinkInstance? linkInst = null;
                Floor? floor;

                if (linkId == 0)
                {
                    floor = doc.GetElement(new ElementId(FloorIds[i])) as Floor;
                }
                else
                {
                    linkInst = doc.GetElement(new ElementId(linkId)) as RevitLinkInstance;
                    var ld   = linkInst?.GetLinkDocument();
                    floor    = ld?.GetElement(new ElementId(FloorIds[i])) as Floor;
                }

                if (floor == null) continue;
                CreateFloorDimension(doc, view, floor, linkInst, hRef, vRef, cx, cy, dimLineOffsetFt, dimType);
            }
        }

        private void CreateGridDimension(
            Document doc, View view, Grid grid, RevitLinkInstance? linkInst,
            Reference hRef, Reference vRef,
            double cx, double cy, double dimLineOffsetFt,
            DimensionType? dimType)
        {
            try
            {
                var gridCurve = grid.Curve as Line;
                if (gridCurve == null) return;

                XYZ dir = gridCurve.Direction.Normalize();
                if (linkInst != null)
                    dir = linkInst.GetTotalTransform().OfVector(dir).Normalize();
                bool isVGrid = Math.Abs(dir.Y) > Math.Abs(dir.X);

                Reference gridRef = new Reference(grid);
                if (linkInst != null)
                    gridRef = gridRef.CreateLinkReference(linkInst);

                var refs = new ReferenceArray();
                Line dimLine;
                double extent = 1000.0;

                if (isVGrid)
                {
                    double dimY = cy + dimLineOffsetFt;
                    dimLine = Line.CreateBound(new XYZ(cx - extent, dimY, 0), new XYZ(cx + extent, dimY, 0));
                    refs.Append(hRef);
                    refs.Append(gridRef);
                }
                else
                {
                    double dimX = cx + dimLineOffsetFt;
                    dimLine = Line.CreateBound(new XYZ(dimX, cy - extent, 0), new XYZ(dimX, cy + extent, 0));
                    refs.Append(vRef);
                    refs.Append(gridRef);
                }

                if (dimType != null)
                {
                    var dim = doc.Create.NewDimension(view, dimLine, refs, dimType);
                    dim?.LookupParameter("Mark")?.Set("LemoineCD");
                }
                else
                {
                    Log($"Grid dim ({grid.Name}): no dimension type available — skipped.", "info");
                }
            }
            catch (Exception ex)
            {
                Log($"Grid dim ({grid.Name}): {ex.Message}", "info");
            }
        }

        private void CreateFloorDimension(
            Document doc, View view, Floor floor, RevitLinkInstance? linkInst,
            Reference hRef, Reference vRef,
            double cx, double cy, double dimLineOffsetFt,
            DimensionType? dimType)
        {
            try
            {
                var geomOpts = new Options { ComputeReferences = true };
                var geom = floor.get_Geometry(geomOpts);
                var tx   = linkInst?.GetTotalTransform() ?? Transform.Identity;

                var faces = new List<(PlanarFace pf, Reference faceRef)>();
                foreach (GeometryObject obj in geom)
                {
                    if (!(obj is Solid solid)) continue;
                    foreach (Face face in solid.Faces)
                    {
                        if (!(face is PlanarFace pf)) continue;
                        if (Math.Abs(pf.FaceNormal.Z) > 0.1) continue;  // skip horizontal faces
                        var faceRef = pf.Reference;
                        if (faceRef == null) continue;
                        if (linkInst != null) faceRef = faceRef.CreateLinkReference(linkInst);
                        faces.Add((pf, faceRef));
                    }
                }

                if (faces.Count == 0)
                {
                    Log($"Floor dim ({floor.Name}): no vertical slab faces found — skipped.", "info");
                    return;
                }
                Log($"Floor dim ({floor.Name}): {faces.Count} vertical face(s).", "info");

                double extent = 1000.0;
                foreach (var (pf, faceRef) in faces)
                {
                    try
                    {
                        XYZ faceN    = tx.OfVector(pf.FaceNormal).Normalize();
                        bool isVFace = Math.Abs(faceN.Y) > Math.Abs(faceN.X);

                        var refs = new ReferenceArray();
                        Line dimLine;

                        if (isVFace)
                        {
                            double dimY = cy + dimLineOffsetFt;
                            dimLine = Line.CreateBound(new XYZ(cx - extent, dimY, 0), new XYZ(cx + extent, dimY, 0));
                            refs.Append(hRef);
                            refs.Append(faceRef);
                        }
                        else
                        {
                            double dimX = cx + dimLineOffsetFt;
                            dimLine = Line.CreateBound(new XYZ(dimX, cy - extent, 0), new XYZ(dimX, cy + extent, 0));
                            refs.Append(vRef);
                            refs.Append(faceRef);
                        }

                        if (dimType != null)
                        {
                            var dim = doc.Create.NewDimension(view, dimLine, refs, dimType);
                            dim?.LookupParameter("Mark")?.Set("LemoineCD");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Floor dim ({floor.Name}), face: {ex.Message}", "info");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Floor dim ({floor.Name}): {ex.Message}", "info");
            }
        }

        // ── FilledRegionType management ───────────────────────────────────────

        private ElementId? GetOrCreateFilledRegionType(Document doc, string hexColor)
        {
            string suffix   = FillStyle == "Solid" ? "S" : "O";
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

            if (FillStyle == "Solid")
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
                if (gs != null) try { dc.LineStyle = gs; } catch { }
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
                catch { }

                try
                {
                    toDelete.AddRange(new FilteredElementCollector(doc, viewId)
                        .OfClass(typeof(FilledRegion))
                        .ToElements()
                        .Where(e => e.LookupParameter("Mark")?.AsString() == "LemoineCD")
                        .Select(e => e.Id));
                }
                catch { }

                try
                {
                    toDelete.AddRange(new FilteredElementCollector(doc, viewId)
                        .OfClass(typeof(Dimension))
                        .ToElements()
                        .Where(e => e.LookupParameter("Mark")?.AsString() == "LemoineCD")
                        .Select(e => e.Id));
                }
                catch { }

                foreach (var id in toDelete)
                    try { doc.Delete(id); } catch { }
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
            catch { }
            return ElementId.InvalidElementId;
        }

        private static ElementId GetSolidFillId(Document doc)
        {
            foreach (var fp in new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>())
            {
                try { if (fp.GetFillPattern().IsSolidFill) return fp.Id; }
                catch { }
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
            catch { }
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
