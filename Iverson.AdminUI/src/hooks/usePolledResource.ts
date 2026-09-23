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
 *
 * ## Notes on the edges
 *
 * - **It is StrictMode-safe.** `main.tsx` wraps the app in `React.StrictMode`, which in dev
 *   mounts, tears down and remounts every effect. `startedRef` is therefore cleared in the
 *   teardown at the same time as the in-flight fetch is aborted; a latch that survived the
 *   simulated remount would leave every widget on a permanent spinner under `npm run dev`
 *   while production, `npm run build` and the test suite all looked fine.
 * - **Losing the access token is recoverable and visible.** No token means no polling — but it
 *   also sets `awaitingToken`, marks retained data stale, and rearms the first fetch, so a
 *   token that comes back restarts the resource instead of leaving it frozen forever.
 * - **A `null` interval still retries a FAILED fetch.** With the default base and ceiling that
 *   is three retries at 60s, 120s and 240s, then `exhausted`. It is "never polled on success",
 *   not "exactly one request, ever".
 * - **Changing `intervalMs` does not reschedule the timer already pending.** Going 10s → 1s
 *   runs out the current 10s wait first, then polls at 1s. Widgets pick a cadence once, so
 *   the extra bookkeeping to cut a pending wait short would never run in this app.
 */

/** Where doubling gives up. Chosen so a 30s poll retries at 60s, 120s and 240s, then stops. */
export const DEFAULT_BACKOFF_CEILING_MS = 5 * 60_000;

/** Backoff base for a non-polled resource, which has no interval to derive one from. */
export const DEFAULT_RETRY_BASE_MS = 30_000;

/**
 * How far either side of a scheduled delay the timer may land, as a fraction. Nine widgets
 * mount together, so without this they would poll on exactly the same boundary for the life of
 * the page and hand the API a periodic spike instead of a flat load.
 */
export const DEFAULT_JITTER_RATIO = 0.1;

