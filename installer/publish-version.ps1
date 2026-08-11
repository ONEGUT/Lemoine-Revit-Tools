#requires -Version 5
<#
.SYNOPSIS
    Compile-time helper for LemoineTools.iss. Works out the next installer version
    from what is currently published in the shared VDC folder, clears the superseded
    installers out of the way, and hands the answer back to the Inno Setup
    preprocessor through an INI file.

.DESCRIPTION
    LemoineTools.iss runs this via ISPP's Exec() while it preprocesses, so compiling
    the .iss straight from the Inno Setup IDE (Build > Compile) auto-versions and
    publishes with no PowerShell typing at all. It is also what build-installer.ps1's
    workflow does by hand, so the two agree on version numbering.

    It reads the highest LemoineToolsSetup-<version>.exe in -PublishDir, bumps it
    (patch by default), and writes to -OutFile:

        [publish]
        next=1.0.2            <- version the .iss should stamp
        current=1.0.1         <- what was published before this build
        outputdir=<path>      <- where the .iss should write setup.exe
        status=ok|nofolder|nopublish
        message=<human-readable detail>

    A missing/unreadable [publish] next value is the .iss's signal to abort the
    compile, so a failure here can never silently produce a mis-versioned installer.

.NOTES
    ORDERING CAVEAT — this runs BEFORE the compiler produces setup.exe, because ISPP
    has no post-compile hook. So the old installer is removed before the new one
    exists. Every removed file is first copied to
    %TEMP%\LemoineToolsSetup-backup\, and the path is reported in [publish] message,
    so a failed compile is always recoverable. Pass -KeepOld to skip removal entirely.
#>
[CmdletBinding()]
param(
    # INI file to write the result to. LemoineTools.iss reads this back with ReadIni().
    [Parameter(Mandatory = $true)]
    [string] $OutFile,

    # Success marker. Written ONLY on a fully successful run — LemoineTools.iss
    # aborts the compile if it is absent, so a helper that crashed, was blocked by
    # execution policy, or never launched can never yield a mis-versioned installer.
    [Parameter(Mandatory = $true)]
    [string] $OkFile,

    # Shared folder to read the current version from and publish into. Built from
    # %USERPROFILE% so it resolves for anyone with the VDC library synced by OneDrive.
    [string] $PublishDir = (Join-Path $env:USERPROFILE "The Lemoine Company\VDC Department - Documents\1 General\1.5 Software\Software Resources\Revit\Plugin"),

    # Which part of the detected version to increment.
    [ValidateSet("patch", "minor", "major", "none")]
    [string] $Bump = "patch",

    # Use this version instead of detecting one (the .iss passes /DMyAppVersion through).
    [string] $Version,

    # Publish the new installer but leave the previous ones in place.
    [switch] $KeepOld,

    # Work out the version but never touch the shared folder; build locally instead.
    [switch] $NoPublish
)

$ErrorActionPreference = "Stop"

# Only ever matches our own installer naming — the publish folder holds other things,
# and nothing outside this exact shape is ever backed up or removed.
$SetupNamePattern = '^LemoineToolsSetup-(\d+(?:\.\d+){1,3})\.exe$'

function Get-PublishedSetup {
    param([string] $Dir)

    if ([string]::IsNullOrWhiteSpace($Dir) -or -not (Test-Path -LiteralPath $Dir)) { return @() }

    # A folder that cannot be listed must not read as "nothing published" — that would
    # reset the version to 1.0.0 and then delete the real release. Fail loudly instead.
    $files = @(Get-ChildItem -LiteralPath $Dir -Filter "LemoineToolsSetup-*.exe" -File -ErrorAction Stop)

    $found = @()
    foreach ($f in $files) {
        if ($f.Name -match $SetupNamePattern) {
            $parsed = $null
            if ([version]::TryParse($Matches[1], [ref] $parsed)) {
                $found += [pscustomobject]@{ File = $f; Version = $parsed }
            }
        }
    }
    return $found
}

# Increment one part and zero the ones below it. Normalises to Major.Minor.Patch.
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

# INI values are read back by ISPP's ReadIni(), which takes the rest of the line
# verbatim — so keep every value on one line and strip anything that would break it.
function Write-ResultIni {
    param(
        [string] $Path,
        [string] $Next,
        [string] $Current,
        [string] $OutputDir,
        [string] $Status,
        [string] $Message
    )

    $clean = { param($s) ($s -replace '[\r\n]+', ' ').Trim() }

    $lines = @(
        "[publish]",
        "next="      + (& $clean $Next),
        "current="   + (& $clean $Current),
        "outputdir=" + (& $clean $OutputDir),
        "status="    + (& $clean $Status),
        "message="   + (& $clean $Message)
    )

    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    Set-Content -LiteralPath $Path -Value $lines -Encoding ASCII
}

