# Self-hosting a Febris node

This is the whole operator story. What you need, how to bring a node up, what it talks to
(nothing external, by design), and how to deploy the client suite through it.

If you only want it running, read [Quickstart](#quickstart). Everything after that is
operational detail.

## What you are deploying, and what you are not

A **node** is the delivery tier of an XR training platform. It holds your learners, your training
modules and the xAPI records their sessions produce, it serves the desktop and Android clients, and
it is the system of record for all of it. One node is a complete product.

What it is **not** is half of something. There is a central hub in the wider Febris product, and it
is not part of this. It is not published, it is not required, and a node never phones one. The
federation seam exists in the code and ships disabled, described under
[Hub federation](#hub-federation), and a node with it off is complete rather than degraded. If you
are looking for the part that talks to Febris, there isn't one.

You need no Febris account, no licence key and no network service we run.

---

## Prerequisites

| You need | Why | Checked by |
|---|---|---|
| **Docker + Compose v2** | the node ships as five containers | `docker --version` and `docker compose version` |
| **openssl** | `selfhost/generate-env.sh` generates your secrets with it and exits if it is missing | `command -v openssl` |
| **POSIX shell (bash)** | `generate-env.sh` is a bash script | `bash --version` |
| **.NET 8 SDK** | only if you want to build or test outside Docker. The compose path does not need it | `dotnet --version` |

### Hosts

| Host | Status |
|---|---|
| Linux x86-64 with Docker Engine 24+ and Compose v2 | **Supported.** This is what the stack is built and tested on |
| Linux on arm64 | Untested. The base images publish arm64 variants, so it may work. Nobody has verified it |
| Windows or macOS with Docker Desktop | Untested. Expected to work, and not something to discover in production |
| Bare metal, no Docker | Best-effort, see [Running without Docker](#running-without-docker). Not covered by the smoke script |

Anything marked untested means exactly that. It is not a warning that it fails, and it is not a
promise that it works.

### Resources

| | Minimum | Comfortable |
|---|---|---|
| vCPU | 2 | 4 |
| RAM | 4 GB | 8 GB |
| Disk | 20 GB | depends entirely on your media, see below |

Five containers, two of them databases, so the floor is about memory rather than CPU. Disk is the
number that will surprise you. The 20 GB minimum covers images, the databases and room to work. It
does **not** account for training module archives or session video, which are the things that
actually grow, and which live in the `storage` volume. Size that against your own content and watch
it, because a full disk stops Postgres writing.

### Ports

| Port | Bound to | What it is |
|---|---|---|
| `${NODE_HTTPS_PORT}`, default 8443 | all interfaces | HTTPS, via the bundled Caddy. Two vhosts share it. The portal on `https://febris.localhost:${NODE_HTTPS_PORT}` and the API on `https://api.febris.localhost:${NODE_HTTPS_PORT}`. The API vhost is the one a device wants |
| `${NODE_API_HTTP_PORT}`, default 8081 | `127.0.0.1` only, by default | the API's plain HTTP port. Health probes use it. Change `NODE_API_HTTP_BIND` to expose it, and understand why before you do |

Nothing else is published. Postgres and Valkey are reachable only from the compose network, not
from the host, which is deliberate.

---

## Quickstart

```sh
git clone https://github.com/Febris-XR/Febris_Node.git febris-node && cd febris-node
./selfhost/generate-env.sh          # writes .env with fresh secrets, chmod 600
docker compose up -d --build        # first build takes a few minutes
```

Then open **https://febris.localhost:8443**, from the machine running Docker.

`febris.localhost` resolves to loopback, so it means "this machine" on whichever machine the
browser is on. That matters because a headless Linux server is the host this guide recommends,
and it has no browser. Two ways round it. Tunnel the port to a machine that does have one, with
`ssh -L 8443:127.0.0.1:8443 you@server`, then open the same URL on your laptop. Or give the node
a real hostname and follow [TLS](#tls-and-reverse-proxying), which is what a deployment you keep
should do anyway.

The API is on its own vhost at the same port, `https://api.febris.localhost:8443`. That is the
URL a device wants, not the portal one.

`generate-env.sh` prints your first-login credentials and stores them in `.env`. Log in with
`NODE_ADMIN_EMAIL` / `NODE_ADMIN_PASSWORD` and **change the password immediately** -- it was
generated on your machine, but it is sitting in a file.

It refuses to run twice. If `.env` already exists it exits non-zero unless you pass `--force`,
which rewrites every generated value in it. That rotates `POSTGRES_PASSWORD` and
`NODE_JWT_SECRET`, the ones an already-provisioned `pgdata` volume and every issued device token
still depend on, and it rotates `NODE_ADMIN_PASSWORD` too. The new admin password is written to
the file but **not applied to an account that already exists**, because the seed runs once and
skips when the bootstrap admin is already there. So on a node that has been up, `--force` does
not reset your login. It replaces the copy in the file with one that no longer opens anything.

### Verifying it came up

```sh
./selfhost/smoke.sh
```

It asserts all five services are running, that liveness and readiness both answer, that the portal
responds through the proxy, and that the proxy is still refusing `/health/*` from outside. It exits
non-zero if any of that fails, which is why every procedure in this document ends with it. Ports
come from your `.env`, so it follows whatever you configured.

By hand, the two commands it wraps are:

```sh
curl http://127.0.0.1:8081/health/ready                 # {"status":"Healthy","totalDurationMs":7}
docker compose ps                                       # five: postgres, valkey, node-api, node-portal healthy; proxy up
```

**If checks 4 and 5 fail with `HTTP 000` while 1 to 3 pass, the node is fine and your resolver is
the problem.** Those two checks reach the portal by name, and `curl` uses the system resolver
rather than a browser's. Browsers synthesize `*.localhost` to loopback on their own. A system
resolver may not, and plain glibc does not. Add this line to `/etc/hosts` on the Docker host and
re-run:

```
127.0.0.1 febris.localhost api.febris.localhost
```

`/health/ready` answers `Healthy` only when every dependency it checks is healthy, so that one
field is a complete readiness answer.

**It does not tell you WHICH dependency failed, by default.** The per-check breakdown names every
registered check, which tells an unauthenticated caller which databases this node owns and whether
Redis and hub federation are configured -- an inventory of your deployment, on an endpoint that has
to stay anonymous. Turning the breakdown on is a `docker-compose.yml` edit, not a `.env` one. Compose reads `.env`
only to substitute `${VAR}` inside the compose file, so a key that no service references reaches
no container and setting it there does nothing at all. Add `HealthChecks__DetailedResponse: "true"`
to the `&node-environment` block, `docker compose up -d`, and prefer taking it back out once you
have your answer.

**The probes are host-local, not public.** This used to say
`curl -k https://febris.localhost:8443/health/ready`, through the bundled proxy. The proxy now
returns 404 for `/health/*` on both vhosts, because the readiness body names every registered check
and would let an unauthenticated caller enumerate which databases the node owns, whether Redis is
configured and whether hub federation is on. That costs nothing operationally, because the
container healthchecks run inside the containers and the API publishes `127.0.0.1:8081` for
exactly this. If you need the probes from elsewhere, put them behind your own authentication
rather than reopening the path.

The TLS certificate is self-signed by the bundled Caddy proxy, which is why `curl` needs `-k`
and your browser will warn once. For a real deployment see [TLS](#tls-and-reverse-proxying).

---

## What is actually running

| Service | Image | Role |
|---|---|---|
| `postgres` | `postgres:16-alpine` | four databases: user, data, xapi, analytics |
| `valkey` | `valkey/valkey:8-alpine` | session and token state (Redis-protocol drop-in) |
| `node-api` | built here | the API: xAPI ingest, artifact store, identity |
| `node-portal` | built here | the web UI |
| `proxy` | `caddy:2-alpine` | TLS termination and routing |

State lives in six named volumes. They are `pgdata`, `valkeydata`, `storage` (uploaded
artifacts), `keys` (the DataProtection key ring), `caddydata` and `caddyconfig`.

`docker compose down` keeps all six. `docker compose down -v` destroys them, including every
package your catalogue has synced.

Three of the six carry state you would miss. `pgdata`, which a database dump reproduces, and
`storage` and `keys`, which are archived directly. [Backups](#backups) covers all three, and says
why the other three need nothing.

### Configuration

Everything is environment variables, read from `.env`. The ones you are likely to touch:

| Variable | Default | Notes |
|---|---|---|
| `NODE_HTTPS_PORT` | `8443` | the port the portal is served on |
| `NODE_API_HTTP_PORT` | `8081` | plain-HTTP API port, for host-local health probes. The portal is deliberately **not** served over it -- its auth cookie is HTTPS-only |
| `NODE_API_HTTP_BIND` | `127.0.0.1` | which interface that port binds to. **Loopback by default**: the API carries device tokens, the hardware credential and learner records, so publishing it to the LAN sends all of it in cleartext. Point devices at the HTTPS API host instead. `0.0.0.0` opts back in, for a device that genuinely cannot trust the local CA, on a network you control |
| `NODE_ADMIN_EMAIL` | generated | the bootstrap administrator |
| `NODE_ADMIN_PASSWORD` | generated | blank is valid -- see below |
| `NODE_AUTO_PROVISION_JIT` | `false` | forwarded to the portal as `Identity__Registration__AutoProvisionJit`. `true` lets an unknown external-IdP user be provisioned an account on first login. Closed by default, and inert until you actually register an SSO provider |
| `SMTP_HOST` / `SMTP_PORT` / `SMTP_SENDER` / `SMTP_PASSWORD` | placeholder | outbound mail. Nothing is sent until you set these |
| `POSTGRES_PASSWORD`, `NODE_JWT_SECRET` | generated | rotate by editing `.env` and recreating the containers |

**Two settings people look for here are not in that table, and not in `.env` at all.**
`ClientDownloads:BaseUrl` decides where the Software Repository pages send you when this node
holds no copy of a package, which is every node on its first boot. It defaults to
`https://www.febr.is`, it is consulted only when the catalogue is empty so a package you sync
always wins, and rendering the link is not a network call. To blank it on an air-gapped node, add
`ClientDownloads__BaseUrl: ""` to the `&node-environment` block in `docker-compose.yml`. Putting
it in `.env` does nothing at all, for the reason given under
[Verifying it came up](#verifying-it-came-up). The `PackageFeed` settings below work the same way.

**On the bootstrap admin.** Those two variables choose between three first-login paths, and the
one you get by copying `.env.example` unedited is the third.

- **Both set.** The account is created with that password on first boot. This is what
  `generate-env.sh` does, so the quickstart above lands here.
- **Email set, password blank.** The account is created with no password, and the only way in is
  the forgot-password flow, which needs SMTP that a fresh node has not configured. On a node
  without mail there is then no way to log in at all. Avoid this combination.
- **Both blank, as `.env.example` ships.** No account is seeded. The node mints a one-time setup
  token, prints it, and you claim the node at `/setup` within sixty minutes. No SMTP needed.

If you wrote your own `.env` and left both blank, read the token off the container log:

```sh
docker compose logs node-portal | grep -A4 'FEBRIS NODE IS UNCLAIMED'
```

The token goes to stdout only and is deliberately never handed to the structured logger, so it
does not appear in any log file you ship somewhere else.

A password you set by hand must be at least 8 characters with an upper, a lower and a digit. A
symbol is optional. One that fails the policy is **rejected** and the node boots with no admin
account at all.

### Adding users

Self-registration ships closed -- `Identity.Registration.Mode` is `AdminOnly` and
`AutoProvisionJit` is `false` -- so nobody signs themselves up. You add your team from the
portal, signed in as an admin:

| Page | For |
|---|---|
| `/User/Create` | one account at a time |
| `/User/BulkCreate` | paste a spreadsheet, or upload a CSV |

Both are gated on the admin/educator roles rather than on the registration policy, so closing
self-registration does not close them.

**These need SMTP.** An admin-created account is given a randomly generated password that is
never shown to you and never sent to the user. The only way in is a link by email, either the
confirmation mail on single create, the forgot-password flow otherwise. Bulk create sends no
mail at all, so those accounts are reachable only through forgot-password. With `SMTP_HOST`
blank nothing is delivered, and you have made accounts nobody can ever log into. Configure SMTP
*before* you provision users.

---

## Deploy the client suite through your node

A node is not only an LMS -- it is the distribution point for the Febris client software. This
is the supported path for getting the mobile suite onto devices, and for a single-headset owner
it is the **only** path, because the Companion app is served by a node. An individual owner runs
their own node exactly as an organisation does.

The whole operator side lives on the **portal**, behind the same signed-in cookie identity and
role gates as every other admin action. There is no API credential to mint and no curl sequence:
the NodeAdmin bearer token that earlier revisions of this page documented (one hour, no refresh,
no revocation) was deleted when the writes moved into the portal, because it existed solely to
let a human reach API-side write routes that no longer exist.

Who talks to what:

| Surface | Auth | Purpose |
|---|---|---|
| Portal -> Software Repository pages | signed-in cookie, educator or admin | download, documentation, archive per platform |
| Portal -> **System -> Node -> Package Feed** | signed-in cookie, **admin only** | pull packages from a release feed |
| `api/CompanionApp/GetLatestVersion` + `Download` | device token | the mobile Server fetches the Companion APK |
| `api/Module/*` | device token | module catalogue + entitlement-gated module delivery |

A device identity cannot put anything into the catalogue, since the writes are portal-only. An
operator does not need a device identity to read it, because the portal serves the same store.

**A feed sync is the only way a CLIENT package gets into this catalogue.** There is no upload form
for one. An earlier ingest route existed and was removed, so if you find an older document
offering you a file picker for a client build, it is out of date. Everything below follows from
that, including the air-gapped case, which is handled by hosting the manifest yourself rather than
by uploading by hand.

**Training modules are a different catalogue and they do have an upload form.** They are your
content rather than our binaries, so there is no feed to sync them from. You add them at
**Content -> Add** in the portal navigation, which takes the archive directly. Nothing in this
section applies to them.

A node whose catalogue is empty is not broken, and that is every node on its first boot. The
Software Repository pages then link out to the project's own download page instead of showing a
dead end. That is a rendered link and not a network call. The node requests nothing, and only your
browser travels, and only if you click. Blank `ClientDownloads:BaseUrl` to switch it off and get
the plain empty state, which is what an air-gapped site wants.

### (a) Pull from a release feed, the only way in

**System -> Node -> Package Feed** on the portal. Point it at the manifest URL of whichever feed
you trust (HTTPS only, and an air-gapped node can point at a manifest served on its own network),
pick a channel, and run it. The form defaults to **dry run**, which is the recommended first
pass, because it produces the same per-package report and changes nothing. Untick it for the real
thing. The report renders inline, counting ingested, already current, filtered, refused and
failed, one
row per package with its reason.

The sync is deliberately conservative:

- **checksum-refusal** -- a package failing `sha256` is rejected, not stored
- **oldest-first** -- versions are applied in order, so a catalogue cannot skip a release
- **never-overwrite** -- an existing version is never silently replaced

One honest limit. The feed format carries a `signerSha256` per payload, the digest of the APK
signing certificate, the only field that speaks to *origin* rather than integrity -- but the node
does not pin or enforce it. Only the artifact checksum is verified.

The manifest is a static JSON document you host yourself. There is no distribution service to
operate. `schemaVersion` must be `1`, and an entry is skipped if its `channel` is not the one
you asked for, if it is `obsolete`, or if it lists `consumers` that exclude `node`:

```jsonc
{
  "schemaVersion": 1,
  "generated": "2026-08-01T00:00:00Z",
  "packages": [
    {
      "uuid": "...stable release identity, never reused...",
      "kind": "AndroidMobileServer",
      "kindId": 200,
      "name": "Febris Mobile Server",
      "version": "1.2.0",
      "versionCode": 10200,
      "channel": "stable",
      "consumers": ["human", "node"],
      "description": "...",
      "packageName": "com.example.febris.server",
      "minSdk": 26,
      "targetSdk": 34,
      "obsolete": false,
      "artifact": {
        "fileName": "febris-server-1.2.0.zip",
        "url": "https://.../febris-server-1.2.0.zip",
        "sizeBytes": 41234567,
        "sha256": "...lowercase hex sha-256 of the zip as served..."
      },
      "contains": [
        { "fileName": "server.apk", "sha256": "...", "signerSha256": "..." }
      ]
    }
  ]
}
```

`kind` and `kindId` are redundant on purpose. A disagreement is a fatal error for that entry,
not something the node resolves in favour of one side. `versionCode` is what the sync orders by.

**This also runs on a schedule.** A background service in the portal syncs on an interval, so the
form above is for the first run and for when you do not want to wait. Three settings drive it, and
like every other outbound destination it is off until you give it a URL.

| Setting | Default | Notes |
|---|---|---|
| `PackageFeed:Url` | blank | the manifest URL. Blank means the scheduler never runs |
| `PackageFeed:Channel` | `stable` | entries on other channels are skipped |
| `PackageFeed:IntervalHours` | `24` | anything under one hour is clamped up to one hour, with a warning |

The scheduled run is never a dry run, so prove the URL with the form's dry run first, then set
the URL once you are happy with the report.

**These three are `docker-compose.yml` edits, not `.env` ones.** Add `PackageFeed__Url`,
`PackageFeed__Channel` and `PackageFeed__IntervalHours` to the `&node-environment` block and run
`docker compose up -d`. The scheduler lives in the portal, so that is the block that matters. A
`PackageFeed__Url` placed in `.env` reaches no container, and the service logs its idle line as
though you had configured nothing.

### (b) Devices pull from the catalogue

Nothing further to configure. Point the mobile Server at your node's API URL and it reads
`GetLatestVersion` / `Download` on its own schedule. The catalogue is the contract.

### (c) Day-one scope: mobile only

The kinds that work end-to-end today are **`AndroidMobileServer` (200)** and
**`AndroidMobileCompanion` (300)**.

`PC = 100`, `CSharp = 400` and `CPP = 500` are reserved values in the enum and in the manifest
format, but there is no PC-through-node delivery flow -- the PC suite installs from a zip. The
slots exist so the feed format does not need to change when that lands.

### A one-time uninstall is coming for Android

If you deploy the published **v0.2.0** Android builds to devices, read this before you do.

Those builds are signed with a debug certificate. The first release-signed build, v0.2.1, carries
a different one, and **Android refuses to update an app across a change of signing certificate**.
The failure is `INSTALL_FAILED_UPDATE_INCOMPATIBLE`, and there is no flag that works around it.
Every device holding v0.2.0 has to uninstall before it can take v0.2.1, which loses that app's
local data on the device.

This happens once. Every build from v0.2.1 onward shares one certificate, so updates after it are
ordinary updates. If you have a choice, wait for v0.2.1 rather than deploying v0.2.0 to a fleet
you would then have to visit twice.

---

## How a simulation's records reach your node

This is the path that makes a node worth running, and it has one surprise in it, so it is worth
reading before you plan an integration.

```
simulation (your code, using the Febris SDK)
    |  builds xAPI statements, hands them over as "sendable"
    v
Febris client  (PC Launcher + Statement Manager, or the Android Mobile Server)
    |  authenticates with a node-issued hardware credential
    |  1. POST /api/Token/authenticate               gets a bearer token
    |  2. POST /api/Statement/StatementInitialization
    |         opens the attempt for one learner and one module, and the
    |         node answers with the initial statement
    |  3. POST /api/Statement/Submit                 sends the rest
    v
your node  (records stored, and they stop here)
```

The middle leg is easy to miss and the whole thing depends on it. The client does not simply post
a finished pile of statements. It names the learner and the module and asks the node to open the
attempt, and the node answers with the initial statement the rest hang off. Both shipping clients
do this, and a client that only ever called submit would produce records with nothing to attach
to.

There is also `POST /api/Statement/Backup`, which both clients keep as a permissive fallback. You
do not configure it and it needs nothing from you. It is worth knowing the name exists if you go
reading your own access logs.

**On Android it is the Mobile Server that talks to your node, never the Companion.** The Companion
runs on the headset, holds no node URL and no hardware credential, and has no code that reaches a
node at all. It hands its statements to the Mobile Server over a Wi-Fi Direct peer link, and the
Server is the device you register and point at your node. That is the same direction the Companion
APK travels, from your node to the Server and on to the headset. If you are looking for somewhere
to type your node's URL, it is on the Server.

**The SDK does not talk to your node.** That is the surprise. It has no HTTP client and no endpoint
setting, and there is nothing in it to point at a server. It builds statements and exposes them
through calls like `GetSendableDispatch` and `EndSimulation`, and the Febris client that launched
the simulation is what collects and transmits them. So a simulation author never configures a node
URL, and an operator never gives one to a simulation author.

That also means there is no way to submit a statement from a simulation without a Febris client in
the loop. If you were planning to post to the node directly from your own code, the API is there
and it is authenticated per device, but that is an integration you would be building yourself
rather than a supported path.

**Where the clients come from.** Both suites are published, at v0.2.0.

| Suite | Where | Assets |
|---|---|---|
| Windows PC | [Febris_PC releases](https://github.com/Febris-XR/Febris_PC/releases) | `FebrisPCSuite-0.2.0-win-x64.msi`, and a `.zip` of the same build |
| Android | [Febris_MobileSuite releases](https://github.com/Febris-XR/Febris_MobileSuite/releases) | `febris-mobile-server-v0.2.0.apk` and `febris-mobile-companion-v0.2.0.apk`, each also as a `.zip` |

Read [A one-time uninstall is coming for Android](#a-one-time-uninstall-is-coming-for-android)
before you put the Android v0.2.0 builds on more than a couple of devices.

**Downloading them does not put them in your catalogue.** Nothing on those pages reaches your node
on its own. To serve them from your node, host a manifest that references those artifacts and point
a feed sync at it, per
[Pull from a release feed](#a-pull-from-a-release-feed-the-only-way-in). Until you do, your
Software Repository pages stay empty and link out instead.

The simulation SDKs are separate from all of this and are published too, on nuget.org and on the
Febris_SDK releases page. They are consumed by whoever writes the simulation, never installed on
the node.

### What you configure, as the operator

Very little, which is the point.

1. **Register the device yourself, before the device is ever pointed here.** There is no
   self-enrolment. A device cannot introduce itself to your node, and the node has no route that
   would let it. You create the record in the portal, at **Operations -> Hardware -> Index** and
   then the green create button on that page, and the node
   mints that device's credential at that moment.

   > **The credential is displayed once and never again.** It is stored as a hash, so nothing can
   > recover it later, not even you. Copy it when it appears. If it is lost, the only way back is
   > **Regenerate Credential** on that device, which invalidates the old one immediately and stops
   > the device authenticating until you enter the new one.

   Registration Settings is a different thing entirely, covered under
   [Adding users](#adding-users). It governs how *people* get accounts, not devices.

2. **Point the client at your node.** The client needs your node's API URL, which is
   `https://api.febris.localhost:8443` on the bundled recipe and your own API hostname on a real
   deployment, plus the credential from step 1. Everything after that is the exchange above.
3. **Nothing about xAPI itself.** There is no LRS endpoint to configure, no shared secret to
   distribute and no per-simulation setup on the node.

Records land in the node's xAPI database and stay there. See
[Outbound connections](#outbound-connections-this-node-makes) for why nothing forwards them on.

### Getting the SDKs

The SDKs are consumed by whoever writes the simulation, not installed on the node.

```sh
dotnet add package Febris.Simulation.XApiSdk
```

The C++ SDK is a Windows x64 bundle on the
[Febris_SDK releases](https://github.com/Febris-XR/Febris_SDK/releases), also available through a
vcpkg registry. Verify what you download.

```sh
sha256sum -c SHA256SUMS
```

That is what exists today, and it is worth being precise about what it proves. A checksum
fetched from the same page as the file proves the download was not corrupted in transit. It does
not prove where the file came from, because whoever could replace the file could replace the
checksum beside it.

Build provenance is the thing that closes that gap, and **no Febris release carries it yet.** The
workflow change that would attest each C++ bundle is written but not merged, so v0.1.0 has none
and a release cut today would have none either. When it lands, the check will be:

```sh
gh attestation verify febris-simulation-sdk-cpp-v<version>-win-x64.zip --repo Febris-XR/Febris_SDK
```

Running that against v0.1.0 fails, and the failure means the attestation is absent rather than
that the file is bad.

The two SDKs are held byte-identical at the same minor version by a conformance gate that runs
before the **C++** bundle is published, so a C++ simulation and a C# one produce the same records.
The C# package's own workflow does not run that gate, so parity is proven at each C++ release
rather than at every publish of either. In practice the two ship together and this has not bitten
anyone, but it is an asymmetry rather than a guarantee and you should know which one you have.

---

## Outbound connections this node makes

A default node contacts **nothing**. Every outbound destination below is opened by a setting you
choose, and each ships blank or off. That is worth knowing precisely rather than approximately if
you are deploying somewhere restricted or air-gapped.

**1. Your mail server, once you configure one.** `EmailSender:*`. Used for password resets, user
invites and account activation. With no mail configured those features do not work and there is no
other route to recover an account, so a node without SMTP effectively has no password reset. Plan
for that before you need it.

If your egress blocks OCSP or CRL lookups, leave `EmailSender:CheckCertificateRevocation` at its
default of `false`. Turning it on makes an unreachable revocation responder reject an otherwise
valid certificate, and **all** mail then fails with "the remote certificate was rejected" even
though the chain and host name are fine. Both are still validated either way.

**2. The client artifact feed, once you point at one.** `PackageFeed:Url`, blank by default, which
leaves the sync idle and the client catalogue empty. The manifest is a static JSON document, so you
may host your own copy and point at that instead. There is no distribution service to call.

**3. A hub, only if you enable federation.** Off by default, see below. When enabled the only thing
that leaves this node is a `GET` to the configured Data API base, issued by the readiness health
check to see whether the hub is reachable. No node data is sent with it.

Nothing else phones home. There is no telemetry, no update check and no licence call. A geocoding
lookup used to sit on the Location save path and is gone as of this release, so an air-gapped node
no longer needs an exception for it.

---

## Hub federation

**Off by default, and a node with it off is complete rather than degraded.** The readiness probe
reports the closed gate as `Healthy`, not `Degraded`, on purpose.

Three independent things hold it shut on a fresh install. The setting defaults to false in code,
neither `appsettings.json` carries a `HubFederation` section, and `docker-compose.yml` blanks
`LicenseKey` and both `ApiUrlPath__*` values explicitly. A node reads its own database and is its
own source of truth.

**Learner data has no route upward, structurally.** Statements, actors, earned credentials and
accounts never leave the node. The only outbound request the federation path can make is the
reachability `GET` described above. If you are evaluating this against a records-privacy
obligation, that is the property to check, and it holds with federation on as well as off.

Federation exists for Febris-operated deployments that run a central hub. There is no public hub to
point a self-hosted node at, so for a third-party deployment the switch has nothing to connect to.
It is documented here so its absence reads as a decision rather than a gap.

---

## TLS and reverse proxying

The bundled Caddy terminates TLS with a self-signed certificate so the quickstart works with no
DNS. For anything real, use your own reverse proxy and terminate there.

### If you already run a reverse proxy

This is the common case, and until 2026-08-25 the recipe did not actually support it. The portal
published no host port at all, so there was nothing for your proxy to point at and the only option
was running the bundled Caddy as a second proxy behind your first one.

Use the overlay:

```sh
export COMPOSE_FILE=docker-compose.yml:selfhost/docker-compose.byo-proxy.yml
docker compose up -d
```

That publishes the portal on `127.0.0.1:8082` and switches the bundled Caddy off. The API is
already on `127.0.0.1:8081`. Point your proxy at those two, as two hostnames -- a subpath mount is
not supported, because nothing in the app rewrites generated URLs for a path prefix.

**Set `COMPOSE_FILE` rather than passing `-f`.** The upgrade command below is a bare
`docker compose up -d --build`, which converges to the base file alone. It would restart the
bundled proxy and drop the portal port without saying anything.

The overlay's header is the full contract your proxy has to satisfy. The five that bite hardest:

| Do this | Because |
|---|---|
| Set `X-Forwarded-Proto` and `X-Forwarded-For` | The node decides "was this HTTPS" from the header. Without it, HSTS is skipped and the Secure auth cookie is never emitted, so **login cannot work**. |
| Preserve the `Host` header | nginx rewrites it by default. Every password-reset and invitation link is built from `Request.Host`, so mail goes out pointing at your upstream's internal name. |
| Overwrite `X-Real-IP`, do not pass it through | It is written straight into the analytics tables with no trust check. |
| Raise the body limit and read timeouts | Module and package uploads are far larger than a default proxy will pass. nginx caps bodies at `1m`. |
| Add your proxy to `ForwardedHeaders__KnownNetworks__1` | Only the compose subnet is trusted by default, so a proxy on the host or another machine is ignored. Use index `__1` -- `__0` is already taken. |

The app does **not** redirect HTTP to HTTPS by default, because a node behind a TLS-terminating
proxy would loop. Turn it on with `Transport__HttpsRedirection=true` only if the app itself
terminates TLS.

The `Transport` section also controls HSTS, CORS and `X-Frame-Options`:

```jsonc
"Transport": {
  "Hsts": { "Enabled": true, "MaxAgeDays": 365, "IncludeSubdomains": true, "Preload": false },
  "HttpsRedirection": false,
  "Cors": { "AllowedHosts": [ ".example.com" ], "AllowCredentials": true },
  "SecurityHeaders": { "XContentTypeOptions": true, "XXssProtection": true, "XFrameOptions": "SameOrigin" }
}
```

`Cors.AllowedHosts` is empty by default, which means only same-origin and localhost. An entry
with a leading dot (`.example.com`) matches the domain and its subdomains. Without one it is an
exact host match. Set `Preload` only once you have actually submitted to the HSTS preload list --
it is close to irreversible.

### Leave `IpRateLimiting:RealIpHeader` empty

It ships empty deliberately and should stay that way. It names an HTTP header the rate-limit
library reads to choose a bucket, and that library does **not** check whether the header came from
a proxy you trust. With it set, a caller can send a different value on every request and never be
limited. Measured at eight login attempts with a rotating `X-Real-IP`, none of them limited.

Left empty, the limiter uses the resolved `RemoteIpAddress` instead, which respects the
`KnownNetworks` trust decision above. That is the whole reason the trust decision exists, so do not
undo it here.

---

## Backups

Pick somewhere to write them that is **not** the clone. No `.gitignore` rule covers a dump, a
volume tarball or a copy of `.env` under another name, so backups written into the clone turn up
in `git status`, and an upgrade that re-clones or a stray `git clean` takes them with it.

```sh
export BACKUP_DIR=/var/backups/febris-node
mkdir -p "$BACKUP_DIR"
```

```sh
docker compose exec -T postgres pg_dumpall -U febris > "$BACKUP_DIR"/febris-$(date +%F).sql
docker run --rm -v febris-node_storage:/from -v "$BACKUP_DIR":/to alpine \
  tar czf /to/storage-$(date +%F).tar.gz -C /from .
```

**`-T` on the dump is not optional.** Without it `docker compose exec` allocates a pseudo-TTY
whenever your stdin is a terminal, which is every time you run this by hand, and the SQL comes
back with carriage returns embedded in it. The file looks perfectly normal and fails on replay.

Compose prefixes volume names with the project name, and `docker-compose.yml` pins that to
`febris-node`, so `febris-node_storage` is right no matter what you called the directory you
cloned into. If you deliberately override it with `COMPOSE_PROJECT_NAME` or `-p`, run
`docker volume ls` and use the prefix you actually have. Getting it wrong fails quietly rather
than loudly, because `docker run -v` creates a missing named volume instead of refusing, so the
command succeeds and the archive comes out empty.

Back up the `keys` volume too. The DataProtection key ring encrypts auth cookies and
at-rest settings. Losing it logs everyone out and makes encrypted settings unreadable.

```sh
docker run --rm -v febris-node_keys:/from -v "$BACKUP_DIR":/to alpine \
  tar czf /to/keys-$(date +%F).tar.gz -C /from .
```

**And back up `.env`.** It is the smallest file here and the only one you cannot regenerate.
It holds `POSTGRES_PASSWORD`, which is the password the `pgdata` volume was initialised with and
the only one that will ever open it, and `NODE_JWT_SECRET`. Without it, a dump and a pile of
volume tarballs will not come up on a replacement host, because nothing in them records what the
database password was.

```sh
cp .env "$BACKUP_DIR"/env-$(date +%F).bak
chmod 600 "$BACKUP_DIR"/env-$(date +%F).bak
```

That copy is secrets in the clear. Keep it wherever you keep secrets, which for most people is
not the same place as the tarballs.

**And back up your edits to the clone.** `.env` is not the only thing you changed. A deployment
that is not the bundled trial edits tracked files, and every one of them comes back pristine from
a fresh `git clone`. Trusting your own proxy means editing
`ForwardedHeaders__KnownNetworks__1` in `docker-compose.yml`, because that setting is hardcoded
there rather than read from `.env`. Real domains and real certificates mean editing
`selfhost/Caddyfile`. A CORS allowance means editing `appsettings.json`. Lose those and the node
comes back up refusing to log anyone in, because without a trusted proxy the Secure auth cookie
is never emitted.

```sh
git diff HEAD > "$BACKUP_DIR"/clone-edits-$(date +%F).patch
```

**Five artifacts, then.** The dump, `storage`, `keys`, `.env`, and that patch. The other four
volumes need nothing from you. `pgdata` is what the dump reproduces. `valkeydata` holds sessions
only, so losing it signs everyone out and costs nothing else. `caddyconfig` is regenerated on
boot. `caddydata` holds the self-signed CA, so keeping it saves your browsers one certificate
warning after a rebuild. If you swapped in real domains and ACME, it also holds your account key
and issued certificates, and discarding it forces a re-issuance.

**Run the dump from inside the container, as above.** `pg_dumpall` refuses to work against a server
newer than itself, and it aborts rather than producing a partial file:

```
pg_dump: error: server version: 18.4; pg_dump version: 12.3
pg_dump: error: aborting because of server version mismatch
```

The container's client tools always match the server it ships with, so `docker compose exec` sidesteps
the whole problem. If you dump from the host instead, your client must be at least the server's major
version.

A backup you have never restored is a hypothesis. Restore into a scratch database at least once
before you need it, using the drill at the end of the next section.

---

## Restoring

**Read this before you need it.** Restoring is not the reverse of `up -d` and there is no undo.

Two different situations send people here, and they start in different places.

**Rolling a running node back to an earlier state.** Start at step 1 below. The stack is already
there and you are replacing what is inside it.

**Rebuilding on a host that is gone.** Do this first, then join at step 2.

```sh
git clone https://github.com/Febris-XR/Febris_Node.git febris-node && cd febris-node
export BACKUP_DIR=/wherever/you/put/the/backups     # this shell is on a new machine
cp "$BACKUP_DIR"/env-2026-08-18.bak .env            # the ORIGINAL .env, not generate-env.sh
chmod 600 .env
git apply "$BACKUP_DIR"/clone-edits-2026-08-18.patch   # your config edits, or login will not work
docker compose up -d --build
docker compose stop node-api node-portal
```

> Do **not** run `generate-env.sh` on this path. It writes a new `POSTGRES_PASSWORD`, and the
> restored `pgdata` only ever answers to the original one. This is the single reason `.env` is in
> the backup set, and the reason a backup without it is not a backup.

### 1. Stop the applications, leave the database running

```sh
docker compose stop node-api node-portal
```

Both hosts migrate on boot and write continuously. Restoring underneath a running host gives you a
database that disagrees with what the application already has in memory.

### 2. Restore the databases

`pg_dumpall` writes a plain SQL script containing `CREATE DATABASE` and `\connect`, so it is
replayed with `psql`, **not** `pg_restore`. (`pg_restore` is for archives produced by
`pg_dump -Fc`. Using the wrong one is the most common way this goes wrong at three in the
morning.)

**Read the whole step before running any of it.** The order below is the order to use, and the
drop comes before the replay.

> Dropping destroys current data irreversibly. Take a fresh dump of the CURRENT state first, even
> if you believe it is broken. A dump of a broken system is still evidence, and it is the only way
> back if the backup you are restoring turns out to be worse.

If the four databases already exist, which is the case on any node that has booted, drop them
first and mean it. The dump recreates each one, and replaying over an existing database errors on
each object and carries on, leaving a half-old, half-new mixture that looks like it worked.

```sh
docker compose exec postgres psql -U febris -d postgres \
  -c 'DROP DATABASE IF EXISTS febris_user' \
  -c 'DROP DATABASE IF EXISTS febris_data' \
  -c 'DROP DATABASE IF EXISTS febris_xapi' \
  -c 'DROP DATABASE IF EXISTS febris_analytics'
```

Then replay:

```sh
docker compose exec -T postgres psql -U febris -d postgres < "$BACKUP_DIR"/febris-2026-08-18.sql
```

Those four names are what `docker-compose.yml` configures. A node someone has customised, or a
development checkout, may use different ones. Check the `ConnectionStrings__*` values in your
compose file rather than trusting this list, and `\l` in psql shows what is actually there.

### 3. Restore the volumes

The databases are only part of the node. `storage` holds uploaded video and files, and `keys` holds
the DataProtection key ring.

> **Archive the current volumes first.** The commands below begin with `rm -rf`, and this is the
> one step in the whole procedure with nothing behind it. Run the two `tar czf` commands from
> [Backups](#backups) under different file names before you run these, even if you are certain the
> current contents are worthless. A dump protects the databases. Nothing protects these.

```sh
docker run --rm -v febris-node_storage:/to -v "$BACKUP_DIR":/from alpine \
  sh -c 'rm -rf /to/* && tar xzf /from/storage-2026-08-18.tar.gz -C /to'
docker run --rm -v febris-node_keys:/to -v "$BACKUP_DIR":/from alpine \
  sh -c 'rm -rf /to/* && tar xzf /from/keys-2026-08-18.tar.gz -C /to'
```

**Restore `keys` from the same moment as the database, or not at all.** The key ring decrypts auth
cookies and at-rest settings. A database from Tuesday with a key ring from Friday leaves settings
that cannot be decrypted, and the symptom is not an obvious error.

### 4. Start, and verify

```sh
docker compose up -d
./selfhost/smoke.sh
```

If you run the byo-proxy overlay rather than the bundled Caddy, set `COMPOSE_FILE` first, or that
bare `docker compose up -d` quietly puts you back on the bundled recipe and starts a proxy you did
not want on a port you may not have free. See
[If you already run a reverse proxy](#if-you-already-run-a-reverse-proxy). The same applies to
`smoke.sh`, which probes the bundled shape.

`smoke.sh` asserts every service is running, that liveness and readiness both answer, that the
portal responds through the proxy, and that the proxy is still refusing `/health/*` from outside.
It exits non-zero if any of those fail, so it works as the last line of a restore script or a cron
job as well as by hand. To probe readiness alone, `curl http://127.0.0.1:8081/health/ready`.

`/health/ready` proves each database is reachable, and the `schema-user`, `schema-data` and
`schema-xapi` checks additionally prove there are no unapplied migrations, so a restore that produced
a stale schema shows up here rather than as a runtime error later.

Two things it does not tell you. **Analytics has no schema check** -- it is provisioned with
`EnsureCreated` rather than migrations, so nothing verifies its shape. And a green probe says nothing
about the ROWS. It proves the tables exist, not that your data came back. Log in and open one screen
carrying real content before you call the restore done.

### 5. Rehearse it, in a cluster of its own

A backup you have never restored is a hypothesis. Rehearse it somewhere that cannot hurt you.

**Not in a scratch database on the live node.** That does not work and it is worse than not
working. `pg_dumpall` writes a cluster-wide script full of `\connect febris_user` lines, and `psql`
obeys them. Pointing it at a scratch database only decides where the first few statements land.
The moment it reaches the first `\connect` it jumps to your real database and replays into it.

Use a throwaway cluster instead, so those `\connect` lines have nowhere real to go:

```sh
docker run --rm -d --name pg-drill -e POSTGRES_USER=febris -e POSTGRES_PASSWORD=drill postgres:16-alpine
until docker exec pg-drill pg_isready -U febris -h 127.0.0.1 -q; do sleep 1; done

docker exec -i pg-drill psql -U febris -d postgres < "$BACKUP_DIR"/febris-2026-08-18.sql
docker exec pg-drill psql -U febris -d febris_data -c '\dt'
docker rm -f pg-drill
```

It is a separate container on no network of yours, and `docker rm -f` at the end takes the whole
thing with it. Nothing it does can reach the node.

Expect one complaint near the top, `role "febris" already exists`. That is correct and harmless.
The drill container created that role at startup and the dump tries to create it again. Any other
error is a real finding.

If the dump replays cleanly and the tables are there, the backup is real. If it does not, you have
found that out on a day when it costs nothing.

---

## Upgrading, and getting back

```sh
git pull && docker compose up -d --build
```

Migrations run at startup and are **forward-only**. There are no `Down` paths wired into the
deployment, so a bad release cannot be undone by re-running the tooling.

**Take a dump before every upgrade.** That dump is the rollback, and it is the only one.

### Rolling back a bad release

Rolling back code is easy. Rolling back a release whose migrations already ran is not, because the
old code will not recognise the new schema.

```sh
# 1. Go back to the commit that was running before.
git log --oneline -5
git checkout <previous-commit>

# 2. Rebuild and restart.
docker compose up -d --build
```

If the release you are backing out **did not** add a migration, that is the whole procedure. Confirm
with `git diff <previous-commit>..<bad-commit> -- enduser/FebrisEndUserDAL/Migrations` returning
nothing.

If it **did** add one, the schema is ahead of the code you just deployed and you must also restore
the pre-upgrade database dump using the section above. Do not try to hand-drop the new columns:
`__EFMigrationsHistory` still names the migration, so the node will believe it is applied and will
not recreate it.

---

## Running without Docker

Supported on a best-effort basis. The compose stack is what gets tested, and this path is not
covered by the smoke script, so treat the steps below as a starting point rather than a warranty.
The images do nothing exotic, and the equivalent by hand is:

1. Provide Postgres 16 or newer and any Redis-protocol server, Valkey recommended. Pick a storage
   directory and a DataProtection key directory.
2. `dotnet publish -c Release` both `enduser/FebrisEndUserApi` and `enduser/FebrisEndUserPortal`.
3. Export the same environment the compose file sets on the `node-*` services. Connection strings,
   `RedisConnectionStrings__*`, `Storage__*`, `SmbClient__Path`, `AppKeys__KeyRingPath`,
   `JwtSettings__Secret`, `NodeBootstrap__*`, and blank `LicenseKey` and `ApiUrlPath__*`. Include
   `Serilog__*` as well. The file sink's size and retention bounds exist only in the compose
   environment, so without them a bare-metal host writes an unbounded log.
4. Start the **API first and let it finish**, then the Portal. Both hosts provision and migrate the
   databases on boot, and compose deliberately serialises that by making `node-portal` wait for
   `node-api` to report healthy, so the two never run migrations against the same databases at
   once. Nothing enforces that ordering for you here.
5. Run the published assemblies, `dotnet Febris.UserNode.Api.dll` and
   `dotnet Febris.UserNode.Portal.dll`. Neither csproj sets `<AssemblyName>`, so those are the
   names `dotnet publish` produces. The Dockerfile entrypoints carry the same ones.
6. Front the portal with an HTTPS-terminating proxy, which login requires, forwarding
   `X-Forwarded-*`, and declare that proxy to the application with
   `ForwardedHeaders__KnownNetworks__0` or `KnownProxies` as
   [If you already run a reverse proxy](#if-you-already-run-a-reverse-proxy) describes.
7. Block `/health/*` at that proxy if the node is publicly reachable. Both hosts map the probes
   anonymously, because an orchestrator probe cannot authenticate, and `selfhost/Caddyfile` shows
   the shape. The readiness body is terse by default, so this is defence in depth rather than the
   only control. Set `HealthChecks:DetailedResponse` to `true` to get per-check detail back on a
   private network.

> **Do not set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`.** It registers a second forwarded-headers
> middleware ahead of everything else, and that one trusts ANY peer, which defeats the
> `KnownNetworks` allowlist you just declared and re-opens a rate-limit bypass. It was removed from
> `docker-compose.yml` for exactly this reason. Declare your proxy explicitly instead.

---

## Troubleshooting

**Portal returns 502.** The API is still starting, usually applying migrations on first boot.
`docker compose logs -f node-api`.

**`/health/ready` reports a database unhealthy.** Postgres came up after the API. It retries.
If it persists, check `POSTGRES_PASSWORD` matches what the volume was initialised with. A
changed password against an existing `pgdata` volume fails exactly this way.

**Login fails with the generated credentials.** Confirm you are using `NODE_ADMIN_EMAIL` from
`.env` and not an address you assumed. If you set `NODE_ADMIN_PASSWORD` by hand and it fails the
password policy (>= 8 characters with an upper, a lower and a digit, a symbol optional), the
seed logs the rejection and creates *no* account -- the node still comes up and still reports
`/health/ready` Healthy, so nothing else looks wrong. Confirm with
`docker compose logs node-portal | grep -i 'failed creating bootstrap admin'`, which prints Identity's
own reason. Then fix the password in `.env` and `docker compose up -d node-portal` to re-run the
seed. If `NODE_ADMIN_PASSWORD` was blank the account has no password by design -- use the
forgot-password flow, which needs SMTP.

**A user you created cannot log in.** Admin-provisioned accounts get a generated password that
is never shown to anyone, so the account is reachable only through an emailed link. With
`SMTP_HOST` blank nothing was ever sent. Configure SMTP, recreate the containers, then have the
user run forgot-password. See [Adding users](#adding-users).

**Certificate warnings.** Expected, because the bundled cert is self-signed. See
[TLS](#tls-and-reverse-proxying).

**Port already in use.** Change `NODE_HTTPS_PORT` (or `NODE_API_HTTP_PORT`, which publishes the
plain-HTTP API on `127.0.0.1:8081` by default) in `.env` and `docker compose up -d` again.

**A migration failed.** The three migrated databases are brought up to date before the host starts,
and a failure there is logged and skipped rather than crashing the process, which means the node
can come up with a half-applied schema and no obvious error. That is what the schema health checks
exist to catch, so start here:

```sh
curl http://127.0.0.1:8081/health/ready     # with HealthChecks:DetailedResponse=true for the detail
docker compose logs node-api | grep -iE 'migrat|provisioning'
```

A `schema-*` check reporting unhealthy while its `database-*` check is healthy means the database
is reachable and its schema is behind. **Do not hand-edit the schema to catch up.**
`__EFMigrationsHistory` would still name the migration as applied and the node would never
reproduce it. Restore the pre-upgrade dump from [Restoring](#restoring) and retry the upgrade.

**Disk full.** Postgres stops accepting writes before anything else visibly breaks, so the first
symptom is usually saves failing while the site still loads.

```sh
df -h
docker system df -v | grep -A6 "Local Volumes"
```

The volume that grows is `febris-node_storage`, holding module archives and session video, and
`pgdata` follows it. Reclaim space by pruning unused images with `docker image prune`, by moving
old media out of the storage volume, or by shortening retention. `AnalyticsRetention:PurgeAfterDays`
is on by default and the video and account purgers are not, so check what you actually have set
before assuming anything is being cleaned up.

**Records are not arriving from clients.** Work along the path in
[How a simulation's records reach your node](#how-a-simulations-records-reach-your-node), because
the failure is nearly always at one of its joins rather than in the node.

1. Can the client reach the node at all? From the device, not from your desk.
2. Is the device registered, and does its credential belong to **this** node? A credential minted
   by a different node will authenticate against nothing here.
3. Is the client authenticating? **The API does not log request paths by default**, so a plain
   grep for a route returns nothing whether the device is failing or fine. The API host does not
   call `UseSerilogRequestLogging`, and its Serilog config overrides the `Microsoft` category to
   `Warning`, which suppresses the framework's own per-request lines. Turn them on for the
   diagnosis by adding `Serilog__MinimumLevel__Override__Microsoft: "Information"` to the
   `&node-environment` block in `docker-compose.yml`, then `docker compose up -d node-api`:

   ```sh
   docker compose logs node-api | grep -i "api/Token"
   ```

   Repeated failures there point at the credential rather than at the statements. Take the
   override back out when you are done, because it is noisy.
4. Are statements being submitted? Same override, then
   `docker compose logs node-api | grep -i "api/Statement"`.
5. Only then look at the simulation. Remember the SDK does not transmit, so a simulation that
   "sent" a statement has really only handed it to the client.

Rate limiting is worth ruling out early if a fleet of devices all authenticate at once, since the
limiter buckets by resolved client IP and every device behind one NAT looks like one caller. See
[the note on `IpRateLimiting:RealIpHeader`](#leave-ipratelimitingrealipheader-empty), and do not
solve it by setting that key.

---

## Support, contributions and licence

**Support.** There is no support contract and no service behind this. Issues and pull requests on
[Febris-XR/Febris_Node](https://github.com/Febris-XR/Febris_Node) are the whole channel, answered
on a best-effort basis. Security reports go through the process in `SECURITY.md` rather than a
public issue.

**Contributions** are welcome, and `CONTRIBUTING.md` is the shape of them. The most useful thing
you can send is a report from a deployment that is not ours, because the failures that matter most
here are the ones nobody on this side can reproduce.

**Licence.** The node is **AGPL-3.0-only**, and the practical consequence is the one people get
wrong. Running a modified node as a network service means the people using it are entitled to that
modified source. Running an unmodified node, or modifying it privately without offering it as a
service to others, obliges you nothing beyond keeping the notices intact. The SDKs are Apache-2.0,
deliberately, so building a simulation against them puts no copyleft obligation on your simulation.

`THIRD-PARTY-NOTICES.md` lists what ships alongside and under what terms.
