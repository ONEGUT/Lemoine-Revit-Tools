#Requires -Version 5.0
<#
.SYNOPSIS
    Installs Lemoine Tools into the current user's Revit add-ins folder.
    No administrator privileges required.

.DESCRIPTION
    Copies LemoineTools.dll and LemoineTools.addin into
        %AppData%\Autodesk\Revit\Addins\<year>\
    for each requested Revit version. %AppData% (Roaming) is owned by the
    current user, so no elevation / UAC prompt is needed.

    Place the built LemoineTools.dll and LemoineTools.addin next to this
    script before running it.

.PARAMETER RevitYears
    One or more Revit version years to target. Defaults to 2024.

.EXAMPLE
    .\Install-LemoineTools.ps1

.EXAMPLE
    .\Install-LemoineTools.ps1 -RevitYears 2024,2025
#>
[CmdletBinding()]
param(
    [int[]] $RevitYears = @(2024)
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$payload   = @('LemoineTools.dll', 'LemoineTools.addin')

# Verify the payload sits next to this script.
$missing = $payload | Where-Object { -not (Test-Path (Join-Path $scriptDir $_)) }
if ($missing) {
    throw ("Missing install file(s) next to this script: {0}. " -f ($missing -join ', ')) +
          ("Place the built LemoineTools.dll and LemoineTools.addin in '{0}' and re-run." -f $scriptDir)
}

$hadError = $false
foreach ($year in $RevitYears) {
    $dest = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    try {
        if (-not (Test-Path $dest)) {
            New-Item -ItemType Directory -Path $dest -Force | Out-Null
        }
        foreach ($file in $payload) {
            Copy-Item -LiteralPath (Join-Path $scriptDir $file) -Destination $dest -Force
        }
        Write-Host "Installed Lemoine Tools for Revit $year -> $dest" -ForegroundColor Green
    }
    catch {
        $hadError = $true
        Write-Warning ("Failed to install for Revit {0}: {1}" -f $year, $_.Exception.Message)
    }
}

if ($hadError) {
    Write-Warning "One or more targets failed. See the messages above."
    exit 1
}

Write-Host "Done. Restart Revit to load Lemoine Tools." -ForegroundColor Cyan
