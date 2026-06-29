# Plan — Externalize & Localize User-Facing Text

## Goal

Move every string a user sees during a **normal run** out of hardcoded C# and into
editable, per-language resource files, rewriting each string as it moves so it sounds
human, uses American English, and matches what the tool actually does today.

This serves three purposes at once:
1. **Editability** — you can change wording yourself without touching code.
2. **Localization** — add a language by adding a folder; no code changes.
3. **Text sweep** — every migrated string is reviewed for voice, spelling, and accuracy in the same pass.

---

## Scope

### In scope — text a user sees in a normal run
- **Ribbon** — panel names, button labels, pulldown labels, and tooltips (`App.cs`).
- **Tool chrome** — each tool's `Title`, `RunLabel`, `StepDefinition` titles, `SummaryFor(...)` lines,
  control labels (`Label = "Import mode"`), dropdown options (`"Single file"`, `"Batch from folder"`),
  warning banners, and review-summary text.
- **Output logs** — the `pushLog(...)` lines that appear in the run's Output log (~134 call sites across ~28 files).
- **Shared chrome** — `StepFlowWindow` / control text: `"Confirm →"`, `"Run in Revit →"`, Reset/Cancel,
  the "Stopped by user — N of M processed" line, progress-cadence lines (`RunProgressReporter`), review headers.
- **Windows** — Global Settings tabs, Lemoine Settings window, Tools Overview window.

### Out of scope — debug / internal (left as hardcoded literals)
- `LemoineLog.Error/Warn/Swallowed(...)` context strings (diagnostics, not user-run text).
- Revit `BuiltInParameter` names, `OST_` category strings, schema GUIDs, internal ids/keys.
- Anything in `Source/Tools/Debuggers/` or the reserved Developer-panel harness.
- XML-serialized settings field names.

A string is **in scope only if it reaches the ribbon, a tool window, or the run Output log.**

---

## Resource file format & layout

### Folder structure (one folder per language)
```
Strings/
  en/
    common.json          ← shared StepFlowWindow / control / run-lifecycle text
    ribbon.json          ← all ribbon panel names, button labels, tooltips
    settings.json        ← Global Settings + Lemoine Settings windows
    overview.json        ← Tools Overview window
    autofilters.discover.json
    autofilters.applyFilters.json
    autofilters.deleteFilters.json
    autofilters.deleteFiltersFromProject.json
    autofilters.legend.json
    ceilings.projectGrids.json
    ceilings.makeGrids.json
    ceilings.reprojectGrids.json
    ceilings.heatmap.json
    linkviews.duplicate.json
    linkviews.byTemplate.json
    linkviews.replicateDependent.json
    linkviews.bulkRename.json
    linkviews.level.json
    modify.splitByLevel.json
    modify.splitByGrid.json
    modify.splitByReferencePlane.json
    modify.splitByCell.json
    modify.extendWalls.json
    clash.finder.json
    clash.elevationFinder.json
    clash.refineDimensions.json
    copy.fromLink.json
    copy.grids.json
    copy.linear.json
    export.bulkExport.json
    export.printView.json
    explode.byTrade.json
    testing.alignSheetViews.json
    testing.placeDependentViews.json
  fr/   ← (future) byte-for-byte same file set, same sections, same order
```
One JSON file per function that has display text or output logs, plus four shared files
(`common`, `ribbon`, `settings`, `overview`). Adding a language = copy the `en/` folder,
translate the values, leave keys/structure untouched.

### Every file uses the same sections in the same order
```jsonc
// ceilings.projectGrids.json
// Tool: Project Ceiling Grids  (command: ProjectedCeilingGridsCommand)
// Imports a DWG ceiling plan and projects its lines onto ceiling soffit faces.
{
  // title — toolbar title at top of the tool window
  "title": "Project Ceiling Grids",

  // runLabel — label on the final action button
  "runLabel": "Run in Revit →",

  // steps — accordion step titles, keyed by step id
  "steps": {
    "S1": "DWG Source",        // step 1 header
    "S2": "Review & Run"       // step 2 header
  },

  // summaries — collapsed one-line summary under each step ({0} = runtime value)
  "summaries": {
    "S1": "{0}",               // selected DWG file name
    "S2": ""                   // (no static summary)
  },

  // labels — control labels, dropdown options, banners shown in the steps
  "labels": {
    "importMode": "Import mode",
    "optionSingleFile": "Single file",
    "optionBatchFolder": "Batch from folder"
  },

  // log — Output-log lines; {0},{1}... are runtime values filled by string.Format
  "log": {
    "foundCeilings": "Found {0} ceilings to project onto.",
    "noCeilings": "No ceilings found in the active view.",
    "done": "Projected {0} of {1} ceiling grids."
  }
}
```
Rules:
- **Sections always present and in this order:** `title`, `runLabel`, `steps`, `summaries`, `labels`, `log`.
  Empty sections stay as `{}` / `""` so every file is structurally identical.
