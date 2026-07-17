# Function Review — Dimensioning Panel (Clash Detection & Dimensioning)

Second application of `plan-function-review-framework.md`. Scope: the entire
Dimensioning ribbon panel — **Clash Definitions**, **Clash Finder & Dimension**,
**Clash Finder & Elevation Tag**, and **Refine Dimensions** — plus the shared
support code they all run on (`ClashEngine`, `ClashShared/*`, the whole
`AutoDimension/*` pipeline, `ElevationTag/ElevationTagRunner`). ~12,000 lines
across 53 files, all read front to back.

Per the framework: **nothing has been changed** — this is the findings report.
Shared-file findings are numbered `Shared-…` and attributed to the group.

## File inventory

| Tool | Files |
|---|---|
| **Clash Definitions** | `ClashDefinitions/{ClashDefinition,ClashDefinitionsSettings,ClashGroupEditor}.cs`, `Windows/ClashDefinitions/ClashDefinitionsWindow.xaml(.cs)`, `Commands/Dimensioning/OpenClashDefinitionsCommand.cs`, `Strings/en/clashDefinitions.json` |
| **Clash Finder & Dimension** | `ClashFinder/{ClashFinderViewModel,ClashFinderEventHandler}.cs`, `Commands/Dimensioning/ClashFinderCommand.cs`, `Strings/en/clash.finder.json` |
| **Clash Finder & Elevation Tag** | `ClashElevationFinder/{ViewModel,EventHandler}.cs`, `ElevationTag/ElevationTagRunner.cs`, `Commands/Dimensioning/ClashElevationFinderCommand.cs`, `Strings/en/clash.elevationFinder.json` |
| **Refine Dimensions** | `AutoDimension/Refine/{ViewModel,EventHandler}.cs`, `Commands/Dimensioning/RefineDimensionsCommand.cs`, `Strings/en/clash.refineDimensions.json` |
| **Shared** | `ClashFinder/ClashEngine.cs`, `ClashShared/*` (tag/callout schemas, group spec, settings, pick handler), `AutoDimension/*` (engine, runner, commit, config, clusterers, chainer, resolvers, pickers, owner schema, Core layout math) |

## Summary

> **Status: ALL FINDINGS FIXED** on this branch (user approved "fix it all").
> Decision items were resolved as: two-click confirm for definition delete,
> per-view marking transactions for the Elevation Finder, tolerance edited in
> inches (stored mm unchanged), and `SourcesExplicit` flag for the
> source-document semantics (empty stays "all docs" for existing files). The
> `DiagnoseSlabEdge` diagnostics and grouping/density tuning summaries stay
> hardcoded by policy (developer diagnostics). Windows-test items W1–W5 below
> still need a Revit run to verify the fixes behave as intended.

| Severity | Count |
|---|---|
| Critical | 0 |
| High | 3 |
| Medium | 13 |
| Low | 14 |

Read these three first:

1. **Shared-4-1** — an element that lands in *both* clash groups clashes with
   itself (and identical groups double-report every pair). Any same-trade
   definition (pipe-vs-pipe) produces a false marker on every element.
2. **Defs-1-1** — unchecking **every** source document in a clash group
   silently means "scan **all** documents", the exact opposite of what the UI
   shows.
3. **Refine-3-1** — Refine's run log over-reports "prior dimension(s) replaced"
   (stale deletions are counted twice).

---

## Shared support (ClashEngine, ClashShared, AutoDimension pipeline)

### Shared-4-1 — Self-clash / duplicate pairs when the groups overlap
**High · Confirmed** · `ClashEngine.cs:1024-1068` (`FindClashes`)

The pair loop `group1 × group2` has no identity guard. If the same element is
selected by both groups (same document, same `ElementId`) — trivially true for
any same-category definition like pipe-vs-pipe — the boolean intersection of
its solid with itself has full volume, so **every such element is reported as a
clash with itself** and gets a marker. Additionally, when both groups contain
elements A and B, the pairs (A,B) and (B,A) are both emitted — every real clash
is marked twice, and both burn against `MaxClashes`.

