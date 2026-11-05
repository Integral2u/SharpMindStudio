# Docker deployment

| Image | What it is | Status |
|---|---|---|
| `docker/server` | Combined SharpMind Server: Studio UI (5117) + Compute API (8080), CPU or GPU | Preview; local-source builds tested |
| `docker/studio` | Studio web app only | Preview; local-source build works, package build after NuGet publish |
| `docker/compute` | Compute API only | Preview; local Docker and Runpod instructions included |

All are plain Docker images, so they run on local Docker or any host that
provides a Docker daemon. The combined `server` image is the one to use for a
deployable SharpMind host; the split images remain useful for a Studio-only
box and for API-only GPU workers.

## Where to run them

| Host | Good for | Notes |
|---|---|---|
| Local Docker | Development and small experiments | Uses the local CPU/GPU; easiest way to test the image lifecycle |
| Runpod | GPU inference and training | GPU containers are the natural fit; mount a network volume at `/data/SharpMind` |
| Vultr | Small cloud hosts / GPU instances | Standard Docker host; choose a GPU plan for training |
| Oracle Cloud Always Free | Studio web UI / small CPU inference | Free tier is Arm CPU, not GPU; fine for Studio, not for GPU training |
| AWS / AWS Free Tier | Studio web UI / small CPU inference | Free tier is small CPU; GPU instances are paid |
| SnapDeploy | Any platform that runs a Docker image | Works as a host if Docker and persistent storage are available |

The image does not depend on the provider. The provider only supplies the
machine, the Docker daemon, ports and persistent storage.

## Persistent data

Mount `/data/SharpMind` to a volume that survives the container. That directory
holds models, corpora, chats, datasets, checkpoints, recipes and run history.
On a temporary or GPU host, **download the work before teardown** — the
container filesystem is scratch space.

## Ports and security

- `5117` — Studio web UI. Put this behind HTTPS/reverse proxy if it is public.
- `5118` — LAN/phone listener. Do not expose this to the public internet without
  the Studio pairing and gateway controls.
- `8080` — Compute API. Every `/v1` call needs `Authorization: Bearer
  $SHARPMIND_TOKEN`; `/health` is open.

For a remote Studio host, pair the browser once and use TLS. For a Compute host,
use a strong per-session bearer token and TLS; prompts should not be logged by
default. The combined image does both behind the same volume.

## GPU

`docker/server/Dockerfile.gpu.local` publishes `SharpMind.GPU` into
`/app/plugins` and copies the CUDA user-space libraries. The driver
(`libcuda.so.1`) comes from the host container runtime.

- `SHARPMIND_INFERENCE_ACCELERATOR=auto` tries the plugin for chat and falls
  back to CPU when the model shape or device is unsupported.
- `SHARPMIND_ACCELERATOR=auto` makes training use the plugin when the job does
  not name an accelerator.
- `/health` reports the plugin, its backend (`CUDA · cuBLAS` / `OpenCL`) and the
  reason for any CPU fallback.
- The plugin supports RMSNorm + RoPE/NoPE + gated-FFN models. Train with the
  LLaMA or Qwen preset to exercise it.

## Build order

1. For public/CI images, publish the six SharpMind 1.1.0 packages.
2. Build `docker/studio` or the combined `docker/server`.
3. Build `docker/compute` for API-only workers.
4. Test the CPU image first, then the GPU image on a GPU host such as Runpod or
   Vultr. The GPU image also runs on CPU and reports why it fell back.

Local-source builds do not need the packages published:

```powershell
cd C:\Integral2u\source\repos
Copy-Item SharpMind.Studio\docker\local-context.dockerignore .dockerignore
docker build -f SharpMind.Studio/docker/server/Dockerfile.local -t sharpmind-server:local .
docker build -f SharpMind.Studio/docker/server/Dockerfile.gpu.local -t sharpmind-server:gpu .
```
