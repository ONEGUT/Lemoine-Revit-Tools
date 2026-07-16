using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Tools.AutoFilters;
using RevitColor = Autodesk.Revit.DB.Color;

namespace LemoineTools.Tools.Ceilings
{
    public class CeilingHeatmapEventHandler : IExternalEventHandler
    {
        // ── Trade this tool registers its filters/rules under ─────────────────
        private const string CHTradeId    = "CH";
        private const string CHTradeLabel = "Ceiling Heatmap";
        private const string CHTradeColor = "#3FA7FF";

        // ── Inputs (set by ViewModel before Raise()) ──────────────────────────
        public List<ElementId>          SelectedViewIds { get; set; } = new List<ElementId>();
        // "Generate heatmap RCPs per level" mode — when non-empty, one CeilingPlan view is
        // found-or-created per level (Sanitize(level.Name) + GenerateSuffix) BEFORE the normal
        // per-view pipeline runs, and SelectedViewIds is replaced with those views for this run.
        // Mutually exclusive with picking existing views — the ViewModel only populates one.
        public List<ElementId>          GenerateForLevelIds { get; set; } = new List<ElementId>();
        public string                   GenerateSuffix      { get; set; } = "_Heatmap";
        public ElementId                GenerateTemplateId  { get; set; } = ElementId.InvalidElementId;
        public bool                     DeleteExisting  { get; set; } = true;
        public bool                     PlaceTags       { get; set; } = false;
        public double                   ElevTolerance   { get; set; } = 1.0 / 96.0; // 1/8 in → ft
        public Autodesk.Revit.DB.Color  ColorLow        { get; set; } = new Autodesk.Revit.DB.Color(0,   0,   255);
        public Autodesk.Revit.DB.Color  ColorMid        { get; set; } = new Autodesk.Revit.DB.Color(0,   255, 0);
        public Autodesk.Revit.DB.Color  ColorHigh       { get; set; } = new Autodesk.Revit.DB.Color(255, 0,   0);

        // ── Callbacks (BeginInvoke-wrapped by StepFlowWindow) ─────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }
        public Action<IReadOnlyList<ResultChip>>? OnResultChips { get; set; }

        // Per-run breakdown surfaced for the result chips (filters vs tags).
        private int _filtersCreated, _filtersReused, _tagsPlaced;

        public string GetName() => "LemoineTools.Tools.Ceilings.CeilingHeatmapEventHandler";

        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            int pass = 0, fail = 0, skip = 0;
            _filtersCreated = _filtersReused = _tagsPlaced = 0;

            try
            {
                RunHeatmap(doc, ref pass, ref fail, ref skip);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("CeilingHeatmap: run aborted", ex); Log(AppStrings.T("ceilings.heatmap.log.error", ex.Message), "fail");
                fail++;
            }
            finally
            {
                // Session-long static handler (App.CeilingHeatmapHandler) — drop the run's payload.
                SelectedViewIds     = new List<ElementId>();
                GenerateForLevelIds = new List<ElementId>();
            }

            Progress(100, pass, fail, skip);
            OnResultChips?.Invoke(new List<ResultChip>
            {
                new ResultChip("filters", _filtersCreated + _filtersReused, "LemoineGreen"),
                new ResultChip("tags",    _tagsPlaced,                      "LemoineGreen"),
                new ResultChip("failed",  fail,                             "LemoineRed"),
                new ResultChip("skipped", skip,                             "LemoineTextDim"),
            });
            Complete(pass, fail, skip);
        }

        // ─────────────────────────────────────────────────────────────────────────
        private void RunHeatmap(Document doc, ref int pass, ref int fail, ref int skip)
        {
            if (GenerateForLevelIds != null && GenerateForLevelIds.Count > 0)
            {
                var generated = GenerateRcpViews(doc, GenerateForLevelIds, GenerateSuffix, GenerateTemplateId, ref fail);
                if (generated.Count == 0)
                {
                    Log(AppStrings.T("ceilings.heatmap.log.noViewsGenerated"), "fail"); fail++; return;
                }
                SelectedViewIds = generated;
            }

            if (SelectedViewIds == null || SelectedViewIds.Count == 0)
            {
                Log(AppStrings.T("ceilings.heatmap.log.noViews"), "fail"); fail++; return;
            }

            // ── Phase 1: Scan ceiling height offsets (0–20%) ─────────────────────
            Log(AppStrings.T("ceilings.heatmap.log.scanning"), "info");

            var heightBuckets = new List<double>();
            // A ceiling visible in several selected views must be counted once, not once
            // per view — dedupe host ceilings by element id and linked ceilings by
            // (link instance id, element id) so the scanned totals are honest.
            var hostSeen   = new HashSet<long>();
            var linkedSeen = new HashSet<(long link, long el)>();

            int viewCount = SelectedViewIds.Count;
            bool cancelledInScan = false;
            for (int vi = 0; vi < viewCount; vi++)
            {
                if (RunState.CancelRequested)
                {
                    Log(AppStrings.T("common.log.stoppedByUser", vi, viewCount), "warn");
                    cancelledInScan = true;
                    break;
                }

                var viewId = SelectedViewIds[vi];
                var vp     = doc.GetElement(viewId) as ViewPlan;
                if (vp == null) { skip++; continue; }

                foreach (Element el in new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(Ceiling))
                    .WhereElementIsNotElementType())
                {
                    var hParam = el.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                    if (hParam == null) continue;   // no height-from-level value → skip, don't bucket as 0
                    if (!hostSeen.Add(el.Id.Value)) continue;  // already scanned in an earlier view
                    AddBucket(heightBuckets, hParam.AsDouble());
                }

                // Linked ceilings are always scanned — in this project ceilings
                // typically live in linked architectural models, not the host.
                ScanLinkedCeilings(doc, vp, heightBuckets, linkedSeen);

                Progress((int)((vi + 1) * 20.0 / viewCount), pass, fail, skip);
            }

