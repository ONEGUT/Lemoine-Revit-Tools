# Plan — Ceiling Heatmap: stop the tolerance rewriting ceiling heights

## Problem

A ceiling at exactly **10'-8"** produces a heatmap filter whose rule value is
**10' 7 251/256"**.

`AddBucket` does not merely *group* ceilings by tolerance — it **snaps the height onto a
grid of tolerance multiples** and stores that synthetic number as the bucket's value:

```csharp
// CeilingHeatmapEventHandler.cs:919
double snapped = Math.Round(heightOffset / tol) * tol;
```

The snapped value is then what is fed to `ParameterFilterRuleFactory.CreateEqualsRule`
(`BuildBucketFilter`, line 933) and persisted into the Auto Filters trade rule
(`RegisterCeilingHeatmapTrade`, line 869). Unless a ceiling's true height is an exact
multiple of the tolerance, the number the user sees drifts by up to half a tolerance.

Reproduced exactly, with a tolerance of 0.54 in (identically 0.27 / 0.18 / 0.09):

```
round(128.00" / 0.54") x 0.54"  =  127.98"  =  10' 7 251/256"
```

### Why the tolerance is a non-dyadic number

The tolerance stepper is declared with `Decimals = 2` (`CeilingHeatmapViewModel.cs:587`),
which cannot represent a fractional inch:

| Value | Intended | What the control does |
|---|---|---|
| Default 1/8" | 0.125 | `Math.Round(0.125, 2)` -> banker's-rounds to **0.12**; the box cannot show 0.125 |
| `MinValue` 1/16" | 0.0625 | commits as **0.06** |
| `Step` | 0.25 | walks 0.12 -> 0.37 -> 0.62 -> 0.87, none of which divide a real ceiling height |

`InlineStepper.CommitValue` re-rounds to `Decimals` (`InlineStepper.xaml.cs:158`), so the
**first time the field is focused and committed the persisted tolerance silently degrades
from 0.125" to 0.12"**, and every bucket goes crooked from then on.

### Two secondary defects from the same code

1. **Boundary miss.** The match epsilon is exactly `tol/2` centred on the snapped value
   (line 931), so a ceiling sitting precisely on a bucket edge is on the epsilon boundary
   and can fail to match its own filter — it stays uncoloured with nothing logged.
2. **Name/value mismatch.** `FormatFtIn` rounds to whole inches, so the filter is *named*
   `10'-8" AFF` while its rule matches 10' 7 251/256" — the label looks right, hiding the
   drift.

## Outcome — the tolerance is removed entirely

Two intermediate schemes were built and both still reported a number no ceiling had:

1. **Snap to a grid of tolerance multiples** (the original). `Math.Round(h / tol) * tol` moved
   the value by up to half a tolerance — an exact 10'-8" ceiling became **10' 7 251/256"** at a
   0.54 in tolerance.
2. **Group near-equal heights, report the bucket's midpoint.** Fixed the case where a bucket
   held one height, but a bucket spanning two real heights still had to report a value that was
   neither: an exact 10'-0" ceiling sharing a bucket with a 10' 0 1/64" one came out as
   **10' 0 1/128"** (their exact average).

The tolerance is therefore gone. **One filter per distinct height offset, matched at its exact
value** — the only scheme where every number Revit displays is a height that exists in the model.

### What that means in code

- **`CeilingHeatmapEventHandler.cs`** — the scan collects exact `AsDouble()` values into a
  `HashSet<double>` (ceilings at the same height give bit-identical doubles from the same
  parameter, so the set groups them exactly); the buckets are that set, sorted. The rule is
  `CreateEqualsRule(paramId, height, 1e-6 ft)` — 1.2e-5 in, the smallest epsilon that still
  matches a double, far below any display rounding and far above double noise at ~10 ft.
  `ElevTolerance`, `BuildBuckets` and `BucketValue` are deleted.
- **`CeilingHeatmapViewModel.cs`** — the tolerance stepper, its label, hint, review row and
  step summary are removed. Step 2 now carries only the "Delete existing heatmap filters"
  toggle; its summary reads "Delete existing" / "Keep existing".
- **`CeilingHeatmapSettings.cs`** — `ElevTolerance` removed. `XmlSerializer` ignores unknown
  elements, so a settings file still carrying `<ElevTolerance>` degrades harmlessly.
- **`GlobalSettingsWindow.ToolGroups.cs`** — the second tolerance stepper on the Global
  Settings → Ceilings page is removed too (it would otherwise have written to a deleted
  property).
- **Strings** — the tolerance keys are dropped from `ceilings.heatmap.json` and
  `globalSettings.json`; `foundBuckets` and the review note now speak of distinct ceiling
  heights rather than buckets; `manyBuckets` warns past 60 heights without mentioning a
  tolerance.

### Accepted trade-off

A model whose ceilings vary by hair's-breadth amounts now gets one filter per variant instead of
a few grouped bands. That is the honest answer, and it is what makes the reported values exact —
but it is worth knowing, so the run log warns past 60 distinct heights.

### Also fixed along the way

- **`FormatFtIn` filter names** were always whole-inch correct (`10'-8" AFF`), which is what
  masked the drift: the filter was *labelled* right while its rule matched a different number.
- **The persisted Auto Filters rule value** was written at 6 decimals of feet (`10.666667`),
  a third of the match epsilon away from the value it described. Now 9 decimals.
- **A latent boundary miss.** The old epsilon was exactly half the grid spacing, so a ceiling
  half a tolerance from the grid point sat on the match edge and could fail its own filter —
  an uncoloured ceiling with nothing logged. With exact-value rules the height *is* the rule
  value, so this cannot occur.

## Files touched

| File | Change |
|---|---|
| `Source/Tools/Ceilings/CeilingHeatmapEventHandler.cs` | Exact-height buckets; `ElevTolerance`/`BuildBuckets`/`BucketValue` deleted; exact-value equals-rule; 9-decimal persisted value; many-heights warning |
| `Source/Tools/Ceilings/CeilingHeatmapViewModel.cs` | Tolerance stepper, review row and summary removed |
| `Source/Tools/Ceilings/CeilingHeatmapSettings.cs` | `ElevTolerance` removed (load-compatible) |
| `Source/Framework/GlobalSettingsWindow.ToolGroups.cs` | Global Settings tolerance stepper removed |
| `Strings/en/ceilings.heatmap.json`, `Strings/en/globalSettings.json` | Tolerance strings removed; bucket wording updated |

Filter naming, the colour ramp, view handling and the Auto Filters trade registration are
otherwise unchanged, so a re-run updates existing filters in place (rewriting any rule an older
build left tolerance-snapped) and view assignments survive.

## Notes

- Cannot be compiled here (Linux; see CLAUDE.md "Build Environment") — needs a Windows build and
  a Revit run to confirm the rule values display as exact feet-inches.
