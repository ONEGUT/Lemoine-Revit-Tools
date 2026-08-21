"""Port of Core/LayoutScorer.cs — hard/soft scoring of one placement.

HARD must reach zero: text or line band overlapping anything, a dimension line
crossing another dimension line or a witness, a witness slicing value text,
off-crop. SOFT is then minimised.
"""
import math

from geom import Vec2, Box2, Seg2
from anatomy import build_anatomy, is_moved
from model import LEADER_OUT, STAGGERED, FLIPPED


class ScoreDetail:
    def __init__(self):
        self.band_vs_obstacles = 0.0
        self.text_vs_obstacles = 0.0
        self.band_vs_bands = 0.0
        self.text_vs_text_or_band = 0.0
        self.line_crosses_line = 0
        self.line_crosses_witness = 0
        self.witness_through_text = 0
        self.leader_crossings = 0
        self.leader_line_crossings = 0
        self.off_crop = False
        self.cramped_penalty = 0.0
        self.leader_slack = 0.0
        self.spacing_deviation = 0.0
        self.stagger_penalty = 0.0
        self.region_overflow = 0.0

    def __str__(self):
        parts = []
        def add(label, v):
            if v > 1e-9:
                parts.append(f"{label} {v:.2f}")
        add("band x obstacle", self.band_vs_obstacles)
        add("text x obstacle", self.text_vs_obstacles)
        add("band x band", self.band_vs_bands)
        add("text x text/band", self.text_vs_text_or_band)
        if self.line_crosses_line:     parts.append(f"line x line x{self.line_crosses_line}")
        if self.line_crosses_witness:  parts.append(f"line x witness x{self.line_crosses_witness}")
        if self.witness_through_text:  parts.append(f"witness x text x{self.witness_through_text}")
        if self.leader_crossings:      parts.append(f"leader x leader x{self.leader_crossings}")
        if self.leader_line_crossings: parts.append(f"leader x line x{self.leader_line_crossings}")
        if self.off_crop:              parts.append("off-crop")
        add("cramped", self.cramped_penalty)
        add("leader-slack", self.leader_slack)
        add("spacing-dev", self.spacing_deviation)
        add("stagger", self.stagger_penalty)
        add("region-overflow", self.region_overflow)
        return "; ".join(parts)


