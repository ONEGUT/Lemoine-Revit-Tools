"""Random penetration-set generator — synthetic but plausible coordination plans.

A scene is a plan view: a structural grid, a slab outline, and a set of
penetrations (sleeves through the slab). Each penetration becomes one clash
source; its cross-section box becomes a layout obstacle, exactly as the real
pipeline treats a clash marker (ClashEngine inherits the marker size from the
clashing element's cross-section).

Every scene is reproducible from its seed.
"""
import math
import random

from geom import Vec2, Box2


class Penetration:
    __slots__ = ("key", "centre", "w", "h", "kind", "round_")

    def __init__(self, key, centre, w, h, kind, round_=False):
        self.key = key
        self.centre = centre
        self.w = w
        self.h = h
        self.kind = kind
        self.round_ = round_

    @property
    def anchor2d(self):
        return self.centre

    @property
    def source_key(self):
        return self.key

    @property
    def box(self):
        return Box2.from_center(self.centre, self.w * 0.5, self.h * 0.5)


class GridLine:
    __slots__ = ("label", "coord", "vertical")

    def __init__(self, label, coord, vertical):
        self.label = label
        self.coord = coord        # x for vertical lines, y for horizontal
        self.vertical = vertical

    @property
    def dir2d(self):
        return Vec2(0, 1) if self.vertical else Vec2(1, 0)

    def mid2d(self, extent):
        if self.vertical:
            return Vec2(self.coord, (extent.min_y + extent.max_y) * 0.5)
        return Vec2((extent.min_x + extent.max_x) * 0.5, self.coord)


class Scene:
    def __init__(self, name, seed, scale, extent, grids, penetrations, pattern=""):
        self.name = name
        self.seed = seed
        self.scale = scale
        self.extent = extent            # Box2, the view crop in model feet
        self.grids = grids
        self.penetrations = penetrations
        self.pattern = pattern

    def summary(self):
        return (f"{self.name}  seed={self.seed}  1:{self.scale}  "
                f"{len(self.penetrations)} penetrations  "
                f"{self.extent.width:.0f}x{self.extent.height:.0f} ft")


# ── Penetration size catalogue (model feet) ────────────────────────────────────
# Diameters/sizes drawn from ordinary MEP: small conduit up to a big duct.
_PIPE_DIAS   = [0.5, 0.67, 0.83, 1.0, 1.25, 1.5, 2.0]        # 6" - 24"
_CONDUIT_DIA = [0.25, 0.33, 0.42]                             # 3" - 5"
_DUCTS       = [(1.0, 0.67), (1.5, 1.0), (2.0, 1.0), (2.5, 1.0), (3.0, 1.33)]


def _pipe(rng, key, centre):
    d = rng.choice(_PIPE_DIAS)
    return Penetration(key, centre, d, d, "pipe", round_=True)


def _conduit(rng, key, centre):
    d = rng.choice(_CONDUIT_DIA)
    return Penetration(key, centre, d, d, "conduit", round_=True)


def _duct(rng, key, centre):
    w, h = rng.choice(_DUCTS)
    if rng.random() < 0.5:
        w, h = h, w
    return Penetration(key, centre, w, h, "duct")


def _any(rng, key, centre):
    r = rng.random()
    if r < 0.55:
        return _pipe(rng, key, centre)
    if r < 0.8:
        return _conduit(rng, key, centre)
    return _duct(rng, key, centre)


# ── Pattern builders — each returns a list of Penetration ──────────────────────

def _rack(rng, origin, direction, count, spacing, jitter, keys):
    """A bank of parallel services crossing a wall: evenly spaced along one line."""
    out = []
    perp = direction.perp()
    for i in range(count):
        off = i * spacing + rng.uniform(-jitter, jitter)
        cross = rng.uniform(-jitter, jitter)
        out.append(_any(rng, next(keys), origin + direction * off + perp * cross))
    return out


def _bank(rng, origin, rows, cols, dx, dy, keys):
    """A shaft / riser block: rows x cols of penetrations."""
    out = []
    for r in range(rows):
        for c in range(cols):
            p = origin + Vec2(c * dx, r * dy)
            out.append(_any(rng, next(keys), p))
    return out


