# Plan — Navisworks Plugin (shared framework with LemoineTools)

## Goal

Add a Navisworks plugin that reuses the LemoineTools WPF UI (StepFlowWindow,
controls, theme/settings system) with as little duplicated code as possible, and
delivers four capabilities Navisworks handles poorly natively:

1. **Discover → Search Sets** — scan each model by category/property and create
   search sets (mirroring the Revit "Discover Rules" tool and the attached
   `Clash Detective & Search Sets.xml` structure). Always **update existing**
   sets by name rather than creating duplicates.
2. **Areas & Levels model** — break the project into areas defined by gridlines
   and/or infinity, per level. Levels toggle on/off; each level defaults to the
   area layout of the floor below.
3. **Clash matrix** — generate/update clash tests crossing the trade search
   sets (the "weave"). Results default to **New** (treated as *Draft*).
4. **Clash grouping + area/level tagging + filtering UI** — group results, tag
   each group with its floor/area (computed from the clash point), filter groups
   by floor/area, and bulk-promote a whole area's status (e.g. New → Active).

## Confirmed decisions

- **Target:** Navisworks **2026** → **`net48`** (.NET Framework 4.8). Navisworks
  2026 hosts plugins on .NET Framework 4.8 (confirmed by the crash + Autodesk's
  placement doc), the **same TFM as Revit 2024** — so the shared framework is a
  clean single-TFM share with **no multi-targeting**. (Original assumption of
  `net8.0-windows` was wrong: that is Revit 2025+, not Navisworks.)
- **Areas:** **Tag the outputs, don't constrain the inputs.** Search sets stay
  trade-only and dynamic; area/level becomes post-process metadata on clash
  results via a separate grouping pass. No static per-area selection sets, no
  combinatorial test explosion.
- **Draft:** Use the native **New** status as "Draft". Promote groups to
  **Active**. (Navisworks status enum is fixed: New/Active/Reviewed/Approved/
  Resolved — custom statuses are not possible.)
- **Grouping (v1):** Start with **distance-based proximity grouping** — within a
  single clash test, results whose clash points fall within **X feet** of each
  other are clustered into one group (union-find on the clash centre points,
  X configurable). The floor/area tagging + filter UI is the **later, advanced**
  layer built on top of this.
- **Base branch:** `claude/focused-volta-5psr8p`.

## Key feasibility findings

- `Source/Lemoine/` (the UI framework) is **host-agnostic**: only 3 of 68 files
  touch the Revit API (`LemoineFailureCapture.cs` + two tool-specific windows),
  and `StepFlowWindow` has **zero** Revit references. Sharing is low-risk.
- Navisworks needs **no `ExternalEvent`** — plugin code runs on the main thread,
  so the tool's `Run()` can perform API work directly (marshalled to the main
  thread if the window lives on its own STA thread). The existing
  `ILemoineTool.Run(pushLog, onProgress, onComplete)` contract is already
  host-agnostic and needs **no change**.
- Navisworks API coverage for the asks:
  - Search sets: `Document.SelectionSets` + `Search`/`SearchConditions` — maps
    directly to the XML `findspec/conditions`. Update-by-name supported.
  - Property discovery: `ModelItem.PropertyCategories` → categories/properties.
  - Clash: `DocumentClash` → `ClashTest` with A/B `ClashTestSelection` pointing
    at search sets; results group/ungroup; status settable per group.
  - Clash result center point available for area/level assignment.
