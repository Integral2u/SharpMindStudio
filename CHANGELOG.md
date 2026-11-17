# Changelog

All notable changes to SharpMind Studio are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.1.2.0] - 2026-11-15

### Added

- **Tune from Tests** - every run row on the Tests page has a **Tune** button,
  and each family or model group has **Tune all**. A run with a tool
  probe tunes Work; the rest tune Chat. Progress and cancellation are shown in
  the filter panel.
- **Sortable Tests tables** - each group on the Tests page has the same
  sortable column headers as the dashboard: run, subject, status, tool,
  tok/s, steps and when.
- **Model actions** - the Settings model panel and a run detail now offer
  **Tune**, **Chat** and **Work** for the model in view. Tune runs the measured
  presets and saves them; Chat and Work start a session with the tuned
  Balanced profile.
- **Checkpoints and exports in Chat** - the run panel lists the finished model
  and the newest checkpoints. **Add to Models** copies a `.smm` into the Models
  folder, **Chat** opens a chat with it, and **Send to host** uploads it to a
  Compute host and activates it there. The Models tab offers the same for files
  in the Training folder. Fine-tune checkpoints are adapters only, so the panel
  says to export a merged model instead.
- **Training form follows the run** - coming back to Training restores the
  active run's inputs, or the last Improve draft, instead of showing defaults.
  The run panel also explains a missing validation line: either no split was
  set or the split produced no held-out documents, and the run log says which.
- **Random seed** - the job editor has a **Randomize** button next to **Seed**
  for a fresh split, batching order and weight initialisation.
- **Remote provider profiles** - OpenRouter and EuroRouter keep separate API
  keys, base URLs and model lists. The existing `openrouter.json` layout is
  migrated into the OpenRouter profile on load, and each chat document records
  which remote profile it uses.
- **EuroRouter (beta)** - a second integrated router with
  `https://api.eurouter.ai`, an `eur_...` key and its own model picker. Fetch
  models works before a key is saved; **Test key** validates the saved key with
  `GET /api/v1/key` without spending credit and reports the remaining balance.
  Responses carry EUR cost, and a 402 shows a clear balance message.
- **OpenCode Go** - the third integrated router, using the OpenAI-compatible
  endpoint at `https://opencode.ai/inference/go/openai/v1` and a Console API
  key. Fetch models reads the Go catalogue, including free models, and
  **Test key** checks the catalogue because Go has no balance endpoint. Studio
  sends `x-opencode-session` per chat for routing and prompt cache, and the
  model list understands the gateway's `limit.context`, `capabilities.tools`
  and per-million `cost` shape.
- **Agent names follow the CUI** - the assistant is named from the sampling
  temperature using the same Greek tiers as the old CUI: `Alpha` through
  `Epsilon`, with `Delta` when no temperature is set. The name is shown above
  each reply instead of the generic "Model", and temporary sub-agents keep the
  `{Deity}-{Tier}` form such as `Athena-Alpha`.
- **New chat starts clean** - **New chat** now closes the open document and
  returns to `/chat` or `/work`, so the session links in the sidebar work
  immediately. Work moves the button next to **New project**, and the sidebar
  shows the open project above its chat and work lists. A new session keeps the
  provider and model used last instead of falling back to OpenRouter's default.
- **Composer layout** - **Attach** sits beside **Send** on the new-chat screen
  instead of below a full-height send button. Agent-question choices are a
  vertical list, so long options no longer wrap into an uneven row.
- **Switch models in an open chat** - the chat header has a **Model** dropdown
  that switches to another local model, another model on the same remote
  provider, or another model on the same Compute host. The transcript stays;
  a local switch loads the target weights on the next turn and applies that
  model's tuned profile for Chat or Work.
- **Currency on costs** - chat costs and key balances now show the ISO currency
  returned by the provider (USD, EUR, ...) instead of a bare symbol.
- **Promotion bootstrap** - the exam scores each held-out document and stores
  its loss, token and byte counts. A promotion verdict now uses a paired 95%
  bootstrap over at least 100 shared documents and 10,000 tokens; a change is
  called better or worse only when the interval clears zero and the 0.5%
  practical threshold. Aggregate-only older runs keep the previous threshold and
  the report says the result was not bootstrapped.