def _scatter(rng, extent, count, keys, margin=8.0):
    out = []
    for _ in range(count):
        p = Vec2(rng.uniform(extent.min_x + margin, extent.max_x - margin),
                 rng.uniform(extent.min_y + margin, extent.max_y - margin))
        out.append(_any(rng, next(keys), p))
    return out


def _keygen():
    i = 0
    while True:
        i += 1
        yield f"p{i:03d}"


# ── Scene generation ───────────────────────────────────────────────────────────

PATTERNS = ("rack", "bank", "scatter", "dense", "pair", "mixed")


def generate(pattern="mixed", seed=None, scale=48, bays_x=4, bays_y=3, bay=25.0):
    """Builds one scene. `bay` is the structural bay size in feet."""
    if seed is None:
        seed = random.randrange(1, 10 ** 6)
    rng = random.Random(seed)
    keys = _keygen()

    # Structural grid: numbered lines run vertically (constant x), lettered run
    # horizontally (constant y) — the usual convention.
    grids = []
    for i in range(bays_x + 1):
        grids.append(GridLine(str(i + 1), i * bay, vertical=True))
    for j in range(bays_y + 1):
        grids.append(GridLine(chr(ord("A") + j), j * bay, vertical=False))

    pad = bay * 0.45
    extent = Box2(-pad, -pad, bays_x * bay + pad, bays_y * bay + pad)

    pens = []
    if pattern == "rack":
        # One long rack crossing the middle bay, parallel to the numbered grids.
        origin = Vec2(bay * 0.8, bay * 1.5 + rng.uniform(-3, 3))
        pens = _rack(rng, origin, Vec2(1, 0), rng.randint(5, 9),
                     rng.uniform(2.2, 4.0), 0.35, keys)

    elif pattern == "bank":
        origin = Vec2(bay * 1.2, bay * 1.2)
        pens = _bank(rng, origin, rng.randint(3, 4), rng.randint(3, 5),
                     rng.uniform(2.0, 3.0), rng.uniform(2.0, 3.0), keys)

    elif pattern == "scatter":
        pens = _scatter(rng, extent, rng.randint(6, 12), keys)

    elif pattern == "dense":
        # A riser pocket: many services through one small opening zone.
        origin = Vec2(bay * 1.6, bay * 1.4)
        for _ in range(rng.randint(12, 20)):
            p = origin + Vec2(rng.uniform(-3.0, 3.0), rng.uniform(-2.5, 2.5))
            pens.append(_any(rng, next(keys), p))

    elif pattern == "pair":
        # Two services mid-bay — the "equidistant between two grids" case.
        cx = bay * 1.5
        cy = bay * 1.5
        pens = [_pipe(rng, next(keys), Vec2(cx - 1.2, cy)),
                _pipe(rng, next(keys), Vec2(cx + 1.2, cy + 0.4))]

    else:  # mixed — the realistic case: several unrelated groups in one view
        pens += _rack(rng, Vec2(bay * 0.6, bay * 0.7), Vec2(1, 0),
                      rng.randint(4, 7), rng.uniform(2.5, 4.0), 0.3, keys)
        pens += _rack(rng, Vec2(bay * 2.6, bay * 0.5), Vec2(0, 1),
                      rng.randint(3, 6), rng.uniform(2.5, 4.0), 0.3, keys)
        pens += _bank(rng, Vec2(bay * 1.35, bay * 1.9), rng.randint(2, 3),
                      rng.randint(3, 4), rng.uniform(2.2, 3.0), rng.uniform(2.2, 3.0), keys)
        pens += _scatter(rng, extent, rng.randint(3, 6), keys)

    # Never let two penetrations sit exactly on top of each other.
    pens = _deoverlap(pens)

    return Scene(f"{pattern}-{seed}", seed, scale, extent, grids, pens, pattern)


def _deoverlap(pens, min_gap=0.15):
    kept = []
    for p in pens:
        clash = False
        for q in kept:
            need = (max(p.w, p.h) + max(q.w, q.h)) * 0.5 + min_gap
            if (p.centre - q.centre).length < need:
                clash = True
                break
        if not clash:
            kept.append(p)
    return kept
