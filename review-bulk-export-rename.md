# Function Review — Bulk Export & Bulk Rename

Run of `plan-function-review-framework.md` (eight passes, severity scale as defined
there). Static analysis only — every finding is **Confirmed** (provable from code)
or **Needs Windows test** (script in the appendix). Nothing has been changed;
approve findings by number.

**Scope**

| Tool | Files |
|---|---|
| **Bulk Export** | `Source/Tools/Export/BulkExport{ViewModel,EventHandler,PrintSetHandler,Settings,DebugLogger}.cs`, `PrintSetModels.cs`, `Source/Commands/Export/BulkExportCommand.cs`, `Strings/en/export.bulkExport.json` |
| **Bulk Rename** | `Source/Tools/Sheets/BulkRename/BulkRename{ViewModel,Engine,RunHandler}.cs`, `Source/Commands/Sheets/BulkRenameCommand.cs`, `Strings/en/linkviews.bulkRename.json` |
| **Shared** | `Source/Tools/Export/ExportOptionsFactory.cs` (also used by Print View — findings there affect both) |

Print View itself is out of scope (sibling tool, separate review).

---

## Resolution (applied on this branch)

All Confirmed, actionable findings below were fixed. Decisions taken:

- **BulkExport-2-1** → option (a): the code now matches the note — NWC/IFC always
  export from the Step 1 selection, even when print sets are checked. The
  `nwcInPack`/`ifcInPack` skips are gone.
- **BulkExport-4-3** → the temporary debug logger was removed entirely.
- **BulkRename-7-1** → two-phase rename (temp values, then finals, in one
  transaction). Shifts and swaps now work; the planner was rewritten so a
  selected item's own value is no longer a false obstacle (this also fixes
  **BulkRename-4-1**).

Deferred by design (flagged for a separate decision, not fixed here):

- **BulkExport-8-4** — externalizing result-strip nouns/chips is a project-wide
  convention change across ~15 tools; out of scope for this fix set.
- **BulkRename-8-1** — relocating the tool's namespace + string prefix touches
  JSON key renames and every `AppStrings.T` call; best as its own change.
- **BulkRename-2-1** — full reorder UI deferred; the minimum fix (a hint line
  stating the numbering order) was applied.
- **BulkRename-4-2 / BulkExport-4-1 / -4-4 / -5-1** carry Windows test scripts
  (appendix) to confirm behaviour on a real Revit; the code fixes are in place.

---

## Bulk Export

### Pass 1 — Inputs & validation

**BulkExport-1-1 · Medium · Confirmed — "Show all views" exposes sheets (and system views) as pickable views.**
`BulkExportCommand.cs:54-61` collects `_allViews` with `OfClass(typeof(View))`
excluding only templates, schedules, and legends — `ViewSheet`, `ProjectBrowser`,
`SystemBrowser`, `Internal`, and `Undefined` views all pass. Without "Show all"
the `allowedFamilies` filter in `BuildEligibleIds` (`BulkExportViewModel.cs:374-391`)
hides them, but with the checkbox on, `showAll ||` short-circuits the filter and
every one becomes eligible — sheets are in the captured browser tree, so they
become selectable as "views" in Views mode (exported under the view pattern,
skipped by NWC/IFC). Fix: exclude the same view types `BulkRenameCommand.cs:62-70`
already excludes, plus `!(v is ViewSheet)`.

