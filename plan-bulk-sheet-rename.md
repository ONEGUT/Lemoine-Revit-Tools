# Plan — Bulk Rename (Sheets & Views)

## Goal
A new Lemoine tool that bulk-renames **sheets and views** using four operations
the user selected: **Find & Replace**, **Add Prefix / Suffix**, **Sequential
numbering**, and **Token / pattern rename**. It must work for sheets (which have
`SHEET_NUMBER` + `SHEET_NAME`) **and** views (which have only a `Name` — no
number), with a live before→after preview.

## Key Revit constraint (views vs sheets)
Per CLAUDE.md, a non-sheet `View` exposes **no** `SHEET_NUMBER` / `SHEET_NAME`.
So the field vocabulary is **mode-aware**:
- **Sheets** → can rewrite **Number** and/or **Name**.
- **Views** → can rewrite **Name** only.

Both sheet numbers and view names must be **unique** in Revit (Revit throws on a
duplicate). The run handler enforces this: collisions (against existing elements
and within the batch) are **skipped and logged**, never silently dropped.

## Architecture (mirrors `ViewsBulkDuplicate`, the closest existing tool)

The tool plugs into `StepFlowWindow` via `ILemoineTool`. Reference template:
`Source/Tools/T03-LinkViews/ViewsBulkDuplicateViewModel.cs` (+ its Command and
RunHandler). Same STA-thread launch, same ExternalEvent pattern, same review step.

### Step flow (accordion)
1. **S1 — Target** — `LemoineSingleSelect`: *Sheets* | *Views*. Choosing this
   drives the downstream field/selection vocabulary, so S2 & S3 implement
   `IStepAware` and rebuild on activation.
2. **S2 — Select items** — `LemoineMultiSelectTabs` grouped by view type
   (views) or one "Sheets" group. Rebuilt on activation from the chosen target.
3. **S3 — Field & Operation** — the working step:
   - **Field** picker (`LemoineSingleSelect`): *Number* | *Name* for sheets;
     hidden/locked to *Name* for views.
   - **Mode** picker (`LemoineSingleSelect`): Find&Replace | Prefix/Suffix |
     Sequential | Token pattern. Changing the mode swaps the inputs panel below
     it (in-step child swap — no conditional-step framework needed).
   - **Mode inputs**:
     - *Find & Replace*: Find / Replace `LemoineTextField`s + Case-sensitive
       toggle + "Whole field only" toggle.
     - *Prefix / Suffix*: Prefix / Suffix `LemoineTextField`s.
     - *Sequential*: `LemoineTokenInput` pattern containing `{Seq}` (+ field
       tokens) and three `LemoineInlineStepper`s (Decimals=0): Start, Increment,
       Pad digits.
     - *Token pattern*: `LemoineTokenInput` over the field tokens
       (`{SheetNumber}`,`{SheetName}`,`{ViewName}`,`{ViewType}`,`{Seq}`).
   - **Live preview**: a mono before→after list of the first ~12 selected items
     plus a "N change · M collide" count, recomputed on every input change.
4. **S4 — Review & Run** — `ILemoineReviewable` summary (target, count, field,
   mode, change/collision counts) with a warning banner when collisions or empty
   results are detected. Carries the Run button (final step, always visible).

### Shared rename engine (Revit-free) — guarantees preview == result
`BulkRenameEngine` holds the operation config (enum + params) and a pure
`Compute(oldValue, tokens, index)` method. **Both** the S3 preview and the run
handler call it, so what the user previews is exactly what gets written.

## Files to ADD
| Path | Purpose |
|---|---|
| `Source/Tools/T03-LinkViews/BulkRename/BulkRenameEngine.cs` | Revit-free config + `Compute()` for all four modes |
| `Source/Tools/T03-LinkViews/BulkRename/BulkRenameViewModel.cs` | `ILemoineTool, ILemoineReviewable, IStepAware` — the 4 steps + preview |
| `Source/Tools/T03-LinkViews/BulkRename/BulkRenameRunHandler.cs` | `IExternalEventHandler` — transaction, per-element rename, uniqueness skip+log |
| `Source/Commands/T03-LinkViews/BulkRenameCommand.cs` | `IExternalCommand` — collect sheets/views on main thread, open `StepFlowWindow` on STA thread |

## Files to EDIT
| Path | Change |
|---|---|
| `Source/App.cs` | (1) declare `static BulkRenameRunHandler` + `ExternalEvent` (near line 62); (2) create them in `OnStartup` (near line 151); (3) add a Large `PushButtonData` "Bulk\nRename" to the **T03 Bulk Views** panel (glyph `` Rename) |

No `.csproj` edit needed — SDK-style project auto-includes new `.cs` files.

## Error-handling commitments (per CLAUDE.md)
- Run handler wraps the transaction; per-element failures are caught, counted as
  `fail`, and logged via `pushLog(...,"fail")` — never swallowed.
- Duplicate number / name collisions are detected up front, **skipped and logged**
  (`pushLog(...,"info")`), and surfaced as a `ReviewWarning` before the run.
- An empty/whitespace resolved value is a skip+log, not a silent rename.
- No `async void`, no unawaited tasks, no empty `catch`. A post-change silent-
  failure scan will be run before reporting complete.

## Out of scope (unless you want them)
- Undo grouping beyond Revit's single-transaction undo (already one undo entry).
- Renaming sheet *revisions* or other parameters — only Number/Name (sheets) and
  Name (views).
- Regex find/replace (plain substring only) — can add later if wanted.
