# Function Review Framework

A repeatable, front-to-back review that can be run against any group of tools
(typically one ribbon panel / one `Source/Tools/<Group>/` folder at a time).
UI **rendering** is out of scope — layout, styling, and visual polish are being
reworked separately. Everything else about a tool is in scope, including what
the UI *says* and what order the steps come in.

## Ground rules

- **Report first, fix on approval.** The review produces a findings report per
  tool. Nothing is changed until the user picks which findings to fix. Approved
  fixes land on one branch per group.
- **Static analysis with runtime flags.** This environment cannot build or run
  Revit. Every finding is tagged either **Confirmed** (provable from the code)
  or **Needs Windows test** (comes with a short test script the user can run on
  their machine — steps, expected result, what to look for in the run log or
  `diagnostics.log`).
- **Text findings are a current-vs-proposed table.** Every awkward, stiff, or
  non-US string is listed with its `Strings/en/*.json` key, the current text,
  and a proposed rewrite. The user approves; rewrites are applied in bulk via
  a Python `str.replace()` script (per the CLAUDE.md externalization rule).
- **Retired tools are skipped by default.** Tools deactivated behind flags
  (e.g. `ShowRetiredSetupTools`) are excluded unless the user asks for them.

## Severity scale

| Level | Meaning |
|---|---|
| **Critical** | Can crash Revit, corrupt the model, or silently destroy user data |
| **High** | Wrong results, silent failure, or a run that lies to the user |
| **Medium** | Performance/memory waste, missing cancel/progress, confusing workflow |
| **Low** | Text tone, log phrasing, minor inconsistency with sibling tools |

## The eight passes

Each tool in the group gets all eight passes, in order. Findings are numbered
`<Tool>-<Pass#>-<n>` (e.g. `AlignCoordinates-3-2`) so approvals are unambiguous.

### Pass 1 — Inputs & validation

- Inventory every input: pickers, steppers, text fields, selections, settings
  carried over from a previous run.
- Boundary validation per CLAUDE.md: user input, Revit API returns, file I/O.
  Internal calls already guaranteed by the framework are *not* flagged.
- Edge-case inputs walked on paper: empty selection, zero links, non-workshared
  document, wrong active view type, pinned elements, metric vs. imperial units,
  a link not loaded, a closed workset.
- Defaults: does each input start in a sensible state, and do persisted
  settings restore correctly (public DTOs, `XmlSerializer` rules)?

### Pass 2 — Step flow & workflow logic

- Walk the step sequence as a human user: is the order the order you'd think
  in? Does any step depend on a later step's information?
- `IStepAware` / `IConditionalSteps` contracts honored — no stale content built
  eagerly at construction, no conditional last step.
- Review/summary step accuracy: does the final pre-run summary honestly state
  what is about to happen (counts, targets, destructive actions)?
- UX-philosophy check (CLAUDE.md): no picker-inside-picker, unambiguous state,
  invalid options hidden not disabled, parent/child selection rules.

### Pass 3 — Output log: what it shows and what it hides

- Every meaningful outcome appears in the run log: created / skipped / failed
  counts, and explicit "Found 0 …" lines for zero-result collectors.
- Progress cadence: ~5% batched progress lines on long runs
  (`RunProgressReporter`), not just the progress bar.
- Failures routed, not swallowed: `RevitFailureCapture` / `RunLogSink` wired,
  per-item failures logged with element identity, warnings not buried.
- The log reads truthfully after the run: numbers match what actually happened,
  a cancelled run says what was preserved.

### Pass 4 — Silent-failure audit (whole tool, not just diffs)

The CLAUDE.md scan applied to every file in the tool:
- Empty or catch-and-discard `catch` blocks (must route through
  `DiagnosticsLog.Swallowed`/`Error` with context).
- Unawaited `Task` / `async void` outside event handlers.
- Ignored return values where failure is meaningful (null Revit API returns,
  `bool` success flags).
- Throwing setters not wrapped (view names, sheet numbers, grid names,
  template assignment across view types).

### Pass 5 — Performance (time to complete)

- `doc.Regenerate()` never per-item in a loop; one regen per run or per
  logical unit only.
