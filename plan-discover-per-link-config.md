# Plan: Discover Rules — Per-Link Category & Scan Config (Option B)

Branch: `claude/hopeful-fermi-bZEKc`

## Summary

Collapse the current S2 (global categories) and S3 (global scan config) into a
single new S2 "Configure Links" step. Each selected link gets its own accordion
card with an independent category picker and scan config grid. The step count
drops from 5 to 4.

## New step flow

| ID | Label            | Notes                                             |
|----|-----------------|---------------------------------------------------|
| S1 | Select Links    | Unchanged — link selection + trade name           |
| S2 | Configure Links | New — per-link accordion: categories + mode/param |
| S3 | Review Rules    | Was S4                                            |
| S4 | Confirm & Commit| Was S5                                            |

## Data model changes

`LinkEntry` gains three new fields (no new class needed):
- `List<string> SelectedCategories` — per-link category selection
- `List<ScanConfigRow> ConfigRows`  — per-link scan rows (rebuilt from above)
- `bool IsExpanded`                  — accordion expand/collapse state (default true)

Remove the two shared-state fields that S2+S3 used:
- `_selectedCategories` (IReadOnlyCollection<string>)
- `_scanConfigRows` (List<ScanConfigRow>)

## UI / field renames

| Old field        | New field        |
|-----------------|-----------------|
| `_s3ScanBtn`     | `_s2ScanBtn`     |
| `_s3ScanStatus`  | `_s2ScanStatus`  |
| `_s4Panel`       | `_s3Panel`       |
| `_s5Review`      | `_s4Review`      |

New field:
- `_s2CardsStack` (StackPanel) — rebuilt whenever S1 selection changes so S2
  always reflects the currently selected set of links.

## New/changed methods

| Method                          | Action                                        |
|--------------------------------|-----------------------------------------------|
| `BuildS1` / `BuildLinkRow`      | Wire S1 checkboxes to `RebuildS2Cards()`      |
| `BuildS2`                       | Full rewrite — StackPanel + cards scroll area + Discover button |
| `BuildLinkCard(link)`           | New — accordion card for one link             |
| `RebuildLinkConfigPanel(link, panel)` | New — fills config rows for a link, was `RebuildS3Panel` |
| `RebuildS2Cards()`              | New — repopulates `_s2CardsStack` from selected links |
| `BuildConfigHeader()`           | Renamed from `BuildS3Header`                  |
| `BuildConfigRow(row)`           | Renamed from `BuildS3ConfigRow`               |
| `OnScanButtonClick`             | Updated — specs now from `link.ConfigRows` per link |
| `OnScanComplete`                | Calls `PopulateS3` / `UpdateS4Summary`        |
| `BuildS3` / `PopulateS3`        | Renamed from `BuildS4` / `PopulateS4`         |
| `BuildS3Header` / `BuildS3RuleRow` | Renamed from S4 equivalents               |
| `BuildS4`                       | Renamed from `BuildS5`                        |
| `UpdateS4Summary`               | Renamed from `UpdateS5Summary`                |
| `IsValid`                       | 4 cases: S2 = `_scanComplete && count > 0`    |
| `SummaryFor`                    | 4 cases: S2 summary is scan status            |
| `Run`                           | Calls `UpdateS4Summary()`                     |

## Category groups

Extracted from `BuildS2` to a `static readonly Dictionary` field so all
link cards share the same reference without re-allocating per card.

## Files changed

- `Source/Tools/T01-AutoFilters/DiscoverViewModel.cs` — only file
