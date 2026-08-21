"""Ports of ClashClusterer.cs, DensityClusterer.cs and ClashRunGrouper.cs.

These three decide WHICH clashes are considered together — and none of their
output is ever revisited by the layout scorer.
"""
import math

from geom import Vec2, Box2


# ── ClashClusterer ─────────────────────────────────────────────────────────────

class Cluster:
    def __init__(self, cid):
        self.id = cid
        self.member_keys = []
        self.member_points = []
        self.tight_box = None
        self.region = None


class ClusterResult:
    def __init__(self):
        self.cluster_by_key = {}
        self.clusters = []


def build_clusters(sources, link_ft):
    """Single-link union-find over paper-space proximity (link_ft in model feet)."""
    result = ClusterResult()
    if not sources:
        return result

    pts = sorted(((s.source_key, s.anchor2d) for s in sources), key=lambda t: t[0])
    parent = list(range(len(pts)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    def union(a, b):
        a, b = find(a), find(b)
        if a != b:
            parent[max(a, b)] = min(a, b)

    link2 = max(link_ft, 0.0) ** 2
    for i in range(len(pts)):
        for j in range(i + 1, len(pts)):
            d = pts[j][1] - pts[i][1]
            if d.x * d.x + d.y * d.y <= link2:
                union(i, j)

    members = {}
    for i in range(len(pts)):
        members.setdefault(find(i), []).append(i)

    for seq, root in enumerate(sorted(members)):
        cl = Cluster(f"g{seq:03d}")
        xs, ys = [], []
        for m in members[root]:
            key, p = pts[m]
            result.cluster_by_key[key] = cl.id
            cl.member_keys.append(key)
            cl.member_points.append(p)
            xs.append(p.x)
            ys.append(p.y)
        cl.tight_box = Box2(min(xs), min(ys), max(xs), max(ys))
        cl.region = cl.tight_box
        result.clusters.append(cl)
    return result


def grow_regions(clusters, max_pad_ft):
    """Balloon every tight box outward at an equal rate until neighbours meet."""
    if not clusters:
        return
    max_pad = max(0.0, max_pad_ft)
    for i, ci in enumerate(clusters):
        pad = max_pad
        for j, cj in enumerate(clusters):
            if i == j:
                continue
            meet = ci.tight_box.chebyshev_gap(cj.tight_box) * 0.5
            pad = min(pad, meet)
        ci.region = ci.tight_box.expand(pad)


# ── DensityClusterer ───────────────────────────────────────────────────────────

class DensityResult:
    def __init__(self):
        self.cluster_by_key = {}
        self.cluster_count = 0
        self.summaries = []


def build_density(sources, link_ft, min_count=4):
    """Pockets packed tighter than their value text -> force-chain on every axis."""
    result = DensityResult()
    if not sources or link_ft <= 1e-9 or min_count < 2:
        return result

    pts = sorted(((s.source_key, s.anchor2d) for s in sources), key=lambda t: t[0])
    parent = list(range(len(pts)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    def union(a, b):
        a, b = find(a), find(b)
        if a != b:
            parent[max(a, b)] = min(a, b)

    link2 = link_ft * link_ft
    for i in range(len(pts)):
        for j in range(i + 1, len(pts)):
            d = pts[j][1] - pts[i][1]
            if d.x * d.x + d.y * d.y <= link2:
                union(i, j)

    members = {}
    for i in range(len(pts)):
        members.setdefault(find(i), []).append(i)

    seq = 0
    for root in sorted(members):
        group = members[root]
        if len(group) < min_count:
            continue
        cid = f"c{seq:03d}"
        seq += 1
        xs, ys = [], []
        for m in group:
            key, p = pts[m]
            result.cluster_by_key[key] = cid
            xs.append(p.x)
            ys.append(p.y)
        result.summaries.append(
            f"Dense area {cid}: {len(group)} clash(es) within "
            f"{max(xs)-min(xs):.1f}x{max(ys)-min(ys):.1f} ft")
    result.cluster_count = seq
    return result


# ── ClashRunGrouper ────────────────────────────────────────────────────────────

class RunInfo:
    def __init__(self, run_id, long_axis, cross_axis):
        self.run_id = run_id
        self.long_axis = long_axis
        self.cross_axis = cross_axis


class _RunCluster:
    __slots__ = ("cid", "members", "key_low", "axis", "min_x", "min_y", "max_x", "max_y")

    def __init__(self, cid, members, key_low, axis, box):
        self.cid = cid
        self.members = members
        self.key_low = key_low
        self.axis = axis
        self.min_x, self.min_y, self.max_x, self.max_y = box


def build_runs(sources, cross_tol_ft, gap_ft):
    """Agglomerative best-fit merging: the lowest worst-case off-line residual wins."""
    out = {}
    if not sources:
        return out, 0

    pts = sorted(((s.source_key, s.anchor2d) for s in sources), key=lambda t: t[0])
    prune = math.sqrt(gap_ft * gap_ft + 4.0 * cross_tol_ft * cross_tol_ft) + 1e-9

    clusters = [_RunCluster(i, [i], pts[i][0], Vec2(1, 0),
                            (pts[i][1].x, pts[i][1].y, pts[i][1].x, pts[i][1].y))
                for i in range(len(pts))]
    fit_cache = {}
    next_id = len(clusters)

    while len(clusters) > 1 and gap_ft > 1e-9 and cross_tol_ft > 1e-9:
        best_a = best_b = -1
        best_fit = None

        for a in range(len(clusters)):
            for b in range(a + 1, len(clusters)):
                ca, cb = clusters[a], clusters[b]
                if _box_gap(ca, cb) > prune:
                    continue
                fit = _get_fit(fit_cache, ca, cb, pts)
                if fit[1] > cross_tol_ft or fit[2] > gap_ft:
                    continue
                better = (best_a < 0
                          or fit[1] < best_fit[1] - 1e-12
                          or (abs(fit[1] - best_fit[1]) <= 1e-12
                              and _key_pair_less(ca, cb, clusters[best_a], clusters[best_b])))
                if better:
                    best_a, best_b, best_fit = a, b, fit

        if best_a < 0:
            break
        ca, cb = clusters[best_a], clusters[best_b]
        merged = _RunCluster(
            next_id, sorted(ca.members + cb.members),
            min(ca.key_low, cb.key_low), best_fit[0],
            (min(ca.min_x, cb.min_x), min(ca.min_y, cb.min_y),
             max(ca.max_x, cb.max_x), max(ca.max_y, cb.max_y)))
        next_id += 1
        clusters.pop(best_b)
        clusters.pop(best_a)
        clusters.append(merged)
        clusters.sort(key=lambda c: c.key_low)

    clusters.sort(key=lambda c: c.key_low)
    for seq, c in enumerate(clusters):
        info = RunInfo(f"run{seq:04d}", c.axis, c.axis.perp())
        for m in c.members:
            out[pts[m][0]] = info
    return out, len(clusters)


def _box_gap(a, b):
    dx = max(0.0, max(a.min_x - b.max_x, b.min_x - a.max_x))
    dy = max(0.0, max(a.min_y - b.max_y, b.min_y - a.max_y))
    return math.hypot(dx, dy)


def _key_pair_less(a1, b1, a2, b2):
    p1 = tuple(sorted((a1.key_low, b1.key_low)))
    p2 = tuple(sorted((a2.key_low, b2.key_low)))
    return p1 < p2


def _get_fit(cache, a, b, pts):
    key = (a.cid, b.cid) if a.cid <= b.cid else (b.cid, a.cid)
    hit = cache.get(key)
    if hit is None:
        hit = cache[key] = _fit_line(a, b, pts)
    return hit


def _fit_line(a, b, pts):
    members = a.members + b.members
    n = len(members)
    sx = sum(pts[m][1].x for m in members)
    sy = sum(pts[m][1].y for m in members)
    centroid = Vec2(sx / n, sy / n)
    axis = _principal_axis(members, pts, centroid)

    cross = axis.perp()
    max_perp = 0.0
    along = []
    for m in members:
        d = pts[m][1] - centroid
        max_perp = max(max_perp, abs(d.dot(cross)))
        along.append(d.dot(axis))
    along.sort()
    max_gap = max((along[i] - along[i - 1] for i in range(1, n)), default=0.0)
    return (axis, max_perp, max_gap)


def _principal_axis(members, pts, centroid):
    sxx = syy = sxy = 0.0
    for m in members:
        d = pts[m][1] - centroid
        sxx += d.x * d.x
        syy += d.y * d.y
        sxy += d.x * d.y
    if sxx + syy < 1e-12:
        return Vec2(1, 0)
    theta = 0.5 * math.atan2(2.0 * sxy, sxx - syy)
    axis = Vec2(math.cos(theta), math.sin(theta)).normalized()
    if axis.length < 1e-9:
        return Vec2(1, 0)
    if axis.x < -1e-9 or (abs(axis.x) <= 1e-9 and axis.y < 0):
        return axis * -1.0
    return axis
