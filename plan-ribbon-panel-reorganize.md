# Plan — Ribbon Panel Reorganization + Per-Group Settings Tabs

Branch: `claude/ribbon-panel-reorganize-uzuxbf` (approved layout, mockup shown & approved).

## 1. Ribbon reorganization (`Source/App.cs`)

Replace the 8 `T0x`-prefixed panels + Testing dump with **9 named panels**:

| Panel | Tools |
|-------|-------|
| Filters & Legends | Auto Filters · Legend Creation |
| Ceilings | Ceiling Heatmap · Ceiling Grids ▾ (Make/Project/Reproject) |
| Views | Bulk Views by Level · Duplicate Views ▾ · Explode by Trade |
| Sheets *(new)* | Place Dependent Views · Align Sheet Views · Bulk Rename |
| Export | Bulk Export · Print View |
| Modify | Split Elements ▾ · Extend Walls |
| Clash | Clash Definitions · Finder & Dimension · Finder & Elevation · Refine Dimensions |
| Copy from Link *(new)* | Copy Linear · Copy Grids · Copy Elements |
| Settings | Settings |

- Distinct Segoe MDL2 glyph per button (several currently collide on the Copy/ViewAll glyph).
- "Testing" panel dissolved; its promoted tools rehomed (Place Dep. Views, Align Sheet Views → Sheets; Copy* → Copy from Link).

## 2. Removals (3 tools)

Delete ribbon button, `App.cs` field + registration, command class, and tool files for:
- **Create Sheets** — `Commands/Testing/CreateSheetsCommand.cs`, `Tools/Testing/CreateSheets/*`
- **By Discipline** (Link Views — Discipline) — `Commands/T03-LinkViews/LinkViewsDisciplineCommand.cs`, `Tools/T03-LinkViews/LinkViewsDiscipline*.cs`
- **UI Debug / debugger** — `Commands/Debuggers/DebugToolCommand.cs`, `Tools/Debuggers/*`

(Note: this removes the developer debug harness that CLAUDE.md references — done per explicit user request.)

## 3. Settings window — per-group tabs, section per tool (`GlobalSettingsWindow`)

Nav becomes: **General** + one tab per ribbon group. Within each tab, a **section per tool that has persisted default settings**; tools without defaults are hidden (per user choice). Groups whose tools have no defaults show a short "No default settings yet" note.

Settings-bearing sections (read/write the existing `*Settings.Instance` singletons, reusing the Dimensions-tab helpers `AddCfgStepper` / `LemoineToggleSwitches` / colour swatch):
- **Ceilings**: Ceiling Heatmap (low/mid/high colours, elev tolerance, place tags), Make Ceiling Grids (output folder, subfolder).
- **Views**: Bulk Views by Level (buffer, cluster threshold, cut offset).
- **Export**: Bulk Export (output folder, formats, filename patterns, PDF/raster options).
- **Clash**: Finder & Dimension = the existing rich "Dimensions" content, relocated here.
- **Copy from Link**: Copy Linear (mode, lengths, re-run flags), Copy Elements (re-run flags).

Empty group tabs: Filters & Legends (each opens its own window), Sheets, Modify.

## 4. Post-change

- Grep for dangling references to removed tools.
- Silent-failure scan per CLAUDE.md.
- Commit + push to the branch.
