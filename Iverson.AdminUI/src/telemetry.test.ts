import { describe, it, expect, vi, beforeEach } from "vitest";

/**
 * Wiring tests for the browser tracer. Every OpenTelemetry package is mocked so the
 * CONFIGURATION handed to each constructor can be inspected — the security-relevant behaviour
 * here (never exporting an OIDC authorization code, and exporting to the API's host rather
 * than the console's own nginx) lives entirely in that configuration, not in code that runs.
 */

const exporterConfigs: Record<string, unknown>[] = [];
const fetchInstrumentationConfigs: Record<string, unknown>[] = [];
const documentLoadConfigs: Record<string, unknown>[] = [];

vi.mock("@opentelemetry/sdk-trace-web", () => ({
  WebTracerProvider: class {
    register() {}
  },
  BatchSpanProcessor: class {
    constructor(public exporter: unknown) {}
  },
}));

vi.mock("@opentelemetry/exporter-trace-otlp-http", () => ({
  OTLPTraceExporter: class {
    constructor(config: Record<string, unknown>) {
      exporterConfigs.push(config);
    }
  },
}));

vi.mock("@opentelemetry/instrumentation-fetch", () => ({
  FetchInstrumentation: class {
    constructor(config: Record<string, unknown>) {
      fetchInstrumentationConfigs.push(config);
    }
  },
}));

vi.mock("@opentelemetry/instrumentation-document-load", () => ({
  DocumentLoadInstrumentation: class {
    constructor(config: Record<string, unknown>) {
      documentLoadConfigs.push(config);
    }
  },
}));

vi.mock("@opentelemetry/instrumentation", () => ({
  registerInstrumentations: () => {},
}));

vi.mock("@opentelemetry/resources", () => ({
  resourceFromAttributes: (attrs: unknown) => attrs,
}));

import { initTelemetry, scrubUrlFromSpan, OTLP_TRACES_URL } from "./telemetry";

type SpanHook = (span: { setAttribute: (key: string, value: unknown) => void }) => void;

function recordingSpan() {
  const attributes: Record<string, unknown> = {};
  return {
    attributes,
    setAttribute(key: string, value: unknown) {
      attributes[key] = value;
    },
  };
}

describe("initTelemetry", () => {
  beforeEach(() => {
    exporterConfigs.length = 0;
    fetchInstrumentationConfigs.length = 0;
    documentLoadConfigs.length = 0;
  });

  it("exports traces to the configured API host, not to a path on the console's own origin", () => {
    // vitest.config.ts sets VITE_API_BASE_URL=http://localhost:8080. The console is served from
    // a different hostname than the API, so a bare "/v1/traces" would resolve against the
    // console's nginx — which has no such route — and every span export would 404 silently.
    initTelemetry();

    expect(OTLP_TRACES_URL).toBe("http://localhost:8080/v1/traces");
    expect(exporterConfigs[0].url).toBe("http://localhost:8080/v1/traces");
  });

  it("ignores its own export endpoint so trace exports do not trace themselves", () => {
    initTelemetry();

    const ignoreUrls = fetchInstrumentationConfigs[0].ignoreUrls as RegExp[];
    expect(ignoreUrls.some((pattern) => pattern.test(OTLP_TRACES_URL))).toBe(true);
    expect(ignoreUrls.some((pattern) => pattern.test("http://localhost:8080/health"))).toBe(false);
  });

  it("scrubs the document span URLs, which is the ONLY cover on the failed-callback path", () => {
    // CallbackPage never navigates when the code exchange errors, so onSigninCallback never
    // fires and the address bar keeps ?code=. If this hook is not wired, the authorization
    // code is exported to Jaeger on exactly the path an operator is most likely to inspect.
    initTelemetry();

    const hooks = documentLoadConfigs[0].applyCustomAttributesOnSpan as {
      documentLoad?: SpanHook;
      documentFetch?: SpanHook;
    };
    expect(hooks?.documentLoad).toBeTypeOf("function");
    expect(hooks?.documentFetch).toBeTypeOf("function");

    window.history.replaceState({}, "", "/callback?code=secret-auth-code&state=abc123");

    for (const hook of [hooks.documentLoad!, hooks.documentFetch!]) {
      const span = recordingSpan();
      hook(span);
      expect(span.attributes["http.url"]).toBe("/callback");
      expect(span.attributes["url.full"]).toBe("/callback");
      expect(JSON.stringify(span.attributes)).not.toContain("secret-auth-code");
    }
  });

  it("escapes regex metacharacters in the API host, so a lookalike URL is not also ignored", async () => {
    // The configured test base URL is `http://localhost:8080` — no dots, no metacharacters —
    // so the escaping in telemetry.ts is INVISIBLE to the assertions above: deleting it leaves
    // them green. Re-import the module against a dotted host, where an unescaped `.` becomes a
    // wildcard and the ignore pattern silently widens to match hosts it was never meant to.
    vi.resetModules();
    vi.doMock("./config", () => ({
      config: {
        oidcClientId: "test-oidc-client-id",
        oidcAuthority: "http://localhost:9000/application/o/test/",
        apiBaseUrl: "https://admin-api.iverson.example",
      },
    }));

    try {
      const fresh = await import("./telemetry");
      fresh.initTelemetry();

      const ignoreUrls = fetchInstrumentationConfigs.at(-1)!.ignoreUrls as RegExp[];
      const matches = (url: string) => ignoreUrls.some((pattern) => pattern.test(url));

      // The real export endpoint is still ignored...
      expect(matches("https://admin-api.iverson.example/v1/traces")).toBe(true);
      // ...but a host that merely has the same shape is not. Unescaped, every `.` matches any
      // character, so this would match and that host's spans would be dropped without trace.
      expect(matches("https://admin-apiXiversonXexample/v1/traces")).toBe(false);
      expect(matches("https://admin-api-iverson-example/v1/traces")).toBe(false);
    } finally {
      vi.doUnmock("./config");
      vi.resetModules();
    }
  });

  it("does not scrub resourceFetch spans, whose URLs are legitimate sub-resource URLs", () => {
    initTelemetry();

    const hooks = documentLoadConfigs[0].applyCustomAttributesOnSpan as
      | Record<string, unknown>
      | undefined;
    expect(hooks?.resourceFetch).toBeUndefined();
  });
});

describe("scrubUrlFromSpan", () => {
  it("overwrites both the current and legacy URL attribute keys", () => {
    window.history.replaceState({}, "", "/callback?code=secret-auth-code&state=abc123");
    const span = recordingSpan();
    span.setAttribute("http.url", "http://console.example/callback?code=secret-auth-code");
    span.setAttribute("url.full", "http://console.example/callback?code=secret-auth-code");

    scrubUrlFromSpan(span as never);

    expect(span.attributes["http.url"]).toBe("/callback");
    expect(span.attributes["url.full"]).toBe("/callback");
  });
});
