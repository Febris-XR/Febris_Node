#!/usr/bin/env bash
# Febris Node -- prove a running stack is actually healthy.
#
# Run this after any procedure that touches the stack. Bringing it up, restoring a backup,
# upgrading, changing configuration. It asserts against the endpoints the node already exposes
# rather than inventing new ones, and it exits non-zero if anything fails, so it works as the
# last line of a script or a cron job as well as by hand.
#
# Usage:  ./selfhost/smoke.sh
#
# Ports come from .env when it exists, so this follows whatever you configured. Override either
# with the environment if you run the stack somewhere unusual.
#
# WHY IT PROBES LOOPBACK AND NOT THE PROXY. Caddy answers 404 for /health/* on purpose, so
# dependency health is not readable by anyone who can reach the site. The health endpoints are
# reachable on the API's own published port, which is bound to 127.0.0.1 by default.
set -euo pipefail

cd "$(dirname "$0")/.."

if [[ -f .env ]]; then
    # Read only the two keys needed, rather than sourcing a file of secrets into this shell.
    api_port_from_env="$(grep -E '^NODE_API_HTTP_PORT=' .env | tail -1 | cut -d= -f2- || true)"
    https_port_from_env="$(grep -E '^NODE_HTTPS_PORT=' .env | tail -1 | cut -d= -f2- || true)"
fi

API_PORT="${NODE_API_HTTP_PORT:-${api_port_from_env:-8081}}"
HTTPS_PORT="${NODE_HTTPS_PORT:-${https_port_from_env:-8443}}"
API="http://127.0.0.1:${API_PORT}"
PORTAL="https://febris.localhost:${HTTPS_PORT}"

failures=0
pass() { printf '  PASS  %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1" >&2; failures=$((failures + 1)); }

need() {
    command -v "$1" >/dev/null 2>&1 || { echo "error: $1 is required." >&2; exit 1; }
}
need curl
need docker

# Return the HTTP status of a URL, or 000 if it could not be reached at all.
#
# The `|| true` sits INSIDE the substitution on purpose. Writing `$(curl ...) || echo 000` appends
# a second 000 to curl's own, producing 000000, which then compares numerically less than 400 and
# reports a dead endpoint as a PASS. That is not hypothetical, it is what the first version of this
# script did, and it is why every caller below range-checks rather than testing for inequality.
http_code() {
    local code
    code="$(curl -k -s -o /dev/null -w '%{http_code}' --max-time "${2:-15}" "$1" 2>/dev/null || true)"
    [[ "${code}" =~ ^[0-9]{3}$ ]] || code="000"
    printf '%s' "${code}"
}

echo "Febris node smoke check"
echo "  api    ${API}"
echo "  portal ${PORTAL}"
echo

# 1. Every service the compose file defines is running. `docker compose ps` with --status running
# lists only what is actually up, so a crashed container is a missing name rather than a name with
# a bad status beside it.
running="$(docker compose ps --services --status running 2>/dev/null || true)"
for svc in postgres valkey node-api node-portal proxy; do
    if printf '%s\n' "${running}" | grep -qx "${svc}"; then
        pass "service ${svc} is running"
    else
        fail "service ${svc} is NOT running"
    fi
done

# 2. Liveness. This deliberately runs no dependency checks, so it answers as long as the process
# is up. A failure here means the API itself is down, not one of its dependencies.
if curl -fsS --max-time 10 "${API}/health/live" >/dev/null 2>&1; then
    pass "api liveness"
else
    fail "api liveness (${API}/health/live did not answer)"
fi

# 3. Readiness. This runs every registered check, so it is the one that proves the databases, the
# cache and the storage provider are all reachable AND that the schema is migrated. The body is
# terse by default. Set HealthChecks:DetailedResponse=true for the per-check array when something
# here fails and you need to know which dependency it was.
ready_body="$(curl -fsS --max-time 30 "${API}/health/ready" 2>/dev/null || true)"
if printf '%s' "${ready_body}" | grep -q '"status":"Healthy"'; then
    pass "api readiness reports Healthy"
elif [[ -n "${ready_body}" ]]; then
    fail "api readiness did not report Healthy: ${ready_body}"
else
    fail "api readiness (${API}/health/ready did not answer)"
fi

# 4. The portal answers through the proxy. -k because the bundled Caddy uses a local self-signed
# CA. Any HTTP status below 400 counts, because an unauthenticated request is expected to redirect
# to the login page rather than return 200.
portal_code="$(http_code "${PORTAL}/")"
if (( portal_code >= 100 && portal_code < 400 )); then
    pass "portal answers through the proxy (HTTP ${portal_code})"
else
    fail "portal did not answer through the proxy (HTTP ${portal_code})"
fi

# 5. The proxy really is refusing to expose health. This is asserted rather than assumed, because
# it is a security property and a future Caddyfile edit could silently drop it.
probe_code="$(http_code "${PORTAL}/health/ready")"
if [[ "${probe_code}" == "404" ]]; then
    pass "proxy refuses /health/* from outside (HTTP 404)"
elif [[ "${probe_code}" == "000" ]]; then
    fail "could not reach the proxy to test the /health/* refusal"
else
    fail "proxy EXPOSED /health/ready (HTTP ${probe_code}, expected 404)"
fi

echo
if (( failures == 0 )); then
    echo "OK   the node is up and healthy."
    exit 0
fi
echo "FAILED   ${failures} check(s). See SELF_HOSTING.md, Troubleshooting." >&2
exit 1
