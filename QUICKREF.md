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

Portal at `https://febris.localhost:8443`. The certificate is self-signed, so your browser warns
once. Change the seeded password immediately.

Full walkthrough: [Quickstart](SELF_HOSTING.md#quickstart).

## Day to day

| Task | Command |
|---|---|
| Health, everything | `./selfhost/smoke.sh` |
| Health, readiness only | `curl http://127.0.0.1:8081/health/ready` |
| Per-check detail | set `HealthChecks:DetailedResponse=true`, then re-probe |
| What is running | `docker compose ps` |
| Logs, one service | `docker compose logs -f node-api` |
| Restart after a config change | `docker compose up -d` |
| Stop, keep data | `docker compose down` |
| Stop, **destroy data** | `docker compose down -v` |

Probe the API on loopback, not through the proxy. Caddy answers 404 for `/health/*` deliberately.

## Back up

```sh
docker compose exec postgres pg_dumpall -U febris > febris-$(date +%F).sql
docker run --rm -v febris-node_storage:/from -v "$PWD":/to alpine \
  tar czf /to/storage-$(date +%F).tar.gz -C /from .
docker run --rm -v febris-node_keys:/from -v "$PWD":/to alpine \
  tar czf /to/keys-$(date +%F).tar.gz -C /from .
```

Three things, not one. The databases, the `storage` volume, and the `keys` volume. Losing `keys`
logs everyone out and makes encrypted settings unreadable. Run `pg_dumpall` from **inside** the
container, because it refuses to work against a server newer than itself.

Full procedure, including the restore drill: [Backups](SELF_HOSTING.md#backups) and
[Restoring](SELF_HOSTING.md#restoring).

## Upgrade

```sh
docker compose exec postgres pg_dumpall -U febris > pre-upgrade-$(date +%F).sql   # do this first
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
| Seeded credentials rejected | Check `NODE_ADMIN_EMAIL` in `.env`, and the seed log for a rejected password |
| A user you created cannot log in | Their password was never shown to anyone. Needs SMTP and forgot-password |
| Certificate warnings | Expected. The bundled certificate is self-signed |
| Port already in use | Change `NODE_HTTPS_PORT` or `NODE_API_HTTP_PORT` in `.env` |
| A migration failed | Readiness with detail on. A `schema-*` check unhealthy while `database-*` is healthy means the schema is behind |
| Disk full | Postgres stops writing before anything else looks wrong. Check `storage` first |
| Records not arriving | Walk the path from the device inward, not from the simulation outward |

Every row expands in [Troubleshooting](SELF_HOSTING.md#troubleshooting).

## Facts worth remembering

- **A default node contacts nothing.** Mail, the artifact feed and a hub are each opt-in.
  [Outbound connections](SELF_HOSTING.md#outbound-connections-this-node-makes)
- **Federation ships off and a node with it off is complete**, not degraded. Learner data has no
  route upward. [Hub federation](SELF_HOSTING.md#hub-federation)
- **The SDK does not talk to your node.** It builds statements, a Febris client transmits them.
  [How records reach your node](SELF_HOSTING.md#how-a-simulations-records-reach-your-node)
- **Only two ports are published.** 8443 through Caddy, and 8081 bound to loopback. The databases
  are not reachable from the host.
- **`docker compose down -v` destroys every uploaded package.** `down` on its own does not.

## Verify a download

```sh
sha256sum -c SHA256SUMS
gh attestation verify <file> --repo Febris-XR/Febris_SDK
```

The checksum proves the bytes did not change in transit. The attestation proves where they came
from, which a checksum fetched from the same page cannot. Provenance is present from v0.1.1 onward.
