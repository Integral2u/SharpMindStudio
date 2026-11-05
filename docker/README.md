# Docker deployment

There will be two images:

| Image | What it is | Status |
|---|---|---|
| `docker/studio` | The Studio web app: chats, data, training, local models | Preview; local-source build works, package build after NuGet publish |
| `docker/compute` | A SharpMind Compute host: inference and training API | Initial scaffold; local Docker and Runpod instructions included |

Both are plain Docker images, so they run on local Docker or any host that
provides a Docker daemon.

## Where to run them

| Host | Good for | Notes |
|---|---|---|
| Local Docker | Development and small experiments | Uses the local CPU/GPU; easiest way to test the image lifecycle |
| RunPod | GPU inference and training | GPU containers are the natural fit; mount persistent storage or download artifacts before teardown |
| Vultr | Small cloud hosts / GPU instances | Standard Docker host; choose a GPU plan for training |
| Oracle Cloud Always Free | Studio web UI / small CPU inference | Free tier is Arm CPU, not GPU; fine for Studio, not for GPU training |
| AWS / AWS Free Tier | Studio web UI / small CPU inference | Free tier is small CPU; GPU instances are paid |
| SnapDeploy | Any platform that runs a Docker image | Works as a host if Docker and persistent storage are available |

The image does not depend on the provider. The provider only supplies the
machine, the Docker daemon, a port and persistent storage.

## Persistent data

Mount `/data/SharpMind` to a volume that survives the container. That directory
holds models, corpora, chats, datasets, checkpoints, recipes and run history.
On a temporary or GPU host, **download the work before teardown** — the
container filesystem is scratch space.

## Ports and security

- `5117` — Studio web UI. Put this behind HTTPS/reverse proxy if it is public.
- `5118` — LAN/phone listener. Do not expose this to the public internet without
  the Studio pairing and gateway controls.

For a remote Studio host, use a strong pairing setup and TLS. For a Compute host,
use a per-session bearer token and TLS; prompts should not be logged by default.

## Build order

1. Publish the six SharpMind 1.1.0 packages.
2. Build `docker/studio`.
3. Add the SharpMind Compute host project and `docker/compute`.
4. Test the compute image on local Docker CPU first, then a GPU host such as
   RunPod or Vultr.
