"""Port of Core/GreedyLayoutEngine.cs and Core/RowPlanner.cs.

Cluster-by-cluster greedy placement, worst-first repair passes, then shared-row
snapping. Searches side x offset x tag-column direction; every other decision
(chaining, targets, grouping, inline-vs-moved) is fixed upstream — see
plan-dimension-placement-quality.md.
"""
import math
import time

from geom import Vec2
from model import POSITIVE, NEGATIVE, INLINE, STAGGERED, FLIPPED
from anatomy import recompute_bounds, is_moved

BOTH_DIRS = (1, -1)
FORWARD_DIR = (1,)


def _flip(side):
    return NEGATIVE if side == POSITIVE else POSITIVE


class GreedyLayoutEngine:
    def __init__(self, cfg, scorer):
        self.cfg = cfg
        self.scorer = scorer

    def arrange(self, dims, static_obstacles):
        if not dims:
            return
        cfg = self.cfg
        t0 = time.perf_counter()

        def elapsed_ms():
            return (time.perf_counter() - t0) * 1000.0

        # One group per cluster, ordered by cluster id.
        keys = sorted({(d.cluster_id or "") for d in dims})
        groups = [[d for d in dims if (d.cluster_id or "") == k] for k in keys]

        settled = []
        for group in groups:
            sort_for_placement(group)

            prev_soft = math.inf
            for _ in range(cfg.max_iterations):
                placed = list(settled)
                for d in group:
                    self._place_one(d, static_obstacles, placed)
                    placed.append(d)

                hard, soft = self._score_group(group, static_obstacles, placed)
                if hard <= 1e-6 and abs(prev_soft - soft) <= cfg.plateau_epsilon:
                    break
                prev_soft = soft
                if elapsed_ms() > cfg.time_cap_ms:
                    break

            settled.extend(group)
            if elapsed_ms() > cfg.time_cap_ms:
                break

        self._repair(dims, static_obstacles, elapsed_ms)

        if cfg.align_shared_rows:
            snap_shared_rows(dims, cfg, self.scorer, static_obstacles)

    def _score_group(self, group, obstacles, alld):
        hard = soft = 0.0
        for d in group:
            h, s = self.scorer.score(d, obstacles, alld)
            hard += h
            soft += s
        return hard, soft

    def _repair(self, dims, obstacles, elapsed_ms):
        cfg = self.cfg
        for _pass in range(cfg.max_repair_passes):
            if elapsed_ms() > cfg.time_cap_ms:
                return

            scored = []
            for d in dims:
                h, s = self.scorer.score(d, obstacles, dims)
                if h > 1e-6 or s > 1e-6:
                    scored.append((h, s, d))
            ranked = [t[2] for t in sorted(scored, key=lambda t: (-t[0], -t[1], t[2].source_key))]

            changed = False
            for d in ranked:
                if elapsed_ms() > cfg.time_cap_ms:
                    return

                orig_side, orig_offset, orig_dir = d.side, d.offset_ft, d.tag_column_dir
                best = self.scorer.score(d, obstacles, dims)
                best_side, best_offset, best_dir = orig_side, orig_offset, orig_dir

                for side in (orig_side, _flip(orig_side)):
                    for step in range(cfg.max_offset_steps):
                        offset = cfg.first_offset_ft + step * cfg.string_spacing_ft
                        d.side = side
                        d.offset_ft = offset
                        self._resolve_segments(d)

                        for cdir in (BOTH_DIRS if _has_moved_tags(d) else FORWARD_DIR):
                            if (side == orig_side and abs(offset - orig_offset) <= 1e-9
                                    and cdir == orig_dir):
                                continue
                            d.tag_column_dir = cdir
                            recompute_bounds(d, cfg)
                            s = self.scorer.score(d, obstacles, dims)
                            better = (s[0] < best[0] - 1e-9
                                      or (abs(s[0] - best[0]) <= 1e-9 and s[1] < best[1] - 1e-9))
                            if better:
                                best, best_side, best_offset, best_dir = s, side, offset, cdir

                d.side, d.offset_ft, d.tag_column_dir = best_side, best_offset, best_dir
                self._resolve_segments(d)
                recompute_bounds(d, cfg)
                if (best_side != orig_side or abs(best_offset - orig_offset) > 1e-9
                        or best_dir != orig_dir):
                    changed = True

            if not changed:
                return

    def _place_one(self, d, obstacles, placed):
        cfg = self.cfg
        original = d.side
        best_side = original
        best_dir = 1
        best_offset = cfg.first_offset_ft
        best_hard = math.inf

        for step in range(cfg.max_offset_steps):
            offset = cfg.first_offset_ft + step * cfg.string_spacing_ft

            for side in (original, _flip(original)):
                d.side = side
                d.offset_ft = offset
                self._resolve_segments(d)

                for cdir in (BOTH_DIRS if _has_moved_tags(d) else FORWARD_DIR):
                    d.tag_column_dir = cdir
                    recompute_bounds(d, cfg)
                    hard = self.scorer.score(d, obstacles, placed)[0]

                    better = hard < best_hard - 1e-9
                    tie = abs(hard - best_hard) <= 1e-9
                    closer = offset < best_offset - 1e-9
                    same_offset = abs(offset - best_offset) <= 1e-9
                    prefers_original = side == original and best_side != original
                    if better or (tie and (closer or (same_offset and prefers_original))):
                        best_hard, best_offset, best_side, best_dir = hard, offset, side, cdir

            if best_hard <= 1e-6:
                break

        d.side, d.offset_ft, d.tag_column_dir = best_side, best_offset, best_dir
        self._resolve_segments(d)
        recompute_bounds(d, cfg)

    def _resolve_segments(self, d):
        """FIXED RULE: any tag wider than its segment is pulled into the tag column."""
        moved_state = STAGGERED if d.side == POSITIVE else FLIPPED
        for seg in d.segments:
            seg.text_state = moved_state if seg.is_cramped else INLINE


