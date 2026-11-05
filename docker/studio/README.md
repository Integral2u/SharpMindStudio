# Studio in Docker

This image runs the **Studio web app**, not the separate SharpMind Compute host.

## Build

The project references the SharpMind NuGet packages, so publish the 1.1.0
packages (or point the build at a local feed) before building the image.

```powershell
docker build -f docker/studio/Dockerfile -t sharpmind-studio:preview .
```

## Run

```powershell
docker run --rm -p 5117:5117 -p 5118:5118 `
  -v "${PWD}/data:/data/SharpMind" `
  sharpmind-studio:preview
```

Or from this folder:

```powershell
docker compose up --build
```

Data, models, chats, datasets and training output live under the mounted
`/data/SharpMind` volume. Do not keep anything only inside the container.

- Web UI: `http://127.0.0.1:5117`
- LAN/phone listener: port `5118`

## Notes

- The desktop shell (`SharpMind.Studio.UI`) is Windows/WebView2 only and is not
  part of this image.
- The future SharpMind Compute host will be a separate image with its own
  inference and training API. This Studio image is for the local-first web UI.
- A GPU is not required for the Studio web UI; model speed is a separate concern.
