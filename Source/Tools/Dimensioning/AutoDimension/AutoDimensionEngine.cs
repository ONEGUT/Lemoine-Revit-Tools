using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Dimensioning.AutoDimension.Resolvers;

namespace LemoineTools.Tools.Dimensioning.AutoDimension
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

        /// <summary>The view's static annotation obstacles (view-2D boxes) — commit uses these
        /// so relocated tag text never lands on a pre-existing annotation.</summary>
        public IReadOnlyList<Core.Box2> Obstacles { get; set; } = new List<Core.Box2>();
    }

    /// <summary>One EXTREME dense area: too packed to dimension legibly even chained at this
    /// view's scale — dimension it in an enlarged-plan callout instead (the callout tier).
    /// Produced by <see cref="AutoDimensionEngine.SurveyDenseAreas"/>; consumed by the Clash
    /// Finder, which creates/reuses the callout view, marks it, and dimensions it.</summary>
    public sealed class DenseCalloutRequest
    {
        public string ClusterId { get; set; } = "";
        /// <summary>World corners of the callout rectangle on the view plane: the cluster box
        /// unioned with the containing room(s)' footprints (host or linked), plus a margin —
        /// a callout always reads a little larger than the room its clashes sit in.</summary>
        public XYZ MinWorld { get; set; } = XYZ.Zero;
        public XYZ MaxWorld { get; set; } = XYZ.Zero;
        /// <summary>Computed callout scale denominator (e.g. 24 for 1:24) at which the area's
        /// text demand fits its extent.</summary>
        public int Scale { get; set; } = 12;
        /// <summary>Members to EXCLUDE from the parent view's dimension pass.</summary>
        public List<string> SourceKeys { get; } = new List<string>();
        public int ClashCount { get; set; }

        /// <summary>For a USER-drawn callout adopted as a pre-defined group: the existing
        /// callout view to take over. <see cref="ElementId.InvalidElementId"/> for the
        /// automatic dense tier, which creates its own views.</summary>
        public ElementId ExistingViewId { get; set; } = ElementId.InvalidElementId;

        /// <summary>World corners of the MEMBERSHIP rectangle of an adopted user callout —
        /// the boundary the user drew, which defines the group. Stamped on the view (via
        /// <c>UserCalloutSchema</c>) so room growth on this run never widens the group on
        /// the next. Unused by the automatic dense tier.</summary>
        public XYZ MembershipMinWorld { get; set; } = XYZ.Zero;
        public XYZ MembershipMaxWorld { get; set; } = XYZ.Zero;
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

        /// <summary>Chain tier handles density ratios up to this; beyond it the area gets a
        /// callout. Ratio = nominal text width × (distinct refs − 1) / extent, worst axis.</summary>
        private const double CalloutDemandRatio = 2.5;

        /// <summary>Standard callout scale denominators, coarsest first.</summary>
        private static readonly int[] CalloutScales = { 64, 48, 32, 24, 16, 12 };

        public EngineOutput BuildPlan(Document doc, View view, AutoDimensionConfig cfg,
            List<Resolvers.ManualDatum>? datums = null, List<Resolvers.SlabScope>? slabScopes = null,
            List<string>? excludeSources = null, bool boundTargetsToCrop = false)
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
                             .OrderBy(l => l.Id.Value))
                {
                    var ld = li.GetLinkDocument();
                    if (ld == null) continue;
                    ctx.Sources.Add(new SourceDoc { Doc = ld, Link = li, Transform = li.GetTotalTransform() });
                }
            }

            // Dense-area callouts dimension only to references SHOWN in their crop: project the
            // crop box into view-2D and let the resolvers reject candidates landing outside it.
            if (boundTargetsToCrop)
            {
                ctx.TargetBounds = CropBounds2D(view, projection);
                if (ctx.TargetBounds.HasValue)
                    _log($"Targets constrained to the visible crop {ctx.TargetBounds.Value} — "
                       + "each clash dimensions to the nearest reference shown in this callout.", "info");
            }

            // ── 1. Ingest source cross-lines ──────────────────────────────────
            var sources = SourceIngest.Collect(doc, view, projection, plan.Unresolved);
            _log($"Ingest: {sources.Count} source line(s), {plan.Unresolved.Count} without a usable reference.", "info");

            // Callout tier: clashes deferred to enlarged dense-area callouts are not
            // dimensioned in this (parent) view — they keep their markers + the callout bubble.
            if (excludeSources != null && excludeSources.Count > 0)
            {
                var excl = new HashSet<string>(excludeSources, StringComparer.Ordinal);
                int before = sources.Count;
                sources = sources.Where(s => !excl.Contains(s.SourceKey)).ToList();
                if (before != sources.Count)
                    _log($"{before - sources.Count} clash(es) deferred to enlarged dense-area callout(s) — not dimensioned here.", "info");
            }
            if (sources.Count == 0)
            {
                plan.Notes.Add("No tagged source cross-lines found in the view (or all deferred to callouts).");
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

            // ── 1b. Cluster the clashes by PAPER-space proximity — the unit of the whole pass ──
            // Every clash belongs to exactly one cluster; runs, chains, regions, layout, and
            // callouts are all cluster-scoped. The link distance is a sheet-inch setting × the
            // view scale, so grouping reads identically at every scale (parents and callouts).
            double linkFt  = cfg.ClusterLinkFt(scale);
            double crossFt = cfg.RunCrossFt(scale);
            var clustering = ClashClusterer.Build(sources, linkFt);
            _log($"Clustered {sources.Count} clash(es) into {clustering.Clusters.Count} cluster(s) "
               + $"(link ≤{cfg.ClusterLinkPaperIn:0.###}\" paper = {linkFt:0.##} ft at 1:{scale:0}).", "info");

            // Collinear runs WITHIN each cluster — a run never spans clusters; run ids are
            // prefixed with the cluster id so the chainer never merges across a boundary.
            var runs = new Dictionary<string, ClashRunGrouper.RunInfo>(StringComparer.Ordinal);
            var nearMisses = new List<string>();
            if (cfg.ChainAligned)
            {
                int runCount = 0;
                var srcByKey = sources.ToDictionary(s => s.SourceKey, s => s, StringComparer.Ordinal);
                foreach (var cluster in clustering.Clusters)
                {
                    var subset = cluster.MemberKeys
                        .Where(srcByKey.ContainsKey)
                        .Select(k => srcByKey[k])
                        .ToList();
                    var g = ClashRunGrouper.Build(subset, crossFt, linkFt);
                    var remap = new Dictionary<ClashRunGrouper.RunInfo, ClashRunGrouper.RunInfo>();
                    foreach (var kv in g.Map)
                    {
                        if (!remap.TryGetValue(kv.Value, out var info))
                        {
                            remap[kv.Value] = info = new ClashRunGrouper.RunInfo
                            {
                                RunId     = cluster.Id + "|" + kv.Value.RunId,
                                LongAxis  = kv.Value.LongAxis,
                                CrossAxis = kv.Value.CrossAxis,
                            };
                        }
                        runs[kv.Key] = info;
                    }
                    runCount += g.RunCount;
                    nearMisses.AddRange(g.NearMisses);
                }
                _log($"Grouped into {runCount} run(s) within the clusters "
                   + $"(off-line ≤{cfg.RunCrossPaperIn:0.####}\" paper = {crossFt:0.##} ft).", "info");
                foreach (var miss in nearMisses) _log(miss, "info");
                if (nearMisses.Count > 0)
                    plan.Notes.Add($"{nearMisses.Count} near-miss grouping pair(s) — the log shows which "
                                 + "distance kept them apart (tune the grouping distances in Settings → Dimensions).");
            }

            // ── 1c. Oversaturated areas: clashes packed tighter than their value texts get
            // collapsed into one chain per axis (split by nearest reference) — solo strings
            // are unplaceable there by construction (their witness forests cross everything).
            // Link radius = the nominal value-text width (~8 glyphs) at this view's scale.
            double nominalTextFt = coreCfg.TextHeightFt * 4.8;
            var density = cfg.DensityChaining
                ? DensityClusterer.Build(sources, nominalTextFt, minCount: 4)
                : new DensityClusterer.Result();
            if (cfg.DensityChaining && density.ClusterCount > 0)
            {
                _log($"Density: {density.ClusterByKey.Count} clash(es) in {density.ClusterCount} oversaturated "
                   + $"area(s) (link ≤{nominalTextFt:0.#} ft) — chaining per axis, split by nearest reference.", "info");
                foreach (var s in density.Summaries) _log(s, "info");
                plan.Notes.Add($"{density.ClusterCount} dense area(s) collapsed into per-axis chains.");
            }

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
                        // Dense-pocket members chain per axis under their pocket id; otherwise a
                        // clash with no clustered run is its own solo run (one dim per axis).
                        runs.TryGetValue(src.SourceKey, out var run);
                        bool dense = density.ClusterByKey.TryGetValue(src.SourceKey, out var pocketId);
                        clustering.ClusterByKey.TryGetValue(src.SourceKey, out var groupId);
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
                            ClusterId   = groupId ?? "",
                            RunId       = dense ? "dense|" + pocketId
                                                : run?.RunId ?? ("solo|" + src.SourceKey),
                            RunLongAxis = run?.LongAxis ?? new Core.Vec2(1, 0),
                            ForceChain  = dense,
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

            // ── 2c. Cluster working regions ────────────────────────────────────
            // Each cluster's tight box grows to cover its dimensions' far side (the resolved
            // grid / slab-edge targets), then every box balloons outward at the same rate
            // until the neighbours' edges meet — equal spacing — so each group owns a fair
            // share of the surrounding empty space to lay its strings and tags out in.
            var clusterById = clustering.Clusters.ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);
            foreach (var d in dims)
            {
                if (string.IsNullOrEmpty(d.ClusterId)
                    || !clusterById.TryGetValue(d.ClusterId, out var cl)) continue;
                var grown = cl.TightBox.Union(Core.Box2.FromPoints(d.TargetPoint, d.TargetPoint));
                foreach (var r in d.RefAnchors) grown = grown.Union(Core.Box2.FromPoints(r, r));
                cl.TightBox = grown;
            }
            ClashClusterer.GrowRegions(clustering.Clusters, maxPadFt: Math.Max(linkFt, nominalTextFt));
            foreach (var d in dims)
            {
                if (!string.IsNullOrEmpty(d.ClusterId)
                    && clusterById.TryGetValue(d.ClusterId, out var cl))
                {
                    d.Region    = cl.Region;
                    d.HasRegion = true;
                }
            }

            // ── 3–6. Abstract layout (Part B) ─────────────────────────────────
            _log($"Collecting obstacles + laying out {dims.Count} dimension(s) (collision-aware, ≤{coreCfg.TimeCapMs} ms)…", "info");
            var obstacles = CollectObstacles(doc, view, projection);
            output.Obstacles = obstacles;
            var scorer = new Core.LayoutScorer(coreCfg, null /* crop scoring optional in Tier 1 */);
            var layout = new Core.GreedyLayoutEngine(coreCfg, scorer);
            layout.Arrange(dims, obstacles);
            _log($"Layout done ({obstacles.Count} obstacle(s) considered).", "info");

            int movedTags = Core.GreedyLayoutEngine.MovedTagCount(dims);
            if (movedTags > 0) plan.Notes.Add($"{movedTags} value tag(s) wider than their crossbar pulled off into tag columns.");

            var finalScore = scorer.ScoreAll(dims, obstacles);
            if (finalScore.Hard > 1e-6)
            {
                plan.Notes.Add($"Layout left {finalScore.Hard:0} hard-constraint penalty — some strings may still overlap (unsatisfiable at this density).");
                int noted = 0;
                foreach (var d in dims)
                {
                    if (noted >= 8) { plan.Notes.Add("…further unresolved strings omitted."); break; }
                    if (scorer.Score(d, obstacles, dims).Hard <= 1e-6) continue;
                    string why = scorer.DescribeHardViolations(d, obstacles, dims);
                    if (why.Length > 0)
                    {
                        plan.Notes.Add($"Unresolved: {d.SourceKey} — {why}.");
                        noted++;
                    }
                }
            }

            // ── Layout snapshot (data harvester) — full problem + solution per view ──
            if (cfg.DumpLayoutSnapshots)
            {
                var snap = Core.LayoutSnapshotWriter.Build(
                    view.Name, (int)scale, coreCfg, dims, obstacles, scorer,
                    nearMisses, plan.Notes);
                string? path = Core.LayoutSnapshotWriter.Write(snap);
                _log(path != null
                    ? $"Layout snapshot written: {path}"
                    : "Layout snapshot FAILED to write — see diagnostics.log.", path != null ? "info" : "fail");
                if (path != null) plan.Notes.Add($"Layout snapshot: {path}");
            }

            plan.Dimensions = dims;
            return output;
        }

        // ── Callout tier survey ────────────────────────────────────────────────
        /// <summary>
        /// Read-only pre-pass for the callout tier: ingests the view's source cross-lines,
        /// clusters them, and returns one request per EXTREME dense area — too dense to dimension
        /// legibly even chained (demand ratio &gt; <see cref="CalloutDemandRatio"/>). Each area's
        /// rectangle is grown to the room(s) its clashes sit in (host or linked) plus a margin;
        /// overlapping areas are merged into one callout; and EVERY clash inside the final
        /// rectangle is swept into the request (so the whole room moves to the callout).
        /// Moderate clusters return nothing (the chain tier keeps them). Never throws.
        /// </summary>
        public static List<DenseCalloutRequest> SurveyDenseAreas(
            Document doc, View view, AutoDimensionConfig cfg, Action<string, string>? log = null)
        {
            var requests = new List<DenseCalloutRequest>();
            try
            {
                var projection = new ViewProjection(view);
                var sources = SourceIngest.Collect(doc, view, projection, new List<Core.UnresolvedTarget>());
                if (sources.Count == 0) return requests;

                double scale = view.Scale <= 0 ? 1 : view.Scale;
                double textPaperFt = ReadDimTextSizeFt(doc, ResolveDimType(doc, cfg.DimensionTypeName))
                                  ?? cfg.Layout.TextHeightFt;
                double thModel  = textPaperFt * scale;
                double nominal  = thModel * 4.8;   // nominal value-text width (~8 glyphs)
                int minClashes  = Math.Max(2, cfg.CalloutMinClashes);

                // One callout candidate per CLUSTER — the same paper-space clusters the
                // dimension pass works in, so a promoted area is exactly one cluster.
                var clustering = ClashClusterer.Build(sources, cfg.ClusterLinkFt(scale));
                var clusters = new List<DensityClusterer.ClusterInfo>();
                foreach (var cl in clustering.Clusters)
                {
                    if (cl.MemberKeys.Count < 2) continue;   // a lone clash is never a callout
                    var info = new DensityClusterer.ClusterInfo
                    {
                        Id   = cl.Id,
                        MinX = cl.TightBox.MinX, MinY = cl.TightBox.MinY,
                        MaxX = cl.TightBox.MaxX, MaxY = cl.TightBox.MaxY,
                    };
                    info.MemberKeys.AddRange(cl.MemberKeys);
                    info.MemberPoints.AddRange(cl.MemberPoints);
                    clusters.Add(info);
                }
                if (clusters.Count == 0) return requests;

                // Room growth needs the clashes' world anchors; the resolver is built lazily —
                // most views have no extreme cluster and skip the link scan entirely.
                var anchors = new Dictionary<string, XYZ>(StringComparer.Ordinal);
                foreach (var s in sources)
                    if (s != null && !string.IsNullOrEmpty(s.SourceKey)) anchors[s.SourceKey] = s.Anchor3d;
                Resolvers.RoomBoundsResolver? rooms = null;
                double margin = nominal * 0.75;

                // ── 1. One room-grown, margin-padded rectangle per EXTREME cluster ──
                // The callout must always read a little larger than the room(s) its clashes
                // sit in (rooms usually live in a linked architectural model): union the
                // containing rooms' footprints with the cluster box, then add the margin.
                var areas = new List<DenseArea>();
                foreach (var cluster in clusters)
                {
                    double ratio = DemandRatio(cluster, nominal, thModel);
                    if (ratio <= CalloutDemandRatio) continue;   // chain tier handles it
                    log?.Invoke($"Dense area: demand ratio {ratio:0.0} > {CalloutDemandRatio:0.0} "
                              + $"({cluster.MemberKeys.Count} clashes) — callout tier.", "info");

                    double minX = cluster.MinX, minY = cluster.MinY;
                    double maxX = cluster.MaxX, maxY = cluster.MaxY;
                    rooms ??= new Resolvers.RoomBoundsResolver(doc, view);
                    var area = new DenseArea();
                    foreach (var key in cluster.MemberKeys)
                    {
                        if (!anchors.TryGetValue(key, out var anchor3d)) continue;
                        var hit = rooms.FindRoom(anchor3d);
                        if (hit == null || !area.RoomKeys.Add(hit.Key)) continue;
                        foreach (var corner in hit.Corners)
                        {
                            var p = projection.To2D(corner);
                            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                        }
                        area.RoomLabels.Add(hit.Label);
                    }
                    area.Rect = new Core.Box2(minX - margin, minY - margin, maxX + margin, maxY + margin);
                    areas.Add(area);
                }

                // ── 2. Merge overlapping rectangles ──
                // Two clusters grown to the same (or adjacent) room would otherwise produce
                // overlapping callouts. Repeat until stable — a merge can create a new overlap.
                for (bool merged = true; merged; )
                {
                    merged = false;
                    for (int i = 0; i < areas.Count && !merged; i++)
                        for (int j = i + 1; j < areas.Count && !merged; j++)
                            if (areas[i].Rect.Intersects(areas[j].Rect))
                            {
                                areas[i].Absorb(areas[j]);
                                areas.RemoveAt(j);
                                merged = true;
                            }
                }

                // ── 3. Sweep + emit ──
                // EVERY clash inside an area's rectangle belongs to its callout — not just the
                // dense-cluster members — so the whole room marks/dimensions in the callout and
                // nothing is left behind in the parent view.
                int seq = 0;
                foreach (var area in areas)
                {
                    var swept = sources.Where(s => area.Rect.Contains(s.Anchor2d)).ToList();
                    if (swept.Count == 0) continue;

                    // Minimum-marker gate: a pocket that sweeps fewer than the configured
                    // minimum never becomes a callout — it stays chained in the parent view.
                    if (swept.Count < minClashes)
                    {
                        log?.Invoke($"Dense area with {swept.Count} clash(es) is under the callout minimum "
                                  + $"of {minClashes} — kept on the chain tier in the parent view.", "info");
                        continue;
                    }

                    // Largest standard scale at which the swept set's text demand fits (ratio
                    // scales linearly with the view scale), and meaningfully larger than the
                    // parent. The scale pick uses a GENEROUS text width (~11 glyphs — real
                    // imperial strings like 2'-3 1/2" — vs the 8-glyph detection nominal) so
                    // segments genuinely fit inline instead of landing just-barely cramped again.
                    var sweptInfo = new DensityClusterer.ClusterInfo
                    {
                        MinX = double.MaxValue, MinY = double.MaxValue,
                        MaxX = double.MinValue, MaxY = double.MinValue,
                    };
                    foreach (var s in swept)
                    {
                        var p = s.Anchor2d;
                        sweptInfo.MemberPoints.Add(p);
                        if (p.X < sweptInfo.MinX) sweptInfo.MinX = p.X; if (p.X > sweptInfo.MaxX) sweptInfo.MaxX = p.X;
                        if (p.Y < sweptInfo.MinY) sweptInfo.MinY = p.Y; if (p.Y > sweptInfo.MaxY) sweptInfo.MaxY = p.Y;
                    }
                    double scaleRatio = DemandRatio(sweptInfo, thModel * 6.0, thModel);
                    double bound = scale / Math.Max(scaleRatio, 1e-9);
                    // Never zoom in past the configured finest scale — keeps callouts from blowing up.
                    int floorScale = Math.Max(1, cfg.MaxCalloutScale);
                    int chosen = floorScale;
                    foreach (var s in CalloutScales)
                        if (s >= floorScale && s <= bound && s <= scale / 2.0) { chosen = s; break; }

                    string id = "c" + seq.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                    seq++;
                    var req = new DenseCalloutRequest
                    {
                        ClusterId  = id,
                        MinWorld   = projection.From2D(new Core.Vec2(area.Rect.MinX, area.Rect.MinY)),
                        MaxWorld   = projection.From2D(new Core.Vec2(area.Rect.MaxX, area.Rect.MaxY)),
                        Scale      = chosen,
                        ClashCount = swept.Count,
                    };
                    req.SourceKeys.AddRange(swept.Select(s => s.SourceKey));
                    requests.Add(req);

                    string roomsTxt = area.RoomLabels.Count > 0
                        ? $"covers {string.Join(", ", area.RoomLabels)} plus a margin"
                        : "no room found at its clashes — sized to the cluster extent";
                    string mergeTxt = area.MergedClusters > 1
                        ? $", merged from {area.MergedClusters} overlapping dense areas" : "";
                    log?.Invoke($"Dense area {id}: callout at 1:{chosen} {roomsTxt}{mergeTxt} — "
                              + $"all {swept.Count} clash(es) inside it move to the callout.", "info");
                }
            }
            catch (Exception ex)
            {
                LemoineLog.Error("AutoDimensionEngine: survey dense areas", ex);
                log?.Invoke($"Dense-area survey failed ({ex.Message}) — callout tier skipped this view.", "fail");
            }
            return requests;
        }

        // ── User-callout tier survey ───────────────────────────────────────────
        /// <summary>Two crop rectangles within this match (per axis, model ft) count as "the
        /// tool's own growth still in place" — beyond it the user resized the callout by hand.</summary>
        private const double UserCalloutRectTolFt = 0.01;

        /// <summary>
        /// Read-only pre-pass for USER-drawn callouts: every plan callout of this view that the
        /// user drew (anything not named by the automatic dense tier) and that contains at least
        /// one clash marker becomes one pre-defined group — its clashes mark and dimension in
        /// THAT callout and never cluster with markers outside it. Group membership is the
        /// rectangle the user drew (re-read from the <c>UserCalloutSchema</c> stamp on re-runs,
        /// so the tool's own room growth never widens the group; a hand-resized callout
        /// re-baselines to the user's new boundary). The emitted crop is grown to the containing
        /// room(s) plus a margin — same as the dense tier — and the scale is only coarsened when
        /// the members' text demand needs it. User areas are NEVER merged with each other or
        /// with automatic dense areas: separation is the point. A clash inside two user callouts
        /// joins the first (lowest view id). Never throws.
        /// </summary>
        public static List<DenseCalloutRequest> SurveyUserCallouts(
            Document doc, View view, AutoDimensionConfig cfg, Action<string, string>? log = null)
        {
            var requests = new List<DenseCalloutRequest>();
            try
            {
                var callouts = CollectUserCallouts(doc, view);
                if (callouts.Count == 0)
                {
                    // Say so explicitly — a drawn callout the collector failed to see would
                    // otherwise be indistinguishable from "none drawn" in the run log.
                    log?.Invoke($"No user-drawn callouts found on '{view.Name}'.", "info");
                    return requests;
                }
                log?.Invoke($"Found {callouts.Count} user-drawn callout(s) on '{view.Name}'.", "info");

                var projection = new ViewProjection(view);
                var sources = SourceIngest.Collect(doc, view, projection, new List<Core.UnresolvedTarget>());
                if (sources.Count == 0) return requests;

                double scale = view.Scale <= 0 ? 1 : view.Scale;
                double textPaperFt = ReadDimTextSizeFt(doc, ResolveDimType(doc, cfg.DimensionTypeName))
                                  ?? cfg.Layout.TextHeightFt;
                double thModel = textPaperFt * scale;
                double nominal = thModel * 4.8;
                double margin  = nominal * 0.75;

                var claimed = new HashSet<string>(StringComparer.Ordinal);
                int seq = 0;

                foreach (var callout in callouts)
                {
                    var cropRect = CropBounds2D(callout, projection);
                    if (cropRect == null)
                    {
                        log?.Invoke($"User callout '{callout.Name}': no readable crop — skipped.", "info");
                        continue;
                    }

                    // Membership: the user's original boundary survives the tool's own room
                    // growth (read back from the stamp), but a hand-resized callout wins.
                    var membership = cropRect.Value;
                    var stamped = UserCalloutSchema.Read(callout);
                    if (stamped != null)
                    {
                        var applied = Core.Box2.FromPoints(
                            projection.To2D(stamped.AppliedMin), projection.To2D(stamped.AppliedMax));
                        if (RectsMatch(cropRect.Value, applied))
                            membership = Core.Box2.FromPoints(
                                projection.To2D(stamped.MembershipMin), projection.To2D(stamped.MembershipMax));
                        else
                            log?.Invoke($"User callout '{callout.Name}' was resized by hand — its group "
                                      + "re-baselines to the new boundary.", "info");
                    }

                    var members = sources
                        .Where(s => membership.Contains(s.Anchor2d) && !claimed.Contains(s.SourceKey))
                        .ToList();
                    int overlapped = sources.Count(s => membership.Contains(s.Anchor2d)) - members.Count;
                    if (overlapped > 0)
                        log?.Invoke($"User callout '{callout.Name}': {overlapped} clash(es) already claimed "
                                  + "by an earlier user callout — first containment wins.", "info");
                    if (members.Count == 0)
                    {
                        log?.Invoke($"User callout '{callout.Name}' contains no clash markers — left untouched.", "info");
                        continue;
                    }
                    foreach (var m in members) claimed.Add(m.SourceKey);

                    // Crop to the boundary the USER drew (plus a small margin), NOT the containing
                    // room. Growing the crop out to the room moved the dimensioned region away from
                    // where the callout was drawn — and could leave the drawn spot bare — so the
                    // user's boundary is authoritative. Dimensions reach only references visible
                    // inside it; a clash whose target sits outside is reported unresolved (the run
                    // log says so), never silently relocated to another area.
                    var grown = new Core.Box2(
                        membership.MinX - margin, membership.MinY - margin,
                        membership.MaxX + margin, membership.MaxY + margin);

                    // Scale: the dense tier's pick (generous ~11-glyph text width), applied only
                    // when it is coarser-to-fit than what the user already set — a callout the
                    // user already enlarged further is left alone.
                    var info = new DensityClusterer.ClusterInfo
                    {
                        MinX = double.MaxValue, MinY = double.MaxValue,
                        MaxX = double.MinValue, MaxY = double.MinValue,
                    };
                    foreach (var m in members)
                    {
                        var p = m.Anchor2d;
                        info.MemberPoints.Add(p);
                        if (p.X < info.MinX) info.MinX = p.X; if (p.X > info.MaxX) info.MaxX = p.X;
                        if (p.Y < info.MinY) info.MinY = p.Y; if (p.Y > info.MaxY) info.MaxY = p.Y;
                    }
                    double ratio = DemandRatio(info, thModel * 6.0, thModel);
                    double bound = scale / Math.Max(ratio, 1e-9);
                    int floorScale = Math.Max(1, cfg.MaxCalloutScale);   // finest scale the pick may reach
                    int chosen = floorScale;
                    foreach (var s in CalloutScales)
                        if (s >= floorScale && s <= bound && s <= scale / 2.0) { chosen = s; break; }
                    int current = callout.Scale <= 0 ? (int)scale : callout.Scale;
                    int finalScale = Math.Min(current, chosen);

                    var req = new DenseCalloutRequest
                    {
                        ClusterId  = "u" + (seq++).ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                        MinWorld   = projection.From2D(new Core.Vec2(grown.MinX, grown.MinY)),
                        MaxWorld   = projection.From2D(new Core.Vec2(grown.MaxX, grown.MaxY)),
                        Scale      = finalScale,
                        ClashCount = members.Count,
                        ExistingViewId     = callout.Id,
                        MembershipMinWorld = projection.From2D(new Core.Vec2(membership.MinX, membership.MinY)),
                        MembershipMaxWorld = projection.From2D(new Core.Vec2(membership.MaxX, membership.MaxY)),
                    };
                    req.SourceKeys.AddRange(members.Select(m => m.SourceKey));
                    requests.Add(req);

                    log?.Invoke($"User callout '{callout.Name}': pre-defined group of {members.Count} "
                              + $"clash(es) at 1:{finalScale} — crop kept at the boundary you drew plus a margin.", "info");
                }
            }
            catch (Exception ex)
            {
                LemoineLog.Error("AutoDimensionEngine: survey user callouts", ex);
                log?.Invoke($"User-callout survey failed ({ex.Message}) — user callouts skipped this view.", "fail");
            }
            return requests;
        }

        /// <summary>Non-template callout views whose parent is <paramref name="parentView"/>,
        /// excluding the dense tier's own "- Dense" views, ordered by id so claiming is
        /// deterministic. Covers EVERY callout class — Revit's default callout type on a plan
        /// is a Detail view (a <see cref="ViewSection"/>, not a <see cref="ViewPlan"/>), so
        /// filtering to ViewPlan silently missed most user-drawn callouts. A collector failure
        /// propagates to the survey's catch, which reports it to the run log (user callouts
        /// skipped for the view) — never silently.</summary>
        private static List<View> CollectUserCallouts(Document doc, View parentView)
        {
            var result = new List<View>();
            string densePrefix = parentView.Name + " - Dense ";
            foreach (var v in new FilteredElementCollector(doc)
                         .OfClass(typeof(View)).Cast<View>()
                         .Where(vw => !vw.IsTemplate)
                         .OrderBy(vw => vw.Id.Value))
            {
                if (v.Name.StartsWith(densePrefix, StringComparison.Ordinal)) continue;
                ElementId parentId;
                // A view that is not a callout has no parent — GetCalloutParentId returning
                // InvalidElementId (or throwing on view kinds that can never be callouts) is
                // the expected probe result for most views, not a failure (deliberately not
                // routed to LemoineLog: it would fire per view per run).
                try { parentId = v.GetCalloutParentId(); }
                catch { continue; }
                if (parentId == parentView.Id) result.Add(v);
            }
            return result;
        }

        private static bool RectsMatch(Core.Box2 a, Core.Box2 b) =>
            Math.Abs(a.MinX - b.MinX) <= UserCalloutRectTolFt
         && Math.Abs(a.MinY - b.MinY) <= UserCalloutRectTolFt
         && Math.Abs(a.MaxX - b.MaxX) <= UserCalloutRectTolFt
         && Math.Abs(a.MaxY - b.MaxY) <= UserCalloutRectTolFt;

        /// <summary>One dense-callout footprint while merging: the room-grown, margin-padded
        /// rectangle plus which rooms it covers. Overlapping footprints are absorbed into one.</summary>
        private sealed class DenseArea
        {
            public Core.Box2 Rect;
            public HashSet<string> RoomKeys { get; } = new HashSet<string>(StringComparer.Ordinal);
            public List<string> RoomLabels { get; } = new List<string>();
            public int MergedClusters = 1;

            public void Absorb(DenseArea o)
            {
                Rect = Rect.Union(o.Rect);
                foreach (var k in o.RoomKeys) RoomKeys.Add(k);
                foreach (var l in o.RoomLabels) if (!RoomLabels.Contains(l)) RoomLabels.Add(l);
                MergedClusters += o.MergedClusters;
            }
        }

        /// <summary>The view's active crop rectangle projected into its 2D plane, or null when
        /// the view has no usable crop. Never throws.</summary>
        private static Core.Box2? CropBounds2D(View view, ViewProjection projection)
        {
            try
            {
                if (!view.CropBoxActive || view.CropBox == null) return null;
                var cb = view.CropBox;
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (double x in new[] { cb.Min.X, cb.Max.X })
                    foreach (double y in new[] { cb.Min.Y, cb.Max.Y })
                        foreach (double z in new[] { cb.Min.Z, cb.Max.Z })
                        {
                            var p = projection.To2D(cb.Transform.OfPoint(new XYZ(x, y, z)));
                            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                        }
                return new Core.Box2(minX, minY, maxX, maxY);
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("AutoDimensionEngine: read crop bounds", ex);
                return null;
            }
        }

        /// <summary>Worst-axis density ratio of a cluster: nominal text width × (distinct
        /// reference positions − 1) over the extent — &gt;1 means texts can't sit inline,
        /// &gt;~2.5 means even tag columns drown. Distinctness at half a text height mirrors
        /// the chainer's coincident-reference dedupe.</summary>
        private static double DemandRatio(DensityClusterer.ClusterInfo cluster, double nominal, double th)
        {
            int nx = DistinctCount(cluster.MemberPoints.Select(p => p.X), th * 0.5);
            int ny = DistinctCount(cluster.MemberPoints.Select(p => p.Y), th * 0.5);
            double ex = Math.Max(cluster.MaxX - cluster.MinX, nominal);
            double ey = Math.Max(cluster.MaxY - cluster.MinY, nominal);
            double rx = nominal * Math.Max(nx - 1, 0) / ex;
            double ry = nominal * Math.Max(ny - 1, 0) / ey;
            return Math.Max(rx, ry);
        }

        private static int DistinctCount(IEnumerable<double> values, double tol)
        {
            int n = 0;
            double last = double.NaN;
            foreach (var v in values.OrderBy(v => v))
            {
                if (double.IsNaN(last) || v - last > tol) { n++; last = v; }
            }
            return n;
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static Core.LayoutConfig BuildCoreConfig(Core.LayoutConfig paper, double scale, double textHeightPaperFt)
        {
            // Spacing/offset/text are paper-space; multiply by the view scale to mix with the
            // model-space source/target points the projection produced. Text height comes from the
            // actual DimensionType (textHeightPaperFt) so cramped-detection + stagger match reality.
            return new Core.LayoutConfig
            {
                SchemaVersion        = paper.SchemaVersion,
                StringSpacingFt      = paper.StringSpacingFt * scale,
                FirstOffsetFt        = paper.FirstOffsetFt * scale,
                PrecisionFt          = paper.PrecisionFt,            // display tolerance, model-space
                TextHeightFt         = textHeightPaperFt * scale,
                WitnessGapFt         = paper.WitnessGapFt * scale,
                WitnessOvershootFt   = paper.WitnessOvershootFt * scale,
                TagColumnBaseHeights = paper.TagColumnBaseHeights,   // text-height multiples — scale-free
                TagColumnStepHeights = paper.TagColumnStepHeights,
                TagColumnAlongHeights = paper.TagColumnAlongHeights,
                OverlapWeight        = paper.OverlapWeight,
                OffCropWeight        = paper.OffCropWeight,
                WitnessCrossWeight   = paper.WitnessCrossWeight,
                CrossingWeight       = paper.CrossingWeight,
                LeaderCrossWeight    = paper.LeaderCrossWeight,
                LeaderLineCrossWeight = paper.LeaderLineCrossWeight,
                LeaderSlackWeight    = paper.LeaderSlackWeight,
                CrampedWeight        = paper.CrampedWeight,
                UnevenSpacingWeight  = paper.UnevenSpacingWeight,
                LeaderWeight         = paper.LeaderWeight,
                RegionWeight         = paper.RegionWeight,
                MaxRepairPasses      = paper.MaxRepairPasses,
                AlignSharedRows      = paper.AlignSharedRows,
                StaggerStackedText   = paper.StaggerStackedText,
                StaggerWeight        = paper.StaggerWeight,
                MaxIterations        = paper.MaxIterations,
                TimeCapMs            = paper.TimeCapMs,
                PlateauEpsilon       = paper.PlateauEpsilon,
                MaxOffsetSteps       = paper.MaxOffsetSteps,
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
                FormatOptions? fo = dt?.GetUnitsFormatOptions();
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
                return types.OrderBy(t => t.Id.Value).First().Id;
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
                    .OfClass(typeof(SpotDimension)).WhereElementIsNotElementType());  // spot elevations/coords
                AddBoxesOf(new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(FilledRegion)).WhereElementIsNotElementType());   // incl. our clash markers
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
