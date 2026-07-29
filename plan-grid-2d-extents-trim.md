# Plan — Fix "0 trimmed, 22 skipped" in Align Sheet Views › Grid 2D extents

## Symptom

Running Align Sheet Views with **Grid 2D extents** ticked logs, for every target view:

```
[SHEET] Grids on 'VIEW': 0 trimmed, 22 skipped (not visible / not collinear).
```

The grids are the same grid elements, visible in both source and target view.

## Diagnosis

`TrimGrids` (`Source/Tools/Sheets/AlignSheetViews/AlignSheetViewsEventHandler.cs:484`):

```csharp
Curve? c = g.GetCurvesInView(DatumExtentType.ViewSpecific, sv)?.FirstOrDefault();
if (c == null) { skipped++; continue; }
if (!(doc.GetElement(g.Id) is Grid tg)) { skipped++; continue; }
tg.SetCurveInView(DatumExtentType.ViewSpecific, tv, c);
```

Two defects.

### 1. The datum extent type is never read or set

A Revit datum carries a per-view, per-end extent mode — the 3D/2D toggle in the UI —
exposed on `DatumPlane` (verified against the checked-in `libs/RevitAPI.dll` metadata for
Revit 2024):

| Member | Signature |
|---|---|
| `GetDatumExtentTypeInView` | `DatumExtentType (DatumEnds, View)` |
| `SetDatumExtentType` | `void (DatumEnds, View, DatumExtentType)` |
| `CanBeVisibleInView` | `bool (View)` |
| `IsCurveValidInView` | `bool (DatumExtentType, View, Curve)` |
| `GetCurvesInView` | `IList<Curve> (DatumExtentType, View)` |
| `SetCurveInView` | `void (DatumExtentType, View, Curve)` |

`DatumEnds` = `End0 | End1`; `DatumExtentType` = `Model | ViewSpecific`.

The code uses **none** of the first four. It asks the source view for `ViewSpecific`
curves on grids that are almost certainly still on the default **`Model` (3D)** extents in
that view, so there are no view-specific curves to return — `FirstOrDefault()` is `null`
and all 22 fall through the first `skipped++`. Symmetrically, `SetCurveInView(ViewSpecific, …)`
on a target view whose grid ends are still `Model` has nothing to write into; the target's
ends must be switched to `ViewSpecific` first.

This is a uniform, all-or-nothing failure, which matches "always 0 trimmed / all 22 skipped".

### 2. `skipped` conflates three unrelated causes, two of them silently

`skipped++` fires on (a) `c == null`, (b) the element re-lookup not being a `Grid`, and
(c) any thrown exception. Only (c) reaches `DiagnosticsLog.Swallowed`, so paths (a) and (b)
leave **no** entry in `%AppData%\LemoineTools\diagnostics.log` and no `issuesRecorded` line
in the run log. The message text "(not visible / not collinear)" names neither of the two
causes that actually fire. This violates the CLAUDE.md rule that a zero result must say why.

Also: `GetCurvesInView` returns `IList<Curve>`; `FirstOrDefault()` silently discards the
remaining curves of a multi-segment grid.

### Was it fixed on `claude/sheet-views-alignment-review-j2qog1`?

No. That branch is a single commit, `80f5ec9` *"Fix Align Sheet Views viewport placement and
harden view matching"*, which is already an ancestor of this branch (it is **not** in `main`).
Its diff covers the footprint-centre alignment maths, rotated viewports and preview
diagnostics — it does not touch `TrimGrids` or any datum API.

## Changes

### `Source/Tools/Sheets/AlignSheetViews/AlignSheetViewsEventHandler.cs`

Rewrite `TrimGrids`:

1. Skip grids where `!g.CanBeVisibleInView(sv)` or `!g.CanBeVisibleInView(tv)` — counted as
   *not visible*, the only case the current message names.
2. Read the source curve from the extent type the source view **actually** uses:
   `g.GetDatumExtentTypeInView(DatumEnds.End0, sv)`, then `GetCurvesInView(thatType, sv)`.
   That is the displayed geometry the tool's doc comment promises to copy.
3. Before writing, put the target's ends into 2D:
   `SetDatumExtentType(DatumEnds.End0, tv, DatumExtentType.ViewSpecific)` and the same for
   `End1`.
4. Pre-check with `IsCurveValidInView(DatumExtentType.ViewSpecific, tv, c)`; a `false`
   result is counted as *rejected* and logged, instead of being discovered as a throw.
5. `SetCurveInView`, still wrapped in try/catch → `DiagnosticsLog.Swallowed`.
6. Replace the single `skipped` counter with separate buckets — `trimmed`, `notVisible`,
   `noSourceCurve`, `rejected`, `errored` — and log every non-zero bucket by name. Route
   the previously silent `noSourceCurve` / element-lookup paths through
   `DiagnosticsLog.Warn` so they land in `diagnostics.log`.
7. Report when a grid yields more than one curve rather than dropping the extras silently.

### `Strings/en/testing.alignSheetViews.json`

Replace `gridsTrimmedSome` with per-cause keys (`gridsNotVisible`, `gridsNoSourceCurve`,
`gridsRejected`, `gridsErrored`, `gridsMultiSegment`); keep `gridsTrimmedNone` and
`noGrids` as-is.

## Not in scope

- Viewport placement, title alignment, scope-box / crop inheritance — untouched.
- No change to the tool's UI or step flow.

## Verification

Cannot be compiled or run here (Linux; `UseWPF` needs the Windows desktop SDK). Needs a
Windows build and one plot: run with **Grid 2D extents** ticked against a source/target
sheet pair and confirm the log now reports either `N trimmed` or a named reason per grid.
