# SharpMind Studio - Design

> **Status:** implementation in progress, October 2026. Studio **1.1.0.0**.
> Built: shell, projects/runs/recipes, canary and tool probes, TESTS run history, Chat lab, layered inference settings, Work mode MVP, model Tune presets, shared skills/tools, the phone pairing gateway, the corpus-to-export Training engine with its lab shell, LoRA fine-tuning, and SMM↔GGUF conversion.
> Remaining: the planner's intake/estimator/rehearsal depth, Work hardening (richer skills and tool management), and gateway proxy mode.
SharpMind Studio is a local-first application for chatting with models, doing project work with them, and building them: from scratch, from a base model, or by upgrading a model you already have. It is the successor to the terminal UI (CUI) as the primary front end, and it is meant to be something people want to use: attractive, calm and fast.

---

## 1. Principles

1. **One engine, three depths.** A novice, a practitioner and an expert all drive the same engine. Easy mode makes suggestions with reasons. Expert mode exposes every knob. Anything the GUI does is a saved, diffable, re-runnable **Recipe**.
2. **Every suggestion explains itself.** No magic defaults. Each recommendation carries a reason, its evidence and the alternatives it rejected.
3. **Honest about cost.** Time, memory and file-size estimates are shown before a run starts, and a short rehearsal run replaces guesses with measurements.
4. **Local-first.** Everything runs on the user's machine. No telemetry. Network access (model hub, remote teachers) is explicit and visible.
5. **Lineage is a feature.** Every artifact knows what produced it: base model, data, recipe, tokenizer.
6. **A clean break from the CUI.** Studio does not reference the CUI. The shared surface is the SharpMind libraries and the file formats, nothing else.

---

## 2. Modes

| Mode | For | What it is |
|---|---|---|
| **Chat** | Everyone | Pick a model, talk to it. Saved sessions, system prompt and tools drawer. Models carry a "runs well / runs slowly / won't fit" badge computed from the machine. |
| **Work** | Practitioners | Project spaces. Each project is a folder with its own system prompt, skills, tool permissions, memory, and `runs/` and `models/` directories. |
| **Training** | Builders | The lab for creating and improving models. Sections: **New model**, **Improve**, **Playbooks**, **Data**, **Runs**, **Models**. |

"Expert" is a per-field toggle, not a mode.

### Work mode sandboxing

- Tools are jailed to the project root, with per-project `Never / Ask / Always` permission profiles.
- Path jailing is not isolation for untrusted code. A container sandbox is deliberately out of scope for 1.1; the boundary is the configured project folders, not the operating system.

---

## 3. Architecture

```mermaid
flowchart TB
  Shell["Shell (optional WPF / WebView2)"] --> UI["Studio UI (Razor / Blazor Server)"]
  UI --> Core["Studio.Core: jobs, events, recipes, advisor"]
  CLI["studio CLI (headless)"] --> Core
  Core --> Lib["SharpMind libraries: Training, Data, Model, Inference, Tokenization, GPU"]
```

### Repository layout (separate repo)

```
SharpMind.Studio/
  global.json                  pin the .NET 10 SDK
  Directory.Build.props        own version (1.1.0.0); IsPackable=false by default
  Directory.Packages.props     central package versions
  SharpMind.References.props   project/package switch (below)
  nuget.config                 local feed + nuget.org
  src/SharpMind.Studio.Core    UI-free: job service, events, recipes, hardware advisor
  src/SharpMind.Studio         Razor UI + Kestrel host
  src/SharpMind.Studio.Shell   optional WPF/WebView2 window (Windows only)
  tests/SharpMind.Studio.Tests
```

- **UI:** Razor components on Blazor Server, hosted by a local Kestrel service. It opens in a browser, or inside the optional shell via `BlazorWebView`. The same UI therefore works locally and against a remote training box.
- **Shell** is a separate project so Core and the UI build and test on Linux CI.
- **Studio.Core stays UI-free** so runs work headless (`studio train recipe.smmtx`), are testable without a browser, and can be driven by any future client.
- **Security defaults:** the service binds to `127.0.0.1`. Remote access is an explicit opt-in with a token. Hub tokens are stored in the OS credential store, never in recipes.

### Referencing the library

Studio develops against the library in **project** mode and ships against it in **package** mode. A switch makes this a build flag instead of an end-of-project migration:

```xml
<!-- SharpMind.References.props -->
<Project>
  <PropertyGroup>
    <SharpMindSource  Condition="'$(SharpMindSource)'==''">project</SharpMindSource>  <!-- project | package -->
    <SharpMindRoot    Condition="'$(SharpMindRoot)'==''">$(MSBuildThisFileDirectory)..\SharpMind\</SharpMindRoot>
    <SharpMindVersion Condition="'$(SharpMindVersion)'==''">1.0.9.0-dev.1</SharpMindVersion>
  </PropertyGroup>
  <ItemGroup Condition="'$(SharpMindSource)'=='project'">
    <ProjectReference Include="$(SharpMindRoot)SharpMind.Training\SharpMind.Training.csproj" />
    <ProjectReference Include="$(SharpMindRoot)SharpMind.Data\SharpMind.Data.csproj" />
    <ProjectReference Include="$(SharpMindRoot)SharpMind.Model\SharpMind.Model.csproj" />
  </ItemGroup>
  <ItemGroup Condition="'$(SharpMindSource)'=='package'">
    <PackageReference Include="SharpMind.Training" Version="$(SharpMindVersion)" />
    <PackageReference Include="SharpMind.Data" Version="$(SharpMindVersion)" />
    <PackageReference Include="SharpMind.Model" Version="$(SharpMindVersion)" />
  </ItemGroup>
</Project>
```

Working rules:

