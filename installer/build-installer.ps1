#requires -Version 5
<#
.SYNOPSIS
    Build the Lemoine Tools Inno Setup installer (setup.exe).

.DESCRIPTION
    Builds each requested Revit year to the location LemoineTools.csproj already
    deploys to — its DeployDir / OutputPath, %ProgramData%\Autodesk\Revit\Addins\<year>\ —
    then compiles installer\LemoineTools.iss, which packages Lemoine's files straight
    from that same location into installer\output\LemoineToolsSetup-<version>.exe.

    Pass -SkipBuild to package whatever is already deployed there (e.g. after a
    Visual Studio build) without rebuilding.

    A year that isn't present at the deploy location is simply left out of the
    installer; only the years actually found are packaged.

.PREREQUISITES
    - .NET SDK, plus the Revit API DLLs in libs\ / libs2025\ / libs2026\ / libs2027\
      for each year you want in the installer.
    - Inno Setup 6 installed, with ISCC.exe on PATH (or pass -Iscc "<full path>").
    - Windows only (the plugin and Inno Setup are both Windows-only).

.EXAMPLE
    installer\build-installer.ps1 -Version 1.2.0

.EXAMPLE
    installer\build-installer.ps1 -SkipBuild        # package the current VS build
#>
[CmdletBinding()]
param(
    [string] $Version = "1.0.0",
    [string] $Iscc    = "ISCC.exe",
    [int[]]  $Years   = @(2024, 2025, 2026, 2027),
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"

$here   = Split-Path -Parent $MyInvocation.MyCommand.Path   # installer\
$root   = Split-Path -Parent $here                          # repo root
$proj   = Join-Path $root "LemoineTools.csproj"
$iss    = Join-Path $here "LemoineTools.iss"
$outdir = Join-Path $here "output"

# Where LemoineTools.csproj deploys each year's build (DeployDir / OutputPath).
$addinsRoot = Join-Path $env:ProgramData "Autodesk\Revit\Addins"

Write-Host "== Lemoine Tools installer build (v$Version) ==" -ForegroundColor Cyan
Write-Host "Source (csproj deploy): $addinsRoot\<year>\" -ForegroundColor DarkGray

# 1. Build each year to its default (csproj-configured) location, unless -SkipBuild.
if (-not $SkipBuild) {
    foreach ($y in $Years) {
        Write-Host "`n-- Building Release$y --" -ForegroundColor Yellow
        & dotnet build $proj -c "Release$y" /nodeReuse:false
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Release$y build failed (missing libs$y\ Revit API DLLs?) — it will be packaged only if a prior build is present."
        }
    }
}

# 2. Find which years are actually present at the deploy location.
$built = @()
foreach ($y in $Years) {
    if (Test-Path (Join-Path $addinsRoot "$y\LemoineTools.dll")) { $built += $y }
}
if ($built.Count -eq 0) {
    throw "No built plugin found under $addinsRoot\<year>\. Build the plugin first (or run without -SkipBuild)."
}
Write-Host "`nPackaging Revit years: $($built -join ', ')" -ForegroundColor Cyan

# 3. Compile the installer. LemoineTools.iss reads the same deploy location from
#    %ProgramData% and packages only Lemoine's own files from each year folder.
if (-not (Test-Path $outdir)) { New-Item -ItemType Directory -Path $outdir | Out-Null }
Write-Host "`n-- Compiling installer --" -ForegroundColor Yellow
& $Iscc "/DMyAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed (exit $LASTEXITCODE). Is Inno Setup 6 installed and ISCC.exe on PATH? " +
          "Otherwise pass -Iscc 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'."
}

$setup = Join-Path $outdir "LemoineToolsSetup-$Version.exe"
Write-Host "`nDone -> $setup" -ForegroundColor Green
