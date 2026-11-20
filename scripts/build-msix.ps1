#requires -Version 5.1
<#
.SYNOPSIS
    Builds the SharpMind Studio MSIX package for the Microsoft Store.

.DESCRIPTION
    Publishes the web host and the desktop shell in package mode, adds the tile
    assets and an AppxManifest generated from msix\AppxManifest.template.xml,
    packs them with MakeAppx and signs the result.

    The Store re-signs submissions, so a self-signed developer certificate is
    enough to submit. The same certificate is what lets you install the package
    locally for testing: trust SharpMind.Studio.cer in
    LocalMachine\TrustedPeople before the first Add-AppxPackage.

.PARAMETER IdentityName
    Package identity name. Must match Product identity in Partner Center.

.PARAMETER Publisher
    Publisher distinguished name. Must match Product identity in Partner Center,
    and the signing certificate's subject.

.EXAMPLE
    .\scripts\build-msix.ps1
    Builds with the development identity CN=SharpMind Studio.

.EXAMPLE
    .\scripts\build-msix.ps1 -IdentityName '12345Integral2u.SharpMindStudio' `
        -Publisher 'CN=1A2B3C4D-5E6F-7890-ABCD-EF1234567890'
#>
param(
    [string]$IdentityName = 'Integral2u.SharpMindStudio',

    [string]$Publisher = 'CN=SharpMind Studio',

    [string]$Version,

    [ValidateSet('x64', 'x86', 'arm64')]
    [string]$Architecture = 'x64',

    [string]$OutDir,

    [string]$PfxPath,

    [string]$PfxPassword,

    # The MSI is framework-dependent and the Setup README says so. A Store
    # download has to start on a machine with nothing installed, so the MSIX
    # carries the .NET runtime unless this is set.
    [switch]$FrameworkDependent,

    [switch]$SkipSign
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$manifestTemplate = Join-Path $root 'SharpMind.Studio Setup\msix\AppxManifest.template.xml'

if (-not (Test-Path $manifestTemplate)) {
    throw "Missing manifest template: '$manifestTemplate'."
}

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
    $Version = @($props.Project.PropertyGroup | Where-Object { $_.Version })[0].Version
    if (-not $Version) { throw 'Could not read <Version> from Directory.Build.props.' }
}

if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') {
    throw "MSIX needs a four-part version whose revision is 0 (got '$Version')."
}

if (-not $OutDir) { $OutDir = Join-Path $root 'SharpMind.Studio Setup\Release' }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

function Find-Tool([string]$Name) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\*\x64\$Name"),
        (Join-Path $env:ProgramFiles "Windows Kits\10\bin\*\x64\$Name"),
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Shared\NuGetPackages\microsoft.windows.sdk.buildtools\*\bin\*\x64\$Name"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\$Name")
    )

    foreach ($candidate in $candidates) {
        $found = Get-ChildItem $candidate -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    throw "$Name was not found. Install the Windows SDK (or the Windows SDK build tools NuGet package)."
}

$makeappx = Find-Tool 'makeappx.exe'
$signtool = Find-Tool 'signtool.exe'

Write-Host "makeappx: $makeappx"
Write-Host "signtool: $signtool"

# --- publish -------------------------------------------------------------

$stage = Join-Path $env:TEMP "sharpmind-msix-$([guid]::NewGuid().ToString('n'))"
New-Item -ItemType Directory -Path $stage -Force | Out-Null

try {
    foreach ($project in @(
            'SharpMind.Studio\SharpMind.Studio.csproj',
            'SharpMind.Studio.UI\SharpMind.Studio.UI.csproj')) {
        Write-Host "Publishing $project"

        $publishArgs = @(
            (Join-Path $root $project),
            '-c', 'Release',
            '-o', $stage,
            '-p:SharpMindSource=package',
            '--nologo',
            '-v', 'quiet')

        if (-not $FrameworkDependent) {
            $runtime = switch ($Architecture) {
                'x64' { 'win-x64' }
                'x86' { 'win-x86' }
                'arm64' { 'win-arm64' }
            }
            $publishArgs += @('-r', $runtime, '--self-contained', 'true')
        }

        dotnet publish @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for '$project'." }
    }

    # Ship binaries, not debug output or developer settings.
    Get-ChildItem $stage -Recurse -File -Include '*.pdb', 'appsettings.Development.json', 'web.config' |
        Remove-Item -Force

    $required = @(
        'SharpMind.Studio.UI.exe',
        'SharpMind.Studio.UI.dll',
        'SharpMind.Studio.exe',
        'SharpMind.Studio.dll',
        'SharpMind.Studio.deps.json',
        'wwwroot\theme.js',
        'wwwroot\chat.js')
    foreach ($file in $required) {
        if (-not (Test-Path (Join-Path $stage $file))) {
            throw "Publish output is missing '$file'; the package would not run."
        }
    }

    # --- tile assets -----------------------------------------------------

    . (Join-Path $PSScriptRoot 'StudioMark.ps1')
    $assets = Join-Path $stage 'Assets'
    New-Item -ItemType Directory -Path $assets -Force | Out-Null

    Save-StudioMark -Path (Join-Path $assets 'Square44x44Logo.png') -Size 44
    Save-StudioMark -Path (Join-Path $assets 'Square150x150Logo.png') -Size 150
    Save-StudioMark -Path (Join-Path $assets 'Square310x310Logo.png') -Size 310
    Save-StudioMark -Path (Join-Path $assets 'StoreLogo.png') -Size 50
    Save-StudioCanvas -Path (Join-Path $assets 'Wide310x150Logo.png') -Width 310 -Height 150 -MarkSize 118
    Save-StudioCanvas -Path (Join-Path $assets 'SplashScreen.png') -Width 620 -Height 300 -MarkSize 200

    # --- manifest --------------------------------------------------------

    $manifest = Get-Content $manifestTemplate -Raw
    $manifest = $manifest.
        Replace('{{IDENTITY_NAME}}', $IdentityName).
        Replace('{{PUBLISHER}}', $Publisher).
        Replace('{{VERSION}}', $Version).
        Replace('{{ARCHITECTURE}}', $Architecture)
    [IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

    # --- pack ------------------------------------------------------------

    $msix = Join-Path $OutDir 'SharpMind.Studio.msix'
    if (Test-Path $msix) { Remove-Item $msix -Force }

    Write-Host "Packing $msix"
    & $makeappx pack /d $stage /p $msix /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE." }

    # --- sign ------------------------------------------------------------

    if (-not $SkipSign) {
        if ($PfxPath) {
            $signArgs = @('sign', '/fd', 'SHA256', '/f', $PfxPath)
            if ($PfxPassword) { $signArgs += @('/p', $PfxPassword) }
        }
        else {
            $cert = Get-ChildItem Cert:\CurrentUser\My |
                Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey } |
                Sort-Object NotAfter -Descending |
                Select-Object -First 1

            if (-not $cert) {
                Write-Host "Creating a developer certificate for '$Publisher'"
                $cert = New-SelfSignedCertificate `
                    -Type CodeSigningCert `
                    -Subject $Publisher `
                    -KeyUsage DigitalSignature `
                    -FriendlyName 'SharpMind Studio MSIX (developer)' `
                    -NotAfter (Get-Date).AddYears(3) `
                    -CertStoreLocation Cert:\CurrentUser\My
            }

            Export-Certificate -Cert $cert -FilePath (Join-Path $OutDir 'SharpMind.Studio.cer') -Force | Out-Null
            $signArgs = @('sign', '/fd', 'SHA256', '/sha1', $cert.Thumbprint)
        }

        $signArgs += @('/tr', 'http://timestamp.digicert.com', '/td', 'SHA256', $msix)

        Write-Host "Signing $msix"
        & $signtool @signArgs
        if ($LASTEXITCODE -ne 0) { throw "signtool sign failed with exit code $LASTEXITCODE." }

        # Run through cmd so a non-zero exit stays a plain exit code instead of a
        # terminating NativeCommandError under $ErrorActionPreference = 'Stop'.
        cmd /c "`"$signtool`" verify /pa `"$msix`" 2>&1" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $signature = Get-AuthenticodeSignature -FilePath $msix
            $signedByUs = $signature.SignerCertificate -and
                $signature.SignerCertificate.Subject -eq $Publisher

            if ($PfxPath -or -not $signedByUs) {
                throw 'signtool verify rejected the signature.'
            }

            # A self-signed certificate is not in a trusted root here, so the
            # chain fails by design. The signature itself is intact, and the
            # Store re-signs submissions anyway.
            Write-Warning "Signed with the developer certificate '$Publisher', which this machine does not trust. Trust SharpMind.Studio.cer in LocalMachine\TrustedPeople to install it locally; submitting it to the Store needs nothing more."
        }
    }

    $size = (Get-Item $msix).Length
    $hash = (Get-FileHash $msix -Algorithm SHA256).Hash
    Write-Host ''
    Write-Host "SharpMind Studio $Version ($Architecture)"
    Write-Host "  identity : $IdentityName"
    Write-Host "  publisher: $Publisher"
    Write-Host "  package  : $msix"
    Write-Host "  size     : $size bytes"
    Write-Host "  sha256   : $hash"

    if ($IdentityName -eq 'Integral2u.SharpMindStudio' -or $Publisher -eq 'CN=SharpMind Studio') {
        Write-Warning 'Built with the development identity. Replace -IdentityName and -Publisher with the values from Partner Center before submitting.'
    }
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

# A failed developer-certificate chain check leaves $LASTEXITCODE set; the
# package itself was produced, so the script ran successfully.
exit 0
