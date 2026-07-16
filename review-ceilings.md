# Function Review — Ceilings Group (Ceiling Heatmap + Ceiling Grids)

Produced by the eight-pass framework in `plan-function-review-framework.md`.
Static analysis only — nothing has been changed. Every finding is tagged
**Confirmed** (provable from the code) or **Needs Windows test** (test script in
the appendix). Approve findings by number and they land on this branch.

**Tools reviewed** (the whole Ceilings ribbon flyout):

| Tool | Files |
|---|---|
| **Ceiling Heatmap** | `Source/Tools/Ceilings/CeilingHeatmap{EventHandler,ViewModel,Settings,DebugHandler}.cs`, `CeilingColorRamp.cs`, `Strings/en/ceilings.heatmap.json` |
| **Make Ceiling Grids** | `MakeCeilingGrids{ViewModel,RunHandler,Phase1Handler,Settings}.cs`, `Strings/en/ceilings.makeGrids.json` |
| **Project Grids** | `ProjectedCeilingGridsViewModel.cs`, `Strings/en/ceilings.projectGrids.json` |
| **Reproject Grids** | `ReprojectCeilingGridsViewModel.cs`, `Strings/en/ceilings.reprojectGrids.json` |
| **Shared grid engine** | `CeilingGridEventHandler.cs`, `CeilingGridHelpers.cs`, `Strings/en/ceilings.grids.json` — findings prefixed `CGE` apply to both Project and Reproject |

Counts: **0 Critical · 9 High · 12 Medium · 14 Low** (one High flagged
borderline-Critical). Key-completeness check passed: every
`AppStrings.T("ceilings.*")` key referenced in the group's `.cs` files exists in
its JSON, and no ceilings JSON key is orphaned.

---

## Ceiling Heatmap

### CH-7-1 — High — Cancel mid-scan still destroys the existing heatmap — Confirmed
`CeilingHeatmapEventHandler.cs:105` (scan cancel), `:169` (delete), `:285` (trade rebuild)
Cancelling during Phase 1 logs "Stopped by user — work so far preserved", `break`s
— and then the run **continues**: Phase 3 `DeleteHeatmapFilters` (which has no
cancel check) deletes every existing `CH_` filter from every view, Phase 4's loop
breaks immediately on the still-set cancel flag having created ~0 filters, and
`RegisterCeilingHeatmapTrade` rebuilds the CH trade from the partial (possibly
empty) bucket list, wiping the previous rules. Net effect: pressing Cancel during
the scan deletes the user's current heatmap and replaces it with nothing, while
the log claims work was preserved.
**Fix:** after any cancel-break before Phase 3, `return` (skip delete / create /
trade rebuild / tags). Cancel during Phase 4 should keep the created filters but
skip the trade `Rules.Clear()` rebuild, or rebuild from `chRules` only when the
loop ran to completion.

### CH-7-2 — High — Whole-inch filter names collide for sub-inch buckets → ceilings silently uncolored — Confirmed (visual check: test W1)
`CeilingHeatmapEventHandler.cs:886` (`FormatFtIn`), `:207-234`
`FormatFtIn` rounds to whole inches, but the default bucket tolerance is 1/8".
Two distinct buckets less than ~1" apart (e.g. 10.00' and 10.03' → both
"10'-0\" AFF") produce the **same filter name**. The second bucket then hits the
`existingFilters.TryGetValue` reuse path — but the reused filter's equals-rule
only matches ±tolerance around the *first* bucket's value, so every ceiling in
the second bucket matches no filter and stays uncolored. The log reports it as a
success ("reused"). Also loses a ramp color (two buckets, one color).
**Fix:** make the filter name collision-proof — append the raw offset when the
rounded name is already taken (e.g. `10'-0" AFF (10.0300')`), or format to the
nearest 1/8" instead of the nearest inch.

