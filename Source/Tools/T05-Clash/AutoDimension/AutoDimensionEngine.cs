using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Clash.AutoDimension.Resolvers;

namespace LemoineTools.Tools.Clash.AutoDimension
{
    /// <summary>The Revit references for one planned dimension, kept parallel to the
    /// (serializable) plan so the plan itself stays Revit-free.</summary>
    public sealed class PlannedRefBundle
    {
        /// <summary>All references for one dimension, ordered along its line (sources then target).
        /// A two-entry list is a plain dimension; more entries make a native chained string.</summary>
        public List<Reference> Ordered { get; set; } = new List<Reference>();
    }

    /// <summary>Output of the read-side engine: the abstract plan plus the Revit data the commit
    /// needs (references, dimension type, and the projection to rebuild world geometry).</summary>
    public sealed class EngineOutput
    {
        public Core.DimensionPlan Plan { get; set; } = new Core.DimensionPlan();
        public Dictionary<string, PlannedRefBundle> Refs { get; set; } = new Dictionary<string, PlannedRefBundle>();
        public ElementId DimTypeId { get; set; } = ElementId.InvalidElementId;
        public ViewProjection Projection { get; set; } = null!;
        public Core.LayoutConfig CoreConfig { get; set; } = new Core.LayoutConfig();
    }

    /// <summary>
    /// Builds a <see cref="Core.DimensionPlan"/> from a view's source cross-lines: ingest →
    /// resolve targets (Part A) → abstract layout (Part B). Read-only — touches no elements.
    /// The result is a dumb input to <see cref="AutoDimensionCommit"/>.
    /// </summary>
    public sealed class AutoDimensionEngine
    {
        private readonly Action<string, string> _log;
        public AutoDimensionEngine(Action<string, string> log) { _log = log ?? ((a, b) => { }); }

