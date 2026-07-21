import { WebTracerProvider, BatchSpanProcessor } from "@opentelemetry/sdk-trace-web";
import { OTLPTraceExporter } from "@opentelemetry/exporter-trace-otlp-http";
import { FetchInstrumentation } from "@opentelemetry/instrumentation-fetch";
import { DocumentLoadInstrumentation } from "@opentelemetry/instrumentation-document-load";
import { registerInstrumentations } from "@opentelemetry/instrumentation";
import { resourceFromAttributes } from "@opentelemetry/resources";
import { WebStorageStateStore } from "oidc-client-ts";
import { config } from "./config";

// react-oidc-context (see auth/AuthProvider.tsx) doesn't set an explicit `userStore` in its
// UserManager settings, so oidc-client-ts falls back to its own default: a WebStorageStateStore
// backed by sessionStorage. This must match that default exactly, or the token read here will
// silently miss the session react-oidc-context actually created.
const userStore = new WebStorageStateStore({ store: window.sessionStorage });

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
 * are exported as spans to `/v1/traces` — a same-origin, authenticated route on Iverson.Api
 * (see Program.cs) that relays them byte-for-byte to Jaeger's OTLP/HTTP endpoint. Same-origin
 * keeps the browser from ever needing Jaeger's own network address or CORS configuration.
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
          url: "/v1/traces",
          headers: getAuthHeaders,
        })
      ),
    ],
  });

  provider.register();

  registerInstrumentations({
    instrumentations: [new FetchInstrumentation(), new DocumentLoadInstrumentation()],
  });
}
