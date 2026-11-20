# SharpMind Studio Setup

Visual Studio Installer project for the Windows desktop build.

## Build

1. Install the **Microsoft Visual Studio Installer Projects** extension.
2. Use Visual Studio 18 (net10 support) and open `SharpMind.Studio.Setup.sln`.
3. Build **Release**. The MSI and bootstrapper land in
   `SharpMind.Studio Setup\Release\`.

The project builds the UI and web host, then packages:

- the web host publish output (API + Studio UI + `wwwroot` + dependencies);
- the WPF wrapper (`SharpMind.Studio.UI.exe`) beside it, which is what
  `StudioProcess` expects;
- the WebView2 native loader under `runtimes\`;
- the application icon, Start menu and desktop shortcuts.

## Before shipping

- The app is framework-dependent. Install the **.NET 10 Desktop Runtime** and
  **ASP.NET Core Runtime**, or publish self-contained before packaging.
- WebView2 Evergreen Runtime is required. Most current Windows installs have it;
  add the bootstrapper if you need to support clean machines.
- `UpgradeCode` stays fixed across 1.1.x. Change `ProductCode`/`PackageCode` for
  each release and bump `ProductVersion`.
- Sign `setup.exe` and the MSI after the build; do not sign before packaging.
  With a PFX certificate:

  ```powershell
  ..\scripts\sign-release.ps1 -PfxPath C:\path\to\codesign.pfx -PfxPassword '...'
  ```

  With a certificate installed from a hardware token or HSM, use its thumbprint:

  ```powershell
  ..\scripts\sign-release.ps1 -Thumbprint <sha1-thumbprint>
  ```

  The script signs and verifies both files with SHA-256 and a timestamp, rebuilds
  `SharpMind.Studio.Setup.msi.zip`, and rewrites `SHA256SUMS.txt`.
- Uninstall must not remove `%USERPROFILE%\SharpMind` (models, sessions, keys).
- The web host needs the generated
  `SharpMind.Studio.staticwebassets.endpoints.json` and
  `SharpMind.Studio.staticwebassets.runtime.json`; both are included from the
  Release output. If a future SDK changes those file names, update the File
  System entries.
- If you replace an already installed test build, bump `ProductCode` and
  `PackageCode` (or uninstall first). Keep `UpgradeCode` fixed.
- Visual Studio creates advertised shortcuts whose target is its installer stub.
  The app replaces them with normal shortcuts to the wrapper on first launch, so
  the correct icon and target appear after the first run.

## Adding static files

The web `wwwroot` files are listed explicitly in the setup project because the
web publish output group does not include them. If a new file is added under
`SharpMind.Studio\wwwroot`, add it to the **File System** view under `wwwroot`
and rebuild.

## MSIX (Microsoft Store)

`scripts\build-msix.ps1` produces the Store package. It does not need Visual
Studio or this installer project: it publishes both projects, generates the tile
assets from `wwwroot\sharpmind-icon.svg`, writes `AppxManifest.xml` from
`msix\AppxManifest.template.xml`, packs with MakeAppx and signs.

```powershell
# development identity, for installing locally
.\scripts\build-msix.ps1

# the identity Partner Center shows under Product identity
.\scripts\build-msix.ps1 -IdentityName '12345Integral2u.SharpMindStudio' `
    -Publisher 'CN=1A2B3C4D-5E6F-7890-ABCD-EF1234567890'
```

Output: `Release\SharpMind.Studio.msix` and, unless `-SkipSign` is used, the
matching `SharpMind.Studio.cer`.

- The package is **self-contained**: it carries the .NET 10 and ASP.NET Core
  runtimes, so a Store download starts on a clean machine. `-FrameworkDependent`
  builds the smaller package that needs the runtimes installed, matching the MSI.
- `Identity Name` and `Publisher` must equal the Partner Center values. A
  mismatch is rejected at submission; a matching Name is also what makes a
  rebuild an update rather than a second app.
- Signing a submission with a self-signed certificate is fine: the Store
  re-signs the package. The certificate only matters for local installs, where
  `SharpMind.Studio.cer` must be trusted in `LocalMachine\TrustedPeople` first.
- The MSIX is Store-only. GitHub releases keep the MSI and zip until the Store
  listing is live.
- The manifest declares `runFullTrust` because the wrapper starts the web host as
  a sibling process and binds loopback listeners.
