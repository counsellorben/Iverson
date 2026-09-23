import { describe, it, expect, vi, afterEach } from "vitest";
import { absoluteUrl, getJson } from "./client";
import type { HealthResponse, MetricsResponse, TenantsResponse } from "./types";

const TOKEN = "test-access-token";

function jsonResponse(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

/**
 * Queues responses for successive `fetch` calls. Anything that is not a `Response` is
 * THROWN rather than returned — `DOMException` is not an `instanceof Error` in jsdom, so
 * testing for `Error` here would silently hand an abort back as if it were a response.
 */
function stubFetch(...responses: unknown[]): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async () => {
    if (responses.length === 0) throw new Error("no stubbed response left");
    const next = responses.shift();
    if (next instanceof Response) return next;
    throw next;
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

const signal = () => new AbortController().signal;

describe("getJson", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("composes an absolute URL against the admin-api origin, trimming a trailing slash", () => {
    // vitest.config.ts pins VITE_API_BASE_URL to http://localhost:8080.
    expect(absoluteUrl("/admin/console/tenants")).toBe(
      "http://localhost:8080/admin/console/tenants"
    );
    expect(absoluteUrl("admin/console/tenants")).toBe(
      "http://localhost:8080/admin/console/tenants"
    );
  });

  it("returns the parsed body on a 200", async () => {
    stubFetch(jsonResponse({ count: 0, tenants: [] }, 200));

    const result = await getJson<TenantsResponse>(
      "/admin/console/tenants",
      TOKEN,
      signal()
    );

    expect(result).toEqual({ kind: "ok", status: 200, data: { count: 0, tenants: [] } });
  });

  it("sends the bearer token and omits cookies, which the server's CORS policy forbids", async () => {
    const fetchMock = stubFetch(jsonResponse({ count: 0, tenants: [] }, 200));

    await getJson("/admin/console/tenants", TOKEN, signal());

    const init = fetchMock.mock.calls[0][1] as RequestInit;
    expect((init.headers as Record<string, string>).Authorization).toBe(`Bearer ${TOKEN}`);
    expect(init.credentials).toBe("omit");
    expect(init.mode).toBe("cors");
  });

  it("surfaces a 503 reason as a problem rather than collapsing it into a failure", async () => {
    stubFetch(
      jsonResponse(
        { reason: "notDeployed", error: "Prometheus is not deployed in this environment." },
        503
      )
    );

    const result = await getJson<MetricsResponse>(
      "/admin/console/metrics",
      TOKEN,
      signal()
    );

    expect(result).toEqual({
      kind: "problem",
      status: 503,
      reason: "notDeployed",
      error: "Prometheus is not deployed in this environment.",
      body: {
        reason: "notDeployed",
        error: "Prometheus is not deployed in this environment.",
      },
    });
  });

  it("treats a declared body-bearing status as the answer, which is how /health reports degraded", async () => {
    stubFetch(
      jsonResponse(
        { status: "degraded", checks: { postgres: true, starrocks: "disabled", qdrant: true, kafka: false } },
        503
      )
    );

    const result = await getJson<HealthResponse>("/health", TOKEN, signal(), {
      bodyBearingStatuses: [503],
    });

    expect(result.kind).toBe("ok");
    if (result.kind !== "ok") throw new Error("unreachable");
    expect(result.status).toBe(503);
    expect(result.data.checks.starrocks).toBe("disabled");
  });

  it("reports the server's empty-bodied 403 as forbidden, not as a generic failure", async () => {
    // What the server ACTUALLY sends: RequireAuthorization("Operator") ends at the stock
    // AuthorizationMiddlewareResultHandler, which emits a 403 with no body at all. The
    // outcome has to come from the status, because there is nothing else to read.
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 403 })));

    const result = await getJson("/admin/console/tenants", TOKEN, signal());

    expect(result).toEqual({ kind: "forbidden", status: 403, error: null });
  });

  it("carries an error string on a 403 that does happen to have a body", async () => {
    stubFetch(jsonResponse({ error: "operators only" }, 403));

    const result = await getJson("/admin/console/tenants", TOKEN, signal());

    expect(result).toEqual({ kind: "forbidden", status: 403, error: "operators only" });
  });

  it("reports a 401 as unauthorized", async () => {
    stubFetch(jsonResponse({}, 401));

    await expect(
      getJson("/admin/console/tenants", TOKEN, signal())
    ).resolves.toEqual({ kind: "unauthorized", status: 401 });
  });

  it("reports a transport failure with a null status", async () => {
    stubFetch(new TypeError("Failed to fetch"));

    const result = await getJson("/admin/console/tenants", TOKEN, signal());

    expect(result).toEqual({
      kind: "failed",
      status: null,
      message: "Failed to fetch",
    });
  });

  it("reports a non-2xx whose body is not JSON as a plain failure", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response("<html>502 Bad Gateway</html>", { status: 502 }))
    );

    const result = await getJson("/admin/console/tenants", TOKEN, signal());

    expect(result).toEqual({ kind: "failed", status: 502, message: "HTTP 502" });
  });

  it("reports a 2xx whose body is not JSON as a failure rather than as ok", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response("not json", { status: 200 }))
    );

    const result = await getJson("/admin/console/tenants", TOKEN, signal());

    expect(result).toEqual({
      kind: "failed",
      status: 200,
      message: "HTTP 200 with an unreadable JSON body",
    });
  });

  it("propagates an abort instead of reporting it as a failure", async () => {
    stubFetch(new DOMException("The operation was aborted.", "AbortError"));

    await expect(
      getJson("/admin/console/tenants", TOKEN, signal())
    ).rejects.toThrow("The operation was aborted.");
  });
});
