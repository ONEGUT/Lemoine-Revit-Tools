#!/usr/bin/env python3
"""Renders an auto-dimension LayoutSnapshot XML (written by the engine when
Settings -> Dimensions -> "Dump layout snapshots" is on) to an SVG of the whole
estimation space: obstacles, witness lines, dimension lines, value-text boxes,
planned tag columns and leader chords. Dimensions with a non-zero hard score
draw red; hovering one shows its per-constraint score breakdown.

Usage:  python3 devtools/render_layout_snapshot.py <snapshot.xml> [out.svg]

Pure stdlib — no packages required.
"""
import math
import sys
import xml.etree.ElementTree as ET

BLUE = "#1f6fd6"; TEAL = "#0e9f9a"; ORANGE = "#e8772e"; PURPLE = "#8a4fd3"
GREY = "#9aa1a9"; RED = "#d23f31"; INK = "#2b2f33"; GREEN = "#3f9b46"


def f(el, name, default=0.0):
    v = el.get(name)
    if v is None:
        return default
    try:
        return float(v)
    except ValueError:
        return default


def cfg_val(cfg, name, default):
    el = cfg.find(name) if cfg is not None else None
    try:
        return float(el.text) if el is not None and el.text else default
    except ValueError:
        return default


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    src = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else src.rsplit(".", 1)[0] + ".svg"

    root = ET.parse(src).getroot()
    cfg = root.find("Config")
    th = cfg_val(cfg, "TextHeightFt", 0.75)
    wgap = cfg_val(cfg, "WitnessGapFt", th * 0.667)
    wover = cfg_val(cfg, "WitnessOvershootFt", th * 1.333)

    obstacles = [(f(b, "MinX"), f(b, "MinY"), f(b, "MaxX"), f(b, "MaxY"))
                 for b in root.findall("Obstacles/Box")]
    dims = root.findall("Dimensions/Dim")

    # ── collect drawing primitives in model space ─────────────────────────
    prims = []   # (kind, payload, dim_or_None);  kinds: rect/line/dot
    pts = []     # extent tracking

    def track(x, y):
        pts.append((x, y))

    for (x0, y0, x1, y1) in obstacles:
        prims.append(("rect", (x0, y0, x1, y1, GREY, 0.16, GREY, 1.0, None), None))
        track(x0, y0); track(x1, y1)

    # cluster working regions (one dashed green box per cluster, deduped by id)
    seen_regions = set()
    for d in dims:
        cid = d.get("ClusterId", "")
        rx0 = f(d, "RegionMinX", float("nan"))
        if not cid or cid in seen_regions or math.isnan(rx0):
            continue
        seen_regions.add(cid)
        ry0, rx1, ry1 = f(d, "RegionMinY"), f(d, "RegionMaxX"), f(d, "RegionMaxY")
        prims.append(("region", (rx0, ry0, rx1, ry1, cid), None))
        track(rx0, ry0); track(rx1, ry1)

    total_hard = 0.0
    for d in dims:
        ax, ay = f(d, "AxisX", 1), f(d, "AxisY", 0)
        n = math.hypot(ax, ay) or 1.0
        ax, ay = ax / n, ay / n
        px, py = -ay, ax
        sign = 1.0 if d.get("Side", "Positive") == "Positive" else -1.0
        off = f(d, "OffsetFt")
        sx, sy = f(d, "SrcX"), f(d, "SrcY")
        tx, ty = f(d, "TgtX"), f(d, "TgtY")
        hard = f(d, "Hard")
        total_hard += hard
        col = RED if hard > 1e-6 else BLUE
        tip = (f"{d.get('SourceKey','')}  hard={hard:.0f} soft={f(d,'Soft'):.1f}"
               + (f"  [{d.get('ScoreDetail')}]" if d.get("ScoreDetail") else ""))

        ox, oy = px * sign * off, py * sign * off
        line_level = sx * px + sy * py + sign * off

        # dimension line
        prims.append(("line", (sx + ox, sy + oy, tx + ox, ty + oy, col, 2.0, tip), d))
        track(sx + ox, sy + oy); track(tx + ox, ty + oy)

        # witnesses + anchors (anchor-aware: each witness runs from the anchor's true
        # position toward the dimension line level, whichever side it sits on)
        anchors = [(f(p, "X"), f(p, "Y")) for p in d.findall("RefAnchors/P")]
        for (rx, ry) in anchors:
            rp = rx * px + ry * py
            dw = 1.0 if line_level >= rp else -1.0
            ra = rx * ax + ry * ay
            w0 = (rx + px * dw * wgap, ry + py * dw * wgap)
            w1 = (ax * ra + px * (line_level + dw * wover),
                  ay * ra + py * (line_level + dw * wover))
            prims.append(("line", (*w0, *w1, TEAL, 1.1, None), d))
            prims.append(("dot", (rx, ry, TEAL), d))
            track(*w1)

        # segment boundaries along the axis
        segs = d.findall("Segments/Seg")
        bounds = sorted(x * ax + y * ay for (x, y) in anchors)
        if len(bounds) != len(segs) + 1:
            a0 = min(sx * ax + sy * ay, tx * ax + ty * ay)
            bounds = [a0]
            for s in segs:
                bounds.append(bounds[-1] + f(s, "LengthFt"))

        col_dir = 1.0 if int(float(d.get("TagColumnDir", "1"))) >= 0 else -1.0
        for k, s in enumerate(segs):
            w = max(f(s, "TextWidthFt"), th)
            half_a, half_p = w / 2, th * 0.55
            centre_a = (bounds[k] + bounds[k + 1]) / 2
            tag_x, tag_y = f(s, "TagX", float("nan")), f(s, "TagY", float("nan"))
            moved = s.get("State", "Inline") != "Inline" and not math.isnan(tag_x)
            if moved:
                cx, cy = tag_x, tag_y
                anchor = (ax * centre_a + px * line_level, ay * centre_a + py * line_level)
                front = (cx - ax * half_a * col_dir, cy - ay * half_a * col_dir)
                prims.append(("line", (*anchor, *front, PURPLE, 1.4, None), d))
            else:
                lvl = line_level + th * 0.7
                cx, cy = ax * centre_a + px * lvl, ay * centre_a + py * lvl
            hx = abs(ax) * half_a + abs(px) * half_p
            hy = abs(ay) * half_a + abs(py) * half_p
            prims.append(("rect", (cx - hx, cy - hy, cx + hx, cy + hy,
                                   ORANGE, 0.10, RED if hard > 1e-6 else ORANGE, 0.9, tip), d))
            track(cx - hx, cy - hy); track(cx + hx, cy + hy)

    # ── model -> svg transform ────────────────────────────────────────────
    if not pts:
        print("nothing to draw"); sys.exit(1)
    minx = min(p[0] for p in pts); maxx = max(p[0] for p in pts)
    miny = min(p[1] for p in pts); maxy = max(p[1] for p in pts)
    pad = max(maxx - minx, maxy - miny) * 0.02 + 1
    minx -= pad; maxx += pad; miny -= pad; maxy += pad
    W = 2400.0
    sc = W / (maxx - minx)
    H = (maxy - miny) * sc + 70

    def X(x): return (x - minx) * sc
    def Y(y): return (maxy - y) * sc + 60   # flip: model y-up -> svg y-down

    o = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W:.0f}" height="{H:.0f}" '
         f'viewBox="0 0 {W:.0f} {H:.0f}" font-family="Arial, sans-serif">',
         f'<rect width="{W:.0f}" height="{H:.0f}" fill="#ffffff"/>',
         f'<text x="14" y="24" font-size="16" font-weight="bold" fill="{INK}">'
         f'{root.get("ViewName","")} &#183; 1:{root.get("ViewScale","?")} &#183; '
         f'{len(dims)} dims / {len(obstacles)} obstacles &#183; total hard {total_hard:.0f} &#183; '
         f'{root.get("Timestamp","")}</text>',
         f'<text x="14" y="44" font-size="12" fill="{GREY}">blue/red = dimension line (red carries a hard violation '
         f'&#8212; hover for the breakdown) &#183; teal = witnesses &#183; orange = value text boxes &#183; '
         f'purple = leader chords &#183; grey = obstacles &#183; dashed green = cluster regions</text>']

    for kind, p, _d in prims:
        if kind == "region":
            x0, y0, x1, y1, cid = p
            o.append(f'<rect x="{X(x0):.1f}" y="{Y(y1):.1f}" width="{(x1-x0)*sc:.1f}" '
                     f'height="{(y1-y0)*sc:.1f}" fill="none" stroke="{GREEN}" '
                     f'stroke-width="1.4" stroke-dasharray="7 5"><title>{esc(cid)}</title></rect>')
            o.append(f'<text x="{X(x0)+4:.1f}" y="{Y(y1)+14:.1f}" font-size="11" '
                     f'fill="{GREEN}">{esc(cid)}</text>')
        elif kind == "rect":
            x0, y0, x1, y1, fill, fop, stroke, sw, tip = p
            t = f"<title>{esc(tip)}</title>" if tip else ""
            o.append(f'<rect x="{X(x0):.1f}" y="{Y(y1):.1f}" width="{(x1-x0)*sc:.1f}" '
                     f'height="{(y1-y0)*sc:.1f}" fill="{fill}" fill-opacity="{fop}" '
                     f'stroke="{stroke}" stroke-width="{sw}">{t}</rect>')
        elif kind == "line":
            x0, y0, x1, y1, stroke, sw, tip = p
            t = f"<title>{esc(tip)}</title>" if tip else ""
            o.append(f'<line x1="{X(x0):.1f}" y1="{Y(y0):.1f}" x2="{X(x1):.1f}" y2="{Y(y1):.1f}" '
                     f'stroke="{stroke}" stroke-width="{sw}">{t}</line>' if tip else
                     f'<line x1="{X(x0):.1f}" y1="{Y(y0):.1f}" x2="{X(x1):.1f}" y2="{Y(y1):.1f}" '
                     f'stroke="{stroke}" stroke-width="{sw}"/>')
        elif kind == "dot":
            x0, y0, fill = p
            o.append(f'<circle cx="{X(x0):.1f}" cy="{Y(y0):.1f}" r="2.5" fill="{fill}"/>')

    o.append("</svg>")
    with open(out, "w") as fh:
        fh.write("\n".join(o))
    print(f"wrote {out}  ({len(dims)} dims, {len(obstacles)} obstacles, total hard {total_hard:.0f})")


def esc(s):
    return (s or "").replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


if __name__ == "__main__":
    main()
