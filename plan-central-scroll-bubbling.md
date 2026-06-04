# Plan — Central Scroll Wheel Bubbling

## Problem

Some scrollers (e.g. the category/match dropdown in the auto-filter rule editor)
scroll **down but not up**. Root cause is a standard WPF behaviour: a
`ScrollViewer` (and a *closed* `ComboBox`) marks the mouse-wheel event as
**handled** even when it is already pinned at its top limit and cannot move.
Because the event is marked handled, it never **bubbles** to the page behind it,
so the page can't scroll up while the cursor is over that control. Scrolling
down still moves the inner control's own content, so it "works".

The existing fix — `LemoineControlStyles.WireBubblingScroll(scrollViewer)` and
`WireComboWheelBubbling(comboBox)` — re-raises the wheel on the parent once the
inner control hits its limit. But it is wired **by hand at each call site**, so
it's easy to miss. Confirmed unwired spots include:

- `GlobalSettingsWindow.Filters.cs:1230` — match-type ComboBox (auto-filter)
- `FiltersSettingsWindow.xaml.cs:292` — legacy autocomplete ComboBox
- `ReplicateDependentViewsViewModel.cs:498` — ComboBox
- inner `ScrollViewer`s in `GlobalSettingsWindow.CeilingHeatmap.cs:28`,
  `ClashDefinitionsWindow.xaml.cs:127`, `LegendSettingsWindow.xaml.cs:346`,
  `MakeCeilingGridsViewModel.cs:257`, `ReplicateDependentViewsViewModel.cs:309`,
  `LinkViewsDisciplineViewModel.cs:120`

## Chosen approach (per user)

**Auto-wire centrally**, and make the wheel **pass through to the parent
scroller/window when the inner control hits its limit**.

## Design

Add a one-time, AppDomain-wide class handler registration in
`LemoineControlStyles`, invoked from `InjectInto(...)` (called by every Lemoine
window). A static guard ensures it registers exactly once.

### New: `EnsureGlobalScrollBubbling()`

```
private static bool _scrollBubblingRegistered;

internal static void EnsureGlobalScrollBubbling()
{
    if (_scrollBubblingRegistered) return;
    _scrollBubblingRegistered = true;

    EventManager.RegisterClassHandler(typeof(ScrollViewer),
        UIElement.PreviewMouseWheelEvent,
        new MouseWheelEventHandler(OnScrollViewerWheel), handledEventsToo: false);

    EventManager.RegisterClassHandler(typeof(ComboBox),
        UIElement.PreviewMouseWheelEvent,
        new MouseWheelEventHandler(OnComboWheel), handledEventsToo: false);
}
```

### `OnScrollViewerWheel`

- Ignore if `e.Handled`.
- Only the **innermost** ScrollViewer under the cursor acts: walk up from
  `e.OriginalSource` to the first `ScrollViewer`; if it isn't `sender`, return.
  (PreviewMouseWheel tunnels outer→inner, so without this guard an outer
  scroller at its own limit would steal the wheel before the inner one scrolls.)
- Compute `atTop = VerticalOffset <= 0`,
  `atBottom = VerticalOffset >= ScrollableHeight - 0.5`.
- If the inner SV can still scroll in the wheel direction → return (let it
  scroll normally).
- Otherwise mark `e.Handled = true` and re-raise a bubbling `MouseWheelEvent`
  on the visual parent, so the parent scroller / page continues the scroll.
  (Same mechanism as the proven `WireBubblingScroll`.)

### `OnComboWheel`

- If `IsDropDownOpen` → return (the open list scrolls normally).
- Otherwise mark handled and re-raise on the parent — so a closed ComboBox
  neither changes its selection on wheel nor traps page scrolling.
  (Same as `WireComboWheelBubbling`.)

### Call site

In `InjectInto(...)`, add `EnsureGlobalScrollBubbling();` near the top.

## Files changed

- `Source/Lemoine/LemoineControlStyles.cs` — add `EnsureGlobalScrollBubbling()`,
  `OnScrollViewerWheel`, `OnComboWheel`, and the `FindAncestorScrollViewer`
  helper; call from `InjectInto`.

No other files need editing: the global handler covers every existing and
future `ScrollViewer`/`ComboBox`, including all the unwired gaps above.

## What is intentionally left alone

- Existing `WireBubblingScroll` / `WireComboWheelBubbling` methods and their
  call sites stay. They're idempotent with the class handler (class handlers run
  first and mark the event handled, so the per-instance `+=` handlers, which use
  `handledEventsToo:false`, simply don't re-fire). Keeping them means windows
  that for any reason haven't called `InjectInto` still behave correctly.

## Known consideration

`RegisterClassHandler` is AppDomain-wide, so it affects every WPF
`ScrollViewer`/`ComboBox` in the process (Revit shares the add-in AppDomain).
The behaviour added is conservative and standard (bubble the wheel only when the
control is at its scroll limit / a combo is closed), so the effect on any
non-Lemoine WPF control is the generally-desirable "wheel passes through at
limits" — no value mutation, no scroll trapping.

## Verification

Cannot build on Linux (net48 + WPF is Windows-only). The change is small and
mirrors the already-proven `WireBubblingScroll`/`WireComboWheelBubbling` logic.
User to confirm in Revit that the auto-filter dropdowns and nested lists now
scroll both directions and pass through to the page at their limits.

## Silent-failure scan

Will run the post-change scan before reporting complete.