*Fix:* in the inner loop, `continue` when `g1.Doc.Equals(g2.Doc) && g1.Id ==
g2.Id`; dedupe unordered pairs by a `HashSet<(long docKey, long idA, long
idB)>` with the ids canonically ordered. *Windows test W1.*

### Shared-4-2 — Bare `catch` blocks not routed through DiagnosticsLog
**Medium · Confirmed**

`ClashEngine.cs` has seven: 571 and 635 (collector per category — a throwing
category vanishes from the scan), 982 (`GetHostBBox`), 1044 (boolean op —
counted and summarized in the log, the most defensible one), 1110
(`get_Geometry`), 1123 (`SolidUtils.CreateTransformed`), 1141 (union fallback).
Also the four launch commands' `catch { _window = null; }`
(`Commands/Dimensioning/*.cs:38-39`). CLAUDE.md requires
`DiagnosticsLog.Swallowed(context, ex)` for every deliberate swallow. These are
per-element hot paths, so a per-element `Swallowed` may flood — an acceptable
alternative is a per-run counter reported once (the `booleanFails` pattern
already used at 1044/1062).

### Shared-4-3 — Unparseable category tokens silently narrow the scan
**Medium · Confirmed** · `ClashEngine.cs:622` (`ScanCategories`), `:550`
(`ScanRules`)

`Enum.TryParse<BuiltInCategory>` failure just `continue`s. A definition saved
with a category token that a future Revit renames (or a hand-edited XML) scans
*fewer* categories with no run-log line. `ScanRules` logs only the
all-categories-failed case. *Fix:* log "category '{0}' not recognized —
skipped" per failed token (once per token).

### Shared-4-4 — Pick handlers' `PushLog` is never wired: pick failures are invisible
**Medium · Confirmed** · `ClashPickEventHandler.cs:56`,
`SlabPickEventHandler.cs:49,59`

No caller ever assigns `PushLog` on either handler (grep confirms), so
"Element pick failed: …", "Slab pick failed: …" and the externalized
`clash.finder.log.pickedNotFloor` are all no-ops. From the user's side: click
Pick, click an element, nothing happens, no explanation. *Fix:* Clash
Definitions window — surface via a status text/`TaskDialog` (it has no run
log); Clash Finder wizard — pass the step-flow log sink when raising the slab
pick.

### Shared-7-1 — One throwing view aborts the whole multi-view dimension run
**Medium · Confirmed** · `AutoDimensionRunner.cs:35-55`