class LayoutScorer:
    def __init__(self, cfg, crop=None):
        self.cfg = cfg
        self.crop = crop

    def score(self, d, obstacles, placed, detail=None):
        cfg = self.cfg
        hard = 0.0
        soft = 0.0

        an = d.anatomy
        if an is None:
            an = d.anatomy = build_anatomy(d, cfg)

        # ── HARD: static obstacles ──
        for ob in obstacles:
            if not d.paper_bounds.intersects(ob):
                continue
            a1 = an.line_band.overlap_area(ob)
            hard += a1 * cfg.overlap_weight
            if detail: detail.band_vs_obstacles += a1
            for tb in an.text_boxes:
                a2 = tb.overlap_area(ob)
                hard += a2 * cfg.overlap_weight
                if detail: detail.text_vs_obstacles += a2

        # ── vs other dimensions ──
        for p in placed:
            if p is d:
                continue
            pa = p.anatomy
            if pa is None:
                continue
            if not d.paper_bounds.intersects(p.paper_bounds):
                continue

            bb = an.line_band.overlap_area(pa.line_band)
            hard += bb * cfg.overlap_weight
            if detail: detail.band_vs_bands += bb

            if an.dim_line.crosses(pa.dim_line):
                hard += cfg.crossing_weight
                if detail: detail.line_crosses_line += 1
            for w in pa.witnesses:
                if an.dim_line.crosses(w):
                    hard += cfg.crossing_weight
                    if detail: detail.line_crosses_witness += 1
            for w in an.witnesses:
                if pa.dim_line.crosses(w):
                    hard += cfg.crossing_weight
                    if detail: detail.line_crosses_witness += 1

            for tb in an.text_boxes:
                tband = tb.overlap_area(pa.line_band)
                hard += tband * cfg.overlap_weight
                if detail: detail.text_vs_text_or_band += tband
                for otb in pa.text_boxes:
                    tt = tb.overlap_area(otb)
                    hard += tt * cfg.overlap_weight
                    if detail: detail.text_vs_text_or_band += tt
                for w in pa.witnesses:
                    if _seg_intersects_box(w, tb):
                        hard += cfg.witness_cross_weight
                        if detail: detail.witness_through_text += 1
            for otb in pa.text_boxes:
                bt = otb.overlap_area(an.line_band)
                hard += bt * cfg.overlap_weight
                if detail: detail.text_vs_text_or_band += bt
                for w in an.witnesses:
                    if _seg_intersects_box(w, otb):
                        hard += cfg.witness_cross_weight
                        if detail: detail.witness_through_text += 1

            for l in an.leaders:
                for ol in pa.leaders:
                    if l.crosses(ol):
                        soft += cfg.leader_cross_weight
                        if detail: detail.leader_crossings += 1
                if l.crosses(pa.dim_line):
                    soft += cfg.leader_line_cross_weight
                    if detail: detail.leader_line_crossings += 1
                for w in pa.witnesses:
                    if l.crosses(w):
                        soft += cfg.leader_line_cross_weight
                        if detail: detail.leader_line_crossings += 1
            for ol in pa.leaders:
                if ol.crosses(an.dim_line):
                    soft += cfg.leader_line_cross_weight
                    if detail: detail.leader_line_crossings += 1
                for w in an.witnesses:
                    if ol.crosses(w):
                        soft += cfg.leader_line_cross_weight
                        if detail: detail.leader_line_crossings += 1

            if cfg.stagger_stacked_text:
                sp = self._stagger_penalty(an, pa, d, p)
                soft += sp
                if detail: detail.stagger_penalty += sp

        # ── HARD: off-crop ──
        if self.crop is not None and not self.crop.contains_box(d.paper_bounds):
            hard += cfg.off_crop_weight
            if detail: detail.off_crop = True

        # ── SOFT: cluster working region ──
        if d.has_region:
            outside = max(0.0, an.line_band.area - an.line_band.overlap_area(d.region))
            for tb in an.text_boxes:
                outside += max(0.0, tb.area - tb.overlap_area(d.region))
            rp = outside * cfg.region_weight
            soft += rp
            if detail: detail.region_overflow += rp

        # ── SOFT: cramped / leadered text ──
        for seg in d.segments:
            if seg.text_state == LEADER_OUT:
                soft += cfg.leader_weight
                if detail: detail.cramped_penalty += cfg.leader_weight
                continue
            if seg.is_cramped:
                overflow = seg.text_width_ft - seg.length_ft
                moved = seg.text_state in (STAGGERED, FLIPPED)
                factor = 0.5 if moved else 1.0
                cp = overflow * cfg.cramped_weight * factor
                soft += cp
                if detail: detail.cramped_penalty += cp

        # ── SOFT: leader slack ──
        min_leader = cfg.text_height_ft * 2.0
        for l in an.leaders:
            slack = max(0.0, l.length - min_leader) * cfg.leader_slack_weight
            soft += slack
            if detail: detail.leader_slack += slack

        # ── SOFT: uneven spacing ──
        dev = abs(d.offset_ft - self._snap_to_grid(d.offset_ft))
        soft += dev * cfg.uneven_spacing_weight
        if detail: detail.spacing_deviation += dev * cfg.uneven_spacing_weight

        return (hard, soft)

    def score_all(self, placed, obstacles):
        hard = soft = 0.0
        for d in placed:
            h, s = self.score(d, obstacles, placed)
            hard += h
            soft += s
        return (hard, soft)

    def describe_hard_violations(self, d, obstacles, others):
        reasons = []
        an = d.anatomy
        if an is None:
            return ""
        for ob in obstacles:
            if not d.paper_bounds.intersects(ob):
                continue
            if an.line_band.overlap_area(ob) > 0:
                reasons.append("line over existing annotation")
                break
        for p in others:
            if p is d or p.anatomy is None:
                continue
            pa = p.anatomy
            if not d.paper_bounds.intersects(p.paper_bounds):
                continue
            if an.line_band.overlap_area(pa.line_band) > 0 and "string on string" not in reasons:
                reasons.append("string on string")
            if an.dim_line.crosses(pa.dim_line) and "dimension lines cross" not in reasons:
                reasons.append("dimension lines cross")
            for w in pa.witnesses:
                if an.dim_line.crosses(w):
                    if "line crosses a witness" not in reasons:
                        reasons.append("line crosses a witness")
                    break
        return ", ".join(reasons)

    def _stagger_penalty(self, an, pa, d, p):
        cfg = self.cfg
        da = d.axis_dir.normalized()
        pb = p.axis_dir.normalized()
        if abs(da.dot(pb)) < 0.9:
            return 0.0
        x_axis = abs(da.x) >= abs(da.y)

        lo = cfg.string_spacing_ft * 0.25
        hi = cfg.string_spacing_ft * 1.75
        pen = 0.0

        for tb in an.text_boxes:
            for ob in pa.text_boxes:
                if x_axis:
                    along = min(tb.max_x, ob.max_x) - max(tb.min_x, ob.min_x)
                else:
                    along = min(tb.max_y, ob.max_y) - max(tb.min_y, ob.min_y)
                if along <= 0:
                    continue
                if x_axis:
                    perp_dist = abs((tb.min_y + tb.max_y) - (ob.min_y + ob.max_y)) * 0.5
                else:
                    perp_dist = abs((tb.min_x + tb.max_x) - (ob.min_x + ob.max_x)) * 0.5
                if perp_dist < lo or perp_dist > hi:
                    continue
                min_width = max(1e-9, min(tb.width if x_axis else tb.height,
                                          ob.width if x_axis else ob.height))
                pen += cfg.stagger_weight * min(1.0, along / min_width)
        return pen

    def _snap_to_grid(self, offset):
        cfg = self.cfg
        if cfg.string_spacing_ft <= 1e-9:
            return cfg.first_offset_ft
        n = (offset - cfg.first_offset_ft) / cfg.string_spacing_ft
        return cfg.first_offset_ft + round(n) * cfg.string_spacing_ft


def _seg_intersects_box(s, b):
    if _inside(s.a, b) or _inside(s.b, b):
        return True
    c00 = Vec2(b.min_x, b.min_y)
    c10 = Vec2(b.max_x, b.min_y)
    c11 = Vec2(b.max_x, b.max_y)
    c01 = Vec2(b.min_x, b.max_y)
    return (s.crosses(Seg2(c00, c10)) or s.crosses(Seg2(c10, c11))
            or s.crosses(Seg2(c11, c01)) or s.crosses(Seg2(c01, c00)))


def _inside(p, b):
    return b.min_x < p.x < b.max_x and b.min_y < p.y < b.max_y
