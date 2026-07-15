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
