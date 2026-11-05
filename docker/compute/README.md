# SharpMind Compute host

A small HTTP service that runs one SharpMind model and accepts inference and
training jobs. It is the first scaffold of the Cloud/Compute provider.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/health` | Open health check: model, data root, running jobs |
| `GET` | `/v1/models` | Models on disk, with the active one flagged |
| `PUT` | `/v1/models/{name}` | Upload a `.smm`/`.gguf`; `?activate=true` loads it |
| `POST` | `/v1/models/activate` | Activate a model already in the models folder |
| `POST` | `/v1/models/unload` | Unload the active model |
| `DELETE` | `/v1/models/{name}` | Delete a model from the models folder |
| `GET` | `/v1/files` | List uploaded job inputs |
| `PUT` | `/v1/files/{path}` | Upload a dataset/job input |
| `GET` | `/v1/files/{path}` | Download an uploaded job input |
| `DELETE` | `/v1/files/{path}` | Delete an uploaded job input |
| `POST` | `/v1/chat/completions` | Streaming or non-streaming chat |
| `POST` | `/v1/training/jobs` | Start a training job |
| `GET` | `/v1/training/jobs` | List jobs |
| `GET` | `/v1/training/jobs/{id}` | Job status, progress, logs |
| `POST` | `/v1/training/jobs/{id}/cancel` | Cancel a job |
| `GET` | `/v1/training/jobs/{id}/logs` | Job log |
| `GET` | `/v1/training/jobs/{id}/artifacts` | Export/adapter paths |
| `GET` | `/v1/training/jobs/{id}/download` | Download the export (`?artifact=adapter` for the adapter) |

All `/v1` endpoints require `Authorization: Bearer $SHARPMIND_TOKEN`.
`/health` is open.

Uploads and downloads are raw request/response bodies, so a large file streams
straight to disk instead of being buffered in memory.

```powershell
curl.exe -X PUT "http://127.0.0.1:8080/v1/models/tinystories.smm?activate=true" `
  -H "Authorization: Bearer change-me" `
  --data-binary "@C:\models\tinystories.smm"
```

## Environment

| Variable | Purpose |
|---|---|
| `SHARPMIND_TOKEN` | Required bearer token unless `SHARPMIND_ALLOW_ANONYMOUS=true` |
| `SHARPMIND_MODEL` | Optional path to a `.smm`/`.gguf`; if blank the host auto-picks one from the models folder |
| `SHARPMIND_MODELS_DIR` | Models folder (default `$SHARPMIND_DATA/Models`) |
| `SHARPMIND_DATA` | Root for models, uploads, recipes and run history (default `/data/SharpMind`) |
| `SHARPMIND_PLUGINS` | Plugin/accelerator folder (default `/app/plugins`) |
| `ASPNETCORE_URLS` | Bind address; the image uses `http://0.0.0.0:8080` |

## Build and test locally

The local build uses the sibling SharpMind source, so nothing needs publishing:

```powershell
cd C:\Integral2u\source\repos
Copy-Item SharpMind.Studio\docker\local-context.dockerignore .dockerignore
docker build -f SharpMind.Studio/docker/compute/Dockerfile.local `
  -t sharpmind-compute:local .

docker run --rm -p 8080:8080 `
  -e SHARPMIND_TOKEN=change-me `
  -v C:\Users\you\SharpMind:/data/SharpMind `
  sharpmind-compute:local
```

Check it:

```powershell
curl http://127.0.0.1:8080/health
curl -H "Authorization: Bearer change-me" http://127.0.0.1:8080/v1/models
```

## Push and run on Runpod

You need a Docker Hub account (free) and a Runpod account. Nothing else has to
be published first.

### 1. Build and push the image

On a machine with Docker running, from the folder that contains both `SharpMind`
and `SharpMind.Studio`:

```powershell
cd C:\Integral2u\source\repos
Copy-Item SharpMind.Studio\docker\local-context.dockerignore .dockerignore
docker build -f SharpMind.Studio/docker/compute/Dockerfile.local `
  -t <dockerhub-user>/sharpmind-compute:preview .