- `SharpMindRoot` points at a **dedicated checkout** (a `git worktree` of the library's 1.0.9.0 branch) that is updated deliberately, never the live working copy.
- Library changes Studio needs land **first**, additive and tested, then get packed as a preview, then Studio bumps.
- **Public API only.** If Studio needs an `internal`, that is a signal for a public API, not for `InternalsVisibleTo`.
- CI builds Studio twice (project mode against library `main`, package mode against the last preview) so breaking library changes surface early.
- Check which library packages are actually published on nuget.org. Until they are, package mode uses a local feed.

---

## 4. Core concepts

### 4.1 Project

A folder: `project.json`, `data/`, `runs/`, `models/`, `evals/`, plus Work-mode settings. Paths come from one place in Studio.Core.

### 4.2 Recipe (`.smmtx`)

`.smmtx` replaces `.smmt`. A Recipe is a **step graph**, not a flat settings bag.

- `schemaVersion` and `$schema` from day one, with migrations.
- Components referenced by **stable IDs**, never assembly-qualified names (the cause of the `.smmt` version-bump breakage fixed in 1.0.7.0).
- A one-way, non-destructive `.smmt â†’ .smmtx` migrator that writes a sibling file. The `.smmt` reader is kept for several versions.
- Steps are **content-addressed**: a step's identity is a hash of its type, parameters and input hashes.

Content addressing gives, for free: "run from here" and cheap experiments (upstream is cached), stale detection when data changes, lineage, playbooks as partial graphs, and branch-and-compare that share identical upstream steps. The library's existing `IncrementalPlanner` already does file-level delta detection, so this generalises something that exists.

Illustrative sketch (not final):

```json
{
  "schema": "smmtx/1",
  "name": "kiosk-assistant-v2.1",
  "playbook": { "id": "edge-standard", "version": 3, "overrides": ["steps.tune.rank", "steps.mix.replay"] },
  "deviceProfile": "kiosk-4gb-arm",
  "steps": [
    { "id": "data",  "type": "ingest",   "params": { "sources": ["client-docs/"] } },
    { "id": "clean", "type": "triage",   "needs": ["data"],  "params": { "dedupe": "near", "pii": "flag" } },
    { "id": "mix",   "type": "mix",      "needs": ["clean"], "params": { "replay": 0.2 } },
    { "id": "tune",  "type": "lora",     "needs": ["mix"],   "base": "models/v2#master", "params": { "rank": 16, "alpha": 32 } },
    { "id": "eval",  "type": "evaluate", "needs": ["tune"] },
    { "id": "pack",  "type": "quantize", "needs": ["tune"],  "params": { "targets": ["Q4_K", "Q8_0"] } }
  ]
}
```

### 4.3 Playbooks

A playbook is a versioned partial Recipe with **locked** fields (base, tokenizer, formatter, quant family) and **open** fields (data, steps, device). Tweaks are stored as a diff over the playbook ("EdgeStandard v3 + 3 overrides"), so a playbook update can merge cleanly.

### 4.4 Device Profile

What the model must run on: RAM, CPU/GPU, **storage read bandwidth and latency**, target tokens/s, maximum file size, context length. Storage matters because streaming MoE pays disk cost on expert cache misses. Studio measures it (a sequential-read benchmark) instead of asking.

### 4.5 Lineage and model versions

A **model version** is a float master (a checkpoint or F16 `.smm`) plus **derived quant builds** per device profile. Quantized builds are outputs and never training roots.

- If a user points at a quantized-only file, the wizard says so: fine-tuning works on the dequantised weights, but the quality ceiling is lower, so find or rebuild the master if possible.
- **Adopt** brings a pre-Studio `.smm` or GGUF in as a lineage root. Old `.smmt` jobs are imported through the migrator so the advisor knows what produced it.

### 4.6 Runs and the event stream

Every run emits one append-only stream, written to `runs/<id>/events.jsonl`. The training screen, history, replay and headless logs all derive from it.

```
RunStarted      { runId, recipeHash, baseModelHash?, device, startedUtc }
StepLogged      { step, loss, lr, gradNorm, tokensPerSec, tokensSeen, memBytes }
EvalCompleted   { step, valLoss, perplexity }
ExpertStats     { step, layer, counts[], routingEntropy }       // MoE only
CheckpointSaved { step, path, modelHash }
ProbeAnswered   { step, probeId, text }
RunFinished     { status, exportPath?, durationMs }
```

### 4.7 Layered settings

Settings resolve from **System -> Family -> Model -> Session**. Every downstream layer is sparse: `null` means inherit. The resolver returns both the effective value and the layer that supplied it, so the UI can show `0.6 (Qwen2 family)` rather than a bare number.

- System defaults are stored in `Studio\Studio\inference.json`.
- Families are detected from model metadata and are keyed by normalized architecture family.
- Models are keyed by full path, so moving a file deliberately resets its model-specific overrides.
- Session overrides live inside the chat document. Clearing them returns to the family/model/system chain.
- The same layering will later carry skills, tools and project settings.

### 4.8 Chat sessions

Chat stores one document per conversation under the project's `chats/` folder. Each document keeps the model path, family, messages, session overrides and a model fingerprint. The KV cache snapshot is stored separately and used on reopen when the model, formatter, generator and cache still match; otherwise the history is restored and the cache is rebuilt.

Chat output is plain text while streaming and sanitized Markdown when the turn completes. Raw HTML is disabled and links are restricted to http, https and mailto.

---

### 4.9 Work mode

The Work MVP is project-scoped:

- Projects live under `%USERPROFILE%\SharpMind\Workspaces`; the default is `Workspaces\Studio`.
- `work.json` holds project instructions, skill selections, web permission, disabled tools, and context locations with per-folder read, write and delete permissions.
- `memory.md` is plain Markdown. Skills are Markdown files in the shared repository; projects select which ones to use and can override the system default.
- Skills live in `%USERPROFILE%\SharpMind\Skills`; defaults are stored in `Studio\work-skills.json` and maintained under Settings > System. Projects pick from that repository, including skills copied in by hand.
- File tools list, read, search, write, append, create and delete across the configured contexts; a separate Web permission governs URL fetches. Tools run in Work only, never Chat.
- Tool defaults live in Settings > System; the catalog includes built-ins plus [ToolDesc] plugin tools discovered from the plugins folder. Projects override individual tools, and Chat never receives them.
- `Ask` posts a permission card in the Work chat with Deny, Allow once and Always allow. Always persists to `work.json`.
- Work chats are ordinary chat documents with `mode: "work"`, so history, KV snapshots and inference settings reuse the Chat spine.
- Work projects can be created and switched without restarting Studio; the current project is remembered.

### 4.10 Tune presets

Model Tune keeps separate Chat and Work tuning:

- Presets: **Performance**, **Balanced**, **Accuracy**.
- Tune builds all three presets in one measured run; each preset records generator/cache/load mode and benchmark results.
- New Chat and Work sessions show the three presets; they are disabled until the selected model and target have been tuned. A Tune/Re-tune button opens the modal panel.
- The chosen preset is snapshotted into the chat session; re-tuning affects new sessions.
- Work presets default to `StandardGeneratorBuilder`, but other and plugin generators can be selected; the measured Work run also checks tool calling.
- Work tuning first establishes or reuses the measured Chat baseline, derives Work presets from it, then tests tool calling and retries with Standard + low temperature if the probe fails.
- Temperature, Top-K, Top-P, Min-P, repetition, generator, KV cache, context, max-new-tokens and load mode are editable.

### 4.11 TESTS and run history

The canary dashboard lives at `/test`; the full history lives at `/runs`.

- `/test` keeps the canary controls, this run's results, and the 12 most recent runs. **Best decode** links straight to the fastest run, and **View all runs** opens `/runs`.
- `/runs` shows every run in the current project. It filters by search text, run type, status and tool status, and groups by **family**, **model** or **run type**.
- Runs are sorted fastest to slowest inside each group; runs without a measurement follow, newest first. Measured decode is the comparison, so tool-probe runs never win a speed award.
- Gold star: fastest successful `model-bench` run whose tool probe passed. Silver star: fastest successful `model-bench` run without a passing tool probe. Users can delete a run or re-run its model from either view.
- Run summaries are cached for three seconds and invalidated on delete, re-run completion, and project switch, so opening a page does not rescan 200 event streams per render.

### 4.12 Phone gateway

Studio hosts the phone experience itself; there is no separate gateway project.

- Two listeners in one process: loopback admin on `127.0.0.1:5117` and a LAN listener on `0.0.0.0:5118`. The LAN port is always bound; `GatewayAuthMiddleware` returns 404 for non-loopback requests while LAN pairing is off, so the toggle takes effect immediately with no restart. mDNS discovery is not used in 1.1.
- Pairing data is JSON under `Studio\Gateway`. Device tokens and pairing tokens are stored as hashes; PINs are salted and slow-hashed; upstream secrets are encrypted. A pairing expires after five minutes, is single-use, and locks after five bad PIN attempts.
- A phone scans the QR code, enters the PIN, and waits for approval. The PC approves **mobile** or **full** access, can change a device's mode, and can revoke it. Revoking invalidates the token and removes the row from the trusted list. Trusted devices sit directly below the QR code.
- Each trusted device also has a **Login QR** button. It issues a one-time, five-minute login token; scanning `/pair/device` swaps in a fresh device token, sets the cookie and opens Chat. Reusing or outliving the code redirects to the normal pairing page. The device QR has its own **Local / Global** switch, independent of the main pairing QR.
- Pairing admin lives in **Settings > Pair**, the last settings tab, so sidebar and back navigation stay available. The QR code is generated as a PNG data URL by QRCoder and appears as soon as the panel opens.
- The QR and URL follow a **Local / Global** choice. Global uses a user-supplied public address or DDNS name; **Find my public IP** asks `api.ipify.org` only when pressed. Global access needs a router port-forward to the LAN address.
- A **Check / Allow through Windows Firewall** action reads or creates the inbound TCP 5118 rule for the Private profile, raising one UAC prompt instead of requiring an administrator shell. The manual `New-NetFirewallRule` command is shown as a fallback.
- `/gateway` redirects to `/settings?tab=pair`; the standalone `/gateway/admin.html` page is kept for diagnostics.
- A paired phone receives an HttpOnly `sharpmind_device` cookie with signed claims. Mobile scope is Chat plus a bare-bones Work: prompt, interrupt, and view sessions and artifacts. Settings, tools, skills, context rules and project switching stay on the PC.
- Restrictions are enforced in Studio from the signed claims, including inside Blazor circuit actions, not only by hiding navigation.

### 4.13 Where data lives

Studio keeps settings separate from project data so a project can be moved or copied without carrying machine preferences.

```
%USERPROFILE%\SharpMind
  Models\ Corpus\ Training\ Chat Sessions\     shared with the CUI
  Skills\                                      shared Work skills repository
  Studio\                                      machine settings, not a project
    settings.json  inference.json  workspace.json
    work-skills.json  work-tools.json
    Gateway\                                   device/pairing state
  Workspaces\
    Studio\                                    default project
      project.json  work.json  memory.md
      runs\  chats\  model-lists\  data\  models\  evals\
```

- Every project gets its own `runs\` and `chats\`. The default project is `Workspaces\Studio`; Work projects are siblings under `Workspaces\`.
- Settings > System shows these paths and opens the project, runs and settings folders directly.
- Empty legacy folders such as `Studio\runs` or `Studio\chats` are pruned at startup when they contain no files. Anything holding data is left alone.

## 5. The Training lab

### 5.1 The Build Map

The lab is **not** a linear wizard. It is a graph of *moves* over a model lineage. You stand on a node (a model state, or nothing), say what you want changed, and the wizard proposes a path through the graph. The path is shown as a tree: chosen steps, ghosted alternatives, a reason on each, and "fork here" to branch variants.

```mermaid
flowchart LR
  A([Nothing]) --> Design
  B([Base model]) --> Data
  C([My model / Adopt]) --> Branch{Branch point}
  D([Checkpoint]) --> Branch
  E([Playbook]) --> Branch
  F([Previous run]) --> Clone[Clone and edit]
  Branch --> AddData[Add data]
  Branch --> Retune[Re-tune]
  Branch --> Shrink[Shrink]
  Branch --> Requant[Re-quantize]
```

**Starting points:** nothing, a base model (hub or local), a model of mine, a checkpoint (resume or branch), a playbook, a previous run, or **Adopt** an existing model.

**Goals** (multi-select): teach it new knowledge, change behaviour or style, make it better at what it does, make it smaller or faster, fit a device, add a capability (context, tool use, language), change architecture.

**Moves** and their stages:

| Move | Notes |
|---|---|
| Design | Shape, dense or MoE, layer layout, vocabulary |
| Formatter | Define, verify or convert the chat template ("dialect") |
| Tokenizer | Inherit, train new (reserving role tokens), or extend |
| Ingest / Triage / Synthesize / Mix / Split | Data stages, see section 6 |
| Pretrain / Continue / LoRA / SFT | Weight-changing training moves |
| Distill / Prune / QAT / Quantize | Compression moves |
| Merge | Adapters or models |
| Evaluate | After every weight-changing move |
| Package | `.smm` / `.gguf` plus report |

**Preconditions keep the tree flexible without making it free-for-all:** SFT needs a formatter and a tokenizer with role tokens; distillation needs a teacher and a student; fine-tuning needs loadable float weights; quantization comes last. A user may jump to any node whose preconditions hold, and the wizard fills in the rest.

**Implemented in 1.1:** `TrainingPlanner` is a deterministic rule set over the start node and the selected goals. It returns **Fast / Balanced / Ambitious** paths whose moves each carry a reason and whose unsupported steps (SFT, distillation, post-training quantisation) stay visible as **blocked** with a "why not" and a supported alternative. The New-model tab shows the intake, the three plans and a "Use plan" action that fills the relevant editor or playbook. The estimator is limited to the parameter count until the intake/estimator rail lands; the rehearsal run is still future work.

### 5.2 Typical paths

| Start | Goal | Suggested path |
|---|---|---|
| Nothing | Narrow edge model (router, extractor, classifier) | Design â†’ Formatter â†’ Tokenizer â†’ Data triage â†’ Pretrain (small corpus) â†’ SFT â†’ QAT â†’ Verify â†’ Package |
| Base model | Tailor to a client | Data â†’ Triage â†’ Continue/LoRA with replay mix â†’ SFT (if chat) â†’ Verify â†’ Quantize â†’ Package |
| My model v2 (float master) | Upgrade with new data | Data delta â†’ LoRA/continue from v2 â†’ Compare vs v2 â†’ Promote v2.1 â†’ Re-quantize variants |
| My model (quantized only) | Upgrade | Warn â†’ find or rebuild master â†’ else LoRA on dequantised weights (lower ceiling) â†’ Re-quantize |
| Checkpoint | Continue | Resume (same recipe) or Branch (change settings or data) |
| My model | Smaller, for a new device | Distill to a smaller student â†’ QAT â†’ Verify against the device profile |
| Playbook | New client | Fill open fields (data, device) â†’ Run |
| Any model | Faster on this device | Re-quantize plan â†’ threads and KV settings â†’ Measure (no training) |

**From-scratch pretraining is gated by honest arithmetic.** Good small public models are trained on trillions of tokens; at roughly a thousand tokens per second (the class of throughput the README reports for a 135M model on GPU), 2B tokens takes about 20 days. From scratch therefore suits narrow-domain edge models where a small corpus is enough, and the wizard shows this calculation before the user commits.

### 5.3 What "upgrade" means

1. **From a point.** Pick any lineage node and choose *resume* (same recipe and optimizer state) or *branch*.
2. **Prefab settings, tweaked.** An **Upgrade Kit** is a playbook of improvement moves with sensible defaults, such as "add data and re-tune" with replay and a low LoRA rank.
3. **New training sets added.** The planner marks downstream steps stale and runs from the data node onward. It chooses between appending and rebuilding and explains why; for small or off-domain data it recommends replay mixing to reduce forgetting.

### 5.4 Wizard behaviour

1. **Intake in about six plain-language questions:** what the model does, where it runs, what data exists, what "good" looks like (a handful of example prompts), licence and privacy constraints.
2. **Three plans:** *Fast* (LoRA on a base), *Balanced* (continue, then SFT, then quantize), *Ambitious* (distill or scratch). Each shows time on this machine, size, risks and why it was suggested.
3. **Every suggestion is a rule result:** `{ value, reason, evidence, alternatives }`, for example *"360M dense, because your target has 2 GB free: weights â‰ˆ 0.2 GB at Q4 plus KV cache at 2k context leaves headroom; 1B fits but misses your 8 tok/s target."* Rules are versioned data files with IDs, so they are testable and transparent.
4. **Three depths per field:** accept, adjust (guard-railed sliders with live estimates), or edit the raw Recipe.
5. **A live estimator rail:** memory, time, file size, tokens-per-parameter, risk flags.
6. **A rehearsal run** of about 30 steps measures real throughput, refines the ETA and catches pipeline errors before hours are committed.
7. **"Why can't I?"** on disabled options. For example, GPU training is currently LoRA-only, so a from-scratch run falls back to CPU, and the card says so with alternatives.

The 1.1 planner implements the three-plan proposal and the reason/alternative structure, with a parameter-count estimate. The six-question intake, the full estimator rail and the rehearsal run remain future work.

### 5.5 Improve: the diagnosis loop

**Diagnose → Hypothesise → Experiment → Compare → Promote.** Diagnosis reads run history and evals:

| Observation | Likely cause | First experiments |
|---|---|---|
| Val loss still falling at the end | Under-trained | More steps, restart at lower LR, better data |
| Train falls, val flat or rising | Overfit | Fewer epochs, more diverse data, lower LoRA rank |
| Spikes or NaN | LR too high or too little warmup | Lower LR, longer warmup, check clipping |
| Good val loss, poor answers | Format or masking issue | Check the formatter preview and SFT data |
| Q4 far worse than F16 | Quantization-limited | Per-tensor plan, QAT, more bits for embedding or output |
| Better in domain, worse in general | Forgetting | Mix in 10â€“30% general replay, lower LR |

Experiments run as short, comparable trials with the same seed and validation set. **Data experiments come first**, since data usually matters more than hyperparameters. A held-out "final exam" set is touched only on promotion, to avoid tuning to the validation set. *Promote* creates a new version with a changelog and a before/after report that doubles as the client upgrade pitch.

### 5.6 Lab shell

`/training` is a first-class section next to Chat and Work, with deep-linkable tabs: **New model** (`?tab=new`), **Improve**, **Playbooks**, **Data**, **Runs** and **Models**. Empty states stay honest: no fabricated runs, estimates or playbooks. Tabs whose engine work is not connected keep their start buttons disabled.

**New model** is a full `.smmt` editor rather than a preset picker. It covers the architecture preset and manual dimensions/kernel options, tokenizer kind and vocabulary, batching, optimizer and LR schedule, accelerator plugin, QAT target (validated against the dimensions before a run), checkpoint interval/retention, resume and warm restart, validation/exam splits, export path, and the optional embedded system prompt, skills folder and tool DLLs. A source/stage section adds any discovered source or cleaning stage, renders each constructor parameter from its metadata, supports per-source and global stage chains, and exposes incremental mode; unknown plugin components are called out rather than silently dropped. A live review shows the resolved architecture, parameter estimate, precision, device and any blocking warning; the same `TrainingJobValidation` rules gate the start action, so the form cannot look valid and fail on a dimension it did not flag. **Save job** writes the `.smmt`, **Load selected** restores it, **Run selected job** starts a pretrain job directly (fine-tunes open in Improve), and the unsaved draft survives tab switches via `StudioTrainingService.Draft`. The **Data** tab summarises the configured sources, per-source stage counts, global stages and incremental state.

**Playbooks** are curated editor values behind **Training → Playbooks**: a tiny smoke test, a small domain pretrain, a LLaMA-style chat base, a Q8_0 packaging run, and two LoRA tunes (merged or adapter-only). Applying one fills New-model or Improve, keeping the corpus and base the user already chose. They are starting points, not measured results, and the tab says so.

### 5.7 Engine reused from the CUI

The CUI already trains a model from a corpus to an exported `.smm`, and that pipeline is not UI-bound. Studio ports the UI-free pieces into `Studio.Core/Training` rather than writing a second engine:

- `TrainingJobSettings` (the persisted `.smmt` job), `JobComponent`/`JobSource`, and the job save/load format stay compatible with existing CUI jobs.
- `TrainingModelOptions` supplies the architecture presets, QAT parsing and vocab padding rules.
- `IncrementalPlanner`, `IncrementalStore` and `SourceHasher` provide per-file corpus deltas and content fingerprints.
- `TrainingRunner` builds the data pipeline, trains or grows the BPE tokenizer, constructs random weights, runs the train loop with checkpoints and QAT, exports each retained checkpoint and the final `.smm`, and stamps lineage.

The Studio-specific layer is `StudioTrainingService`: one active run, a log tail, step metrics, progress, cancellation and auto-saved jobs under `StudioPaths.Training`. `TrainingRunnerSmokeTests` runs a one-layer, two-step character-tokenizer job end-to-end so the port cannot regress silently.

What the CUI pipeline does **not** do is create a fine-tune job, so Studio adds one.

### 5.8 LoRA fine-tune

**Training → Improve** turns a base `.smm` into a tuned model with LoRA:

- `TrainingRunner` loads the base with `SmmTrainingPipeline.LoadForTraining`; the base's tokenizer, model config and chat template are reused, so the new corpus is encoded exactly as the base was trained.
- `LoRAModel` attaches rank-`r` adapters to the selected projections; only `LoRAParameters()` go to the optimizer and backprop, so the base stays frozen and memory stays small.
- Output is chosen per run: **merged** (fold `W += scale·A·B` into the base and export an ordinary `.smm`), **adapter only** (write a portable `.smlora` with rank, alpha, targets, base fingerprint and the adapter tensors), or **both**. A `.smlora` is a few MB even for a large base and is rejected up front when paired with a base it was not trained against.
- Every fine-tune checkpoint (rolling, best-validation, interrupted and final) also carries `adapters.smlora` beside the generic checkpoint state. Resume resolves an explicit `.smlora` file or checkpoint folder first, then the latest step checkpoint, then the best-validation checkpoint. The adapter file supplies the rank/targets and its base fingerprint is validated; the run continues the step counter with a fresh optimizer (weights warm-restart, data cursor restarts). `StartFresh` ignores all of it.
- After the loop, the ordinary SMM exporter writes a merged result with the base chat template, system prompt, skills and lineage (`method=lora`, `base`, `lora_rank`, `lora_alpha`). The merged file needs no adapter at inference.

`FineTuneRunnerSmokeTests` trains a tiny base, fine-tunes it, asserts the base file's hash is unchanged, and reloads the merged export for inference. `AdapterResumeSmokeTests` covers adapter-only export, resume from a `.smlora`, checkpoint-folder resume continuing the recorded step, and the guard that rejects a resume with no steps left.

**Real-model check (SmolLM2-135M-Instruct).** A Q8_0 GGUF (138 MB) converted to SMM in 3.9 s, then `LoadForTraining` rebuilt a Llama-family model (vocab 49,152, hidden 576, 30 layers, context 8,192). A rank-8/alpha-16 adapter on q/v produced 60 adapters and 460,800 trainable values — 0.34% of the base. Three CPU steps ran at roughly 4 s each after the base load (~10 s to the first step), peaked at 1.54 GB working set, and the merged F32 export was 515 MB. This is a feasibility smoke, not a quality run: it proves convert → load → LoRA → merge → export works on a real Llama-family base. GPU accelerators and non-Llama architectures remain untested.

### 5.9 Live curves and validation

The lab needs a diagnosis signal, not just a final loss number:

- `TrainingJobSettings.ValidationPercent` (0–90, default 0 for old jobs) asks for a held-out split. `TrainingRunner` uses the library's `DeterministicSplit.TrainVal`, which buckets documents by a stable content hash — appending files never moves an existing document across the split.
- The split is taken **after** cleaning, and a fixed set of up to four held-out batches is materialised once. Every evaluation scores the same documents, so val-loss points are comparable across steps. The tokenizer is still trained over the full corpus; it learns the token inventory, not the loss being measured.
- The loop's existing `evaluate` hook runs at the logging cadence, so every logged step carries a val loss. `TrainLoop` already keeps the best checkpoint when val loss improves; the runner logs each evaluation and reports `BestValLoss`/`BestValStep` on the result.
- `StudioTrainingService` keeps a `Curve` of logged points plus the best step. `TrainingLossChart` draws train and validation lines in the run panel, with a dashed marker at the best val step.

A finished run is persisted by `TrainingRunStore` as one JSON record under `Training/run-history`: the curve, best val step, final loss, elapsed time, export size and a compact job profile. `StudioTrainingService` caches the history and invalidates it when a run finishes or a record is deleted; a corrupt or half-written file is skipped rather than hiding the rest.

**Training → Runs** lists those records under **Experiments**, each with **Use as base** (promote a successful `.smm` straight into an Improve job) and **Delete**. With two runs that logged at least two points, **Compare** offers two selectors, a metric table that marks the lower loss and elapsed time as better, and the two curves side by side.

### 5.10 Held-out exam and promotion

A run can reserve a second deterministic slice, `TestPercent`, disjoint from validation. Training branches it out before the validation split and never reads it; the tokenizer still sees the full corpus. The slice is scored only when the user runs an **Exam**, either from a run card or as part of a comparison.

- `TrainingExamRunner` loads the exported `.smm` with its embedded tokenizer, rebuilds the job's cleaned full-corpus pipeline (`TrainingRunner.BuildCleanPipeline`), applies the same content-hash test branch, reads up to eight fixed batches, and reports cross-entropy loss, perplexity and token count.
- `StudioExamService` owns the one active exam, streams its status, writes `ExamLoss`/`ExamPerplexity`/tokens/timestamp back into the run record, and refreshes history. Adapter-only runs are rejected until merged; runs without a saved `.smmt` or a test split fail with a clear message.
- **Promote A over B** compares two examined runs. `TrainingPromotionReport.Compare` calls a candidate better when its exam loss is at least 0.5% lower, worse when at least 0.5% higher, and inconclusive in between. `TrainingPromotionStore` writes the report as JSON and Markdown under `Training/promotions`; neither model file is modified.

The exam is what makes the **Promote** step honest: the held-out slice was excluded from training and from checkpoint selection, so the reported difference is not just the run's own validation curve. `TrainingExamRunnerTests` trains, exports and scores a model; `TrainingPromotionStoreTests` covers verdicts, the inconclusive band and both output formats.

`ValidationSplitSmokeTests` trains from several documents with validation on and asserts the split, the evaluation log, and a best step in range. `TrainingRunStoreTests` covers the history round trip, ordering, corrupt-file tolerance and deletion.

### 5.11 Format conversion

**Training → Models** converts between the packaged model (`.smm`) and GGUF in both directions. The direction follows the source extension, the destination is suggested beside the source with the extension swapped, and overwriting an existing file needs an explicit tick. `.smmt` is not a model format: it is the saved training-job JSON, so it is never offered as a conversion source.

`ModelConverter` (in `Studio.Core/Training`) is a thin wrapper over the library's `SmmToGufConverter` and `GgufToSmmConverter`. Both stream each tensor's raw bytes and cannot normalise or re-quantise them. The wrapper adds a progress relay, cancellation (leaving the destination untouched), and a read-back check that reopens the new file with the loader that will consume it, so a truncated or config-less output fails at conversion time rather than at model load. `StudioConversionService` holds the single active conversion and its progress across tab switches; `ModelConverterTests` covers the round trip and the overwrite/unknown-source guards.

---

## 6. Data

### 6.1 Two triage tracks

- **Corpus triage** (pretraining and continued training): quality, near-duplicates, language, licence, PII, length, domain tags. The formatter plays no part; it is text and a token stream.
- **Instruction triage** (SFT): formatter-aware. Each example is rendered through the model's formatter, then checked for valid roles, length after rendering, correct loss mask, and contamination against the eval set.

### 6.2 The formatter belongs to the model

For a base model it is inherited and only verified. For from-scratch it is designed up front, because role tokens must be reserved when the tokenizer is trained: **formatter, then tokenizer, then pretraining**.

The **Formatter Designer** picks a template (ChatML, Llama 3, Gemma, custom Jinja) and shows a live preview of a rendered conversation with tokens coloured by whether they count toward the loss. One formatter object serves training, chat and the server, and is embedded in the `.smm`.

### 6.3 Data Factory (synthetic data from local models)

A local GGUF teacher, in-process or through the OpenAI-compatible server, can generate Q&A from client documents, rewrite or clean data, label and tag records, judge and filter examples, and produce teacher answers for distillation.

Guard rails:

- **Speed.** CPU decoding is a few tokens per second on modest hardware. 10,000 pairs of about 200 tokens is 2M tokens, roughly 55 hours at 10 tok/s. The estimator says so up front, and results are cached by prompt hash so runs are resumable.
- **Licence.** Each synthetic record stores the teacher model and its licence, with a warning when the terms may restrict training other models. This is a flag, not legal advice.
- **Model collapse.** Warn when synthetic data dominates a pretraining mix. In SFT it is normal, but real data should stay in the blend.
- **Human review.** A queue where 50 sampled records are approved, edited or rejected before the run.

---

## 7. Model design defaults

These are defaults with reasons, not laws. The wizard shows the reason and the alternatives each time.

- **Dense by default, MoE when the device suits it.** SharpMind's streaming mode does not need every expert resident. `MoEResidencyPlanner.SuggestResidentExperts` clamps pinned experts per layer to a memory budget (with 20% headroom), and cold experts load on demand per token's top-k. MoE memory therefore scales with *resident experts Ã— layers*, not total experts. With top-k = 6, 6 resident experts per layer is the floor (fewer means every token touches storage) and about 2Ã— is comfortable. MoE is attractive with fast storage and a narrow workload, and a poor choice on slow flash.
- **Locality versus balance (hypothesis to test).** The standard load-balancing loss pushes expert usage toward uniform, the opposite of what a cache wants. For edge-streaming MoE, offer a locality-aware recipe: weaker balancing plus domain-specialised experts. Verify measures the result.
- **Working-set curve.** Verify runs the client's probe prompts, records per-layer expert routing, and plots hit rate against resident-expert count, so `DesiredResidentExperts` is recommended from evidence. The measured "hot experts per layer" can ship in the package so the edge device starts with warm pins.
- **Deep and thin for small models.** SmolLM2-style shapes (about 30 layers at 576 hidden for 135M) tend to beat wide and shallow at this size.
- **Vocabulary as a parameter lever.** The embedding table can be a large share of a tiny model. Show its percentage, and allow a smaller vocabulary for narrow domains.
- **Context is a memory bill.** Show KV-cache memory per context length for the chosen shape.
- **Quantization with QAT.** Small models degrade more under heavy quantization, so below Q5 suggest QAT and a per-tensor plan.

### Training precision

- **F32 master** is the default (`QuantAwareTraining: null` in `.smmt`).
- **QAT target** (F16, Q8, Q4 and below) is described as a quality tool for export. In the library it is fake quantization: master weights, gradients and optimizer state stay F32, so memory does not drop, and backward stays F32. Q8 QAT is therefore not expected to be much faster; the ceiling is about 1.5Ã— per step and 10â€“20% is realistic. The rehearsal run measures F32 against the chosen target on the user's machine.
- **Vocabulary multiples.** Q8_0 needs the vocab size to be a multiple of 32; K-quants need hidden size a multiple of 128 and vocab Ã— hidden a multiple of 256. Studio validates this before starting and offers to pad the model vocab.
- **Later:** LoRA over a quantized frozen base, and 8-bit optimizer states for memory.

### Distillation, quantization, hub

- **Distillation:** *sequence-level* (a teacher generates text; the student does SFT; works across tokenizers and with any OpenAI-compatible endpoint) first; *logit-level* (KL with temperature, shared tokenizer, cached top-k teacher logits) later.
- **Quantization:** a sensitivity-aware plan (collect activations on a calibration set, measure perplexity change per tensor role, spend bits where they hurt least), with a Pareto chart of size versus quality and measured speed on this machine.
- **Hub client:** HttpClient only; search, resumable ranged download into the model cache, gated-licence display, dataset download. GGUF metadata sits at the start of the file, so a ranged request can run the architecture-support check **before** downloading gigabytes. The token comes from `HF_TOKEN` or the OS credential store.

---

## 8. Experience design

### Signature moments

1. **Watch a model learn.** Point at a base model and a folder of documents, press go, and see live loss curves, checkpoint markers, and the same probe prompts answered at each checkpoint. Click any checkpoint to chat with it.
2. **Drop a folder, get a model.** The guided flow estimates what fits and how long it takes, picks defaults, and ends in a packaged `.smm` or GGUF.
3. **One shell, three depths.** Chat, Work and Training in one app; every GUI action is a Recipe you can open, diff and rerun headless.
4. **Lineage you can see.** A tree of base, fine-tune, merge and quant, with the datasets behind each node.

### The training screen

- Live train and validation loss with EMA smoothing, learning-rate overlay, grad norm, tokens/s, memory and ETA.
- Checkpoint markers on the curve; click one to chat with it (time travel).
- A **snapshot timeline**: the same probe prompts answered at each checkpoint, side by side.
- For MoE, a per-layer expert utilisation heatmap and routing entropy, so collapse is visible within minutes.
- Data funnel (sources through stages, with kept and dropped counts), token histograms and a sample inspector.
- A/B arena between any two checkpoints or models, blind, with local Elo.

### Visual language

An instrument panel: dark and calm, one accent colour, monospaced tabular numbers, motion only where it carries state (a live curve, a pulsing checkpoint). Every empty state says what to do next. Every number has an obvious meaning, with a "what's this?" popover for newcomers. Light and dark themes via CSS variables, keyboard-first, respects reduced-motion and contrast settings. Charts are SVG.

---

## 9. Library dependencies (SharpMind 1.0.9.0)

Studio depends on additive library changes. The CUI and its tests must not move: new optional parameters, new types and new overloads only. Status is **open** for all items.

### Telemetry and checkpoints

| # | Change |
|---|---|
| A1 | `TrainStepResult`: add `TokensThisStep`, `TokensSeen`, `TokensPerSecond` |
| A2 | Eval hook: `TrainConfig.EvalInterval` and an optional `Func<int, EvalResult>` on `RunAsync` |
| A3 | Best-checkpoint tracking (`KeepBest`) |
| A4 | Checkpoint `Extra` metadata (base fingerprint, recipe, dataset and tokenizer hashes, val loss, tokens seen, full or LoRA) |
| A5 | Data cursor saved in the checkpoint so resume skips consumed batches |
| A6 | Cancelling a run writes a final checkpoint at the current step |
| A7 | `Lineage` block in `SmmWriteOptions`, emitted under meta `lineage`; a Studio-written file must still load in the CUI |

### Data

| # | Change |
|---|---|
| B1 | `CountingStage` decorator and a `PipelineNode` overload, giving per-stage in/out/dropped counts on today's string pipeline |
| B2 | Deterministic `TrainVal(pipeline, valFraction, seed)` split using a stable content hash |
| B3 | A cached, fixed validation set of N batches |
| B4 | Typed record path (`DataRecord`, `IRecordSource`) alongside the string path, with adapters |
| B5 | `IRecordBatchStrategy` and `SftBatcher` with assistant-only loss masks |
| B6 | `ConversationTokenizer` using `IChatPromptFormatter`, diffing rendered turns to find assistant spans. The riskiest data item |
| B7 | Weighted merge node for replay mixing (for example 80% client data, 20% general) |

### Fine-tuning from a base

| # | Change |
|---|---|
| C1 | `SmmTrainingPipeline.LoadForTraining`: load a GGUF or SMM as float weights, reuse its tokenizer and config, optionally attach LoRA. **Wired in Studio and smoke-tested end-to-end on SmolLM2-135M** (Q8_0 GGUF → SMM → LoRA → merged export) |
| C2 | Support matrix: which loaded architectures the training transformer can actually run, with a clear `NotSupported` for the rest. Llama-family is verified; other architectures still need a load/train check |
| C3 | Adapter-only save and load with a base-fingerprint check. Wired in Studio: `.smlora` export and resume from an adapter file or checkpoint folder |
| C4 | Export fidelity: the tokenizer and `tokenizer.chat_template` survive a merged export; F16 or Q8_0 export before `SmmQuantizer` handles K-quants. Covered for merged output in the library; Studio writes the fine-tune at F32 |
| C5 | Training memory estimator next to `ModelSizer` (full vs LoRA, optimizer, activations) |

### Streaming MoE and precision

| # | Change |
|---|---|
| E1 | Public read-out of per-layer expert route counts after a run |
| E2 | Explicit pin list or `ExpertHeatProfile` to pre-warm pins, persisted in `.smm` meta |
| E3 | Per-token expert-miss counter |
| E4 | Sequential-read benchmark helper for the Device Profile |
| P1 | Wizard QAT list gains Q8_0, Q4_0 and the legacy types; stop silently remapping `Q8_0` to `Q8_K` |
| P2 | Pre-flight QAT validation before tokenizer and model build, with an offer to pad the model vocab. Check that the exporter and loader tolerate a model vocab larger than the tokenizer vocab |
| P3 | Investigate whether frozen-base LoRA skips the base weight-gradient matmul (the layer `Backward` computes it unconditionally; `BackpropEngine` may route around it) |

### Smaller checks

- `Evaluator` should work on a quantized, inference-loaded model, so the F16-versus-Q4 gap can be measured on the same validation set.
- The tokenizer trainer should accept a reserved special-token list.

### Not in 1.0.9.0

Studio.Core owns the job service, the `events.jsonl` writer, projects, `.smmtx`, the hash-based source fingerprinting, the probe runner, the hub client and all UI. MoE load-balancing loss, logit-level distillation and sensitivity-aware quantization are deferred. Gemma 4 support is tracked separately and is not a Studio dependency.

---

## 10. Release scope

**1.1.0.0 is defined by the spine, not the feature count:** events, runs, projects, Recipes, and a complete Chat, Work and guided fine-tune loop, with each Training lab lighting up on that spine over time. If the spine is right, each lab ships as an increment instead of another re-architecture.

Draft slices, not yet confirmed:

| Version | Contents |
|---|---|
| **1.1.0.0** | Studio shell, Chat, Work, and one polished guided flow: train from a corpus, watch it live with checkpoint markers, package it. The Training lab exposes a rules-based build-map planner, a full `.smmt` editor (sources/stages, architecture, accelerator, QAT, schedule, checkpoints, splits), curated playbooks, fine-tuning with merged `.smm` or adapter-only `.smlora` output and resume (smoke-tested on SmolLM2-135M), run history with curves and comparison, and a held-out exam backing a written promotion report |
| 1.1.1.0 | Data Lab, Formatter Designer, probe-prompt snapshot timeline, A/B arena, hub client |
| 1.1.2.0 | Quant Lab (sensitivity-aware plans, Pareto chart) and distillation (sequence-level first, logit-level second) |
| 1.1.3.0 | Architect canvas, MoE load-balancing and utilisation heatmap, upcycling and Branch-Train-Mix, then HMoE |

**Build order inside the lab:** the commercial loop first (tailor a playbook to a client), because it forces run history, the eval suite and the Recipe format that "Improve" reuses.

---

## 11. Risks and limits

- **Scope.** This is a product-sized project. The two riskiest items, GPU full-parameter training and from-scratch MoE, stay off the 1.1.0.0 critical path.
- **Hardware reality.** Per the library README, the GPU engine trains LoRA adapters only, so from-scratch training is CPU-only today. MoE training is CPU-only and has no load-balancing loss, so from-scratch MoE can collapse onto a few experts.
- **C1 is unproven** on real models (section 9). The shipped corpus-to-export path does not depend on it: it starts from random weights and trains the tokenizer and model together.
- **Ported engine drift.** The CUI's training job and runner are copied into Studio.Core until they move to a shared library, so changes to `.smmt` semantics must be made in both places.
- **Chat-template masking (B6)** can break on templates that strip or inject text.
- **Synthetic data licensing** is flagged, not solved.
- **Remote access** widens the attack surface and stays opt-in.

## 12. Open questions

1. Does the author keep float masters (checkpoints or F16 files) of existing models, or are some only available as quantized builds? This sets how much of "upgrade what I have" can run at full quality on day one.
2. Is the first guided flow raw documents (continued training or LoRA on text) or chat and instruction data? Documents need none of B4â€“B6, so they could slip to 1.1.x.
3. Will Studio share the library's licence and public visibility, or have its own?
4. How much remote and multi-user access is in scope for 1.1.0.0?

## 13. Glossary

- **Recipe / `.smmtx`:** the step-graph description of a build.
- **Playbook:** a versioned partial Recipe with locked and open fields.
- **Device Profile:** the target hardware, including storage speed.
- **Float master:** the full-precision version a model's quantized builds derive from.
- **Replay mix:** general data blended into domain data to reduce forgetting.
- **QAT:** quantization-aware training.
- **Working-set curve:** cache hit rate versus resident-expert count for streaming MoE.



