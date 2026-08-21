"""Port of Core/DimAnatomy.cs, Core/TagColumnPlanner.cs and Core/DimGeometry.cs.

The drawn anatomy of a dimension at its current side/offset/text states: the
dimension line, one witness per reference, every value-text box, and a chord
approximation of each moved tag's arc leader.
"""
import math

from geom import Vec2, Box2, Seg2
from model import INLINE, FLIPPED, STAGGERED, LEADER_OUT, POSITIVE


def is_moved(state):
    return state in (LEADER_OUT, STAGGERED, FLIPPED)


# ── DimGeometry ────────────────────────────────────────────────────────────────

def segment_boundaries(d, axis):
    """Axial coordinates of the segment boundaries (len == segments + 1)."""
    n = len(d.segments)
    if d.ref_anchors and len(d.ref_anchors) == n + 1:
        return sorted(r.dot(axis) for r in d.ref_anchors)
    bounds = [min(d.source_point.dot(axis), d.target_point.dot(axis))]
    for seg in d.segments:
        bounds.append(bounds[-1] + seg.length_ft)
    return bounds


def recompute_bounds(d, cfg):
    plan_columns(d, cfg)
    d.anatomy = build_anatomy(d, cfg)
    d.paper_bounds = d.anatomy.bounds


def axial_length(d):
    axis = d.axis_dir.normalized()
    return abs((d.target_point - d.source_point).dot(axis))


# ── TagColumnPlanner ───────────────────────────────────────────────────────────

def plan_columns(d, cfg):
    for seg in d.segments:
        seg.tag_pos = None

    th = cfg.text_height_ft
    if th <= 0 or not d.segments:
        return

    axis = d.axis_dir.normalized()
    perp = axis.perp()
    sign = 1.0 if d.side == POSITIVE else -1.0
    col_dir = -1.0 if d.tag_column_dir < 0 else 1.0
    line_level = d.source_point.dot(perp) + sign * d.offset_ft

    bounds = segment_boundaries(d, axis)

    run = []
    for k in range(len(d.segments) + 1):
        if k < len(d.segments) and is_moved(d.segments[k].text_state):
            run.append((d.segments[k], (bounds[k] + bounds[k + 1]) * 0.5))
            continue
        if run:
            _plan_column(run, axis, perp, sign, col_dir, line_level, th, cfg)
            run = []


def _plan_column(run, axis, perp, sign, col_dir, line_level, th, cfg):
    # Front line just past the group's furthest dimension edge in the column direction.
    edge = -math.inf if col_dir > 0 else math.inf
    for seg, centre_a in run:
        e = centre_a + col_dir * seg.length_ft * 0.5
        if (e > edge) if col_dir > 0 else (e < edge):
            edge = e
    front_line = edge + col_dir * cfg.tag_column_along_heights * th

    ordered = sorted(run, key=lambda t: t[1], reverse=(col_dir > 0))

    level = cfg.tag_column_base_heights * th
    if sign < 0:
        level += th          # below-line tags need the extra baseline push
    step = cfg.tag_column_step_heights * th
    for seg, _centre_a in ordered:
        half_along = max(seg.text_width_ft, th) * 0.5
        centre_a = front_line + col_dir * half_along
        seg.tag_pos = axis * centre_a + perp * (line_level + sign * level)
        level += step


# ── DimAnatomy ─────────────────────────────────────────────────────────────────

class DimAnatomy:
    __slots__ = ("dim_line", "line_band", "witnesses", "text_boxes", "leaders", "bounds")

    def __init__(self):
        self.witnesses = []
        self.text_boxes = []
        self.leaders = []


def build_anatomy(d, cfg):
    an = DimAnatomy()
    axis = d.axis_dir.normalized()
    perp = axis.perp()
    sign = 1.0 if d.side == POSITIVE else -1.0
    th = max(cfg.text_height_ft, 1e-6)
    off_vec = perp * (sign * d.offset_ft)
    line_level = d.source_point.dot(perp) + sign * d.offset_ft

    a = d.source_point + off_vec
    b = d.target_point + off_vec
    an.dim_line = Seg2(a, b)

    band = th * 0.75
    tiny = th * 0.1
    pad_x = abs(perp.x) * band + abs(axis.x) * tiny
    pad_y = abs(perp.y) * band + abs(axis.y) * tiny
    line_box = Box2.from_points(a, b)
    an.line_band = Box2(line_box.min_x - pad_x, line_box.min_y - pad_y,
                        line_box.max_x + pad_x, line_box.max_y + pad_y)
    bounds = an.line_band

    for r in (d.ref_anchors or []):
        anchor_perp = r.dot(perp)
        wdir = 1.0 if line_level >= anchor_perp else -1.0
        w = Seg2(r + perp * (wdir * cfg.witness_gap_ft),
                 axis * r.dot(axis) + perp * (line_level + wdir * cfg.witness_overshoot_ft))
        an.witnesses.append(w)
        bounds = bounds.union(w.bounds)

    seg_bounds = segment_boundaries(d, axis)
    for k, seg in enumerate(d.segments):
        centre_a = (seg_bounds[k] + seg_bounds[k + 1]) * 0.5
        half_along = max(seg.text_width_ft, th) * 0.5
        half_perp = th * 0.55

        if is_moved(seg.text_state) and seg.tag_pos is not None:
            tag = seg.tag_pos
            box = _axis_box(tag, axis, perp, half_along, half_perp)
            an.text_boxes.append(box)
            bounds = bounds.union(box)

            col_dir = -1.0 if d.tag_column_dir < 0 else 1.0
            anchor = axis * centre_a + perp * line_level
            front = tag - axis * (half_along * col_dir)
            leader = Seg2(anchor, front)
            an.leaders.append(leader)
            bounds = bounds.union(leader.bounds)
        else:
            centre = axis * centre_a + perp * (line_level + th * 0.7)
            box = _axis_box(centre, axis, perp, half_along, half_perp)
            an.text_boxes.append(box)
            bounds = bounds.union(box)

    an.bounds = bounds
    return an


def _axis_box(centre, axis, perp, half_along, half_perp):
    half_x = abs(axis.x) * half_along + abs(perp.x) * half_perp
    half_y = abs(axis.y) * half_along + abs(perp.y) * half_perp
    return Box2.from_center(centre, half_x, half_y)
