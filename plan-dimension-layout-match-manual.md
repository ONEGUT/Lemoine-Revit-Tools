# Plan — Make generated dimension layout match the manual drawing

Goal: close the gap between the engine's slab-penetration dimensioning (bottom of the
supplied PDF) and the hand-drafted version (top). Findings are ranked by how strongly
they show in the comparison and by confidence.

> Build/verify constraint: this project is Windows-only (UseWPF + net48). I cannot build
> or render the result on Linux, so every change below is conservative, kept behind the
> existing config, and will need one visual check on Windows. Numeric tweaks stay tunable.

---

## Finding 1 — Leadered text is dragged into side-columns (the #1 visual gap)

**What the generated drawing does:** `AutoDimensionCommit.ApplyTextStates` moves *every*
cramped segment flagged `Staggered` or `LeaderOut` to a single column placed **before the
run start** (`colAxial = runStart − 4·textHeight`) and stacks the marks outward
(`LaneBase 2 + lane·2.6` text-heights). Revit then draws a curved leader from that far
column to each mark — the fan of arcs all over the bottom image.

**What the manual drawing does:** value text stays **on its own segment** (centred, or
nudged a little along/above the line). Short segments are allowed to let their text
**overhang**; long curved leaders are essentially never used.

**Rule changes**
- **1a (geometry, high impact):** In `ApplyTextStates`, position moved text at the
  segment's **own along-axis midpoint**, pushed straight out perpendicular by a small
  fixed amount — a short, straight leader (or none) instead of a cross-drawing arc.
  Drop the `runStart − 4·th` column and the lane fan for the normal case; keep a minimal
  alternating nudge only when two adjacent segments' text would physically overlap.
- **1b (scoring bias):** In `ResolveSegments` + `LayoutConfig`, prefer **Inline overhang**
  the way a drafter does: raise `LeaderWeight` (8 → ~40) and lower `CrampedWeight`
  (10 → ~3) so text overhanging a short segment beats leadering it out. Net effect:
  `LeaderOut` becomes rare; `Staggered` becomes a small inline nudge, not a column move.

Confidence: **high** — this is the dominant difference and is a direct mechanism in code.

---

## Finding 2 — Fragmented short strings vs. long chained runs to the edge

**Observed:** the manual version consolidates aligned penetrations into **continuous
chained dimension runs** that carry across a bay to the slab edge; the generated version
leaves more **separate short strings**, which are exactly the ones that come out cramped
and then get leadered (feeds Finding 1).

**Rule changes**
- **2a:** Raise the default `ChainMaxGapMm` (1500 → ~3000) so penetrations spread across a
  bay chain into one run instead of several.
- **2b (optional):** Nudge `ChainCollinearToleranceMm` (150 → ~250) so marks that are a
  little off the shared baseline still join the run.

Both already exist as Clash-Finder steppers, so this is a default change, fully tunable.

Confidence: **medium**.

---

## Finding 3 — Tight / uneven ladder spacing

**Observed:** manual parallel strings sit in an **even ladder** with generous, uniform
offset; generated strings are packed tighter (small `FirstOffset` / `StringSpacing`), so
they collide and the greedy engine is forced to move/leader text.

**Rule change**
- **3a:** Increase `FirstOffsetFt` (3/8" → ~1/2") and `StringSpacingFt` (3/8" → ~1/2"–5/8")
  paper-space defaults so banks separate cleanly and inline text has room. Keep the
  `UnevenSpacingWeight` cadence so they still snap to a regular ladder.

Confidence: **medium**.

---

## Finding 4 — Side / reading-direction consistency (optional, lower confidence)

**Observed:** the manual cluster dimensions read consistently **toward the nearest slab
edge** (short witness lines, text outside the slab body); the greedy engine takes the first
side that clears, so direction is less consistent.

**Rule change**
- **4a:** Seed each dimension's initial `Side` toward the nearer slab edge before the greedy
  offset search, so ties resolve outboard. Small, isolated change in the engine seeding.

Confidence: **low–medium** — propose last, easy to drop.

---

## Persistence / migration (needed for the numeric defaults to actually apply)

`AutoDimensionConfig` (incl. the `Layout` block) is serialized to
`%AppData%\LemoineTools\AutoDimension.xml` and saved by the standalone Auto Dimension
wizard. A user with an existing file would keep the **old** Layout numbers, so changing
code defaults alone won't reach them.

- Bump `AutoDimensionConfig.SchemaVersion` 1 → 2 and, on load of an older version, refresh
  the `Layout` defaults (and the new `ChainMaxGapMm` default) to the new values.
- The geometry/algorithm changes (1a, 2 grouping, 4a) take effect regardless of persisted
  config; only the weight/spacing numbers (1b, 3a) rely on this migration.

---

## Files touched

| File | Change |
|------|--------|
| `Core/LayoutConfig.cs` | `LeaderWeight`, `CrampedWeight`, `FirstOffsetFt`, `StringSpacingFt` defaults (1b, 3a) |
| `Core/GreedyLayoutEngine.cs` | `ResolveSegments` bias toward inline (1b); optional Side seeding (4a) |
| `AutoDimensionCommit.cs` | `ApplyTextStates` — per-segment local text placement, drop the side-column fan (1a) |
| `AutoDimensionConfig.cs` | `ChainMaxGapMm` default (2a); `SchemaVersion` bump + migration |
| `DimensionChainer.cs` | only if 2b collinear default moves |

## Suggested order / scope
1. **Finding 1 (1a + 1b)** — biggest win, do first; re-check on Windows.
2. **Findings 2 + 3** — consolidate runs and open up the ladder.
3. **Finding 4** — only if still needed after 1–3.

Each is independent and reversible; we can stop after any step once it looks right.
