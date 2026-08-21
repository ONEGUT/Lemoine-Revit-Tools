"""Drawing-grade SVG renderer.

Draws the scene the way it would print: structural grid with bubbles, slab
outline, penetrations with their clash cross, and the placed dimensions with
real witness lines, diagonal ticks, arc leaders and value text at true text
height for the view scale. The point is to be judged by eye, so nothing is
schematic — paper sizes are real paper sizes.

`--debug` overlays the layout core's internal view: obstacle boxes, line bands,
cluster working regions and per-dimension score detail.
"""
import html
import math

from geom import Vec2, Box2
from anatomy import is_moved
from model import POSITIVE

PX_PER_INCH = 96.0          # rendering resolution of one paper inch

INK        = "#141414"      # dimensions, text
GRID_INK   = "#8d8d8d"
SLAB_INK   = "#5a5a5a"
PEN_FILL   = "#d8d8d8"
PEN_INK    = "#2a2a2a"
CLASH_INK  = "#c0392b"
DEBUG_BAND = "#1f6fd6"
DEBUG_REG  = "#0e9f9a"
DEBUG_OBS  = "#e8772e"
BAD_INK    = "#d23f31"


class Canvas:
    """Model feet -> SVG pixels at the view scale."""

    def __init__(self, extent, scale, margin_in=0.55):
        self.scale = float(scale)
        self.ppf = PX_PER_INCH * 12.0 / self.scale      # px per model foot
        self.margin = margin_in * PX_PER_INCH
        self.extent = extent
        self.w = extent.width * self.ppf + 2 * self.margin
        self.h = extent.height * self.ppf + 2 * self.margin

    def x(self, mx):
        return self.margin + (mx - self.extent.min_x) * self.ppf

    def y(self, my):
        return self.h - self.margin - (my - self.extent.min_y) * self.ppf

    def p(self, v):
        return (self.x(v.x), self.y(v.y))

    def ft(self, model_ft):
        return model_ft * self.ppf


def render(result, scene, out_path, debug=False, title=None, zoom=None):
    cfg = result.cfg
    extent = zoom or scene.extent
    c = Canvas(extent, scene.scale)
    o = []

    o.append(f'<svg xmlns="http://www.w3.org/2000/svg" width="{c.w:.0f}" height="{c.h:.0f}" '
             f'viewBox="0 0 {c.w:.0f} {c.h:.0f}" font-family="Helvetica,Arial,sans-serif">')
    o.append(f'<rect width="{c.w:.0f}" height="{c.h:.0f}" fill="#ffffff"/>')

    _draw_grid(o, c, scene)
    _draw_slab(o, c, scene)
    if debug:
        _draw_debug_under(o, c, result)
    _draw_penetrations(o, c, scene)
    for d in result.dims:
        _draw_dimension(o, c, d, cfg, debug, result)
    if debug:
        _draw_debug_over(o, c, result)

    _draw_titleblock(o, c, scene, result, title)
    o.append("</svg>")

    with open(out_path, "w") as f:
        f.write("\n".join(o))
    return out_path


# ── layers ─────────────────────────────────────────────────────────────────────

def _draw_grid(o, c, scene):
    lw = max(0.5, c.ft(0.0) + 0.7)
    dash = f"{PX_PER_INCH*0.30:.1f},{PX_PER_INCH*0.05:.1f},{PX_PER_INCH*0.05:.1f},{PX_PER_INCH*0.05:.1f}"
    e = c.extent
    bub_r = PX_PER_INCH * 0.14
    for g in scene.grids:
        if g.vertical:
            if not (e.min_x - 1e-6 <= g.coord <= e.max_x + 1e-6):
                continue
            x = c.x(g.coord)
            y0, y1 = c.y(e.min_y), c.y(e.max_y)
            o.append(f'<line x1="{x:.1f}" y1="{y0:.1f}" x2="{x:.1f}" y2="{y1:.1f}" '
                     f'stroke="{GRID_INK}" stroke-width="{lw:.2f}" stroke-dasharray="{dash}"/>')
            for yy, dy in ((y1, -bub_r), (y0, bub_r)):
                _bubble(o, x, yy + dy, bub_r, g.label)
        else:
            if not (e.min_y - 1e-6 <= g.coord <= e.max_y + 1e-6):
                continue
            y = c.y(g.coord)
            x0, x1 = c.x(e.min_x), c.x(e.max_x)
            o.append(f'<line x1="{x0:.1f}" y1="{y:.1f}" x2="{x1:.1f}" y2="{y:.1f}" '
                     f'stroke="{GRID_INK}" stroke-width="{lw:.2f}" stroke-dasharray="{dash}"/>')
            for xx, dx in ((x0, -bub_r), (x1, bub_r)):
                _bubble(o, xx + dx, y, bub_r, g.label)


