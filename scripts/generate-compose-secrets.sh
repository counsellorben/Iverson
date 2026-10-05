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
)

umask 077
touch "$ENV_FILE"
chmod 600 "$ENV_FILE"
for name in "${NAMES[@]}"; do
    if ! grep -q "^${name}=" "$ENV_FILE"; then
        echo "${name}=$(rand)" >> "$ENV_FILE"
    fi
done

# CSR round-10 #5: the certificate the compose authentik-tls-proxy serves on 8443 and the CA the API
# trusts for it. Written only when missing, like the secrets above, so a re-run keeps them. The CA
# key is deleted once the certificate is signed. The files are world-readable inside an owner-only
# directory: under rootless podman a container uid cannot read a host file that is 0600, and the
# directory keeps other host users out.
TLS_DIR="$(dirname "$0")/../Iverson.Server/deploy/compose-tls"
if [ ! -f "$TLS_DIR/tls.crt" ]; then
    mkdir -p "$TLS_DIR"
    chmod 700 "$TLS_DIR"
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT
    openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -subj "/CN=iverson-compose-authentik-ca" \
        -keyout "$work/ca.key" -out "$TLS_DIR/ca.crt" 2>/dev/null
    openssl req -newkey rsa:2048 -nodes -subj "/CN=authentik-server" \
        -keyout "$TLS_DIR/tls.key" -out "$work/tls.csr" 2>/dev/null
    printf 'subjectAltName=DNS:authentik-server\n' > "$work/ext.cnf"
    openssl x509 -req -in "$work/tls.csr" -CA "$TLS_DIR/ca.crt" -CAkey "$work/ca.key" \
        -set_serial "0x$(openssl rand -hex 8)" -days 3650 -extfile "$work/ext.cnf" -out "$TLS_DIR/tls.crt" 2>/dev/null
    chmod 644 "$TLS_DIR/ca.crt" "$TLS_DIR/tls.crt" "$TLS_DIR/tls.key"
    echo "Wrote $TLS_DIR"
fi

echo "Wrote/updated $ENV_FILE"