`engine.BuildPlan(...)` is not guarded per view; an exception on view 3 of 20
kills the entire run (caught only by the caller's fatal handler). Clash Finder
is insulated (it calls the runner once per view), but **Refine Dimensions**
passes its whole selection in one call — one bad view means zero dimensions
everywhere. *Fix:* wrap the per-view plan build in try/catch → log the view as
failed, continue; same for the per-view `Commit` call inside the transaction.

### Shared-5-1 — `ResolveRuleColor` scans every trade × rule per element
**Medium · Needs Windows test** · `ClashEngine.cs:728-746`, called from
`ScanCategories` (:642) and `ScanElements` (:690)

In Categories/Elements modes, every scanned element walks all Auto Filter
trades and rules, each `MatchesRule` doing `LookupParameter` string reads. On
a model with 50k elements in the picked categories and a few dozen rules this
is millions of parameter reads before detection even starts. *Fix:* pre-index
rules by `BuiltInCategory` once per scan; cache the (typeId, ruleId) match
verdict. *Windows test W2 (timing).*

### Shared-5-2 — Clash detection is O(N×M) with bbox-only pruning
**Medium · Needs Windows test** · `ClashEngine.cs:1013-1071`

The pair test iterates the full cross product with a per-pair AABB check.
Progress is reported (`RunProgressReporter`, good), but 5k × 5k = 25M bbox
tests before any solid work. A sorted sweep on X (or a coarse grid hash) cuts
this by orders of magnitude on large models. *Windows test W2.*

### Shared-6-1 — Run-level override of the shared `AutoDimensionConfig` singleton
**Low · Confirmed** · `ClashFinderEventHandler.cs:101-113,273-277`,
`RefineDimensionsEventHandler.cs:62-90`

`TargetType`/`MaxCalloutScale` on `AutoDimensionConfig.Instance` are mutated
for the run and restored in `finally` — correct — but if anything calls
`cfg.Save()` while a run is in flight (e.g. the Dimensions settings window
auto-saving), the temporary override is persisted. *Fix (when convenient):*
pass the overrides down as parameters, or clone the config for the run.

### Shared-2-1 — Dead code: `SlabScopePicker.PickForViews`
**Low · Confirmed** · `SlabScopePicker.cs:19-77`

The per-view cascading in-canvas floor pick was superseded by the up-front
`SlabPickEventHandler`; only `FloorFilter`/`ResolveScope` are still referenced.
Also dead: `ClashDefinitionsSettings.ExportTo` / `TryImportFrom`
(see Defs-4-2). Remove or keep deliberately (comment why).

### Shared-8-1 — Systemic: 88 hardcoded run-log lines in the shared pipeline
**Medium (by volume) · Confirmed**

Count of interpolated `Log(...)`/`log(...)` lines not going through
`AppStrings.T` (all user-facing run-log output per CLAUDE.md):

| File | Lines |
|---|---|
| `AutoDimensionEngine.cs` | 26 |
| `ClashEngine.cs` | 22 |
| `AutoDimensionCommit.cs` | 8 |
| `AutoDimensionRunner.cs` | 7 |
| `SlabEdgeTargetResolver.cs` | 6 (all behind `DiagnoseSlabEdge` — arguably developer diagnostics; decide) |
| `SlabScopePicker.cs` | 5 (dead code path) |
| `ElevationTagRunner.cs` | 4 |
| `ManualDatumPicker.cs` | 3 |
| `ClashFinderEventHandler.cs` | 2 (lines 337, 461 "Cleared … previous marker element(s)"), plus the two Callouts-category messages at 379-382 |
| `GridTargetResolver.cs`, `PickerViewGuard.cs` | 1 each |

One of them also carries a UK spelling into the run log:
`ClashEngine.cs:244` "shown in fallback **colour**". *Fix:* one bulk Python
externalization pass into a new `Strings/en/clash.autoDim.json` (+ the few
strays into `clash.finder.json`), with the count-checked `str.replace()`
discipline from CLAUDE.md. Recommend excluding the `DiagnoseSlabEdge`
diagnostics (treat as developer-only, like `DiagnosticsLog`).

### Shared-8-2 — PickObject prompts and pick-flow strings hardcoded
**Low · Confirmed** · `ClashPickEventHandler.cs:36-38`,
`SlabPickEventHandler.cs:43-45`, `ManualDatumPicker.cs:40`,
`SlabScopePicker.cs:42-50`

Revit status-bar prompts are user-facing text.

### Shared-8-3 — Run log prints raw `OST_` enum tokens
**Low · Confirmed** · `ClashEngine.cs:667` (`Category OST_DuctCurves: 12
element(s)`) and the `Label = ostStr` at :651 that flows into later output.
Resolve to `Category.GetCategory(doc, bic)?.Name` for display.

---

## Clash Definitions

### Defs-1-1 — Unchecking every source document silently scans ALL documents
**High · Confirmed** · `ClashGroupEditor.cs:95-114` (`CommitSources`) +
`ClashEngine.cs:500-506` (`FilterSources`)

The spec stores "empty `SourceLinkIds` = scan every document". The editor's
doc checklist writes exactly the checked set — so a user who unchecks every
document (intending "none / let me think") saves an empty list, and the next
run scans **everything**, including links they explicitly deselected. The UI
state (all boxes unchecked) and the behavior (scan all) are opposites.
*Fix options (decision):* (a) forbid zero docs — auto-recheck the host and
show a hint; (b) persist an explicit "AllDocuments" flag so empty really means
none and log "no source documents — group produced no elements".

### Defs-1-2 — Editing a group pins "all documents" to the docs present at edit time
**Medium · Confirmed** · `ClashGroupEditor.cs:81-97`

A definition saved with the "scan all" default gets every doc pre-checked on
open; the first commit (any checkbox, even a workset toggle) writes that
explicit list. A link added to the project next month is then silently outside
the definition. *Fix:* keep writing an empty list while *all* docs are checked
(or the flag from Defs-1-1).

### Defs-2-1 — Delete definition: no confirmation, no undo
**Medium · Confirmed (decision)** · `ClashDefinitionsWindow.xaml.cs:280-289`

The trash glyph deletes immediately; closing the window auto-saves. A misclick
permanently destroys a definition built up over many sessions. Sibling
patterns elsewhere in the app use a confirm step for destructive row actions.
*Fix (pick one):* confirm dialog, or an inline "Deleted — Undo" pill until the
window closes.

### Defs-4-1 — Pick-element failures invisible in this window
**Medium · Confirmed** — the window-side half of Shared-4-4: the Definitions
window has no run log and never wires `PushLog`, so "Element pick failed"
never surfaces anywhere.

### Defs-4-2 — Import/Export dead code; import would wipe the library
**Low · Confirmed** · `ClashDefinitionsSettings.cs:206-228`

`ExportTo`/`TryImportFrom` have zero call sites. `TryImportFrom` assigns
`Instance.Definitions = imported` — a full replace, not a merge — so wiring it
to a button later without noticing would destroy the user's saved library.
Delete both, or keep `ExportTo` and rewrite import as merge-by-id when the
feature lands.

### Defs-2-2 — `MultiSelectTabs` subscribed after `SetGroups`
**Low · Confirmed** · `ClashGroupEditor.cs:439-447,456-466`

CLAUDE.md's contract is subscribe-*before*-`SetGroups` (the end-of-setup event
seeds the mirror). Here the mirrors are pre-seeded from the spec so it's
currently benign, but it deviates from the documented contract and from
`ClashFinderViewModel`, and would break if `SetGroups` ever pruned unknown
selections. Swap the two statements.

