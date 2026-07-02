# Plan: Support Revit 2024, 2025, 2026, and 2027

## Why this is more than swapping a DLL path

Autodesk moved Revit's runtime to .NET 8 starting with **Revit 2025**. Revit
2024 runs .NET Framework 4.8 (current project target); 2025, 2026, and 2027
all require .NET 8 (`net8.0-windows`). So three of the four target years
share one runtime, but the plugin still needs **four separate build outputs**
(one `LemoineTools.dll` per year, each referencing that year's own
`RevitAPI.dll`/`RevitAPIUI.dll` and deployed to that year's Addins folder) —
API assemblies aren't guaranteed binary-compatible release to release even
within the same TFM, and each year's install expects its own copy.

Plain `<TargetFrameworks>` multi-targeting only gives one output per distinct
framework moniker, so it can produce at most 2 outputs here (net48 +
net8.0-windows) — not 4. To get 4 distinguishable outputs, this needs
**per-year build Configurations** layered on top of the two TFMs (a standard
pattern for multi-version Revit plugins): `Release2024`, `Release2025`,
`Release2026`, `Release2027` (and `Debug*` equivalents), each conditionally
picking its `TargetFramework`, `RevitDir`, `DeployDir`, API reference path,
and a `DefineConstants` symbol (`REVIT2024` / `REVIT2025` / `REVIT2026` /
`REVIT2027`) for the rare case a call site needs to branch by year.

## Current state (confirmed by reading the repo)

- `LemoineTools.csproj`: single `<TargetFramework>net48</TargetFramework>`,
  single unconditional `Configuration` (Debug/Release only).
- `RevitDir` / `DeployDir` are single-valued, hardcoded to Revit 2024's
  install path and `C:\ProgramData\Autodesk\Revit\Addins\2024\`.
- `libs/RevitAPI.dll` and `libs/RevitAPIUI.dll` checked in are the **2024**
  API assemblies (used as the no-local-install fallback).
- `LemoineTools.addin` is a single manifest with no version scoping.
- No source file has version-conditional logic today — the only `"2024"`
  hits are a code comment and an unrelated sample project-number string. So
  the C# source itself is not expected to need `#if` branches yet.

## Proposed changes

1. **`LemoineTools.csproj` — per-year Configurations over two TFMs**
   - Define `Configurations` as `Debug2024;Release2024;Debug2025;Release2025;Debug2026;Release2026;Debug2027;Release2027`.
   - `TargetFramework` set by `Condition` on `$(Configuration)`:
     `net48` for `*2024`, `net8.0-windows` for `*2025`/`*2026`/`*2027`.
   - `RevitDir` per year: `C:\Program Files\Autodesk\Revit 20XX` if it
     exists, else fall back to `libs20XX\` (2024 keeps the existing bare
     `libs\` folder to avoid an unnecessary rename/break).
   - `DeployDir` per year: `C:\ProgramData\Autodesk\Revit\Addins\20XX\`.
   - `DefineConstants` per year (`REVIT2024`, etc.) appended, not replacing
     the existing constants.
   - `RevitAPI`/`RevitAPIUI` `<Reference>` `HintPath` continues to point at
     `$(RevitDir)`, unchanged in shape — just now resolves differently per
     Configuration.

2. **`LemoineTools.addin` — unchanged content, deployed 4x**
   - No manifest changes needed; each Configuration's `DeployDir` already
     places a copy in that year's Addins folder via the existing `<Content
     Include>` block.

3. **New `libs2025/`, `libs2026/`, `libs2027/` folders**
   - Mirror the existing `libs/` fallback pattern (which stays as Revit
     2024's folder). Each needs that year's real `RevitAPI.dll`/
     `RevitAPIUI.dll` added by whoever has the corresponding Revit install —
     can't be sourced from this sandbox.
   - **2027 caveat:** as of today (2026-07-02) Revit 2027 has not shipped, so
     there is no API SDK to reference yet. `libs2027/` will be scaffolded
     empty with a placeholder/readme; the Configuration will exist but won't
     build until Autodesk actually releases 2027's API assemblies.

4. **Build-time API diffs**
   - No source changes made speculatively. CLAUDE.md's existing "Revit API
     gotchas" table is all 2024-specific findings, not deprecations. Any
     year-specific breakage (removed enum members, changed overloads,
     namespace moves) only surfaces once actually compiled against that
     year's SDK on Windows — fix at the specific call site with
     `#if REVIT2025` / etc. at that point, not ahead of time.

5. **Docs**
   - Update the "Build Environment" section of `CLAUDE.md` to describe the
     4-Configuration / 2-TFM setup, the four `libs20XX/` fallback folders,
     and the 2027-not-yet-shipped caveat.

## Out of scope / explicit non-goals

- No source-level `#if REVITxxxx` guards added preemptively — only if/when a
  real build breaks on a given year's SDK.
- Not attempting a build in this sandbox — per CLAUDE.md, this project only
  builds on Windows; verification has to happen there, and 2027 specifically
  can't be verified at all until Autodesk ships it.
- Not changing the default `dotnet build` behavior for anyone who doesn't
  pass a `-c Release20XX` — need to confirm with the user what the default
  Configuration should resolve to (proposal: `Release2024`, since that's
  the currently-working, currently-installed target).

## Files touched

- `LemoineTools.csproj` (Configurations, per-year TargetFramework/RevitDir/DeployDir/refs/DefineConstants)
- `libs2025/`, `libs2026/`, `libs2027/` (new folders; DLLs added once sourced per year)
- `CLAUDE.md` (Build Environment section update)
