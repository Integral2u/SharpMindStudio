# Microsoft Store listing — SharpMind Studio

Everything Partner Center asks for, ready to paste. The package is
`SharpMind.Studio Setup\Release\SharpMind.Studio.msix`; the images are in this
folder (`logo-300.png` for the app tile icon, `icons\` for the tile set).

Identity, for reference:

| Field | Value |
|---|---|
| Package/Identity/Name | `42769PixelWhipped.SharpMindStudio` |
| Package/Identity/Publisher | `CN=25DAC6FD-1AC6-4AC3-BD12-47C75263F078` |
| Publisher display name | SharpMind Studio |
| Version | 1.1.2.0 |

---

## Product name

```
SharpMind Studio
```

## Short description (max 256 characters)

```
A local-first workbench for running, chatting with and fine-tuning models on your own machine, and renting a GPU by the hour when a job needs more.
```

## Description

```
SharpMind Studio is a desktop workbench for models you run yourself.

Start with a model on this PC: chat with it, attach files, switch models mid-conversation, and keep every session in a folder you own. When a job needs more memory than your machine has, add a GPU you rent by the hour, deploy the SharpMind server to it, and keep the same models, tokenizer and run formats. When you want a frontier model instead, connect a router you already pay for with your own API key.

WHAT YOU CAN DO

Chat — Local models, remote models through OpenRouter, EuroRouter or OpenCode Go, file attachments, saved history, and a per-chat model switcher.

Work — Give the assistant a project folder and let it read, edit and run things. Every tool has its own permission, and a program is shown to you as the exact command line before it runs.

Training — LoRA fine-tunes on a local GPU or a rented one, with dataset preparation, resume, checkpoints and export in the same window.

Data Factory — Build and clean training data from documents, JSONL or text, with provenance recorded per record.

Models — Download GGUF files, convert and quantize them, and inspect what a model container carries.

Labs — Layer importance, latent projection, activation maps, skip-layer experiments and quantization comparison for the model you are actually running.

Compute — Deploy and destroy Vultr or Runpod instances from Studio, with an exit prompt that warns you about anything still running.

Pairing — Open the same Studio from a phone on your own network, with revocable devices.

BUILT TO STAY ON YOUR MACHINE

There is no SharpMind account and no telemetry. Models, chats, projects, training output and settings live under your Windows profile, and uninstalling leaves them there. Prompts leave the machine only when you send them to a remote provider you configured, and API keys are stored encrypted with the Windows data protection API.

STAY IN CONTROL OF TOOLS

Work tools are off, ask or always per tool, agent, model and project. A folder you never granted cannot be written to. Programs run with the arguments you approve, no shell expansion, and the process tree is killed on timeout.

WHAT IT EXPECTS

Windows 10 version 2004 or later, 64-bit. A model file in GGUF or SharpMind container format, from your disk or downloaded in the app. 8 GB of memory is comfortable for 7B models at common quantizations; a discrete GPU is optional. The .NET runtime is included, so there is nothing else to install.
```

## Product features (max 20, each 200 characters)

```
Local chat with GGUF and SharpMind container models, no account required
Remote models through OpenRouter, EuroRouter and OpenCode Go with your own API key
Work mode: read, edit and run against a project folder with per-tool permissions
LoRA fine-tuning with dataset preparation, resume, checkpoints and export
Data Factory for building and cleaning training data with provenance
Model downloads, conversion, quantization and container inspection
Labs: layer importance, activation maps, skip-layer and quantization comparison
Deploy and tear down Vultr or Runpod GPU instances from inside the app
Light and dark appearance with a custom accent colour
File attachments on local and remote models
Phone pairing over your own network with revocable devices
No telemetry, no advertising identifier, data stays under your profile
```

## What's new in this version (1.1.2)

```
Tool permissions — every Work tool is off, ask or always, set per agent, model, project or tool. A program now shows its exact command line in the prompt you approve.

MCP servers — add stdio servers in Settings, connect them, and their tools join the normal list switched off until you enable them.

Attachments on remote providers — images, documents and files are sent as content parts, and returned files render with open and download.

Light appearance — Settings has Dark and Light, live-switched, with an accent colour that stays readable in both.

Compute — Vultr deploy and teardown from the app, a Compute page with manual destroy, an exit prompt that offers Keep running, and idle teardown at 15, 30 or 60 minutes for instances Studio created.

Chat and Work — sessions grouped by project, model switching in an open chat, and New chat starts clean.
```

## Search terms (7, 30 characters each)

```
local llm
gguf
fine tuning
lora
openrouter
offline chat
ai workbench
```

## Category

```
Developer tools
```

`Productivity` is the alternative if you would rather not sit next to IDEs. The
app is genuinely both; the Work, training and model tooling leans developer.

## System requirements

```
OS: Windows 10 version 2004 (build 19041) or later, or Windows 11; 64-bit
Memory: 8 GB for 7B models at common quantizations; 16 GB or more for larger models
Disk: 500 MB for the app, plus the models you keep (2-40 GB each)
Graphics: optional; a discrete GPU makes local inference and training faster
Notes: the .NET runtime is bundled; WebView2 is present on current Windows and installs automatically if missing
```

## Age rating

Answer the IARC questionnaire honestly; the answers that matter here:

- **Unrestricted internet access: yes.** Remote model providers and web fetch are user-initiated but not filtered.
- **User-generated content: no.** There is no user-to-user content or social surface. Model output is generated, not shared.
- **Purchases: no.** Studio never sells anything; provider accounts and compute are paid directly to those providers.

Expect a **12+ / Teen** result for unrestricted internet access. Do not claim
"Everyone" by answering no to internet access if remote models are enabled in the
build you ship.

## Listing details

```
Support contact: https://github.com/Integral2u/SharpMindStudio/issues
Website: https://www.integral2u.com/
Privacy policy: https://www.integral2u.com/wiki/privacy.html
Copyright: © 2026 Integral2u
Developer: SharpMind Studio
```

## Screenshots

Partner Center requires at least one desktop screenshot at **1366×768** or
larger (1920×1080 is safest); up to 10.

The two images in `docs\` are 1366×726 and 1366×728 — 40 pixels short of the
minimum, so they will be rejected. Options:

1. Re-shoot at 1920×1080 from the app (best; two or three are enough).
2. Capture them headlessly at 1920×1080 against a throwaway data folder, so no
   personal chats appear in a public listing.

Suggested captions, once shot:

```
Chat with a local model, attachments and the model switcher in one window
Work mode with per-tool permissions and the exact command before it runs
Training a LoRA from a finished dataset, with checkpoints and export
```

## App tile icon

Upload `logo-300.png` (300×300). The package carries its own tile assets
(`icons\`); both come from `scripts\build-store-assets.ps1`, so the mark is the
same everywhere.
