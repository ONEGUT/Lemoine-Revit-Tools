# Function Review — Setup Panel

Reviewed per `plan-function-review-framework.md` (all eight passes). Tools covered:
**Upgrade & Link Models**, **Align Coordinates**, **Push Coordinates to Links**, plus
their launch commands and shared support files (`CoordinatesGeometry.cs`,
`CoordinatesModels.cs`). Link Audit and Compare Grids are retired behind
`ShowRetiredSetupTools` and were skipped.

Every finding is tagged **Confirmed** (provable from the code) or **Needs Windows
test** (test script in the appendix). Nothing has been changed — pick the findings
you want fixed and I'll apply them on a `review-fixes-setup` branch.

**Totals: 0 Critical · 9 High · 8 Medium · 12 Low.**

The headline: Upgrade Links is in strong shape mechanically (memory posture, progress
cadence, cancellation, externalized text) but has two deep run-lifecycle holes.
Align and Push are mechanically decent but **neither is externalized at all** —
every label, hint, and log line is a hardcoded string, which is also why neither has
a `Strings/en/*.json` file. And Align ships a broken input control.

---

## Upgrade & Link Models

Files: `UpgradeLinksViewModel.cs`, `UpgradeLinksRunHandler.cs`,
`UpgradeLinksScanHandler.cs`, `UpgradeLinksSettings.cs`, `UpgradeLinksModels.cs`,
`UpgradeLinksCommand.cs`, `Strings/en/upgradeLinks.json`.

### UL-7-1 · HIGH · Confirmed — Closing the window during a Cloud pause permanently bricks the tool
`UpgradeLinksRunHandler.cs:56-72, 397`. During a Cloud run the handler holds
`_cloudActive = true` and an open `_cloudWaitDoc` between `Execute()` calls while
waiting on the user. `StepFlowWindow`'s `Closed` handler only nulls the callbacks
(`OnWindowClosed`) — nothing clears the cloud state. After that, **every** future
run of the tool sets a fresh `Spec` and raises the event, but `Execute` sees
`_cloudActive`, routes into `ContinueCloudRun`, finds neither Continue nor Skip
requested, and returns (`line 397`). The new run silently does nothing, forever,
until Revit restarts. The upgraded document is also left open.
**Fix:** have `OnWindowClosed` set an abort flag (and `Raise()`), or make `Execute`
treat "fresh non-empty `Spec` while `_cloudActive`" as an implicit abort of the
stale cloud run (finish it as cancelled, close the wait doc, then start the new run).

### UL-4-1 · HIGH · Confirmed — Failed link placement is silently counted as "skipped"
`UpgradeLinksRunHandler.cs:561-573` and `199-210`. `LinkIntoHost` returns false on
three paths. One logs (`linkExistsSkip`); the other two log **nothing to the run
log**: an invalid `typeId` after `Create` (line 561), and an exception from
`RevitLinkInstance.Create` (lines 566-573, diagnostics only). The caller counts
every false return as `skip`, so a genuine placement failure shows up in the run
summary as a skip with zero explanation — the file was upgraded and saved but never
linked, and the log doesn't say so.
**Fix:** log a ✗ line naming the file on both silent paths, and count the
instance-create exception as `fail`, not `skip`.

### UL-1-1 · HIGH · Needs Windows test — Upgrading a file that's already linked into the host fails confusingly
`UpgradeLinksRunHandler.cs:179`. If a queued file is currently loaded as a link in
the host, `OpenDocumentFile` on that path returns the in-memory **linked** document
(this exact behavior is confirmed in `PushCoordinatesToLinksRunHandler`'s doc
comment from a real project run). `SaveAs` on a linked document should then throw,
and the file lands in the log as a raw exception failure. The "Reload existing link"
toggle implies re-upgrading already-linked files is a supported workflow — Push
Coordinates solves this by unloading the link type first; Upgrade Links never does.
**Fix:** before opening, check whether a loaded `RevitLinkType` points at this path
and unload it first (reloading it afterward from the saved copy), or pre-detect and
skip with a clear message.

