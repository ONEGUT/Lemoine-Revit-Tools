# Plan — Link Views (Level): View Geometry settings → per-run in S1

## Goal
Move the three View Geometry settings off the global "Link Views" settings tab and
surface them as per-run controls in the tool's S1 "Source Documents" step.

Settings affected (all on `LinkViewsLevelSettings.Instance`):
- **XY buffer** — margin around each building cluster crop box (ft)
- **Cluster threshold** — max room-edge gap for union-find cluster merging (ft)
- **Cut plane offset** — height above level elevation for the plan cut plane (ft)

Decision (carried from prior session): **Move to S1 only** — remove the global tab.

## Why this is low-risk
`LinkViewsLevelRunHandler` already reads `LinkViewsLevelSettings.Instance` directly,
so per-run controls that write to that singleton need **no run-handler changes**.

## Files changed
- `Source/Tools/T03-LinkViews/LinkViewsLevelViewModel.cs`
  - S1: append a "VIEW GEOMETRY" section with three `LemoineInlineStepper` rows that
    write to `LinkViewsLevelSettings.Instance` and `Save()` on change.
  - Remove the `ILemoineToolSettings` implementation (`GetSettingsSpec` / `ApplySettings`)
    and drop the interface from the class declaration.
- `Source/Lemoine/GlobalSettingsWindow.xaml.cs`
  - Remove the `("t04", "Link Views")` nav def and its `case "t04"` content branch
    (the only consumer of the removed spec), plus the now-dead using.
- `Source/Lemoine/T03-LinkViews/GlobalSettingsWindow.LinkViews.cs`
  - Update the stale tab comment.

## Out of scope
- `LinkViewsDisciplineViewModel` (no global tab; untouched).
- Run handler, settings DTO, and persistence (unchanged).
