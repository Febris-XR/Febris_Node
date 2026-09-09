# Contributing

Thanks for looking. Before you invest time, please read the two short sections at the end --
[Review expectations](#review-expectations) and [Scope](#scope) -- because this is a pre-1.0
project with one maintainer and both of those will shape whether a change lands.

If you want to understand the codebase first, start with [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## Prerequisites

| You need | For |
|---|---|
| **.NET 8 SDK** | building and running the tests |
| **Docker + Compose v2** | the full stack (Postgres, Valkey, Portal, API, TLS proxy) |
| **openssl** | `selfhost/generate-env.sh` generates local secrets with it |
| **POSIX shell (bash)** | `generate-env.sh` is a bash script |

You do not need a Febris account, a licence key, or any network service the maintainer runs.

## Build and test

Build the two hosts. Every other node project is pulled in as a project reference, so these two
builds cover all eight of them:

```sh
dotnet build enduser/FebrisEndUserApi/Febris.UserNode.Api.csproj
dotnet build enduser/FebrisEndUserPortal/Febris.UserNode.Portal.csproj
```

Run the two test suites:

```sh
dotnet test tests/FebrisArchitectureTests/Febris.ArchitectureTests.csproj
dotnet test tests/FebrisEndUserBLLTests/Febris.UserNode.LogicLayer.Tests.csproj
```

Pass each project path explicitly rather than relying on a bare `dotnet build` / `dotnet test`
in the repository root.

No database, container or network is needed for any of this -- the logic-layer suite uses the EF
Core in-memory provider, and the architecture suite reads project files off disk. At the time of
writing that is 298 logic-layer tests and 391 shared-services tests passing, with exactly one
skip: the `IStorageProvider` conformance round-trip, which needs a live S3/MinIO endpoint and
runs only if you set `FEBRIS_S3_TEST_ENDPOINT` and `FEBRIS_S3_TEST_BUCKET` (plus
`FEBRIS_S3_TEST_ACCESSKEY` / `FEBRIS_S3_TEST_SECRETKEY`). Any other skip or failure means
something is wrong with your environment, not with the suite.

### Running the stack

To exercise a change end to end you want the real thing:

```sh
./selfhost/generate-env.sh          # writes .env with fresh local secrets, chmod 600
docker compose up -d --build        # first build takes a few minutes
```

`generate-env.sh` refuses to run if a `.env` is already there, and that is deliberate: an
overwrite rotates `POSTGRES_PASSWORD` and `NODE_JWT_SECRET` out from under a stack that is
already provisioned, breaking the existing Postgres volume and every issued token. Pass
`--force` only when you mean exactly that. Otherwise keep the `.env` you have.

Then <https://febris.localhost:8443>, logging in with the `NODE_ADMIN_EMAIL` /
`NODE_ADMIN_PASSWORD` that `generate-env.sh` printed. To check health, use
`curl http://127.0.0.1:8081/health/ready`, or `./selfhost/smoke.sh` for the whole set. The proxy
answers 404 for `/health/*` on purpose, so probing it through `https://febris.localhost:8443` is
not a health failure.

The compose build uses the repository root as its Docker context because the host Dockerfiles
restore from the repository root, so **rebuild with `docker compose up -d --build`**, not by
building an image from inside a host directory.

Full operator detail -- configuration, TLS, backups, upgrades, troubleshooting -- is in
[`SELF_HOSTING.md`](SELF_HOSTING.md). Never commit your `.env`. It is git-ignored and holds
generated secrets.

## The architecture tests are the first gate you will hit

`tests/FebrisArchitectureTests` is not a documentation exercise. It parses the `.csproj` and
`.cs` files on disk and fails the build on a structural violation, so it is usually the first
thing a well-intentioned change trips. Run it before you open a pull request. When it fails, the
assertion message names the offending projects and files.

There are two rules it enforces, and they are not negotiable in a pull request.

**1. The edge boundary.** Everything under `enduser/` is an "edge" deployment -- code that runs
somewhere the maintainer does not control. An edge project may reference **only**:

* `Febris.EnumLibrary`
* `Febris.ModelLibrary`
* `Febris.SharedServices`
* `Febris.XApi.Models`
* other projects under `enduser/`

Anything else fails. Those four libraries must also stay clean transitively, or the allowlist
would be worthless. There is a grandfathered-exception list in `EdgeBoundaryTests.cs`. It is
currently **empty** and a third test asserts it holds no stale entries, so it can only shrink.
Do not add to it -- a pull request that adds an entry to make a new reference legal will be
rejected on principle rather than on the merits.

**2. No new duplicate types.** `DuplicateTypeGuardTests` fails if a type name becomes defined in
a project it was not defined in before. This exists because a real bug shipped when one copy of a
type was fixed and its twin was not. If you hit it, the fix is to **reference the existing type,
or extract it to a shared project both callers can reference**. Copying it is the failure mode
the guard exists to stop. `DuplicateTypeBaseline.txt` records existing debt and may only shrink:
when you consolidate a duplicated type back to one definition, delete its line, or the
stale-entry test will (correctly) fail.

### Layering rules the tests cannot see

The data-access layer references only the shared libraries -- it has no reference back to the
logic layer or to either host -- so the Portal/API -> LogicLayer -> DataAccessLayer direction holds
by construction, and MSBuild would reject a cycle if you tried to introduce one. Two conventions
on top of that are enforced by review, not by a test:

* **Controllers do not touch `DbContext`.** Presentation calls logic, logic calls the data-access
  layer's query classes. The one sanctioned exception is a host's composition root
  (`Startup`/`Program`) registering contexts and running the startup provisioner.
* **Enums live in `Febris.EnumLibrary`.** Not next to the class that happens to use them.

New query classes should use the DI constructor. The naming-convention sweep in
`FebrisUserNodeDataAccessRegistration` registers `IXxxQueries -> XxxQueries` automatically. Some
older classes still carry a legacy self-newing constructor alongside the DI one. That is a
strangler migration in progress, not a pattern to copy.

## Pull requests

* Branch from `main` and open the pull request against `main`.
* One logical change per pull request. A 40-file refactor mixed with a bug fix will sit unread
  for much longer than the two of them separately.
* Say in the description what you changed and **how you verified it**. "Tests pass" is fine when
  a test covers it. If the change has runtime behaviour that the suites do not reach, say what
  you actually ran.
* Add a test when the change is testable. The logic-layer and shared-services suites are where
  they go. The architecture suite is a structural guard, not a place for feature tests.
* Match the surrounding style. The codebase is not uniform and there is no formatter gate.
  Consistency with the file you are editing beats consistency with your own preferences.
* New first-party source files carry the two-line SPDX header:

  ```csharp
  // SPDX-FileCopyrightText: 2026 Febris
  // SPDX-License-Identifier: AGPL-3.0-only
  ```

  Vendored third-party files keep their original headers and are never rewritten.
* Do not commit `.env`, secrets, or anything under `bin/` or `obj/`.

Contributions are accepted under the repository's licence, **AGPL-3.0-only**. There is **no CLA**
and none is planned. A [DCO](https://developercertificate.org/) sign-off (`git commit -s`) may be
introduced later. If that happens it will be announced in the repository and applied going
forward, not retroactively. Signing off today does no harm and costs nothing.

## Review expectations

Be realistic about this: **one maintainer, part-time, no service-level commitment.**

* Expect days, not hours, for a first response -- and longer during a release cut.
* Small, self-contained pull requests get reviewed. Large ones may sit, and I would rather say so
  here than leave you guessing.
* **Open an issue before starting anything substantial.** A large pull request that does not fit
  where the project is going is the worst outcome for both of us, and I cannot promise to review
  a design after you have already implemented it.
* Silence means a backlog, not a rejection. A polite ping after a couple of weeks is welcome.

## Scope

Some things are outside this repository by design, and a pull request adding them will be
declined regardless of quality. A Febris node is standalone: there is no hub, marketplace,
central catalogue authority, commerce or billing, CRM, or licence issuance here, and none is
planned. [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) explains what that means in practice,
including the federation seam that exists in the code and ships closed.

Changes that make a node depend on a network service in order to function will be declined. That
independence is the point of the project.

## Reporting a security issue

**Do not open a public issue or a pull request for a security bug.** Use this repository's
private advisory channel: **Security -> Advisories -> [Report a vulnerability](https://github.com/Febris-XR/Febris_Node/security/advisories/new)**.
That channel stays private between you and the maintainer until a fix is published. See
[`SECURITY.md`](SECURITY.md) for what to include.

## Bug reports

Open an issue with: what you did, what you expected, what happened, and how you are running the
node (the compose stack, or something else). For a stack trace, `docker compose logs node-api` and
`docker compose logs node-portal` are usually the useful ones. Note that the project is pre-1.0
and the answer to some reports will honestly be "yes, known, not fixed yet".
