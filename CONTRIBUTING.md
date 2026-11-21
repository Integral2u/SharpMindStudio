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

Studio builds against the published SharpMind packages by default, so a clean
clone needs no engine checkout:

```powershell
dotnet build SharpMind.Studio.slnx -c Release
```

To develop against the sibling `SharpMind` source tree instead, check it out
beside this repository on its matching `release/x.y.z` branch and pass the
switch:

```powershell
dotnet build SharpMind.Studio.slnx -c Release -p:SharpMindSource=project
```

The `release/x.y.z` branch defaults to the source tree and `main` defaults to the
packages. Keep both paths working: CI builds and tests the package mode on every
push, and the release branch against the engine source, so do not add a Studio
dependency on an unreleased SharpMind API.

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

## Branch and release workflow

See [docs/contributing/workflow.md](docs/contributing/workflow.md) for the
local-first branch, push, release, Store resubmission and hotfix rules. In
short: work on `release/x.y.z` or a short-lived branch off it, commit locally,
push for backups or for gates that cannot run locally, and merge to `main` only
at release time or for an urgent fix.

## Pull requests

- Keep changes focused and explain the user-visible effect.
- Add or update tests for behaviour changes.
- Run the full build, tests and encoding guard before opening a PR.
- For security issues, follow [SECURITY.md](SECURITY.md) instead of opening a public PR with details.
