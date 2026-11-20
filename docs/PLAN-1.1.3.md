# Plan — 1.1.3 and later: hosted base, gateway and tiers

Status: planned, not started. Trigger: the Windows Store release (1.1.2) has
produced a user base of roughly 100 accounts. Nothing here ships before then,
and nothing here belongs in the 1.1.2 package.

## Why a base first

The first useful hosted thing is not model routing. It is an **account that owns
devices**, so a phone can find its Studio without port forwarding, DDNS or
re-pairing after an IP change. That is valuable with no model gateway at all,
and it gives the later tiers an identity and a quota hook they can bill against.

## Phase 1 — Base (target: 1.1.3)

Scope: accounts, devices, pairing, relay. Free. No Stripe.

- **Account:** email magic link first, passkeys later. No password reset flow.
- **Devices:** desktop and phone register under the account.
- **Pairing:** the phone lists the account's desktops, selects one, the desktop
  approves, the phone gets a short-lived session token. Pairing is by account,
  never by IP.
- **Relay:** Studio keeps an outbound WebSocket to the control plane; the phone
  talks to the control plane and frames are forwarded to the desktop. No inbound
  port on the user's machine, works behind CGNAT.
- **Privacy:** forward opaque frames with keys exchanged between phone and
  desktop. The service is a switchboard, not a reader. Store no prompts, no
  chat history, no model metadata.

### Data model

| Table | Holds |
|---|---|
| `users` | email, created, status |
| `devices` | user, name, platform, public key, last seen |
| `pairings` | user, phone device, desktop device, approved at, revoked at |
| `sessions` | short-lived relay tokens, expiry |

Four tables, nothing else.

### Stack and cost

- ASP.NET Core, Postgres, Caddy, Docker Compose.
- One Vultr Cloud Compute instance, 2 vCPU / 4 GB, London: about $24/month.
- Postgres on the same host until a restart would cost money.
- Nightly `pg_dump` to Vultr Object Storage: about $5/month.
- Firewall: 443/80 open, 22 restricted, nothing else.
- `api.integral2u.com` points here. Reserve the subdomain now.

### Deliberately excluded

No model routing, no provider keys, no quotas, no Stripe, no chat storage, no
hosted inference. The legal surface stays "account and relay".

## Phase 2 — White-label gateway (router of routers)

Only after the base has users.

- Provider adapters behind one OpenAI-compatible endpoint.
- Per-account quotas, per-provider fallback, allowlisted models.
- **Provider terms decide what can be fronted.** Several free tiers prohibit
  resale or proxying; the adapter list is a legal list, not a technical one, and
  it will change. One provider disappearing overnight must be a routing change,
  not a rewrite.
- Prompts pass through; log metadata only.

## Phase 3 — Shared hosted inference

- A shared GPU with a queue, never a per-user instance. A dedicated instance is
  £100+/month per user and will not sell.
- Priced by tokens or requests against the account quota from phase 1.
- Rent the GPU only once the gateway is profitable.

## Phase 4 — Corporate

- Per-tenant LoRA adapters on the shared hardware.
- Per-seat pricing, a DPA, and retention options.

## Tiers

| Tier | Price | Includes | Cost to service |
|---|---|---|---|
| Free | £0 | Account, pairing, BYO keys, ~100 gateway requests/day, allowlisted small models | ~£0 |
| Plus | £4/mo or £40/yr | ~2,000/day, priority routing, 3 devices, fallback | upstream free, bandwidth |
| Pro | £10/mo | Paid-model passthrough at cost + 10%, 5 devices | pass-through |
| Corporate | quote | Per-seat, optional LoRA hosting, private model | per instance |

Break-even at £4/mo: Stripe takes about 1.5% + 20p, netting roughly £3.74, so a
$30/month host needs **eight subscribers**. At £10/mo it is three to four. Annual
pricing at two months free is worth offering — it front-loads cash and cuts churn.

## Distribution

- The hosted client is a **private repository** built to a DLL, added to Studio by
  a manual reference and included in the MSIX, not published as a NuGet package.
- The DLL holds only the client: auth, device registration, quota headers, licence
  state. Provider keys and gateway logic stay server-side.
- Keep an offline grace period so a network blip never locks a paying user out of
  their own desktop app.

## Compliance

- UK VAT on digital services via Stripe Tax.
- Privacy policy must state that prompts pass through and are not logged.
- A DPA for corporate customers.
- Per-account rate limits, device caps and a kill switch from day one — a free
  tier plus a relay attracts scrapers.

## Open decisions

1. Magic link or password for the base (recommendation: magic link).
2. Whether the relay is end-to-end encrypted from day one (recommendation: yes).
3. Whether the base is free forever or becomes the Free tier of the paid ladder
   (recommendation: Free tier, same account).