### UL-1-2 · MEDIUM · Confirmed — Selected-folder saves silently overwrite pre-existing files
`UpgradeLinksRunHandler.cs:681-689, 512`. `UniqueFileName` only de-duplicates
against names used **this run**; `SaveAsOptions.OverwriteExistingFile = true` then
silently replaces any file already sitting in the destination folder with the same
name. For re-runs that's arguably the desired behavior (refresh the upgraded copy);
for an unrelated same-named file it's silent data loss. **Decision needed:** keep
(document it in the destination hint) or pre-check `File.Exists` and warn/skip.

### UL-2-1 · MEDIUM · Confirmed — Review step omits two behavior-changing options
`UpgradeLinksViewModel.cs:208-219, 535-564`. "Audit each file on open" and "Reload
existing link" change what the run does, but the Review step shows neither (no
review item, no chips), and their `StateChanged` doesn't raise `ValidationChanged`.
The pre-run summary therefore doesn't honestly state what's about to happen.
**Fix:** add `ReviewChips` (e.g. `audit ✓ / reload ✓`) and call `Changed()` in the
toggle handler.

### UL-8-1 · MEDIUM · Confirmed — Placement labels cached per first-touched language
`UpgradeLinksViewModel.cs:688-689`. `LabelToPlacement` is a `static readonly`
dictionary keyed by `AppStrings` display labels, built once at first type load.
After a language switch, a newly opened window builds its pickers from the *new*
culture's labels, but `SelectionChanged` looks them up in the dictionary keyed by
the *old* culture's labels — the lookup misses and the placement change is silently
ignored. **Fix:** build the map per-instance (or key the picker by enum, not label).

### UL-8-2 · MEDIUM · Confirmed — Ribbon tooltip describes behavior the tool no longer has
`Strings/en/ribbon.json:22-24`. The tip says files are saved "into a subfolder next
to this model or over the original." There is no auto-subfolder anymore (files save
directly into a chosen folder), and the Cloud destination isn't mentioned. Proposed
rewrite in the string table below.

### UL-3-1 · LOW · Confirmed — A whole-scan failure is invisible in the UI
`UpgradeLinksViewModel.cs:405-410`. Per-file scan errors correctly mark rows
unreadable, but if the scan handler itself throws, `OnError` only writes to the
diagnostics log and clears the spinner — rows sit at version "?" with no hint that
the scan died. **Fix:** show a one-line warning in the files table on `OnError`.

### UL-1-3 · LOW · Confirmed — Duplicate adds are silently dropped
`UpgradeLinksViewModel.cs:358-362`. Re-adding a file already in the list is
silently ignored. Fine behavior, but a small "already in the list — skipped N"
note would explain why nothing appeared.

### UL-1-4 · LOW · Confirmed — Run can start before the version scan finishes
`UpgradeLinksViewModel.cs:571, 621`. Unscanned rows default to `Readable = true`,
so a fast user can reach Run while the scan is in flight and include a file the
scan would have flagged too-new; it then fails at open with a generic message. Low
because the scan is fast. **Fix option:** treat `_scanning` as invalid for the
files step.

### UL-2-2 · LOW · Confirmed — "Save as" boxes stay editable when the destination ignores them
The Current-location card explains that save-as names are ignored there, but the
Files step (which comes *before* the destination choice) gives no cue, and the
boxes remain enabled. Step order makes truly disabling them awkward; a chip on the
review step ("save-as names ignored — current location") would close the gap.

### Passes with no findings
- **Pass 5 (performance)** — serial one-at-a-time processing, detach + close-all-
  worksets on open (the dominant RAM saver), close-before-link, background opens
  only. This is the model the other tools should follow. No changes.
- **Pass 6 (memory)** — payload cleared in `finally` (correctly deferred during a
  live cloud run), `IToolCleanup` implemented, dispatcher marshaling via
  `BeginInvoke`. Clean apart from the UL-7-1 leak.
- **Pass 7 (cancellation)** — checked between files in both local and cloud modes,
  logs the shared `common.log.stoppedByUser` line, work preserved. Clean.
- **Key completeness** — every `AppStrings.T("upgradeLinks.*")` key referenced in
  code exists in the JSON; no unused keys. Verified by script.

---

## Align Coordinates

Files: `AlignCoordinatesViewModel.cs`, `AlignCoordinatesRunHandler.cs`,
`AlignCoordinatesCommand.cs`, shared `CoordinatesGeometry.cs` / `CoordinatesModels.cs`.

