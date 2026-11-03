# SharpMind Studio

SharpMind Studio is a local-first application for chatting with models, doing
project work with them, and building them: from scratch, from a base model, or
by upgrading a model you already have. It is the successor to the SharpMind
terminal UI (CUI) as the primary front end.

Studio is built on the published [SharpMind](https://github.com/Integral2u/SharpMind)
libraries and shares their model, tokenizer and run formats.

## Modes

| Mode | For | What it is |
|---|---|---|
| **Chat** | Everyone | Pick a model, talk to it. Saved sessions, system prompt and a tools drawer. Models carry a "runs well / runs slowly / won't fit" badge computed from the machine. |
| **Work** | Practitioners | Project spaces. Each project is a folder with its own system prompt, skills, tool permissions, memory, and `runs/` and `models/` directories. |
| **Training** | Builders | The lab for creating and improving models: **New model**, **Improve**, **Playbooks**, **Data**, **Runs**, **Models**. |

## Requirements

- Windows 10/11 (the optional WPF/WebView2 shell is Windows-only).
- [.NET 10 SDK](https://dotnet.microsoft.com/) (pinned by `global.json`).
- A GGUF or `.smm` model for Chat and Work.

## Build and run

```powershell
dotnet build SharpMind.Studio.slnx -c Debug
dotnet test  SharpMind.Studio.slnx -c Debug

dotnet run --project SharpMind.Studio
```

The web UI listens on `http://127.0.0.1:5117`. The optional desktop shell is
`SharpMind.Studio.UI`; it reads the Studio host/port from
`%USERPROFILE%\SharpMind\Studio\settings.json`.

Build a release and publish the web host or desktop shell:

```powershell
dotnet publish SharpMind.Studio     -c Release -o publish/web
dotnet publish SharpMind.Studio.UI  -c Release -o publish/desktop
```

## SharpMind reference mode

Studio defaults to the published **SharpMind 1.0.9** NuGet packages.

`SharpMind.References.props` controls the reference mode:

- **package** (default) resolves the six `SharpMind.*` packages from NuGet.
  Versions are centralised in `Directory.Packages.props`.
- **project** builds against a sibling source checkout at `..\SharpMind\`.
  The project-reference block is kept but commented out; uncomment it and pass
  `-p:SharpMindSource=project`:

  ```powershell
  dotnet build SharpMind.Studio.slnx -c Debug -p:SharpMindSource=project
  ```

`nuget.config` contains a commented local feed (`..\local-packages`) for testing
unpublished packages. Uncomment it when needed.

## Where data lives

Studio keeps machine settings separate from project data.

```
%USERPROFILE%\SharpMind
  Models\  Corpus\  Training\  Chat Sessions\   shared with the CUI
  Skills\                                       shared Work skills repository
  Studio\                                       machine settings, not a project
    settings.json  inference.json  workspace.json
    work-skills.json  work-tools.json
    Gateway\                                    device/pairing state
  Workspaces\
    Studio\                                     default project
      project.json  work.json  memory.md
      runs\  chats\  model-lists\  data\  models\  evals\
```

## Phone pairing

Studio hosts the phone experience itself. Two listeners run in one process:
loopback admin on `127.0.0.1:5117` and a LAN listener on `0.0.0.0:5118`.
Pairing, device tokens and the QR/Local-Global helpers live under
**Settings > Pair**. The LAN port is always bound; while LAN pairing is off,
non-loopback requests are rejected so the toggle takes effect immediately.

Paired phones get Chat plus a bare-bones Work scope, enforced server-side from
signed claims.

## License

MIT. See [LICENSE.txt](LICENSE.txt).
