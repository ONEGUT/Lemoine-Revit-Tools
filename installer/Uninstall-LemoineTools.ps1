#Requires -Version 5.0
<#
.SYNOPSIS
    Removes Lemoine Tools from the current user's Revit add-ins folder.
    No administrator privileges required.

.DESCRIPTION
    Deletes LemoineTools.dll and LemoineTools.addin from
        %AppData%\Autodesk\Revit\Addins\<year>\
    for each requested Revit version. Only touches the per-user folder; a
    machine-wide copy under ProgramData (if any) is left untouched because
    removing it would require elevation.

.PARAMETER RevitYears
    One or more Revit version years to target. Defaults to 2024.

.EXAMPLE
    .\Uninstall-LemoineTools.ps1

.EXAMPLE
    .\Uninstall-LemoineTools.ps1 -RevitYears 2024,2025
#>
[CmdletBinding()]
param(
    [int[]] $RevitYears = @(2024)
)

$ErrorActionPreference = 'Stop'

$payload    = @('LemoineTools.dll', 'LemoineTools.addin')
$removedAny = $false
$hadError   = $false

foreach ($year in $RevitYears) {
    $dest = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
    foreach ($file in $payload) {
        $path = Join-Path $dest $file
        if (Test-Path $path) {
            try {
                Remove-Item -LiteralPath $path -Force
                $removedAny = $true
                Write-Host "Removed $path" -ForegroundColor Yellow
            }
            catch {
                $hadError = $true
                Write-Warning ("Failed to remove {0}: {1}" -f $path, $_.Exception.Message)
            }
        }
    }
}

if (-not $removedAny -and -not $hadError) {
    Write-Host "Nothing to remove — Lemoine Tools was not found in the per-user add-ins folder(s)."
}

if ($hadError) {
    Write-Warning "One or more files could not be removed. See the messages above."
    exit 1
}

Write-Host "Done. Restart Revit to unload Lemoine Tools." -ForegroundColor Cyan
