import { config } from "../config";

/**
 * The console's fetch layer.
 *
 * ## Five outcomes, never flattened into one
 *
 * `getJson` returns a discriminated union rather than throwing, because four of the five
 * outcomes below are things a widget must RENDER, not things it should treat as "request
 * failed":
 *
 * | `kind`           | Meaning                                                              |
 * |------------------|----------------------------------------------------------------------|
 * | `"ok"`           | 2xx (or a declared body-bearing status) with a parsed JSON body.      |
 * | `"problem"`      | Non-2xx WITH a parsed JSON body — e.g. a 503 carrying `reason`.       |
 * | `"unauthorized"` | 401. The session is no longer valid; the widget shows nothing new.    |
 * | `"forbidden"`    | 403. This session may not read this endpoint. Derived from the STATUS.|
 * | `"failed"`       | Transport failure, or a non-2xx whose body is not JSON.               |
 *
 * The `"problem"` case is the one that is easy to get wrong. `/admin/console/metrics` answers
 * 503 with `reason: "notDeployed"` when Prometheus is simply not installed in this profile —
 * a supported deployment shape, not a fault — and `/admin/console/data-volume` answers 503
 * with `reason: "disabled"` for the same class of reason. Collapsing those into a thrown
 * error would render a red card where the honest output is "not deployed here". The parsed
 * body is therefore carried through on `body`, with `reason` and `error` lifted out for
 * convenience.
 *
 * A 401 is reported as `kind: "unauthorized"` and nothing more — there is no renewal path to
 * route it to. Main's auth configuration requests no `offline_access` scope, so
 * `oidc-client-ts` never holds a refresh token, and this app's CSP has no `frame-src`, so the
 * hidden-iframe silent renewal that `automaticSilentRenew` would need is blocked anyway. An
 * expired session is ended instead: `useSessionExpiry` calls `removeUser()` once the token's
 * own expiry fires, and that is what trips `AuthGate`'s redirect into the login flow.
 *
 * ## 403 is its own outcome, and it is derived from the status alone
 *
 * 403 is a real, per-endpoint authorization answer
 * that a fresh token cannot change, and it belongs on the widget that asked.
 *
 * It gets its own `kind` because **the server sends a 403 with NO BODY**.
 * `RequireAuthorization("Operator")` is handled by `AuditingAuthorizationMiddlewareResultHandler`,
 * which delegates to the stock `AuthorizationMiddlewareResultHandler` and emits an empty
 * response. Anything reading a `reason` or `error` out of a 403 body would be reading a field
 * that never arrives, and the outcome would degrade to a generic `"failed"` — a red "request
 * failed" card where "you are not authorized to see this" belongs. That is not a hypothetical:
 * the three Operator-gated endpoints (`tenants`, `qdrant`, `metrics`) 403 for every human until
 * an operator is onboarded — the `operators` Authentik group and its token claim both exist,
 * but group membership is a deliberate one-time manual step, not something blueprinted into any
 * deployment (see `docs/runbooks/operator-access-onboarding.md`). `error` is carried only on the
 * chance a future 403 does include a body, and is `null` for every 403 the server sends now.
 */

// ── Result contract ───────────────────────────────────────────────────────────

export interface ApiSuccess<T> {
  kind: "ok";
  status: number;
  data: T;
}

/** A non-2xx response that nevertheless carried a JSON body worth rendering. */
export interface ApiProblem {
  kind: "problem";
  status: number;
  /** The body's `reason` field if it had a string one, else null. */
  reason: string | null;
  /** The body's `error` field if it had a string one, else null. */
  error: string | null;
  /** The whole parsed body, for a caller that knows more about the shape than this layer does. */
  body: unknown;
}

export interface ApiUnauthorized {
  kind: "unauthorized";
  status: 401;
}

/**
 * 403 — authenticated, but not permitted to read this endpoint. Recognised by STATUS, because
 * the server's 403 has no body; `error` is `null` unless some future 403 supplies one.
 */
export interface ApiForbidden {
  kind: "forbidden";
  status: 403;
  error: string | null;
}

