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
- Uninstall must not remove `%USERPROFILE%\SharpMind` (models, sessions, keys).

## Adding static files

The web `wwwroot` files are listed explicitly in the setup project because the
web publish output group does not include them. If a new file is added under
`SharpMind.Studio\wwwroot`, add it to the **File System** view under `wwwroot`
and rebuild.
