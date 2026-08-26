import { useCallback, useEffect, useRef, useState } from "react";
import type { ApiFailure, ApiFetcher } from "../api/client";
import { describeError, isAbort } from "../api/client";

/**
 * Drives one console resource: fetch, poll, back off, retain, pause.
 *
 * Four behaviours, each of which the widgets above depend on:
 *
 * 1. **It polls on `intervalMs`** — or not at all, when `intervalMs` is `null`. There is no
 *    default: `/admin/console/data-volume` costs one sequential StarRocks `COUNT(*)` per
 *    registered type per request, so every call site must choose its cadence deliberately
 *    rather than inherit a fast one.
 * 2. **Failures back off by doubling to a ceiling, then stop.** Not plateau — stop, with
 *    `exhausted: true` and `refresh()` as the only way forward. A `reason: "notDeployed"`
 *    metrics endpoint will never start answering; polling it forever is pure waste.
 * 3. **The last good value is retained and marked stale**, never replaced by a spinner. A
 *    widget that flips back to "loading" every time one poll fails is unreadable. Render
 *    `data` with an "as of {asOf}" line whenever `stale` is true.
 * 4. **Polling stops entirely while the tab is hidden.** The timer is cleared, not merely
 *    ignored — that pause is the only thing bounding the cost of a user with six console
 *    tabs open. The remaining delay is preserved and resumed on unhide.
 *
 * The access token is an ARGUMENT, not something the fetch layer reaches for: `useAuth()` is
 * React context and the fetch layer is module scope.
 */

/** Where doubling gives up. Chosen so a 30s poll retries at 60s, 120s and 240s, then stops. */
export const DEFAULT_BACKOFF_CEILING_MS = 5 * 60_000;

/** Backoff base for a non-polled resource, which has no interval to derive one from. */
export const DEFAULT_RETRY_BASE_MS = 30_000;

export interface PolledResourceOptions {
  /** Delay past which the hook stops retrying. Defaults to {@link DEFAULT_BACKOFF_CEILING_MS}. */
  readonly backoffCeilingMs?: number;
  /** Backoff base. Defaults to `intervalMs`, or {@link DEFAULT_RETRY_BASE_MS} when not polling. */
  readonly retryBaseMs?: number;
}

export interface PolledResource<T> {
  /** The last successful payload, retained across failures. `null` until the first success. */
  data: T | null;
  /** When `data` was fetched, epoch ms. `null` until the first success. */
  lastUpdatedAt: number | null;
  /** `lastUpdatedAt` as local 24-hour `HH:MM`, for an "as of {asOf}" line. `null` if never. */
  asOf: string | null;
  /** True when the latest attempt failed but `data` is a retained earlier success. */
  stale: boolean;
  /** True only before the first attempt settles — i.e. when there is genuinely nothing to show. */
  loading: boolean;
  /** The latest failure, or `null` if the latest attempt succeeded. See `ApiFailure`. */
  failure: ApiFailure | null;
  /** True once backoff hit the ceiling and polling stopped. `refresh()` is the only way on. */
  exhausted: boolean;
  /** True while the tab is hidden and the timer is stopped. */
  paused: boolean;
  /** Fetch now, resetting backoff. The manual-retry action behind `exhausted`. */
  refresh: () => void;
}

interface InternalState<T> {
  data: T | null;
  lastUpdatedAt: number | null;
  stale: boolean;
  loading: boolean;
  failure: ApiFailure | null;
  exhausted: boolean;
  paused: boolean;
}

/** Local 24-hour `HH:MM`. Formatted by hand rather than via `toLocaleTimeString`, which would
 * make the rendered string — and every test asserting on it — depend on the runner's locale. */
