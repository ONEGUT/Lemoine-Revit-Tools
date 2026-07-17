# Web Migration — Questions & Decisions Log

Questions accumulated during the full migration pass (step-flow tools + static
windows). Logged instead of asked, per instruction; review after the pass.
Decisions I made unilaterally are marked **DECIDED** with rationale — flag any
you want changed.

## Decisions made during the pass

- **DECIDED — Feature flag instead of 30 Developer buttons.** Every production
  command now opens the web version when the "Web UI" flag is ON (Developer
  panel toggle button; persisted machine-wide), and the WPF version when OFF
  (default). This keeps R25 (parallel until verified) without flooding the
  ribbon. The three existing parallel Web dev buttons (Push Coords, Delete
  Filters, Web Pilot) remain until cleanup.

## Open questions

*(appended as encountered)*

## BulkRename — live preview placement (divergence)
The WPF S3 panel rebuilds a 12-row rename preview on every keystroke. The web shell
can only rebuild a whole step's inputs (which would steal focus from the text field
being typed in), so the preview rows render on the review step S4 — which the shell
already auto-refreshes — and S3's summary line shows the live change count.
Question: acceptable, or should the shell gain a partial "update one input" channel
so the preview can live inside S3 like WPF?

## File-dialog filters
WebInput.FileBrowser now carries an optional Windows dialog filter string
("CAD files|*.dwg"); stepflow.js passes it through the browseFile action and the
C# side applies it to OpenFileDialog (malformed strings fall back to no filter,
logged via DiagnosticsLog.Swallowed). Used by Projected Ceiling Grids.

## PruneTree promoted to WebToolBase
The eligible-leaf BrowserTree pruning (WPF picker's eligibleIds) is now a shared
protected helper on WebToolBase; ReplicateDependentViews' private copy removed.

## ClashFinder — window re-activation after slab pick (open)
The WPF wizard implements IWindowActivatable so a slab pick (which pulls Revit's
main window forward) re-activates the tool window when the pick resolves. The web
shell has no activate hook yet — after a pick the user clicks back to the tool
window manually. If this matters, WebStepFlowWindow can expose an Activate
callback wired through WebToolLauncher's keyed-window map.

## CeilingHeatmap — gradient preview (divergence)
The WPF ramp step draws a live Low→Mid→High gradient bar. The web port shows the
three color swatches (native color inputs) without the combined gradient strip.
A CSS linear-gradient preview could be added to the colorPicker row group later.

## Discover — live scan log dropped (divergence)
The WPF Discover S3 step shows a per-line colour-coded scan log (✓/✗/·) plus a
progress bar, and auto-navigates S2→S3→S4 as the scan runs and completes. The web
port shows a single status + percentage line on S3 (rebuilt on progress and
completion) and relies on the user pressing Confirm to walk S3→S4→S5 — the web
shell has no tool-driven "navigate to step N" channel (WPF's IStepNavigable
NavigateRequested). If the auto-advance matters, WebStepFlowWindow could grow a
"navigate" bridge message the tool raises alongside StepInputsChanged.

## Discover — per-rule row layout (divergence)
The WPF S4 lays each discovered rule as one grid row (checkbox · colour swatch ·
inline-editable name · category · count). The web port stacks three inputs per
rule (include toggle, colour picker, name field) under a per-trade group hint.
Functionally identical; visually taller. A future compound "rule row" web
component could restore the single-line layout.

## Global Settings window — General tab migrated; other tabs deferred (review)
Built a web settings surface (settings.html + lib/settings.js + WebSettingsWindow +
WebSettings payload builder), gated by the same WebUi flag. The General tab is fully
web-backed (theme cards with live colour previews + active badge, UI-size choice rows,
language rows, diagnostics log path + Open Log). Theme/size/language route through the
same AppSettings setters, so they persist and propagate live to every open web window
via ThemeChanged/UiSizeChanged. The other 8 ribbon-group tabs render a "managed in the
classic window" placeholder and still open the WPF window's content when the flag is off.

REMAINING SETTINGS TABS (deferred — each is its own sizeable effort):
- Filters (globalSettings.Filters.cs, ~3620 lines): AutoFilters trade editor, per-trade
  rule rows, clash-definition management, colour ramps. The largest single surface in the
  app; needs a drag-reorderable rule editor web component.
- Naming (~877 lines): user-token editor (bind to Revit parameter GUID-first), per-tool
  default-pattern rows. Needs a token-CRUD web surface.
- Dimensions (~263), ToolGroups per-tool settings (~379), CeilingHeatmap (~197),
  LinkViews (~30): mostly spec-driven field rows — portable with the existing WebInput
  factories once a "settings tab = list of WebInput rows" spec is added to WebSettings.

Question: proceed tab-by-tab in a follow-up pass, or is a settings-tab spec model
(reusing WebInput rows, like the step-flow tools) preferred so the simpler tabs
(Dimensions/ToolGroups/CeilingHeatmap/LinkViews) can be batch-ported first?

## Other static windows — not yet migrated (review)
These bespoke WPF windows are outside the step-flow + settings surfaces and were not
touched this pass. Each needs its own web surface decision:
- FiltersSettingsWindow / LegendSettingsWindow (standalone editors)
- ClashDefinitionsWindow (clash-rule CRUD)
- ToolsOverviewWindow (tool gallery/launcher)
- LinkAuditWindow (link display-mode audit table)
- ColorPickerWindow (the shared swatch/hex picker — note: the web colorPicker input
  added for CeilingHeatmap already covers the inline-swatch use; a full standalone
  picker window may not be needed).
Recommend prioritising by usage; ClashDefinitions and ToolsOverview are the most-opened.

## Auto Filters web port — deferred WPF extras (review)
The web Auto Filters window covers the full single-rule editing surface, history,
templates and view application. Three WPF-only extras were deliberately deferred:
multi-select over rule rows with batch field apply, the merge-selected-rules flow
(merge into one / create combined), and drag-reorder across trades. If any of these
is used day-to-day, they slot into the existing action vocabulary.

## Legend Creator web port — preview + placement (review)
The web preview is a client-side approximation (white page, groups in row lanes,
swatch+label lines) rather than the WPF pixel-metric preview. Group placement is
append-per-row + whole-card drops; the WPF single-live-insertion-marker lane grid
(drag groups between/within rows) is not yet rebuilt — groups can be added, renamed,
deleted and filled, but not dragged to reorder. Flag if row-level rearranging matters
before the WPF window retires.
