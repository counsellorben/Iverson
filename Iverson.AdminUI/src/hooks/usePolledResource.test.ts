import { renderHook, act } from "@testing-library/react";
import type { RenderHookResult } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { usePolledResource } from "./usePolledResource";
import type { ApiFetcher, ApiResult } from "../api/client";

interface Payload {
  value: number;
}

const TOKEN = "test-access-token";

const ok = (value: number): ApiResult<Payload> => ({
  kind: "ok",
  status: 200,
  data: { value },
});

const failed: ApiResult<Payload> = {
  kind: "failed",
  status: null,
  message: "network down",
};

/** jsdom's `visibilityState` is a getter, so it has to be redefined rather than assigned. */
function defineVisibility(state: "visible" | "hidden"): void {
  Object.defineProperty(document, "visibilityState", {
    configurable: true,
    get: () => state,
  });
}

/** Change the visibility AND fire the event a mounted hook listens for. */
function setVisibility(state: "visible" | "hidden"): void {
  defineVisibility(state);
  document.dispatchEvent(new Event("visibilitychange"));
}

/**
 * Move the fake clock and let every promise chain it released settle. The extra microtask
 * turns are not decoration: an attempt awaits the fetcher and only then sets state, so the
 * state update lands a tick or two after `advanceTimersByTimeAsync` returns.
 */
async function settle(ms: number): Promise<void> {
  await vi.advanceTimersByTimeAsync(ms);
  await Promise.resolve();
  await Promise.resolve();
  await Promise.resolve();
}

/** `settle`, wrapped in `act` for the common case where nothing else happens inside. */
async function advance(ms: number): Promise<void> {
  await act(async () => {
    await settle(ms);
  });
}

/**
 * Mount and let the first fetch settle, both INSIDE one `act`. The mount effect's fetch
 * resolves on a microtask after `renderHook` returns, so mounting outside `act` puts that
 * first state update outside it too — React reports it, and the escaped update is a real
 * timing hole, not just noise.
 */
async function mount<R>(render: () => R): Promise<RenderHookResult<R, unknown>> {
  let handle!: RenderHookResult<R, unknown>;
  await act(async () => {
    handle = renderHook(render);
    await settle(0);
  });
  return handle;
}

async function mountWith<P, R>(
  render: (props: P) => R,
  initialProps: P
): Promise<RenderHookResult<R, P>> {
  let handle!: RenderHookResult<R, P>;
  await act(async () => {
    handle = renderHook(render, { initialProps });
    await settle(0);
  });
  return handle;
}