**BulkExport-1-2 · Medium · Confirmed — 160 lines of dead settings code carrying latent bugs.**
`BulkExportViewModel.GetSettingsSpec`/`ApplySettings` (`BulkExportViewModel.cs:1271-1433`)
are never called: the class does not declare `IToolSettings` (line 21), and the only
renderer of that interface (`BuildSpecContent` in
`GlobalSettingsWindow.CeilingHeatmap.cs:26`) itself has no callers. The real Bulk
Export settings tab is hand-built in `GlobalSettingsWindow.ToolGroups.cs:248-291`.
The dead block already disagrees with reality (its `"zoompercent"` case checks
`value is int` while the spec renderer delivers `double` — would silently reset to
100; its `"single"`/`"file"` kinds aren't renderable at all) and the comment at
`BulkExportViewModel.cs:1173` ("see ApplySettings") points at it. Fix: delete the
block (and the misleading comment), or actually wire `IToolSettings` up — but not
both surfaces.

**BulkExport-1-3 · Low · Confirmed — DWG placeholder string can be selected as a setup name.**
With no DWG setups in the project, `BuildS5Dwg` (`BulkExportViewModel.cs:1028-1033`)
puts the literal "(No DWG setups found in project)" into the combo; selecting it
stores it in `_dwgSetup`, and the run later reports "DWG setup '(No DWG setups
found in project)' not found". Guard: don't wire `onChange` when the list is the
placeholder.

**BulkExport-1-4 · Low · Confirmed — mode/show-all toggles silently wipe the S1 selection.**
`RefreshMultiSelect` (`BulkExportViewModel.cs:350-370`) rebuilds the picker and
clears `_selectedNames` with no message. Switching Sheets↔Views arguably must
clear; toggling "Show all non-template views" losing a 50-item selection is
avoidable (re-seed the still-eligible ids via `SetTree`'s selected-ids overload,
as Bulk Rename's S2 does).

### Pass 2 — Step flow & workflow logic

**BulkExport-2-1 · High · Confirmed — the Print Sets note contradicts what the run actually does. DECISION NEEDED.**
`labels.printSetsNote` tells the user "NWC/IFC are per-view and always export from
the Step 1 selection", but `BulkExportEventHandler.Execute` (lines 208-218) skips
NWC/IFC entirely whenever any print set is checked (`nwcInPack`/`ifcInPack` warn +
skip; `ExportPrintSetMode` never exports them). One of the two must change:
(a) make the code match the note — when print sets are checked, still run the
NWC/IFC branches of `ExportIndividualMode` against the S1 selection; or
(b) fix the note to say NWC/IFC are skipped while print sets are checked.
Option (a) matches the note's promise and is what a user reading the UI expects —
recommended.

**BulkExport-2-2 · Medium · Confirmed — the review step never warns about formats that will be wholly skipped.**
`ReviewWarning => null` always (`BulkExportViewModel.cs:1125`). NWC/IFC enabled in
Sheets mode, or (today) with print sets checked, sail through S9 showing "Formats:
PDF, NWC" and then export nothing for NWC. Per the framework, the pre-run summary
must honestly state what is about to happen — populate `ReviewWarning` for these
combinations (the S3 `modeHint` exists but S9 is the last thing the user reads).

**BulkExport-2-3 · Low · Confirmed — Save-print-set with a blank name is a silent no-op.**
`SaveCurrentSelectionAsPrintSet` (`BulkExportViewModel.cs:478`) returns without
feedback; the handler's `printSetNameRequired` message exists but is unreachable
from this path (and would be invisible anyway — see BulkExport-3-1).

### Pass 3 — Output log

**BulkExport-3-1 · High · Confirmed — print-set save failures are invisible to the user.**
`handler.OnError = msg => DiagnosticsLog.Warn(...)` (`BulkExportViewModel.cs:492`).
A duplicate set name makes `ViewSheetSetting.SaveAs` throw; the user clicks Save
and *nothing happens* — the error goes only to diagnostics.log. Surface it in the
window (inline message next to the save row).

**BulkExport-3-2 · Medium · Confirmed — fatal catch reports `fail = 1`, discarding failures already counted.**
`onComplete(pass, 1, skip)` (`BulkExportEventHandler.cs:231`) — if 5 exports
already failed before the fatal exception, the result strip shows 1 failed while
the log shows 5+ failure lines. Should be `onComplete(pass, fail + 1, skip)`.

**BulkExport-3-3 · Low · Confirmed — progress can overshoot its scale in print-set mode.**
A set whose members no longer exist advances `done` by the *original* member count
(`BulkExportEventHandler.cs:298`) while `total` only counted still-existing members
(line 274-275) — `done * 90 / total` can exceed 90.

**BulkExport-3-4 · Low · Confirmed — the "skipped" count mixes units.**
One skip per blocked *format* (NWC in Sheets mode, line 161) vs one per *element*
(missing DWG setup, `skip += elements.Count`, line 499) vs one per *set*
(`packNoItems`, which also logs at "fail" severity while incrementing `skip`).
The "skipped N" result chip isn't interpretable. Pick one unit (per intended
output file) and align.

