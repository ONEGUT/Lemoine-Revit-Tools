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
    (patch by default), and writes -OutFile as an ISPP include the .iss #includes:

        #define PublishNextVersion "1.0.2"    <- version the .iss stamps
        #define PublishPrevVersion "1.0.1"    <- what was published before this build
        #define PublishOutputDir "<path>"     <- where the .iss writes setup.exe
        #define PublishStatus "ok"            <- ok | nofolder | nopublish
        #define PublishMessage "<detail>"     <- shown in the compiler output

    On failure it writes a file defining PublishError instead, and never defines
    PublishNextVersion. The .iss stops the compile unless PublishNextVersion is
    defined, so a failure here cannot produce a mis-versioned installer, and it
    cannot silently yield an empty version either (an empty AppVersion is what
    Inno reports as "The [Setup] section must include an AppVersion directive").

.NOTES
    ORDERING CAVEAT — this runs BEFORE the compiler produces setup.exe, because ISPP
    has no post-compile hook. So the old installer is removed before the new one
    exists. Every removed file is first copied to %TEMP%\LemoineToolsSetup-backup\
    (a backup that fails blocks the delete), and the path is reported in
    PublishMessage, so a failed compile is always recoverable. Pass -KeepOld to skip
    removal entirely.
#>
[CmdletBinding()]
param(
    # ISPP include file to generate. LemoineTools.iss #includes it, so the version
    # arrives as a real #define rather than something parsed back out of a data file:
    # either PublishNextVersion is defined and correct, or the compile stops.
    [Parameter(Mandatory = $true)]
    [string] $OutFile,

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

# Values become ISPP string literals in the generated include, so strip anything that
# could break out of one: newlines (a #define is a single line) and double quotes.
# Backslashes are NOT escapes in ISPP strings, so Windows paths pass through as-is.
function ConvertTo-IsppLiteral {
    param([string] $Value)
    if ($null -eq $Value) { return "" }
    return (($Value -replace '[\r\n]+', ' ') -replace '"', "'").Trim()
}

# Writes the ISPP include LemoineTools.iss pulls in. Only ever called on success —
# on failure Write-FailureInclude is used instead, so PublishNextVersion is defined
# if and only if there is a real version to build.
function Write-SuccessInclude {
    param(
        [string] $Path,
        [string] $Next,
        [string] $Current,
        [string] $OutputDir,
        [string] $Status,
        [string] $Message
    )

    if ([string]::IsNullOrWhiteSpace($Next)) {
        throw "Internal error: no version was determined, refusing to write a success include."
    }

    $lines = @(
        "; Generated by publish-version.ps1 - do not edit, do not commit.",
        "; Regenerated every time LemoineTools.iss is compiled.",
        ("; Written " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss")),
        "",
        ('#define PublishNextVersion "' + (ConvertTo-IsppLiteral $Next)      + '"'),
        ('#define PublishPrevVersion "' + (ConvertTo-IsppLiteral $Current)   + '"'),
        ('#define PublishOutputDir "'   + (ConvertTo-IsppLiteral $OutputDir) + '"'),
        ('#define PublishStatus "'      + (ConvertTo-IsppLiteral $Status)    + '"'),
        ('#define PublishMessage "'     + (ConvertTo-IsppLiteral $Message)   + '"')
    )
    Set-Content -LiteralPath $Path -Value $lines -Encoding ASCII
}

# Defines PublishError and nothing else. LemoineTools.iss turns that into a compiler
# error carrying this message, so a failure here is always visible and explained.
function Write-FailureInclude {
    param([string] $Path, [string] $Message)

    $lines = @(
        "; Generated by publish-version.ps1 - the run FAILED, see below.",
        ('; Written ' + (Get-Date -Format "yyyy-MM-dd HH:mm:ss")),
        "",
        ('#define PublishError "' + (ConvertTo-IsppLiteral $Message) + '"')
    )
    Set-Content -LiteralPath $Path -Value $lines -Encoding ASCII
}

try {
    $publishDirExists = -not [string]::IsNullOrWhiteSpace($PublishDir) -and (Test-Path -LiteralPath $PublishDir)

    # ---------------------------------------------------------------------
    # Build locally: no detection, no folder changes.
    # ---------------------------------------------------------------------
    if ($NoPublish) {
        $v = if ($Version) { $Version } else { "1.0.0" }
        Write-SuccessInclude -Path $OutFile -Next $v -Current "" -OutputDir "output" `
                             -Status "nopublish" -Message "-NoPublish: building to installer\output\, shared folder untouched."
        exit 0
    }

    # ---------------------------------------------------------------------
    # Folder unreachable: fall back to a local build rather than guessing a
    # version. Nothing is deleted, because nothing could be read.
    # ---------------------------------------------------------------------
    if (-not $publishDirExists) {
        $v = if ($Version) { $Version } else { "1.0.0" }
        Write-SuccessInclude -Path $OutFile -Next $v -Current "" -OutputDir "output" -Status "nofolder" `
                             -Message "Publish folder NOT found ($PublishDir) - building to installer\output\ instead. Is the VDC library synced by OneDrive?"
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

    Write-SuccessInclude -Path $OutFile -Next $next -Current $currentVersion -OutputDir $PublishDir `
                         -Status "ok" -Message $message
    exit 0
}
catch {
    # Emit an include that defines PublishError and NOT PublishNextVersion, so
    # LemoineTools.iss stops the compile and shows this message rather than stamping
    # a guessed version.
    $reason = $_.Exception.Message
    try {
        Write-FailureInclude -Path $OutFile -Message $reason
    }
    catch {
        # The include could not be written either. Deliberately swallowed: the .iss
        # already treats a missing/incomplete include as a hard error, which is the
        # safe outcome, and there is no channel left to report on.
    }
    exit 1
}