def _bubble(o, x, y, r, label):
    o.append(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="{r:.1f}" fill="#ffffff" '
             f'stroke="{GRID_INK}" stroke-width="0.9"/>')
    o.append(f'<text x="{x:.1f}" y="{y + r*0.36:.1f}" font-size="{r*1.05:.1f}" '
             f'text-anchor="middle" fill="{GRID_INK}">{html.escape(label)}</text>')


def _draw_slab(o, c, scene):
    xs = [g.coord for g in scene.grids if g.vertical]
    ys = [g.coord for g in scene.grids if not g.vertical]
    if not xs or not ys:
        return
    x0, y0 = c.x(min(xs)), c.y(min(ys))
    x1, y1 = c.x(max(xs)), c.y(max(ys))
    o.append(f'<rect x="{min(x0,x1):.1f}" y="{min(y0,y1):.1f}" '
             f'width="{abs(x1-x0):.1f}" height="{abs(y1-y0):.1f}" '
             f'fill="none" stroke="{SLAB_INK}" stroke-width="1.4"/>')


def _draw_penetrations(o, c, scene):
    for p in scene.penetrations:
        cx, cy = c.p(p.centre)
        if p.round_:
            r = c.ft(p.w * 0.5)
            o.append(f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="{max(r,1.2):.1f}" '
                     f'fill="{PEN_FILL}" stroke="{PEN_INK}" stroke-width="0.8"/>')
        else:
            w, h = c.ft(p.w), c.ft(p.h)
            o.append(f'<rect x="{cx-w/2:.1f}" y="{cy-h/2:.1f}" width="{max(w,2):.1f}" '
                     f'height="{max(h,2):.1f}" fill="{PEN_FILL}" stroke="{PEN_INK}" '
                     f'stroke-width="0.8"/>')
        # the clash cross marker: arms ending at the marker edge
        ax = max(c.ft(p.w * 0.5), 2.0)
        ay = max(c.ft(p.h * 0.5), 2.0)
        o.append(f'<line x1="{cx-ax:.1f}" y1="{cy:.1f}" x2="{cx+ax:.1f}" y2="{cy:.1f}" '
                 f'stroke="{CLASH_INK}" stroke-width="0.9"/>')
        o.append(f'<line x1="{cx:.1f}" y1="{cy-ay:.1f}" x2="{cx:.1f}" y2="{cy+ay:.1f}" '
                 f'stroke="{CLASH_INK}" stroke-width="0.9"/>')


def _draw_dimension(o, c, d, cfg, debug, result):
    an = d.anatomy
    if an is None:
        return
    bad = False
    if debug and getattr(result, "scorer", None):
        bad = result.scorer.score(d, result.obstacles, result.dims)[0] > 1e-6
    ink = BAD_INK if bad else INK
    lw = 0.85

    axis = d.axis_dir.normalized()
    perp = axis.perp()
    sign = 1.0 if d.side == POSITIVE else -1.0
    th = cfg.text_height_ft

    # witness lines
    for w in an.witnesses:
        x1, y1 = c.p(w.a)
        x2, y2 = c.p(w.b)
        o.append(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" '
                 f'stroke="{ink}" stroke-width="{lw*0.8:.2f}"/>')

    # dimension line
    ax1, ay1 = c.p(an.dim_line.a)
    ax2, ay2 = c.p(an.dim_line.b)
    o.append(f'<line x1="{ax1:.1f}" y1="{ay1:.1f}" x2="{ax2:.1f}" y2="{ay2:.1f}" '
             f'stroke="{ink}" stroke-width="{lw:.2f}"/>')

    # diagonal ticks where each witness meets the line
    line_level = d.source_point.dot(perp) + sign * d.offset_ft
    tick = c.ft(th * 0.9)
    for r in (d.ref_anchors or []):
        pt = axis * r.dot(axis) + perp * line_level
        tx, ty = c.p(pt)
        dx = tick * 0.5 * (axis.x + perp.x) / math.sqrt(2)
        dy = -tick * 0.5 * (axis.y + perp.y) / math.sqrt(2)
        o.append(f'<line x1="{tx-dx:.1f}" y1="{ty-dy:.1f}" x2="{tx+dx:.1f}" y2="{ty+dy:.1f}" '
                 f'stroke="{ink}" stroke-width="{lw*1.2:.2f}"/>')

    # value text + leaders
    font_px = c.ft(th)
    from anatomy import segment_boundaries
    bounds = segment_boundaries(d, axis)
    vertical = abs(axis.y) > abs(axis.x)
    for k, seg in enumerate(d.segments):
        label = seg.value_str or ""
        centre_a = (bounds[k] + bounds[k + 1]) * 0.5

        if is_moved(seg.text_state) and seg.tag_pos is not None:
            pos = seg.tag_pos
            anchor = axis * centre_a + perp * line_level
            half_along = max(seg.text_width_ft, th) * 0.5
            col = -1.0 if d.tag_column_dir < 0 else 1.0
            front = pos - axis * (half_along * col)
            _arc_leader(o, c, anchor, front, ink)
        else:
            pos = axis * centre_a + perp * (line_level + th * 0.7)

        tx, ty = c.p(pos)
        rot = ""
        if vertical:
            rot = f' transform="rotate(-90 {tx:.1f} {ty:.1f})"'
        # text baseline sits just above the point for inline, centred for moved
        dy = 0 if is_moved(seg.text_state) else -font_px * 0.15
        o.append(f'<text x="{tx:.1f}" y="{ty+dy:.1f}" font-size="{font_px:.2f}" '
                 f'text-anchor="middle" fill="{ink}"{rot}>{html.escape(label)}</text>')