describe("usePolledResource", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    // Pinned so the "as of HH:MM" assertions are not clock-dependent.
    vi.setSystemTime(new Date(2026, 7, 25, 14, 5, 0));
  });

  afterEach(() => {
    // Restore the property WITHOUT dispatching: React Testing Library's automatic cleanup
    // runs after this hook, so anything still mounted here would take the event — and its
    // state update would land outside `act`.
    defineVisibility("visible");
    vi.useRealTimers();
  });

  describe("polling", () => {
    it("fetches once on mount and then again on each interval boundary", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(ok(1));

      await mount(() => usePolledResource(fetcher, 30_000, TOKEN));
      expect(fetcher).toHaveBeenCalledTimes(1);

      // Just short of the interval: still one. This is what separates "polls on its
      // interval" from "polls on some interval".
      await advance(29_999);
      expect(fetcher).toHaveBeenCalledTimes(1);

      await advance(1);
      expect(fetcher).toHaveBeenCalledTimes(2);

      await advance(30_000);
      expect(fetcher).toHaveBeenCalledTimes(3);
    });

    it("passes the access token and an abort signal to the fetcher", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(ok(1));

      await mount(() => usePolledResource(fetcher, 30_000, TOKEN));

      expect(fetcher).toHaveBeenCalledWith(TOKEN, expect.any(AbortSignal));
    });

    it("does not fetch at all until an access token is available", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(ok(1));

      const { result, rerender } = await mountWith(
        ({ token }: { token: string | undefined }) =>
          usePolledResource(fetcher, 30_000, token),
        { token: undefined as string | undefined }
      );

      await advance(90_000);
      expect(fetcher).not.toHaveBeenCalled();
      expect(result.current.loading).toBe(true);

      await act(async () => {
        rerender({ token: TOKEN });
        await settle(0);
      });
      expect(fetcher).toHaveBeenCalledTimes(1);
    });

    it("fetches once and never polls when the interval is null", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(ok(1));

      await mount(() => usePolledResource(fetcher, null, TOKEN));
      expect(fetcher).toHaveBeenCalledTimes(1);

      await advance(10 * 60_000);
      expect(fetcher).toHaveBeenCalledTimes(1);
    });

    it("stops polling once unmounted", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(ok(1));

      const { unmount } = await mount(() => usePolledResource(fetcher, 1_000, TOKEN));
      expect(fetcher).toHaveBeenCalledTimes(1);

      unmount();
      await advance(60_000);
      expect(fetcher).toHaveBeenCalledTimes(1);
    });
  });

  describe("backoff", () => {
    it("doubles the delay after each failure, then stops once it would pass the ceiling", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(failed);

      const { result } = await mount(() =>
        usePolledResource(fetcher, 1_000, TOKEN, { backoffCeilingMs: 8_000 })
      );
      expect(fetcher).toHaveBeenCalledTimes(1);

      // A failure must NOT retry on the plain 1s interval — the first retry is at 2s.
      await advance(1_000);
      expect(fetcher).toHaveBeenCalledTimes(1);
      await advance(1_000);
      expect(fetcher).toHaveBeenCalledTimes(2);

      // ...then 4s, not another 2s.
      await advance(3_999);
      expect(fetcher).toHaveBeenCalledTimes(2);
      await advance(1);
      expect(fetcher).toHaveBeenCalledTimes(3);

      // ...then 8s, which is exactly the ceiling and so is still attempted.
      await advance(7_999);
      expect(fetcher).toHaveBeenCalledTimes(3);
      await advance(1);
      expect(fetcher).toHaveBeenCalledTimes(4);

      // The next delay would be 16s, past the ceiling: it stops rather than plateauing.
      expect(result.current.exhausted).toBe(true);
      await advance(60 * 60_000);
      expect(fetcher).toHaveBeenCalledTimes(4);
    });

    it("resumes from a stopped state on manual refresh, with backoff reset", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(failed);

      const { result } = await mount(() =>
        usePolledResource(fetcher, 1_000, TOKEN, { backoffCeilingMs: 2_000 })
      );
      await advance(2_000);
      expect(fetcher).toHaveBeenCalledTimes(2);
      expect(result.current.exhausted).toBe(true);

      await act(async () => {
        result.current.refresh();
        await settle(0);
      });
      expect(fetcher).toHaveBeenCalledTimes(3);
      expect(result.current.exhausted).toBe(false);

      // Backoff restarted from the base, so the next retry is 2s away again rather than
      // still being stopped.
      await advance(1_999);
      expect(fetcher).toHaveBeenCalledTimes(3);
      await advance(1);
      expect(fetcher).toHaveBeenCalledTimes(4);
    });

    it("clears backoff and returns to the plain interval after a recovery", async () => {
      const fetcher = vi
        .fn<ApiFetcher<Payload>>()
        .mockResolvedValueOnce(failed)
        .mockResolvedValue(ok(3));

      const { result } = await mount(() => usePolledResource(fetcher, 1_000, TOKEN));
      expect(fetcher).toHaveBeenCalledTimes(1);

      await advance(2_000); // the backed-off retry, which succeeds
      expect(fetcher).toHaveBeenCalledTimes(2);
      expect(result.current.stale).toBe(false);
      expect(result.current.failure).toBeNull();

      await advance(1_000); // back on the plain interval
      expect(fetcher).toHaveBeenCalledTimes(3);
    });

    it("backs off a 503 problem the same way, while keeping its reason readable", async () => {
      const notDeployed: ApiResult<Payload> = {
        kind: "problem",
        status: 503,
        reason: "notDeployed",
        error: "Prometheus is not deployed in this environment.",
        body: { reason: "notDeployed" },
      };
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(notDeployed);

      const { result } = await mount(() =>
        usePolledResource(fetcher, 1_000, TOKEN, { backoffCeilingMs: 1_500 })
      );

      expect(result.current.failure).toEqual(notDeployed);
      // 2s would pass the 1.5s ceiling, so a permanently-unavailable source is not polled
      // forever.
      expect(result.current.exhausted).toBe(true);
      await advance(60_000);
      expect(fetcher).toHaveBeenCalledTimes(1);
    });
  });

  describe("stale retention", () => {
    it("keeps the last good value and its timestamp when a later poll fails", async () => {
      const fetcher = vi
        .fn<ApiFetcher<Payload>>()
        .mockResolvedValueOnce(ok(7))
        .mockResolvedValue(failed);

      const { result } = await mount(() => usePolledResource(fetcher, 60_000, TOKEN));

      expect(result.current.data).toEqual({ value: 7 });
      expect(result.current.loading).toBe(false);
      expect(result.current.stale).toBe(false);
      expect(result.current.asOf).toBe("14:05");

      await advance(60_000); // the clock is now 14:06, and this poll fails

      // The retained value, not a spinner and not null.
      expect(result.current.data).toEqual({ value: 7 });
      expect(result.current.loading).toBe(false);
      expect(result.current.stale).toBe(true);
      expect(result.current.failure).toEqual(failed);
      // Still the timestamp of the value actually on screen — 14:05, not "now".
      expect(result.current.asOf).toBe("14:05");
    });

    it("reports no data and no staleness when the very first attempt fails", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(failed);

      const { result } = await mount(() => usePolledResource(fetcher, 60_000, TOKEN));

      expect(result.current.data).toBeNull();
      expect(result.current.loading).toBe(false);
      // Nothing is being retained, so nothing is stale — the widget shows its error state.
      expect(result.current.stale).toBe(false);
      expect(result.current.asOf).toBeNull();
      expect(result.current.failure).toEqual(failed);
    });

    it("drops the stale flag once a poll succeeds again", async () => {
      const fetcher = vi
        .fn<ApiFetcher<Payload>>()
        .mockResolvedValueOnce(ok(1))
        .mockResolvedValueOnce(failed)
        .mockResolvedValue(ok(2));

      const { result } = await mount(() => usePolledResource(fetcher, 1_000, TOKEN));
      await advance(1_000);
      expect(result.current.stale).toBe(true);

      await advance(2_000); // backed-off retry, which succeeds
      expect(result.current.data).toEqual({ value: 2 });
      expect(result.current.stale).toBe(false);
      expect(result.current.failure).toBeNull();
    });
  });

  describe("visibility", () => {
    it("stops the timer while the tab is hidden and resumes on return", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(ok(1));

      const { result } = await mount(() => usePolledResource(fetcher, 1_000, TOKEN));
      expect(fetcher).toHaveBeenCalledTimes(1);

      act(() => setVisibility("hidden"));
      expect(result.current.paused).toBe(true);

      // Sixty intervals' worth of hidden time produces no requests at all.
      await advance(60_000);
      expect(fetcher).toHaveBeenCalledTimes(1);

      await act(async () => {
        setVisibility("visible");
        await settle(0);
      });
      expect(result.current.paused).toBe(false);
      // The interval elapsed while hidden, so it is due immediately.
      expect(fetcher).toHaveBeenCalledTimes(2);

      await advance(1_000);
      expect(fetcher).toHaveBeenCalledTimes(3);
    });

    it("resumes the remainder of the interval rather than restarting it", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(ok(1));

      const { result } = await mount(() => usePolledResource(fetcher, 10_000, TOKEN));
      expect(fetcher).toHaveBeenCalledTimes(1);

      act(() => setVisibility("hidden"));
      // Asserted here as well as in the test above, because the call-count assertions in
      // THIS test would coincide with a broken pause: 3s of a 10s interval spent hidden
      // reaches the same 10s firing point whether or not the timer was ever stopped.
      expect(result.current.paused).toBe(true);
      await advance(3_000);
      expect(fetcher).toHaveBeenCalledTimes(1);

      await act(async () => {
        setVisibility("visible");
        await settle(0);
      });
      // 3s of the 10s already passed, so it is not due yet...
      expect(fetcher).toHaveBeenCalledTimes(1);
      await advance(6_999);
      expect(fetcher).toHaveBeenCalledTimes(1);
      // ...and it fires at the 7s remainder, not after a fresh 10s.
      await advance(1);
      expect(fetcher).toHaveBeenCalledTimes(2);
    });

    it("does not start polling a tab that was already hidden at mount", async () => {
      defineVisibility("hidden");
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(ok(1));

      await mount(() => usePolledResource(fetcher, 1_000, TOKEN));
      // The mount fetch still happens — the widget has to render something.
      expect(fetcher).toHaveBeenCalledTimes(1);

      // But no polling follows it, even though no visibilitychange event ever arrived.
      await advance(60_000);
      expect(fetcher).toHaveBeenCalledTimes(1);
    });

    it("does not resume a stopped resource just because the tab came back", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(failed);

      const { result } = await mount(() =>
        usePolledResource(fetcher, 1_000, TOKEN, { backoffCeilingMs: 1_000 })
      );
      expect(result.current.exhausted).toBe(true);
      expect(fetcher).toHaveBeenCalledTimes(1);

      act(() => setVisibility("hidden"));
      await advance(5_000);
      await act(async () => {
        setVisibility("visible");
        await settle(0);
      });

      expect(fetcher).toHaveBeenCalledTimes(1);
      expect(result.current.exhausted).toBe(true);
    });
  });

  describe("token changes", () => {
    it("retries immediately on a renewed token when the resource is failing", async () => {
      const fetcher = vi
        .fn<ApiFetcher<Payload>>()
        .mockResolvedValueOnce({ kind: "unauthorized", status: 401 })
        .mockResolvedValue(ok(5));

      const { result, rerender } = await mountWith(
        ({ token }: { token: string }) => usePolledResource(fetcher, 60_000, token),
        { token: "expired" }
      );
      expect(fetcher).toHaveBeenCalledTimes(1);
      expect(result.current.failure).toEqual({ kind: "unauthorized", status: 401 });

      await act(async () => {
        rerender({ token: "renewed" });
        await settle(0);
      });

      expect(fetcher).toHaveBeenCalledTimes(2);
      expect(fetcher).toHaveBeenLastCalledWith("renewed", expect.any(AbortSignal));
      expect(result.current.data).toEqual({ value: 5 });
      expect(result.current.failure).toBeNull();
    });

    it("does not refetch a healthy resource when the token is silently renewed", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(ok(1));

      const { rerender } = await mountWith(
        ({ token }: { token: string }) => usePolledResource(fetcher, null, token),
        { token: "first" }
      );
      expect(fetcher).toHaveBeenCalledTimes(1);

      // Silent renewal happens roughly every five minutes. If it forced a refetch, an
      // expensive non-polled resource would be back on a timer through the back door.
      await act(async () => {
        rerender({ token: "second" });
        await settle(0);
      });
      expect(fetcher).toHaveBeenCalledTimes(1);
    });
  });
});
