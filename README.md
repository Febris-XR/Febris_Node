This is the first part of the Febris OSS release. It is safe to call this version 4 of the Febris platform. Many aspects of Version 3 had to be stripped out (The central hub, marketplace, developer system, accreditation system, micro-credentialing, CRM, LMS components that added centralized truth, and there may be a few parts that are now gone that previously existed that I cannot recall right this second) and I used Claude to create and cut that seam. If there are lingering parts, I apologize and I will fix it as soon as I can. I feel like I stretched Claude's capabilities while working on this project. AI was not used on any of the other version of Febris so some of these cuts may seem a little ragged but the entire system was built by one person so, please cut me a little slack.

Claude is far better at documenting code than I have ever been and I suspect between my naming conventions and Claude's documentation, this release will be easy to follow.

# febris-node

**Self-hostable, standalone training/LMS delivery node. No central services required.**

A Febris node is an ASP.NET Core (.NET 8) LMS and content-delivery server for XR and simulation
training. One node owns everything it needs: identity and accounts, cohorts, curricula and
modules, an xAPI statement store, usage analytics, and an artifact store that distributes the
client software to devices. It ships as a Docker Compose stack -- Postgres 16, Valkey 8, an API,
a web portal, and a Caddy reverse proxy -- and it comes up with no Febris account, no licence
key, and no service the maintainer operates.

