import { StrictMode } from "react";
import type { ComponentType, ReactNode } from "react";
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
async function mount<R>(
  render: () => R,
  wrapper?: ComponentType<{ children: ReactNode }>
): Promise<RenderHookResult<R, unknown>> {
  let handle!: RenderHookResult<R, unknown>;
  await act(async () => {
    handle = renderHook(render, wrapper ? { wrapper } : undefined);
    await settle(0);
  });
  return handle;
}

/**
 * A fetcher whose every call is left pending until the test resolves it by hand, with the
 * token and signal it was handed. Needed for anything about ordering: two requests in flight
 * at once, or one still in flight at unmount.
 */
function deferredFetcher<T>() {
  const calls: Array<{
    token: string;
    signal: AbortSignal;
    resolve: (result: ApiResult<T>) => void;
  }> = [];
  const fetcher: ApiFetcher<T> = (token, signal) =>
    new Promise<ApiResult<T>>((resolve) => {
      calls.push({ token, signal, resolve });
    });
  return { fetcher, calls };
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
  let random: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    vi.useFakeTimers();
    // Pinned so the "as of HH:MM" assertions are not clock-dependent.
    vi.setSystemTime(new Date(2026, 7, 25, 14, 5, 0));
    // Scheduled delays are jittered by +/-10%. 0.5 is the centre of that draw and yields the
    // delay unchanged, which keeps every boundary assertion below exact. The jitter itself is
    // tested on its own further down.
    random = vi.spyOn(Math, "random").mockReturnValue(0.5);
  });

  afterEach(() => {
    random.mockRestore();
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
      expect(result.current.awaitingToken).toBe(true);

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

      const { result } = await mount(() => usePolledResource(fetcher, 1_000, TOKEN));
      // The mount fetch still happens — the widget has to render something.
      expect(fetcher).toHaveBeenCalledTimes(1);
      // Pinned explicitly: the initial `paused` is seeded from the visibility state at mount,
      // and no visibilitychange event will ever arrive to correct it. Call counts alone leave
      // that seed unfalsifiable.
      expect(result.current.paused).toBe(true);

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
    it("retries immediately on a new token when the resource is failing", async () => {
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

    it("does not refetch a healthy resource when the token changes", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(ok(1));

      const { rerender } = await mountWith(
        ({ token }: { token: string }) => usePolledResource(fetcher, null, token),
        { token: "first" }
      );
      expect(fetcher).toHaveBeenCalledTimes(1);

      // A token change alone must not force a refetch: this hook's job is polling on
      // `intervalMs`, not reacting to token identity. A healthy resource refetching on every
      // token change would put an expensive non-polled resource back on a timer through the
      // back door.
      await act(async () => {
        rerender({ token: "second" });
        await settle(0);
      });
      expect(fetcher).toHaveBeenCalledTimes(1);
    });
  });

  describe("StrictMode", () => {
    it("still loads when the first effect pass is torn down and remounted", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(ok(1));

      // `main.tsx` wraps the whole app in StrictMode, which in dev mounts every effect, tears
      // it down, and mounts it again. A first-fetch latch that survived that teardown would
      // leave every widget on a permanent spinner under `npm run dev` — while the production
      // build, `npm run build` and this suite all stayed green.
      const { result } = await mount(
        () => usePolledResource(fetcher, 30_000, TOKEN),
        StrictMode
      );

      expect(fetcher).toHaveBeenCalledTimes(2);
      expect(result.current.data).toEqual({ value: 1 });
      expect(result.current.loading).toBe(false);

      // And it is genuinely polling afterwards, not merely showing one value.
      await advance(30_000);
      expect(fetcher).toHaveBeenCalledTimes(3);
    });
  });

  describe("losing the access token", () => {
    it("stops polling, says so, and restarts when a token comes back", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(ok(1));

      const { result, rerender } = await mountWith(
        ({ token }: { token: string | undefined }) =>
          usePolledResource(fetcher, 1_000, token),
        { token: "first" as string | undefined }
      );
      expect(fetcher).toHaveBeenCalledTimes(1);
      expect(result.current.awaitingToken).toBe(false);

      await act(async () => {
        rerender({ token: undefined });
        await settle(0);
      });

      // Visible, not silent: the widget can say why the figure stopped moving.
      expect(result.current.awaitingToken).toBe(true);
      expect(result.current.stale).toBe(true);
      expect(result.current.data).toEqual({ value: 1 });

      await advance(60_000);
      expect(fetcher).toHaveBeenCalledTimes(1);

      await act(async () => {
        rerender({ token: "second" });
        await settle(0);
      });

      // Recoverable: a restored token restarts the resource rather than leaving it frozen.
      expect(fetcher).toHaveBeenCalledTimes(2);
      expect(fetcher).toHaveBeenLastCalledWith("second", expect.any(AbortSignal));
      expect(result.current.awaitingToken).toBe(false);
      expect(result.current.stale).toBe(false);

      await advance(1_000);
      expect(fetcher).toHaveBeenCalledTimes(3);
    });

    it("restarts backoff from the base once a lost token comes back", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(failed);

      const { rerender } = await mountWith(
        ({ token }: { token: string | undefined }) =>
          usePolledResource(fetcher, 1_000, token, { backoffCeilingMs: 8_000 }),
        { token: "first" as string | undefined }
      );
      expect(fetcher).toHaveBeenCalledTimes(1); // one failure banked, retry due at 2s
      await advance(2_000);
      expect(fetcher).toHaveBeenCalledTimes(2); // two banked, retry due at 4s

      await act(async () => {
        rerender({ token: undefined });
        await settle(0);
      });
      await advance(30_000);
      expect(fetcher).toHaveBeenCalledTimes(2);

      await act(async () => {
        rerender({ token: "second" });
        await settle(0);
      });
      expect(fetcher).toHaveBeenCalledTimes(3);

      // Back to the 2s base. Carrying the earlier count over would have put this retry at 8s
      // and left the resource one failure from exhausting instead of three.
      await advance(1_999);
      expect(fetcher).toHaveBeenCalledTimes(3);
      await advance(1);
      expect(fetcher).toHaveBeenCalledTimes(4);
    });

    it("does not pretend to retry while there is no token to retry with", async () => {
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(failed);

      const { result, rerender } = await mountWith(
        ({ token }: { token: string | undefined }) =>
          usePolledResource(fetcher, 1_000, token, { backoffCeilingMs: 1_000 }),
        { token: "first" as string | undefined }
      );
      expect(fetcher).toHaveBeenCalledTimes(1);
      expect(result.current.exhausted).toBe(true);

      await act(async () => {
        rerender({ token: undefined });
        await settle(0);
      });
      expect(result.current.awaitingToken).toBe(true);

      await act(async () => {
        result.current.refresh();
        await settle(0);
      });

      // `refresh` cannot fetch without a token, so it must not clear the one flag that tells
      // the widget the resource has stopped. Reporting `exhausted: false` here would promise a
      // retry that issues no request and re-arms no timer.
      expect(result.current.exhausted).toBe(true);
      expect(fetcher).toHaveBeenCalledTimes(1);
      await advance(60_000);
      expect(fetcher).toHaveBeenCalledTimes(1);

      // The token returning is what actually re-arms it.
      await act(async () => {
        rerender({ token: "second" });
        await settle(0);
      });
      expect(fetcher).toHaveBeenCalledTimes(2);
    });
  });

  describe("in-flight requests", () => {
    it("aborts a superseded request and ignores the answer it arrives with", async () => {
      const { fetcher, calls } = deferredFetcher<Payload>();

      const { result } = await mount(() => usePolledResource(fetcher, null, TOKEN));
      expect(calls).toHaveLength(1);

      await act(async () => {
        result.current.refresh();
        await settle(0);
      });
      expect(calls).toHaveLength(2);
      // The older request is cancelled, not merely ignored — it stops occupying a connection.
      expect(calls[0].signal.aborted).toBe(true);
      expect(calls[1].signal.aborted).toBe(false);

      await act(async () => {
        calls[1].resolve(ok(2));
        await settle(0);
      });
      expect(result.current.data).toEqual({ value: 2 });

      // The superseded request answers late — as a request made with the previous token would,
      // landing after the one made with the new token. Its older value must not overwrite the
      // newer one.
      await act(async () => {
        calls[0].resolve(ok(1));
        await settle(0);
      });
      expect(result.current.data).toEqual({ value: 2 });
    });

    it("aborts the request still in flight when the hook unmounts", async () => {
      const { fetcher, calls } = deferredFetcher<Payload>();

      const { unmount } = await mount(() => usePolledResource(fetcher, 1_000, TOKEN));
      expect(calls).toHaveLength(1);
      expect(calls[0].signal.aborted).toBe(false);

      unmount();

      expect(calls[0].signal.aborted).toBe(true);
    });
  });

  describe("jitter", () => {
    it("spreads the poll interval so widgets mounted together drift apart", async () => {
      random.mockReturnValue(1); // the top of the +/-10% draw
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(ok(1));

      await mount(() => usePolledResource(fetcher, 10_000, TOKEN));
      expect(fetcher).toHaveBeenCalledTimes(1);

      // Not on the round boundary every other widget would also pick...
      await advance(10_999);
      expect(fetcher).toHaveBeenCalledTimes(1);
      // ...but 10% late, at 11s.
      await advance(1);
      expect(fetcher).toHaveBeenCalledTimes(2);
    });

    it("spreads early as well as late, so the drift is not one-sided", async () => {
      random.mockReturnValue(0); // the bottom of the +/-10% draw
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(ok(1));

      await mount(() => usePolledResource(fetcher, 10_000, TOKEN));
      expect(fetcher).toHaveBeenCalledTimes(1);

      // 10% EARLY, at 9s. A jitter that only ever ran late would halve the spread and leave
      // every widget drifting the same direction.
      await advance(8_999);
      expect(fetcher).toHaveBeenCalledTimes(1);
      await advance(1);
      expect(fetcher).toHaveBeenCalledTimes(2);
    });

    it("can be switched off, which is what keeps a cadence exact when one is needed", async () => {
      random.mockReturnValue(1);
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(ok(1));

      await mount(() => usePolledResource(fetcher, 10_000, TOKEN, { jitterRatio: 0 }));

      await advance(9_999);
      expect(fetcher).toHaveBeenCalledTimes(1);
      await advance(1);
      expect(fetcher).toHaveBeenCalledTimes(2);
    });

    it("decides where backoff gives up on the unjittered delay", async () => {
      random.mockReturnValue(1);
      const fetcher = vi.fn<ApiFetcher<Payload>>().mockResolvedValue(failed);

      const { result } = await mount(() =>
        usePolledResource(fetcher, 1_000, TOKEN, { backoffCeilingMs: 8_000 })
      );

      await advance(60_000);
      // Jitter moves WHEN each retry lands, never HOW MANY there are: 2s, 4s and 8s raw are
      // all within the ceiling, 16s is not, so it is four attempts either way.
      expect(fetcher).toHaveBeenCalledTimes(4);
      expect(result.current.exhausted).toBe(true);
    });
  });
});
