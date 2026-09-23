# Serve the `/v1/traces` relay on the admin listener — design

**Status:** approved design, 2026-09-23 (the admin-console main-merge spec's follow-up 5).
**Base:** local `main` at `bc2be1c9` (the admin-console merge).

## Goal

The admin console's browser span export reaches Jaeger again, in compose and on every Helm profile that runs the console. Authentication and rate limiting stay exactly as strict as they are today.

## Background (measured)

**The console exports to `<API_BASE_URL>/v1/traces`.**
- `Iverson.AdminUI/src/telemetry.ts:27` sets `OTLP_TRACES_URL = absoluteUrl("/v1/traces")`.
- The target is `admin-api.<ingressHost>` under Helm and `http://localhost:8081` in compose (`.env.development`).
- The exporter (`@opentelemetry/exporter-trace-otlp-http` 0.220.0) always uses `fetch`, with `Content-Type: application/json`, the console's `Authorization: Bearer …`, `mode: 'cors'` cross-origin, and `keepalive` for small batches.

**Kestrel has two listeners, with different protocols** (`Iverson.Api/appsettings.json`):
- `:8080` accepts `Http2` only, cleartext (h2c).
- `:8081` accepts `Http1` only.

**Main's `0baebc4b` pinned every non-operational endpoint to 8080** via `RequireListenerPort`, and every operational endpoint to 8081. The 8080 group includes gRPC, `/admin/reconcile`, `/admin/dlq*` and the `/v1/traces` relay (`Program.cs:769`). The 8081 group is `/health*`, `/build`, and the Prometheus scrape. The commit gives no traces-specific reason.

**The admin-api Ingress routes only `/admin` and `/health` to 8081.** The branch had a `/v1/traces` → 8081 rule, but the admin-console merge dropped it, because 8081 answers `/v1/traces` with the port gate's 404.

**A browser can't reach 8080.** A scratch Kestrel app with main's two endpoint configurations gave:

| Request | Listener | Result |
|---|---|---|
| HTTP/1.1 POST | `Http2`-only | `400 An HTTP/1.x request was sent to an HTTP/2 only endpoint` |
| h2c POST | `Http2`-only | 200 |
| HTTP/1.1 POST | `Http1` | 200 |

Browsers never speak h2c. ingress-nginx sends HTTP/1.1 upstream unless an Ingress sets `backend-protocol: GRPC`, and no chart value does so except AWS's api Ingress (`values-aws.yaml:91`).

## Decision

**Remove the relay's listener pin, and restore the admin-api Ingress `/v1/traces` rule.** The user picked this option (A) over the alternatives below.

| Option | Verdict | Why |
|---|---|---|
| **A** — unpin + restore the Ingress rule | **Chosen** | One line of server code. Every existing protection stays. Works in compose, on kind and in the cloud. |
| **B** — pin to 8081 | Rejected (dominated by A) | The pre-auth IP limiter exempts endpoints pinned to 8081 (`Program.cs:461`), and so does the post-auth limiter (`Program.cs:117`). Main's own api-Ingress route to 8080 would then 404. |
| **C** — export same-origin through main's api Ingress to 8080 | Rejected | Fails outright in compose, because a browser can't do h2c. Fails on the nginx profiles, where HTTP/1.1 upstream gets a 400. |
| **D** — a new `/admin/console/traces` route | Rejected | Reachability and limits are the same as A. Costs a refactor of main's 28-line inline handler into a shared method plus a console URL change. Buys only a literal 8080 pin, and the `/admin/console/*` endpoints already answer on both listeners. |

## Design

1. **Server.** In `Iverson.Server/Iverson.Api/Program.cs`, the relay's registration ends with `.RequireAuthorization().RequireRateLimiting("traces");`. The `.WithMetadata(new RequireListenerPort(8080))` is removed. A short comment beside it explains why this endpoint is deliberately unpinned:
   - It is browser-originated. 8080 is h2c-only, which browsers can't speak, so it must also answer on the `Http1` listener that the admin-api Ingress targets.
   - It keeps authentication (the fallback authenticated-user policy), the per-user 60/min `traces` policy (`Program.cs:144-152`, keyed by `sub`), and both global limiters. Those limiters exempt only endpoints pinned to 8081.

   Nothing else in `Program.cs` changes. Main's api Ingress keeps `/v1/traces` → 8080 unchanged (`charts/api/templates/ingress.yaml:95`).
2. **Ingress.**
   - Restore the `/v1/traces` rule in `charts/api/templates/admin-api-ingress.yaml`, verbatim from `87914794` (`path: /v1/traces`, `pathType: Prefix`, backend `{{ .Release.Name }}-api:8081`). It goes inside the existing `adminConsoleEnabled` gate, so laptop still publishes nothing.
   - The header comment (`:14`) and `charts/api/values.yaml:45` go back to naming `/admin`, `/health` and `/v1/traces`.
   - No annotation, CSP, CORS or NetworkPolicy change. The admin-api origin is already in both CSPs, CORS already allows `Authorization`/`Content-Type` with any method (`Program.cs:169-176`), and the api NetworkPolicy already admits ingress-nginx and the cloud LB ranges on 8081.
3. **Console and compose.** No change. Compose already publishes `127.0.0.1:8081`, sets `AdminConsole__Origin=http://localhost:5173`, and gives the relay `Jaeger__OtlpHttpUrl`.

## Testing and verification

1. **Real-endpoint gate test.**
   - Resolve the actual `POST /v1/traces` endpoint from the app's `EndpointDataSource`. `AuthenticationPipelineTests.cs:183/205` is the precedent.
   - Run it through `Program.ListenerPortGateAsync` with `Connection.LocalPort` set to 8081, then 8080. Both must reach `next`.
   - **Mutation proof:** restore the 8080 pin temporarily and watch the 8081 case fail.

   Main's existing gate unit tests and `TracesRelayEndpointTests` stay as they are.
2. **Helm renders** on all five profiles, with main's CI overrides:
   - the admin-api Ingress has `/v1/traces` → 8081 on local, aws, azure and gcp;
   - laptop has no admin-api Ingress;
   - main's api Ingress still has `/v1/traces` → 8080;
   - kubeconform finds 0 invalid.
3. **Comment sweep.** Correct every comment that describes the admin-api host's paths or says console span export is off. Known targets: `admin-api-ingress.yaml:14` and `charts/api/values.yaml:45`. Leave the notes that are already true after the fix, such as `telemetry.ts:22-26`, `Program.cs:159` and `docker-compose.yml:501`.
4. **Gates.** Full `Iverson.Api.Tests` and the Helm loop. No compose smoke test: the gate test proves the same routing fact without a running stack.

## Known issues / accepted as out of scope

- **Threat model (the user's decision: note it, don't edit).** The uncommitted rewrite of `docs/security/tma.md` in the main checkout adds a "Two-listener Kestrel topology" section. That section will be false after this change, and is already partly false after the admin-console merge, which serves authenticated `/admin/console/*` on 8081 through the admin-api Ingress. Its claims:
  - `/v1/traces` is served on `:8080` only;
  - `:8081` is health/observability, "passive-read-only", with "no writes reachable".

  After this change, `/v1/traces` (an authenticated POST relayed to Jaeger) answers on both listeners. Whoever finishes that rewrite should reflect both facts. This work does not touch `tma.md`.
- **Main's api Ingress on the nginx profiles forwards HTTP/1.1 to the h2c-only 8080.** No chart value sets `backend-protocol: GRPC` outside AWS, so per the probe above, everything that Ingress routes to 8080 on local should get a 400, gRPC included. This is pre-existing on main and outside this design's path.
- **UNVERIFIED, and moot:** how an AWS ALB `GRPC` target group treats a non-gRPC POST. It only mattered for option C, which fails on compose and nginx regardless.

## Verified assumptions

| # | Assumption | Evidence |
|---|---|---|
| 1 | Exactly one `RequireListenerPort(8080)` on the relay | `Program.cs:769`; the pin string occurs once |
| 2 | Unpinned endpoints pass the port gate on any listener | `ListenerPortGateAsync`, `Program.cs:793-803` (no metadata → `next()`); main's test `ListenerPortGate_UnmarkedEndpoint_InvokesNext` (`AuthenticationPipelineTests.cs:267`) |
| 3 | Unpinned endpoints stay under both global limiters | `Program.cs:117` and `:461` exempt only `…RequireListenerPort>()?.Port == 8081`, plus gRPC content type for post-auth |
| 4 | The `traces` policy is endpoint-attached and listener-independent | `.RequireRateLimiting("traces")`; policy at `Program.cs:144-152`, keyed by `sub` |
| 5 | JWT auth doesn't depend on the listener | `AddJwtBearer` (`:182`, `:206`) and `FallbackPolicy` (`:235`) carry no listener condition |
| 6 | 8081 is `Http1` in every deployment | `appsettings.json` Kestrel endpoints; no `Kestrel__*`/`ASPNETCORE_URLS` override in Helm, compose or the Dockerfile |
| 7 | The relay is mapped in the `api` role, and the api Service exposes 8081 | `Program.cs:715` block; `charts/api/templates/service.yaml:13-14` |
| 8 | The branch-tip rule restores cleanly | `git show 87914794:…/admin-api-ingress.yaml` `:51-57` |
| 9 | Browsers and ingress-nginx can't reach 8080 over HTTP/1.1 | Kestrel probe above (400 vs 200); `backend-protocol` only at `values-aws.yaml:91` |
| 10 | Exporter transport and headers | `otlp-exporter-base/build/esm/transport/fetch-transport.js:52-76` (fetch, `keepalive`, `mode: 'cors'`); `OTLPTraceExporter.js:13` (`Content-Type: application/json`); `telemetry.ts:65-88` (bearer header) |
| 11 | Keepalive plus CORS preflight works in current browsers | Doc-tier: the restriction was old Chromium only (~v70). See Chromium issue [41384936](https://issues.chromium.org/issues/41384936) and mdn/browser-compat-data [#16414](https://github.com/mdn/browser-compat-data/issues/16414) |
| 12 | Both CSPs allow the admin-api origin | `nginx.conf` `connect-src 'self' ${ADMIN_API_ORIGIN}`; admin-ui `ingress.yaml` snippet `admin-api.%s` |
| 13 | Compose wiring | `docker-compose.yml:454-455` (ports), `:497` (Jaeger), `:504` (CORS origin); `.env.development:3` |
| 14 | Nothing asserts or blocks the pin | No test pins `/v1/traces` (it appears only in `TracesRelayEndpointTests`, in-process with `LocalPort` 0); `templates/networkpolicies.yaml` api-ingress admits ingress-nginx and `clusterCidrs` on 8081 |
| 15 | The console calls no other 8080-pinned route | It calls only `/admin/console/*` (unpinned) and `/health` (pinned 8081); `api/console.ts` |
| 16 | Laptop renders no admin-api Ingress | `adminConsoleEnabled` is false by default and unset in `values-laptop.yaml` (the I1 renders on `a55e53ac`) |
