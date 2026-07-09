#!/usr/bin/env python3
"""Joins a WPF snapshot (from LemoinePreview --capture) against a twin
measurement (from measure.py) by element id and reports every geometric
deviation.

This is a plain deterministic script — same two input files always produce
the same report. It does not use AI/LLM judgement anywhere; applying a fix
for a reported delta is a separate, human/Claude step.

Usage:
    python3 devtools/design-twin/compare.py <wpf.json> <twin.json> [--tolerance 0.5] [--overlay out.png --wpf-png a.png --twin-png b.png]

Exit code is nonzero if any element is out of tolerance or if either side
has ids the other is missing — a diff report you can wire into CI.
Pure stdlib for the report; --overlay needs Pillow (optional, only for the
visual overlay image — the numeric report never depends on it).
"""
import argparse
import json
import sys
from pathlib import Path


def load(path):
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    if "elements" not in data:
        sys.exit(f"compare.py: {path} has no 'elements' key — wrong schema?")
    return data


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("wpf_json")
    ap.add_argument("twin_json")
    ap.add_argument("--tolerance", type=float, default=0.5, help="max allowed delta in px/DIP (default 0.5)")
    ap.add_argument("--overlay", help="write a visual diff overlay PNG to this path")
    ap.add_argument("--wpf-png")
    ap.add_argument("--twin-png")
    args = ap.parse_args()

    wpf = load(args.wpf_json)
    twin = load(args.twin_json)

    wpf_ids = set(wpf["elements"].keys())
    twin_ids = set(twin["elements"].keys())

    missing_in_twin = sorted(wpf_ids - twin_ids)
    missing_in_wpf = sorted(twin_ids - wpf_ids)
    common = sorted(wpf_ids & twin_ids)

    rows = []
    for eid in common:
        w = wpf["elements"][eid]
        t = twin["elements"][eid]
        dx = t["x"] - w["x"]
        dy = t["y"] - w["y"]
        dw = t["width"] - w["width"]
        dh = t["height"] - w["height"]
        worst = max(abs(dx), abs(dy), abs(dw), abs(dh))
        rows.append({
            "id": eid, "dx": dx, "dy": dy, "dw": dw, "dh": dh, "worst": worst,
            "wpf": w, "twin": t,
        })

    rows.sort(key=lambda r: -r["worst"])
    out_of_tolerance = [r for r in rows if r["worst"] > args.tolerance]

    print(f"compare.py: {len(common)} elements matched, "
          f"{len(missing_in_twin)} only in WPF, {len(missing_in_wpf)} only in twin")
    print(f"tolerance: {args.tolerance}px\n")

    if missing_in_twin:
        print(f"MISSING IN TWIN ({len(missing_in_twin)}):")
        for eid in missing_in_twin:
            print(f"  - {eid}")
        print()

    if missing_in_wpf:
        print(f"MISSING IN WPF ({len(missing_in_wpf)}) (twin has an element the capture never tagged):")
        for eid in missing_in_wpf:
            print(f"  - {eid}")
        print()

    if out_of_tolerance:
        print(f"OUT OF TOLERANCE ({len(out_of_tolerance)}), worst first:")
        for r in out_of_tolerance:
            print(f"  {r['id']:<40} dx={r['dx']:+7.2f}  dy={r['dy']:+7.2f}  "
                  f"dw={r['dw']:+7.2f}  dh={r['dh']:+7.2f}   (wpf {r['wpf']['x']:.1f},{r['wpf']['y']:.1f} "
                  f"{r['wpf']['width']:.1f}x{r['wpf']['height']:.1f} -> "
                  f"twin {r['twin']['x']:.1f},{r['twin']['y']:.1f} {r['twin']['width']:.1f}x{r['twin']['height']:.1f})")
        print()
    else:
        print("All matched elements within tolerance.\n")

    if args.overlay and args.wpf_png and args.twin_png:
        write_overlay(args.wpf_png, args.twin_png, out_of_tolerance, args.overlay)
        print(f"Overlay written to {args.overlay}")

    failed = bool(out_of_tolerance or missing_in_twin or missing_in_wpf)
    sys.exit(1 if failed else 0)


def write_overlay(wpf_png, twin_png, out_of_tolerance, out_path):
    try:
        from PIL import Image, ImageDraw, ImageChops
    except ImportError:
        print("compare.py: --overlay requires Pillow (pip install pillow) — skipping image, report above still valid")
        return

    a = Image.open(wpf_png).convert("RGBA")
    b = Image.open(twin_png).convert("RGBA").resize(a.size)
    blended = Image.blend(a, b, 0.5)
    draw = ImageDraw.Draw(blended)
    # Elements were measured in DIPs/CSS px at 1x logical size; screenshots may
    # be captured at a higher device-scale-factor. Caller is responsible for
    # passing PNGs whose pixel size matches the JSON's logical coordinate
    # space (i.e. scale factor 1), or pre-scaling the boxes before calling this.
    for r in out_of_tolerance:
        w = r["wpf"]
        draw.rectangle(
            [w["x"], w["y"], w["x"] + w["width"], w["y"] + w["height"]],
            outline=(255, 0, 128, 255), width=2,
        )
    blended.save(out_path)


if __name__ == "__main__":
    main()
