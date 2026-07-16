# Web Migration — Status & Remaining Work

> **Merged `main` (2026-07-16).** Reconciled the branch with main's review-fix wave
> (Align/Push Coordinates, Upgrade Links, Bulk Export/Rename, Splits, Ceilings). Conflicts
> resolved in ProjectedCeilingGrids command + BulkExport handler/ViewModel (kept the
> TokenResolver naming path and layered main's per-format uniqueness + loud degenerate
> fallback). **API drift fixed:** `UpgradeLinksViewModel.LabelToPlacement` (removed) →
> `TryLabelToPlacement`; **`AlignCoordinatesWebTool` re-ported** to main's redesigned four-way
> anchor model (Internal Origin / PBP / Survey / Grid Intersection + per-anchor Level, no more
> `ZSource`/`Overridden`). *Follow-up:* `PushCoordinatesWebTool` doesn't yet surface main's new
> `PublishReplace` opt-in (compiles; feature parity pending). All changes are read-verified —
> a Windows build is the confirmation step.


Snapshot of the WebView2 UI migration on branch `claude/webview2-testing-menu-9i6jz2`.
Cross-reference with `plan-webview2-ui-migration.md` (the authoritative rules + phases)
and `web-migration-questions.md` (open decisions). Last updated 2026-07-15.

---

## 1. Step-flow tools — 34 / 34 ported to `IWebTool`

**Every `IStepFlowTool` in the app now has a parallel web port.** Production commands
open the web version when the machine-wide **Web UI** flag is ON (Developer panel),
the WPF version when OFF (rule R25 — both stacks coexist until each is verified).

| Tool | Web port | Windows-verified? |
|------|----------|-------------------|
| Push Coordinates to Links | ✅ | ✅ confirmed (Revit 2026, pilot) |
| Web Pilot (harness) | ✅ | ✅ confirmed |
| Delete Filters from Project | ✅ | ⏳ pending |
| Print View | ✅ | ⏳ pending |
| Bulk Export | ✅ | ⏳ pending |
| Bulk Rename | ✅ | ⏳ pending |
| Bulk Views | ✅ | ⏳ pending |
| Views By Link / By Template / Bulk Duplicate | ✅ | ⏳ pending |
| Link Views to Level | ✅ | ⏳ pending |
| Replicate Dependent Views | ✅ | ⏳ pending |
| Place Dependent Views | ✅ | ⏳ pending |
| Align Sheet Views | ✅ | ⏳ pending |
| Explode View By Trade | ✅ | ⏳ pending |
| Scope Box Creator | ✅ | ⏳ pending |
| Make / Reproject / Projected Ceiling Grids | ✅ | ⏳ pending |
| Ceiling Heatmap | ✅ | ⏳ pending |
| Clash Finder / Clash Elevation Finder | ✅ | ⏳ pending |
| Refine Dimensions | ✅ | ⏳ pending |
| Copy Datums / Linear / From Link | ✅ | ⏳ pending |
| Compare Grids | ✅ | ⏳ pending |
| Align Coordinates | ✅ | ⏳ pending |
| Upgrade Links | ✅ | ⏳ pending |
| Discover | ✅ | ⏳ pending |
| Split By Cell / Grid / Level / Reference Plane | ✅ | ⏳ pending |
| Extend Walls | ✅ | ⏳ pending |

> The bulk of the pass is **code-complete but unverified on Windows** (this repo
> cannot compile on Linux). Verification is the current gating activity, not more porting.

### Known step-flow divergences from WPF (logged in `web-migration-questions.md`)
- **Bulk Rename** live preview moved to the review step (no partial "update one input" channel).
- **Discover** dropped the live per-line scan log + auto-advance (no tool-driven "navigate to step N" bridge message).
- **Ceiling Heatmap** shows three colour swatches instead of the combined Low→Mid→High gradient bar.
- **Discover** rule rows stack three inputs instead of one grid row.

---

## 2. Component library (`Source/Web/lib/`) — coverage

Every WPF input control has a web twin, verified rendering in headless Chromium:

Button · InlineStepper · TextField · SingleSelect · ToggleSwitch · SectionCard ·
WarnBanner · MultiSelectTabs (Hierarchy carets / indeterminate / DisabledItems / All-row) ·
CheckList · Review · FolderBrowser · FileBrowser · TokenInput · **BrowserTree** (Project
Browser view/sheet tree — folders, nested dependents, right-click-selects-descendants,
single-select) · NumberRange · SearchSelect · ColorPicker.

**View/sheet picker audit result:** every WPF tool that uses `BrowserTreePicker` maps to
`WebInput.BrowserTree`, fed by the real `BrowserTreeCapture` Project-Browser hierarchy. The
tab-style pickers that remain (Views-By-Link *levels*, Link-Views *levels*, template / trade /
category pickers) use `MultiSelectTabs` in the WPF originals too — they are faithful, not
regressions. No view/sheet tree was downgraded to a flat checkbox list.

