# SharpMind Studio

SharpMind Studio is a local-first application for chatting with models, doing
project work with them, and building them: from scratch, from a base model, or
by upgrading a model you already have. It is the successor to the SharpMind
terminal UI (CUI) as the primary front end.

Studio is built on the published [SharpMind](https://github.com/Integral2u/SharpMind)
libraries and shares their model, tokenizer and run formats.

The static preview site and wiki live under [`docs/`](docs/index.html) and publish
to GitHub Pages. Open `docs/index.html` locally to preview it without a server.

## Modes

| Mode | For | What it is |
|---|---|---|
| **Chat** | Everyone | Pick a model, talk to it. Saved sessions, system prompt and a tools drawer. Models carry a "runs well / runs slowly / won't fit" badge computed from the machine. |
| **Work** | Practitioners | Project spaces. Each project is a folder with its own system prompt, skills, tool permissions, memory, and `runs/` and `models/` directories. |
| **Training** | Builders | The lab for creating and improving models: **New model**, **Improve**, **Playbooks**, **Data**, **Runs**, **Models**. |

## Requirements

- Windows 10/11 (the optional WPF/WebView2 shell is Windows-only).
- [.NET 10 SDK](https://dotnet.microsoft.com/) (pinned by `global.json`).
- A GGUF or `.smm` model for local Chat and Work, or an OpenRouter API key for
  remote models.

## OpenRouter

Chat and Work can use OpenRouter models as well as local weights. Add an API key
under **Settings > OpenRouter Providers**; it is encrypted with a local key file,
and the provider URL and active model list live beside it. Fetch the catalog,
add models to the active list, and pick a default. On first use the active list
starts with `openrouter/free` and the free models.

OpenRouter is inference-only: local tuning remains unavailable for remote
models. Work mode keeps its project tools, permissions, skills and memory local.
Only the model conversation and the tool results the model asks for are sent to
OpenRouter. OpenAI-style tool calling is supported, including the provider's
native streaming tool-call deltas.

## HuggingFace models

The access token lives under **Settings → Providers → HuggingFace Model source**.
Only a token is needed for gated repositories; public GGUF downloads work without
one, and no username is required.

Open **Training → Models** and use **Find GGUF models**:

- search the HuggingFace Hub for GGUF repositories
- inspect license, gated status, files, quantisation and size
- download to `%USERPROFILE%\SharpMind\Models` with progress and resume

Downloaded GGUF files can be converted to `.smm` with the converter directly
below the browser, or used as local models where supported.

## Data Factory

Open **Training > Data** to build a dataset as a saved recipe (`.smmdf`):

1. Pick a text folder or JSONL source.
2. Apply HTML stripping, text normalisation, length limits, deduplication and an
   optional drop regex.
3. Choose a local model or an active OpenRouter model and edit the system/user
   prompt template. `{text}`, `{source}`, `{name}` and `{id}` are available.
4. Preview the source or generate a small sample before committing to a run.
5. Export chat JSONL (`messages`) for SFT or text JSONL (`text`) for
   pretraining.

Recipes are stored under `Training/DataFactory`, so a dataset can be regenerated
or adjusted later.

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

### Docker

A preview Docker image for the Studio web app lives in `docker/studio`. The
local-source Dockerfile builds against the sibling `SharpMind` checkout and does
not need the packages published; the normal Dockerfile uses NuGet for a clean
public/CI build.

```powershell
# local source (parent repos folder as context)
docker build -f SharpMind.Studio/docker/studio/Dockerfile.local `
  -t sharpmind-studio:local ..

# package build, after the SharpMind packages are published
docker build -f docker/studio/Dockerfile -t sharpmind-studio:preview .
docker run --rm -p 5117:5117 -p 5118:5118 `
  -v "${PWD}/data:/data/SharpMind" sharpmind-studio:preview
```

See `docker/studio/README.md` for compose, volumes and the data directory.

### Compute host

`SharpMind.Studio.ComputeHost` is the first Cloud/Compute service: one model per
process, OpenAI-style chat completions, and background training jobs. Build it
with `docker/compute/Dockerfile.local` or the package Dockerfile, then run it on
local Docker or a GPU host such as Runpod. See `docker/compute/README.md` for
the API, environment variables, local test commands and Runpod steps.

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
| Same corpus, larger 256/6 model, first pass | 2,000 | 3.62 / ppl 37.4 | 3.69 / ppl 39.9 | 57 min |
| Same corpus, 256/6 continued from 2,000 | 4,000 | 3.32 / ppl 27.6 | 3.38 / ppl 29.4 | +51 min |

The larger corpus reduced both validation loss and the validation-to-exam gap.
Extending the same run continued to improve it, but the gain from 5,000 to
10,000 steps was smaller than the gain from more data. The 256/6 model caught
up to the 128/4 at 10,000 steps, but it runs about three times slower on this
CPU. On this hardware the 128/4 is the better value; a larger model needs much
more compute to justify itself.

Example generation from the 10,000-step model, prompted with
`Once upon a time, there was a little`:

> named Lily. She loved to play outside in the park with her toys. One day,
> Lily's mom asked her to help her mom if she could play with her. Lily asked
> her mom if she

This is a smoke-test result, not a quality benchmark: the model is a 4.3 MB
128/4 model, not a production model. It is enough to prove the merge, JSONL
source, tokenizer, EOS boundaries, checkpoints, curves, export, checkpoint
continuation and exam paths.

The 256/6 4,000-step model generated:

> named Lily. She loved to play outside in the park with her friends. One day,
> Lily's mom asked her to help her mom if she could go to the park. Lily was so
> excited to

It is comparable to the 128/4 model on the exam but much slower to train and
generate, so the 128/4 + 20 MB corpus is the current practical sweet spot on
this machine.

### Supervised fine-tuning

Tick **SFT** in the Improve panel and point at a chat JSONL file (or a
`*.jsonl` glob). Each line can be `messages`, `conversations` (ShareGPT),
`prompt`/`response` or `instruction`/`output`; the layout is auto-detected. Use
**Preview data** to see valid/skipped records, detected layouts, the
train/validation/exam counts, token lengths and a rendered sample before
starting. The base model's embedded chat template is used by default, with a
formatter override for data in another skeleton. Loss is computed only on
assistant tokens, and the held-out exam uses the same conversation slice and
mask.

A short smoke on the 128/4 TinyStories base (20 synthetic conversations, 5
steps, rank 4) trained 8 adapters, merged a 4.3 MB `.smm`, and scored the 25%
held-out exam at loss 4.34 / ppl 76.5 on 97 assistant tokens. That run proves
the SFT data, batching, validation, export and exam paths; it is not a quality
benchmark.

The optional **SharpMind.GPU** plugin was also probed. ILGPU sees the Vega 3
through OpenCL (`gfx902`), but the GPU training engine is **LoRA-only**: it
refuses full pretraining because `EmbeddingTable.weight` is not trainable in
M1. These pretraining runs therefore use the CPU engine. A GPU LoRA fine-tune
is the only GPU training path worth testing on this hardware, and the
integrated Vega 3 is likely slower than the existing CPU path.

The recipe cache now keeps the cleaned corpus as
`Training/recipes/data/<hash>.jsonl`. A run that changes only the tokenizer or
training settings reuses that data phase; changing the source files or stages
invalidates it. Tokenizer, checkpoint and package phases follow the same
content-addressed pattern.

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