- **Constraints to design around:**
  - No spatial search predicate (can't express "inside rectangle" as a rule) →
    area binding must be a geometry post-process on results, not on inputs.
  - No arbitrary persisted custom properties on a clash result → floor/area
    identity lives in group name/structure + our own sidecar metadata.
  - **Cannot build on Linux** (per CLAUDE.md). All builds/tests run on the
    user's Windows machine; this session delivers code + integration steps.

## Code-sharing strategy — Linked source files first, extract a library later

Constraints discovered that drive this:

- `LemoineTools.csproj` is SDK-style at the **repo root** and globs `**/*.cs`
  (only `LemoinePreview/` is excluded). Any sibling project folder must be
  explicitly `Remove`d or it gets double-compiled.
- `LemoineTheme.cs` hardcodes `pack://application:,,,/LemoineTools;component/`
  for the embedded **IcoMoonFree.ttf** icon font (a WPF `<Resource>`). No XAML
  hardcodes component URIs — this is the **only** assembly-name landmine.
- `GlobalSettingsWindow` is **partially Revit-coupled** (its per-tool partial
  classes configure Revit tools), so it is **not** part of the shareable core.
  Navisworks gets its own settings tabs.

**v1 mechanism — linked files (Revit project untouched):**

```
LemoineTools.sln
├── LemoineTools            (net48, Revit 2024)         — UNCHANGED, keeps building
├── LemoineNavisworks       (net48, Navisworks 2026)
│     ├── links  Source/Lemoine/*.cs + Controls/** + Templates/**  (the framework)
│     ├── links  Source/Resources/Fonts/IcoMoonFree.ttf  as <Resource>
│     └── own Source/  — Navis plugin entry + tools (Discover, Areas, Clash, UI)
```

Linked files compile **into** the Navis assembly (same-assembly access, so no
`InternalsVisibleTo` needed and no second copy of the framework loaded — the two
hosts are separate processes). The shared core is: `StepFlowWindow`, the
`Controls/` library, `Templates/`, theme + settings primitives, and the tool
contracts. **Excluded** from the link: `LemoineFailureCapture.cs`, the T05-Clash
window, the LegendCreator window, and the Revit-coupled `GlobalSettingsWindow`
(+ its T0x partials).

**Enabling fix (safe, helps both hosts, verifiable via the Revit build):** change
the hardcoded font pack URI to resolve the **executing assembly name at runtime**
(`Assembly.GetExecutingAssembly().GetName().Name`), so it is correct whether the
file compiles into `LemoineTools` or `LemoineNavisworks`.

**Later hardening (own phase, once the plugin works):** extract the shared core
into a real `net48` class library `Lemoine.Core` with `InternalsVisibleTo`, and
have both hosts reference it (both are net48, so no multi-targeting). Deferred because a
~100-file extraction is high-risk to the working Revit build and **cannot be
compiled in this Linux environment** to verify.

## Phases (each = its own branch, one logical change)

**Phase 0 — Enabling fix.** Make the font pack URI assembly-relative in
`LemoineTheme.cs`. Tiny, safe, verifiable through the existing Revit build (the
URI resolves to `LemoineTools` exactly as before). No behavior change. *(Done in
the opening commit alongside this plan.)*

**Phase 1 — Navisworks skeleton.** New `LemoineNavisworks` project
(`net48`, `UseWPF`) that **links** the shared framework files + the icon
font `<Resource>`, references the Navisworks 2026 API via install-path with a
`libs-navis/` fallback (mirroring the Revit csproj pattern), and adds it to the
`.sln`. A `[Plugin]` `AddInPlugin` entry + ribbon/tool button shows
`StepFlowWindow` hosting a trivial tool to prove the shared UI renders under
Navisworks. Window owner/threading per the skill's `references/navisworks.md`
(not present in this checkout — to be reconciled on Windows).

**Phase 2 — Discover → Search Sets.** `NavisDiscoverViewModel` mirroring the
Revit Discover step flow: pick models/categories → scan `PropertyCategories` →
review rules (include/exclude/rename/colour) → commit to `SelectionSet` search
sets with viewfolder nesting like the XML; **update existing by name**.

**Phase 3 — Areas & Levels model.** Tool settings: levels (toggle, elevation),
per-level area definitions (grid lines and/or infinity), inherit-from-below
default. Read grid geometry to build area rectangles; level Z-ranges from
elevations. Data/UI only here; consumed by Phase 5.

**Phase 4 — Clash matrix.** Generate/update the trade-pair clash tests (hard
clash, tolerance, ignore-same-file rule — matching the XML). Update-existing by
name. Selectable trade-pairs to avoid full-cross blowup. Results default New.

**Phase 5 — Clash grouping (distance-based v1).** Within each clash test, cluster
results whose clash centre points are within **X feet** of each other (union-find
on the points; X configurable) into one `ClashResultGroup`. Idempotent — re-derive
from geometry each run. **Advanced (later):** assign each result/group a
(level, area) from its clash centre point vs level Z-ranges + grid-defined area
rectangles; name groups with the floor/area identity; persist sidecar metadata.

**Phase 6 — Clash group filter + bulk status UI.** WPF UI to list groups, filter
by floor/area, and bulk-promote status across a whole area/group.

## Open items to confirm before Phase 0

- Branch to base this work from.
- Whether to vendor the Navisworks API DLLs into `libs/` now (needed for the
  Navis project to reference) or wire the install-path fallback only.
- Phase 2 first deliverable scope (full discover flow vs minimal search-set
  create/update to validate the API round-trip).
```