            int hostCeilings = hostSeen.Count, linkedCeilings = linkedSeen.Count;
            Log(AppStrings.T("ceilings.heatmap.log.scanned", hostCeilings, linkedCeilings), "info");
            DiagnosticsLog.Info("CeilingHeatmap",
                $"scan complete — {hostCeilings} host + {linkedCeilings} linked ceilings across "
                + $"{viewCount} view(s); {heightBuckets.Count} height bucket(s).");

            // Cancelled during the scan → bail out BEFORE deleting or creating anything,
            // so an existing heatmap is left untouched (the previous behaviour deleted the
            // current filters and rebuilt the trade from a partial/empty bucket list).
            if (cancelledInScan)
                return;   // nothing created yet — leave any existing heatmap in place

            if (heightBuckets.Count == 0)
            {
                Log(AppStrings.T("ceilings.heatmap.log.noOffsets"), "fail");
                DiagnosticsLog.Warn("CeilingHeatmap",
                    "no ceilings found in host or links for the selected views — nothing to bucket.");
                fail++; return;
            }

            heightBuckets.Sort();
            Log(AppStrings.T("ceilings.heatmap.log.foundBuckets", heightBuckets.Count), "info");

            // ── Phase 2: Resolve Revit parameters (20–30%) ───────────────────────
            // "Height Offset From Level" is a built-in parameter, so its ElementId is
            // always valid — it does NOT depend on a host ceiling existing. (The old
            // host-only sample check failed whenever every ceiling lived in a link.)
            ElementId ceilingCatId  = new ElementId(BuiltInCategory.OST_Ceilings);
            ElementId heightParamId = new ElementId(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);

            // Diagnostic: host view filters only cascade onto a link when that link is
            // displayed "By Host View". Report any link that isn't, so the user knows
            // why its ceilings won't be colored (we don't change the link's display).
            ReportLinkDisplayModes(doc);

            ElementId solidFillId = GetSolidFillPatternId(doc);
            if (solidFillId == ElementId.InvalidElementId)
                Log(AppStrings.T("ceilings.heatmap.log.noSolidFill"), "info");

            var rampColors = BuildHeatmapRamp(heightBuckets.Count);
            Progress(30, pass, fail, skip);

            // ── Phase 3: Delete existing heatmap filters (30–40%) ────────────────
            if (DeleteExisting)
                DeleteHeatmapFilters(doc, ref fail);

            Progress(40, pass, fail, skip);

            // ── Phase 4: Create/reuse filters, apply overrides (40–95%) ──────────
            Log(AppStrings.T("ceilings.heatmap.log.creatingFilters"), "info");

            var existingFilters = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .ToDictionary(f => f.Name);

