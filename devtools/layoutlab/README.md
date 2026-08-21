# Layout Lab — auto-dimension design harness

A Revit-free replay of the auto-dimension algorithm, so placement quality can be
seen and changed without a Windows build, a live model, or a tool run.

`pipeline.py` mirrors `AutoDimensionEngine.BuildPlan`; `geom/anatomy/scorer/
layout/chainer/grouping` are faithful ports of the corresponding `Core/*.cs` and
grouping files. The scene generator invents plausible penetration sets, and the
renderer draws the result the way it would print — real paper sizes, real text
height for the view scale — because the output is meant to be judged by eye.

## Use

    python3 run.py --pattern mixed --seed 7             # one scene + metrics + SVG
    python3 run.py --pattern all --seed 7               # one of every pattern
    python3 run.py --pattern rack --batch 6             # six random racks
    python3 run.py --pattern mixed --seed 7 --zoom busiest --debug
    python3 shot.py out.svg out.png                     # rasterise for review

Patterns: `rack` (services crossing a wall), `bank` (shaft block), `scatter`,
`dense` (riser pocket), `pair` (mid-bay, equidistant grids), `mixed`.

`--debug` overlays the layout core's own view: obstacle boxes, line bands,
cluster working regions, and red linework on any string with a hard violation.

## Metrics

`metrics.py` measures what the production scorer does **not**: strings drawn per
penetration, redundant measures, repeated value strings, max row depth, and ink
in paper inches. These are the candidate objective terms for "as few lines as
possible carrying as much information as possible" — see
`plan-dimension-placement-quality.md` at the repo root.

## Parity

This is a design lab, not a second implementation. When an algorithm change
settles here it gets written in C#; the fixtures then act as the regression set.
Anything changed in `Core/*.cs` must be mirrored here or the lab stops telling
the truth.