docker login
docker push <dockerhub-user>/sharpmind-compute:preview
```

### 2. Create a network volume (optional but recommended)

Dashboard → **Storage** → **Network volumes** → **New volume**. Pick the same
region you will use for the GPU. Size it for the models you want to run; even
20 GB is enough for several small GGUF/SMM models. Mount path inside the pod:
`/data/SharpMind`.

Skip this for a first smoke test and upload a small model instead (step 5).

### 3. Create the Pod

Dashboard → **Pods** → **Deploy**. Choose **Pods**, then:

| Field | Value |
|---|---|
| GPU | Any 16–24 GB card is a fine start (RTX 4090 / A5000 / L4) |
| Template | **Custom** / **Custom container image** |
| Container image | `<dockerhub-user>/sharpmind-compute:preview` |
| Container disk | 20 GB or more |
| Network volume | `sharpmind-data`, mounted at `/data/SharpMind` |
| Expose HTTP ports | `8080` |
| Expose TCP ports | optional; add `8080/tcp` if you want long streams |

Environment variables:

```text
SHARPMIND_TOKEN=<a long random secret>
SHARPMIND_DATA=/data/SharpMind
SHARPMIND_MODELS_DIR=/data/SharpMind/Models
SHARPMIND_PLUGINS=/app/plugins
```

You do **not** need `SHARPMIND_MODEL`: if it is blank the host picks a model from
`/data/SharpMind/Models`, and you can upload and activate one at runtime.

Deploy and wait for the pod to show **Running**.

### 4. Find the URL

Open the pod → **Connect** → **HTTP Service** (or the Connect panel's port
`8080` link). It looks like:

```text
https://<POD_ID>-8080.proxy.runpod.net
```

Check it:

```powershell
curl.exe https://<POD_ID>-8080.proxy.runpod.net/health
```

`modelConfigured` is `false` until a model is uploaded or placed on the volume.
That is expected.

### 5. Upload and activate a model

```powershell
$base = "https://<POD_ID>-8080.proxy.runpod.net"
$auth = "Authorization: Bearer <SHARPMIND_TOKEN>"

curl.exe -X PUT "$base/v1/models/tinystories.smm?activate=true" `
  -H $auth `
  --data-binary "@C:\Users\you\SharpMind\Training\tinystories-large-10000\tinystories-large-10000.smm"

curl.exe -H $auth "$base/v1/models"
```

A small model uploads in seconds. Large models should go on the network volume
instead, or be uploaded with a TCP-exposed port, because the HTTP proxy has a
100-second response timeout.

Test a chat:

```powershell
curl.exe -X POST "$base/v1/chat/completions" `
  -H $auth -H "Content-Type: application/json" `
  -d '{\"messages\":[{\"role\":\"user\",\"content\":\"Once upon a time\"}],\"max_tokens\":32}'
```

### 6. Upload a dataset and train

```powershell
curl.exe -X PUT "$base/v1/files/datasets/smoke.txt" `
  -H $auth `
  --data-binary "@C:\data\smoke.txt"
```

The response contains `fullPath` (for example
`/data/SharpMind/Files/datasets/smoke.txt`). Use it in the job request. A
relative `exportPath` is resolved under `/data/SharpMind/Training`, so you can
send a portable job:

```json
{
  "job": {
    "name": "runpod-smoke",
    "sources": [
      {
        "component": {
          "typeName": "SharpMind.Data.Sources.TextFileSource",
          "args": { "path": "/data/SharpMind/Files/datasets/smoke.txt", "mode": "FilePerDoc" }
        }
      }
    ],
    "totalSteps": 100,
    "exportPath": "runpod-smoke/out.smm",
    "tokenizerCachePath": "runpod-smoke/out.tokenizer.json"
  }
}
```

Poll, then download the export:

```powershell
curl.exe -H $auth "$base/v1/training/jobs/<JOB_ID>"
curl.exe -H $auth "$base/v1/training/jobs/<JOB_ID>/download" -o out.smm
```

### 7. Stopping

Stop the pod from the dashboard. Files on the network volume survive; anything
only on the container disk is lost. Remember that a pod bills while it is
running, even when idle.

### Runpod proxy limits

The HTTP proxy has a **100-second** response timeout. The Compute host is
designed around that:

- chat can stream, but very long generations should use TCP exposure instead;
- training jobs return `202` immediately and run inside the pod, so you poll a
  short status endpoint instead of holding the connection;
- large uploads/downloads should use the network volume or a TCP port.

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

- One model per host process; switching models unloads the previous one.
- No tool calling yet; inference and training only.
- No multi-tenant isolation; run one host per user/session.
- Uploads live in `/data/SharpMind/Files`; keep the network volume attached so
  they survive a pod restart.
- Artifacts are served while the job is in memory; download them before
  stopping the pod, or keep the volume.
- Uploading or downloading over the Runpod HTTP proxy is subject to its
  100-second timeout; use a network volume or a TCP port for large files.