- Batched API calls (`CopyElements` with many ids, chunked with per-element
  fallback), no N+1 collector patterns, collectors filtered as narrowly as
  possible (`OfClass`/`OfCategory` before LINQ).
- No redundant re-scans of data already captured earlier in the run.
- Where cost is inherently unknowable from reading (regen cost, large-model
  scaling), flag as **Needs Windows test** with a timing script.

### Pass 6 — Memory & lifetime

- Static `ExternalEvent` handlers clear per-run payloads (input lists, cached
  `View`/`Element` refs, scan results) in `finally` at end of `Execute`.
- ViewModels that park callbacks on static handlers implement
  `IToolCleanup.OnWindowClosed` and null them.
- Global-event subscriptions (`ThemeChanged`, `UiSizeChanged`) use named
  handlers detached on `Closed`; no anonymous-lambda leaks (crash class).
- View activation guarded (`PickerViewGuard` pattern) — no views left open.

### Pass 7 — Cancellation, transactions & re-run safety

- Looping handlers test `RunState.CancelRequested` at the progress boundary,
  log "Stopped by user — N of M processed; work so far preserved", and fall
  through to commit.
- Transaction hygiene: mutations grouped sensibly, failed items skip-and-log
  rather than abort the run, forced-modal and duplicate-type dialogs suppressed
  where CLAUDE.md prescribes.
- Idempotency: what happens on a second run? Stamped outputs reconciled,
  uniqueness collisions (grids, view names, sheet numbers) pre-checked and
  skip-and-logged, no duplicate garbage left behind.

### Pass 8 — Descriptive text & externalization

- Every user-facing string (labels, hints, tooltips, step titles, review text,
  log lines) goes through `AppStrings.T(...)`; deliberate-hardcode categories
  (logic tokens, glyphs, format specifiers) are respected.
- Key completeness: every `AppStrings.T("...")` key referenced in the group's
  `.cs` files exists in the corresponding `Strings/en/*.json` (flatten + regex
  diff, per CLAUDE.md).
- Tone pass: text reads like a US construction/BIM professional wrote it —
  plain, direct, contractions fine, no stiff or translated-sounding phrasing,
  US spelling (color, center, meter), imperial-first where units are named.
  Output is the current-vs-proposed table.
- Cross-tool consistency: sibling tools in the group phrase the same concepts
  the same way (e.g. all say "Skipped N (name already exists)" identically).

## Deliverables per group

1. `review-<group>.md` at the repo root:
   - One section per tool, findings numbered and severity-tagged, each with
     file:line, the problem, the proposed fix, and Confirmed / Needs Windows
     test status.
   - The string table (key | current | proposed) as its own section.
   - A short "Windows test script" appendix for every Needs-Windows-test item.
2. A chat summary: counts by severity, the handful of findings worth reading
   first, and anything that changes how the tool should work (flagged for a
   decision, not silently redesigned).
3. After the user approves specific findings: fixes on a branch named for the
   group (e.g. `review-fixes-setup`), one logical change set, with the standard
   post-change silent-failure scan before commit.

## First application — Setup panel

Active tools (Link Audit and Compare Grids are retired behind
`ShowRetiredSetupTools` and skipped):

| Tool | Files |
|---|---|
| **Upgrade Links** | `Source/Tools/Setup/UpgradeLinks{ViewModel,RunHandler,ScanHandler,Settings,Models}.cs`, `Strings/en/upgradeLinks.json` |
| **Align Coordinates** | `Source/Tools/Setup/AlignCoordinates{ViewModel,RunHandler}.cs`, `CoordinatesGeometry.cs`, `CoordinatesModels.cs` |
| **Push Coordinates to Links** | `Source/Tools/Setup/PushCoordinatesToLinks{ViewModel,RunHandler}.cs` (shares the Coordinates support files) |

Shared support files (`CoordinatesGeometry.cs`, `CoordinatesModels.cs`) are
reviewed once and findings attributed to the group, not a single tool.
Ribbon labels/tooltips for the panel (`Strings/en/ribbon.json` setup entries)
are included in Pass 8.

## Running it on the next group

Say "run the function review on <panel/folder>". The same eight passes,
deliverables, and severity scale apply; only the file inventory section
changes. No re-approval of the framework is needed — only of the findings it
produces.
