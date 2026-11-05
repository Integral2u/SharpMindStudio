# SharpMind Server

One container that runs both halves of a deployable SharpMind host:

| Port | Service |
|---|---|
| `5117` | Studio web UI |
| `5118` | Studio LAN / phone listener |
| `8080` | Compute API (chat, uploads, training jobs) |

Both processes share `/data/SharpMind`, so a model downloaded or trained in
Studio is immediately the model the API serves, and anything uploaded through
the API shows up in Studio.

## Build

The local-source builds use the sibling `SharpMind` tree; nothing has to be
published. Run from the folder that contains both repositories and make sure
`docker/local-context.dockerignore` is copied to the parent as `.dockerignore`.

```powershell
cd C:\Integral2u\source\repos
Copy-Item SharpMind.Studio\docker\local-context.dockerignore .dockerignore

# CPU
docker build -f SharpMind.Studio/docker/server/Dockerfile.local `
  -t sharpmind-server:local .

# GPU (ILGPU plugin + CUDA user-space libraries)
docker build -f SharpMind.Studio/docker/server/Dockerfile.gpu.local `
  -t sharpmind-server:gpu .
```

## Run

```powershell
docker run --rm -p 5117:5117 -p 8080:8080 `
  -e SHARPMIND_TOKEN=change-me `
  -v "${PWD}/server-data:/data/SharpMind" `
  sharpmind-server:local
```

Or:

```powershell
docker compose up --build
# GPU:
docker compose -f docker-compose.gpu.yml up --build
```

Check both services:

```powershell
curl.exe http://127.0.0.1:5117/health
curl.exe http://127.0.0.1:8080/health
```

## First access and pairing

Studio treats non-loopback clients as LAN clients and answers 404 until LAN
access is on. The image sets `Studio__LanEnabled=true`, so the browser is
redirected to `/pair` and must be approved once.

Locally, run one pairing offer inside the container and approve it:

```powershell
docker exec <container> curl -s -X POST http://127.0.0.1:5117/gateway/api/pairing/new
# open http://127.0.0.1:5117/pair and enter the token and pin, then:
docker exec <container> curl -s -X POST `
  http://127.0.0.1:5117/gateway/api/pairings/<ID>/approve `
  -H "Content-Type: application/json" -d "{\"mode\":\"desktop\"}"
```

On Runpod use the pod's **Web Terminal** for the same two commands.

## Runpod

1. Push the image to Docker Hub:

   ```powershell
   docker tag sharpmind-server:gpu <dockerhub-user>/sharpmind-server:gpu
   docker push <dockerhub-user>/sharpmind-server:gpu
   ```

2. Create a GPU pod with the custom image. Expose **HTTP ports 5117 and 8080**
   (Runpod gives each its own proxy host), and attach a network volume at
   `/data/SharpMind`.
3. Environment:

   ```text
   SHARPMIND_TOKEN=<a long random secret>
   SHARPMIND_DATA=/data/SharpMind
   SHARPMIND_MODELS_DIR=/data/SharpMind/Models
   SHARPMIND_PLUGINS=/app/plugins
   ```

   The GPU image already sets `SHARPMIND_ACCELERATOR=auto` and
   `SHARPMIND_INFERENCE_ACCELERATOR=auto`.

4. The two URLs are:

   ```text
   Studio UI:   https://<POD_ID>-5117.proxy.runpod.net
   Compute API: https://<POD_ID>-8080.proxy.runpod.net
   ```

5. In Studio **Settings → Providers → SharpMind Compute**, add a Remote host
   with the API URL and token, set the Studio URL to the `5117` URL, then press
   **Test**. **System → Connection → Use a Compute host** fills the same address.

## GPU plugin

`/health` on the API shows what loaded:

```json
"accelerators": [
  { "name": "ilgpu", "inference": true, "backend": "CUDA · cuBLAS" }
],
"acceleratorReason": "Inference on ilgpu (CUDA · cuBLAS)."
```

- `SHARPMIND_INFERENCE_ACCELERATOR=auto` tries the plugin for chat; an
  unsupported model shape falls back to CPU and the reason appears in `/health`.
- `SHARPMIND_ACCELERATOR=auto` makes training jobs use the plugin when they do
  not name an accelerator. An explicit job accelerator is never overridden.
- The plugin only supports RMSNorm + RoPE/NoPE + gated-FFN models. A GPT-2 /
  LayerNorm / dense-FFN model stays on the CPU by design; train with the
  **LLaMA** or **Qwen** preset to exercise the GPU.
- The NVIDIA driver is mounted by the container runtime. The image only carries
  cuBLAS/cudart; `libcuda.so.1` must come from the host.

## Notes

- The token is required for every API call; `/health` is open.
- The Studio UI and API are separate ports; both must be exposed.
- Data, models, chats, jobs and training output live under `/data/SharpMind`.
  Keep the volume attached to survive a pod restart.
- The Runpod HTTP proxy has a 100-second response timeout; training jobs return
  `202` and are polled, so they are unaffected.
