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
        public List<string>    Group1RuleKeys    { get; set; } = new List<string>();
        public List<string>    Group2RuleKeys    { get; set; } = new List<string>();
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
            public FilterRuleConfig   Rule          = null!;
            public FilterTradeConfig  Trade         = null!;
            public BoundingBoxXYZ     HostBBox      = null!;
        }

        private class ClashResult
        {
            public ClashElement  Group1    = null!;
            public ClashElement  Group2    = null!;
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
            // 1. Resolve rules
            var group1 = ResolveRules(Group1RuleKeys);
            var group2 = ResolveRules(Group2RuleKeys);

            if (group1.Count == 0)
            {
                Log("Group 1 rules could not be resolved — check Auto Filters settings.", "fail");
                fail++; return;
            }
            if (group2.Count == 0)
            {
                Log("Group 2 rules could not be resolved — check Auto Filters settings.", "fail");
                fail++; return;
            }

            // 2. Collect source documents (host + all loaded links)
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

            // 3. Scan elements
            var group1Elements = ScanGroup(group1, sources);
            var group2Elements = ScanGroup(group2, sources);
            Log($"Group 1: {group1Elements.Count} element(s)   Group 2: {group2Elements.Count} element(s)", "info");

            Progress(30, pass, fail, skip);

            // 4. Find clashes
            double toleranceFt = ToleranceMm / 304.8;
            var clashes = FindClashes(group1Elements, group2Elements, MaxClashes);
            bool hitLimit = clashes.Count >= MaxClashes;

            if (clashes.Count == 0)
            {
                Log("No clashes detected.", "pass");
                if (group1Elements.Count > 0 && group2Elements.Count > 0)
                {
                    var b1 = group1Elements[0].HostBBox;
                    var b2 = group2Elements[0].HostBBox;
                    Log($"  Diag G1[0] ({group1Elements[0].Rule.Name}): " +
                        $"X[{b1.Min.X:F1},{b1.Max.X:F1}] Y[{b1.Min.Y:F1},{b1.Max.Y:F1}] Z[{b1.Min.Z:F1},{b1.Max.Z:F1}]", "info");
                    Log($"  Diag G2[0] ({group2Elements[0].Rule.Name}): " +
                        $"X[{b2.Min.X:F1},{b2.Max.X:F1}] Y[{b2.Min.Y:F1},{b2.Max.Y:F1}] Z[{b2.Min.Z:F1},{b2.Max.Z:F1}]", "info");
                }
                pass = 1; return;
            }

            Log(hitLimit
                ? $"Found {clashes.Count} clash(es) — limit of {MaxClashes} reached. " +
                  "Increase Max Clashes in Settings to detect more."
                : $"Found {clashes.Count} clash(es).",
                hitLimit ? "info" : "info");

            Progress(40, pass, fail, skip);

            // 5. Resolve dimension type
            DimensionType? dimType = ResolveDimType(doc);

            // 6. Create annotations in a single transaction
            using (var tx = new Transaction(doc, "Lemoine — Clash Dimension"))
            {
                tx.Start();
                ConfigureFailures(tx);

                if (ClearPrevious) ClearPreviousAnnotations(doc);

                // Resolve line style for cross arms
                ElementId lineStyleId = ResolveLineStyleId(doc);

                // Cache FilledRegionType per hex+style pair to avoid repeated creation
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

        // ── Rule resolution ───────────────────────────────────────────────────

        private static List<(FilterTradeConfig trade, FilterRuleConfig rule)> ResolveRules(
            List<string> persistKeys)
        {
            var result = new List<(FilterTradeConfig, FilterRuleConfig)>();
            var keySet = new HashSet<string>(persistKeys);
            foreach (var trade in AutoFiltersSettings.Instance.Trades)
                foreach (var rule in trade.Rules)
                    if (keySet.Contains($"{trade.Id}::{rule.Id}"))
                        result.Add((trade, rule));
            return result;
        }

        // ── Element scanning ──────────────────────────────────────────────────

        private List<ClashElement> ScanGroup(
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
                    Log($"Rule '{rule.Name}' has no categories configured — skipped. " +
                        "Add at least one BuiltInCategory to this rule in Auto Filters.", "info");
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

                            var hostBBox = GetHostBBox(el, tx);
                            if (hostBBox == null) continue;

                            result.Add(new ClashElement
                            {
                                Doc           = srcDoc,
                                LinkInstance  = link,
                                HostTransform = tx,
                                Id            = el.Id,
                                Rule          = rule,
                                Trade         = trade,
                                HostBBox      = hostBBox,
                            });
                            srcCount++;
                        }
                    }
                    if (srcCount > 0 || link != null)
                        Log($"  [{srcDoc.Title}] '{rule.Name}': {srcCount} element(s)", "info");
                    ruleTotal += srcCount;
                }
                if (ruleTotal == 0)
                    Log($"  Rule '{rule.Name}': 0 matching elements in any document — check filter categories/match criteria.", "info");
            }

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

            // Transform all 8 corners to host coordinates and compute AABB
            var pts = new XYZ[]
            {
                new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
                new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z),
            };

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var p in pts)
            {
                XYZ t = hostTransform.OfPoint(p);
                if (t.X < minX) minX = t.X; if (t.X > maxX) maxX = t.X;
                if (t.Y < minY) minY = t.Y; if (t.Y > maxY) maxY = t.Y;
                if (t.Z < minZ) minZ = t.Z; if (t.Z > maxZ) maxZ = t.Z;
            }

            var result = new BoundingBoxXYZ();
            result.Min = new XYZ(minX, minY, minZ);
            result.Max = new XYZ(maxX, maxY, maxZ);
            return result;
        }

        // ── Clash detection ───────────────────────────────────────────────────

        private static List<ClashResult> FindClashes(
            List<ClashElement> group1, List<ClashElement> group2, int maxClashes)
        {
            const double minOverlap = 0.001;
            var results = new List<ClashResult>();

            foreach (var g1 in group1)
            {
                var b1 = g1.HostBBox;
                foreach (var g2 in group2)
                {
                    var b2 = g2.HostBBox;
                    double ox = Math.Min(b1.Max.X, b2.Max.X) - Math.Max(b1.Min.X, b2.Min.X);
                    double oy = Math.Min(b1.Max.Y, b2.Max.Y) - Math.Max(b1.Min.Y, b2.Min.Y);
                    double oz = Math.Min(b1.Max.Z, b2.Max.Z) - Math.Max(b1.Min.Z, b2.Min.Z);

                    if (ox > minOverlap && oy > minOverlap && oz > minOverlap)
                    {
                        var overlap = new BoundingBoxXYZ();
                        overlap.Min = new XYZ(
                            Math.Max(b1.Min.X, b2.Min.X),
                            Math.Max(b1.Min.Y, b2.Min.Y),
                            Math.Max(b1.Min.Z, b2.Min.Z));
                        overlap.Max = new XYZ(
                            Math.Min(b1.Max.X, b2.Max.X),
                            Math.Min(b1.Max.Y, b2.Max.Y),
                            Math.Min(b1.Max.Z, b2.Max.Z));

                        results.Add(new ClashResult { Group1 = g1, Group2 = g2, OverlapBBox = overlap });
                        if (results.Count >= maxClashes) return results;
                    }
                }
            }
            return results;
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

            double cx      = (minX + maxX) / 2.0;
            double cy      = (minY + maxY) / 2.0;
            double halfW   = (maxX - minX) / 2.0;
            double halfH   = (maxY - minY) / 2.0;
            double armLen  = Math.Max(0.5, Math.Max(halfW, halfH) * 1.5);  // min 0.5 ft ≈ 150 mm

            // ── FilledRegion ─────────────────────────────────────────────────
            string hexColor = (clash.Group1.Rule.SurfColor ?? "#888888").TrimStart('#').ToUpperInvariant();
            string cacheKey = $"{hexColor}_{FillStyle}";
            if (!regionTypeCache.TryGetValue(cacheKey, out ElementId? typeId))
            {
                typeId = GetOrCreateFilledRegionType(doc, hexColor);
                regionTypeCache[cacheKey] = typeId;
            }

            if (typeId == null || typeId == ElementId.InvalidElementId)
            {
                Log("Could not create FilledRegionType — no template type found in project. " +
                    "Cross annotation will be placed without a filled region.", "info");
            }
            else
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

            // ── Cross lines ──────────────────────────────────────────────────
            double dimLineOffsetFt = DimLineOffsetMm / 304.8;

            if (DimTarget == "Centre")
            {
                // 4 half-arms: each arm has its centre point as a shared endpoint
                var hLeft  = CreateLine(doc, view, lineStyleId, new XYZ(cx - armLen, cy, 0), new XYZ(cx,       cy, 0));
                var hRight = CreateLine(doc, view, lineStyleId, new XYZ(cx,       cy, 0), new XYZ(cx + armLen, cy, 0));
                var vBot   = CreateLine(doc, view, lineStyleId, new XYZ(cx, cy - armLen, 0), new XYZ(cx, cy,       0));
                var vTop   = CreateLine(doc, view, lineStyleId, new XYZ(cx, cy,       0), new XYZ(cx, cy + armLen, 0));

                if (hLeft != null && hRight != null && vBot != null && vTop != null)
                {
                    // Centre reference: end(1) of left half = end(0) of right half = both at (cx,cy)
                    var hCentreRef = hLeft.GeometryCurve.GetEndPointReference(1);
                    var vCentreRef = vBot.GeometryCurve.GetEndPointReference(1);

                    CreateGridAndFloorDimensions(doc, view, hCentreRef, vCentreRef,
                        cx, cy, dimLineOffsetFt, dimType, isFromCenter: true);
                }
            }
            else
            {
                // 2 full-length arms
                var hLine = CreateLine(doc, view, lineStyleId, new XYZ(cx - armLen, cy, 0), new XYZ(cx + armLen, cy, 0));
                var vLine = CreateLine(doc, view, lineStyleId, new XYZ(cx, cy - armLen, 0), new XYZ(cx, cy + armLen, 0));

                if (hLine != null && vLine != null)
                {
                    // GetEndPointReference(0) = start, (1) = end
                    // hLine: (0) = left tip, (1) = right tip
                    // vLine: (0) = bottom tip, (1) = top tip
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
                bool isVGrid = Math.Abs(dir.Y) > Math.Abs(dir.X);  // N-S grid → measure X

                // Lift reference to host context if element lives in a linked file
                Reference gridRef = new Reference(grid);
                if (linkInst != null)
                    gridRef = gridRef.CreateLinkReference(linkInst);

                var refs = new ReferenceArray();
                Line dimLine;
                double extent = 1000.0;

                if (isVGrid)
                {
                    double dimY = cy + dimLineOffsetFt;
                    dimLine = Line.CreateBound(
                        new XYZ(cx - extent, dimY, 0),
                        new XYZ(cx + extent, dimY, 0));
                    refs.Append(hRef);
                    refs.Append(gridRef);
                }
                else
                {
                    double dimX = cx + dimLineOffsetFt;
                    dimLine = Line.CreateBound(
                        new XYZ(dimX, cy - extent, 0),
                        new XYZ(dimX, cy + extent, 0));
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

                // Collect ALL vertical planar faces — each one gets its own dimension
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
                        bool isVFace = Math.Abs(faceN.Y) > Math.Abs(faceN.X);  // N/S-facing face

                        var refs = new ReferenceArray();
                        Line dimLine;

                        if (isVFace)
                        {
                            double dimY = cy + dimLineOffsetFt;
                            dimLine = Line.CreateBound(
                                new XYZ(cx - extent, dimY, 0),
                                new XYZ(cx + extent, dimY, 0));
                            refs.Append(hRef);
                            refs.Append(faceRef);
                        }
                        else
                        {
                            double dimX = cx + dimLineOffsetFt;
                            dimLine = Line.CreateBound(
                                new XYZ(dimX, cy - extent, 0),
                                new XYZ(dimX, cy + extent, 0));
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
                // Outline: no foreground fill
                newType.ForegroundPatternId = ElementId.InvalidElementId;
            }

            return newType.Id;
        }

        // ── Detail line creation ──────────────────────────────────────────────

        private static DetailCurve? CreateLine(Document doc, View view, ElementId lineStyleId,
            XYZ start, XYZ end)
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

                // Detail lines with Mark="LemoineCD"
                try
                {
                    toDelete.AddRange(
                        new FilteredElementCollector(doc, viewId)
                        .OfCategory(BuiltInCategory.OST_Lines)
                        .WhereElementIsNotElementType()
                        .ToElements()
                        .Where(e => e.LookupParameter("Mark")?.AsString() == "LemoineCD")
                        .Select(e => e.Id));
                }
                catch { }

                // FilledRegions with Mark="LemoineCD"
                try
                {
                    toDelete.AddRange(
                        new FilteredElementCollector(doc, viewId)
                        .OfClass(typeof(FilledRegion))
                        .ToElements()
                        .Where(e => e.LookupParameter("Mark")?.AsString() == "LemoineCD")
                        .Select(e => e.Id));
                }
                catch { }

                // Dimensions with Mark="LemoineCD"
                try
                {
                    toDelete.AddRange(
                        new FilteredElementCollector(doc, viewId)
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
                    .FirstOrDefault(dt => dt.Name == DimStyleName);
            }
            if (dimType == null)
            {
                var defId = doc.GetDefaultElementTypeId(ElementTypeGroup.LinearDimensionType);
                if (defId != null && defId != ElementId.InvalidElementId)
                    dimType = doc.GetElement(defId) as DimensionType;
            }
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