/** Transport failure (`status: null`) or an unreadable non-2xx response. */
export interface ApiFailed {
  kind: "failed";
  status: number | null;
  message: string;
}

export type ApiFailure = ApiProblem | ApiUnauthorized | ApiForbidden | ApiFailed;

export type ApiResult<T> = ApiSuccess<T> | ApiFailure;

/**
 * A fetcher in the shape `usePolledResource` drives. The access token is an ARGUMENT rather
 * than something this module reaches for, because `useAuth()` is React-context-only and a
 * module-scope fetch layer cannot see it.
 */
export type ApiFetcher<T> = (
  accessToken: string,
  signal: AbortSignal
) => Promise<ApiResult<T>>;

export function isOk<T>(result: ApiResult<T>): result is ApiSuccess<T> {
  return result.kind === "ok";
}

// ── Requests ──────────────────────────────────────────────────────────────────

export interface JsonRequestOptions {
  /**
   * Statuses whose JSON body IS the answer rather than a problem report. `/health` answers
   * 503 with exactly the same `{ status, checks }` body it returns with a 200 — the degraded
   * body is the thing the health strip renders, so treating it as an error would blank the
   * widget precisely when it has the most to say.
   */
  readonly bodyBearingStatuses?: readonly number[];
}

/**
 * Composes an absolute URL against the dedicated `admin-api` origin. The console is served
 * from a different host than the API, so every request here is cross-origin by construction
 * and a relative path would hit the console's own nginx.
 */
export function absoluteUrl(path: string): string {
  const base = config.apiBaseUrl.replace(/\/+$/, "");
  return path.startsWith("/") ? `${base}${path}` : `${base}/${path}`;
}

export async function getJson<T>(
  path: string,
  accessToken: string,
  signal: AbortSignal,
  options: JsonRequestOptions = {}
): Promise<ApiResult<T>> {
  let response: Response;
  try {
    response = await fetch(absoluteUrl(path), {
      method: "GET",
      signal,
      headers: {
        Accept: "application/json",
        Authorization: `Bearer ${accessToken}`,
      },
      // The bearer token is the only credential this API accepts, and the server's CORS
      // policy deliberately does not set AllowCredentials — asking for cookies here would
      // fail the CORS check outright.
      credentials: "omit",
      mode: "cors",
    });
  } catch (error) {
    // An abort is the caller superseding or unmounting this request, not a failure to
    // report. It propagates so the hook can drop the result silently.
    if (isAbort(error)) throw error;
    return { kind: "failed", status: null, message: describeError(error) };
  }

  if (response.status === 401) {
    return { kind: "unauthorized", status: 401 };
  }

  const bodyBearing =
    response.ok || (options.bodyBearingStatuses?.includes(response.status) ?? false);

  let body: unknown;
  let parsed = true;
  try {
    body = await response.json();
  } catch (error) {
    if (isAbort(error)) throw error;
    parsed = false;
  }

  if (response.status === 403) {
    return { kind: "forbidden", status: 403, error: readStringField(body, "error") };
  }

  if (bodyBearing) {
    return parsed
      ? { kind: "ok", status: response.status, data: body as T }
      : {
          kind: "failed",
          status: response.status,
          message: `HTTP ${response.status} with an unreadable JSON body`,
        };
  }

  return parsed
    ? {
        kind: "problem",
        status: response.status,
        reason: readStringField(body, "reason"),
        error: readStringField(body, "error"),
        body,
      }
    : { kind: "failed", status: response.status, message: `HTTP ${response.status}` };
}

function readStringField(body: unknown, field: string): string | null {
  if (typeof body !== "object" || body === null) return null;
  const value = (body as Record<string, unknown>)[field];
  return typeof value === "string" ? value : null;
}

export function isAbort(error: unknown): boolean {
  return (
    typeof error === "object" &&
    error !== null &&
    "name" in error &&
    (error as { name?: unknown }).name === "AbortError"
  );
}

export function describeError(error: unknown): string {
  if (error instanceof Error) return error.message;
  return String(error);
}
