#!/usr/bin/env python3
"""Layout Lab — generate random penetration sets, dimension them with the
production algorithm, render the result, and report quality metrics.

    python3 run.py --pattern mixed --seed 7
    python3 run.py --batch 6 --pattern rack --out /tmp/lab
    python3 run.py --pattern mixed --seed 7 --zoom busiest --debug

Nothing here touches Revit: `pipeline.py` is a faithful Python port of the
Revit-free layout core (Core/*.cs) plus the engine's orchestration, so an
algorithm change can be seen before it is written in C#.
"""
import argparse
import os
import sys

import metrics
import pipeline
import render
import scene as scene_mod
from geom import Box2


def busiest_region(result, scene, pad_ft=12.0):
    """Crop around the cluster carrying the most dimension strings."""
    counts = {}
    for d in result.dims:
        counts[d.cluster_id] = counts.get(d.cluster_id, 0) + 1
    if not counts:
        return None
    best = max(sorted(counts), key=lambda k: counts[k])
    box = None
    for d in result.dims:
        if d.cluster_id != best or d.anatomy is None:
            continue
        box = d.anatomy.bounds if box is None else box.union(d.anatomy.bounds)
    return box.expand(pad_ft) if box else None


def one(pattern, seed, scale, out_dir, debug=False, zoom=None, quiet=False):
    sc = scene_mod.generate(pattern, seed=seed, scale=scale)
    result = pipeline.build_plan(sc)
    m = metrics.compute(result, sc)

    os.makedirs(out_dir, exist_ok=True)
    stem = os.path.join(out_dir, sc.name + ("-debug" if debug else ""))

    crop = None
    if zoom == "busiest":
        crop = busiest_region(result, sc)
        stem += "-zoom"
    svg = render.render(result, sc, stem + ".svg", debug=debug,
                        title=sc.name, zoom=crop)

    if not quiet:
        print(f"\n{sc.summary()}")
        print(metrics.format_table(m))
        if m["violations"]:
            print("  hard faults:")
            for key, h, why in m["violations"][:6]:
                print(f"    {key[:46]:<46} {h:8.0f}  {why}")
        print(f"  -> {svg}")
    return sc, result, m, svg


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--pattern", default="mixed",
                    choices=list(scene_mod.PATTERNS) + ["all"])
    ap.add_argument("--seed", type=int, default=None)
    ap.add_argument("--scale", type=int, default=48, help="view scale denominator")
    ap.add_argument("--batch", type=int, default=1, help="how many scenes")
    ap.add_argument("--out", default="/tmp/layoutlab")
    ap.add_argument("--debug", action="store_true", help="overlay the core's internal view")
    ap.add_argument("--zoom", choices=["busiest"], default=None)
    args = ap.parse_args()

    patterns = list(scene_mod.PATTERNS) if args.pattern == "all" else [args.pattern]
    seed0 = args.seed if args.seed is not None else 1

    summary = []
    for pat in patterns:
        for i in range(args.batch):
            seed = seed0 + i
            _sc, _r, m, svg = one(pat, seed, args.scale, args.out,
                                  debug=args.debug, zoom=args.zoom)
            summary.append((pat, seed, m, svg))

    if len(summary) > 1:
        print("\n" + "=" * 84)
        print(f"{'pattern':<9} {'seed':>5} {'pens':>5} {'strings':>8} {'chained':>8} "
              f"{'str/pen':>8} {'repeat':>7} {'depth':>6} {'hard':>8}")
        for pat, seed, m, _svg in summary:
            print(f"{pat:<9} {seed:>5} {m['penetrations']:>5} {m['strings']:>8} "
                  f"{m['chained_strings']:>8} {m['strings_per_penetration']:>8.2f} "
                  f"{m['repeated_values']:>7} {m['max_row_depth']:>6} {m['hard_total']:>8.0f}")


if __name__ == "__main__":
    main()