- **Group-aware text splits** - Data Factory text JSONL uses its `source` and
  `id` fields as the split key, so every variant of one document stays in
  train, validation or exam. Splits are applied at the source before cleaning;
  text runs with held-out splits skip the packed-data cache so the group key is
  not lost. Plain text sources keep the content hash, and incremental runs keep
  the text-hash fallback because their sources are already reduced to deltas.
- **Vultr deploy and teardown from the API** - save a Vultr personal access
  token in Settings → Providers → Vultr, pick a region, plan, Ubuntu 24.04 and
  an SSH key that already exists on the account, then press **Deploy instance**.
  Studio creates a firewall with TCP 22, 5117 and 8080, labels and tags the
  instance `sharpmind-<short-id>`, waits for the public IP, and runs the same SSH
  setup as a manual host. It never uploads an SSH key.
- **Compute page** - a new left-menu item with a running count, the instance
  list, IP, uptime, hourly rate and estimated spend, per-row **Destroy**, and
  **Destroy all running**. It also hosts the Vultr deploy form. **Create
  compute** on Training jumps there with a GPU plan preselected.
- **Compute in the sidebar** - the Compute menu item expands like Chat and Work
  and lists every Vultr instance with its state and hourly rate, plus a
  **Destroy** button per row. Instances in active use show a pulsing dot next to
  the menu item, an "in use" note in the list and on the Compute table; use
  means training, rehearsal or exam on that host, or a chat/work turn loading or
  streaming from it. Destroying an in-use instance asks for a second,
  explicit confirmation. A stopped instance stays listed because Vultr bills it
  until it is destroyed.
- **Exit guard** - closing Studio with instances still running shows a prompt
  with **Keep running** as the default, **Cancel**, and **Destroy and exit**.
  Tagged instances that Studio is not tracking are detected at startup, listed
  on Compute, and included in the teardown.
- **Validated end to end** - the Vultr API deploy, SSH setup, training path and
  the new checkpoint/model-list flow were exercised on a live instance, and the
  walkthrough was recorded.
- **Local GPU accelerator** - the source build copies the optional
  `SharpMind.GPU` plugin into `plugins/ilgpu` with ILGPU, so the training
  Accelerator list and local chat can use CUDA/OpenCL. CPU stays the fallback,
  and a packaged install needs the plugin folder shipped beside the app.
- **Context compaction** - the inference profile has a **Context compactor**
  option. **Summarize** is the default: at 70% of the context the active model
  summarizes older turns, marks them ignored and pins the summary. Repeated
  compactions fold into one rolling summary. **Truncate**, **None** and plugin
  compactors by name are also listed. System prompts, pinned messages and the
  current turn are never summarized.
- **Idle teardown** - each Vultr host chooses **15**, **30** or **60** minutes,
  or **Never**. Training, rehearsal, exam, a busy chat/Work turn and a deploy,
  SSH setup or model upload reset the timer. The host's job list is also checked
  every 30 seconds, so a job that outlived a Studio restart keeps the instance
  alive. After the window a 10-minute **Keep** countdown runs, then the instance
  and its disk are destroyed. Only instances this Studio created are eligible:
  tagged untracked instances are shown but never destroyed, including by
  **Destroy all tracked** and the exit prompt.

### Changed

- **Tests page** - family and model groups collapse, the redundant Type column
  and per-row kind label are gone, and the separate Run type filter and grouping
  are gone. A tool probe folds into its benchmark, so the Tool column carries
  Yes/No/N/A. The group Tune/Re-run buttons no longer wrap, and tuning shows the
  loading ring while it runs.
- **One row per run** - a tool probe that a benchmark links to is folded into
  its model-bench row in TESTS and the dashboard list. The Tool column already
  reports Yes/No/N/A, so the probe no longer appears as a second line; its
  detail page stays reachable from that column.
- **Sidebar** - removed the non-interactive local-first pill.
- **Chat model switcher** - the header's **Model** control is a dropdown that
  switches immediately, and the composer no longer names LFM3-VL in its
  attachment hint, so other vision models read the same way.
