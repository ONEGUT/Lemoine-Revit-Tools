# Plan: Support Revit 2026 alongside Revit 2024

## Why this is more than a version bump

Autodesk moved Revit's runtime to .NET 8 starting with **Revit 2025**. Revit 2024
runs .NET Framework 4.8 (current project target); Revit 2026 requires .NET 8
(`net8.0-windows`). So supporting both means the assembly has to be built twice,
once per runtime, from one source tree — not just swapping which `RevitAPI.dll`
is referenced.

## Current state (confirmed by reading the repo)

- `LemoineTools.csproj`: single `<TargetFramework>net48</TargetFramework>`.
- `RevitDir` / `DeployDir` are single-valued, hardcoded to Revit 2024's install
  path and `C:\ProgramData\Autodesk\Revit\Addins\2024\`.
- `libs/RevitAPI.dll` and `libs/RevitAPIUI.dll` checked in are the **2024**
  API assemblies (used as the no-local-install fallback).
- `LemoineTools.addin` is a single manifest with no version scoping.
- No source file has version-conditional logic today — the only `"2024"` hits
  are a code comment and an unrelated sample project-number string. So the
  C# source itself is not expected to need `#if` branches yet, though we won't
  know for certain (API surface diffs) until it's built against the 2026 SDK.

## Proposed changes

1. **`LemoineTools.csproj` — multi-target**
   - `<TargetFrameworks>net48;net8.0-windows</TargetFrameworks>` (note plural
     property + `UseWPF`/`UseWindowsForms` both still apply per-target).
   - Make `RevitDir`, `DeployDir`, and the `RevitAPI`/`RevitAPIUI` reference
     block conditional on `$(TargetFramework)`:
     - `net48` → Revit 2024 install path fallback → `libs/` (existing 2024
       DLLs, untouched).
     - `net8.0-windows` → Revit 2026 install path fallback → new
       `libs2026/` folder (new 2026 `RevitAPI.dll`/`RevitAPIUI.dll`, not yet
       in the repo — need to source these, e.g. copy from a Revit 2026 install
       or the Revit 2026 API SDK).
   - `DeployDir` becomes `...\Addins\2024\` for net48 and `...\Addins\2026\`
     for net8.0-windows, so `dotnet build` produces both outputs in one pass
     without clobbering each other.
   - Extend the sibling-subproject exclusion (`LemoinePreview`, `LemoineNavisworks`)
     unchanged — it's version-agnostic.

2. **`LemoineTools.addin` — one manifest per deploy folder**
   - Keep it as-is content-wise; the build copies it into both
     `Addins\2024\` and `Addins\2026\` (already handled by `<Content Include>`
     + the per-TFM `OutputPath`/`DeployDir`).

3. **`libs2026/` — new folder for the 2026 Revit API DLLs**
   - Mirrors the existing `libs/` fallback pattern. Needs the actual 2026
     `RevitAPI.dll`/`RevitAPIUI.dll` added by whoever has a Revit 2026
     install (can't be sourced from this environment).

4. **Build-time API diffs**
   - Nothing to change speculatively — Revit's public API is largely stable
     year over year, and CLAUDE.md's existing "Revit API gotchas" table is
     all 2024-specific findings, not deprecations. Any 2026-only breakage
     (removed enum members, changed overloads, etc.) will only surface once
     it's actually built against the 2026 SDK on Windows — at that point add
     a `#if NET8_0_WINDOWS` / `#if NET48` branch at the specific call site
     rather than restructuring speculatively.

5. **Docs**
   - Update the "Build Environment" section of `CLAUDE.md` to describe the
     multi-target setup and the two `libs*/` fallback folders once this
     lands.

## Out of scope / explicit non-goals

- No source-level `#if` guards added preemptively — only if/when a real
  build breaks.
- Not touching Revit 2025 — the ask is 2024 + 2026 only.
- Not attempting a build in this sandbox — per CLAUDE.md, this project only
  builds on Windows; verification has to happen there.

## Files touched

- `LemoineTools.csproj` (multi-target, conditional RevitDir/DeployDir/refs)
- `libs2026/RevitAPI.dll`, `libs2026/RevitAPIUI.dll` (new, added once sourced)
- `CLAUDE.md` (Build Environment section update)
