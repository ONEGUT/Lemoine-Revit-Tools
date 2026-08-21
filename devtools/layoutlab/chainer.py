"""Port of DimensionChainer.cs — turns resolved clash->target pairs into planned
dimensions. This is where "chain vs. don't chain" is decided today, by a fixed
geometric rule, before the layout scorer ever sees anything.
"""
import math
from collections import Counter

from geom import Vec2
from model import PlannedDimension, PlannedSegment, POSITIVE

GLYPH_WIDTH_FACTOR = 0.6   # average glyph advance as a fraction of text height


class ResolvedItem:
    def __init__(self, source_key, source2d, axis, target2d, target_key,
                 target_type="Grid", run_id="", cluster_id="",
                 run_long_axis=None, force_chain=False):
        self.source_key = source_key
        self.source2d = source2d
        self.axis = axis
        self.target2d = target2d
        self.target_key = target_key
        self.target_type = target_type
        self.run_id = run_id
        self.cluster_id = cluster_id
        self.run_long_axis = run_long_axis or Vec2(1, 0)
        self.force_chain = force_chain


def axis_tag(axis):
    return "x" if abs(axis.x) >= abs(axis.y) else "y"


def arch_feet_inch(value_ft, denom=8):
    """Architectural feet-inches string, e.g. 12' - 4 3/8". Used for both glyph
    counting and the rendered value text."""
    neg = value_ft < 0
    v = abs(value_ft)
    total_frac = round(v * 12.0 * denom)
    feet, rem = divmod(total_frac, 12 * denom)
    inches, frac = divmod(rem, denom)
    s = f"{'-' if neg else ''}{feet}' - {inches}"
    if frac:
        num, den = frac, denom
        while num % 2 == 0 and den % 2 == 0:
            num //= 2
            den //= 2
        s += f" {num}/{den}"
    return s + '"'


def estimate_text_width(value_ft, text_height_model_ft, value_fmt=None):
    s = (value_fmt(value_ft) if value_fmt else "") or arch_feet_inch(value_ft)
    return max(3, len(s)) * text_height_model_ft * GLYPH_WIDTH_FACTOR


class ChainResult:
    def __init__(self):
        self.dims = []


def build_chains(items, cfg, value_fmt=None):
    """Group by (cluster, run, axis, [target when force-chained]); chain along the
    run, collapse to one representative across it."""
    result = ChainResult()
    if not items:
        return result

    groups = {}
    for it in items:
        key = (it.cluster_id, it.run_id, axis_tag(it.axis),
               it.target_key if it.force_chain else "")
        groups.setdefault(key, []).append(it)

    for key in sorted(groups):
        group = groups[key]
        axis = group[0].axis.normalized()
        perp = axis.perp()
        long_axis = group[0].run_long_axis.normalized()

        along = (group[0].force_chain
                 or abs(axis.dot(long_axis)) >= abs(axis.dot(long_axis.perp())))

        target = _choose_target(group)

        if along and len(group) >= 2:
            _emit_chain(group, axis, perp, target, cfg, result, value_fmt)
        else:
            rep = _representative(group, axis)
            _emit_single(rep, axis, target, cfg, result, value_fmt)

    return result


def _choose_target(group):
    """Majority-vote target key; ties lexicographic."""
    counts = Counter(it.target_key for it in group)
    best = max(sorted(counts), key=lambda k: counts[k])
    winner = min((it for it in group if it.target_key == best), key=lambda it: it.source_key)
    return (winner.target2d, winner.target_key)


def _representative(group, measure_axis):
    coords = sorted(it.source2d.dot(measure_axis) for it in group)
    median = coords[len(coords) // 2]
    return min(group, key=lambda it: (abs(it.source2d.dot(measure_axis) - median), it.source_key))


def _emit_single(it, axis, target, cfg, result, value_fmt):
    tgt_pt, tgt_key = target
    src_a = it.source2d.dot(axis)
    tgt_a = tgt_pt.dot(axis)
    length = abs(tgt_a - src_a)

    # Re-anchor the shared target onto the representative's own axis line so the band
    # is orthogonal (the majority target came from a different member).
    target_point = it.source2d + axis * (tgt_a - src_a)

    key = f"{it.source_key}|{axis_tag(axis)}"
    anchors = [it.source2d, target_point]
    if anchors[0].dot(axis) > anchors[1].dot(axis):
        anchors.reverse()

    d = PlannedDimension()
    d.source_key = key
    d.target_key = f"{tgt_key}|{axis_tag(axis)}"
    d.target_type = it.target_type
    d.cluster_id = it.cluster_id
    d.source_point = it.source2d
    d.target_point = target_point
    d.axis_dir = axis
    d.side = POSITIVE
    d.offset_ft = cfg.first_offset_ft
    d.ref_anchors = anchors
    d.segments = [PlannedSegment(
        length, estimate_text_width(length, cfg.text_height_ft, value_fmt),
        (value_fmt(length) if value_fmt else arch_feet_inch(length)))]
    result.dims.append(d)


def _emit_chain(run, axis, perp, target, cfg, result, value_fmt):
    tgt_pt, tgt_key = target
    tgt_a = tgt_pt.dot(axis)
    tgt_perp = tgt_pt.dot(perp)
    base_perp = sum(it.source2d.dot(perp) for it in run) / len(run)

    refs = [(it.source2d.dot(axis), it.source2d.dot(perp)) for it in run]
    refs.append((tgt_a, tgt_perp))
    refs.sort(key=lambda t: t[0])

    deduped = []
    for ra in refs:
        if not deduped or abs(ra[0] - deduped[-1][0]) > cfg.precision_ft:
            deduped.append(ra)

    if len(deduped) < 2:
        _emit_single(run[0], axis, target, cfg, result, value_fmt)
        return

    min_a, max_a = deduped[0][0], deduped[-1][0]
    sp = axis * min_a + perp * base_perp
    tp = axis * max_a + perp * base_perp

    segments = []
    for k in range(1, len(deduped)):
        length = deduped[k][0] - deduped[k - 1][0]
        segments.append(PlannedSegment(
            length, estimate_text_width(length, cfg.text_height_ft, value_fmt),
            (value_fmt(length) if value_fmt else arch_feet_inch(length))))

    key = "chain|" + axis_tag(axis) + "|" + "+".join(sorted(it.source_key for it in run))

    d = PlannedDimension()
    d.source_key = key
    d.target_key = f"{tgt_key}|chain|{axis_tag(axis)}"
    d.target_type = run[0].target_type
    d.cluster_id = run[0].cluster_id
    d.source_point = sp
    d.target_point = tp
    d.axis_dir = axis
    d.side = POSITIVE
    d.offset_ft = cfg.first_offset_ft
    d.ref_anchors = [axis * a + perp * p for a, p in deduped]
    d.segments = segments
    result.dims.append(d)
