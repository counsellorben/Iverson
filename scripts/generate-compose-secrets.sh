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

echo "Wrote/updated $ENV_FILE"