- **Every key carries a `//` comment** stating exactly where the text appears (which is the
  "documented what each line is linked to" requirement). Placeholders documented as `{0} = ...`.
- Files for windows without steps/logs (e.g. `ribbon.json`) keep only the sections they use,
  but those window files are themselves mutually consistent (ribbon files all share one schema, etc.).

### JSON-with-comments — no outside dependency (Approach A, approved)
Files are authored as JSON with full-line `//` comments documenting each entry. At load time a
small pre-pass strips any line whose first non-whitespace characters are `//` (whole-line comments
only — never `//` inside a string value such as a URL), then the cleaned text is parsed by the
framework's **built-in** JSON reader (`System.Web.Extensions` → `JavaScriptSerializer`, a GAC
framework assembly — **no NuGet, nothing to install**). Result: friendly commented-JSON authoring,
every line documented, zero outside dependencies.

---

## Code: central accessor

New file `Source/Lemoine/LemoineStrings.cs` (Revit-free, like `LemoineLog`):

- `LemoineStrings.Load(string cultureFolder)` — called once at startup (`App.OnStartup`).
  Reads every `*.json` under `Strings/<culture>/` (resolved relative to the executing assembly's
  folder, i.e. the deploy dir) into an **immutable** `Dictionary<string,string>` keyed
  `"<file>.<section>.<key>"` (e.g. `"ceilings.projectGrids.log.foundCeilings"`).
- `L.T(string key, params object[] args)` — returns the string; if `args` supplied, runs
  `string.Format`. Static `using` alias `L` for terseness at call sites.
- **Fallback chain:** current language → English (`en`) → return the key literal **and**
  `LemoineLog.Warn("LemoineStrings", "missing key: " + key)`. A missing/typo'd key is never silent.
- **Thread-safe by construction:** loaded once, never mutated; the per-STA-thread tool windows
  only read. (Matches the project's STA-thread discipline.)
- **Language is read once at load.** Changing language reloads the table and applies to tool
  windows opened *afterward* (open windows are not live-rebuilt — simpler and avoids re-parenting
  every control). This is acceptable and documented in the Settings UI.

### Language setting
- Add `Language` to the existing UI settings DTO (alongside theme / UI size). DTO must stay
  `public` (per the `XmlSerializer` rule in CLAUDE.md).
- Default: system culture if a matching `Strings/<culture>/` folder exists, else `en`.
- Add a **Language** picker to the Global Settings → General tab. Selecting a language saves the
  setting and reloads `LemoineStrings`; a small note says it applies to tools opened next.

### csproj
- `<None Include="Strings\**\*.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>`
  so the folder ships into the deploy dir next to the DLL.
- Add the `Newtonsoft.Json` PackageReference (if the recommended path is chosen).
- `Strings\**` belongs to this project — it is **not** added to the sibling-exclusion list.

---

## Migration approach (per file — this is also the text sweep)

For each tool, in this order:
1. Read the ViewModel **and** its EventHandler to collect every in-scope string and to confirm
   what the tool actually does (so the rewritten text is **accurate**, not just inherited).
2. Create `Strings/en/<file>.json` with all strings, each **rewritten** for: human voice,
   American spelling (e.g. "colour"→"color", "centre"→"center", "optimise"→"optimize"),
   and accuracy to current behavior. Document each line with a `//` comment.
3. Replace the literals in `.cs` with `L.T(...)`. Convert interpolated logs
   (`$"Found {n} ceilings"`) into templates (`L.T("...log.foundCeilings", n)` ↔ `"Found {0} ceilings"`).
4. Note any wording that was **inaccurate** to current behavior in the commit/summary so you can confirm.

Done in **phases on the one branch**, committing per phase:
- **Phase 1 — framework:** `LemoineStrings`, csproj wiring, language setting + picker, `common.json`.
- **Phase 2 — chrome:** `ribbon.json`, `settings.json`, `overview.json`.
- **Phase 3 — tools:** one commit per tool group (T01…T07, BulkExport, Testing).

---

## Risks / notes
- **Large diff across ~40 files.** Phasing + per-group commits keep it reviewable.
- **Cannot build on Linux** (per CLAUDE.md) — you compile/verify on Windows. I'll run the
  required post-change silent-failure scan and keep each phase self-contained.
- **Missing-key safety** is built into the accessor (logs + key fallback), so a mistyped key
  surfaces in diagnostics rather than showing blank text.
- Dynamic, high-cardinality values inside log lines (counts, names) stay as `{0}` placeholders —
  only the surrounding sentence is translated.

---

## Open items for approval
1. **JSON + Newtonsoft.Json dependency** (recommended) vs. **XML, no new dependency**.
2. **Which branch to base from?**
