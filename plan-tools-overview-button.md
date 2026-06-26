# Plan — Tools Overview ribbon button

## Goal
Add a read-only **"Overview"** ribbon button (Settings panel, right of the gear) that opens a
themed window explaining every tool in the project and how they tie together. Layout = **Option A
(Pipeline + Catalog)**, approved from mockup `outA.png`.

## What it looks like (approved)
- Custom-chrome window in the live `LemoineTheme` (mirrors `GlobalSettingsWindow` shell).
- **Row 0** — title bar ("Lemoine Tools — Overview").
- **Row 1** — 6-stage **workflow strip**: Set Up → Prep Geometry → Build Views → Coordinate →
  Document → Output. Clicking a stage selects the first category in that stage.
- **Row 2** — body split: **left rail** of the 8 categories (with tool counts) + **right scroll pane**
  of full mini-docs **tool cards** for the selected category (icon, blurb, "fed by ← / feeds →"
  relationship chips, mono example line).
- **Row 3** — footer (Close + build-info line, same as settings).

## Files to add
| File | Purpose |
|---|---|
| `Source/Lemoine/ToolsOverviewWindow.xaml` | Window shell — same 4-row grid + named borders as `GlobalSettingsWindow.xaml` |
| `Source/Lemoine/ToolsOverviewWindow.xaml.cs` | Code-behind: theme apply, `LemoineControlStyles.InjectInto`, named ThemeChanged/UiSizeChanged handlers detached on `Closed`, builds toolbar / workflow strip / rail / cards / footer |
| `Source/Lemoine/ToolsOverviewCatalog.cs` | Pure data: `OverviewStage[]` → `OverviewCategory[]` → `OverviewTool[]` (name, glyph, blurb, feeds[], fedBy[], example). Single source of truth for the content, no Revit refs |
| `Source/Commands/OpenOverviewCommand.cs` | `IExternalCommand` — singleton open (mirrors `OpenSettingsCommand`): reuse if visible, else `new ToolsOverviewWindow().Show()` |

## Files to edit
| File | Change |
|---|---|
| `Source/App.cs` | In the **Settings** panel, after the gear `AddItem(...)`, add a second `PushButtonData("LT_Overview", "Overview", "OpenOverviewCommand", …)` with the Segoe MDL2 **Info** glyph `0xE946`. Add a static `ToolsOverviewWindow? Overview` field for the singleton (parallel to `GlobalSettings`) |

## Content source
Tool blurbs/relationships are authored in `ToolsOverviewCatalog.cs`, seeded from the existing ribbon
tooltips in `App.cs` plus the cross-tool ties already verified during research:
- Auto Filters trades → Explode View by Trade, Clash Definitions, Ceiling Heatmap, Legend Creation
- Copy from Link → Modify / Clash (host geometry)
- Clash chain: Definitions → Finder(s) → Refine Dimensions
- Views/Ceilings → Sheets (Place → Align → Rename) → Export

## Constraints honored
- **Not** a `StepFlowWindow` (wizard chrome is wrong for a reference) — modelled on
  `GlobalSettingsWindow`, opened on Revit's main STA thread like `OpenSettingsCommand` (no extra
  STA boilerplate).
- All colours/sizes via `SetResourceReference` — no literals.
- Named global-event handlers, detached on `Closed` (no leaked-subscription Revit crash).
- Read-only window: no Revit API calls, no transactions, no ExternalEvent.
- `ToolsOverviewWindow` excluded from sibling-project globs is N/A (it lives under `Source/`, already swept).

## Extension — Dummy run + clickable links (approved follow-up)
- Each tool card gets a **"Dummy run ▶"** button that opens the real `StepFlowWindow` (on its own
  dedicated STA thread, like the tool commands) with a catalog-driven, **Revit-free** demo tool:
  real Lemoine controls, the tool's actual steps, validation, and a simulated run (progress + log).
- **feeds/fed-by chips are clickable** — resolving the chip text to a tool (jump to its card + flash)
  or a category (jump to it). Unresolvable chips (e.g. "Discover") stay plain.
- New files: `OverviewDemoTool.cs` (Revit-free `ILemoineTool` engine + spec types) and
  `ToolsOverviewDemos.cs` (per-tool specs for all ~28 tools, step titles mirrored from the real
  ViewModels). The STA launch is wrapped in `try/catch → LemoineLog.Error` + latch release so a
  construction throw is logged, never hangs Revit.

## Out of scope
- Option B node-map / Map toggle (can be a later second view).
- Per-tool screenshots (mini-docs are text + relationship chips only).
- Pixel-faithful demo controls — the demos mirror each tool's real *step structure* with
  representative sample data, not its exact every-control layout.

## Verification
- Project builds on Windows only (can't compile on Linux — per CLAUDE.md). I'll do the post-change
  silent-failure scan and a structural self-review; you build/run in Revit.