### Defs-8-1 — ClashGroupEditor UI strings hardcoded
**Low · Confirmed** · `ClashGroupEditor.cs` (~15 strings): "Group definition
mode", the mode display items ("Filter Rules"/"Categories"/"Select Elements"),
"Source documents (which models this group scans)", "Check a model to scan
it…", "No documents available.", "No Auto Filters rules configured…", "No
categories available.", "＋ Pick host elements", "＋ Pick linked elements",
"Clear", "No elements picked yet…", "{n} element(s) picked.", the `Summary()`
strings ("{n} rule(s)/category(ies)/element(s)"). Externalize the display
strings into `clashDefinitions.json` (the mode/persist tokens stay in code).

### Defs-8-2 — "Centre" displayed in the Marker Reference picker
**Low · Confirmed** · `ClashDefinitionsWindow.xaml.cs:357`

`"Edge"/"Centre"` are persisted tokens correctly kept in code, but they're
also used **as the display strings**, so the UI shows the UK spelling. Same
pattern as the phase picker fix: display via externalized labels ("Edge",
"Center"), map back to the tokens on selection. (`Fill Style`'s
"Solid"/"Outline" have the same token-as-display shape; the words are fine but
they'd move to the same display map.)

### Defs-8-3 — Tolerance edited in mm while every sibling input is imperial
**Low · decision** · `ClashDefinitionsWindow.xaml.cs:342-344`,
`clashDefinitions.labels.tolerance`

"Clash Tolerance (mm)" sits next to Clash Finder's oversize edited in inches
(stored mm). Imperial-first per the framework's tone rule would edit this in
inches too (store mm unchanged). Flagged for a decision, not silently changed.

---

## Clash Finder & Dimension

### Finder-7-1 — Disabling the dense-callout tier strands old "- Dense" callouts
**Medium · Confirmed** · `ClashFinderEventHandler.cs:202-240`

The stale-callout sweep lives inside `if (dimCfg.DenseCalloutsEnabled)`. Turn
the tier off in Settings after a run that created dense callouts, and every
re-run (even with Clear previous on) leaves the old callout views and their
bubbles on the parent forever, with stale markers inside. *Fix:* run
`SweepStaleCallouts` whenever `ClearPrevious` is set, with an empty keep-list
when the tier is off.

### Finder-6-1 — Run payload cleared outside `finally`
**Low · Confirmed** · `ClashFinderEventHandler.cs:289-302`

`ViewIds`/`Definitions`/`SlabScopes` are cleared after the completion
callbacks; a throwing callback (`Progress(100…)`, `OnResultChips`, `Complete`)
skips the clear and the static handler pins the run's views/definitions until
the next run. `ClashElevationFinderEventHandler` (:139-161) already does this
correctly — callbacks wrapped, clear in `finally`. Mirror that structure.

### Finder-3-1 — A deleted/unresolvable view id is skipped with no log line
**Low · Confirmed** · `ClashFinderEventHandler.cs:131-138`

`doc.GetElement(viewId) as View == null` bumps `viewsSkipped` silently. The
user sees "1 view with nothing to mark" and can't tell it was never a view at
all. Log "View id {0} no longer exists — skipped."

### Finder-3-2 — Progress bar sits at 5% through the whole detection phase
**Low · Confirmed** · `ClashFinderEventHandler.cs:65-97`

Phase 1 (detection — often the dominant cost) reports one 5% tick total; the
per-definition `FindClashes` progress goes to the *log* only. Report
`5 + defsDetected * 5 / Definitions.Count` per definition so the bar moves.

### Finder-5-1 — `doc.Regenerate()` per callout inside the create/adopt loops
**Noted (accepted) · Confirmed** · `ClashFinderEventHandler.cs:332,456`

Per-item regen in a loop is on the CLAUDE.md blacklist; here it's deliberate
(the new/regrown view must exist before its marker pass) and bounded by the
few callouts per view. Left as-is; recorded so the exception is explicit.

### Finder-8-1 — Hardcoded log lines in the handler
**Low · Confirmed** · `ClashFinderEventHandler.cs:337,461` ("Cleared {n}
previous marker element(s) in '{v}'." — the externalized
`clash.finder.log.cleared` already exists and should be used), and 378-382
(the two Callouts-annotation-category messages). Counted in Shared-8-1's bulk
pass.

*(Checked clean: S1/S2 validation gates; subscribe-before-`SetGroups` in the
VM; `IToolCleanup` nulls all parked callbacks incl. the slab pick;
slab pick's dispatcher-shutdown guard; slab override correctly ignored when
the destination isn't Slab Edge; review summary/chips honestly reflect clear /
callouts / cap / destination; per-view transactions preserve committed work on
cancel; `common.log.stoppedByUser` used in both phases.)*

---

## Clash Finder & Elevation Tag

### Elev-7-1 — Cancel granularity: one transaction for the whole marking run
**Medium · Confirmed** · `ClashElevationFinderEventHandler.cs:59-109`,
`ElevationTagRunner.cs`

Marking runs all definitions × all views inside a single transaction, with the
cancel check only *between definitions* — a one-definition run over 50
sections cannot be stopped at all, and `ElevationTagRunner` has no
`RunState.CancelRequested` checks anywhere (nor a batched progress cadence in
its per-marker loop). The plan-view Clash Finder commits per view; this tool
should at minimum check cancel per view inside `ClashEngine.Run`'s view loop
(pass a cancel probe) or split marking into per-view transactions like its
sibling. *Decision:* per-view transactions (matches sibling, preserves partial
work) vs. cancel-check-only (simpler, still all-or-nothing on commit).

### Elev-1-1 — Spot-type fallback can offer non-elevation spot types
**Medium · Needs Windows test** · `ClashElevationFinderCommand.cs:66-73`,
`ElevationTagRunner.cs:118-121`

When the `OST_SpotElevations`-scoped collector finds nothing, the fallback
collects **every** `SpotDimensionType` — which includes spot *coordinate* and
spot *slope* types. Picking one of those: `ChangeTypeId` to a different spot
kind either throws (swallowed to diagnostics — tag silently keeps the default
type) or mis-tags. *Fix:* filter the fallback by
`type.Category?.Id == OST_SpotElevations` via the collector's category filter,
or drop the fallback and let the existing "No spot elevation type found"
path speak. *Windows test W3.*

### Elev-2-1 — Fixed 1.5 ft / 3.0 ft leader offsets ignore view scale
**Medium · Needs Windows test** · `ElevationTagRunner.cs:110-111`

The spot-elevation bend/end sit a fixed model distance right of the anchor. At
1:12 the tag reads far away; at 1:100+ it overlaps the round it labels. Scale
them with `view.Scale` (paper-space constant, e.g. 3/16" paper × scale).
*Windows test W3.*

### Elev-3-1 — No progress or batched log cadence inside the marker/tag loops
**Low · Confirmed**

Per-definition progress exists (`10 + done*70/count`), but a single definition
detecting + marking hundreds of clashes across many sections reports nothing
in between (detection's `RunProgressReporter` logs, but the bar is static),
and the tag pass jumps 85 → 100. Cheap fix: per-view progress inside the tag
runner via a callback.

### Elev-8-1 — `ElevationTagRunner` log lines hardcoded
**Low · Confirmed** · `ElevationTagRunner.cs:65,71,104,130` — counted in
Shared-8-1.

*(Checked clean: payload clear in `finally` with guarded callbacks — the model
the other handlers should copy; `StoreyMarginMm = 0` for section gating is
correct; clear-previous also removes tagged `SpotDimension`s; marker/tag
counts reported separately so the headline isn't double-counted; spot
reference anchored to the tagged diameter line per the CLAUDE.md rule.)*

---

## Refine Dimensions

### Refine-3-1 — "replaced" double-counts stale deletions: the log lies
**High · Confirmed** · `RefineDimensionsEventHandler.cs:85` +
`AutoDimensionCommit.cs:49-59`

`AutoDimensionCommit` increments `DeletedPrior` for **every** deleted prior
dimension and `StaleDeleted` *additionally* for the stale subset — so
`replaced = result.DeletedPrior + result.StaleDeleted` counts every stale
dimension twice. A run that deletes 40 priors (10 stale) reports "50 prior
dimension(s) replaced". The final headline and the "replaced" result chip are
both wrong. *Fix:* `replaced = result.DeletedPrior;` (keep `StaleDeleted` for
a separate "(N stale)" clause if wanted).

### Refine-7-1 — The run cannot be cancelled once started
**Medium · Confirmed** · `RefineDimensionsEventHandler.cs:51-56`

`RunState.CancelRequested` is checked exactly once, before any work.
`AutoDimensionRunner` has no cancel checks, and all views commit in one
transaction — the red Cancel button does nothing for a 30-view refine. *Fix:*
check the flag in the runner's per-view plan loop (skip remaining views, log
the standard stopped-by-user line, still commit the plans already built), per
the framework's cancel-at-progress-boundary rule.

### Refine-3-2 — No progress during the commit phase
**Low · Confirmed** · `AutoDimensionRunner.cs:37` +
`RefineDimensionsEventHandler.cs:79`

The runner's progress callback fires only during plan building (mapped 5→95);
placing the dimensions — half the work — happens with the bar frozen. Report
per-view progress from the commit loop too.

*(Checked clean: crop-bounded resolution per view — matches the CLAUDE.md
"Refine gets no callouts" note and the review chips say so honestly; config
override restored in `finally`; payload cleared in `finally`; S1 required
gate; honest "never detects or marks" copy throughout.)*

---

## Pass 8 — String table (current vs proposed)

Key completeness: **clean both directions** — all 208 `AppStrings.T` keys used
by the group exist in the JSONs, and no clash-prefixed JSON key is unused
(verified by flatten + regex diff).

The externalized text is in good shape; only these rewrites are proposed:

| Key | Current | Proposed |
|---|---|---|
| `clash.finder.labels.storeyNote` | The **storey** depth margin (how far below a level a clash still counts as that level's **storey**) lives in Settings → Dimensions. | The **story** depth margin (how far below a level a clash still counts as that level's **story**) lives in Settings → Dimensions. |
| `clashDefinitions.labels.markerReference` *(display of its options — see Defs-8-2)* | Edge / **Centre** (persisted tokens shown raw) | Edge / **Center** shown; tokens unchanged in storage |

Everything else read as plain, direct US construction English (contractions,
imperial-first, honest verbs). The real Pass-8 work is the *un*-externalized
text: Shared-8-1/-8-2/-8-3, Finder-8-1, Elev-8-1, Defs-8-1 — proposed as one
bulk count-checked Python pass creating `Strings/en/clash.autoDim.json` and
extending `clashDefinitions.json` / `clash.finder.json`.

---

## Appendix — Windows test scripts

**W1 — Self-clash (Shared-4-1)**
1. Create a definition with the *same* single category (e.g. Pipes) in Group 1
   and Group 2, tolerance 25 mm, Max Clashes 500.
2. Run Clash Finder on one plan view of an area with ~10 pipes that do NOT
   touch each other.
3. *Current behavior to confirm:* every pipe gets a marker ("clash" with
   itself); the log's clash count ≈ pipe count although nothing intersects.
4. *After fix:* 0 clashes. Also verify two genuinely crossing pipes produce
   exactly **one** marker, not two stacked.

**W2 — Detection timing (Shared-5-1 / Shared-5-2)**
1. On a large coordination model, build a Categories-mode definition (Ducts vs
   Structural Framing) covering the host + all links.
2. Run once and note the timestamps in the run log between "Group 1: N
   element(s)" and "Found N clash(es)" and the total wall time.
3. If detection dominates (> ~60 s), the scan-index and pair-pruning fixes are
   worth scheduling; attach the log.

**W3 — Elevation tags (Elev-1-1 / Elev-2-1 and the below-line tag push)**
1. In a project with NO spot elevation types but with spot coordinate types,
   open Clash Finder & Elevation Tag → confirm whether the Spot type picker
   lists coordinate types (fallback path) and what a run places.
2. In a normal project, run on one section at 1:12 and one at 1:96; check the
   tag leader length relative to the round at both scales.
3. While there, confirm the below-line moved-tag extra push (CLAUDE.md open
   item) on the same plot.

**W4 — Stale dense callouts (Finder-7-1)**
1. Run Clash Finder with dimension pass + dense callouts ON on a dense view →
   confirm "- Dense c000…" callouts created.
2. Settings → Dimensions: turn dense callouts OFF. Re-run the same view with
   Clear previous ON.
3. *Current behavior to confirm:* the old dense callout views and their
   bubbles remain (with stale markers). *After fix:* they are swept.

**W5 — Source-document inversion (Defs-1-1)**
1. Edit a clash group; uncheck every source document; close (auto-save); run.
2. *Current behavior to confirm:* the log shows elements scanned from the host
   and links despite nothing being checked.
