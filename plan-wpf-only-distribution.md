# Plan — WPF-only distribution branch

## Goal

A distributable build where **only the WPF UI is reachable in Revit** — every
WebView2/HTML rework is disabled. Built for shipping the installer while the web
stack is still unverified on Windows.

Branch: `wpf-only-distribution`, based on `claude/plugin-distribution-review-5eoxz0`
(so it carries the Inno Setup installer *and* is WPF-only).

## Approach — hard-disable, don't strip (Option A)

The web/WPF split already runs through **one** machine-wide flag,
`WebUiSettings.Instance.Enabled` (default OFF = WPF). All ~35 production commands and
bespoke windows branch on it (directly, or via `WebToolLauncher.Enabled` which just
forwards to it). The only entry points to web are that flag and the Developer panel's
web buttons. So WPF-only is a two-edit change — no need to touch every command or
delete the web code.

Keeping the web code in place (just unreachable) means this branch stays trivially
mergeable with `main` and the web work can be re-enabled later. Physically stripping
web (60+ files, the WebView2 package, build targets) was rejected as risky and
un-mergeable.

## Changes

1. **`Source/Framework/Web/WebUiSettings.cs`** — `Enabled` getter always returns
   `false`; the setter becomes a no-op. Every `if (WebToolLauncher.Enabled)` /
   `if (WebUiSettings.Instance.Enabled)` now takes the WPF path, and a stale saved
   flag can never turn web back on. Persistence/DTO kept so the file still compiles
   identically to the web branch.

2. **`Source/App.cs`** — remove the entire **Developer** ribbon panel. All five of its
   buttons are web/dev tools (WebView2 Test, Web Pilot, Push Coords (Web), Delete
   Filters (Web), Web UI On/Off), so with the flag hard-off there is nothing left to
   show and no way to reach web. The command classes stay in the assembly (still
   compile); they're just not surfaced on the ribbon.

## Deliberately unchanged

- All `*WebTool.cs`, `Source/Web/` assets, `Source/Framework/Web/`, the WebView2
  `PackageReference`, and the `CopyWebAssets` / `CopyWebView2Loader` build targets stay.
  The web code still compiles; it is simply unreachable. (The installer will still ship
  the unused `Web\` folder + WebView2 DLLs — a harmless slight bloat. Trimming those
  from the WPF-only build is an optional follow-up, not done here to keep the build
  identical and avoid regressing the WebView2 "silent blank" safeguards.)

## Verification

Windows-only, same as the plugin: build, open Revit, confirm every tool opens its WPF
window and the Developer panel is gone. Cannot be verified on Linux.
