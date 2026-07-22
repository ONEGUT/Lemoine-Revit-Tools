#requires -Version 5
<#
.SYNOPSIS
    Build the Lemoine Tools Inno Setup installer (setup.exe).

.DESCRIPTION
    For each requested Revit year, builds the matching Release<year> configuration
    straight into a clean installer\stage\<year>\ folder (overriding DeployDir so
    packaging never touches — or sweeps files out of — the shared ProgramData
    Addins folder), then compiles installer\LemoineTools.iss with ISCC into
    installer\output\LemoineToolsSetup-<version>.exe.

    A year that fails to build (e.g. libs<year>\ has only placeholder READMEs, not
    the real Revit API DLLs) is warned and skipped; the installer bundles only the
    years that produced output.

.PREREQUISITES
    - .NET SDK, plus the Revit API DLLs in libs\ / libs2025\ / libs2026\ / libs2027\
      for each year you want in the installer.
    - Inno Setup 6 installed, with ISCC.exe on PATH (or pass -Iscc "<full path>").
    - Windows only (the plugin and Inno Setup are both Windows-only).

.EXAMPLE
    installer\build-installer.ps1 -Version 1.2.0

.EXAMPLE
    installer\build-installer.ps1 -Years 2024 -Iscc "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
#>
[CmdletBinding()]
param(
    [string]  $Version = "1.0.0",
    [string]  $Iscc    = "ISCC.exe",
    [int[]]   $Years   = @(2024, 2025, 2026, 2027)
)

$ErrorActionPreference = "Stop"

$here   = Split-Path -Parent $MyInvocation.MyCommand.Path   # installer\
$root   = Split-Path -Parent $here                          # repo root
$proj   = Join-Path $root "LemoineTools.csproj"
$stage  = Join-Path $here "stage"
$outdir = Join-Path $here "output"
$iss    = Join-Path $here "LemoineTools.iss"

Write-Host "== Lemoine Tools installer build (v$Version) ==" -ForegroundColor Cyan

# 1. Clean staging so a removed/renamed file from a prior build can't linger.
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

# 2. Build each year into its own clean stage\<year>\ folder.
$built = @()
foreach ($y in $Years) {
    $dest = Join-Path $stage "$y"
    Write-Host "`n-- Building Release$y -> $dest --" -ForegroundColor Yellow
    & dotnet build $proj -c "Release$y" "/p:DeployDir=$dest\" /nodeReuse:false
    if (($LASTEXITCODE -eq 0) -and (Test-Path (Join-Path $dest "LemoineTools.dll"))) {
        Write-Host "   Release$y OK" -ForegroundColor Green
        $built += $y
    }
    else {
        Write-Warning "Release$y produced no output (missing libs$y\ Revit API DLLs?) — skipping."
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    }
}

if ($built.Count -eq 0) {
    throw "No Revit year built. Populate libs\ (and libs2025\ .. libs2027\) with the Revit API DLLs, then retry."
}
Write-Host "`nBundling Revit years: $($built -join ', ')" -ForegroundColor Cyan

# 3. Compile the installer.
if (-not (Test-Path $outdir)) { New-Item -ItemType Directory -Path $outdir | Out-Null }
Write-Host "`n-- Compiling installer --" -ForegroundColor Yellow
& $Iscc "/DMyAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed (exit $LASTEXITCODE). Is Inno Setup 6 installed and ISCC.exe on PATH? " +
          "Otherwise pass -Iscc 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'."
}

$setup = Join-Path $outdir "LemoineToolsSetup-$Version.exe"
Write-Host "`nDone -> $setup" -ForegroundColor Green