export function formatAsOf(at: number | null): string | null {
  if (at === null) return null;
  const d = new Date(at);
  return `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
}

function isHidden(): boolean {
  return typeof document !== "undefined" && document.visibilityState === "hidden";
}

export function usePolledResource<T>(
  fetcher: ApiFetcher<T>,
  intervalMs: number | null,
  accessToken: string | undefined,
  options: PolledResourceOptions = {}
): PolledResource<T> {
  const backoffCeilingMs = options.backoffCeilingMs ?? DEFAULT_BACKOFF_CEILING_MS;
  const retryBaseMs = options.retryBaseMs ?? intervalMs ?? DEFAULT_RETRY_BASE_MS;

  const [state, setState] = useState<InternalState<T>>(() => ({
    data: null,
    lastUpdatedAt: null,
    stale: false,
    loading: true,
    failure: null,
    exhausted: false,
    paused: isHidden(),
  }));

  // "Latest value" refs, so a changed fetcher or token does not tear down the scheduler.
  const fetcherRef = useRef(fetcher);
  fetcherRef.current = fetcher;
  const tokenRef = useRef(accessToken);
  tokenRef.current = accessToken;

  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const mountedRef = useRef(true);
  const failuresRef = useRef(0);
  const exhaustedRef = useRef(false);
  const startedRef = useRef(false);
  // The delay currently scheduled, and when it was scheduled — kept so hiding the tab can
  // clear the timer and unhiding can resume the REMAINDER rather than restarting the wait.
  const pendingDelayRef = useRef<number | null>(null);
  const pendingSinceRef = useRef(0);
  const attemptRef = useRef<() => void>(() => {});

  const clearTimer = useCallback(() => {
    if (timerRef.current !== null) {
      clearTimeout(timerRef.current);
      timerRef.current = null;
    }
  }, []);

  const scheduleNext = useCallback(
    (delayMs: number) => {
      clearTimer();
      pendingDelayRef.current = delayMs;
      pendingSinceRef.current = Date.now();
      // A hidden tab records the pending delay but arms no timer. This is what makes the
      // pause real rather than cosmetic — and it also covers a widget mounted while the tab
      // is already hidden, where no `visibilitychange` event will ever arrive to stop it.
      if (isHidden()) return;
      timerRef.current = setTimeout(() => {
        timerRef.current = null;
        pendingDelayRef.current = null;
        attemptRef.current();
      }, delayMs);
    },
    [clearTimer]
  );

  const attempt = useCallback(async () => {
    const token = tokenRef.current;
    if (token === undefined || token === "") return;

    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;

    let result;
    try {
      result = await fetcherRef.current(token, controller.signal);
    } catch (error) {
      if (isAbort(error) || controller.signal.aborted) return;
      result = { kind: "failed", status: null, message: describeError(error) } as ApiFailure;
    }

    // Superseded by a newer attempt, or unmounted while in flight.
    if (controller.signal.aborted || !mountedRef.current) return;

    if (result.kind === "ok") {
      failuresRef.current = 0;
      exhaustedRef.current = false;
      const at = Date.now();
      setState((s) => ({
        ...s,
        data: result.data,
        lastUpdatedAt: at,
        stale: false,
        loading: false,
        failure: null,
        exhausted: false,
      }));
      if (intervalMs !== null) scheduleNext(intervalMs);
      return;
    }

    // Every non-ok outcome — including a 503 the widget will render calmly, and including a
    // 401 whose renewal is already in flight — backs off the same way. What differs is what
    // the widget DISPLAYS, which is `failure.kind`'s job, not the scheduler's.
    failuresRef.current += 1;
    const delay = retryBaseMs * 2 ** failuresRef.current;
    const giveUp = delay > backoffCeilingMs;
    exhaustedRef.current = giveUp;
    const failure = result;
    setState((s) => ({
      ...s,
      loading: false,
      failure,
      stale: s.data !== null,
      exhausted: giveUp,
    }));
    if (!giveUp) scheduleNext(delay);
  }, [intervalMs, retryBaseMs, backoffCeilingMs, scheduleNext]);

  // Declared before the effects that call it, so the assignment lands first.
  useEffect(() => {
    attemptRef.current = () => {
      void attempt();
    };
  }, [attempt]);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      clearTimer();
      pendingDelayRef.current = null;
      abortRef.current?.abort();
    };
  }, [clearTimer]);

  const refresh = useCallback(() => {
    failuresRef.current = 0;
    exhaustedRef.current = false;
    clearTimer();
    pendingDelayRef.current = null;
    setState((s) => ({ ...s, exhausted: false }));
    attemptRef.current();
  }, [clearTimer]);

  useEffect(() => {
    if (accessToken === undefined || accessToken === "") return;
    if (!startedRef.current) {
      startedRef.current = true;
      attemptRef.current();
      return;
    }
    // A renewed token only unblocks a resource that is CURRENTLY failing. A healthy one must
    // not refetch on every silent renew — that would put `/admin/console/data-volume` back on
    // a timer through the back door, at whatever cadence the IdP happens to renew on.
    if (failuresRef.current > 0) refresh();
  }, [accessToken, refresh]);

  useEffect(() => {
    const onVisibilityChange = () => {
      if (isHidden()) {
        // Stop the clock. The pending delay is preserved for the resume below.
        clearTimer();
        setState((s) => (s.paused ? s : { ...s, paused: true }));
        return;
      }
      setState((s) => (s.paused ? { ...s, paused: false } : s));
      const pending = pendingDelayRef.current;
      // No pending schedule means nothing was waiting: exhausted, or a settled one-shot.
      if (pending === null) return;
      const remaining = Math.max(0, pending - (Date.now() - pendingSinceRef.current));
      if (remaining === 0) {
        pendingDelayRef.current = null;
        attemptRef.current();
      } else {
        scheduleNext(remaining);
      }
    };

    document.addEventListener("visibilitychange", onVisibilityChange);
    return () => document.removeEventListener("visibilitychange", onVisibilityChange);
  }, [clearTimer, scheduleNext]);

  return {
    data: state.data,
    lastUpdatedAt: state.lastUpdatedAt,
    asOf: formatAsOf(state.lastUpdatedAt),
    stale: state.stale,
    loading: state.loading,
    failure: state.failure,
    exhausted: state.exhausted,
    paused: state.paused,
    refresh,
  };
}
