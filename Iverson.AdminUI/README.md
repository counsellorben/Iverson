# Iverson.AdminUI

The admin dashboard for Iverson: a React + TypeScript SPA (Vite, react-router,
`react-oidc-context`) that lets an admin — an operator or a tenant admin — log in
through Authentik and see tenants, schemas, data volume and platform metrics via
Iverson.Api.

## Prerequisites

- Node.js 20+ and npm
- The rest of the Iverson stack running locally — this app talks to
  Iverson.Api and redirects to Authentik for login, so both need to be up
  first. From the repo root, generate the compose stack's dev secrets, then
  from `Iverson.Server/`:

  ```bash
  ./scripts/generate-compose-secrets.sh
  cd Iverson.Server
  docker compose build iverson-api
  docker compose up -d
  ```

  (or `dotnet run` from `Iverson.Server/Iverson.Launcher`). This brings up
  Authentik at `https://localhost:9443` and the API at `https://localhost:8081`,
  matching the defaults in `.env.development`. Both serve TLS from the compose CA
  (`Iverson.Server/deploy/compose-tls/ca.crt`, written by
  `scripts/generate-compose-secrets.sh`): import it into your browser's or your
  operating system's trusted roots first (`docs/runbooks/csr12-platform.md` §4).
- At least one admin to log in with: an operator, or a member of `tenant-admins`. Authentik
  refuses everyone else at sign-in. See [Creating a human user and granting operator access](../docs/user-management-and-security.md#creating-a-human-user-and-granting-operator-access)
  if you don't have one yet. (Bootstrap admin login for compose:
  `admin@iverson.local` / the `AUTHENTIK_BOOTSTRAP_PASSWORD` value generated
  into `.env` by `scripts/generate-compose-secrets.sh`.)
  For compose, one is already seeded: `iverson-loadtest-bypass-user`, whose password is
  the `IVERSON_BYPASS_PASSWORD` value in the same `.env`; it belongs to `operators`, compose's
  dev-only Operator group, out of the box (`blueprints/compose-only/service-clients.yaml`).

## Running locally

```bash
cd Iverson.AdminUI
npm install
npm run dev
```

This starts the Vite dev server at `http://localhost:5173`. Opening it
redirects, unauthenticated, straight into Authentik's login page (Authorization
Code + PKCE, MFA-enforced) — `http://localhost:5173/callback` is already a
registered redirect URI on the `iverson-oidc-default` provider for both the
compose and kind targets, so no extra Authentik config is needed for this
default port.

## Configuration

Runtime config is read from `window.__ADMIN_UI_CONFIG__` first, then falls
back to Vite build-time env vars (`src/config.ts`). `.env.development` ships
with defaults for the docker-compose target:

```
VITE_OIDC_CLIENT_ID=dev-iverson-human-oidc-client-id
VITE_OIDC_AUTHORITY=https://localhost:9443/application/o/iverson-api/
VITE_API_BASE_URL=https://localhost:8081
```

To point at a different target (e.g. a local kind cluster), copy these into
a git-ignored `.env.local` and override as needed — `.env.local` takes
precedence over `.env.development` in Vite's env loading order.

## Other scripts

| Command | Purpose |
|---|---|
| `npm run build` | Production build to `dist/` (served by nginx — see `Dockerfile`) |
| `npm test` | Run the Vitest suite once |
| `npm run generate` | Regenerate gRPC-Web TypeScript clients from `Iverson.Clients/Common/Proto/` into `generated/` (requires `protoc` + `protoc-gen-ts_proto`; see `scripts/generate_protos.sh`) |

## Building the container image

```bash
docker build -f Iverson.AdminUI/Dockerfile -t iverson-admin-ui .
```

(build context is the repo root — the Dockerfile also copies
`Iverson.Clients/Common/Proto`). The container serves the built SPA via
nginx and renders `OIDC_CLIENT_ID` / `OIDC_AUTHORITY` / `API_BASE_URL` into
`config.js` at startup from environment variables (`docker-entrypoint.sh`).
