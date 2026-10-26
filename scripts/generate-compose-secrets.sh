#!/usr/bin/env bash
#
# Generates/updates Iverson.Server/.env with random dev-only credentials for the docker-compose
# stack's Authentik OAuth2 clients, users, admin-orchestrator API token, and Qdrant API key —
# replacing the values that used to be hardcoded in compose-only/service-clients.yaml and
# docker-compose.yml (CSR round-4 finding #8, CSR round-7 finding F10). Safe to re-run: it appends
# only the keys missing from an existing .env, leaving already-provisioned values untouched, so
# re-running after the stack has already provisioned Authentik with one set of values won't
# desynchronize the two.
set -euo pipefail

ENV_FILE="$(dirname "$0")/../Iverson.Server/.env"

rand() { openssl rand -hex 32; }

NAMES=(
    IVERSON_LOADTEST_CLIENT_SECRET
    IVERSON_WEBTEST_CLIENT_SECRET
    IVERSON_ADMIN_AUTOMATION_CLIENT_SECRET
    IVERSON_SMOKE_TEST_PASSWORD
    IVERSON_BYPASS_PASSWORD
    IVERSON_ADMIN_ORCHESTRATOR_PASSWORD
    IVERSON_ADMIN_ORCHESTRATOR_TOKEN
    QDRANT__SERVICE__API_KEY
    AUTHENTIK_SECRET_KEY
    AUTHENTIK_BOOTSTRAP_PASSWORD
    AUTHENTIK_BOOTSTRAP_TOKEN
    # CSR round 12 §7.1: the master key every StarRocks tenant login's password is derived from.
    # Never change it on a stack that has tenant logins: provisioning only creates missing ones.
    IVERSON_STARROCKS_TENANT_KEY
)

umask 077
touch "$ENV_FILE"
chmod 600 "$ENV_FILE"
for name in "${NAMES[@]}"; do
    if ! grep -q "^${name}=" "$ENV_FILE"; then
        echo "${name}=$(rand)" >> "$ENV_FILE"
    fi
done

# CSR round-10 #5 and round 12 §4.6: the compose CA and the certificates it signs, one per TLS
# server: authentik-tls-proxy (tls.crt, the name the proxy has always mounted), Postgres, the
# StarRocks FE, Kestrel (iverson-api and iverson-worker share one) and Qdrant. Every leaf names its
# compose service and `localhost`, which host clients dial; only Kestrel's also names 127.0.0.1.
# All of them are rewritten together, under a new CA, when any file is missing or any certificate
# ends within 30 days, so a re-run otherwise keeps them. The CA key is deleted once every leaf is
# signed. The CA carries keyUsage: Python 3.14's default context refuses a CA without it. The
# files are world-readable inside an owner-only directory: under rootless podman a container uid
# cannot read a host file that is 0600, and the directory keeps other host users out.
TLS_DIR="$(dirname "$0")/../Iverson.Server/deploy/compose-tls"
# <file stem> <common name> <subjectAltName>
LEAVES=(
    "tls authentik-server DNS:authentik-server,DNS:localhost"
    "postgres postgres DNS:postgres,DNS:localhost"
    "starrocks starrocks DNS:starrocks,DNS:localhost"
    "kestrel iverson-api DNS:iverson-api,DNS:iverson-worker,DNS:localhost,IP:127.0.0.1"
    "qdrant qdrant DNS:qdrant,DNS:localhost"
)
renew=no
[ -f "$TLS_DIR/ca.crt" ] || renew=yes
for leaf in "${LEAVES[@]}"; do
    read -r stem _ <<< "$leaf"
    if [ ! -f "$TLS_DIR/$stem.crt" ] || [ ! -f "$TLS_DIR/$stem.key" ] \
        || ! openssl x509 -checkend $((30 * 24 * 3600)) -noout -in "$TLS_DIR/$stem.crt" >/dev/null 2>&1; then
        renew=yes
    fi
done
if [ "$renew" = yes ]; then
    mkdir -p "$TLS_DIR"
    chmod 700 "$TLS_DIR"
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT
    # openssl's progress output is noise on success; on failure show it and stop.
    quiet() { "$@" 2>"$work/openssl.err" || { cat "$work/openssl.err" >&2; exit 1; }; }
    quiet openssl req -x509 -newkey rsa:2048 -nodes -days 825 -subj "/CN=iverson-compose-ca" \
        -addext "keyUsage=critical,keyCertSign,cRLSign" -keyout "$work/ca.key" -out "$TLS_DIR/ca.crt"
    for leaf in "${LEAVES[@]}"; do
        read -r stem cn san <<< "$leaf"
        quiet openssl req -newkey rsa:2048 -nodes -subj "/CN=$cn" \
            -keyout "$TLS_DIR/$stem.key" -out "$work/$stem.csr"
        printf 'subjectAltName=%s\n' "$san" > "$work/$stem.cnf"
        quiet openssl x509 -req -in "$work/$stem.csr" -CA "$TLS_DIR/ca.crt" -CAkey "$work/ca.key" \
            -set_serial "0x$(openssl rand -hex 8)" -days 825 -extfile "$work/$stem.cnf" -out "$TLS_DIR/$stem.crt"
        chmod 644 "$TLS_DIR/$stem.crt" "$TLS_DIR/$stem.key"
    done
    chmod 644 "$TLS_DIR/ca.crt"
    echo "Wrote $TLS_DIR"
fi

echo "Wrote/updated $ENV_FILE"