- **CPU tier options** - the inference profile reads its choices from the
  `HardwareTier` enum, so Arm64 and future tiers appear without a UI change.
- **Agent prompts** - the parent prompt now explains `delegate`/`spawn_agent`,
  lists the saved agents and says the specialist cannot see the chat and must be
  given the full task. A delegated agent is told to return a self-contained
  answer and not ask the user questions.
- **Agent tool calls** - a test now drives `delegate` and `spawn_agent` through
  the parent builder to the Studio executor and checks the returned result. If a
  model prints a tool call instead of running it, the chat says so and points at
  Tool-call recovery; on a Compute host it says the host is inference-only.
- **Containers run unprivileged** - the images create a `sharpmind` user
  (UID 10001); the entrypoint fixes data and plugin folder ownership, then
  drops root before starting Studio or the Compute host. Port exposure and
  token behaviour are unchanged.
- Version bumped to **1.1.2.0** for the next development cycle.
- Source development continues to use the sibling SharpMind project
  references. The package solution keeps the published **1.1.1** packages as
  its compatibility baseline until the 1.1.2 engine packages are published.

### Fixed

- **Vultr settings panel did not load plans** - Settings passed the literal
  text `provider.Id` instead of the host id, so the panel never found the host
  and **Load plans** could not save the key. The Compute page's deploy panel
  had the same mistake, which also defeated the Training plan preselection.
  The panel now also reads the key as it is typed, uses the pasted key for the
  account, plan, region, OS and SSH-key calls, and only stores it after Vultr
  accepts it; a rejected key stays in the field with the Vultr error shown.
- **Deploy no longer fails while SSH is starting** - a fresh Vultr instance can
  report `active running` before `sshd` accepts connections, and the first
  refused connection used to fail the whole deploy. Deploy now waits up to five
  minutes for TCP 22, reports each attempt, then runs the setup. The SSH error
  text also notes that a new instance can refuse connections for a minute.
- **SSH setup on Windows** - remote scripts kept the source file's CRLF line
  endings, so bash read the carriage return as part of each command and the
  probe failed with `set: -` or a syntax error before setup could start. Scripts
  are now sent to the host with LF endings.
- **JSONL sources kept a blank text source** - a new job starts with one empty
  text source as a convenience; adding a JSONL or other source now replaces it,
  so the "A text source has no path" warning does not stick.
- **Compute count without opening the page** - the sidebar and billing banner
  refresh every 30 seconds, so an instance created in the Vultr console is
  noticed without visiting the Compute page.
- **JSONL files were read as raw text** - adding a `.jsonl` through the text
  corpus box created a text source, so the model learned the JSON wrapper and
  `\u0022` escapes. The quick add now uses the JSONL source for `.jsonl` paths,
  and an older job with that mistake shows a warning.
- **Model lists refresh when opened** - Chat and the labs rescan the Models
  folder on entry, so a model added from Training or the run panel appears
  without restarting Studio.
- **Model list pauses and missing `.smm` files** - the catalog is built once at
  startup and reused, scans are serialised, and Chat and the labs no longer
  rescan on every open. Adding a model from Training, a HuggingFace download,
  a conversion or a run that exports into Models updates the cache. Settings
  → System has a **Refresh list** button for changes made outside Studio.
  The Model visualizer and Latent space now list `.smm` models; Quantization
  remains GGUF-only and says so.
- **Compute sidebar spinner** - the 30-second background refresh no longer
  flashes the loading ring next to Compute; the count badge updates in place.

## [1.1.1.0] - 2026-11-08

### Added

- **Published package build** - the Studio package solution now restores the
  published SharpMind 1.1.1 packages from nuget.org and compiles the same
  audio/labs code as the source solution. CI builds the package solution
  directly; no sibling checkout or local package feed is required. Source
  development keeps project references to the sibling engine checkout.
- **Provider copy** - Chat now says "local, OpenRouter or Compute"; Work still
  says local or OpenRouter because Compute hosts are inference-only. The wiki
  provider table compares all three.
