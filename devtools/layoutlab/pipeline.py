"""Orchestration — the Python mirror of AutoDimensionEngine.BuildPlan.

Runs the current production algorithm end to end on a synthetic Scene:
ingest -> cluster -> runs -> density -> resolve targets -> chain -> regions ->
layout -> score. No Revit involved.
"""
from geom import Vec2, Box2
from config import LayoutConfig
from chainer import ResolvedItem, build_chains, arch_feet_inch, estimate_text_width
from grouping import build_clusters, build_density, build_runs, grow_regions
from layout import GreedyLayoutEngine, moved_tag_count
from scorer import LayoutScorer, ScoreDetail
from anatomy import recompute_bounds

X_AXIS = Vec2(1, 0)
Y_AXIS = Vec2(0, 1)


class RunOptions:
    """Mirror of the AutoDimensionConfig knobs that reach the layout."""
    def __init__(self):
        self.dimension_both_axes = True
        self.chain_aligned = True
        self.density_chaining = True
        self.cluster_link_paper_in = 0.625
        self.run_cross_paper_in = 0.0625
        self.max_distance_ft = 50.0
        self.axis_tolerance_deg = 15.0
        self.text_paper_ft = (3.0 / 32.0) / 12.0     # dimension type text height
        self.value_denom = 8                          # display precision, 1/8"

    def cluster_link_ft(self, scale):
        return self.cluster_link_paper_in / 12.0 * max(scale, 1.0)

    def run_cross_ft(self, scale):
        return self.run_cross_paper_in / 12.0 * max(scale, 1.0)


class PlanResult:
    def __init__(self):
        self.dims = []
        self.obstacles = []
        self.cfg = None
        self.clusters = []
        self.unresolved = []
        self.notes = []
        self.hard = 0.0
        self.soft = 0.0


def resolve_grid_target(pen, axis, scene, opts):
    """Port of GridTargetResolver: nearest grid running ACROSS the measurement axis."""
    import math
    src_axial = pen.anchor2d.dot(axis)
    perp_cos = math.cos(math.radians(90.0 - opts.axis_tolerance_deg))

    best = None
    for g in scene.grids:
        if abs(g.dir2d.dot(axis)) > perp_cos:
            continue
        grid_axial = g.mid2d(scene.extent).dot(axis)
        dist = abs(grid_axial - src_axial)
        if dist > opts.max_distance_ft:
            continue
        if best is not None and dist >= best[0]:
            continue
        land = pen.anchor2d + axis * (grid_axial - src_axial)
        best = (dist, land, f"grid:{g.label}", g)
    return best


def build_plan(scene, opts=None, cfg_paper=None):
    opts = opts or RunOptions()
    cfg_paper = cfg_paper or LayoutConfig()
    result = PlanResult()

    scale = max(scene.scale, 1)
    cfg = cfg_paper.scaled(scale, opts.text_paper_ft)
    result.cfg = cfg

    def value_fmt(v):
        return arch_feet_inch(v, opts.value_denom)

    sources = list(scene.penetrations)
    if not sources:
        return result

    # ── 1b. Paper-space clusters — the unit of the whole pass ──
    link_ft = opts.cluster_link_ft(scale)
    cross_ft = opts.run_cross_ft(scale)
    clustering = build_clusters(sources, link_ft)
    result.clusters = clustering.clusters

    # ── Collinear runs WITHIN each cluster ──
    runs = {}
    if opts.chain_aligned:
        by_key = {s.source_key: s for s in sources}
        for cl in clustering.clusters:
            subset = [by_key[k] for k in cl.member_keys if k in by_key]
            g, _n = build_runs(subset, cross_ft, link_ft)
            remap = {}
            for k, info in g.items():
                if id(info) not in remap:
                    from grouping import RunInfo
                    remap[id(info)] = RunInfo(cl.id + "|" + info.run_id,
                                              info.long_axis, info.cross_axis)
                runs[k] = remap[id(info)]

    # ── 1c. Oversaturated pockets -> force-chain on every axis ──
    nominal_text_ft = cfg.text_height_ft * 4.8
    density = (build_density(sources, nominal_text_ft, min_count=4)
               if opts.density_chaining else build_density([], 1.0))

    axes = [X_AXIS, Y_AXIS] if opts.dimension_both_axes else [X_AXIS]

    # ── 2. Resolve targets ──
    resolved = []
    resolved_keys = set()
    for src in sources:
        for ax in axes:
            hit = resolve_grid_target(src, ax, scene, opts)
            if hit is None:
                continue
            _dist, land, tkey, _g = hit
            run = runs.get(src.source_key)
            pocket = density.cluster_by_key.get(src.source_key)
            resolved.append(ResolvedItem(
                source_key=src.source_key,
                source2d=src.anchor2d,
                axis=ax,
                target2d=land,
                target_key=tkey,
                cluster_id=clustering.cluster_by_key.get(src.source_key, ""),
                run_id=("dense|" + pocket) if pocket else
                       (run.run_id if run else "solo|" + src.source_key),
                run_long_axis=(run.long_axis if run else Vec2(1, 0)),
                force_chain=bool(pocket)))
            resolved_keys.add(src.source_key)

    for s in sources:
        if s.source_key not in resolved_keys:
            result.unresolved.append(s.source_key)

    # ── 2b. Chain ──
    chained = build_chains(resolved, cfg, value_fmt)
    dims = chained.dims

    # ── 2c. Cluster working regions ──
    by_id = {c.id: c for c in clustering.clusters}
    for d in dims:
        cl = by_id.get(d.cluster_id)
        if cl is None:
            continue
        grown = cl.tight_box.union(Box2.from_points(d.target_point, d.target_point))
        for r in d.ref_anchors:
            grown = grown.union(Box2.from_points(r, r))
        cl.tight_box = grown
    grow_regions(clustering.clusters, max(link_ft, nominal_text_ft))
    for d in dims:
        cl = by_id.get(d.cluster_id)
        if cl is not None:
            d.region = cl.region

    # ── 3-6. Layout ──
    obstacles = sorted((p.box for p in sources), key=lambda b: (b.min_x, b.min_y))
    result.obstacles = obstacles

    scorer = LayoutScorer(cfg, None)
    for d in dims:
        recompute_bounds(d, cfg)
    GreedyLayoutEngine(cfg, scorer).arrange(dims, obstacles)

    result.dims = dims
    result.hard, result.soft = scorer.score_all(dims, obstacles)
    result.scorer = scorer
    result.moved_tags = moved_tag_count(dims)
    return result
