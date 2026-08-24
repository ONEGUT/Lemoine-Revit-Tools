# Plan — Upgrade & Link Models UI fixes

Branch: `claude/upgrade-links-ui-cmgrc9` (designated).

## 1. Files & Placement — original name above, editable name below, bigger box

**Problem.** `StepFlowWindow` is 540 px wide. `FileRowGrid()` is a 5-column grid
(`4 | * | 118 version | 168 placement | 40 remove`), which leaves the star column ~153 px —
so the "Save as" `TextBox` renders ~120 px wide and clips almost every real model name.
The file's **original** name is never shown at all; only its folder is.

**Change (`UpgradeLinksViewModel.BuildFileRow` + `BuildHeaderRow`, replacing `FileRowGrid`).**
Each file becomes a 3-line card instead of a single 5-column row:

| Line | Content |
|---|---|
| 1 | original file name (mono, `LemoineText`, ellipsized, full path as tooltip) · version badge · remove button |
| 2 | source folder (mono, `LemoineTextDim`, ellipsized) |
| 3 | editable "Save as" box (star ≈ 260 px, `LemoineFS_MD`, `MinHeight` = `LemoineH_Input`) · `.rvt` · placement picker (150 px) |

- Rows are `Border`s in a `StackPanel`, separated by a 1 px top border (`LemoineBorder`);
  the header becomes a `Border` with `LemoineRaised` so the background spans the full width
  (today's `Grid.Margin` inset leaves gaps at the edges).
- Header columns collapse to two: `Original → Save as` (star) and `Placement` (150), aligned
  with line 3. The `Version` header label is dropped — the badge is self-describing.
- `colFile` string value changes to `Original → Save as`; the now-unused `colVersion` key is removed.

## 2. Destination — accent highlight only on the real choice

**Problem.** `RebuildDestCards` highlights the `Local` wrapper (accent border + `LemoineAccentDim`
background) whenever `_dest != Cloud`, and then highlights the selected sub-card inside it — two
nested accent surfaces. When the host is not a cloud model, `Local` is the only top-level card, so
its highlight carries no information at all.

**Change.**
- `_hostIsCloud == false` → the `Local` wrapper is not rendered. `Selected folder` and
  `Current location` become top-level (`sub: false`) cards; only the selected one is accented.
- `_hostIsCloud == true` → `Local` stays as a plain group heading (`LemoineBorder`, transparent,
  no cursor, no click handler) that always contains both sub-cards. The accent goes only to the
  selected sub-card, or to the `Cloud` card.
- Both sub-choice click handlers gain an `if (_dest != …)` guard, so clicking the already-selected
  card no longer tears down and rebuilds the live `FolderBrowser` under the user's cursor.

New/changed helpers: `BuildLocalGroup`, `BuildSelectedFolderCard(bool sub)`,
`BuildCurrentLocationCard(bool sub)`; `BuildLocalSubCards` is removed.

## Files touched

- `Source/Tools/Setup/UpgradeLinksViewModel.cs`
- `Strings/en/upgradeLinks.json`

No handler, settings, or run-path changes — this is presentation only.
