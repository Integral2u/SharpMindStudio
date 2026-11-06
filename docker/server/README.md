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

If you do not have a Runpod account, [create one here](https://runpod.io?ref=ltj4z1kb).

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

## Vultr

If you do not have an account, [create one here](https://www.vultr.com/?ref=9933964).

1. Create a Cloud Compute or GPU instance with Ubuntu in a region near you.
   Choose a GPU image that includes the NVIDIA driver for a GPU host. Open
   **22** (setup), **5117** and **8080** in the Vultr firewall.
2. Add an SSH key when you create the instance, or paste your public key into
   the instance's web console once (`/root/.ssh/authorized_keys`). If you do not
   have a key, **Edit → Create key** makes `~/.ssh/sharpmind_ed25519` on this PC
   and shows the public key to copy. A root password works too.
3. In Studio: **Settings → Providers → Compute → Add host → Vultr cloud
   server**, then **Edit**:
   - Base URL `http://<public-ip>:8080`
   - Studio URL `http://<public-ip>:5117`
   - Server image `<dockerhub-user>/sharpmind-server:gpu` (Press **Publish the
     server image** first.)
   - SSH host, user, private key path (or password); tick **GPU host** on a GPU
     plan
4. Save the host and press **Set up & deploy** on its card. The helper logs in,
   installs Docker, installs the NVIDIA container toolkit on a GPU host, pulls
   the image and starts the container with `/data/SharpMind` mounted. It then
   tests the API from Studio.

The helper cannot install the NVIDIA **driver** itself (that needs a reboot).
If `nvidia-smi` is missing it stops and says so; use a Vultr GPU image or
install the driver from the console first, then deploy again. Studio's token is
generated and saved with the host, so the container and Studio always match.

Everything is still one Docker host, so the manual commands keep working:

```bash
docker run -d --restart unless-stopped \
  -p 5117:5117 -p 8080:8080 \
  -e SHARPMIND_TOKEN=<long-random-token> \
  -v /data/SharpMind:/data/SharpMind \
  <dockerhub-user>/sharpmind-server:gpu
```

GPU training on Vultr requires a GPU plan; the CPU plans are fine for Studio and
small CPU inference. Vultr bills a stopped instance until it is destroyed.

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
- Inference supports RMSNorm or LayerNorm + RoPE/NoPE + gated or dense GELU FFN,
  so GPT-2/BERT-style models now run on the GPU too. GPU **training is LoRA
  fine-tuning only** (M1): it requires a LLaMA/Qwen-style base model with
  RMSNorm + gated FFN. From-scratch pretraining stays on the CPU (the plugin
  reports "only LoRA adapters are trainable in M1").
- The NVIDIA driver is mounted by the container runtime. The image only carries
  cuBLAS/cudart; `libcuda.so.1` must come from the host.

## Notes

- The token is required for every API call; `/health` is open.
- The Studio UI and API are separate ports; both must be exposed.
- Data, models, chats, jobs and training output live under `/data/SharpMind`.
  Keep the volume attached to survive a pod restart.
- The Runpod HTTP proxy has a 100-second response timeout; training jobs return
  `202` and are polled, so they are unaffected.
