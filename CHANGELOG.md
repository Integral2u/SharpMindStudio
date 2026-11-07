# Changelog

All notable changes to SharpMind Studio are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.1.1.0] - 2026-11-08

### Added

- **Chat artifacts** - attach images, audio, text and other files from the
  composer; render returned images, audio, text/code and download links.
  Generated data-URL media and fenced code blocks are captured as artifacts.
- **LFM3 text support** - tri-hybrid layer metadata (13 short-conv, 5
  sliding-window, 6 global layers) and the per-head `attn_gate` path.
- **Training experiment controls** - configurable seed across splits,
  batching and weight init; bits-per-byte exam and promotion metric;
  tokens-per-parameter advisory.
- **Data Factory durability** - retry/backoff, `.partial` output with
  checkpoint/resume, variant identity, provider/model provenance and a
  terms notice.

### Changed

- Extracted OpenRouter and Compute chat turns behind `IChatBackend`.
- Split `Settings.razor` / `Training.razor` markup and code, and extracted
  the appearance, OpenRouter, HuggingFace, registry and saved-jobs panels.
- Split `TrainingRunner.RunAsync` into input, tokenizer, data, resume,
  model, train-loop and export stages.
- Version bumped to **1.1.1.0**. The Studio source still references the
  published **SharpMind 1.1.0** packages for clean-clone builds.

### Fixed

- `FetchUrl` SSRF hardening, DPAPI-wrapped secret keys and the cross-site
  Fetch Metadata guard.
- Data Factory variants now share a split group through `GroupId`.
- Remaining mojibake in comments, logs and UI strings.

### Known limitations

- Chat attachments are local-only; remote providers do not receive content
  parts yet, and the local engine does not encode image/audio tensors.
- LFM3 text chat is validated with a templated `2+2` smoke run; broader
  quality evaluation is still pending.
- ASR/TTS is not enabled: the main LFM3-Audio GGUF is a text backbone plus
  ASR/TTS templates. The audio encoder and codec live in the separate
  `mmproj` file, which the engine does not load yet. The Nemotron `asr`
  architecture needs an encoder-decoder audio frontend.

## [1.1.0.0]

First public repository release.

### Added

- **Training lab** — the UI-free corpus-to-export engine: job editor, model
  presets, tokenizer and formatter handling, QAT, incremental data sources,
  checkpoints and `.smm` export.
- **Format conversion** — `.smm` ↔ GGUF both ways, with read-back verification.
- **Live curves and validation** — deterministic train/validation split, fixed
  evaluation batches and a train/validation loss chart.
- **Run history** — per-project run records with comparison, delete and re-run.
- **Held-out exam and promotion** — a deterministic test slice, exam scoring,
  and promotion reports.
- **LoRA fine-tuning** — merged `.smm` and adapter-only `.smlora` output,
  adapter/checkpoint resume, and a real-model smoke on SmolLM2-135M.
- **Supervised fine-tuning (SFT)** — chat JSONL in `messages`, `conversations`,
  `prompt/response` or `instruction/output` shape, assistant-only loss masks,
  the base model's chat template (or a formatter override), and the same
  validation/exam/recipe-cache path as the other training modes.
- **Planner** — six-question intake, three plans (Fast/Balanced/Ambitious),
  heuristic estimates, and a ~30-step rehearsal.
- **Versioned rules** — planner rules as versioned JSON under `Training/rules`.
- **Content-addressed recipe cache** — data, tokenizer and full-train hashes
  under `Training/recipes`.
- **Fork from run** — clone a finished job and continue from its checkpoint.
- **Work mode** — per-project tools grouped by category, context locations with
  read/write/delete permissions, skills with filter/preview and project
  overrides, web permission, and send gating when the tool probe failed.
- **Phone pairing** — in-process LAN listener, QR/PIN pairing with hashed
  device and pairing tokens, salted slow PINs, AES-GCM secrets, trusted devices,
  one-time login QR, and a server-enforced mobile scope.
- **Layered inference settings** — System → Family → Model → Session, with each
  value showing the layer that supplied it.
- **OpenRouter** — remote Chat and Work models alongside local weights, with an
  encrypted API key under Settings, fixed app attribution, an active-model list
  with free-model helpers, streaming chat completions, OpenAI-style tool calls,
  and the local project tools/permissions still enforced for Work. Remote models
  are inference-only; local tuning is unavailable for them.
- **Data Factory** — a saved pipeline under **Training > Data**: source files or
  JSONL, cleaning and filters, a local or OpenRouter provider transform, preview
  and sample review, then chat or text JSONL export ready for SFT or pretraining.
- **HuggingFace model browser** — search the Hub for GGUF repositories, inspect
  license/gated status/files/quantisation/size, download to `Models` with resume,
  and store an encrypted token for gated repositories.
- **Compute hosts** — one model per process behind an OpenAI-style API, with
  Local Docker start/stop, Runpod pod deploy/terminate, and SSH setup for Vultr
  or custom Linux hosts (Docker and NVIDIA container toolkit install, image
  pull, container run, logs and stop). Chat and Training route to the host;
  models and datasets upload with progress; the GPU plugin accelerates
  inference and LoRA fine-tuning and falls back to the CPU with a loud log.

### Changed

- References the published **SharpMind 1.1.0** NuGet packages by default.
  The local project-reference block is kept but commented out and can be
  re-enabled with `-p:SharpMindSource=project`.
- **Tool-call recovery** (`Strict` / `Lenient` / `Loose`) is a layered inference
  option and part of tuning. Chat defaults to `Strict`; Work defaults to
  `Lenient`, or `Loose` for the Accuracy preset. Work tool probes run with the
  tuned mode.

### Notes

- Gateway proxy mode is intentionally left as built.
- Finer-grained per-phase Recipe nodes and a full-scale training run remain
  future work.