### AC-1-1 · HIGH · Confirmed — Per-link override "Grid 1" dropdown is never shown
`AlignCoordinatesViewModel.cs:429-431`. In the link-override panel, `col1` gets the
"Grid 1" **label** added, then `g1Sel` (the actual `SingleSelect`) is created and
wired — but never added to `col1.Children`. The user sees a "Grid 1" caption with
no control under it and can never change Grid 1 for a link override; it's stuck on
the link's alphabetically-first grid, and the Grid 2 candidate list is filtered
against that stuck value. One-line fix: `col1.Children.Add(g1Sel);`.

### AC-8-1 · HIGH · Confirmed — Entire tool's text is hardcoded
No `AppStrings.T` call anywhere in the ViewModel, run handler, or command; there is
no `Strings/en/` file for this tool. Every step title, label, hint, review line,
warning, and run-log message violates the externalization rule. Fix is a bulk
Python-script pass creating `setup.alignCoordinates.json` (per the CLAUDE.md
rewiring rule) — the proposed English text is in the string table below so the tone
pass and the externalization land together.

### AC-4-1 · MEDIUM · Confirmed — Z behavior contradicts its own documentation for Internal-Origin links
`AlignCoordinatesRunHandler.cs:180-183` vs `227-231, 257`. The method comment says
a Matched Level Z target "only applies to a Grid-Intersection-overridden link,"
but the code sets `applyZ = true` for every Internal-Origin link, so those links'
origins get moved to the host level's elevation too. Either the comment or the code
is wrong. **Decision needed** — my read: the code is the more defensible behavior
(the user explicitly picked a Z target), so fix the comment; but if links modeled
on-origin should never take a level Z, fix the code.

### AC-4-2 · MEDIUM · Confirmed — A mid-link exception commits a half-transformed link and leaves it unpinned
`AlignCoordinatesRunHandler.cs:234-263` with the catch at `142-147`. Rotation and
translation are two separate API calls inside one transaction covering all links.
If translation throws after rotation succeeded, the per-link catch logs a failure
but the loop continues and the transaction commits — that link ends up **rotated
but not moved**, a worse state than untouched, and the re-pin at line 262 never
runs so it's also left unpinned. **Fix:** wrap each link in a `SubTransaction`
(rollback on throw) and restore the pin in a `finally`.

### AC-1-2 · MEDIUM · Confirmed — Command dereferences `ActiveUIDocument` without a guard
`AlignCoordinatesCommand.cs:40`. `uiApp.ActiveUIDocument.Document` — the other two
commands use `?.`. `BuildTool` is also the window's Reload callback, so if the user
closes all documents and clicks Reload, this throws `NullReferenceException` on the
window's STA thread (the dispatcher net catches it, but the tool dies with a
diagnostics entry instead of a clean "no document" state).

### AC-3-1 · LOW · Confirmed — Log identifies links by raw element id
`AlignCoordinatesRunHandler.cs:146, 188`. "✗ Link 4211058: …" and "⚠ Link 4211058
is not loaded" — `spec.LinkName` is available on both paths and is what the user
recognizes. Use it (id in parentheses if you want it kept).

### AC-3-2 · LOW · Confirmed — Skip messages use inconsistent severities
"not loaded — skipped" logs as `warn`; grid-not-found / no-plan-line / parallel
skips log as `info` (`RunHandler.cs:188, 200, 206, 212`). All four are the same
outcome. Recommend `warn` for all skips so they're scannable in the log.

### AC-4-3 · LOW · Confirmed — Base-point pin state not restored on failure
`AlignCoordinatesRunHandler.cs:275-295`. `MoveBasePoint` unpins, moves, re-pins —
but the re-pin isn't in a `finally`, so a throw at `MoveElement` leaves the point
unpinned. Same pattern (and same fix) as AC-4-2's link pinning.

