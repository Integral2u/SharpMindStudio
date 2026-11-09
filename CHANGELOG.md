# Changelog

All notable changes to SharpMind Studio are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.1.1.0] - 2026-11-08

### Added

- **Agent definitions** - the Agents tab defines specialists (name, purpose, rules,
  current or named model, temperature, seed, TTL, enabled, max delegation depth).
  Enabled agents are exposed to chat and work as the engine's `delegate` tool;
  same-model agents use the engine's sub-agent path, while an agent with its own
  model is loaded through the model host for one task and cached until its TTL.
- **Chat history search** - the Chat and Work lists in the sidebar have a search
  box that matches a session name or any message body, and the preview shows the
  matching line. Results stay split by chat/work.
- **Chat artifacts** - attach images, audio, text and other files from the
  composer; render returned images, audio, text/code and download links.
  Generated data-URL media and fenced code blocks are captured as artifacts.
- **Local microphone dictation** - when an LFM3-Audio model and its `mmproj`
  are present, the composer shows a Mic button. Recording returns a WAV that
  the native encoder transcribes into the composer; attached audio files are
  transcribed before the turn. Recordings longer than four seconds are chunked.
- **Prompt lookup drafting** - the inference settings expose
  `PromptLookupDraft` (0 = off) for faster copy-heavy replies with unchanged
  output.
- **Speech models** - an LFM3-Audio model treats a text prompt as a TTS
  request: the words are synthesized into the assistant's 24 kHz WAV artifact
  automatically, with no Speak button and no text reply; an attached audio file
  is transcribed and the transcript becomes the assistant reply. The chat's
  session options expose the voice
  (female/male), language, maximum frames, temperature, top-k and seed, so the
  same prompt can be re-synthesized with different prosody. The audio card shows
  its duration, and the WAV is saved with the chat. A punctuation-only model
  reply (the LFM3-Audio backbone's bare ":") is treated as no speech: the turn
  is hidden from the transcript.
- **Wiki and in-app guide** - `docs/wiki/labs.html` explains each lab, the layer
  delta colours, manual surgery, the same-model quantization check and agent
  definitions; the Labs page links to it. The providers page lists the GPU and
  inference partner programs.
- **Labs menu** - a top-level Labs page with tabs for Layer importance,
  Quantization, Model visualizer, Latent space and Agents, so more
  model-analysis labs can be added without new navigation items. The Layer
  importance tab ablates each transformer block of any loaded model, shows the
  attention/feed-forward activation RMS and the next-token loss delta, and
  highlights the least important layer as a surgery candidate. The same run
  produces a surgery preview: skip the least important layers in rank order and
  show the loss after each prefix, with the low-risk rows highlighted. Each row
  can export a pruned GGUF with those blocks removed and reports the output path
  and size. The table has a legend for the teal/yellow delta bars, and a Manual
  surgery input exports any removable layers in the 1..N-2 range (the first and
  last blocks stay). The
  Quantization tab compares a reference GGUF with a candidate tensor by tensor
  (relative L2, cosine, max abs) without a forward pass, flags whether the two
  files are the same model (same architecture, tensor names and shapes) before
  the forward-loss comparison, and can write a smaller GGUF itself: a fixed
  Q2_K–Q8_K/Q8_0/Q4_0/F16 target, a per-role blend, or a byte budget that picks
  the coarsest fitting K-quant; K-quant rows are row-padded, so 896-wide models
  such as Qwen2-0.5B run the full quantization. Then optionally loads both models
  in turn and measures the calibration cross-entropy and perplexity.
  The Model visualizer tab
  captures one prompt through the activation hook and draws attention/FFN RMS
  heatmaps over layers and tokens on a log scale; a right-hand column labels
  each layer as full/sliding attention, linear or recurrent. The Latent space tab projects
  the prompt's final hidden states to two dimensions with classical MDS, labels
  each point with its decoded token and draws a hoverable scatter with the
  explained variance; an Aggregate option keeps earlier prompts in the plot
  instead of clearing it, with one colour per prompt. The Agents tab defines
  specialist agents and lists the work agent's tools and skills with their
  enabled state and the current transcript as a turn-by-turn trace of agents,
  tool results and artifacts; it needs no model load. Source builds until the
  1.1.1 packages publish.
- **LFM3-VL image attachments** - when a vision projector is present next to
  the model, attached images are decoded and described natively. An image-only
  send uses "Describe this image."; the answer joins the transcript and the next
  text turn rebuilds the session so the vision turn stays in context. The chat
  session options set the image's longest side (default 512 px) and the answer
  token cap (default 128).
- **Artifact cards** - images open or download, audio plays and downloads,
  text/code downloads, with the artifact type and size in the header.
- **Larger sponsor pill** in the top bar.
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
  published **SharpMind 1.1.0** packages for clean-clone builds. The microphone
  and native ASR UI compile only in source-tree mode until the 1.1.1 packages
  are published.

### Fixed

- LFM3 per-head `attn_gate` loads in streaming and quantized-resident modes,
  so LFM3-Audio chats no longer fail with "RawQuantizedData is null".
- An `asr` encoder-decoder model (Nemotron ASR) or a `mmproj` file selected as a
  chat model now reports what it is instead of a generic load failure.
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
