import { getJson } from "./client";
import type { ApiFetcher } from "./client";
import type {
  DataVolumeResponse,
  HealthResponse,
  MetricsResponse,
  QdrantResponse,
  SchemaCatalogResponse,
  TenantsResponse,
} from "./types";

/**
 * The console's six read endpoints, one typed fetcher each, in the shape
 * `usePolledResource` drives.
 *
 * These are module-level constants on purpose: a widget can pass one straight to the hook
 * without a `useCallback`, and a stable fetcher identity is what stops the hook's scheduling
 * effects from tearing down and restarting on every render.
 *
 * Every one of them is a GET, and none of them takes a caller-supplied parameter — the
 * server exposes fixed projections, not a query interface.
 */

export const CONSOLE_ROUTE_PREFIX = "/admin/console";

/**
 * Operator-gated: a cross-tenant enumeration. A non-Operator gets `kind: "forbidden"` with a
 * null `error` — the server's 403 carries no body. UNTIL AN OPERATOR IS ONBOARDED, this is the
 * outcome every human gets: the `operators` Authentik group exists and its claim is wired
 * through, but membership is a deliberate one-time manual step (not blueprinted, to avoid
 * hardcoding a privilege escalation) — see
 * `docs/runbooks/operator-access-onboarding.md`. Render it as "not authorized", not as a
 * request failure.
 */
export const fetchTenants: ApiFetcher<TenantsResponse> = (token, signal) =>
  getJson<TenantsResponse>(`${CONSOLE_ROUTE_PREFIX}/tenants`, token, signal);

/**
 * Authenticated, not Operator-gated: it filters per-row and per-field internally. Check
 * `withheldTypeCount` before rendering an empty catalog as "no types".
 */
export const fetchSchema: ApiFetcher<SchemaCatalogResponse> = (token, signal) =>
  getJson<SchemaCatalogResponse>(`${CONSOLE_ROUTE_PREFIX}/schema`, token, signal);

/**
 * EXPENSIVE. One sequential StarRocks `COUNT(*)` per registered type, per request, uncached,
 * with row authorization re-evaluated per type — so its cost scales with the type registry
 * AND with the number of open browser tabs. Fetch on mount and on manual refresh; do not put
 * it on a short poll.
 *
 * Answers 503 with `reason` `"notReady"` or `"disabled"`, which arrive as `kind: "problem"`.
 */
export const fetchDataVolume: ApiFetcher<DataVolumeResponse> = (token, signal) =>
  getJson<DataVolumeResponse>(`${CONSOLE_ROUTE_PREFIX}/data-volume`, token, signal);

/**
 * Operator-gated: collection names are tenant-scoped, so enumerating them is cross-tenant.
 * Answers `kind: "forbidden"` for every human today — see {@link fetchTenants}.
 */
export const fetchQdrant: ApiFetcher<QdrantResponse> = (token, signal) =>
  getJson<QdrantResponse>(`${CONSOLE_ROUTE_PREFIX}/qdrant`, token, signal);

/**
 * Operator-gated, so `kind: "forbidden"` for every human today — see {@link fetchTenants}.
 * Otherwise answers 503 with `reason` `"notDeployed"` (Prometheus is not installed in
 * this profile — a supported configuration) or `"unreachable"` (installed but not
 * answering); both arrive as `kind: "problem"` with the reason lifted out. Individual metric
 * values may also be `null`, meaning "no current sample", which is not zero.
 */
export const fetchMetrics: ApiFetcher<MetricsResponse> = (token, signal) =>
  getJson<MetricsResponse>(`${CONSOLE_ROUTE_PREFIX}/metrics`, token, signal);

/**
 * `/health` is anonymous, but it is routed on the same `admin-api` host and the bearer token
 * is sent anyway for consistency.
 *
 * IT ANSWERS 503 WITH THE SAME BODY IT ANSWERS 200 WITH — the degraded `checks` object is
 * exactly what the health strip renders — so 503 is declared body-bearing here and arrives as
 * `kind: "ok"`. Read `data.status` to tell healthy from degraded; `result.status` carries the
 * HTTP code if a caller wants it.
 *
 * It is also write-bearing (it drives readiness), which is why Task 10 polls it at 60s rather
 * than 30s.
 */
export const fetchHealth: ApiFetcher<HealthResponse> = (token, signal) =>
  getJson<HealthResponse>("/health", token, signal, { bodyBearingStatuses: [503] });