### Not yet built as web components (needed only by unported windows below)
- Drag-reorderable rule-row editor (Filters settings)
- Token-CRUD editor (Naming settings)
- Legend drag/drop lane grid (Legend Creator)
- Colour ramp / gradient strip (standalone; inline colour input already exists)

---

## 3. Bespoke (non-step-flow) windows — remaining

| Window | Status | Notes |
|--------|--------|-------|
| **Global Settings** | 🟡 General tab web-backed; 8 tabs remain | See §4 |
| Filters Settings (standalone) | ❌ WPF only | Shares the Filters-tab editor (~3620 lines) |
| Legend Settings / Legend Creator | ❌ WPF only | Drag/drop lane grid — hardest surface (Phase 3 wave 5) |
| **Clash Definitions** | ✅ web | CRUD editor on `WebWindowBase` (`WebClashDefinitions`/`WebClashDefinitionsWindow` + `clashdefs.html`): definition sidebar, Group 1/2 editor (mode, source-doc tree, rules/categories tabs, element-pick via ExternalEvent), marking settings; auto-saves on close |
| **Tools Overview** | ✅ web | Field guide on `WebWindowBase` (`WebToolsOverviewWindow` + `toolsoverview.html`); category tabs, feeds/fed-by chip navigation, demo launch |
| **Link Audit** | ✅ web | Read-only report on `WebWindowBase` (`WebLinkAuditWindow` + `linkaudit.html`) |
| **Scope Box Manager** | ✅ web | Master/detail on `WebWindowBase` (`WebScopeBoxManager`/`WebScopeBoxManagerWindow` + `scopeboxmanager.html`/`lib/scopeboxmanager.js`): sidebar (All/Used/Unused filter, usage, bulk rename/delete), per-box editor (inline rename, size, duplicate/delete/bind-sides/split), views + datums sections, and 6 in-page overlays (assign views = browserTree, assign datums = tabs, bind sides, split, rename = tokenInput, delete confirm). Reuses the SAME `ScopeBoxManager` scan/run ExternalEvents unchanged. Code-complete, pending Windows verify. |
| Color Picker (standalone) | ✅ resolved | **No web port needed** — `ColorPickerWindow` is only an internal WPF helper (`PickColor`/`BuildColorPickerSwatch`) for CeilingHeatmap/Discover/ToolGroups/Filters, all already replaced by the inline `WebInput.Color` input (or the excluded Filters surface). |

> **`WebWindowBase`** now encapsulates the WebView2 host/bridge/theme/borderless-chrome shell
> for bespoke (non-step-flow) windows — Link Audit is the first consumer; Tools Overview and
> Clash Definitions will build on it too.

---

## 4. Global Settings tabs — buildout status

| Tab | WPF source (lines) | Status | Shape |
|-----|--------------------|--------|-------|
| General | General.cs (394) | ✅ web | theme cards, size, language, diagnostics |
| Dimensioning | Dimensions.cs (263) | ✅ web (spec) | field rows |
| Setup | ToolGroups.cs (379) | ✅ web (spec) | field rows |
| Ceilings | CeilingHeatmap group | ✅ web (spec) | colour + field rows |
| Views | ScopeBox group | ✅ web (spec) | field rows |
| Export | Bulk Export group | ✅ web (spec) | field rows |
| Copy | Copy group | ✅ web (spec) | field rows |
| Naming | Naming.cs (877) | ✅ web (bespoke) | token-CRUD master/detail editor (`lib/naming.js` + `WebNaming`) |
| Filters | Filters.cs (3620) | ❌ | AutoFilters trade editor + clash defs + colour ramps — **bespoke, largest** |

**Done:** a *settings-tab spec model* (`WebSettings.BuildTab` / `TabSpec`) renders each tab
as an ordered list of `WebInput` rows via the shared lemoine.js factories, each auto-saving
to the same tool settings singleton the WPF tab wrote to (same AppStrings keys, same value
transforms). The 6 field-row tabs plus the bespoke **Naming** token-CRUD editor are ported;
only **Filters** remains (its own follow-up — the largest single surface, needs a
drag-reorderable rule editor). The Naming tab needed the Revit parameter snapshot plumbed
into `WebSettingsWindow` (captured on the main thread in `OpenSettingsCommand`, exactly like
the WPF window). ⏳ Naming + spec tabs pending a Windows build + click-through verification.

---

## 5. Recommended next sequence

1. ✅ **Fix web settings click bug** (done — payload double-unwrap in `WebSettingsWindow`).
2. **Verify the 34-tool pass on Windows** (turn on Web UI flag, smoke each; log results to
   `plan-webview2-ui-migration.md` §5). This is the real gate before deleting any WPF.
3. **Settings spec model + 6 field-row tabs** (Dimensioning, Setup, Ceilings, Views, Export, Copy).
4. **Naming tab** (token-CRUD web component).
5. **Filters tab** (drag-reorderable rule editor) + fold in the standalone Filters Settings window.
6. **Other bespoke windows**, usage-ranked: Tools Overview, Clash Definitions, Link Audit,
   Scope Box Manager, Color Picker, Legend Creator.
7. **Phase 5 decommission** per the master plan once each tool/window is verified at zero WPF consumers.