### CH-8-1 — High — "Place ceiling tags" toggle describes the opposite of what the run does — Confirmed
`Strings/en/ceilings.heatmap.json:48` vs `CeilingHeatmapEventHandler.cs:347-356`
The toggle description says *"Ceilings that already carry a tag are skipped."*
The code does the reverse: it **deletes every existing ceiling tag in the view**
(including tags the user placed and positioned by hand) and re-tags everything at
the centroid. A user who manually adjusted tag positions loses that work with no
warning — the only hint is the post-run "N existing removed and replaced" line.
**Decision needed:** either (a) fix the text to say tags are replaced (and
ideally add it to the review-step note since it's destructive), or (b) change the
code to actually skip already-tagged ceilings. (a) is the smaller change; (b)
matches what the UI has been promising.

### CH-4-1 — High — Color-ramp save/load/delete failures are discarded — Confirmed
`CeilingHeatmapViewModel.cs:375` (`Save(name, ramp, out _)`), `:385` (Load error
discarded), `:406` (`Delete(info, out _)`)
`TemplateStore.Save/Load/Delete` return `bool` + an `out string? error`; all
three call sites throw both away. A failed save (locked file, full disk) clears
the name box and refreshes the combo exactly like a success — the user believes
the ramp was saved. A failed load silently does nothing.
**Fix:** check the `bool`; on failure surface the error (status text near the
row or a `DiagnosticsLog.Warn` + visible message). No exception plumbing needed
— the store already hands back the message.

### CH-3-1 — High — Headline result count mixes filter buckets into the "tags" total — Confirmed
`CeilingHeatmapEventHandler.cs:276` (`pass++` per bucket), `:442` (`pass += tagPlaced`),
`CeilingHeatmapViewModel.cs:27` (`ResultNoun => "tags"`)
`pass` is incremented once per height bucket in Phase 4 and then has the tag
count added in Phase 5, but the run strip labels the total "tags". A run with 12
buckets and 300 tags reports "312 tags". With tags off it reports "12 tags"
having placed none. The chips row is correct; the headline number lies.
**Fix:** stop counting buckets into `pass` (the chips already carry
filters/tags separately), or make `ResultNoun` neutral ("items") — pick one and
mirror whichever convention the other tools use.

### CH-1-1 — Medium — Tolerance stepper allows 0 — Confirmed (rule-match behavior: test W2)
`CeilingHeatmapViewModel.cs:564-571` (`MinValue = 0`)
At 0, `AddBucket` requires exact double equality, so near-identical offsets
explode into one bucket per floating-point value (hundreds of filters on a real
model), and `CreateEqualsRule(heightParamId, offset, 0)` will practically never
match a stored double — filters get created but color nothing.
**Fix:** floor `MinValue` at one step (0.25") or clamp `_elevTolerance` to a tiny
positive epsilon before the run, mirroring the annotation-crop floor rule.

### CH-1-2 — Medium — `Run` dereferences a nullable handler with `!` — Confirmed
`CeilingHeatmapViewModel.cs:674,681,696` vs `MakeCeilingGridsViewModel.cs:497-502`
The constructor accepts `CeilingHeatmapEventHandler?`/`ExternalEvent?` but `Run`
uses `_handler!`/`_event!`. If registration failed, this NREs inside the run
click. Make Ceiling Grids guards the same situation with a
`runHandlerMissing` log + `onComplete(0,1,0)` — Heatmap should match.

### CH-7-4 — Medium — Reused filters keep their old tolerance — Confirmed
`CeilingHeatmapEventHandler.cs:211-214`
With "Delete existing" off, a bucket whose name already exists reuses the filter
as-is; `SetElementFilter` is never called, so a tolerance changed in the UI has
no effect on reused filters (they keep the rule minted under the old tolerance).
Overrides are refreshed, the rule is not.
**Fix:** on reuse, rebuild the rule via `SetElementFilter` (update-in-place per
the ParameterFilterElement lifecycle rule — keeps the ElementId and view links).

### CH-7-3 — Medium — Bucket anchors are scan-order dependent and adjacent rules can overlap — Needs Windows test (W3)
`CeilingHeatmapEventHandler.cs:782-787`
`AddBucket` keeps the first-seen value as the bucket anchor, so the same model
scanned with a different view selection/order can anchor buckets at different
values (different filter names, re-created filters on re-run). And two buckets
between 1× and 2× tolerance apart both match ceilings in the overlap zone — the
view's filter order silently decides the color.
**Fix candidate:** snap anchors to a tolerance grid (`Math.Round(offset/tol)*tol`)
so anchors are order-independent and non-overlapping.

### CH-3-2 — Medium — Per-view filter-apply failures don't say which view, and aren't counted — Confirmed
`CeilingHeatmapEventHandler.cs:269-272`
The catch logs `"Error applying filter to view: {0}"` with only the exception
message — across 30 views you can't tell which failed (typical thrower: a view
whose filters are template-controlled). It also logs with "fail" status but
never increments `fail`, so the completion counts don't reflect it.
**Fix:** include `vp.Name` in the message and count the failure once per view.

### CH-4-2 — Medium — Generated-RCP rename/template failures never reach the run log — Confirmed
`CeilingHeatmapEventHandler.cs:959,967`
In generate mode, a name conflict (view keeps Revit's generated name) and a
template-apply failure (wrong view type, dependent view) go only to
`DiagnosticsLog.Swallowed`. The user asked for a specific suffix and template;
getting neither should be a "warn" line in the run log, not just diagnostics.

### CH-5-1 — Medium — Filter deletion is filters × all-views `GetFilters()` — Confirmed
`CeilingHeatmapEventHandler.cs:699-709`
`DeleteHeatmapFilters` loops every project view inside every filter, calling
`v.GetFilters()` each time — with 40 buckets and 500 views that's 20,000
collector calls before the delete. Invert it: per view, call `GetFilters()`
once, remove the intersection with the heatmap set. Same pattern in Make Ceiling
Grids (MCG-5-1).

### CH-5-2 — Medium — Apply loop re-reads each view's filter list per bucket — Confirmed
`CeilingHeatmapEventHandler.cs:263`
Phase 4 builds a fresh `HashSet` from `vp.GetFilters()` for every bucket × view
pair. Cache each view's filter set once before the bucket loop and update it as
filters are added — buckets × views collector calls become views.

### CH-1-3 — Low — Level rows print a raw double elevation — Confirmed
`CeilingHeatmapViewModel.cs:243`, `Strings/en/ceilings.heatmap.json:30`
`levelRow` formats `{1}` from `l.ElevationFt` unformatted — a level at 10.1'
renders "10.099999999999999 ft". Format `F2` (or ft-in via the existing
`FormatFtIn`) before substitution.

### CH-1-4 — Low — "Delete existing" is the one run option not persisted — Confirmed
`CeilingHeatmapSettings.cs` persists colors, tolerance, PlaceTags;
`_deleteExisting` always resets to true. If deliberate (safety default), fine —
say the word and this is dropped.

### CH-3-3 — Low — Linked-ceiling scan count double-counts across views — Confirmed
`CeilingHeatmapEventHandler.cs:127`
A link visible in N selected views is scanned N times, so "Scanned X linked
ceiling(s)" inflates by up to N×. Buckets dedupe fine; only the log number is
wrong.

### CH-4-3 — Low — Tag falls back to the model origin silently — Confirmed
`CeilingHeatmapEventHandler.cs:564`
`GetTagPoint` returns `XYZ.Zero` when geometry and bounding box both fail — the
tag is placed at (0,0,0), far from its ceiling, with no log line. Skip-and-log
instead of tagging the origin.

### CH-6-1 — Low — Run payload cleared outside `finally` — Confirmed
`CeilingHeatmapEventHandler.cs:74-75`
The per-run lists are cleared after the try/catch rather than in `finally`
(Make Ceiling Grids uses `finally`). Works today because the catch swallows, but
the memory-discipline rule says `finally` — trivial alignment.

### CH-6-2 — Low — Debug harness wiring is dead — Confirmed
`App.cs:203-205`, `CeilingHeatmapViewModel.cs:104-112`
`CeilingHeatmapDebugHandler` + its ExternalEvent are created every session and
registered into the ViewModel's statics, but nothing ever calls
`_debugEvent.Raise()` — the 320-line diagnostics handler is unreachable. Per the
debugger-harness rule: repoint it at the Developer panel or remove it.

---

## Make Ceiling Grids

### MCG-7-1 — High — Cancel mid-run wipes the existing hide-filter set — Confirmed
`MakeCeilingGridsRunHandler.cs:110-114` (Phase 1 cancel), `:171` (delete), `:178` (trade)
Same class as CH-7-1: cancelling during Phase 1 breaks the view loop, then Phase
2 still runs `DeleteHideFilters` (no cancel check — deletes every `CG_` filter
project-wide), `CreateAndApplyHideFilters` breaks immediately with zero rules,
and `RegisterHideTrade` clears the CG trade's rules and saves an empty set. A
cancel intended to stop work destroys the previous run's hide filters.
**Fix:** bail out before Phase 2 when cancel was requested during Phase 1; skip
the trade rebuild when the create loop was cancelled.

### MCG-1-1 — High — The required "Select Documents" step doesn't scope the run — Confirmed
`MakeCeilingGridsRunHandler.cs:21-22` (set at `MakeCeilingGridsViewModel.cs:522-523`,
then only cleared at `:62-63` — never read)
`IncludeHost` and `LinkInstIds` on the **run** handler are dead inputs: the run
always creates one RCP per **host** level and relies on "By Host View" cascade,
regardless of which documents were checked. Only the Phase-1 *type scan* honors
the selection. So Step 1 reads as "which models this run covers" but actually
only controls which ceiling types are listed in Step 2 — unchecking the host
document changes nothing about the created views or visible ceilings.
**Decision needed:** either (a) delete the dead properties and retitle/annotate
Step 1 so it's honestly "which models to list ceiling types from", or (b) make
the run actually consume the selection (e.g. hide all ceilings of unselected
documents via per-link overrides). (a) is honest and small; (b) is a behavior
change worth its own plan.

### MCG-3-1 — Medium — Hide-filter apply failures don't name the view — Confirmed
`MakeCeilingGridsRunHandler.cs:299-302`
Same as CH-3-2: `"Error applying hide filter to view: {0}"` carries no view
identity and doesn't bump `fail`.

### MCG-5-1 — Medium — `DeleteHideFilters` is filters × all-views `GetFilters()` — Confirmed
`MakeCeilingGridsRunHandler.cs:338-348`
Same inversion fix as CH-5-1.

### MCG-1-2 — Low — Output folder validated as non-empty only — Confirmed
`MakeCeilingGridsViewModel.cs:452`
No existence/creatability check until export time (Phase 3 then fails per view).
`EnsureDir` creates missing folders so this mostly self-heals; an invalid drive
or illegal chars fails N times instead of once at review. Cheap pre-check
possible; Low because failures are logged.

### MCG-2-1 — Low — Any document-selection change discards all type exclusions — Confirmed
`MakeCeilingGridsViewModel.cs:168-175`
Adding one more link wipes `_excludedTypeKeys` entirely and forces a full
re-scan + re-exclusion. Exclusions are name-based, so they could survive the
re-scan via the existing `IntersectWith` reconciliation (`:312`) instead of
`Clear()`.

### MCG-3-2 — Low — The "Created: {0}" no-export branch is unreachable — Confirmed
`MakeCeilingGridsRunHandler.cs:212-215`
`IsValid("export")` requires a non-blank folder, so `OutputFolder` is never
empty at run time; the `else` branch (create views without exporting) is dead.
Either remove it or (if create-only is a real workflow) make the folder optional
— currently the required folder forces a DWG export the user may not want.

### MCG-4-1 — Low — DWG export silently overwrites existing files — Confirmed
`MakeCeilingGridsRunHandler.cs:493-501`
`doc.Export` overwrites `<name>.dwg` without warning. Re-runs into the same
folder are presumably the workflow (refresh exports), so probably fine — but the
log could say "overwrote" vs "created" so the run reads truthfully.

### MCG-4-2 — Low — Phase-1 scan errors lose the stack — Confirmed
`MakeCeilingGridsPhase1Handler.cs:80-83`
`OnError?.Invoke(ex.Message)` surfaces the message in the step UI but never
routes the exception through `DiagnosticsLog.Error`, so diagnostics has no trace
of a scan failure.

---

## Project Grids

### PG-1-1 — Medium — Single-file mode never checks the active view — Needs Windows test (W4)
`CeilingGridEventHandler.cs:47` (`RunProject(doc, doc.ActiveView, …)`)
The tool projects into whatever view is active. Run from a 3D view, sheet, or
schedule the collector/import behavior is untested — best case a confusing "No
ceiling elements in view" fail, worst case an import lands in an unusable view.
The review step says "Target view: Active view" but never states *which* view
that is, and nothing validates it's a ceiling plan.
**Fix:** capture the active view's name/type at window-open on the main thread,
show it in the review ("Active view: L1 - Ceiling"), and fail fast with a clear
message when it isn't a plan.

### PG-1-2 — Low — File path not validated for existence — Confirmed
`ProjectedCeilingGridsViewModel.cs:200-206`
Batch mode checks `Directory.Exists`; single mode only checks non-blank. A
deleted/mistyped file fails at run with the import error — works, but the
sibling check is inconsistent. Add `File.Exists`.

---

## Reproject Grids

### RG-7-1 — High (borderline Critical) — Cancel between delete and re-create loses grid curves; the cancel log claims preservation — Confirmed
`CeilingGridEventHandler.cs:332-333` (delete), `:340-346` (cancellable create loop), `:364` (commit)
`RunReproject` deletes **all** matched source curves up front, then recreates
them in a second loop that honors `RunState.CancelRequested`. Cancelling during
the create loop breaks out and **commits** — every deleted curve not yet
recreated is gone, and the log says "work so far preserved." A creation-failure
streak has the same shape (deleted originals, failed replacements, still
commits). Revit undo can restore it (hence not flat Critical), but a user who
saves/syncs before noticing has lost the grids. In batch mode this repeats per
view.
**Fix options:** (a) delete each curve only after its replacements are created
(pair the delete with the create per source curve); (b) on cancel inside the
create loop, `RollBack()` this view's transaction instead of committing and log
"view X rolled back". (a) preserves partial progress honestly; (b) is simpler.

---

## Shared grid engine (Project + Reproject)

### CGE-4-1 — High — A curve that fails both creation paths still counts as a success — Confirmed
`CeilingGridHelpers.cs:185-203`, callers at `CeilingGridEventHandler.cs:226-236,348-357`
`TryCreateModelCurve` swallows the model-curve failure in a bare `catch` (no
logging at all — forbidden pattern), falls back to a detail curve, and swallows
*that* failure too (logged to diagnostics only). It never throws — so the
caller's `catch` is unreachable, `pass++` runs unconditionally, and `fail` can
never be incremented by curve creation. A run where every curve failed reports
"Complete — N curve(s) created" with N = the full count. The silent
detail-curve fallback also changes the output kind (view-only annotation instead
of a model curve) with no run-log mention.
**Fix:** return an enum (`ModelCurve` / `DetailCurve` / `Failed`) from
`TryCreateModelCurve`, log the first failure through `DiagnosticsLog.Swallowed`,
count `Failed` into `fail`, and summarize the fallback count ("N created as
detail curves — sketch plane rejected").

### CGE-3-1 — Medium — Per-view completion lines report the cumulative total — Confirmed
`CeilingGridEventHandler.cs:246,366` (uses `ref pass`)
In batch mode, "Complete — {0} model curve(s) created" prints the running total
across all views/DWGs processed so far, not this view's count — view 3's line
includes views 1–2's curves. Track a per-view delta (`pass - passBefore`) for
the per-view line; keep the cumulative for the batch summary.

### CGE-3-2 — Low — Progress bar jumps backward in batch mode — Confirmed
`CeilingGridEventHandler.cs:121` vs `:239`
The outer batch loop reports `done*90/total` while the inner per-view run
reports its own `done*90/total` — each new DWG snaps the bar back toward 0.
Scale the inner progress into the outer item's slice.

### CGE-7-1 — Low — Dead "preselected" legacy path and stale mode not cleared — Confirmed
`CeilingGridEventHandler.cs:19,22,257`
Nothing sets `ReprojectMode = "preselected"` or `PreSelectedIds` anymore — the
branch at `:257` is dead legacy support. `DwgPath`/`BatchDwgFolder`/
`ReprojectMode` also aren't reset in `finally` (the lists are); strings are
harmless memory-wise but the mode is one forgotten assignment away from a
stale-state bug. Remove the dead path or clear everything.

---

## String findings (Pass 8) — current vs. proposed

The behavior-mismatch item (CH-8-1) is listed above as a code/text decision, not
here. All keys verified present; table covers tone/accuracy only.

| Key | Current | Proposed |
|---|---|---|
| `ceilings.makeGrids.log.raising` | "Raising Revit external event…" | "Starting run in Revit…" |
| `ceilings.projectGrids.log.raising` | "Raising Revit external event…" | "Starting run in Revit…" |
| `ceilings.reprojectGrids.log.raising` | "Raising Revit external event…" | "Starting run in Revit…" |
| `ceilings.grids.log.dwgImportFalse` | "DWG import returned false — check the file path." | "Revit couldn't import the DWG — check the file path and format." |
| `ceilings.grids.log.noSoffit` | "Could not extract soffit faces." | "Couldn't find any ceiling soffit faces to project onto." |
| `ceilings.heatmap.labels.levelRow` | `"{0}  ({1} ft)"` (raw double) | `"{0}  ({1} ft)"` with the elevation pre-formatted to 2 decimals (code change, CH-1-3) |
| `ceilings.heatmap.summaries.s2Tol` | "Tol {0} in" | "Tolerance {0} in" |
| `ceilings.makeGrids.log.rcpFailed` | "Could not create RCP for {0}." | "Couldn't create a ceiling plan for level {0}." |
| `ceilings.heatmap.log.noRcpType` | "No Ceiling Plan view family type found in this document." | "No ceiling plan view type exists in this project — create one in Revit first." |
| `ceilings.makeGrids.log.noRcpType` | "No Ceiling Plan view family type found in this project." | (same rewrite as above, so the two tools match) |

Cross-tool consistency notes:
- Run labels differ across siblings: "Apply Heatmap →" / "Create in Revit →" /
  "Run in Revit →" ×2. Fine if intentional; flag only.
- `ceilings.heatmap.log.filterApplyError` and
  `ceilings.makeGrids.log.filterApplyError` should both gain a `{view}` slot when
  CH-3-2 / MCG-3-1 are fixed.

---

## Appendix — Windows test scripts

**W1 — CH-7-2 filter-name collision (visual confirmation).**
In a test model, place two ceilings on one level with Height Offsets 10' 0" and
10' 3/8" (0.375" apart — more than the 1/8" tolerance, less than 1"). Run the
heatmap on that RCP with default tolerance. *Expected bug:* log says
"1 filter(s) created, 1 reused" (or similar), only one `CH_10'-0" AFF` filter
exists, and the 10' 3/8" ceiling stays uncolored. *Fixed behavior:* two filters,
both ceilings colored.

**W2 — CH-1-1 zero tolerance.**
Set Elevation Tolerance to 0 and run on a floor with many ceilings at nominally
equal offsets. Watch the bucket count in the log ("Found N distinct
height-offset bucket(s)") and whether ceilings actually color. *Expected bug:*
bucket count far above the real distinct-height count and/or ceilings uncolored
because the 0-epsilon equals rule never matches.

**W3 — CH-7-3 overlap/order dependence.**
Ceilings at 10' 0", 10' 3/16", 10' 3/8" with 1/4" tolerance. Run twice with the
view selection ordered differently (if multiple views) or after re-running with
Delete existing on. Check whether bucket names/anchors shift between runs and
which color the middle ceiling takes.

**W4 — PG-1-1 wrong active view.**
Open Project Grids with (a) a 3D view active, (b) a sheet active, (c) a floor
plan (not RCP) active; run single-file mode with a valid DWG each time. Record
what the log reports and whether anything was imported/left behind in the model
(check for orphaned ImportInstances). This decides how strict the fail-fast
check needs to be.

**W5 — RG-7-1 cancel data loss.**
In a throwaway model: RCP with ~50 grid model curves over ceilings. Run
Reproject and hit Cancel while the "new curves" phase is ticking (watch for the
5% progress lines). *Expected bug:* fewer curves in the view after the run than
before, log ends with "Stopped by user — work so far preserved." Confirm Ctrl+Z
restores them (single undo step per view).
