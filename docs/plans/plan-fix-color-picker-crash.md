# Plan: Fix Color Picker Crash in Revit

## Problem

`ed3c300` introduced two `Popup` controls with `StaysOpen=false` and
`AllowsTransparency=true` into `LemoineColorPickerPanel`:

1. `_setPopup` — the named color-set dropdown
2. An inline "Remove" popup — created fresh on every right-click, never cleaned up

`StaysOpen=false` hooks `ComponentDispatcher.ThreadFilterMessage` to intercept
global Win32 messages. In Revit's hosting environment this fires in unexpected
states and crashes the process. `AllowsTransparency=true` on popup HWNDs also
misbehaves inside Revit's compositor.

## Fix strategy: replace Popups with inline toggle panels

Remove both `Popup` instances entirely. Replace each with a collapsible
`Border`/`StackPanel` that lives in the normal visual tree — no HWNDs, no
message hooks, no layered windows.

### Set-selector dropdown → inline toggle panel

- The dropdown `Border` button stays as-is.
- Clicking it toggles the visibility of an inline `StackPanel` that sits
  directly below the button in the right column's `StackPanel`.
- The panel renders the same set list + "New Set" entry row.
- Clicking outside is not detected (no `StaysOpen` equivalent needed): the
  panel closes when you pick a set, create a set, or click the dropdown button
  a second time. That's sufficient UX.
- `_popupJustClosed` flag and `_setPopup` / `Popup` type are removed.

### Right-click "Remove" → inline overlay panel or context menu

Two options:

**Option A — WPF ContextMenu** (standard WPF control, safe in Revit):
- Set `swatch.ContextMenu = new ContextMenu { ... }` with a single "Remove"
  `MenuItem`.
- `ContextMenu` is a WPF popup type too, but it uses the standard Win32
  context-menu message path, not `ComponentDispatcher.ThreadFilterMessage`.
  It is safe in Revit.

**Option B — Inline overlay** (no popup at all):
- On right-click, show a small `Border` overlay positioned absolutely over the
  swatch inside the project grid, with a "Remove" label. Dismiss on any click.

Plan uses **Option A** (ContextMenu) for the Remove action — it is the
standard pattern, shorter code, and is safe in Revit.

## Files changed

| File | Change |
|------|--------|
| `Source/Lemoine/Controls/Color/LemoineColorPickerPanel.xaml.cs` | Replace `_setPopup` (Popup) with `_setDropdown` (StackPanel toggle); replace right-click Popup with ContextMenu; remove `_popupJustClosed` |

No XAML, no project file, no other files need changes.

## What is NOT changed

- ColorSet data model, persistence (XML), set management logic — untouched.
- Recent swatches, SV/hue/alpha bitmap rendering — untouched.
- Window XAML and `LemoineColorPickerWindow.xaml.cs` — untouched.
- `SizeToContent` on the window — left as-is (not confirmed as crash cause).
