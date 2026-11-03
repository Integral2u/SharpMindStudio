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

## Training smoke test (working notes)

> This section is a pre-release helper and will be rewritten with the main README.
> It records the first real corpus-to-export runs so the numbers and commands are
> easy to reproduce.

The headless CLI can merge a corpus, train a saved `.smmt` job and score the
held-out exam without the UI:

```powershell
# Split a pre-marked corpus into JSONL documents.
dotnet run --project SharpMind.Studio.Cli -- merge <source-folder> <output.jsonl> `
  --layout separator --separator '<|endoftext|>'

# Train a saved job.
dotnet run --project SharpMind.Studio.Cli -- train <job.smmt> --fresh

# Continue from the cached checkpoint to a longer horizon.
dotnet run --project SharpMind.Studio.Cli -- train <job.smmt> --steps 10000 `
  --export <new-export-folder>\model.smm --resume

# Score the held-out exam, either from a run record or a job/model pair.
dotnet run --project SharpMind.Studio.Cli -- exam <run-history.json>
dotnet run --project SharpMind.Studio.Cli -- exam <job.smmt> <model.smm>
```

Two TinyStories runs used the same 128-hidden / 4-layer model, BPE 2048,
128-token sequences and a 10% held-out exam:

| Corpus | Steps | Validation | Exam | Time |
|---|---:|---:|---:|---:|
| TinyStories subset, 256 stories | 2,000 | 4.43 / ppl 83.9 | 4.70 / ppl 110.2 | ~16 min |
| TinyStories prefix, 23,311 JSONL docs | 5,000 | 3.49 / ppl 32.6 | 3.60 / ppl 36.6 | 47 min |
| Same corpus, continued from step 5,000 | 10,000 | 3.26 / ppl 26.0 | 3.36 / ppl 28.9 | +47 min |

The larger corpus reduced both validation loss and the validation-to-exam gap.
Extending the same run continued to improve it, but the gain from 5,000 to
10,000 steps was smaller than the gain from more data. The model is still
capacity-limited.

Example generation from the 10,000-step model, prompted with
`Once upon a time, there was a little`:

> named Lily. She loved to play outside in the park with her toys. One day,
> Lily's mom asked her to help her mom if she could play with her. Lily asked
> her mom if she

This is a smoke-test result, not a quality benchmark: the model is a 4.3 MB
128/4 model, not a production model. It is enough to prove the merge, JSONL
source, tokenizer, EOS boundaries, checkpoints, curves, export, checkpoint
continuation and exam paths.

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
