# Plan — Legend placement in Align Sheet Views

Add an option to Align Sheet Views that copies the reference sheet's **legend
placements** onto every target sheet: place the legend if the target does not carry
it, move the target's existing instance if it does.

---

## 1. Why legends are a separate pass, not another "inherited" property

The existing tool aligns *views* by computing a shared world anchor from each view's
crop box and converting it to a sheet coordinate through the view's own scale. None of
that applies to a legend:

| | Model view | Legend |
|---|---|---|
| Lives on | exactly one sheet | **any number of sheets, and more than once per sheet** |
| Has a crop box / world geometry | yes | no — nothing to anchor to |
| Footprint varies per sheet | yes (scale, crop, annotation crop) | **no** — the legend view's content and scale are properties of the view, so its drawn size is identical on every sheet |
| Missing on the target | reported, nothing to do | **can be created** (`Viewport.Create`) |

The last two rows are what make this tractable: because a legend's footprint is
identical everywhere, "same place on the target sheet" is a straight copy of the
source viewport's box centre — no scale maths, no crop prediction, no anchor
projection. So legends get their own capture / match / place path rather than being
bolted onto `MatchedPair`.

---

## 2. Bug this surfaces first — legends are not explicitly excluded today

`CaptureSheet` (`AlignSheetViewsEventHandler.cs:1401-1410`) filters viewports with:

```csharp
if (view.Scale <= 0) continue;                 // perspective / schedule / legend — no model scale
BoundingBoxXYZ cb = view.CropBox;
if (cb == null || cb.Transform == null) continue;
```

The comment asserts a legend has no model scale. A legend view **does** carry a
Scale parameter, so that guard is not what is excluding legends — at best the
`CropBox` guard is, and that is not something to rely on. If a legend does slip
through today it is eligible for matching against another legend (same `ViewType`,
parallel `ViewDirection`) and would be *moved by crop-anchor maths that means nothing
for a legend*.

**Fix as part of this change:** exclude `ViewType.Legend` explicitly in `CaptureSheet`,
so legends can never enter the view matcher, can never be reported as a missing/extra
*view*, and are owned solely by the new pass. This is a correctness fix regardless of
whether the checkbox is ticked.

---

## 3. UI — one checkbox in Step 3

Step 3 ("Options") currently has two sections: the fallback-overlap stepper and
`INHERIT FROM SOURCE VIEW`. Legends are sheet content, not a per-view property, so
they get their own section beneath it:

```
FALLBACK MATCH — MINIMUM OVERLAP (%)
  [ 50 ]  −  +

INHERIT FROM SOURCE VIEW
  [ ] Scope box assignment
  [ ] Crop size + annotation crop
  [ ] Grid extents, heads and elbows
  [ ] Crop region visibility

SHEET CONTENT
  [ ] Place legends from the reference sheet
```

- Built with the existing `OptionCheck` helper and `SectionLabel2` — no new control,
  no layout change, so this stays inside the established Step 3 stack.
- Off by default (every existing option is).
- Step 4 review gains a `Legends` row: *"Placed from the reference sheet"* / *"Not
  placed"*, and the review warning gains the clause *"…and legends will be placed or
  moved on every selected sheet"* when it is on.
- Step 3's collapsed summary appends `| legends`.
- A UI mockup goes to the user for approval before the code is written, per the
  project's WPF rule, and `/revit-navisworks-ui` is invoked first.

---

## 4. Behaviour

### 4.1 Which source sheet's legends

Legends come from the **same reference sheet the target's views were aligned to**
(`bestSource`), so a target never mixes geometry from one reference with legends from
another. When more than one reference is selected, the per-sheet roll-up already names
the chosen reference.

### 4.2 Matching legend → legend

Identity is the **legend view's `ElementId`** — "the exact legend", as requested.
Per legend view, on one sheet:

- Source has *n* instances, target has *m*.
- Pair them **globally best-first by distance** between source box centre and target
  box centre (the same greedy, best-first shape as `MatchSheet`'s overlap pass) so a
  legend already roughly in place maps to the source instance it is nearest to,
  rather than by list order.
- Leftover source instances (n > m) → **create**.
- Leftover target instances (m > n) → **leave and report**. Never delete.
- Legend views on the target that the source does not carry → **leave and report**.

### 4.3 Placing one legend

Order matters, because `GetBoxCenter()`/`GetBoxOutline()` both track the view title —
so two viewports' box centres are only comparable when their title state matches:

1. Unpin if pinned (record the original state).
2. `ChangeTypeId` to the source viewport's type **if it differs** — this is what
   governs whether a title is drawn at all. Logged when it changes.
3. Copy `Rotation`.
4. Copy `LabelLineLength`, then `LabelOffset`.
5. `SetBoxCenter(sourceBoxCentre)` — absolute, so a re-run is idempotent and a
   correction can never accumulate.
6. Restore the pin state.

For a legend the target does not have, step 1 becomes
`Viewport.Create(doc, targetSheetId, legendViewId, sourceBoxCentre)` (null-checked)
and the rest is unchanged.

### 4.4 Verification — no new regenerates

The tool was deliberately optimised down to one regenerate per sheet (grids only) plus
one for the whole run. **This adds none.** Legend writes happen inside the per-sheet
loop; the existing run-end `doc.Regenerate()` (`AlignSheetViewsEventHandler.cs:385`)
makes created viewports and title writes live; a legend verification pass runs beside
`VerifyPlacements`:

- Read source and target `GetBoxOutline()`.
- **Sizes equal (within tolerance)** → if the min corners differ, correct by the delta.
- **Sizes differ** → do *not* "correct". A centre that moved at a different size is the
  box growing around a stationary drawing, not a misplacement — report it and leave the
  legend where it is. (This is exactly the trap that silently moved every view by its
  own title height in an earlier version of this tool.)

---

## 5. Edge cases

### Ways a legend can appear
| # | Case | Handling |
|---|---|---|
| 1 | One instance on the sheet (normal) | Move or create |
| 2 | **Same legend placed twice on one sheet** | Instance-count reconciliation (§4.2) |
| 3 | Same legend on many sheets | Expected — legends are the only view type that can be |
| 4 | Legend viewport with / without a view title | Viewport type is copied, so the target matches |
| 5 | Title moved (`LabelOffset`) or resized (`LabelLineLength`) | Both copied before positioning |
| 6 | Viewport rotated on the sheet | `Rotation` copied before positioning |
| 7 | **Viewport pinned** | Unpinned, moved, pin state restored |
| 8 | **Keynote legend / schedule** | Not a viewport (`ScheduleSheetInstance`) — out of scope. If the reference sheet carries any, one line says so, so their absence is never a surprise |
| 9 | Revision schedule in the title block | Part of the title block; untouched, not reported |

### Source side
| # | Case | Handling |
|---|---|---|
| 10 | **Reference sheet has no legends** | Explicit "no legends on reference '<x>'" line — a silent empty pass is indistinguishable from a broken one |
| 11 | **Reference sheet has legends but no placeable views** | Today it is dropped as a source entirely (`sourceNoViews`, line 143). With the option on it is **kept** — it still scores 0 on views, so it is only ever chosen when nothing else matches, but a legend-only reference sheet becomes usable |
| 12 | Several references selected | Legends follow `bestSource` only (§4.1) |

### Target side
| # | Case | Handling |
|---|---|---|
| 13 | **Target has no placeable views** (`noPlaceableViews`, line 244) | Currently an early `continue`. Restructured so legends still run — legend placement does not depend on view alignment |
| 14 | **No view counterpart found** (`noCounterpart`, line 275) | Same restructure |
| 15 | Legend already in exactly the right place | No-op, counted as "already positioned", not logged per item |
| 16 | Legend present, wrong place | Moved |
| 17 | Legend absent | Created |
| 18 | Target has extra instances / extra legends | Left, reported, never deleted |
| 19 | `Viewport.CanAddViewToSheet` false | Reported, skipped |
| 20 | `Viewport.Create` returns null or throws | Treated as a failure and reported per legend — the rest of the sheet still runs |
| 21 | **Workshared, sheet or viewport owned by another user** | The write throws; caught per legend, reported by name, sheet continues |
| 22 | **Target title block a different size or placed at a different origin** | The legend is still placed at the source's sheet coordinate (consistent with how views are aligned), and a warning names any legend whose footprint lands outside the target's title-block bounding box |
| 23 | Target sheet has no title block | Containment check skipped silently — not an error |
| 24 | Placed legend overlaps an existing viewport on the target | Warned during the verification pass (outlines are being read there anyway) |

### Run mechanics
| # | Case | Handling |
|---|---|---|
| 25 | Cancellation mid-run | Legend work sits inside the per-sheet loop, so `RunState.CancelRequested` already covers it; queued run-end work still completes and is committed |
| 26 | Undo | Same single transaction as the alignment — one undo step |
| 27 | Re-running the tool | `SetBoxCenter` is absolute and creation is guarded by an existing-instance check, so a second run is a no-op |
| 28 | Static handler retains state | New per-run fields cleared in the existing `finally` |

### Deliberately not done
Deleting target legends the reference does not have (destructive, and a target sheet
legitimately carrying an extra legend is common); creating legend *views*; moving
schedules or keynote legends; editing legend content.

---

## 6. Reporting

Follows the tool's existing discipline — one roll-up line per sheet, detail only for
problems:

- Per sheet, when the option is on: one legend line, e.g.
  `[A-101] Legends: 2 placed, 1 moved, 1 already in place.`
  or the zero case `[A-101] Reference '<x>' has no legends — nothing to place.`
- Problems get their own lines (couldn't create, owned by another user, outside the
  title block, overlapping a viewport, size mismatch at verification).
- Run end: `Legends: N placed, M moved, K already in place, J left unchanged.`
- Legend successes and failures feed the existing `pass`/`fail` counters, so the
  final `Done — …` line stays truthful.

---

## 7. Files

**Changed**

| File | Change |
|---|---|
| `Source/Tools/Sheets/AlignSheetViews/AlignSheetViewsViewModel.cs` | `_placeLegends` field, the Step 3 `SHEET CONTENT` section, review row + warning clause, Step 3 summary, wire `handler.PlaceLegends` in `Run` |
| `Source/Tools/Sheets/AlignSheetViews/AlignSheetViewsEventHandler.cs` | `PlaceLegends` input; explicit `ViewType.Legend` exclusion in `CaptureSheet`; keep legend-only source sheets; restructure the two early `continue`s so legends still run; new `CaptureLegends` / `MatchLegends` / `PlaceLegend` / `VerifyLegendPlacements`; legend tallies in the roll-up; clear new state in `finally` |
| `Strings/en/testing.alignSheetViews.json` | New `labels.secSheetContent`, `labels.optLegends`, `review.itemLegends` + values, `inherit`-style summary token, and the new `log.*` lines |

**Added**

| File | Purpose |
|---|---|
| `plan-align-sheet-views-legends.md` | This plan |

No new controls, no new windows, no new settings-file keys (the checkbox is a per-run
choice, like every other option in this tool — nothing project-specific is persisted).

---

## 8. Decisions taken

1. **Placement coordinate basis** — **absolute sheet coordinates**, identical to how
   views are aligned, with a warning naming any legend whose footprint lands outside
   the target's title block. The title-block-origin-relative alternative was
   considered and rejected: it only helps when title blocks sit at different origins,
   and on a different-size title block it anchors bottom-left, which is wrong for a
   legend placed top-right.
2. **Title / viewport-type mirroring** — **copy** the source viewport's type,
   `LabelLineLength` and `LabelOffset` before positioning, so the overlay is exact.
   Logged whenever the type actually changes.
3. **Base branch** — `claude/legend-placement-function-g0f43c`, which already exists
   and carries the current main tip (PRs #131 and #132).
4. **Legends stay decoupled from view matching** — a target sheet that matched no
   views still receives its legends (§5 items 11, 13, 14).

---

## 9. As built — deltas from the plan

Three things the implementation added that the plan did not call for. All came out of
the post-change silent-failure scan.

1. **A partial read of a target sheet's legends now skips that sheet.** `CaptureLegends`
   reports whether it read the sheet in full. It matters more than a normal swallowed
   error: an incomplete read is indistinguishable from "this sheet does not have the
   legend", and this pass answers a missing legend by *creating* one — so swallowing it
   would have silently duplicated legends on top of the ones already there. The sheet is
   left untouched and reported instead. On the reference side a partial read is only
   lossy (a legend never gets copied), so it warns and carries on.
2. **The viewport-type change is only reported for a legend that already existed.** A
   newly created viewport is born with the document's default type, so adopting the
   reference's is part of placing it — reporting that would have put a warning line
   against every legend placed on every sheet, which is the common case.
3. **Two diagnostics-only paths were promoted to the run log**: a refused view-title
   write (the view path already warned for the same failure) and a legend whose outline
   could not be read at verification time — the latter is a placement that was never
   checked, which a silent `continue` would have made look like a clean verification.
