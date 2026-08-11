#requires -Version 5
<#
.SYNOPSIS
    Build the Lemoine Tools Inno Setup installer (setup.exe), then publish it to the
    shared VDC plugin folder with an auto-incremented version.

.DESCRIPTION
    Builds each requested Revit year to the location LemoineTools.csproj already
    deploys to — its DeployDir / OutputPath, %ProgramData%\Autodesk\Revit\Addins\<year>\ —
    then compiles installer\LemoineTools.iss, which packages Lemoine's files straight
    from that same location into installer\output\LemoineToolsSetup-<version>.exe.

    VERSIONING is driven by whatever is already published in -PublishDir. The script
    reads the LemoineToolsSetup-<version>.exe sitting there, takes the highest version
    it finds, and bumps it (patch by default) — so a published 1.0.1 builds 1.0.2.
    Pass -Version to set the number by hand, or -Bump none to rebuild the same version.

    PUBLISHING copies the finished .exe into -PublishDir and then removes the older
    LemoineToolsSetup-*.exe files, leaving exactly one installer for people to download.
    The copy happens BEFORE the delete (and is hash-verified) so a failure can never
    leave the shared folder without an installer. Pass -NoPublish to build only, or
    -KeepOld to leave the previous versions in place.

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
    installer\build-installer.ps1
    # reads the published 1.0.1, builds + publishes 1.0.2, deletes 1.0.1

.EXAMPLE
    installer\build-installer.ps1 -Bump minor       # 1.0.1 -> 1.1.0

.EXAMPLE
    installer\build-installer.ps1 -Version 2.0.0    # exact version, no auto-detect

.EXAMPLE
    installer\build-installer.ps1 -SkipBuild        # package the current VS build

.EXAMPLE
    installer\build-installer.ps1 -NoPublish        # build locally, don't touch the shared folder

.EXAMPLE
    installer\build-installer.ps1 -CheckPublish     # just show the publish folder + next version

.NOTES
    This script NEVER launches the installer it builds. If a setup wizard opens after
    you "build", the Inno Setup Compiler IDE compiled it, not this script — its Run (F9)
    command compiles AND runs the output, and it does no versioning or publishing.
    Use Build > Compile (Ctrl+F9) in that IDE, or run this script instead.