- **Remote hosts and routers** - Vast.ai, MaxCloudOn and Novita appear as manual
  remote-host presets in Providers > Compute, OpenRouter is listed as integrated,
  and the Vast.ai, EuroRouter and Novita sign-up links are available. MaxCloudOn
  code **SharpMindStudio** takes 20% off. The provider page groups hosts and
  routers with their integration status; EuroRouter still needs a generic
  OpenAI-compatible provider integration.
- **Wiki navigation** - Labs is back in the deployed wiki navigation and the
  in-app link now uses the real `wiki/labs.html` path. A new Settings guide joins
  the wiki index, and Chat, Work, Training, Labs and Settings each link to their
  guide. The site sidebar links to the wiki index everywhere.
- **Plugin component host** - Settings > System now lists and reloads every plugin
  type Studio can host, not just tool classes: context compactors, prompt
  pre/post-processors, generator/cache builders and accelerator plugins. A plugin
  pre/post-processor is composed with Studio's artifact extraction, and a selected
  plugin compactor applies to new chat and agent sessions.
- **Remembered model** - the Labs model choosers (Layer importance, Quantization,
  Model visualizer and Latent space) open on the last model used anywhere in
  Studio instead of resetting to the first catalog entry.
- **Agent definitions** - the Agents tab defines specialists (name, purpose, rules,
  inherit current or named model, temperature, seed, TTL, enabled, max delegation
  depth, allowed tools and file/web Ask/Allow/Deny permissions). Enabled agents are
  exposed to chat and work as the `delegate` tool; the model can also call
  `spawn_agent` to create a temporary specialist from task-supplied name, rules,
  model and tools. Studio runs both through the agent runner, so a same-model agent
  borrows the current weights and a named model is loaded for one task and cached
  until its TTL. Agent file/web permissions default to Ask and never change the
  project policy.
- **Chat history search** - the Chat and Work lists in the sidebar have a search
  box that matches a session name or any message body, and the preview shows the
  matching line. Results stay split by chat/work.
- **Chat artifacts** - attach images, audio, text and other files from the
  composer; render returned images, audio, text/code and download links.
  Generated data-URL media and fenced code blocks are captured as artifacts.
- **Audio-only turns** - attaching a WAV now enables Send without typed text.
  The first turn loads the audio backbone on demand, transcribes it, and keeps
  the attachment on the user message. The attachment control is also available
  on the new Chat/Work composer, not only inside an open chat. Remote chats
  still reject attachments.
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
  definitions; the Labs page links to it. The providers page lists remote hosts
  and model routers.
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
  each layer as full/sliding attention, linear or recurrent. An architecture-template
  editor beside it builds a model shape without a capture: hidden/FFN width, depth,
  heads, KV heads, context and a per-layer full/sliding plan with add, insert and
  remove. It can be seeded from the selected model and sent to Training > New model;
  the same plan can be edited there and trains with per-layer sliding windows. The
  visualizer and latent tabs show loading status and an Interrupt button for both
  weight loading and the forward pass. Architecture templates can be opened and
  saved as JSON, and the New model editor has the same Open template action beside
  Tiny/Small/Medium. Seeded Gemma-3 models resolve their stored sliding pattern,
  and the layer plan can show Full, Sliding, Linear or Recurrent. The latent
  space tab can import a prompt list (TXT, JSON or JSONL) and export projected
  tokens as JSONL. The Latent space tab projects
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

- Chat attachments are local-only; OpenRouter and Compute turns still receive
  text only.
- LFM3 text chat is validated with templated smoke runs; broader quality
  evaluation is still pending.
- LFM3-VL image input and LFM3-Audio ASR/TTS require the matching `mmproj`
  projector next to the backbone. Nemotron `asr` uses a different
  encoder-decoder frontend and is not supported.
- LFM3-VL understands images; it does not generate them. Studio has no
  text-to-image/diffusion runtime.
- Agent model selection is one model or inherit-current. Prioritized fallback
  model lists and delegation from a remote parent chat are not implemented.
- GPU training is LoRA-only; from-scratch pretraining and MoE training are CPU
  paths.

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
