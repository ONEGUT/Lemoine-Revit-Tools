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

## Fix — tolerance groups, it never rewrites the number

### 1. `CeilingHeatmapEventHandler.cs` — replace snap-to-grid with real-value clustering

- Scan phase collects **exact observed heights** into a `HashSet<double>` (values read from
  the same parameter are bit-identical, so exact dedupe is correct). `AddBucket` is
  replaced by a plain `observed.Add(hParam.AsDouble())` at both call sites (lines 140, 966).
- After the scan, sort the distinct values and cluster greedily, anchored on each cluster's
  minimum so cluster width can never exceed `tol` (no single-linkage chaining):

  ```csharp
  foreach (double v in sorted)
      if (clusters.Count > 0 && v - clusters[last].min <= tol) clusters[last].max = v;
      else clusters.Add((min: v, max: v));
  ```

  Sorted input makes this fully deterministic and order-independent — the property the
  current grid snap was introduced to guarantee is preserved.
- Each bucket carries `(min, max)`. The rule becomes
  `CreateEqualsRule(paramId, (min+max)/2, (max-min)/2 + 1e-6)`.
  **The floor is added, not `Math.Max`'d** — a randomized sweep over 4000 synthetic models
  caught the difference: `(min+max)*0.5` is itself rounded, so an endpoint can sit up to an
  ulp further from the midpoint than the exact half-width, and with `Math.Max` a bucket's own
  outermost ceiling landed just outside its window and matched no filter at all.
  **When every ceiling in a bucket shares one height (the normal case) `min == max`, so the
  filter carries the exact real value — 10'-8" stays 10'-8".** The 1e-6 ft floor
  (0.000012") is far below any display rounding and far above double noise.
- Adjacent clusters are provably disjoint (`nextMin > prevMin + tol >= prevMax`), so the
  buckets still tile the elevation axis without overlap, and every scanned ceiling now sits
  strictly *inside* its own bucket's window rather than on its boundary — fixing defect (1).
- `chRules` stores the representative value; widen its format from `"0.######"` to
  `"0.#########"` so the persisted rule string round-trips without truncation.

### 2. `CeilingHeatmapViewModel.cs` — quarter-inch increments only

Per the user's follow-up: the tolerance is a fractional-inch quantity in **quarter-inch
steps, 1/4" minimum, 1/4" default**, up to 12".

- `MinValue = 0.25`, `Step = 0.25`, `MaxValue = 12`, `Decimals = 2`. Every legal value is an
  exact 2-decimal number, so it survives `InlineStepper.CommitValue`'s round-to-`Decimals`
  untouched — which is what the old 1/8" default did not (0.125 banker's-rounded to 0.12 in
  the box, and the first commit silently persisted 0.12" as the tolerance).
- `NormalizeToleranceInches` clamps and snaps to a quarter, applied in three places: on load
  (migrating a settings file from the old 1/8" default), on the stepper's `ValueChanged`
  (the centre field is typeable, so the snapped value is written back to the box), and once
  more before the run.
- This supersedes the earlier "allow tolerance = 0" idea — the 1/4" floor stays, and it is no
  longer needed: with the clustering fix the tolerance never distorts a reported height, so
  turning it off is not required to get exact numbers.
- The settings default (`CeilingHeatmapSettings.ElevTolerance`) moves from `1.0/96.0` (1/8")
  to `1.0/48.0` (1/4").

### 3. Run-log honesty

- A model with widely varied ceiling heights can still yield a great many filters. Keep the
  existing `foundBuckets` line and add a `warn` when the bucket count exceeds 60, naming the
  count and the tolerance, so a filter explosion is visible rather than a surprise in the
  browser.

### 4. `Strings/en/ceilings.heatmap.json`

- Reword `labels.tolHint` to state the corrected semantics: ceilings within this range
  share one colour, and the filter reports a real ceiling height — 0 means group only
  ceilings at exactly the same height.
- Add the new bucket-count warning key.

## Files touched

| File | Change |
|---|---|
| `Source/Tools/Ceilings/CeilingHeatmapEventHandler.cs` | Replace `AddBucket` snap with sorted real-value clustering; `BuildBucketFilter` takes `(min, max)`; drop the `1/96` fallbacks; widen the persisted rule format; bucket-count warning |
| `Source/Tools/Ceilings/CeilingHeatmapViewModel.cs` | Tolerance stepper `Decimals`/`Step`/`MinValue`; 4-decimal seed; remove the zero-tolerance reset |
| `Strings/en/ceilings.heatmap.json` | Reworded tolerance hint + new warning string |

No change to filter naming (`FormatFtIn`), the colour ramp, view handling, or the Auto
Filters trade registration beyond the value written into each rule. Existing filters are
matched by name as before, so a re-run updates them in place and view assignments survive.

## Notes

- Cannot be compiled here (Linux; see CLAUDE.md "Build Environment") — needs a Windows build
  and a Revit run to confirm the rule values display as exact feet-inches.
- UI change is a property tweak on an existing `InlineStepper` (no layout change), so
  `/revit-navisworks-ui` will be invoked before writing it, but no mockup render is
  warranted — nothing moves on screen.