def _arc_leader(o, c, a, b, ink):
    x1, y1 = c.p(a)
    x2, y2 = c.p(b)
    mx, my = (x1 + x2) * 0.5, (y1 + y2) * 0.5
    # bow the chord slightly, as Revit's Arc leader does
    nx, ny = -(y2 - y1), (x2 - x1)
    n = math.hypot(nx, ny) or 1.0
    bow = min(10.0, n * 0.18)
    cx, cy = mx + nx / n * bow, my + ny / n * bow
    o.append(f'<path d="M {x1:.1f} {y1:.1f} Q {cx:.1f} {cy:.1f} {x2:.1f} {y2:.1f}" '
             f'fill="none" stroke="{ink}" stroke-width="0.7"/>')


# ── debug overlays ─────────────────────────────────────────────────────────────

def _rect(o, c, b, stroke, fill="none", width=0.7, dash=None):
    x0, y0 = c.x(b.min_x), c.y(b.max_y)
    da = f' stroke-dasharray="{dash}"' if dash else ""
    o.append(f'<rect x="{x0:.1f}" y="{y0:.1f}" width="{c.ft(b.width):.1f}" '
             f'height="{c.ft(b.height):.1f}" fill="{fill}" stroke="{stroke}" '
             f'stroke-width="{width}"{da}/>')


def _draw_debug_under(o, c, result):
    seen = set()
    for d in result.dims:
        if d.region is None or d.cluster_id in seen:
            continue
        seen.add(d.cluster_id)
        _rect(o, c, d.region, DEBUG_REG, "rgba(14,159,154,0.05)", 0.8, "6,4")


def _draw_debug_over(o, c, result):
    for b in result.obstacles:
        _rect(o, c, b, DEBUG_OBS, "none", 0.6)
    for d in result.dims:
        if d.anatomy:
            _rect(o, c, d.anatomy.line_band, DEBUG_BAND, "rgba(31,111,214,0.05)", 0.5)
            for tb in d.anatomy.text_boxes:
                _rect(o, c, tb, DEBUG_BAND, "none", 0.4)


def _draw_titleblock(o, c, scene, result, title):
    pad = 8
    lines = [title or scene.name,
             f"1:{scene.scale}   {len(scene.penetrations)} penetrations   "
             f"{len(result.dims)} dimension strings   "
             f"{sum(len(d.segments) for d in result.dims)} segments"]
    if result.hard > 1e-6:
        lines.append(f"hard {result.hard:.0f}   soft {result.soft:.0f}   "
                     f"moved tags {result.moved_tags}")
    else:
        lines.append(f"no hard violations   soft {result.soft:.0f}   "
                     f"moved tags {result.moved_tags}")
    o.append(f'<rect x="0" y="0" width="{max(len(l) for l in lines)*7.2 + pad*2:.0f}" '
             f'height="{pad*2 + 15*len(lines):.0f}" fill="#ffffff" fill-opacity="0.92"/>')
    y = pad + 12
    for i, ln in enumerate(lines):
        o.append(f'<text x="{pad}" y="{y:.0f}" font-size="{13 if i == 0 else 11}" '
                 f'fill="#333" font-weight="{"600" if i == 0 else "400"}">'
                 f'{html.escape(ln)}</text>')
        y += 15
