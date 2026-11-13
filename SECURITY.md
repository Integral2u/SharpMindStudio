# Security Policy

## Reporting a vulnerability

Please report security issues privately rather than opening a public issue:

- Preferred: [GitHub private security advisory](https://github.com/Integral2u/SharpMindStudio/security/advisories/new)
- Alternative: open an issue titled only "Security contact requested" with no details, and a maintainer will arrange a private channel.

Include the affected version or commit, steps to reproduce, and the impact you observed. This is a small project without a formal SLA; expect an initial response within a few days.

## Supported versions

Only the latest release and `main` are supported. Fixes land as new builds rather than backports.

## How Studio is expected to be run

- The desktop listener is `http://127.0.0.1:5117`. Loopback peers are treated as the signed-in desktop user.
- The LAN listener (`5118`) is bound but returns 404 until pairing is enabled. Paired devices are limited to Chat and Work.
- Do not expose the admin port directly to the internet. Use pairing, a private tunnel, or the Compute host's token-protected API instead.
- Cross-site browser requests to `/gateway` and `/_blazor` are rejected using Fetch Metadata. Non-browser clients do not send those headers and are unaffected.
- Host filtering is intentionally unrestricted in 1.1.1 so LAN and container deployments keep working. A same-origin page that DNS-rebinds to `127.0.0.1` is a known residual risk; do not browse untrusted domains on a machine hosting an unlocked Studio instance.

## Security-relevant design

### Secrets at rest

OpenRouter, HuggingFace, Compute/SSH and Gateway secrets are encrypted with AES-GCM. The key file lives next to the data; on Windows it is wrapped with DPAPI (`CurrentUser`), so copying the profile to another account or machine does not expose the secrets. Backups therefore restore only as the same Windows user on the same machine. Linux/Docker keeps the raw key with owner-only permissions.

### SSH host verification

The SSH setup helper pins the host key on first connect and refuses to connect
when the key changes. The SHA-256 fingerprint is stored in
`<data root>/Compute/ssh-known-hosts.json`. If a host is rebuilt or replaced,
press **Forget host key** in Settings → Providers before connecting again.
Deleting that file forgets every pinned host. This protects the setup and
container-control channel from a network-position attacker presenting a
different host key; it is not a substitute for checking a provider-supplied
fingerprint on first use when one is available.

### Work web access

`FetchUrl` only connects to public unicast addresses. Loopback, private, link-local, carrier-grade NAT, unique-local, multicast, documentation and cloud metadata ranges are blocked in a `SocketsHttpHandler.ConnectCallback`; redirects are followed manually and revalidated; the system proxy is ignored; responses are streamed with a 512 KB cap and a 20 second budget. The project's Web permission still gates every call.

### Installer and updates

The 1.1.1 MSI is not code-signed, so Windows SmartScreen shows an unknown publisher. Verify the SHA-256 published with the release. Updates are manual in 1.1.1; a signed installer is a prerequisite for automatic updates.

### Models, plugins and containers

GGUF files are untrusted binary input and exercise `unsafe` kernels. Accelerator plugins are arbitrary code by design. Do not load either from a source you do not trust. The GPU image runs the Studio web UI and Compute API as the unprivileged `sharpmind` user (UID 10001) after fixing data and plugin folder ownership; set `SHARPMIND_TOKEN`, expose only the ports you need, and keep the container's data volume private.

## Out of scope

- Malware already running as the desktop user.
- Deliberately loading a malicious model or plugin.
- Denial of service from a deliberately huge local corpus or model.
