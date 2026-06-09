# Plan — Slab-Edge Auto-Target Diagnostics (linked slab never found on its own)

## Symptom (confirmed by user)
- "To Slab Edge" in Clash Finder / Auto Dimension **works when a slab is picked** (host or linked).
- In **automatic mode (no pick → scan all floors)** it **never resolves to the intended linked slab**, and it fails **silently** (no "Missing link ref", no red "fail" lines noticed).

## What this rules out
Because picking a linked floor works, the per-floor pipeline is already proven on linked geometry:
- `Floor.get_Geometry` on a linked-doc element ✔
- link-transform of face normal/point ✔
- `LinkRefHelper.ToHostReference` / `Reference.CreateLinkReference` ✔
- `NewDimension` with a linked reference ✔

The only code path that differs between pick and auto is the floor-scope filter in
`SlabEdgeTargetResolver.EnsureCache` (`Source/Tools/T05-Clash/AutoDimension/Resolvers/SlabEdgeTargetResolver.cs:163-180`).
So the fault is in the **all-floors candidate competition / selection**, not in link scanning.

## Leading hypotheses (to be confirmed by data, not guessed)
1. **Out-ranked** — a nearer host (or other) slab edge wins the nearest-edge sort (`Resolve` lines 99-110), so the dimension lands on the wrong slab and the intended linked one is never chosen.
2. **Ambiguity knockout** — with many stacked/parallel slab faces in scope, the top-two-within-threshold guard (`Resolve` lines 122-141) returns Ambiguity (or the same-edge collapse, lines 112-118, drops the wanted face), yielding no placement.
3. **Linked faces absent in auto mode only** — unlikely given pick works, but must be confirmed: linked faces actually enter the candidate list when `SlabScopes` is empty.

## Diagnostic changes (logging only — no behaviour change)
All additions route through the existing run-log sink (`ctx.Log`) and `LemoineLog`, so nothing is swallowed.

### File: `Source/Tools/T05-Clash/AutoDimension/Resolvers/SlabEdgeTargetResolver.cs`
1. **Per-source-doc cache breakdown** (in `EnsureCache`): for the host and each link, log
   `floors collected`, `planar faces seen`, `vertical faces kept`, `dropped: null ref`,
   `dropped: link-ref conversion failed`. Replaces the single total at line 231 with a
   per-source tally plus the total. Confirms hypothesis 3 in one run.
2. **Per-resolve candidate dump** (in `Resolve`, slab mode): for each source line, log the top
   N (≈5) candidates with: source label (`host` / `link <id>`), floor id, `RadialDist`,
   `Delta`, `Area`, `Score`, and the final outcome (`won` / `ambiguous vs <key>` / `no candidate`).
   Confirms hypotheses 1 and 2.
3. Gate the verbose per-resolve dump behind a flag so it cannot spam huge runs (see config knob below);
   the per-source cache breakdown always logs (cheap, one line per doc).

### File: `Source/Tools/T05-Clash/AutoDimension/AutoDimensionConfig.cs`
- Add `public bool DiagnoseSlabEdge { get; set; } = false;` (XML-persisted, default off) to switch the
  verbose per-resolve candidate dump on for a diagnostic run without recompiling. No migration needed
  (new optional element; absent in old files → false).

## Out of scope (this branch)
No selection/scoring fix yet. Once the run log identifies which hypothesis is real, the actual fix
(e.g. prefer-linked weighting, tighten ambiguity in scan-all, or a "links first" pass) goes on a
separate branch.

## Files touched
- `Source/Tools/T05-Clash/AutoDimension/Resolvers/SlabEdgeTargetResolver.cs` (logging)
- `Source/Tools/T05-Clash/AutoDimension/AutoDimensionConfig.cs` (one diagnostic flag)

## Verify
Cannot build on Linux (per CLAUDE.md). User runs a SlabEdge auto pass on Windows with
`DiagnoseSlabEdge=true`, then pastes `%AppData%\LemoineTools\diagnostics.log` + the run log.