# Written last, and only on success — see the -OkFile parameter notes.
function Set-OkMarker {
    param([string] $Path)
    Set-Content -LiteralPath $Path -Value "ok" -Encoding ASCII
}

try {
    $publishDirExists = -not [string]::IsNullOrWhiteSpace($PublishDir) -and (Test-Path -LiteralPath $PublishDir)

    # ---------------------------------------------------------------------
    # Build locally: no detection, no folder changes.
    # ---------------------------------------------------------------------
    if ($NoPublish) {
        $v = if ($Version) { $Version } else { "1.0.0" }
        Write-ResultIni -Path $OutFile -Next $v -Current "" -OutputDir "output" `
                        -Status "nopublish" -Message "-NoPublish: built to installer\output\, shared folder untouched."
        Set-OkMarker -Path $OkFile
        exit 0
    }

    # ---------------------------------------------------------------------
    # Folder unreachable: fall back to a local build rather than guessing a
    # version. Nothing is deleted, because nothing could be read.
    # ---------------------------------------------------------------------
    if (-not $publishDirExists) {
        $v = if ($Version) { $Version } else { "1.0.0" }
        Write-ResultIni -Path $OutFile -Next $v -Current "" -OutputDir "output" -Status "nofolder" `
                        -Message "Publish folder not found ($PublishDir) - built to installer\output\ instead. Is the VDC library synced by OneDrive?"
        Set-OkMarker -Path $OkFile
        exit 0
    }

    $published        = @(Get-PublishedSetup -Dir $PublishDir | Sort-Object Version)
    $currentPublished = if ($published.Count -gt 0) { $published[-1] } else { $null }
    $currentVersion   = if ($currentPublished) { $currentPublished.Version.ToString() } else { "" }

    # ---------------------------------------------------------------------
    # Decide the version.
    # ---------------------------------------------------------------------
    if ($Version) {
        if ($Version -notmatch '^\d+(\.\d+){1,3}$') {
            throw "Version '$Version' is not a numeric version (expected e.g. 1.0.2)."
        }
        $next   = $Version
        $detail = "version $next was given explicitly"
    }
    elseif ($currentPublished) {
        if ($Bump -eq "none") {
            $next   = $currentVersion
            $detail = "rebuilding published version $next"
        }
        else {
            $next   = Step-Version -V $currentPublished.Version -Part $Bump
            $detail = "$currentVersion -> $next ($Bump)"
        }
    }
    else {
        $next   = "1.0.0"
        $detail = "no LemoineToolsSetup-<version>.exe found in the publish folder - starting at $next"
    }

    # ---------------------------------------------------------------------
    # Clear the superseded installers. The compiler writes the new .exe straight
    # into this folder immediately after, so this is the "delete the old, place
    # the new" step — every removal is backed up first (see .NOTES).
    # ---------------------------------------------------------------------
    $notes = @()
    if ($KeepOld) {
        $notes += "previous installers kept (-KeepOld)"
    }
    else {
        $newVersion = [version] $next
        $backupDir  = Join-Path $env:TEMP "LemoineToolsSetup-backup"
        $removed    = 0

        foreach ($old in $published) {
            # Never remove an installer NEWER than the one about to be built. A mistyped
            # version would otherwise wipe out a good newer release.
            if ($old.Version -gt $newVersion) {
                $notes += "kept $($old.File.Name) (newer than $next)"
                continue
            }

            try {
                if (-not (Test-Path -LiteralPath $backupDir)) {
                    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
                }
                Copy-Item -LiteralPath $old.File.FullName -Destination $backupDir -Force -ErrorAction Stop
                Remove-Item -LiteralPath $old.File.FullName -Force -ErrorAction Stop
                $removed++
            }
            catch {
                # Reported back to the compiler output rather than swallowed: a leftover
                # old installer means people can still download the wrong version.
                $notes += "COULD NOT REMOVE $($old.File.Name) ($($_.Exception.Message)) - delete it by hand"
            }
        }

        if ($removed -gt 0) { $notes += "removed $removed old installer(s), backed up to $backupDir" }
    }

    $message = $detail
    if ($notes.Count -gt 0) { $message += "; " + ($notes -join "; ") }

    Write-ResultIni -Path $OutFile -Next $next -Current $currentVersion -OutputDir $PublishDir `
                    -Status "ok" -Message $message
    Set-OkMarker -Path $OkFile
    exit 0
}
catch {
    # Leave [publish] next EMPTY so LemoineTools.iss aborts the compile instead of
    # stamping a wrong version. The message is what the .iss tells the user to read.
    try {
        Write-ResultIni -Path $OutFile -Next "" -Current "" -OutputDir "" -Status "error" `
                        -Message $_.Exception.Message
    }
    catch {
        # Nothing more can be done — with no readable INI the .iss still aborts,
        # which is the safe outcome.
    }
    exit 1
}
