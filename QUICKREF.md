# Quick reference

One page for people who already know what they are doing. Everything here is derived from
[`SELF_HOSTING.md`](SELF_HOSTING.md), and where the two disagree, that one is right.

---

## Bring a node up

```sh
git clone https://github.com/Febris-XR/Febris_Node.git febris-node && cd febris-node
./selfhost/generate-env.sh          # writes .env with fresh secrets, prints your first login
docker compose up -d --build
./selfhost/smoke.sh                 # exits non-zero if anything is wrong
```

Portal at `https://febris.localhost:8443`, API at `https://api.febris.localhost:8443`. Both are
loopback names, so **open them from the machine running Docker**. On a headless server, tunnel
with `ssh -L 8443:127.0.0.1:8443 you@server` and use the same URL at the other end.

The certificate is self-signed, so your browser warns once. Change the seeded password
immediately. If you wrote your own `.env` and left both admin variables blank, no account is
seeded at all. Claim the node at `/setup` with the token from
`docker compose logs node-portal | grep -A4 'FEBRIS NODE IS UNCLAIMED'`.

Full walkthrough: [Quickstart](SELF_HOSTING.md#quickstart).

## Day to day

| Task | Command |
|---|---|
| Health, everything | `./selfhost/smoke.sh` |
| Health, readiness only | `curl http://127.0.0.1:8081/health/ready` |
| Per-check detail | add `HealthChecks__DetailedResponse: "true"` to `&node-environment` in `docker-compose.yml`, then `up -d`. **Not** a `.env` key |
| What is running | `docker compose ps` |
| Logs, one service | `docker compose logs -f node-api` |
| Restart after a config change | `docker compose up -d` |
| Stop, keep data | `docker compose down` |
| Stop, **destroy data** | `docker compose down -v` |

Probe the API on loopback, not through the proxy. Caddy answers 404 for `/health/*` deliberately.

## Get packages into the catalogue

**Feed sync is the only way a client package gets in.** There is no upload form. Point
**System -> Node -> Package Feed** at a manifest URL, dry-run it, then run it for real. Set
`PackageFeed__Url` in the `&node-environment` block of `docker-compose.yml` to have it repeat on
`PackageFeed__IntervalHours`, default 24, minimum 1. **Not** a `.env` key.
[Deploy the client suite](SELF_HOSTING.md#deploy-the-client-suite-through-your-node)

## Back up

```sh
export BACKUP_DIR=/var/backups/febris-node        # NOT inside the clone
mkdir -p "$BACKUP_DIR"

docker compose exec -T postgres pg_dumpall -U febris > "$BACKUP_DIR"/febris-$(date +%F).sql
docker run --rm -v febris-node_storage:/from -v "$BACKUP_DIR":/to alpine \
  tar czf /to/storage-$(date +%F).tar.gz -C /from .
docker run --rm -v febris-node_keys:/from -v "$BACKUP_DIR":/to alpine \
  tar czf /to/keys-$(date +%F).tar.gz -C /from .
cp .env "$BACKUP_DIR"/env-$(date +%F).bak && chmod 600 "$BACKUP_DIR"/env-$(date +%F).bak
```

**Four things, not three.** The databases, `storage`, `keys`, and `.env`. Losing `keys` logs
everyone out and makes encrypted settings unreadable. Losing `.env` loses `POSTGRES_PASSWORD`,
which is the only password the `pgdata` volume will ever answer to, so a restore onto a new host
is impossible without it. `-T` is required or the dump comes back with carriage returns in it.
Run `pg_dumpall` from **inside** the container, because it refuses to work against a server newer
than itself.

Rehearse in a **throwaway cluster**, never a scratch database on the live node. The dump is full
of `\connect` lines and `psql` obeys them, so a scratch database does not contain it.

```sh
docker run --rm -d --name pg-drill -e POSTGRES_USER=febris -e POSTGRES_PASSWORD=drill postgres:16-alpine
until docker exec pg-drill pg_isready -U febris -q; do sleep 1; done
docker exec -i pg-drill psql -U febris -d postgres < "$BACKUP_DIR"/febris-$(date +%F).sql
docker rm -f pg-drill
```

Full procedure: [Backups](SELF_HOSTING.md#backups) and [Restoring](SELF_HOSTING.md#restoring).

## Upgrade

```sh
docker compose exec -T postgres pg_dumpall -U febris > "$BACKUP_DIR"/pre-upgrade-$(date +%F).sql
git pull
docker compose up -d --build
./selfhost/smoke.sh
```

That dump is your rollback and it is the only one. Migrations run automatically on boot, and old
code will not recognise a new schema.
[Upgrading, and getting back](SELF_HOSTING.md#upgrading-and-getting-back).

## Troubleshooting, first move

| Symptom | Start here |
|---|---|
| Portal returns 502 | The API is still starting, usually applying migrations on first boot |
| A database reports unhealthy | Postgres came up after the API. It retries |
| Nothing loads at all | You are not on the Docker host. `febris.localhost` is loopback |
| Seeded credentials rejected | Check `NODE_ADMIN_EMAIL` in `.env`, then grep the node-portal log for `failed creating bootstrap admin` |
| No admin account exists | Both admin variables were blank. Claim at `/setup` with the logged token |
| A user you created cannot log in | Their password was never shown to anyone. Needs SMTP and forgot-password |
| Certificate warnings | Expected. The bundled certificate is self-signed |
| Port already in use | Change `NODE_HTTPS_PORT` or `NODE_API_HTTP_PORT` in `.env` |
| A migration failed | Readiness with detail on. A `schema-*` check unhealthy while `database-*` is healthy means the schema is behind |
| Disk full | Postgres stops writing before anything else looks wrong. Check `storage` first |
| Software Repository is empty | Expected on a fresh node. Nothing has been synced. It links out instead |
| Records not arriving | Walk the path from the device inward, not from the simulation outward |

Every row expands in [Troubleshooting](SELF_HOSTING.md#troubleshooting).

## Facts worth remembering

- **A default node contacts nothing.** Mail, the artifact feed and a hub are each opt-in.
  [Outbound connections](SELF_HOSTING.md#outbound-connections-this-node-makes)
- **Federation ships off and a node with it off is complete**, not degraded. Learner data has no
  route upward. [Hub federation](SELF_HOSTING.md#hub-federation)
- **The SDK does not talk to your node.** It builds statements, a Febris client transmits them,
  and the client opens the attempt with `/api/Statement/StatementInitialization` before it
  submits. [How records reach your node](SELF_HOSTING.md#how-a-simulations-records-reach-your-node)
- **On Android the Mobile Server talks to your node, not the Companion.** The Companion holds no
  node URL and reaches the Server over Wi-Fi Direct.
- **Devices do not enrol themselves.** Create each one at **Operations -> Hardware**, then the
  create button on that page, and copy the
  credential, which is shown once and stored only as a hash.
- **The client suites are published, at v0.2.0**, on the Febris_PC and Febris_MobileSuite
  releases pages rather than this one. Downloading them does not put them in your catalogue. That
  takes a feed sync against a manifest you host.
- **Only two ports are published.** 8443 through Caddy, and 8081 bound to loopback. The databases
  are not reachable from the host.
- **`docker compose down -v` destroys every synced package.** `down` on its own does not.

## Verify a download

```sh
sha256sum -c SHA256SUMS
```

That proves the bytes did not change in transit. It does not prove where they came from, because
whoever could swap the file could swap the checksum beside it. Build provenance would close that,
and **no release carries it yet**. The attesting workflow change is written and unmerged, so
`gh attestation verify` fails on every current artifact.