#>
[CmdletBinding()]
param(
    # Exact version to stamp. Omit to auto-detect from -PublishDir and bump it.
    [string] $Version,

    # Which part of the detected version to increment. Ignored when -Version is given.
    [ValidateSet("patch", "minor", "major", "none")]
    [string] $Bump = "patch",

    # Shared folder the installer is published to. Built from %USERPROFILE% so it
    # resolves for anyone with the VDC Department library synced by OneDrive, e.g.
    #   C:\Users\<you>\The Lemoine Company\VDC Department - Documents\...\Plugin
    [string] $PublishDir = (Join-Path $env:USERPROFILE "The Lemoine Company\VDC Department - Documents\1 General\1.5 Software\Software Resources\Revit\Plugin"),

    # Build only — leave the shared folder alone.
    [switch] $NoPublish,

    # Publish the new .exe but keep the older ones instead of deleting them.
    [switch] $KeepOld,

    # Report what the publish folder resolves to and which version would be built,
    # then stop. Builds nothing, copies nothing, deletes nothing.
    [switch] $CheckPublish,

    [string] $Iscc  = "ISCC.exe",
    [int[]]  $Years = @(2024, 2025, 2026, 2027),
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

# Only ever matches our own installer naming — the publish folder may hold other
# files (docs, older tooling), and nothing outside this exact shape is touched.
$SetupNamePattern = '^LemoineToolsSetup-(\d+(?:\.\d+){1,3})\.exe$'

# Every LemoineToolsSetup-<version>.exe in $Dir, as { File, Version } records.
# An unreachable folder (OneDrive not synced, wrong machine) yields nothing rather
# than throwing — the caller decides whether that is fatal.
function Get-PublishedSetup {
    param([string] $Dir)

    if ([string]::IsNullOrWhiteSpace($Dir) -or -not (Test-Path -LiteralPath $Dir)) { return @() }

    # A folder that cannot be listed (permissions, a stalled OneDrive placeholder) must
    # not read as "no installers published" — that would silently reset the version to
    # 1.0.0 and then delete the real release. Say so instead of returning empty quietly.
    try {
        $files = @(Get-ChildItem -LiteralPath $Dir -Filter "LemoineToolsSetup-*.exe" -File -ErrorAction Stop)
    }
    catch {
        throw "Could not read the publish folder $Dir — $($_.Exception.Message). " +
              "Re-run once it is reachable, or pass -NoPublish to build without publishing."
    }

    $found = @()
    foreach ($f in $files) {
        if ($f.Name -match $SetupNamePattern) {
            $parsed = $null
            if ([version]::TryParse($Matches[1], [ref] $parsed)) {
                $found += [pscustomobject]@{ File = $f; Version = $parsed }
            }
            else {
                Write-Warning "Ignoring unparseable installer name: $($f.Name)"
            }
        }
    }
    return $found
}

# Increment one part and zero the ones below it. Normalises to Major.Minor.Patch,
# so a 2- or 4-part published version still yields a clean 3-part next version.
function Step-Version {
    param([version] $V, [string] $Part)

    $maj = [Math]::Max($V.Major, 0)
    $min = [Math]::Max($V.Minor, 0)
    $pat = [Math]::Max($V.Build, 0)

    switch ($Part) {
        "major" { $maj++; $min = 0; $pat = 0 }
        "minor" { $min++; $pat = 0 }
        "patch" { $pat++ }
    }
    return "$maj.$min.$pat"
}

Write-Host "== Lemoine Tools installer build ==" -ForegroundColor Cyan
Write-Host "Source (csproj deploy): $addinsRoot\<year>\" -ForegroundColor DarkGray
Write-Host "Publish folder:         $PublishDir" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
# 1. Work out the version: explicit -Version wins, otherwise read what is
#    currently published and bump it.
# ---------------------------------------------------------------------------
$publishDirExists = -not [string]::IsNullOrWhiteSpace($PublishDir) -and (Test-Path -LiteralPath $PublishDir)
$published        = @(Get-PublishedSetup -Dir $PublishDir | Sort-Object Version)
$currentPublished = if ($published.Count -gt 0) { $published[-1] } else { $null }

if ($Version) {
    if ($Version -notmatch '^\d+(\.\d+){1,3}$') {
        throw "-Version '$Version' is not a numeric version (expected e.g. 1.0.2)."
    }
    Write-Host "Version:                $Version (given)" -ForegroundColor Cyan
}
elseif ($currentPublished) {
    $currentVersion = $currentPublished.Version.ToString()
    if ($Bump -eq "none") {
        $Version = $currentVersion
        Write-Host "Version:                $Version (rebuilding published version)" -ForegroundColor Cyan
    }
    else {
        $Version = Step-Version -V $currentPublished.Version -Part $Bump
        Write-Host "Currently published:    $($currentPublished.File.Name)" -ForegroundColor DarkGray
        Write-Host "Version:                $currentVersion -> $Version ($Bump)" -ForegroundColor Cyan
    }
}
else {
    $Version = "1.0.0"
    if ($publishDirExists) {
        Write-Warning "No LemoineToolsSetup-<version>.exe found in the publish folder — starting at $Version. Pass -Version to set it explicitly."
    }
    else {
        Write-Warning "Publish folder not found: $PublishDir"
        Write-Warning "Cannot auto-detect the current version — starting at $Version. Pass -Version, or -PublishDir if the folder lives elsewhere."
    }
}

if ($published.Count -gt 1) {
    Write-Warning "Publish folder holds $($published.Count) installers; the highest ($($currentPublished.Version)) was used as the current version."
}

# -CheckPublish: report and stop. Nothing is built, copied or deleted — this exists
# to confirm the OneDrive path resolves before committing to a full 4-year build.
if ($CheckPublish) {
    Write-Host "`n-- Publish check (nothing built, nothing changed) --" -ForegroundColor Yellow
    if ($publishDirExists) {
        Write-Host "Folder exists:          yes" -ForegroundColor Green
        if ($published.Count -gt 0) {
            Write-Host "Installers found:" -ForegroundColor DarkGray
            foreach ($p in $published) { Write-Host "    $($p.File.Name)" -ForegroundColor DarkGray }
        }
        else {
            Write-Host "Installers found:       none matching LemoineToolsSetup-<version>.exe" -ForegroundColor Yellow
            $anyFile = @(Get-ChildItem -LiteralPath $PublishDir -File -ErrorAction SilentlyContinue | Select-Object -First 10)
            if ($anyFile.Count -gt 0) {
                Write-Host "Folder does contain:" -ForegroundColor DarkGray
                foreach ($f in $anyFile) { Write-Host "    $($f.Name)" -ForegroundColor DarkGray }
            }
        }
        Write-Host "Would build + publish:  LemoineToolsSetup-$Version.exe" -ForegroundColor Cyan
    }
    else {
        Write-Host "Folder exists:          NO" -ForegroundColor Red
        Write-Host "Nothing would be published. Check that OneDrive has the VDC Department" -ForegroundColor Red
        Write-Host "library synced to this machine, then re-run. If it syncs to a different" -ForegroundColor Red
        Write-Host "path, pass -PublishDir '<path>'." -ForegroundColor Red
    }
    return
}

# ---------------------------------------------------------------------------
# 2. Build each year to its default (csproj-configured) location, unless -SkipBuild.
# ---------------------------------------------------------------------------
if (-not $SkipBuild) {
    foreach ($y in $Years) {
        Write-Host "`n-- Building Release$y --" -ForegroundColor Yellow
        & dotnet build $proj -c "Release$y" /nodeReuse:false
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Release$y build failed (missing libs$y\ Revit API DLLs?) — it will be packaged only if a prior build is present."
        }
    }
}