### AC-4-4 · LOW · Confirmed — `AlignResult.Failed` is unreachable
`AlignOneLink` only ever returns `Aligned` or `Skipped` (failures throw to the
caller's catch). Harmless dead path; remove the enum member or return it from a
future validated-failure path.

### AC-2-1 · Note (no action) — Zero-links run is legitimate
The "links" step is `required: false`, so with no links loaded the user can still
run to move just the host points — that works and is sensible. The final "Done.
0 link(s) aligned…" line reads slightly odd for that case; the string table
proposes a host-only variant.

### Passes with no findings
- **Pass 5** — collectors are class-filtered, grid geometry is captured once on the
  main thread at launch, no regens, no per-item collector loops. Clean.
- **Pass 6** — `LinkSpecs` cleared in `finally`; `IToolCleanup` nulls callbacks. Clean.
- **Pass 7** — cancel checked per link with the preserve-and-commit fall-through. Clean.

---

## Push Coordinates to Links

Files: `PushCoordinatesToLinksViewModel.cs`, `PushCoordinatesToLinksRunHandler.cs`,
`PushCoordinatesToLinksCommand.cs`.

### PC-3-1 · HIGH · Confirmed — A failed base-point correction still reports "✓ pushed"
`PushCoordinatesToLinksRunHandler.cs:328-347` with the flow at `214-239`.
`MoveBasePoint` catches its own exceptions, logs a `warn`, and returns `void` — the
caller can't tell it failed. The run then proceeds to **save/sync the link file**
(for worksharing, with a central-history comment claiming the points were
corrected), publish coordinates, delete and recreate the instance, and log
"✓ corrected in its own file and re-placed" with a `pass` count. If both point
moves failed, the tool did real, hard-to-undo work (sync + instance recreation)
while accomplishing nothing — and told the user it succeeded.
**Fix:** return `bool` from `MoveBasePoint`; if every requested point move failed,
close the link doc **without saving** and report the link as failed.

### PC-7-1 · HIGH · Needs Windows test — Multiple instances of one link file aren't handled
`PushCoordinatesToLinksRunHandler.cs` (whole flow). If the same link file is placed
twice in the host, each instance arrives as its own spec: `Unload()` unloads the
shared **type** (both instances), the file gets opened/corrected/saved once per
instance, `PublishCoordinates` for the second instance of the same file will throw
or record a conflicting position (Revit allows one published position per link file
without named locations), and delete/recreate only touches one instance while the
other keeps its old transform against a now-moved file. **Fix:** group specs by
`RevitLinkType` up front; process one instance per type and skip-and-log the rest
("this file is placed N times — push once, then reposition the other copies").

### PC-7-2 · HIGH · Needs Windows test — Delete/recreate destroys everything that referenced the instance
`PushCoordinatesToLinksRunHandler.cs:290-291`. `hostDoc.Delete(li.Id)` takes down
host elements that depend on that instance — dimensions and tags referencing the
link's geometry, per-view graphic overrides on the instance, its workset
assignment, copy/monitor relationships. The recreate is by design (per CLAUDE.md,
it's what converts Origin-to-Origin to Shared positioning) but the user is never
warned. **Fix:** `Document.Delete` returns the deleted ids — count anything beyond
the instance itself and log it ("removed N dependent elements"); add a sentence to
`ReviewNote` warning that dimensions/overrides tied to the link instance won't
survive. Windows test confirms exactly which dependents die.

### PC-8-1 · HIGH · Confirmed — Entire tool's text is hardcoded
Same as AC-8-1: zero `AppStrings` usage, no JSON file. Proposed
`setup.pushCoordinates.json` content is in the string table.

### PC-4-1 · MEDIUM · Needs Windows test — Two flagged-unverified assumptions gate the whole flow
Already marked in code comments, kept visible here so they get a real test:
(a) `Unload()` releases the in-memory link document so `OpenDocumentFile` returns a
standalone transactable doc (`lines 177-181`); (b) `SynchronizeWithCentral` works
on a background-opened, never-activated document — and note the open is on the
**central path directly**, not a local (`lines 225-235`). Test script in appendix.

### PC-1-1 · LOW · Confirmed — One bad link aborts collecting the rest
`PushCoordinatesToLinksCommand.cs:69-83`. The link-collection loop sits inside a
single try/catch, so an exception on one link abandons the remainder and the tool
opens with a partial list, silently. Align's command guards per-link; mirror that.

### PC-3-2 · LOW · Confirmed — Base-point-missing warnings don't affect the outcome
`RunHandler.cs:330`. If the link doc lacks a base point, the run logs a warn and
continues to save/sync/recreate anyway — same shape as PC-3-1 (it's the `bp == null`
branch of the same fix): if nothing was actually corrected, don't save and don't
count it as pushed.

### Passes with no findings
- **Pass 2** — step order (links → settings → review) is right; review warning and
  `IsValid` agree; the workshared-sync explanation on the settings step is exactly
  the kind of consequence surfacing the other tools should copy.
- **Pass 5** — one open/save per link is inherent to the job; recovery-reload path
  keeps the host consistent on failure. Clean.
- **Pass 6/7** — payload cleared in `finally`, `IToolCleanup` implemented, cancel
  checked per link. Clean.

---

## Group-wide findings

### GR-4-1 · LOW · Confirmed — Bare `catch { _window = null; }` in all three commands
`AlignCoordinatesCommand.cs:34`, `PushCoordinatesToLinksCommand.cs:33`,
`UpgradeLinksCommand.cs:32`. The singleton-activate pattern swallows with a bare
catch — the CLAUDE.md rule requires `DiagnosticsLog.Swallowed` even for deliberate
swallows. If this is meant to be the house pattern for every command, route it
through `Swallowed` once (or acknowledge the exception in CLAUDE.md).

### GR-3-1 · LOW · Confirmed — Cancel and completion lines aren't standardized
Upgrade Links uses `common.log.stoppedByUser` ("Stopped by user — {0} of {1}
processed; work so far preserved."); Align and Push hardcode their own variants
("Stopped by user — 3 link(s) aligned so far; work preserved."). Same for the final
line: "Done — {0} linked, {1} skipped, {2} failed." vs "Done. {0} link(s) aligned,
{1} skipped, {2} failed." Standardize on the `common` key and the "Done — …" shape
as part of the externalization pass.

### GR-8-1 · LOW · Confirmed — British spellings in code comments
`CoordinatesGeometry.cs:18` "normalised", `CoordinatesModels.cs:37` "modelling".
Not user-facing, so skip unless you want comments US-English too — flagging since
the request was "all descriptive text."

---

## String table — current vs. proposed

### Align Coordinates (all currently hardcoded → new `Strings/en/setup.alignCoordinates.json`)

| Where | Current | Proposed |
|---|---|---|
| Run log (VM `Run`) | `Raising Revit ExternalEvent…` | `Starting alignment…` |
| Run log | `Run handler not registered.` | `Internal error: run handler not registered.` (matches Upgrade Links) |
| Run log | `✗ Link 4211058: {msg}` | `✗ [LinkName]: {msg}` |
| Run log | `⚠ Link 4211058 is not loaded — skipped.` | `⚠ [LinkName] isn't loaded — skipped.` |
| Run log | `A host grid has no usable straight line in plan.` | `Grid '{0}' isn't a straight line in plan — pick two straight grids.` |
| Run log | `Done. {0} link(s) aligned, {1} skipped, {2} failed.` | `Done — {0} aligned, {1} skipped, {2} failed.` (add host-only variant: `Done — host points moved; no links selected.`) |
| Run log | `✓ {name}: aligned to {method}, plan only (no matching level — Z unchanged).` | keep, minor: `…plan only — no matching level, so Z was left alone.` |
| Step hint | `Anchors the host and every link to their own Internal Origin — no picking needed when the project was modeled the normal way.` | keep (reads well) |
| Step hint | `Use the separate "Push Coordinates to Links" tool to commit this into the linked files.` | `This only moves the copies in this model. To make it stick in the link files themselves, run Push Coordinates to Links after.` |
| Review note | `Links are repositioned in the host only — use "Push Coordinates to Links" to commit this into the linked files.` | same rewrite as above |
| Empty state | `No loaded links found.` | `No loaded links in this model. You can still run to move the host points.` |
| Labels | `Move which host point(s)` | `Which host points should move?` |
| Chip | `rotate ✓ / rotate ✗` | keep |

### Push Coordinates to Links (all hardcoded → new `Strings/en/setup.pushCoordinates.json`)

| Where | Current | Proposed |
|---|---|---|
| Run log (VM `Run`) | `Raising Revit ExternalEvent…` | `Starting push…` |
| Run log | `Done. {0} link(s) pushed, {1} skipped, {2} failed.` | `Done — {0} pushed, {1} skipped, {2} failed.` |
| Run log | `⚠ {name}: link instance no longer exists — skipped.` | `⚠ {name}: that link isn't in the model anymore — skipped.` |
| Run log | `✗ {name}: reloaded the corrected file but could not publish shared coordinates ({msg}) — link left as-is.` | keep (good) |
| Run log | `⚠ {name}: left unloaded after a failure — reload it manually via Manage Links.` | keep (good) |
| Step hint | `Every source is corrected and saved in place. A workshared source is Synchronized With Central so the team's actual central model is corrected — never a copy.` | keep |
| Review note | `Each selected link's own file is corrected and saved, then re-placed in the host using Shared Coordinates.` | add: `Dimensions or overrides attached to the old link placement may not survive the re-place.` (pairs with PC-7-2) |
| Sync comment | `Lemoine Tools: corrected Project Base Point / Survey Point` | keep hardcoded (goes into central history, treat as a logic token) but only write it when a correction actually happened (PC-3-1) |

### Upgrade & Link Models (edits to existing `upgradeLinks.json` / `ribbon.json`)

| Key | Current | Proposed |
|---|---|---|
| `ribbon.buttons.upgradeLinks.tip` | `…upgrade every file to the current version, save it (into a subfolder next to this model or over the original), and link it into the active model…` | `Pick Revit files from any folder, choose each one's link placement, then upgrade every file to the current version, save it (to a folder you choose, back over the original, or as a cloud model), and link it into the active model. Files are processed one at a time to keep memory flat.` |
| `labels.filesHint` | `Add Revit files from any folder — they accumulate into one list. …` | `Add Revit files from any folder — keep adding and they all collect in one list. …` (optional; current is acceptable) |
| `labels.optCurrentLocationDesc` | `Saves each upgraded model back over its own source path.` | `Saves each upgraded model back over its original file.` |
| `log.cloudSkipped` | `Skipped {0} by request.` | `Skipped {0} — you chose to skip it.` |
| everything else | — | reads natural and US-English; no changes proposed |

---

## Appendix — Windows test scripts

### T1 (UL-1-1) — Upgrade a file that's already linked
1. In a host model, link `A.rvt`. Open Upgrade & Link Models, queue that same
   `A.rvt` (older version), destination Selected folder, run.
2. **Expect (current code):** the file fails at save with a linked-document
   exception in the log (`fileFail`), confirming the finding. If it somehow
   succeeds, note what `OpenDocumentFile` returned and close the finding.

### T2 (PC-7-1) — Same link placed twice
1. Place `A.rvt` twice in a host (two instances, one type). Align both, then run
   Push Coordinates with both instances checked.
2. **Watch for:** second `PublishCoordinates` throwing; whether instance 2 is
   deleted/recreated or left stale; the state of `A.rvt` after two open/save cycles.

### T3 (PC-7-2) — Dependents of a re-placed link
1. In the host, dimension from a wall to linked geometry; apply a per-view graphic
   override to the link instance; note its workset. Run Push Coordinates on it.
2. **Expect:** dimension deleted (Revit warns "elements were deleted"), override
   and workset reset on the new instance. Record which, so the warning text and the
   deleted-dependents count in the log can be written accurately.

### T4 (PC-4-1) — Workshared push end-to-end
1. Make `A.rvt` workshared with a central; link into a host; align; run Push.
2. **Watch for:** `OpenDocumentFile` on the central path succeeding without a
   local; `SynchronizeWithCentral` succeeding on the never-activated doc; the
   central's base points actually moved afterward (open it manually to verify).

### T5 (AC-4-1) — Z target on an Internal-Origin link
1. Align Coordinates: Z method = Matched Level (pick a level at, say, 10′), links
   on default Internal Origin. Run.
2. **Observe:** does the link's origin land at Z = 10′ (code behavior) — and is
   that what you want for on-origin-modeled links, or should they stay at Z = 0
   (comment behavior)? This decides AC-4-1's fix direction.

### T6 (UL-7-1) — Cloud-pause brick repro (confirmation only)
1. Cloud host, queue 2 files, destination Cloud, run. When the first pause appears,
   close the tool window without Continue/Skip.
2. Reopen the tool, queue a file, run. **Expect (current code):** log shows
   "Starting upgrade & link…" then nothing ever happens; the previously opened
   upgrade doc is still open. Restart Revit to recover.
