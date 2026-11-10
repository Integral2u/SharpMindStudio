param(
    [Parameter(Mandatory = $true)]
    [string]$PfxPath,

    [Parameter(Mandatory = $true)]
    [string]$PfxPassword,

    [string]$SignTool = 'C:\Integral2u\SignTool-10.0.26100.14-x64\signtool.exe',

    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $SignTool)) {
    throw "signtool.exe was not found at '$SignTool'."
}
if (-not (Test-Path $PfxPath)) {
    throw "The signing certificate was not found at '$PfxPath'."
}

$release = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\SharpMind.Studio Setup\Release'))
$setup = Join-Path $release 'setup.exe'
$msi = Join-Path $release 'SharpMind.Studio Setup.msi'
$zip = Join-Path $release 'SharpMind.Studio.Setup.msi.zip'
$sums = Join-Path $release 'SHA256SUMS.txt'

foreach ($file in @($setup, $msi)) {
    if (-not (Test-Path $file)) {
        throw "Missing installer output: '$file'."
    }

    Write-Host "Signing $file"
    & $SignTool sign /fd SHA256 /f $PfxPath /p $PfxPassword `
        /tr $TimestampUrl /td SHA256 $file
    if ($LASTEXITCODE -ne 0) {
        throw "signtool sign failed for '$file' with exit code $LASTEXITCODE."
    }

    & $SignTool verify /pa /v $file
    if ($LASTEXITCODE -ne 0) {
        throw "signtool verify failed for '$file' with exit code $LASTEXITCODE."
    }
}

Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path $setup, $msi -DestinationPath $zip -CompressionLevel Optimal

$lines = foreach ($file in @($setup, $msi, $zip)) {
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash
    "$hash  $([IO.Path]::GetFileName($file))"
}
[IO.File]::WriteAllLines($sums, $lines, [Text.UTF8Encoding]::new($false))

Get-Content $sums