# ---------------------------------------------------------------------------
# 3. Find which years are actually present at the deploy location.
# ---------------------------------------------------------------------------
$built = @()
foreach ($y in $Years) {
    if (Test-Path (Join-Path $addinsRoot "$y\LemoineTools.dll")) { $built += $y }
}
if ($built.Count -eq 0) {
    throw "No built plugin found under $addinsRoot\<year>\. Build the plugin first (or run without -SkipBuild)."
}
Write-Host "`nPackaging Revit years: $($built -join ', ')" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# 4. Compile the installer. LemoineTools.iss reads the same deploy location from
#    %ProgramData% and packages only Lemoine's own files from each year folder.
#    It compiles to the LOCAL installer\output\ first — publishing is a separate
#    verified copy in step 5, so a failed compile never lands in the shared folder.
# ---------------------------------------------------------------------------
#    /DAutoPublish=0 turns OFF the .iss's own compile-time auto-publish. That path
#    exists for compiling the .iss straight from the Inno Setup IDE; here it would
#    fight this script — it would pick its own version, write to the shared folder,
#    and leave installer\output\ empty. This script publishes itself, in step 5,
#    with a verified copy-then-delete instead.
if (-not (Test-Path $outdir)) { New-Item -ItemType Directory -Path $outdir | Out-Null }
Write-Host "`n-- Compiling installer --" -ForegroundColor Yellow
& $Iscc "/DMyAppVersion=$Version" "/DAutoPublish=0" $iss
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed (exit $LASTEXITCODE). Is Inno Setup 6 installed and ISCC.exe on PATH? " +
          "Otherwise pass -Iscc 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'."
}

$setup = Join-Path $outdir "LemoineToolsSetup-$Version.exe"
if (-not (Test-Path -LiteralPath $setup)) {
    throw "ISCC reported success but $setup is missing — nothing was published."
}
Write-Host "Built -> $setup" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 5. Publish: copy the new .exe to the shared folder, verify it, then remove the
#    older installers. Copy-then-verify-then-delete (rather than delete-then-copy)
#    means a half-finished publish leaves the previous installer usable.
# ---------------------------------------------------------------------------
if ($NoPublish) {
    Write-Host "`n-NoPublish — shared folder untouched." -ForegroundColor DarkGray
    return
}

if (-not $publishDirExists) {
    Write-Warning "Publish folder not found: $PublishDir"
    Write-Warning "The installer was built at $setup but NOT published. Check that OneDrive has the VDC Department library synced, or pass -PublishDir."
    return
}

Write-Host "`n-- Publishing --" -ForegroundColor Yellow
$target = Join-Path $PublishDir "LemoineToolsSetup-$Version.exe"

try {
    Copy-Item -LiteralPath $setup -Destination $target -Force -ErrorAction Stop
}
catch {
    throw "Could not copy the installer to $target — $($_.Exception.Message). " +
          "The build is still available at $setup; the shared folder was left as it was."
}

# Verify the published copy really is the file we just built before deleting anything.
$srcHash = (Get-FileHash -LiteralPath $setup   -Algorithm SHA256).Hash
$dstHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
if ($srcHash -ne $dstHash) {
    throw "The copy at $target does not match the file that was built (SHA256 mismatch). " +
          "Nothing was deleted — re-run, or copy $setup across by hand."
}
Write-Host "Published -> $target" -ForegroundColor Green

if ($KeepOld) {
    Write-Host "-KeepOld — previous installers left in place." -ForegroundColor DarkGray
}
else {
    $targetFull  = (Get-Item -LiteralPath $target).FullName
    $newVersion  = [version] $Version
    $removed     = 0
    $failed      = 0
    $keptNewer   = 0

    foreach ($old in (Get-PublishedSetup -Dir $PublishDir)) {
        if ($old.File.FullName -eq $targetFull) { continue }

        # Never delete an installer NEWER than the one just published. A mistyped
        # -Version, or an auto-detect that fell back to 1.0.0 because the folder was
        # briefly unreachable, would otherwise wipe out a good newer release.
        if ($old.Version -gt $newVersion) {
            Write-Warning "Kept $($old.File.Name) — it is newer than the $Version just published. Two installers are now in the folder; remove one by hand."
            $keptNewer++
            continue
        }

        try {
            Remove-Item -LiteralPath $old.File.FullName -Force -ErrorAction Stop
            Write-Host "Removed old -> $($old.File.Name)" -ForegroundColor DarkGray
            $removed++
        }
        catch {
            Write-Warning "Could not remove $($old.File.FullName) — $($_.Exception.Message). Delete it by hand, or people may download the old version."
            $failed++
        }
    }
    if ($removed -eq 0 -and $failed -eq 0 -and $keptNewer -eq 0) {
        Write-Host "No older installers to remove." -ForegroundColor DarkGray
    }
}

Write-Host "`nDone -> v$Version published to $PublishDir" -ForegroundColor Green