**BulkExport-3-5 · Low · Confirmed — cancel line diverges from the house pattern.**
`log.stoppedExport` ("… {0} export(s) written; files so far preserved") lacks the
"of M" total; Bulk Rename and other tools use `common.log.stoppedByUser`
("{0} of {1} processed; work so far preserved"). Align (pass the total).

### Pass 4 — Silent-failure audit

**BulkExport-4-1 · High · Confirmed (impact Needs Windows test) — the chosen DWG export setup is never actually applied.**
`ExportOptionsFactory.BuildDwgOptions` (`ExportOptionsFactory.cs:59-70`) verifies
the named `ExportDWGSettings` exists, then returns `new DWGExportOptions
{ MergedViews = true }` — plain defaults. The setup's layer mappings, units,
colors, etc. never reach the export; the S5 combo and the `dwgNote` ("The export
setup must already exist…") are a fiction. Fix: return
`found.GetDWGExportOptions()` from the matched settings element (or
`DWGExportOptions.GetPredefinedOptions(doc, setupName)`); both APIs exist in the
2024 DLL. **Affects Print View too** (`PrintViewEventHandler.cs:151` uses the same
factory). Windows test A confirms the visible difference.

**BulkExport-4-2 · Medium · Confirmed — print-set handler can throw outside its try/finally.**
`BulkExportPrintSetHandler.Execute` line 28 dereferences
`app.ActiveUIDocument.Document` *before* the `try` — if the document was closed
between `Raise()` and execution, the NRE escapes the handler and the `finally`
payload-clear never runs. Both sibling handlers null-guard
(`app.ActiveUIDocument?.Document` / explicit null check). Move the deref inside
the try with the same guard.

**BulkExport-4-3 · Medium · Confirmed — TEMPORARY debug logger ships user/machine data into the export folder. DECISION NEEDED.**
`BulkExportDebugLogger` (whole file, flagged "remove before release") writes
`BulkExportDebug_<timestamp>.log` — including machine name and username — into the
user's chosen output folder on **every** run; that folder is typically a
deliverables directory that gets shared. It is constructed *before* the
OutputFolder guard (`BulkExportEventHandler.cs:82` vs 102), so an empty folder
means a relative-path write attempt into Revit's working directory (swallowed by
its bare `catch { }` blocks, which themselves violate the DiagnosticsLog rule).
Decision: remove it now, or keep until NWC/IFC is confirmed stable on Windows —
if kept, at minimum construct it after the folder guard and drop the
machine/username header.

**BulkExport-4-4 · Medium · Needs Windows test — duplicate resolved filenames silently overwrite each other.**
Nothing de-duplicates resolved names within a run. A pattern with no per-item
token (e.g. `{ProjectName}`) or two items resolving identically (fallback names,
same view name after sanitising) means each export overwrites the previous file
while the log claims N passed — N files reported, 1 on disk. Fix: track resolved
names per run; on collision append ` (2)`, ` (3)`… and log it. Windows test B
confirms Revit's PDF/DWG overwrite behaviour (expected: silent overwrite).

**BulkExport-4-5 · Low · Confirmed — bare `catch { _window = null; }` in the window-reuse guard.**
`BulkExportCommand.cs:37` (and identically `BulkRenameCommand.cs:39`) swallow the
dispatcher exception without `DiagnosticsLog.Swallowed`. Route it.

