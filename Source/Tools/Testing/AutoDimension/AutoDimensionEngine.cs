using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Testing.AutoDimension.Resolvers;

namespace LemoineTools.Tools.Testing.AutoDimension
{
    /// <summary>The Revit references for one planned dimension, kept parallel to the
    /// (serializable) plan so the plan itself stays Revit-free.</summary>
    public sealed class PlannedRefBundle
    {
        public Reference Source { get; set; } = null!;
        public Reference Target { get; set; } = null!;
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

        public EngineOutput BuildPlan(Document doc, View view, AutoDimensionConfig cfg)
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

            // ── 2. Resolve targets (Part A) ───────────────────────────────────
            bool slab = string.Equals(cfg.TargetType, "SlabEdge", StringComparison.OrdinalIgnoreCase);
            ITargetResolver resolver = slab ? new SlabEdgeTargetResolver() : (ITargetResolver)new GridTargetResolver();
            output.DimTypeId = ResolveDimType(doc, cfg.DimensionTypeName);

            Core.Vec2 axis = projection.HorizontalAxis;
            double scale = view.Scale <= 0 ? 1 : view.Scale;
            var coreCfg = BuildCoreConfig(cfg.Layout, scale);
            output.CoreConfig = coreCfg;

            var dims = new List<Core.PlannedDimension>();
            foreach (var src in sources)
            {
                var res = resolver.Resolve(src, ctx);
                if (!res.Success)
                {
                    if (res.Ambiguity != null) { res.Ambiguity.TargetType = slab ? Core.TargetType.SlabEdge : Core.TargetType.Grid; plan.Ambiguities.Add(res.Ambiguity); }
                    else if (res.Unresolved != null) plan.Unresolved.Add(res.Unresolved);
                    continue;
                }

                double axialLen = Math.Abs((res.TargetPoint2d - src.Anchor2d).Dot(axis));
                var seg = new Core.PlannedSegment
                {
                    LengthFt    = axialLen,
                    TextWidthFt = EstimateTextWidth(axialLen, coreCfg.TextHeightFt),
                };

                dims.Add(new Core.PlannedDimension
                {
                    SourceKey   = src.SourceKey,
                    TargetKey   = res.TargetKey,
                    TargetType  = slab ? Core.TargetType.SlabEdge : Core.TargetType.Grid,
                    SourcePoint = src.Anchor2d,
                    TargetPoint = res.TargetPoint2d,
                    AxisDir     = axis,
                    Side        = Core.DimSide.Positive,
                    OffsetFt    = coreCfg.FirstOffsetFt,
                    Segments    = new List<Core.PlannedSegment> { seg },
                });

                output.Refs[src.SourceKey] = new PlannedRefBundle { Source = src.SourceRef, Target = res.TargetRef! };
            }

            _log($"Resolved {dims.Count}/{sources.Count} target(s) — {plan.Unresolved.Count} unresolved, {plan.Ambiguities.Count} ambiguous.",
                dims.Count > 0 ? "info" : "fail");

            // ── 3–6. Abstract layout (Part B) ─────────────────────────────────
            var obstacles = CollectObstacles(doc, view, projection);
            var scorer = new Core.LayoutScorer(coreCfg, null /* crop scoring optional in Tier 1 */);
            var layout = new Core.GreedyLayoutEngine(coreCfg, scorer);
            layout.Arrange(dims, obstacles);

            int leadered = Core.GreedyLayoutEngine.LeaderedCount(dims);
            if (leadered > 0) plan.Notes.Add($"{leadered} segment(s) leadered to fit dense text.");

            var finalScore = scorer.ScoreAll(dims, obstacles);
            if (finalScore.Hard > 1e-6)
                plan.Notes.Add($"Layout left {finalScore.Hard:0} hard-constraint penalty — some strings may still overlap (unsatisfiable in Tier 1).");

            plan.Dimensions = dims;
            return output;
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static Core.LayoutConfig BuildCoreConfig(Core.LayoutConfig paper, double scale)
        {
            // Spacing/offset/text are paper-space; multiply by the view scale to mix with the
            // model-space source/target points the projection produced.
            return new Core.LayoutConfig
            {
                SchemaVersion       = paper.SchemaVersion,
                StringSpacingFt     = paper.StringSpacingFt * scale,
                FirstOffsetFt       = paper.FirstOffsetFt * scale,
                PrecisionFt         = paper.PrecisionFt,            // display tolerance, model-space
                TextHeightFt        = paper.TextHeightFt * scale,
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

        private static double EstimateTextWidth(double valueFt, double textHeightModelFt)
        {
            // Rough value string ("12.34'") → character count → width at ~0.6× height per glyph.
            string s = valueFt.ToString("0.##", CultureInfo.InvariantCulture) + "'";
            int chars = Math.Max(3, s.Length);
            return chars * textHeightModelFt * 0.6;
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