The clone-and-run path in [Quickstart](#quickstart) has been verified by reading the compose
file, the scripts and the Caddyfile together, and by checking each command against the code it
drives. It has **not** been executed end to end on a clean host. Nobody has watched
`generate-env.sh` run, the stack come up and a first login succeed in one sitting.

That is worth saying plainly rather than leaving you to discover it. If something in the
quickstart does not behave as written, it is a defect and we want it reported. Open an issue with
the command you ran and what came back.

---

## What is in here

Each of these is code in this repository, not a roadmap item. Where something is a seam rather
than a finished feature, it says so.

**Operator-configurable transport hardening, owned by the node.** HSTS (enabled, max-age,
`includeSubDomains`, `preload`), app-level HTTPS redirection, a CORS allow-list,
`X-Frame-Options`, `X-Content-Type-Options` and `X-XSS-Protection` all bind from a single
`Transport` configuration section (`NodeTransportOptions`, in `Febris.SharedServices`) and are
applied by the node's own request pipeline. The defaults are deliberate and a missing section
changes nothing. HSTS is on outside Development. HTTPS redirection is **off**, because a node
behind a TLS-terminating proxy would loop. The CORS allow-list is **empty**, meaning same-origin
plus localhost and no third-party origin. `X-Frame-Options` defaults to `SameOrigin`, with an
unrecognised value failing back to `SameOrigin` rather than silently dropping the header. An
allow-list entry is an exact host (`app.example.com`) or, with a leading dot (`.example.com`),
the domain and its subdomains -- written so `evilexample.com` cannot match. Both hosts apply the
full set, so one policy configures the whole deployment rather than the browser-facing half of it.

**Per-IP endpoint rate limiting.** Endpoint-level rules with in-memory counters, returning 429.
Shipped defaults: 5 requests per 15 minutes on the Portal's login, forgot-password and register
POSTs and on the API's token endpoints, plus a catch-all of 120/min (Portal) and 1200/min (API).
Rules live in `appsettings.json` under `IpRateLimiting`, so an operator retunes them without a
rebuild.

**External SSO provider scaffolding -- a seam, not a working integration.** Be clear on what this
is: `ExternalAuthProviderRegistration.cs` binds an `ExternalAuthProviders` config section
(a Google block, a Microsoft block, and a list of generic OpenID Connect entries covering Azure
AD v2, Okta, Auth0, Keycloak and ADFS), validates each entry, and logs which providers an
operator enabled. The per-provider `AddGoogle` / `AddMicrosoftAccount` / `AddOpenIdConnect`
calls are present but commented out, because their authentication packages are not in the
project's package set. **No external IdP actually signs a user in today.** Enabling one is
documented in that file as three steps -- add the NuGet package, fill in the config block,
uncomment the registration call -- and the surrounding plumbing (the login page's external-login
buttons, the `ExternalLogin` page, the JIT provisioning gate) is already wired.

**Asymmetric JWT signing with PEM keys.** `JwtSigningKeyProvider` resolves signing material once
at construction so a misconfigured host fails at boot rather than on the first authentication.
It reads an RSA private key in PEM (PKCS#8 or PKCS#1) from `FEBRIS_JWT_SIGNING_PRIVATE_KEY` or
`JwtSettings:PrivateKey`, derives a stable `kid` from the public key, and issues node-admin and
device tokens with RS256 when a key is present -- falling back to the legacy HMAC secret when it
is not, with validators accepting both during the transition. Non-Development boots reject an
RSA key under 2048 bits, an HMAC secret under 32 bytes, and an unsubstituted `{Placeholder}`
template. Development generates an ephemeral key pair so the asymmetric path is exercisable
locally. The provider also has a publisher-only mode that carries no symmetric secret at all,
for a host whose only JWT role is publishing a JWKS. That host is not in this repository.

**Identity-policy gates that cannot be declared without being enforced.** One `Identity`
configuration section covers registration mode, password composition, lockout, two-factor,
login, session and account-lifecycle policy. A build-time ratchet
(`IdentityGateCoverageTests`) reflects over the options tree and fails the build for any leaf
knob that is neither marked with `[EnforcesGate]` on the code that honours it nor listed in
`DeferredGates` with a written reason. That deferral list is currently empty -- every declared
gate is enforced. The load-bearing ones:

- a **2FA-enrollment middleware** that redirects an authenticated user to the authenticator
  setup page until they enrol -- `Off` by default, or `AdminsRequired` / `AllRequired`. Its
  allow-list keeps the enrolment flow, recovery codes, logout and the health probes reachable so
  the gate can never trap a user.
- an **absolute session timeout** stamped into the auth ticket at sign-in, which expires a
  session regardless of activity and is not extended by a refresh. Off unless
  `Session.AbsoluteTimeoutMinutes` is set.
- a **soft-delete purge**, a hosted service that hard-deletes accounts retained past
  `AccountLifecycle.PurgeAfterDays`. Unset by default, and no-ops rather than guessing.
- a **JIT provisioning gate**, `Registration.AutoProvisionJit`, which ships **closed** (`false`):
  a user an external IdP authenticates for the first time is turned away unless an admin has
  already provisioned them. Set it to `true` if you want first external login to auto-create a
  local account. Note this gate only governs the external-IdP path -- local password login and
  the bootstrap admin seeded from `NODE_ADMIN_EMAIL` are unaffected either way.

**A built-in artifact store, and a package feed sync that refuses more than it accepts.** The
node is the distribution point for its own client software. The surface splits by audience:
operators upload packages and trigger the feed sync from the **portal**, behind the same
signed-in cookie identity and admin role gates as every other operator action, while devices
fetch through the API with their own device tokens (`api/CompanionApp` for the Companion APK,
`api/Module` for entitlement-gated module delivery). A device identity cannot write to the
catalogue, and no separate API credential exists for humans. Bytes live behind a storage seam,
catalogue rows in the node's own database. `PackageFeedSyncLogic` pulls from a manifest URL the
operator chooses, and its guarantees are the interesting part:

- **checksum verified before ingest, not after.** The artifact is streamed to a temp file while
  hashing in one pass and compared to the manifest's `sha256`. A mismatch, a missing or
  malformed checksum, or a download that exceeds the size ceiling is refused with nothing
  written. Verifying afterwards would mean a truncated download had already become a published
  package.
- **never overwrite.** A UUID already held with a matching checksum is `AlreadyCurrent`. The
  same UUID advertising *different* bytes is refused and reported -- a release identity does not
  get to change what it is.
- **oldest-first, as a correctness requirement.** The catalogue resolves "latest" by row
  timestamp, so whatever lands last is what devices are offered. Entries are therefore applied
  in ascending version order. Newest-first would leave every node serving the *oldest* release
  in the feed, and it would look like it had worked until someone tried to install.
- **per-package outcomes.** One bad entry does not abandon the run. Every package carries its
  own `Ingested` / `AlreadyCurrent` / `Filtered` / `Refused` / `Failed` verdict, and `dryRun`
  produces the same report while changing nothing.

Two honest limits on that. The feed format carries a `signerSha256` per payload -- the SHA-256 of
the signing certificate, the only field in a manifest that speaks to *origin* rather than
integrity -- but the node does **not** yet pin or enforce it. Today it verifies the artifact
checksum only. And the manual `Upload` path records the SHA-256 of what it stored rather than
checking it against a declared value, because on that path there is no manifest to check
against.

**LRS-style xAPI ingest with statement-UUID dedupe.** Statements arrive from at-least-once
producers: a lost response re-POSTs the same statement, and a crash between upload and file-move
re-uploads it on the next poll. Ingest extracts the producer-assigned UUID (the xAPI `id`, then
the Febris-dialect `uuid`), and a statement already persisted under that UUID returns the
existing record as success instead of inserting a second row -- backed by a dedicated index.
Statements carrying no usable identifier keep insert-always behaviour, explicitly. Reads are
default-deny and FERPA-scoped: staff see the tenant, a learner sees their own actor, a
parent/guardian sees exactly the actors of students linked to them, and anyone else is denied.
This is an LRS-*style* store -- it is not a conformance-tested xAPI 1.0.3 LRS and does not claim
to be.

**Enforced layering.** Architecture tests assert the boundary rather than trusting review: a
node project may reference only `Febris.EnumLibrary`, `Febris.ModelLibrary`,
`Febris.SharedServices` and `Febris.XApi.Models`, plus its own projects -- never a shared
data-access or business-logic layer belonging to a service outside this repository. The
grandfathered-violations list is empty, and a companion test fails if it ever holds a stale
entry, so it cannot quietly become a dumping ground.

---

## Quickstart

Prerequisites: **.NET 8 SDK**, **Docker + Compose v2**, **openssl + POSIX bash**. Only Docker,
openssl and bash are needed for the compose path. The .NET 8 SDK is for building or testing
outside containers.

```sh
git clone https://github.com/Febris-XR/Febris_Node.git febris-node && cd febris-node
./selfhost/generate-env.sh          # writes .env with fresh secrets, chmod 600
docker compose up -d --build        # first build takes a few minutes
```

Then open **https://febris.localhost:8443**. `generate-env.sh` prints your first-login
credentials and stores them in `.env`. Change the password immediately -- it was generated on
your machine, but it is sitting in a file.

Verify:

```sh
./selfhost/smoke.sh   # every service up, both probes healthy, proxy still refusing /health/*
```

That is the same check every other procedure in [`SELF_HOSTING.md`](SELF_HOSTING.md) ends with, and
it exits non-zero if anything fails. To do it by hand instead:

```sh
curl http://127.0.0.1:8081/health/ready   # {"status":"Healthy","totalDurationMs":7}
docker compose ps                          # postgres, valkey, node-api, node-portal, proxy
```

Probe the API on its own loopback port rather than through the proxy. Caddy answers 404 for
`/health/*` deliberately, so dependency health is not readable by anyone who can reach the site.
That refusal lives in `selfhost/Caddyfile` and is the reason this command does not use the 8443
URL.

`/health/live` answers as long as the process is up. `/health/ready` runs every dependency check
and by default reports only the overall status and a duration. Set
`HealthChecks:DetailedResponse=true` for the per-check array that names each database, cache and
storage probe separately, which is what you want when something is wrong and not before. Both
endpoints are anonymous, for container and orchestrator probes.

The bundled Caddy certificate is self-signed, so your browser warns once, and any `curl` against
the 8443 URL needs `-k` -- see [`SELF_HOSTING.md`](SELF_HOSTING.md) for putting your own proxy in
front.

[`SELF_HOSTING.md`](SELF_HOSTING.md) is the full operator story: what each container does, the
environment variables, TLS, backups, upgrades, deploying the client suite through your node, and
troubleshooting.

---

## Architecture

Two ASP.NET Core hosts over a shared four-layer stack:

| Host | Namespace | Role |
|---|---|---|
| `node-api` | `Febris.UserNode.Api` | the token-authenticated API devices talk to: xAPI ingest, artifact store, launcher |
| `node-portal` | `Febris.UserNode.Portal` | the web UI, and the owner of ASP.NET Identity (login, roles, 2FA) |

Both sit on `Febris.UserNode.LogicLayer` (business logic) over
`Febris.UserNode.DataAccessLayer` (EF Core), which owns four Postgres databases -- user, data,
xAPI and analytics -- plus the artifact storage seam. Beneath that is the shared triad:
`Febris.EnumLibrary`, `Febris.ModelLibrary` and `Febris.SharedServices`. `Febris.XApi.Models`,
the netstandard2.0 xAPI contract, is a published NuGet package like the rest of the triad.
Nothing shared is vendored as source in this repository.

Valkey (Redis-protocol) is optional and the node adapts to its absence: configured, sessions use
a server-side ticket store with an HTTPS-strict cookie. Not configured, the encrypted ticket
lives in the cookie and the cookie policy relaxes so login works over plain HTTP on localhost.

[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) has the detail: layer responsibilities, the
request path, the database split, and where the seams are.

---

## What is intentionally not here

A Febris node is one half of a two-part system, and this repository is deliberately the half
that stands alone. Not present, and not planned for this repository:

- **the hub** -- the central multi-tenant service.
- **the marketplace** and **commerce** -- listings, purchasing, invoicing, payment.
- **the central catalogue** and **licence issuance**.

Those are closed by design. The node does not need them: it serves its own users, holds its own
content, and distributes its own client software. Their absence is the point of the project, not
a capability withheld from it.

You will nonetheless find marketplace, purchase and invoice *read* types in the tree. They are
federation clients, not implementations -- every one of them checks a single gate before any HTTP
is attempted, and that gate (`HubFederation.Enabled`) defaults to **false**, with a config-less
process getting a permanently closed gate. Closed, those calls return empty and the node runs
local-only. A test suite runs both hosts' real `ConfigureServices` against a configuration with
zero hub credentials -- no API URL, no licence key, no `HubFederation` section -- and then
resolves the controllers' dependency graphs, the same resolutions the first inbound request
performs. "Works with no hub" is therefore a build gate rather than a promise. **A node with
default configuration makes no outbound call to any Febris service.**

Also not here, and all of it is published elsewhere. The **simulation SDKs** are on nuget.org, on
the [Febris_SDK releases](https://github.com/Febris-XR/Febris_SDK/releases) page and through a
vcpkg registry. The **Windows/PC suite** is on the
[Febris_PC releases](https://github.com/Febris-XR/Febris_PC/releases) page and the **Android
suite** on the [Febris_MobileSuite releases](https://github.com/Febris-XR/Febris_MobileSuite/releases)
page, both at v0.2.0. None of them reaches a node's catalogue on its own. That takes a feed sync
against a manifest you host, which [`SELF_HOSTING.md`](SELF_HOSTING.md) covers.

---

## Configuration posture

Everything an operator touches is configuration, not a code edit, and every knob has a safe
default that a missing section preserves.

- **Secrets come from the environment.** `generate-env.sh` writes a `chmod 600` `.env` with
  fresh Postgres and JWT secrets, and the compose stack reads it. Nothing is compiled in.
- **Config sections are env-overridable** in the standard ASP.NET Core way, so
  `Transport:HttpsRedirection` is `Transport__HttpsRedirection=true` in a container. The four
  sections worth knowing are `Transport` (transport hardening), `Identity` (identity policy),
  `IpRateLimiting` (rate limits) and `HubFederation` (off).
- **Unresolved placeholders are surfaced, not swallowed.** A value still reading as a literal
  `{Placeholder}` in a deployed environment -- a secret nobody injected -- is logged at startup,
  and fails the boot outright when `ConfigValidation:FailFastOnUnresolvedPlaceholders` is set.
- **Missing optional dependencies are not failures.** Health checks are registered only for what
  a host actually owns, so an absent optional subsystem never reads as unhealthy.
- **The bootstrap admin can be passwordless on purpose.** If `NODE_ADMIN_PASSWORD` is blank the
  account is created without one and you set it through the forgot-password flow, which needs
  SMTP. The project does not store a password you did not choose.

---

## Project status

Pre-1.0, and honest about it.

- **Solo maintainer.** Expect slow review and no on-call.
- **No long-term-support branch.** Fixes land on the default branch. Upgrades run migrations at
  startup. Take a database backup first, because there is no downgrade path yet.
- **Interfaces may change** before 1.0, including configuration keys and API routes.
- **Test suites are green** and are the honest measure of what is pinned: 298 node business-logic
  tests, 391 in the node slice of the shared-services suite, and 5 architecture tests. They ship
  in this repository -- [`CONTRIBUTING.md`](CONTRIBUTING.md) has the per-project `dotnet test`
  commands.
- **Known gaps**, stated rather than discovered. The feed-sync tests fake the HTTP fetch and
  exercise real storage and catalogues, so the path has not been run against a live public feed.
  `signerSha256` is carried in the feed format but not enforced. External SSO is scaffolding as
  described above.
- **The Windows and mobile clients live in separate repositories** and are published there at
  v0.2.0. A node serves them only once an operator syncs a feed that lists them, so the
  client-distribution surface is exercisable against real artifacts rather than only against
  fixtures.

---

## Security

Report vulnerabilities privately through this repository's GitHub security advisories --
Security -> Advisories -> **Report a vulnerability**. Please do not open a public issue for a
security bug. See [`SECURITY.md`](SECURITY.md).

---

## Licence

**AGPL-3.0-only.** See [`LICENSE`](LICENSE). Running a modified node as a network service
obliges you to offer that modified source to its users. That is intended, and it is why this
repository can be the flagship rather than a demo.