**BulkExport-4-6 · Low · Confirmed — the fatal catch never reaches diagnostics.log.**
`BulkExportEventHandler.cs:227-232` logs the fatal exception to the run log and
the (temporary) debug file, but not `DiagnosticsLog.Error`. Once BulkExport-4-3
removes the debug file, the stack trace is lost entirely. Add
`DiagnosticsLog.Error("BulkExport: fatal", ex)`.

### Pass 5 — Performance

**BulkExport-5-1 · Medium · Confirmed (cost Needs Windows test) — titleblock pre-flight is an N+1 collector.**
`BulkExportEventHandler.cs:143-150` runs one `FilteredElementCollector` per
selected sheet. For a 500-sheet export that is 500 scoped collectors before the
first export starts. Replace with one unscoped collector over
`OST_TitleBlocks` → group by `OwnerViewId` → set lookup. Windows test C times it.

No other findings: exports are inherently per-item API calls, combined PDF is one
call, no `Regenerate` in any loop, no redundant re-scans.

### Pass 6 — Memory & lifetime

**BulkExport-6-1 · Medium · Confirmed — print-set handler callbacks root the closed window.**
`SaveCurrentSelectionAsPrintSet` parks `OnCreated`/`OnError` closures (capturing
the ViewModel *and* live step UI via `rebuildList`) on the session-long static
`App.BulkExportPrintSetHandler`, and its `finally` clears only `Name`/`MemberIds`.
`OnWindowClosed` (`BulkExportViewModel.cs:29-36`) nulls the main handler's
callbacks but not the print-set handler's — after a save, the closed window's VM
and step content stay rooted until the next save or forever this session. Null
them in `OnWindowClosed` (and/or in the handler's `finally`).

Otherwise clean: run handler clears `SelectedIds`/`PrintSets` in `finally`, VM
implements `IToolCleanup`, no global-event subscriptions, no view activation.

### Pass 7 — Cancellation, transactions & re-run safety

Clean apart from the cancel-line text (BulkExport-3-5) and counters above:
`RunState.CancelRequested` is tested in every loop; file exports need no commit
fall-through; the print-set DWG loop restores the swapped `FilenamePattern`
before returning; IFC's per-item transaction is required by the API; re-runs
deterministically overwrite (acceptable for an export tool once BulkExport-4-4
handles intra-run collisions).

### Pass 8 — Text & externalization

Key completeness: **PASS** — every `AppStrings.T("export.bulkExport.*")` key
exists in the JSON, and no JSON key is unused.

**BulkExport-8-1 · Low · Confirmed — logs say "pack", the UI says "print set".**
`packNoItems`, `nwcInPack`, `ifcInPack`, `pdfPackOk/False/Fail`, `dwgPackNoSetup`
all use the retired "pack" term. Also `packNoItems` says "no matching items in
selection", but print-set membership is explicitly *not* gated by the S1
selection (`BulkExportEventHandler.cs:265-268`) — the real condition is "no
members still exist". String table below.

**BulkExport-8-2 · Low · Confirmed — filename preview shows a non-US date.**
`UpdatePreview` (`BulkExportViewModel.cs:730`) renders the IssueDate sample as
`dd/MM/yy`. Preview-only (the real value comes from the sheet parameter), but a
US user reads 07/16/26 as expected, not 16/07/26. Change the sample format to
`M/d/yy` (code change, not JSON).

**BulkExport-8-3 · Low · Confirmed — "Warning:" prefixes duplicate the warn channel.**
`droppedWarn` and `noTitleblock` start with "Warning:" while already pushed at
"warn" severity. String table below.

**BulkExport-8-4 · Low · Confirmed (cross-cutting) — result-strip nouns/chips are hardcoded English.**
`ResultNoun => "files"` and chip labels ("failed", "skipped") bypass AppStrings —
but so do all ~15 other tools' (`"markers"`, `"views"`, `"dims"`…). This is a
project-wide convention, not a Bulk Export defect; fixing it only here would
create inconsistency. Flagged for a one-time project-wide decision.

---

## Bulk Rename

### Pass 1 — Inputs & validation

**BulkRename-1-1 · Low · Confirmed — `Run()` uses null-forgiving `_runHandler!`/`_runEvent!`.**
`BulkRenameViewModel.cs:567,576`. The constructor accepts nulls (preview host);
Bulk Export guards with `if (_handler == null || _event == null) return;`. Match it.

Otherwise strong: every step gated by `IsValid`, the plan-driven preview validates
the operation before the Run button enables, steppers clamp ranges, and the run
handler re-reads authoritative values from the live document.

### Pass 2 — Step flow & workflow logic

**BulkRename-2-1 · Medium · Confirmed — {Seq} numbering order is fixed and never stated.**
Sequential/Token numbering follows a hard-coded order — sheets by current number,
views by type-then-name (`OrderedEntries`, `BulkRenameViewModel.cs:391-409`) — and
the UI never says so, nor offers reordering. The preview makes the result visible,
which mitigates, but a user renumbering sheets has no way to control which sheet
gets which number other than pre-arranging current numbers. Minimum fix: a hint
line under the steppers ("Numbered in current sheet-number order" / "…by view type,
then name"). Reordering support is a larger feature — flagged for a decision, not
silently designed.

`IStepAware` contract honored (S2/S3 rebuilt on activation, S1→S2/S3 pushed on
target change); no conditional steps; no picker-inside-picker; the review step is
plan-accurate including collision/empty warnings. 

### Pass 3 — Output log

**BulkRename-3-1 · Low · Confirmed — "Raising Revit external event…" is developer-speak.**
`BulkRenameViewModel.cs:575` / `log.raising`. Reword or drop (string table below).

Otherwise exemplary: per-item skip reasons with old→new values, a
`Complete — N renamed, N skipped, N failed` line, the `nonFatal` diagnostics
counter, the shared stopped-by-user line, and an explicit `nothingToDo` line for
the zero-item case.

### Pass 4 — Silent-failure audit

**BulkRename-4-1 · High · Confirmed — case-only renames on unique fields are wrongly skipped as collisions.**
`BulkRenameEngine.Plan` seeds `used` (OrdinalIgnoreCase) with every selected item's
own current value (`BulkRenameEngine.cs:140-145`), then flags any case-insensitive
match as `Collision` (line 164). So Find&Replace "LEVEL"→"Level" on view names
computes `"LEVEL 1"` → `"Level 1"`, finds its *own* old value in `used`, and skips
with "value already in use" — which is false (it's the same element; Revit accepts
a case-change rename). Fixing name casing is a bread-and-butter bulk-rename job
and it currently cannot be done at all on views or sheet numbers. Fix: before the
collision check, treat `newValue` equal-ignore-case to the item's **own**
`oldValue` as a valid `Change` (also verify Revit accepts it — Windows test D
covers both this and BulkRename-4-2).

**BulkRename-4-2 · Medium · Needs Windows test — is Revit's uniqueness really case-insensitive?**
The engine's comment claims OrdinalIgnoreCase "mirrors Revit's behaviour"
(`BulkRenameEngine.cs:131`). Unverified. If Revit actually allows `"Level 1"` and
`"LEVEL 1"` as distinct view names, the plan over-skips legitimate renames as
collisions. Windows test D.

**BulkRename-4-3 · Low · Confirmed** — the bare `catch { _window = null; }`
(`BulkRenameCommand.cs:39`), same as BulkExport-4-5.

Everything else passes: `Parameter.Set`'s bool return is checked and thrown,
`v.Name =` is wrapped per-item with `DiagnosticsLog.Swallowed` + run-log line, no
unawaited tasks, no ignored nulls (`GetElement` null → logged skip).

### Pass 5 — Performance

No findings. Single transaction, one collector for existing values, O(n) engine,
no regeneration, no re-scans.

### Pass 6 — Memory & lifetime

No findings. Handler clears `OrderedIds`/`Config` in `finally`; VM implements
`IToolCleanup` and nulls all three callbacks; no global-event subscriptions; no
view activation.

### Pass 7 — Cancellation, transactions & re-run safety

**BulkRename-7-1 · Medium · Confirmed — shift-renumbering mostly fails by design. DECISION NEEDED.**
Plan frees an item's old value only for *later* items (`used.Remove(oldValue)` on
`Change`, `BulkRenameEngine.cs:171-175`), and items are processed in ascending
current-number order. The classic "shift sheet numbers up by one" (101,102,103 →
102,103,104) therefore renames only the **last** sheet — the rest are skipped as
collisions with their still-unrenamed neighbors. The preview shows this honestly
(no lying), but the tool refuses one of the most common renumbering jobs. Options:
(a) **two-phase rename** — inside the same transaction, first set every `Change`
item to a temporary unique value, then to its final value; handles shifts *and*
swaps (A↔B) which CLAUDE.md notes trip Revit's transient check — recommended;
(b) order-aware processing (descending when shifting up) — cheaper but only fixes
monotone shifts. Needs your pick before implementing.

Otherwise textbook: cancel tested per item, logs the common stopped line, `break`s
and falls through to `tx.Commit()` preserving applied renames; failure options
configured (`SetClearAfterRollback`, delayed mini-warnings); collisions pre-checked
*and* the setter still wrapped; re-runs recompute from the live document and skip
unchanged items — no duplicate garbage possible.

### Pass 8 — Text & externalization

Key completeness: **PASS** — all `linkviews.bulkRename.*` keys exist and are used;
`common.log.stoppedByUser` exists in `common.json`.

**BulkRename-8-1 · Low · Confirmed — the tool lives in three different homes.**
Files under `Source/Tools/Sheets/BulkRename/`, namespace
`LemoineTools.Tools.LinkViews.BulkRename`, strings under
`linkviews.bulkRename.json`, ribbon on the Sheets panel. Purely mechanical churn
to unify (namespace + string prefix `sheets.bulkRename`) — flagged for a decision
since it touches JSON key renames and every `AppStrings.T` call in the tool.

**BulkRename-8-2 · Low · Confirmed — no result chips/noun.**
Bulk Rename doesn't implement `IRunResult`, so the finish strip shows no "N
renamed" breakdown while sibling tools show theirs. Low-cost add
(`ResultNoun => "renames"`, chips renamed/skipped/failed).

Tone is otherwise clean US-professional throughout both tools.

---

## String table (current → proposed)

Rewrites to apply in bulk (Python `str.replace()` per the CLAUDE.md rule) once
approved. Rows marked ⚑ depend on a decision above.

| Key | Current | Proposed |
|---|---|---|
| ⚑ `export.bulkExport.labels.printSetsNote` | "…NWC/IFC are per-view and always export from the Step 1 selection." | If BulkExport-2-1 option (b): "…NWC/IFC are per-view and are skipped while any print set is checked — clear all print sets to export them." If option (a): keep as is (code changes instead). |
| `export.bulkExport.log.packNoItems` | "Pack '{0}': no matching items in selection — skipped." | "Print set '{0}': none of its members exist in the model anymore — skipped." |
| `export.bulkExport.log.nwcInPack` | "NWC: Skipped — NWC is per-view and cannot be exported as part of a pack. Clear packs (Step 2) to export NWC." | "NWC: Skipped — NWC is per-view and can't be grouped into a print set. Uncheck all print sets (Step 2) to export NWC." *(moot if 2-1 option (a))* |
| `export.bulkExport.log.ifcInPack` | (same pattern) | (same pattern as above, IFC) |
| `export.bulkExport.log.pdfPackOk` | "PDF (pack): {0}.pdf [{1} sheets]" | "PDF (print set): {0}.pdf [{1} sheets]" |
| `export.bulkExport.log.pdfPackFalse` | "PDF failed — pack '{0}': export returned false." | "PDF failed — print set '{0}': export returned false." |
| `export.bulkExport.log.pdfPackFail` | "PDF failed — pack '{0}': {1}" | "PDF failed — print set '{0}': {1}" |
| `export.bulkExport.log.dwgPackNoSetup` | "DWG setup '{0}' not found — pack '{1}' DWG skipped." | "DWG setup '{0}' not found — print set '{1}' DWG skipped." |
| `export.bulkExport.log.droppedWarn` | "Warning: {0} element(s) could not be resolved — they may have been deleted since the window was opened." | "{0} element(s) could not be resolved — they may have been deleted since the window was opened." |
| `export.bulkExport.log.noTitleblock` | "Warning: sheet {0} has no titleblock — paper size may be incorrect." | "Sheet {0} has no titleblock — paper size may be incorrect." |
| `export.bulkExport.log.stoppedExport` | "Stopped by user — {0} export(s) written; files so far preserved." | Retire; use `common.log.stoppedByUser` ("Stopped by user — {0} of {1} processed; work so far preserved.") with the run total. |
| `export.bulkExport.labels.saveAsPrintSetHint` | "…named Revit print set (View ▸ Sheet Set)…" | "…named Revit print set (View/Sheet Set)…" — matches Revit's own dialog label. |
| `linkviews.bulkRename.log.raising` | "Raising Revit external event…" | "Starting rename…" (or delete the line — the framework already shows run start). |

Not JSON but text-adjacent: `BulkExport-8-2` (preview date `dd/MM/yy` → `M/d/yy`
in `BulkExportViewModel.cs:730`).

---

## Appendix — Windows test scripts

**Test A — DWG setup actually applied (BulkExport-4-1).**
1. In a project with a customized DWG export setup (change a layer color/name
   mapping in File → Export → CAD Formats → DWG → Modify setup), run Bulk Export
   DWG for one sheet with that setup selected in S5.
2. Also export the same sheet manually via File → Export → DWG with the same setup.
3. Open both DWGs (or compare layer tables). **Current expectation: the Bulk
   Export DWG ignores the setup** (default layers) — confirming the bug. After the
   fix, the two should match.

**Test B — silent filename overwrite (BulkExport-4-4).**
1. Set the filename pattern to a constant (e.g. just `{ProjectName}`), select 3
   sheets, PDF individual (Combine off), export.
2. Expected today: log shows 3 "PDF: …" pass lines; output folder contains **one**
   PDF (last writer wins), no warning anywhere. Note whether Revit throws or
   overwrites — either way the log lies about 3 files.

**Test C — titleblock pre-flight timing (BulkExport-5-1).**
1. In a large project (300+ sheets), select all sheets, start a PDF export, and
   time the gap between pressing Run and the first "PDF:" log line.
2. Re-time after the one-collector fix. Expect the gap to shrink noticeably on
   large sets; if it's already sub-second, downgrade the finding to Low.

**Test D — case-only rename & Revit case-sensitivity (BulkRename-4-1 / 4-2).**
1. Manually (in Revit UI) rename a view "TEST VIEW" → "Test View". Expected: Revit
   accepts (same element, case change). If it refuses, BulkRename-4-1's fix needs
   a temp-name hop.
2. Try creating a second view named "test view" while "Test View" exists. If Revit
   **allows** it, its uniqueness is case-sensitive and the engine's
   OrdinalIgnoreCase comparison over-skips (BulkRename-4-2 fix: switch to Ordinal).
   If Revit refuses, current comparison is correct.
3. In Bulk Rename, run Find&Replace "TEST"→"Test" on that view. Current
   expectation: preview shows "(collides — will skip)" — confirming BulkRename-4-1.

**Test E — print-set NWC/IFC behavior (BulkExport-2-1, only if option (a) chosen).**
After the fix: check one print set, enable NWC, Views mode with a 3D view selected
in S1. Expect the print set's PDF/DWG **plus** the NWC of the S1 3D view, and no
`nwcInPack` skip line.
