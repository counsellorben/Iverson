#!/usr/bin/env bash
#
# Generates Iverson.Server/.env with 7 random dev-only credentials for the docker-compose stack's
# Authentik OAuth2 clients, users, and admin-orchestrator API token — replacing the values that used
# to be hardcoded in compose-only/service-clients.yaml (CSR round-4 finding #8). Safe to re-run: it
# refuses to overwrite an existing .env, so re-running after the stack has already provisioned
# Authentik with one set of values won't desynchronize the two.
set -euo pipefail

ENV_FILE="$(dirname "$0")/../Iverson.Server/.env"

if [[ -f "$ENV_FILE" ]]; then
    echo "$ENV_FILE already exists — not overwriting. Delete it first if you want fresh credentials."
    exit 0
fi

rand() { openssl rand -hex 32; }

cat > "$ENV_FILE" <<EOF
IVERSON_LOADTEST_CLIENT_SECRET=$(rand)
IVERSON_WEBTEST_CLIENT_SECRET=$(rand)
IVERSON_ADMIN_AUTOMATION_CLIENT_SECRET=$(rand)
IVERSON_SMOKE_TEST_PASSWORD=$(rand)
IVERSON_BYPASS_PASSWORD=$(rand)
IVERSON_ADMIN_ORCHESTRATOR_PASSWORD=$(rand)
IVERSON_ADMIN_ORCHESTRATOR_TOKEN=$(rand)
EOF

echo "Wrote $ENV_FILE"
