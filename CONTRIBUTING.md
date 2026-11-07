# Contributing to SharpMind Studio

Thanks for taking the time to help. Studio is a Windows desktop shell around the SharpMind libraries plus a Blazor workbench, so there are a few moving parts.

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- For the installer: Visual Studio 18 Insiders with **Microsoft Visual Studio Installer Projects**

## Build and test

```powershell
dotnet build SharpMind.Studio.slnx -c Debug
dotnet test SharpMind.Studio.slnx -c Debug --no-build
```

Studio normally builds against the sibling `SharpMind` source checkout when it exists. A clean clone with no sibling uses the published NuGet packages; keep both paths working:

```powershell
dotnet build SharpMind.Studio.slnx -c Release -p:SharpMindSource=package
```

CI runs that package-mode build on every push, so do not add a Studio dependency on an unreleased SharpMind API.

## Encoding guard

```powershell
pwsh ./scripts/check-encoding.ps1
```

CI fails on the UTF-8-decoded-as-Windows-1252 signatures (`U+00E2`, `U+00C3`, `U+00C2`, `U+FFFD`). Keep comments and log strings as plain ASCII punctuation; avoid pasting text from a console that has already mangled it.

## Style

- C# file-scoped namespaces, nullable enabled, `sealed` unless inheritance is intended.
- Prefer small services and records over static mutable state.
- Tests use xUnit and live in `SharpMind.Studio.Tests`.
- Do not commit secrets, keys, model files, build output, or `SharpMind.Studio Setup/Release/`.

## Installer

The per-user MSI is built from `SharpMind.Studio.Setup.sln` with Visual Studio 18; the `.slnx` cannot host the `.vdproj`.

- Keep `UpgradeCode` fixed.
- Bump `PackageCode` for every rebuilt MSI; bump `ProductCode` only when the product identity changes.
- Do not commit the generated MSI or `setup.exe`.
- The app repairs the installer's advertised shortcut on first launch (icon and target), so test a clean install, not only a source build.

## Pull requests

- Keep changes focused and explain the user-visible effect.
- Add or update tests for behaviour changes.
- Run the full build, tests and encoding guard before opening a PR.
- For security issues, follow [SECURITY.md](SECURITY.md) instead of opening a public PR with details.
