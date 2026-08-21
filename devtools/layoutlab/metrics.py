"""Quality metrics — the vocabulary for "as few lines as possible carrying as
much information as possible".

None of these are currently in LayoutScorer; they are the candidate objective
terms. Measuring them first is the point.
"""
import math
from collections import defaultdict

from layout import axis_tag, line_level
from anatomy import is_moved


def _span(d):
    axis = d.axis_dir.normalized()
    a0 = d.source_point.dot(axis)
    a1 = d.target_point.dot(axis)
    return (min(a0, a1), max(a0, a1))


def compute(result, scene):
    dims = result.dims
    cfg = result.cfg
    scale = max(scene.scale, 1)
    m = {}

    m["penetrations"] = len(scene.penetrations)
    m["strings"] = len(dims)
    m["segments"] = sum(len(d.segments) for d in dims)
    m["chained_strings"] = sum(1 for d in dims if len(d.segments) > 1)
    m["moved_tags"] = sum(1 for d in dims for s in d.segments if is_moved(s.text_state))
    m["cramped_segments"] = sum(1 for d in dims for s in d.segments if s.is_cramped)

    # Ink: dimension line + witness length, reported in PAPER inches (what the eye sees).
    dim_ink = sum(d.anatomy.dim_line.length for d in dims if d.anatomy)
    wit_ink = sum(w.length for d in dims if d.anatomy for w in d.anatomy.witnesses)
    m["dim_line_ink_in"] = dim_ink / scale * 12.0
    m["witness_ink_in"] = wit_ink / scale * 12.0
    m["total_ink_in"] = m["dim_line_ink_in"] + m["witness_ink_in"]

    # Strings per located penetration — the headline "lines per unit of information".
    located = _located(dims, scene)
    m["located"] = len(located)
    m["unlocated"] = len(scene.penetrations) * 2 - sum(len(v) for v in located.values())
    m["strings_per_penetration"] = (len(dims) / len(scene.penetrations)
                                    if scene.penetrations else 0.0)

    # Redundancy: a penetration measured more than once on the same axis.
    redundant = 0
    for key, axes in located.items():
        for tag, count in axes.items():
            if count > 1:
                redundant += count - 1
    m["redundant_measures"] = redundant

    # Row depth: deepest stack of dimension lines in any corridor.
    m["max_row_depth"] = _max_row_depth(dims, cfg)

    # Repeated values: same-axis strings with equal values whose spans overlap —
    # visually the same number written twice.
    m["repeated_values"] = _repeated_values(dims, cfg)

    # Hard violations.
    scorer = getattr(result, "scorer", None)
    bad = []
    if scorer:
        for d in dims:
            h, _s = scorer.score(d, result.obstacles, dims)
            if h > 1e-6:
                bad.append((d.source_key, h, scorer.describe_hard_violations(
                    d, result.obstacles, dims)))
    m["hard_strings"] = len(bad)
    m["hard_total"] = result.hard
    m["soft_total"] = result.soft
    m["violations"] = sorted(bad, key=lambda t: -t[1])[:10]
    return m


def _located(dims, scene):
    """penetration key -> {axis tag: number of strings measuring it}."""
    out = defaultdict(lambda: defaultdict(int))
    for d in dims:
        tag = axis_tag(d)
        for key in _member_keys(d):
            out[key][tag] += 1
    return out


def _member_keys(d):
    """Recovers the penetration keys a string measures from its source key."""
    sk = d.source_key
    if sk.startswith("chain|"):
        parts = sk.split("|", 2)
        return parts[2].split("+") if len(parts) > 2 else []
    return [sk.rsplit("|", 1)[0]]


def _max_row_depth(dims, cfg):
    """Max number of dimension lines stacked over one along-axis position."""
    depth = 0
    for tag in ("x", "y"):
        group = [d for d in dims if axis_tag(d) == tag]
        if not group:
            continue
        # Sample along the axis at the text-height resolution.
        events = []
        for d in group:
            lo, hi = _span(d)
            events.append((lo, 1, line_level(d)))
            events.append((hi, -1, line_level(d)))
        events.sort()
        live = 0
        for _a, delta, _lvl in events:
            live += delta
            depth = max(depth, live)
    return depth


def _repeated_values(dims, cfg):
    tol = max(cfg.precision_ft, 1e-6)
    count = 0
    for tag in ("x", "y"):
        group = [d for d in dims if axis_tag(d) == tag]
        for i in range(len(group)):
            for j in range(i + 1, len(group)):
                a, b = group[i], group[j]
                sa, sb = _span(a), _span(b)
                if min(sa[1], sb[1]) - max(sa[0], sb[0]) <= 0:
                    continue          # spans do not overlap — not a visual repeat
                va = [s.length_ft for s in a.segments]
                vb = [s.length_ft for s in b.segments]
                if len(va) != len(vb):
                    continue
                if all(abs(x - y) <= tol for x, y in zip(va, vb)):
                    count += 1
    return count


def format_table(m):
    rows = [
        ("penetrations",            f"{m['penetrations']}"),
        ("dimension strings",       f"{m['strings']}"),
        ("  of which chained",      f"{m['chained_strings']}"),
        ("segments (values shown)", f"{m['segments']}"),
        ("strings / penetration",   f"{m['strings_per_penetration']:.2f}"),
        ("redundant measures",      f"{m['redundant_measures']}"),
        ("repeated value strings",  f"{m['repeated_values']}"),
        ("max row depth",           f"{m['max_row_depth']}"),
        ("moved tags",              f"{m['moved_tags']}"),
        ("cramped segments",        f"{m['cramped_segments']}"),
        ("dim-line ink (paper in)", f"{m['dim_line_ink_in']:.0f}"),
        ("witness ink (paper in)",  f"{m['witness_ink_in']:.0f}"),
        ("strings w/ hard fault",   f"{m['hard_strings']}"),
        ("hard total",              f"{m['hard_total']:.0f}"),
        ("soft total",              f"{m['soft_total']:.0f}"),
    ]
    w = max(len(r[0]) for r in rows)
    return "\n".join(f"  {k.ljust(w)}  {v}" for k, v in rows)