        public EngineOutput BuildPlan(Document doc, View view, AutoDimensionConfig cfg,
            List<Resolvers.ManualDatum>? datums = null, List<Resolvers.SlabScope>? slabScopes = null)
        {
            var output = new EngineOutput();
            var plan = output.Plan;
            plan.RunId = Guid.NewGuid().ToString("N");
            plan.SchemaVersion = cfg.SchemaVersion;

            var projection = new ViewProjection(view);
            output.Projection = projection;

            // ── Sources: host + loaded links ──────────────────────────────────
            var ctx = new ResolveContext
            {
                HostDoc    = doc,
                View       = view,
                Projection = projection,
                Config     = cfg,
                ReportMissingLink = m => { if (!plan.MissingLinkRefs.Contains(m)) plan.MissingLinkRefs.Add(m); },
                Datums = datums ?? new List<Resolvers.ManualDatum>(),
                SlabScopes = slabScopes ?? new List<Resolvers.SlabScope>(),
                Log = _log,
            };
            ctx.Sources.Add(new SourceDoc { Doc = doc, Link = null, Transform = Transform.Identity });
            if (cfg.IncludeLinks)
            {
                foreach (var li in new FilteredElementCollector(doc)
                             .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                             .OrderBy(l => l.Id.IntegerValue))
                {
                    var ld = li.GetLinkDocument();
                    if (ld == null) continue;
                    ctx.Sources.Add(new SourceDoc { Doc = ld, Link = li, Transform = li.GetTotalTransform() });
                }
            }

            // ── 1. Ingest source cross-lines ──────────────────────────────────
            var sources = SourceIngest.Collect(doc, view, projection, plan.Unresolved);
            _log($"Ingest: {sources.Count} source line(s), {plan.Unresolved.Count} without a usable reference.", "info");
            if (sources.Count == 0)
            {
                plan.Notes.Add("No tagged source cross-lines found in the view.");
                return output;
            }

            // ── 2. Resolve targets (Part A) — once per measurement axis ───────
            string tt   = cfg.TargetType ?? "Grid";
            bool slab    = string.Equals(tt, "SlabEdge", StringComparison.OrdinalIgnoreCase);
            bool manual  = string.Equals(tt, "ManualDatum", StringComparison.OrdinalIgnoreCase);
            ITargetResolver resolver = manual ? new ManualDatumResolver()
                                     : slab   ? new SlabEdgeTargetResolver()
                                     : (ITargetResolver)new GridTargetResolver();
            Core.TargetType ttEnum = manual ? Core.TargetType.ManualDatum
                                   : slab   ? Core.TargetType.SlabEdge
                                   : Core.TargetType.Grid;
            output.DimTypeId = ResolveDimType(doc, cfg.DimensionTypeName);

            double scale = view.Scale <= 0 ? 1 : view.Scale;
            double textPaperFt = ReadDimTextSizeFt(doc, output.DimTypeId) ?? cfg.Layout.TextHeightFt;
            var coreCfg = BuildCoreConfig(cfg.Layout, scale, textPaperFt);
            output.CoreConfig = coreCfg;
            _log($"Text size: {textPaperFt * 12.0:0.###}\" paper × 1:{scale:0} → {coreCfg.TextHeightFt:0.##} ft model (cramped + stagger basis).", "info");

            // Exact value formatter using the dimension type's own units format, so width estimation
            // counts the real glyphs (e.g. 0' - 11 5/8") in whatever units the type displays.
            var valueFmt = BuildValueFormatter(doc, output.DimTypeId);

            var axes = cfg.DimensionBothAxes
                ? new[] { new Core.Vec2(1, 0), new Core.Vec2(0, 1) }
                : new[] { new Core.Vec2(1, 0) };

            // ── 1b. Cluster clashes into physical runs (axis-agnostic, before resolve) ──
            // A run governs both its dimensions: chained along its length, single across it.
            double runCrossFt = cfg.RunCrossToleranceMm / 304.8;
            double runGapFt   = cfg.RunGapMm / 304.8;
            var runs = cfg.ChainAligned
                ? ClashRunGrouper.Build(sources, runCrossFt, runGapFt)
                : new Dictionary<string, ClashRunGrouper.RunInfo>();
            if (cfg.ChainAligned)
                _log($"Grouped {sources.Count} clash(es) into {runs.Values.Distinct().Count()} run(s) "
                   + $"(cross ≤{cfg.RunCrossToleranceMm:0} mm, gap ≤{cfg.RunGapMm:0} mm).", "info");

            var resolved        = new List<ResolvedItem>();
            var resolvedSources = new HashSet<string>();
            var firstFailReason = new Dictionary<string, string>();   // sourceKey → reason, used only if no axis resolved

            _log($"Resolving {sources.Count} clash(es) × {axes.Length} axis/axes to {tt} target(s)…", "info");
            int processed = 0;
            foreach (var src in sources)
            {
                foreach (var ax in axes)
                {
                    ctx.Axis = ax;
                    var res = resolver.Resolve(src, ctx);
                    if (res.Success)
                    {
                        // A clash with no clustered run is its own solo run (one dim per axis).
                        runs.TryGetValue(src.SourceKey, out var run);
                        resolved.Add(new ResolvedItem
                        {
                            SourceKey   = src.SourceKey,
                            SourceRef   = src.SourceRef,
                            Source2d    = src.Anchor2d,
                            Axis        = ax,
                            TargetRef   = res.TargetRef!,
                            Target2d    = res.TargetPoint2d,
                            TargetKey   = res.TargetKey,
                            TargetType  = ttEnum,
                            RunId       = run?.RunId ?? ("solo|" + src.SourceKey),
                            RunLongAxis = run?.LongAxis ?? new Core.Vec2(1, 0),
                        });
                        resolvedSources.Add(src.SourceKey);
                    }
                    else if (res.Ambiguity != null)
                    {
                        res.Ambiguity.TargetType = ttEnum;
                        plan.Ambiguities.Add(res.Ambiguity);
                    }
                    else if (res.Unresolved != null && !firstFailReason.ContainsKey(src.SourceKey))
                    {
                        firstFailReason[src.SourceKey] = res.Unresolved.Reason;
                    }
                }
                if (++processed % 200 == 0)
                    _log($"  …{processed}/{sources.Count} clash(es) resolved", "info");
            }

            foreach (var kv in firstFailReason)
                if (!resolvedSources.Contains(kv.Key))
                    plan.Unresolved.Add(new Core.UnresolvedTarget { SourceKey = kv.Key, TargetType = ttEnum, Reason = kv.Value });

            _log($"Resolved {resolved.Count} dimension(s) over {axes.Length} axis/axes from {sources.Count} source(s) — {plan.Unresolved.Count} unresolved, {plan.Ambiguities.Count} ambiguous.",
                resolved.Count > 0 ? "info" : "fail");

            // ── 2b. Build run-aware dimensions: chain along each run, single across it ──
            _log($"Building run-aware dimensions (grouping {(cfg.ChainAligned ? "on" : "off")})…", "info");
            var chained = DimensionChainer.Build(resolved, coreCfg, valueFmt);
            var dims = chained.Dims;
            output.Refs = chained.Refs;

            int chainedStrings = dims.Count(d => d.Segments.Count > 1);
            _log($"{dims.Count} dimension(s) to place ({chainedStrings} chained).", "info");
            if (chainedStrings > 0) plan.Notes.Add($"{chainedStrings} chained string(s) grouping aligned clashes.");

            // ── 3–6. Abstract layout (Part B) ─────────────────────────────────
            _log($"Collecting obstacles + laying out {dims.Count} dimension(s) (collision-aware, ≤{coreCfg.TimeCapMs} ms)…", "info");
            var obstacles = CollectObstacles(doc, view, projection);
            var scorer = new Core.LayoutScorer(coreCfg, null /* crop scoring optional in Tier 1 */);
            var layout = new Core.GreedyLayoutEngine(coreCfg, scorer);
            layout.Arrange(dims, obstacles);
            _log($"Layout done ({obstacles.Count} obstacle(s) considered).", "info");

            int leadered = Core.GreedyLayoutEngine.LeaderedCount(dims);
            if (leadered > 0) plan.Notes.Add($"{leadered} segment(s) leadered to fit dense text.");

            var finalScore = scorer.ScoreAll(dims, obstacles);
            if (finalScore.Hard > 1e-6)
                plan.Notes.Add($"Layout left {finalScore.Hard:0} hard-constraint penalty — some strings may still overlap (unsatisfiable in Tier 1).");

            plan.Dimensions = dims;
            return output;
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static Core.LayoutConfig BuildCoreConfig(Core.LayoutConfig paper, double scale, double textHeightPaperFt)
        {
            // Spacing/offset/text are paper-space; multiply by the view scale to mix with the
            // model-space source/target points the projection produced. Text height comes from the
            // actual DimensionType (textHeightPaperFt) so cramped-detection + stagger match reality.
            return new Core.LayoutConfig
            {
                SchemaVersion       = paper.SchemaVersion,
                StringSpacingFt     = paper.StringSpacingFt * scale,
                FirstOffsetFt       = paper.FirstOffsetFt * scale,
                PrecisionFt         = paper.PrecisionFt,            // display tolerance, model-space
                TextHeightFt        = textHeightPaperFt * scale,
                OverlapWeight       = paper.OverlapWeight,
                OffCropWeight       = paper.OffCropWeight,
                WitnessCrossWeight  = paper.WitnessCrossWeight,
                CrampedWeight       = paper.CrampedWeight,
                UnevenSpacingWeight = paper.UnevenSpacingWeight,
                LeaderWeight        = paper.LeaderWeight,
                MaxIterations       = paper.MaxIterations,
                TimeCapMs           = paper.TimeCapMs,
                PlateauEpsilon      = paper.PlateauEpsilon,
                MaxOffsetSteps      = paper.MaxOffsetSteps,
            };
        }

        /// <summary>
        /// Returns a formatter that renders a length (internal ft) exactly as the resolved dimension
        /// type will display it. It reads the type's own units-format override when present (so it
        /// matches the placed dimension's text in any unit system), else the project length units.
        /// Used to estimate text width by glyph count at plan time; never throws.
        /// </summary>
        private static Func<double, string?> BuildValueFormatter(Document doc, ElementId dimTypeId)
        {
            // GetUnits() returns a mutable copy of the project units, so we can override just the
            // Length format options with the dimension type's own when it sets one (no need to know
            // the unit system to build a fresh Units — Units has no UnitSystem getter in 2024).
            Units units = doc.GetUnits();
            try
            {
                var dt = doc.GetElement(dimTypeId) as DimensionType;
                FormatOptions fo = dt?.GetUnitsFormatOptions();
                if (fo != null && !fo.UseDefault)
                    units.SetFormatOptions(SpecTypeId.Length, fo);
            }
            catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionEngine: read dim units format", ex); }

            return ft =>
            {
                try { return UnitFormatUtils.Format(units, SpecTypeId.Length, ft, false); }
                catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionEngine: format dim value", ex); return null; }
            };
        }

        /// <summary>Paper-space text height (ft) of the resolved dimension type, or null to fall
        /// back to the config default. Drives cramped-detection and stagger spacing.</summary>
        private static double? ReadDimTextSizeFt(Document doc, ElementId dimTypeId)
        {
            if (dimTypeId == ElementId.InvalidElementId) return null;
            try
            {
                var dt = doc.GetElement(dimTypeId) as DimensionType;
                var p  = dt?.get_Parameter(BuiltInParameter.TEXT_SIZE);
                if (p != null && p.StorageType == StorageType.Double)
                {
                    double v = p.AsDouble();
                    if (v > 1e-6) return v;
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionEngine: read dim text size", ex); }
            return null;
        }

        private static ElementId ResolveDimType(Document doc, string name)
        {
            try
            {
                var types = new FilteredElementCollector(doc)
                    .OfClass(typeof(DimensionType)).Cast<DimensionType>()
                    .Where(t => t.StyleType == DimensionStyleType.Linear)
                    .ToList();
                if (types.Count == 0) return ElementId.InvalidElementId;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    var match = types.FirstOrDefault(t => t.Name == name);
                    if (match != null) return match.Id;
                }
                var defId = doc.GetDefaultElementTypeId(ElementTypeGroup.LinearDimensionType);
                if (defId != null && defId != ElementId.InvalidElementId) return defId;
                return types.OrderBy(t => t.Id.IntegerValue).First().Id;
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("AutoDimensionEngine: resolve dimension type", ex);
                return ElementId.InvalidElementId;
            }
        }

        /// <summary>
        /// Builds the explicit, small collision set: existing (non-owned) dimensions, text notes,
        /// tags, and the source cross-lines — projected to view-2D boxes. Architectural/structural
        /// background geometry is excluded by construction (never added here).
        /// </summary>
        private static IReadOnlyList<Core.Box2> CollectObstacles(Document doc, View view, ViewProjection projection)
        {
            var boxes = new List<Core.Box2>();

            void AddBoxesOf(FilteredElementCollector col, Func<Element, bool>? keep = null)
            {
                foreach (var e in col)
                {
                    if (keep != null && !keep(e)) continue;
                    BoundingBoxXYZ? bb = null;
                    try { bb = e.get_BoundingBox(view); }
                    catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionEngine: obstacle bbox", ex); }
                    if (bb == null) continue;
                    Core.Vec2 a = projection.To2D(bb.Min);
                    Core.Vec2 b = projection.To2D(bb.Max);
                    boxes.Add(Core.Box2.FromPoints(a, b));
                }
            }

            try
            {
                AddBoxesOf(new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(Dimension)).WhereElementIsNotElementType(),
                    e => !AutoDimOwnerSchema.IsOwned(e));          // never collide-test against our own prior dims
                AddBoxesOf(new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(TextNote)).WhereElementIsNotElementType());
                AddBoxesOf(new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(IndependentTag)).WhereElementIsNotElementType());
                AddBoxesOf(new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_Lines).WhereElementIsNotElementType(),
                    ClashTagSchema.IsOurs);                        // the source cross-lines
            }
            catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionEngine: collect obstacles", ex); }

            // Deterministic order.
            boxes.Sort((x, y) =>
            {
                int c = x.MinX.CompareTo(y.MinX);
                if (c != 0) return c;
                return x.MinY.CompareTo(y.MinY);
            });
            return boxes;
        }
    }
}