            // Pre-compute a unique rule/filter name per bucket. FormatFtIn rounds to whole
            // inches, so two sub-inch-apart buckets could otherwise collide on one name — the
            // second would silently reuse the first's filter (whose rule matches a different
            // offset) and its ceilings would stay uncolored. Disambiguate any collision with
            // the raw offset so every bucket gets its own filter.
            var ruleNames = new string[heightBuckets.Count];
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < heightBuckets.Count; i++)
            {
                double ft = UnitUtils.ConvertFromInternalUnits(heightBuckets[i], UnitTypeId.Feet);
                string baseName = $"{FormatFtIn(ft)} AFF";
                string name = baseName;
                if (usedNames.Contains(name))
                {
                    // Same whole-inch label as an earlier bucket — qualify with the raw offset.
                    name = $"{baseName} ({heightBuckets[i]:0.####}')";
                    int n = 2;
                    while (usedNames.Contains(name))
                        name = $"{baseName} ({heightBuckets[i]:0.####}' #{n++})";
                }
                usedNames.Add(name);
                ruleNames[i] = name;
            }

            // Cache each selected view's current filter-id set once — the bucket loop below
            // checks and updates the cache instead of calling GetFilters() per bucket × view.
            var viewPlans     = new List<ViewPlan>();
            var viewFilterIds = new List<HashSet<long>>();
            foreach (ElementId viewId in SelectedViewIds)
            {
                if (doc.GetElement(viewId) is ViewPlan vp0)
                {
                    viewPlans.Add(vp0);
                    viewFilterIds.Add(new HashSet<long>(vp0.GetFilters().Select(id => id.Value)));
                }
            }

            int created = 0, reused = 0;
            bool cancelledInApply = false;

            // Buckets that yielded a valid filter — used to rebuild the Ceiling Heatmap
            // trade's rules after the transaction commits (rule ⇄ filter linked by name).
            var chRules = new List<(double offset, string ruleName, RevitColor color)>();

            using (var tx = new Transaction(doc, "Ceiling Height Offset Heatmap"))
            {
                ConfigureFailures(tx);
                tx.Start();

                int total = heightBuckets.Count;
                for (int i = 0; i < total; i++)
                {
                    if (RunState.CancelRequested)
                    {
                        Log(AppStrings.T("common.log.stoppedByUser", i, total), "warn");
                        cancelledInApply = true;
                        break;
                    }

                    double heightOffset = heightBuckets[i];
                    Autodesk.Revit.DB.Color color = rampColors[i];

                    string ruleName    = ruleNames[i];
                    string filterName  = AutoFiltersSettings.MakeFilterName(CHTradeId, ruleName);

                    ParameterFilterElement? pfe;
                    if (existingFilters.TryGetValue(filterName, out pfe))
                    {
                        // Rebuild the rule in place so a tolerance changed in the UI takes effect
                        // on a reused filter. SetElementFilter keeps the ElementId, so existing
                        // view assignments and legend links survive.
                        try { pfe.SetElementFilter(BuildBucketFilter(heightParamId, heightOffset)); }
                        catch (Exception ex) { DiagnosticsLog.Swallowed($"CeilingHeatmap: rebuild rule for reused filter '{filterName}'", ex); }
                        reused++;
                    }
                    else
                    {
                        try
                        {
                            pfe = ParameterFilterElement.Create(
                                doc, filterName,
                                new List<ElementId> { ceilingCatId },
                                BuildBucketFilter(heightParamId, heightOffset));
                            existingFilters[filterName] = pfe;
                            created++;
                        }
                        catch (Exception ex)
                        {
                            Log(AppStrings.T("ceilings.heatmap.log.filterCreateError", filterName, ex.Message), "fail");
                            fail++; continue;
                        }
                    }

                    var ogs = new OverrideGraphicSettings();

                    // Foreground: explicit black color, no fill-pattern override.
                    var black = new RevitColor(0, 0, 0);
                    ogs.SetSurfaceForegroundPatternColor(black);
                    ogs.SetCutForegroundPatternColor(black);

                    if (solidFillId != ElementId.InvalidElementId)
                    {
                        // Color the BACKGROUND pattern with the filter's solid fill so the
                        // foreground keeps its (black) color with no pattern override — matching
                        // the Fill Pattern Graphics dialog: Background = solid fill in the ramp
                        // color, Foreground = black, no pattern.
                        ogs.SetSurfaceBackgroundPatternId(solidFillId);
                        ogs.SetSurfaceBackgroundPatternColor(color);
                        ogs.SetSurfaceBackgroundPatternVisible(true);
                        ogs.SetCutBackgroundPatternId(solidFillId);
                        ogs.SetCutBackgroundPatternColor(color);
                        ogs.SetCutBackgroundPatternVisible(true);
                    }

                    for (int v = 0; v < viewPlans.Count; v++)
                    {
                        var vp   = viewPlans[v];
                        var have = viewFilterIds[v];
                        try
                        {
                            if (have.Add(pfe.Id.Value))
                                vp.AddFilter(pfe.Id);
                            vp.SetFilterVisibility(pfe.Id, true);
                            vp.SetFilterOverrides(pfe.Id, ogs);
                        }
                        catch (Exception ex)
                        {
                            Log(AppStrings.T("ceilings.heatmap.log.filterApplyError", vp.Name, ex.Message), "fail");
                            fail++;
                        }
                    }

                    chRules.Add((heightOffset, ruleName, color));
                    pass++;
                    Progress(40 + (int)((i + 1) * 50.0 / total), pass, fail, skip);
                }

                tx.Commit();
            }

            // Mirror the created filters into a "Ceiling Heatmap" trade so they appear in the
            // rules list. Skip this when the apply loop was cancelled — chRules is then partial
            // and rebuilding would drop the un-applied buckets' rules from the trade.
            if (!cancelledInApply)
                RegisterCeilingHeatmapTrade(chRules);

            // ── Phase 5: Place ceiling tags (90–98%) ──────────────────────────────
            if (PlaceTags && !cancelledInApply)
                PlaceCeilingTags(doc, ref pass, ref fail, ref skip);

            // ── Summary ───────────────────────────────────────────────────────────
            double lowFt  = UnitUtils.ConvertFromInternalUnits(heightBuckets[0],       UnitTypeId.Feet);
            double highFt = UnitUtils.ConvertFromInternalUnits(heightBuckets.Last(),    UnitTypeId.Feet);

            _filtersCreated = created; _filtersReused = reused;
            Log(AppStrings.T("ceilings.heatmap.log.complete", created, reused), "pass");
            Log(AppStrings.T("ceilings.heatmap.log.range", FormatFtIn(lowFt), FormatFtIn(highFt)), "info");
            Log(AppStrings.T("ceilings.heatmap.log.appliedTo", SelectedViewIds.Count), "info");
        }

        // ── Phase 5: Place ceiling tags ───────────────────────────────────────────
        private void PlaceCeilingTags(Document doc, ref int pass, ref int fail, ref int skip)
        {
            Log(AppStrings.T("ceilings.heatmap.log.placingTags"), "info");

            FamilySymbol? tagSymbol = GetOrLoadTagSymbol(doc);
            if (tagSymbol == null)
            {
                Log(AppStrings.T("ceilings.heatmap.log.tagFamilyMissing"), "fail");
                fail++; return;
            }

            if (!tagSymbol.IsActive)
            {
                using (var txActivate = new Transaction(doc, "Activate Ceiling Tag Symbol"))
                {
                    ConfigureFailures(txActivate);
                    txActivate.Start();
                    tagSymbol.Activate();
                    txActivate.Commit();
                }
            }

            int viewCount  = SelectedViewIds.Count;
            int tagPlaced  = 0;
            int tagDeleted = 0;

            var ceilingTagCatId = new ElementId(BuiltInCategory.OST_CeilingTags);

            using (var tx = new Transaction(doc, "Place Ceiling Tags"))
            {
                ConfigureFailures(tx);
                tx.Start();

                for (int vi = 0; vi < viewCount; vi++)
                {
                    if (RunState.CancelRequested)
                    {
                        Log(AppStrings.T("common.log.stoppedByUser", vi, viewCount), "warn");
                        break;
                    }

                    var viewId = SelectedViewIds[vi];
                    var vp     = doc.GetElement(viewId) as ViewPlan;
                    if (vp == null) continue;   // already counted as skipped in the scan pass

                    foreach (var staleId in new FilteredElementCollector(doc, viewId)
                        .OfClass(typeof(IndependentTag))
                        .Cast<IndependentTag>()
                        .Where(t => t.Category?.Id == ceilingTagCatId)
                        .Select(t => t.Id)
                        .ToList())
                    {
                        try { doc.Delete(staleId); tagDeleted++; }
                        catch (Exception __lex) { DiagnosticsLog.Swallowed("CeilingHeatmap: delete element (protected or already gone)", __lex); }
                    }

                    var hostCeilings = new FilteredElementCollector(doc, viewId)
                        .OfClass(typeof(Ceiling))
                        .WhereElementIsNotElementType()
                        .ToList();

                    var linkInstances = new FilteredElementCollector(doc, viewId)
                        .OfClass(typeof(RevitLinkInstance))
                        .Cast<RevitLinkInstance>()
                        .Where(li => li.GetLinkDocument() != null)
                        .ToList();

                    var linkedCeilings = new List<(RevitLinkInstance Link, Document LinkDoc, Transform Xform, Element El)>();
                    foreach (RevitLinkInstance link in linkInstances)
                    {
                        Document  linkDoc = link.GetLinkDocument();
                        Transform xform   = link.GetTotalTransform();
                        var bbFilter = GetViewBoundsFilter(vp, xform.Inverse);

                        foreach (Element el in new FilteredElementCollector(linkDoc)
                            .OfClass(typeof(Ceiling))
                            .WherePasses(bbFilter)
                            .WhereElementIsNotElementType())
                            linkedCeilings.Add((link, linkDoc, xform, el));
                    }

                    // A single view can hold thousands of ceilings, so the per-view progress
                    // band alone goes silent here — report tag placement at 5% intervals.
                    var tagProgress = new RunProgressReporter(
                        Log, hostCeilings.Count + linkedCeilings.Count,
                        $"ceiling tags (view {vi + 1} of {viewCount})");

                    foreach (Element el in hostCeilings)
                    {
                        XYZ? tagPt = GetTagPoint(el as Ceiling, doc);
                        if (tagPt == null)
                        {
                            Log(AppStrings.T("ceilings.heatmap.log.tagNoPoint", el.Id), "warn");
                            skip++; tagProgress.Tick(); continue;
                        }
                        try
                        {
                            IndependentTag.Create(
                                doc, viewId, new Reference(el),
                                false, TagMode.TM_ADDBY_CATEGORY,
                                TagOrientation.Horizontal, tagPt);
                            tagPlaced++;   // folded into the pass total at the end of this method
                        }
                        catch (Exception ex)
                        {
                            Log(AppStrings.T("ceilings.heatmap.log.tagHostFailed", el.Id, ex.Message), "fail");
                            fail++;
                        }
                        tagProgress.Tick();
                    }

                    foreach (var lc in linkedCeilings)
                    {
                        XYZ? localPt = GetTagPoint(lc.El as Ceiling, lc.LinkDoc);
                        if (localPt == null)
                        {
                            Log(AppStrings.T("ceilings.heatmap.log.tagNoPoint", lc.El.Id), "warn");
                            skip++; tagProgress.Tick(); continue;
                        }
                        try
                        {
                            Reference linkedRef = new Reference(lc.El).CreateLinkReference(lc.Link);
                            XYZ tagPt = lc.Xform.OfPoint(localPt);

                            IndependentTag.Create(
                                doc, viewId, linkedRef,
                                false, TagMode.TM_ADDBY_CATEGORY,
                                TagOrientation.Horizontal, tagPt);
                            tagPlaced++;   // folded into the pass total at the end of this method
                        }
                        catch (Exception ex)
                        {
                            Log(AppStrings.T("ceilings.heatmap.log.tagLinkedFailed", lc.El.Id, ex.Message), "fail");
                            fail++;
                        }
                        tagProgress.Tick();
                    }

                    Progress(90 + (int)((vi + 1) * 8.0 / viewCount), pass, fail, skip);
                }

                tx.Commit();
            }

            if (tagDeleted > 0)
                Log(AppStrings.T("ceilings.heatmap.log.tagsPlacedReplaced", tagPlaced, tagDeleted, Math.Max(0, tagPlaced - tagDeleted)), "pass");
            else
                Log(AppStrings.T("ceilings.heatmap.log.tagsPlacedNone", tagPlaced), "pass");

            // Tags are a primary deliverable of the heatmap — count them toward pass so the
            // headline total reflects the ceilings tagged, not just the bucket filters created.
            pass += tagPlaced;
            _tagsPlaced = tagPlaced;
        }

        private FamilySymbol? GetOrLoadTagSymbol(Document doc)
        {
            const string FamilyName   = "Ceiling Tag";
            const string ResourceName = "LemoineTools.Source.Resources.RevitFamilys.Ceiling Tag.rfa";

            Family? existing = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => f.Name.Equals(FamilyName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
                return existing.GetFamilySymbolIds()
                    .Select(id => doc.GetElement(id) as FamilySymbol)
                    .FirstOrDefault(s => s != null);

            string tempPath = Path.Combine(Path.GetTempPath(), "Ceiling Tag.rfa");
            try
            {
                using (Stream? stream = System.Reflection.Assembly
                           .GetExecutingAssembly()
                           .GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        Log(AppStrings.T("ceilings.heatmap.log.resourceMissing", ResourceName), "fail");
                        return null;
                    }
                    using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                        stream.CopyTo(fs);
                }

                Family? loaded;
                using (var tx = new Transaction(doc, "Load Ceiling Tag Family"))
                {
                    ConfigureFailures(tx);
                    tx.Start();
                    doc.LoadFamily(tempPath, out loaded);
                    tx.Commit();
                }

                return loaded?.GetFamilySymbolIds()
                    .Select(id => doc.GetElement(id) as FamilySymbol)
                    .FirstOrDefault(s => s != null);
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("ceilings.heatmap.log.tagFamilyLoadFailed", ex.Message), "fail");
                return null;
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception __lex) { DiagnosticsLog.Swallowed("CeilingHeatmap: delete temp image file", __lex); }
            }
        }

        private static XYZ? GetTagPoint(Ceiling? ceiling, Document doc)
        {
            if (ceiling == null) return null;

            var opts = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };
            GeometryElement? geom = ceiling.get_Geometry(opts);

            Face? bottomFace = null;
            if (geom != null)
            {
                foreach (GeometryObject obj in geom)
                {
                    var solid = obj as Solid;
                    if (solid == null || solid.Volume <= 1e-9) continue;
                    foreach (Face face in solid.Faces)
                    {
                        try
                        {
                            BoundingBoxUV fbb = face.GetBoundingBox();
                            UV mid = new UV(
                                (fbb.Min.U + fbb.Max.U) * 0.5,
                                (fbb.Min.V + fbb.Max.V) * 0.5);
                            XYZ n = face.ComputeNormal(mid);
                            if (n.Z < -0.9) { bottomFace = face; break; }
                        }
                        catch (Exception __lex) { DiagnosticsLog.Swallowed("CeilingHeatmap: skip malformed face", __lex); }
                    }
                    if (bottomFace != null) break;
                }
            }

            if (bottomFace != null)
            {
                BoundingBoxUV uvBox = bottomFace.GetBoundingBox();

                UV uvMid = new UV(
                    (uvBox.Min.U + uvBox.Max.U) * 0.5,
                    (uvBox.Min.V + uvBox.Max.V) * 0.5);
                if (bottomFace.IsInside(uvMid))
                    return bottomFace.Evaluate(uvMid);

                UV? uvCentroid = ComputeOuterLoopCentroidUV(bottomFace);
                if (uvCentroid != null && bottomFace.IsInside(uvCentroid))
                    return bottomFace.Evaluate(uvCentroid);

                const int N = 7;
                for (int ui = 1; ui < N; ui++)
                for (int vi = 1; vi < N; vi++)
                {
                    var uv = new UV(
                        uvBox.Min.U + (uvBox.Max.U - uvBox.Min.U) * ui / N,
                        uvBox.Min.V + (uvBox.Max.V - uvBox.Min.V) * vi / N);
                    if (bottomFace.IsInside(uv))
                        return bottomFace.Evaluate(uv);
                }
            }

            BoundingBoxXYZ? bb = ceiling.get_BoundingBox(null);
            if (bb != null)
                return new XYZ(
                    (bb.Min.X + bb.Max.X) * 0.5,
                    (bb.Min.Y + bb.Max.Y) * 0.5,
                     bb.Min.Z);
            return null;   // no point found — caller skips-and-logs rather than tagging the origin
        }

        private static UV? ComputeOuterLoopCentroidUV(Face face)
        {
            try
            {
                EdgeArrayArray loops = face.EdgeLoops;
                if (loops.Size == 0) return null;
                EdgeArray outerLoop = loops.get_Item(0);

                double uSum = 0, vSum = 0;
                int    count = 0;

                foreach (Edge edge in outerLoop)
                {
                    foreach (XYZ pt in edge.Tessellate())
                    {
                        IntersectionResult? ir = face.Project(pt);
                        if (ir == null) continue;
                        uSum  += ir.UVPoint.U;
                        vSum  += ir.UVPoint.V;
                        count++;
                    }
                }
                return count > 0 ? new UV(uSum / count, vSum / count) : null;
            }
            catch { return null; }
        }

        private static BoundingBoxIntersectsFilter GetViewBoundsFilter(
            ViewPlan view, Transform invLinkXform)
        {
            double levelElev = view.GenLevel?.Elevation ?? 0.0;
            double zMaxWorld = levelElev + 30.0;
            try
            {
                Level? nextLevel = new FilteredElementCollector(view.Document)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .Where(l => l.Elevation > levelElev + 1.0)
                    .OrderBy(l => l.Elevation)
                    .FirstOrDefault();
                if (nextLevel != null)
                    zMaxWorld = nextLevel.Elevation;
            }
            catch (Exception __lex) { DiagnosticsLog.Swallowed("CeilingHeatmap: find next level elevation", __lex); }

            double zMin = invLinkXform.OfPoint(new XYZ(0, 0, levelElev - 1.0)).Z;
            double zMax = invLinkXform.OfPoint(new XYZ(0, 0, zMaxWorld)).Z;
            if (zMin > zMax) { double tmp = zMin; zMin = zMax; zMax = tmp; }

            if (!view.CropBoxActive)
            {
                return new BoundingBoxIntersectsFilter(new Outline(
                    new XYZ(-1e6, -1e6, zMin),
                    new XYZ( 1e6,  1e6, zMax)));
            }

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            bool   got  = false;
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
            catch (Exception __lex) { DiagnosticsLog.Swallowed("CeilingHeatmap: compute ceiling bounds", __lex); }

            if (!got)
            {
                BoundingBoxXYZ cb = view.CropBox;
                Transform      t  = cb.Transform;
                foreach (XYZ local in new[]
                {
                    new XYZ(cb.Min.X, cb.Min.Y, 0),
                    new XYZ(cb.Max.X, cb.Min.Y, 0),
                    new XYZ(cb.Max.X, cb.Max.Y, 0),
                    new XYZ(cb.Min.X, cb.Max.Y, 0),
                })
                {
                    XYZ w = t.OfPoint(local);
                    if (w.X < minX) minX = w.X;
                    if (w.Y < minY) minY = w.Y;
                    if (w.X > maxX) maxX = w.X;
                    if (w.Y > maxY) maxY = w.Y;
                }
            }

            XYZ p1 = invLinkXform.OfPoint(new XYZ(minX, minY, 0));
            XYZ p2 = invLinkXform.OfPoint(new XYZ(maxX, maxY, 0));

            return new BoundingBoxIntersectsFilter(new Outline(
                new XYZ(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), zMin),
                new XYZ(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y), zMax)));
        }

        private void DeleteHeatmapFilters(Document doc, ref int fail)
        {
            // Match the current "CH_" naming convention plus the legacy "Ceiling Heatmap — "
            // names from earlier versions so re-runs clean up both.
            var heatmapFilters = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .Where(f => f.Name.StartsWith(CHTradeId + "_")
                         || f.Name.StartsWith("Ceiling Heatmap — "))
                .ToList();

            if (heatmapFilters.Count == 0)
            {
                Log(AppStrings.T("ceilings.heatmap.log.noHeatmapFilters"), "info");
                return;
            }

            Log(AppStrings.T("ceilings.heatmap.log.removingFilters", heatmapFilters.Count), "info");

            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .ToList();

            var heatmapIds = new HashSet<long>(heatmapFilters.Select(f => f.Id.Value));

            using (var tx = new Transaction(doc, "Delete Ceiling Heatmap Filters"))
            {
                ConfigureFailures(tx);
                tx.Start();

                // Iterate views once, reading each view's filter list a single time, instead of
                // scanning every view for every heatmap filter (filters × views GetFilters calls).
                foreach (View v in allViews)
                {
                    try
                    {
                        foreach (ElementId fid in v.GetFilters().Where(id => heatmapIds.Contains(id.Value)).ToList())
                            v.RemoveFilter(fid);
                    }
                    catch (Exception __lex) { DiagnosticsLog.Swallowed("CeilingHeatmap: remove filter from view (view type may not support filters)", __lex); }
                }

                foreach (var pfe in heatmapFilters)
                {
                    try   { doc.Delete(pfe.Id); }
                    catch (Exception ex)
                    {
                        Log(AppStrings.T("ceilings.heatmap.log.filterDeleteError", pfe.Name, ex.Message), "fail");
                        fail++;
                    }
                }

                tx.Commit();
            }
        }

        // ── Mirror created filters into the "Ceiling Heatmap" trade ───────────────
        // Rebuilds the trade's rules every run so they always match the current buckets
        // (per the rebuild-each-run decision). Each rule is linked to its Revit filter by
        // the shared name convention. The trade is flagged ExternallyManaged so the generic
        // AutoFilters "Create Filters" engine never tries to regenerate these numeric filters.
        private void RegisterCeilingHeatmapTrade(
            List<(double offset, string ruleName, RevitColor color)> chRules)
        {
            try
            {
                var settings = AutoFiltersSettings.Instance;

                var trade = settings.Trades.FirstOrDefault(
                    t => string.Equals(t.Id, CHTradeId, StringComparison.OrdinalIgnoreCase));
                if (trade == null)
                {
                    trade = new FilterTradeConfig { Id = CHTradeId };
                    settings.Trades.Add(trade);
                }

                trade.Label             = CHTradeLabel;
                trade.Color             = CHTradeColor;
                trade.ExternallyManaged = true;

                // Rebuild rules from this run's buckets.
                trade.Rules.Clear();
                foreach (var (offset, ruleName, color) in chRules)
                {
                    string hex = ToHex(color);
                    var rule = FilterRuleConfig.NewBlank();
                    rule.Name              = ruleName;
                    rule.Enabled           = true;
                    rule.Parameter         = "Height Offset From Level";
                    rule.BuiltInCategories = new List<string> { "OST_Ceilings" };
                    rule.MatchType         = "equals";
                    rule.Match             = new List<string> { offset.ToString("0.######") };
                    rule.CutColor          = hex;
                    rule.SurfColor         = hex;
                    rule.LineColor         = hex;
                    rule.Notes             = "Auto-generated by Ceiling Heatmap (numeric height match, " +
                                             "± tolerance). Managed by the Ceiling Heatmap tool.";
                    trade.Rules.Add(rule);
                }

                settings.Save();
                Log(AppStrings.T("ceilings.heatmap.log.tradeRegistered", CHTradeLabel, trade.Rules.Count), "info");
            }
            catch (Exception ex)
            {
                // Non-fatal: the Revit filters were already created/applied above. Surface the
                // failure so the rules-list sync issue isn't hidden.
                DiagnosticsLog.Error("CeilingHeatmap: register Ceiling Heatmap trade", ex);
                Log(AppStrings.T("ceilings.heatmap.log.rulesUpdateFailed", ex.Message), "fail");
            }
        }

        private static string ToHex(RevitColor c)
            => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

        private void AddBucket(List<double> buckets, double heightOffset)
        {
            // Snap the offset to a fixed tolerance grid so the bucket anchor is independent
            // of scan order and adjacent buckets never overlap. The old first-seen anchor was
            // order-dependent (different filter names on re-run) and could place two buckets
            // within one tolerance of each other, letting a view's filter order decide the
            // color. ElevTolerance is clamped > 0 by the UI, but guard anyway.
            double tol     = ElevTolerance > 1e-9 ? ElevTolerance : (1.0 / 96.0);
            double snapped = Math.Round(heightOffset / tol) * tol;
            foreach (double b in buckets)
                if (Math.Abs(b - snapped) < tol * 0.5) return;
            buckets.Add(snapped);
        }

        // Builds the equals-rule filter for one height-offset bucket. The match tolerance is
        // half the bucket grid spacing (AddBucket snaps to a spacing of ElevTolerance), so
        // adjacent buckets' match ranges tile the elevation axis without overlapping — each
        // ceiling maps to exactly one bucket.
        private ElementParameterFilter BuildBucketFilter(ElementId heightParamId, double heightOffset)
        {
            double tol = ElevTolerance > 1e-9 ? ElevTolerance * 0.5 : (1.0 / 192.0);
            return new ElementParameterFilter(
                ParameterFilterRuleFactory.CreateEqualsRule(heightParamId, heightOffset, tol));
        }

        /// <summary>Scans every visible link in <paramref name="view"/> for ceilings,
        /// adding their height offsets to <paramref name="buckets"/>. Each linked ceiling is
        /// recorded in <paramref name="seen"/> as (link instance id, element id) so a ceiling
        /// visible in more than one selected view is only counted once.</summary>
        private void ScanLinkedCeilings(
            Document hostDoc, ViewPlan view,
            List<double> buckets, HashSet<(long link, long el)> seen)
        {
            var links = new FilteredElementCollector(hostDoc, view.Id)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .Where(li => li.GetLinkDocument() != null)
                .ToList();

            foreach (RevitLinkInstance link in links)
            {
                Document?  linkDoc   = link.GetLinkDocument();
                if (linkDoc == null) continue;

                Transform linkXform = link.GetTotalTransform();
                var bbFilter = GetViewBoundsFilter(view, linkXform.Inverse);

                foreach (Element el in new FilteredElementCollector(linkDoc)
                    .OfClass(typeof(Ceiling))
                    .WherePasses(bbFilter)
                    .WhereElementIsNotElementType())
                {
                    var hParam = el.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                    if (hParam == null) continue;   // no height-from-level value → skip, don't bucket as 0
                    if (!seen.Add((link.Id.Value, el.Id.Value))) continue;  // already scanned
                    AddBucket(buckets, hParam.AsDouble());
                }
            }
        }

        /// <summary>
        /// Diagnostic only — host view filters cascade onto a link's elements only when
        /// the link is displayed "By Host View" in that view. For every selected view ×
        /// visible link, log the link's display mode and warn (in the step log and
        /// diagnostics.log) about any link that won't be colored. Does NOT change the
        /// link's display settings.
        /// </summary>
        private void ReportLinkDisplayModes(Document doc)
        {
            int notCascading = 0;

            foreach (ElementId viewId in SelectedViewIds)
            {
                if (!(doc.GetElement(viewId) is View view)) continue;

                foreach (RevitLinkInstance link in new FilteredElementCollector(doc, viewId)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .Where(li => li.GetLinkDocument() != null))
                {
                    LinkVisibility mode;
                    try
                    {
                        // GetLinkOverrides returns null when the link uses the default
                        // display (By Host View) and was never customized in this view.
                        RevitLinkGraphicsSettings? gs = view.GetLinkOverrides(link.Id);
                        mode = gs?.LinkVisibilityType ?? LinkVisibility.ByHostView;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Swallowed("CeilingHeatmap: read link display mode", ex);
                        continue;
                    }

                    if (mode != LinkVisibility.ByHostView)
                    {
                        notCascading++;
                        string linkName = link.Name;
                        Log(AppStrings.T("ceilings.heatmap.log.linkNotByHost", linkName, view.Name, mode),
                            "fail");
                        DiagnosticsLog.Warn("CeilingHeatmap",
                            $"link '{linkName}' in view '{view.Name}' display={mode}; "
                            + "host filters will not cascade onto its ceilings.");
                    }
                }
            }

            if (notCascading == 0)
                DiagnosticsLog.Info("CeilingHeatmap",
                    "all visible links display By Host View — heatmap filters will cascade onto linked ceilings.");
        }

        private static ElementId GetSolidFillPatternId(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill)
                ?.Id ?? ElementId.InvalidElementId;
        }

        private static string FormatFtIn(double valueFt)
        {
            int totalInches = (int)Math.Round(valueFt * 12.0);
            // Sign must survive on the whole value: when ft rounds to 0, a bare
            // Abs(inches) made +6" and -6" both read "0'-6"", collapsing two buckets
            // into one filter name. Carry the sign as a prefix on the magnitude.
            string sign   = totalInches < 0 ? "-" : "";
            int absInches = Math.Abs(totalInches);
            int ft        = absInches / 12;
            int inches    = absInches % 12;
            return $"{sign}{ft}'-{inches}\"";
        }

        private List<Autodesk.Revit.DB.Color> BuildHeatmapRamp(int count)
        {
            if (count == 1)
                return new List<Autodesk.Revit.DB.Color> { ColorLow };

            var colors = new List<Autodesk.Revit.DB.Color>(count);
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / (count - 1);
                Autodesk.Revit.DB.Color from, to;
                double seg;
                if (t <= 0.5) { from = ColorLow; to = ColorMid;  seg = t * 2.0; }
                else          { from = ColorMid;  to = ColorHigh; seg = (t - 0.5) * 2.0; }

                byte r = (byte)Math.Round(from.Red   + seg * (to.Red   - from.Red));
                byte g = (byte)Math.Round(from.Green + seg * (to.Green - from.Green));
                byte b = (byte)Math.Round(from.Blue  + seg * (to.Blue  - from.Blue));
                colors.Add(new Autodesk.Revit.DB.Color(r, g, b));
            }
            return colors;
        }

        // Finds (by name) or creates one CeilingPlan view per level, applies the chosen
        // template (if any), and returns the resulting view ids. Mirrors
        // MakeCeilingGridsRunHandler's find-or-create pattern, but does NOT restrict
        // category visibility — the heatmap needs ceilings visible alongside everything else.
        private List<ElementId> GenerateRcpViews(
            Document doc, List<ElementId> levelIds, string suffix, ElementId templateId, ref int fail)
        {
            var result = new List<ElementId>();

            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.CeilingPlan);
            if (vft == null)
            {
                Log(AppStrings.T("ceilings.heatmap.log.noRcpType"), "fail");
                fail++;
                return result;
            }

            int created = 0, reused = 0;
            using (var tx = new Transaction(doc, "Generate Heatmap RCPs"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (var levelId in levelIds)
                {
                    var level = doc.GetElement(levelId) as Level;
                    if (level == null) continue;

                    string viewName = SanitizeViewName(level.Name) + suffix;
                    try
                    {
                        var view = FindRcpByName(doc, viewName);
                        if (view == null)
                        {
                            view = ViewPlan.Create(doc, vft.Id, level.Id);
                            try { view.Name = viewName; }
                            catch (Exception ex)
                            {
                                DiagnosticsLog.Swallowed($"CeilingHeatmap: name conflict for generated RCP '{viewName}'", ex);
                                Log(AppStrings.T("ceilings.heatmap.log.generateRenameConflict", viewName), "warn");
                            }
                            created++;
                        }
                        else reused++;

                        if (templateId != ElementId.InvalidElementId)
                        {
                            try { view.ViewTemplateId = templateId; }
                            catch (Exception ex)
                            {
                                DiagnosticsLog.Swallowed($"CeilingHeatmap: apply template to generated RCP '{viewName}'", ex);
                                Log(AppStrings.T("ceilings.heatmap.log.generateTemplateFailed", view.Name, ex.Message), "warn");
                            }
                        }

                        result.Add(view.Id);
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        DiagnosticsLog.Error($"CeilingHeatmap: generate RCP for level '{level.Name}'", ex);
                        Log(AppStrings.T("ceilings.heatmap.log.generateFailed", level.Name, ex.Message), "fail");
                    }
                }

                tx.Commit();
            }

            Log(AppStrings.T("ceilings.heatmap.log.generated", created, reused), "info");
            return result;
        }

        private static ViewPlan? FindRcpByName(Document doc, string name)
            => new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v =>
                    v.Name == name && !v.IsTemplate &&
                    (doc.GetElement(v.GetTypeId()) as ViewFamilyType)?.ViewFamily == ViewFamily.CeilingPlan);

        private static string SanitizeViewName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Join("_", name.Split(invalid)).Trim();
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private void Log(string t, string s)      => PushLog?.Invoke(t, s);
        private void Progress(int p, int pa, int f, int s) => OnProgress?.Invoke(p, pa, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