export interface PolledResourceOptions {
  /** Delay past which the hook stops retrying. Defaults to {@link DEFAULT_BACKOFF_CEILING_MS}. */
  readonly backoffCeilingMs?: number;
  /** Backoff base. Defaults to `intervalMs`, or {@link DEFAULT_RETRY_BASE_MS} when not polling. */
  readonly retryBaseMs?: number;
  /** Jitter fraction. Defaults to {@link DEFAULT_JITTER_RATIO}; `0` disables it. */
  readonly jitterRatio?: number;
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
  /**
   * True while there is no access token to fetch with — the session is still resolving, or it
   * went away. Polling is stopped, and it restarts by itself once a token arrives. Distinct
   * from `loading`, which means "fetching, nothing to show yet".
   */
  awaitingToken: boolean;
  /**
   * Fetch now, resetting backoff. The manual-retry action behind `exhausted`.
   *
   * A NO-OP while `awaitingToken` — there is nothing to fetch with, so it changes no state
   * rather than clearing `exhausted` for a request that will not happen. A returning token
   * re-arms the resource on its own.
   */
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
  awaitingToken: boolean;
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

function hasToken(token: string | undefined): token is string {
  return token !== undefined && token !== "";
}

export function usePolledResource<T>(
  fetcher: ApiFetcher<T>,
  intervalMs: number | null,
  accessToken: string | undefined,
  options: PolledResourceOptions = {}
): PolledResource<T> {
  const backoffCeilingMs = options.backoffCeilingMs ?? DEFAULT_BACKOFF_CEILING_MS;
  const retryBaseMs = options.retryBaseMs ?? intervalMs ?? DEFAULT_RETRY_BASE_MS;
  const jitterRatio = options.jitterRatio ?? DEFAULT_JITTER_RATIO;

  const [state, setState] = useState<InternalState<T>>(() => ({
    data: null,
    lastUpdatedAt: null,
    stale: false,
    loading: true,
    failure: null,
    exhausted: false,
    paused: isHidden(),
    awaitingToken: !hasToken(accessToken),
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
  /** Whether a first fetch has been started for this MOUNT. Cleared on teardown — see the
   *  StrictMode note on the hook — and whenever the access token goes away. */
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

  /** Spread a delay by ±`jitterRatio`. `Math.random() === 0.5` returns the delay unchanged. */
  const jitter = useCallback(
    (delayMs: number) => {
      if (jitterRatio <= 0) return delayMs;
      const spread = delayMs * jitterRatio;
      return Math.max(0, Math.round(delayMs + (Math.random() - 0.5) * 2 * spread));
    },
    [jitterRatio]
  );

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
    if (!hasToken(token)) return;

    // Supersede whatever is in flight. Without this, an earlier request could land AFTER a newer
    // one (a Refresh click, or a changed token) and overwrite it with older data.
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
      if (intervalMs !== null) scheduleNext(jitter(intervalMs));
      return;
    }

    // Every non-ok outcome — including a 503 the widget will render calmly, and including a
    // 401, which nothing renews — backs off the same way. What differs is what the widget
    // DISPLAYS, which is `failure.kind`'s job, not the scheduler's.
    failuresRef.current += 1;
    // The ceiling is compared against the UNJITTERED delay, so where backoff gives up is
    // exact and does not wobble with the jitter draw.
    const delay = retryBaseMs * 2 ** failuresRef.current;
    const giveUp = delay > backoffCeilingMs;
    const failure = result;
    setState((s) => ({
      ...s,
      loading: false,
      failure,
      stale: s.data !== null,
      exhausted: giveUp,
    }));
    if (!giveUp) scheduleNext(jitter(delay));
  }, [intervalMs, retryBaseMs, backoffCeilingMs, scheduleNext, jitter]);

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
      // Cleared alongside the abort below, so a StrictMode remount — or any real remount —
      // starts a fresh first fetch instead of inheriting a latch from the torn-down pass.
      startedRef.current = false;
      clearTimer();
      pendingDelayRef.current = null;
      abortRef.current?.abort();
    };
  }, [clearTimer]);

  const refresh = useCallback(() => {
    // With no token there is nothing to fetch with, and `attempt` would return immediately
    // having scheduled nothing. Clearing `exhausted` here would report a retry that never
    // happened and leave the resource silent with no flag saying so. Leave the state alone:
    // the token effect below re-arms the resource the moment a token comes back.
    if (!hasToken(tokenRef.current)) return;
    failuresRef.current = 0;
    clearTimer();
    pendingDelayRef.current = null;
    setState((s) => ({ ...s, exhausted: false }));
    attemptRef.current();
  }, [clearTimer]);

  useEffect(() => {
    if (!hasToken(accessToken)) {
      // No token, no polling — and SAY SO. Leaving `awaitingToken` off here would freeze the
      // last good value on screen with `stale: false`, `failure: null` and `exhausted: false`,
      // giving the widget nothing to render and no reason to think anything was wrong.
      clearTimer();
      pendingDelayRef.current = null;
      startedRef.current = false;
      setState((s) => {
        const stale = s.data !== null;
        return s.awaitingToken && s.stale === stale ? s : { ...s, awaitingToken: true, stale };
      });
      return;
    }

    setState((s) => (s.awaitingToken ? { ...s, awaitingToken: false } : s));

    if (!startedRef.current) {
      startedRef.current = true;
      // Backoff restarts from the base. Carrying the pre-token-loss failure count over would
      // resume at base*2^(n+1) and could exhaust after a single further failure instead of
      // three — a resource that lost its token mid-backoff would come back nearly dead.
      failuresRef.current = 0;
      attemptRef.current();
      return;
    }
    // A changed token only unblocks a resource that is CURRENTLY failing. A healthy one must
    // not refetch on every token change. Nothing renews the token today (`automaticSilentRenew`
    // is off), but if renewal returns, refetching here would put `/admin/console/data-volume`
    // back on a timer through the back door, at whatever cadence the IdP renews on.
    if (failuresRef.current > 0) refresh();
  }, [accessToken, refresh, clearTimer]);

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
      // Resumed unjittered: the wait was already drawn when it was first scheduled, and
      // re-drawing it here would let a hide/show cycle stretch a delay past its own ceiling.
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
    awaitingToken: state.awaitingToken,
    refresh,
  };
}
