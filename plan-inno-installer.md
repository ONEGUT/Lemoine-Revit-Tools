# Plan — Inno Setup installer for Lemoine Tools

## Goal

Rebuild the local-install distribution the project had in its 2024-only days: a
double-clickable `setup.exe` (produced by **Inno Setup**) that installs the plugin
into Revit's add-ins folder, for one or more Revit years, with a clean uninstall.

The old script was never in this repo's git history (the repo is re-rooted at PR #66),
so this is authored from scratch. The `.iss` source lives in the repo; the `setup.exe`
is compiled on Windows (Inno Setup is Windows-only, same constraint as the plugin).

## Decisions (defaults — confirm before build)

| Decision | Choice | Why |
|----------|--------|-----|
| Tool | **Inno Setup** (ISPP preprocessor) | What was used before; free; `.iss` is a plain text script that lives in the repo |
| Install scope | **Per-machine** → `{commonappdata}\Autodesk\Revit\Addins\<year>\` | Matches the current build's `DeployDir`; all Windows users get it; requires admin (standard for Revit add-ins) |
| Revit years | **All years that built** (2024–2027) | Mirrors `BuildAllYears`; years whose `libs<year>\` has no real API DLLs simply don't build and are auto-excluded from the installer |
| Layout | Flat, per year | The `.addin` uses a relative `<Assembly>LemoineTools.dll</Assembly>`, so the DLL + `.addin` + deps + `Strings\` + `Web\` must sit together in each year folder |

## Files added (all under `installer/`)

1. **`installer/LemoineTools.iss`** — the Inno Setup script.
   - `[Setup]`: app name/version/publisher, `PrivilegesRequired=admin`,
     `ArchitecturesInstallIn64BitMode=x64`, output to `installer/output/`,
     compression, uninstall registry entry.
   - `[Components]`: one checkbox per Revit year, each guarded by ISPP
     `#if DirExists("stage\<year>")` so only years that actually built appear.
   - `[Files]`: per year, packages **only Lemoine's own artifacts** from the
     staging folder → `{commonappdata}\Autodesk\Revit\Addins\<year>\`:
     `LemoineTools.dll`, `LemoineTools.addin`, `WebView2Loader.dll`,
     `Microsoft.Web.WebView2.*.dll`, `Strings\**`, `Web\**`.
     RevitAPI/RevitAPIUI are never shipped (Revit provides them).
   - `[Code]`: detect a running `Revit.exe` and warn the user to close it first
     (the plugin DLL is file-locked while Revit is open, which would fail the copy).

2. **`installer/build-installer.ps1`** — one command to produce the installer:
   - Builds each year into a **clean staging folder** via
     `dotnet build LemoineTools.csproj -c Release<year> /p:DeployDir=installer\stage\<year>\ /nodeReuse:false`
     (redirecting `DeployDir` keeps packaging away from the shared ProgramData
     Addins folder, so no other vendor's files can be swept in). A year that
     fails to build (missing API DLLs) is warned and skipped, not fatal.
   - Runs `ISCC installer\LemoineTools.iss` to emit
     `installer/output/LemoineToolsSetup-<version>.exe`.
   - Fails clearly if `ISCC.exe` (Inno Setup Compiler) isn't on `PATH`.

3. **`installer/README.md`** — prerequisites (install Inno Setup 6, put `ISCC` on
   PATH), how to run the one command, where the `setup.exe` lands, and what the
   installer does on the target machine.

## Files changed

- **`.gitignore`** — ignore `installer/stage/` and `installer/output/` (build
  artifacts, never committed).

## Explicitly NOT in scope (call out for later if wanted)

- Code-signing the `setup.exe` (needs a cert; SmartScreen will warn unsigned).
- Auto-update / version checking.
- Bundling the WebView2 Evergreen **Runtime** installer (assumed present on target,
  as Revit itself ships/uses WebView2).
- Uninstalling per-user leftovers (settings in `%AppData%\LemoineTools\`) — left in
  place by default so a reinstall keeps user settings/diagnostics.

## Build & verify constraint

The `.iss`, `.ps1`, and README are authored here, but **compiling the actual
`setup.exe` and test-installing it must happen on Windows** with Inno Setup + the
Revit API DLLs present. I cannot produce or run the installer in this Linux
environment — I'll hand you the one command and the exact expected output.