def _has_moved_tags(d):
    return any(is_moved(s.text_state) for s in d.segments)


def moved_tag_count(dims):
    return sum(1 for d in dims for s in d.segments if is_moved(s.text_state))


# ── RowPlanner ─────────────────────────────────────────────────────────────────

def axis_tag(d):
    return "x" if abs(d.axis_dir.x) >= abs(d.axis_dir.y) else "y"


def line_level(d):
    perp = d.axis_dir.normalized().perp()
    sign = 1.0 if d.side == POSITIVE else -1.0
    return d.source_point.dot(perp) + sign * d.offset_ft


def _span(d):
    axis = d.axis_dir.normalized()
    a0 = d.source_point.dot(axis)
    a1 = d.target_point.dot(axis)
    return (min(a0, a1), max(a0, a1))


def sort_for_placement(dims):
    """By axis, then corridor, then SHORTEST span first (ASME: shortest nearest the object)."""
    if not dims or len(dims) <= 1:
        return
    info = {}
    tags = sorted({axis_tag(d) for d in dims})
    for tag in tags:
        grp = [d for d in dims if axis_tag(d) == tag]
        ordered = sorted(
            ((d,) + _span(d) for d in grp),
            key=lambda t: (t[1], t[0].source_key))

        corridor_start = None
        corridor_end = -math.inf
        for d, min_a, max_a in ordered:
            if corridor_start is None or min_a > corridor_end:
                corridor_start = min_a
                corridor_end = max_a
            elif max_a > corridor_end:
                corridor_end = max_a
            info[id(d)] = (tag, min_a, max_a - min_a, corridor_start)

    dims.sort(key=lambda d: (info[id(d)][0], info[id(d)][3], info[id(d)][2],
                             info[id(d)][1], d.source_key))


def snap_shared_rows(dims, cfg, scorer, obstacles):
    """NCS 'align dimensions in one line' — post-hoc pairwise pull onto a shared level."""
    if not dims or len(dims) < 2:
        return
    level_tol = cfg.string_spacing_ft * 0.75
    max_gap = cfg.string_spacing_ft * 4.0
    min_offset = cfg.first_offset_ft * 0.5

    ordered = sorted(dims, key=lambda d: d.source_key)
    for i in range(len(ordered)):
        for j in range(i + 1, len(ordered)):
            a, b = ordered[i], ordered[j]
            if axis_tag(a) != axis_tag(b):
                continue
            la, lb = line_level(a), line_level(b)
            d_level = abs(la - lb)
            if d_level <= 1e-9 or d_level > level_tol:
                continue
            min_a, max_a = _span(a)
            min_b, max_b = _span(b)
            gap = max(min_b - max_a, min_a - max_b)
            if gap < 0 or gap > max_gap:
                continue
            if not _try_snap(b, la, min_offset, scorer, obstacles, dims, cfg):
                _try_snap(a, lb, min_offset, scorer, obstacles, dims, cfg)


def _try_snap(d, target_level, min_offset, scorer, obstacles, dims, cfg):
    perp = d.axis_dir.normalized().perp()
    sign = 1.0 if d.side == POSITIVE else -1.0
    new_offset = sign * (target_level - d.source_point.dot(perp))
    if new_offset < min_offset:
        return False

    before = scorer.score(d, obstacles, dims)[0]
    saved = d.offset_ft

    d.offset_ft = new_offset
    recompute_bounds(d, cfg)
    after = scorer.score(d, obstacles, dims)[0]

    if after > before + 1e-9:
        d.offset_ft = saved
        recompute_bounds(d, cfg)
        return False
    return True
