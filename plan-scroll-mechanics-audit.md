# Scroll Mechanics Audit & Consolidation Plan

## How scrolling currently works (5 overlapping mechanisms)

The wheel/scroll behaviour has accreted one layer per bug fix. Today a single
ScrollViewer can be touched by up to three of these at once:

1. **`OnScrollViewerWheel`** — global class handler on *every* `ScrollViewer`
   (`EnsureGlobalScrollBubbling`). Two jobs: (a) self-contained popups
   (`SelfContainedScroll` tag **or** `IsInsidePopup`) scroll themselves via
   `WheelScrollBy`; (b) otherwise the innermost scroller bubbles to its parent
   at its scroll limit.
2. **`WireBubblingScroll(sv)`** — per-instance `PreviewMouseWheel` handler,
   called 17×. Does the *same* "bubble at limit" as 1(b).
3. **`OnComboWheel`** — global class handler; a **closed** ComboBox passes the
   wheel to the page instead of changing selection.
4. **`OnComboLoaded`** — per-ComboBox owner-window redirect for the **open**
   dropdown (added this session).
5. **`WireComboWheelBubbling(combo)`** — per-instance, called 4×. Same as 3.
   Plus **`LemoineTagChipInput.OnOwnerPreviewMouseWheel`** — a second, separate
   owner-window redirect implementation duplicating 4's idea.

## Structural problems

1. **Redundant handlers stack up.** The global class handlers (1, 3) now do what
   `WireBubblingScroll` (2) and `WireComboWheelBubbling` (5) were added for. Every
   wired scroller carries both a class handler *and* an instance handler.

2. **The discover "menu" is triple-wired.** `LemoineMultiSelectTabs` wires its two
   inner scrollers with `WireBubblingScroll` in its ctor
   (`LemoineMultiSelectTabs.xaml.cs:40-41`), and `DiscoverViewModel` wires the
   *same* scrollers **again** on `Loaded`
   (`DiscoverViewModel.cs:416-420`). With the global class handler that is three
   layers of wheel handling on the category picker the user reports as broken.

3. **Combo dropdowns lean on the unreliable `IsInsidePopup`.** Nothing tags a
   ComboBox's internal scroller self-contained, so it depends on the tree-walk
   popup check (CLAUDE.md: "unreliable under Revit's hosting") *or* the new
   owner-window redirect — two different code paths, picked by where Windows
   routes `WM_MOUSEWHEEL`.

4. **Scroll units are non-deterministic.** The injected ComboBox template
   (`LemoineControlStyles.cs` `ComboBoxXaml` ~line 269) never sets
   `CanContentScroll`, so whether the dropdown scrolls by item or by pixel is left
   to framework defaults. `WheelScrollBy` then *guesses* the unit — the cause of
   the "one notch jumps top-to-bottom" report (48 treated as 48 items).

5. **Two owner-window redirect implementations** (tag-chip + combo) that should be
   one shared helper.

## Proposed consolidation (one source of truth)

1. **Single authoritative classification.** Keep `OnScrollViewerWheel` as the only
   per-scroller wheel handler. Decide self-contained vs. bubble from the
   `SelfContainedScroll` attached tag (authoritative) with `IsInsidePopup` as
   backstop only.
2. **Retire the per-instance wiring.** Make `WireBubblingScroll` /
   `WireComboWheelBubbling` thin no-ops (or remove call sites) so there is exactly
   one handler per scroller. Remove the duplicate wiring in
   `DiscoverViewModel.cs:416-420`.
3. **Make combo scroll units deterministic.** Set `CanContentScroll` explicitly in
   the ComboBox template so `WheelScrollBy` no longer guesses.
4. **One shared owner-window redirect helper** used by both the tag-chip popup and
   the ComboBox dropdown.
5. **Build a scroll debug harness** (`ILemoineTool` in `Source/Tools/Debuggers/`,
   per CLAUDE.md): one button per construct — page scroller, MultiSelectTabs,
   ComboBox dropdown, tag-chip popup, nested scrollers — that logs offset/extent
   and `CanContentScroll` on each wheel notch. This is how we pin the exact
   remaining behaviour on Windows instead of guessing (reading code has not
   converged).

## Files in scope

- `Source/Lemoine/LemoineControlStyles.cs` (handlers, combo template, helpers)
- `Source/Lemoine/Controls/Input/LemoineTagChipInput.xaml.cs` (share redirect)
- `Source/Lemoine/Controls/Input/LemoineMultiSelectTabs.xaml.cs` (single wiring)
- `Source/Tools/T01-AutoFilters/DiscoverViewModel.cs` (remove double-wiring)
- `Source/Tools/Debuggers/ScrollProbeViewModel.cs` (new harness) + a Developer button
- Audit-only review of the other 15 `WireBubblingScroll` call sites

## Notes

- Cannot build/run on Linux (net48 + WPF). The harness is for Windows verification.
- One logical change; will land on the current branch `claude/intelligent-carson-fKJpx`.
