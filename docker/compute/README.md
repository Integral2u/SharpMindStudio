# SharpMind Compute host

A small HTTP service that runs one SharpMind model and accepts inference and
training jobs. It is the first scaffold of the Cloud/Compute provider.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/health` | Open health check: model, data root, running jobs |
| `GET` | `/v1/models` | OpenAI-style model list (one model) |
| `POST` | `/v1/chat/completions` | Streaming or non-streaming chat |
| `POST` | `/v1/training/jobs` | Start a training job |
| `GET` | `/v1/training/jobs` | List jobs |
| `GET` | `/v1/training/jobs/{id}` | Job status, progress, logs |
| `POST` | `/v1/training/jobs/{id}/cancel` | Cancel a job |
| `GET` | `/v1/training/jobs/{id}/logs` | Job log |
| `GET` | `/v1/training/jobs/{id}/artifacts` | Export/adapter paths |

All `/v1` endpoints require `Authorization: Bearer $SHARPMIND_TOKEN`.
`/health` is open.

## Environment

| Variable | Purpose |
|---|---|
| `SHARPMIND_TOKEN` | Required bearer token unless `SHARPMIND_ALLOW_ANONYMOUS=true` |
| `SHARPMIND_MODEL` | Path to a `.smm`/`.gguf` model |
| `SHARPMIND_MODELS_DIR` | Folder used to auto-pick a model when `SHARPMIND_MODEL` is unset |
| `SHARPMIND_DATA` | Root for models, data, recipes and run history (default `/data/SharpMind`) |
| `SHARPMIND_PLUGINS` | Plugin/accelerator folder (default `/app/plugins`) |
| `ASPNETCORE_URLS` | Bind address; the image uses `http://0.0.0.0:8080` |

## Build and test locally

The local build uses the sibling SharpMind source, so nothing needs publishing:

```powershell
cd C:\Integral2u\source\repos
docker build -f SharpMind.Studio/docker/compute/Dockerfile.local `
  -t sharpmind-compute:local .

docker run --rm -p 8080:8080 `
  -e SHARPMIND_TOKEN=change-me `
  -e SHARPMIND_MODEL=/data/SharpMind/Models/your-model.smm `
  -v C:\Users\you\SharpMind:/data/SharpMind `
  sharpmind-compute:local
```

Check it:

```powershell
curl http://127.0.0.1:8080/health
curl -H "Authorization: Bearer change-me" http://127.0.0.1:8080/v1/models
```

## Push and run on Runpod

1. Push the image to Docker Hub or another registry:

```powershell
docker login
docker tag sharpmind-compute:local <dockerhub-user>/sharpmind-compute:preview
docker push <dockerhub-user>/sharpmind-compute:preview
```

2. In Runpod, create a **Pod** (GPU if you want GPU inference/training), choose
   **Custom Image**, and enter `<dockerhub-user>/sharpmind-compute:preview`.
3. Expose HTTP port **8080** (Edit Template → Expose HTTP Ports).
4. Add environment variables:
   - `SHARPMIND_TOKEN=...`
   - `SHARPMIND_MODEL=/data/SharpMind/Models/your-model.smm`
   - `SHARPMIND_DATA=/data/SharpMind`
5. Attach a network volume at `/data/SharpMind` (or upload a model to the
   container and keep it only for a short test).
6. Deploy. The URL is:

```text
https://<POD_ID>-8080.proxy.runpod.net
```

Example:

```powershell
curl -H "Authorization: Bearer ..." `
  https://<POD_ID>-8080.proxy.runpod.net/health
```

### Runpod proxy limits

The HTTP proxy has a **100-second** response timeout. The Compute host is
designed around that:

- chat can stream, but very long generations should use TCP exposure instead;
- training jobs return `202` immediately and run inside the pod, so you poll a
  short status endpoint instead of holding the connection.

If you need long-lived streams or WebSockets, expose a TCP port instead of the
HTTP proxy.

## Training jobs

The job request is the Studio `.smmt` payload directly:

```json
{
  "job": {
    "name": "runpod-smoke",
    "sources": [
      {
        "component": {
          "typeName": "SharpMind.Data.Sources.TextFileSource",
          "args": { "path": "/data/SharpMind/Corpus/smoke.txt", "mode": "FilePerDoc" }
        }
      }
    ],
    "totalSteps": 100,
    "exportPath": "/data/SharpMind/Training/runpod-smoke.smm"
  }
}
```

The data and export paths must exist inside the pod/volume. Upload the corpus or
point at a file already on the mounted volume.

Poll:

```powershell
curl -H "Authorization: Bearer ..." `
  https://<POD_ID>-8080.proxy.runpod.net/v1/training/jobs/<JOB_ID>
```

Artifacts are written to the paths in the job. Keep them on a persistent volume
before the pod is terminated.

## Current limits

- One model per host process.
- No tool calling yet; inference and training only.
- No dataset upload endpoint yet; use the mounted volume.
- No artifact download endpoint yet; use the Runpod volume or add one next.
- No multi-tenant isolation; run one host per user/session.
