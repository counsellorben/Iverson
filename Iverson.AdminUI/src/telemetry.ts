import { WebTracerProvider, BatchSpanProcessor } from "@opentelemetry/sdk-trace-web";
import { OTLPTraceExporter } from "@opentelemetry/exporter-trace-otlp-http";
import { FetchInstrumentation } from "@opentelemetry/instrumentation-fetch";
import { DocumentLoadInstrumentation } from "@opentelemetry/instrumentation-document-load";
import { registerInstrumentations } from "@opentelemetry/instrumentation";
import { resourceFromAttributes } from "@opentelemetry/resources";
import { WebStorageStateStore } from "oidc-client-ts";
import type { Span } from "@opentelemetry/api";
import { config } from "./config";
import { absoluteUrl } from "./api/client";

// react-oidc-context (see auth/AuthProvider.tsx) doesn't set an explicit `userStore` in its
// UserManager settings, so oidc-client-ts falls back to its own default: a WebStorageStateStore
// backed by sessionStorage. This must match that default exactly, or the token read here will
// silently miss the session react-oidc-context actually created.
const userStore = new WebStorageStateStore({ store: window.sessionStorage });

// URL for exporting traces to Iverson.Api; used by both the OTLPTraceExporter and
// FetchInstrumentation's ignoreUrls to avoid instrumental feedback (fetch-instrumented
// spans from the trace export endpoint being re-exported in the next batch).
//
// Composed from the configured API base rather than written as the bare path "/v1/traces".
// The console is served from its own hostname and the API from a dedicated `admin-api` one,
// so a relative path resolves against the CONSOLE's nginx, which has no /v1/traces route:
// every span export would 404 and tracing would be silently dead. `absoluteUrl` is the same
// composition the fetch layer uses, so there is one definition of "where the API lives".
export const OTLP_TRACES_URL = absoluteUrl("/v1/traces");

/** Escapes a URL for literal use inside a RegExp — `.` and `?` in a URL are not wildcards. */
function escapeForRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * Overwrites a document span's URL attributes with the bare path.
 *
 * The document-load and document-fetch spans record the URL the browser actually loaded. On
 * the OIDC redirect that URL is `/callback?code=…&state=…`, so the authorization code is
 * carried into the span and exported to Jaeger. The `onSigninCallback` scrub in
 * `auth/AuthProvider.tsx` clears the address bar on the success path, but it runs AFTER these
 * spans are created and never runs at all on the error path — `CallbackPage` renders the
 * failure and never navigates. This hook is therefore the one that actually holds on the path
 * where the exchange fails, which is also the path most likely to be traced and inspected.
 *
 * Both the current (`url.full`) and legacy (`http.url`) semantic-convention keys are
 * overwritten, because which one the SDK emits depends on the version's stability opt-in.
 */
export function scrubUrlFromSpan(span: Span): void {
  span.setAttribute("http.url", window.location.pathname);
  span.setAttribute("url.full", window.location.pathname);
}

// Mirrors oidc-client-ts's internal UserManager._userStoreKey format
// (`user:${authority}:${client_id}`, with the "oidc." prefix applied by the store itself) —
// there is no public API to ask oidc-client-ts for "the current user" outside of a React hook,
// and this needs to run once at app startup, before React (and useAuth()) exist.
function userStoreKey(): string {
  return `user:${config.oidcAuthority}:${config.oidcClientId}`;
}

async function getAuthHeaders(): Promise<Record<string, string>> {
  const raw = await userStore.get(userStoreKey());
  if (!raw) return {};
  try {
    const accessToken = (JSON.parse(raw) as { access_token?: string }).access_token;
    return accessToken ? { Authorization: `Bearer ${accessToken}` } : {};
  } catch {
    return {};
  }
}

/**
 * Initializes browser-side OpenTelemetry tracing: fetch calls and the initial document load
 * are exported as spans to the API's `/v1/traces` — an authenticated route on Iverson.Api
 * (see Program.cs) that relays them byte-for-byte to Jaeger's OTLP/HTTP endpoint, so the browser
 * never needs Jaeger's own network address or CORS configuration.
 *
 * Must be called once, before React renders (see main.tsx) — not a React hook, since spans
 * for the document-load instrumentation need to be captured from app startup.
 */
export function initTelemetry(): void {
  const provider = new WebTracerProvider({
    resource: resourceFromAttributes({ "service.name": "Iverson.AdminUI" }),
    spanProcessors: [
      new BatchSpanProcessor(
        new OTLPTraceExporter({
          url: OTLP_TRACES_URL,
          headers: getAuthHeaders,
        })
      ),
    ],
  });

  provider.register();

  registerInstrumentations({
    instrumentations: [
      new FetchInstrumentation({
        ignoreUrls: [new RegExp(`${escapeForRegExp(OTLP_TRACES_URL)}$`)],
      }),
      new DocumentLoadInstrumentation({
        // Not applied to `resourceFetch`: those spans describe sub-resources with URLs of
        // their own, which never carry the authorization code and which this would erase.
        applyCustomAttributesOnSpan: {
          documentLoad: scrubUrlFromSpan,
          documentFetch: scrubUrlFromSpan,
        },
      }),
    ],
  });
}
