#requires -Version 5.1
<#
.SYNOPSIS
    Writes the Store listing images: the 300x300 app tile icon Partner Center
    asks for, plus the tile assets the MSIX carries, so the same mark can be
    reused for the GitHub social preview or a future listing update.

.DESCRIPTION
    The drawing lives in scripts\StudioMark.ps1, which the MSIX packager also
    uses. Nothing here is hand-edited; rerun it after changing the mark.

.EXAMPLE
    .\scripts\build-store-assets.ps1
#>
param(
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'StudioMark.ps1')

if (-not $OutDir) { $OutDir = Join-Path $root 'store' }
$icons = Join-Path $OutDir 'icons'
New-Item -ItemType Directory -Path $icons -Force | Out-Null

# Partner Center: App tile icon, 300x300 PNG.
Save-StudioMark -Path (Join-Path $OutDir 'logo-300.png') -Size 300

# The same assets the package ships, kept beside the listing copy.
Save-StudioMark -Path (Join-Path $icons 'Square44x44Logo.png') -Size 44
Save-StudioMark -Path (Join-Path $icons 'Square150x150Logo.png') -Size 150
Save-StudioMark -Path (Join-Path $icons 'StoreLogo.png') -Size 50
Save-StudioMark -Path (Join-Path $icons 'Square310x310Logo.png') -Size 310
Save-StudioCanvas -Path (Join-Path $icons 'Wide310x150Logo.png') -Width 310 -Height 150 -MarkSize 118
Save-StudioCanvas -Path (Join-Path $icons 'SplashScreen.png') -Width 620 -Height 300 -MarkSize 200

Get-ChildItem $OutDir -Recurse -File | ForEach-Object {
    '{0,-40} {1,8} {2}' -f $_.FullName.Substring($OutDir.Length + 1), $_.Length,
        (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0, 12)
}
