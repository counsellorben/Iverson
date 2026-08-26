import type { ReactNode } from "react";
import { Alert, Box, Button, CircularProgress, Paper, Typography } from "@mui/material";
import type { PolledResource } from "../hooks/usePolledResource";

/**
 * The shell every landing-page widget renders inside, and the single place the console's
 * resource-state ladder is decided.
 *
 * There are four widgets on the landing page and they all face the same seven-way outcome
 * from `usePolledResource`. Deciding that ladder once, in {@link resolveWidgetState}, is what
 * keeps three specific mistakes from being made four times over:
 *
 * 1. **`awaitingToken` is checked BEFORE `loading`.** At a tokenless mount BOTH are true. A
 *    widget that branches on `loading` first renders a spinner that will never resolve — a
 *    permanent hang that looks like a bug in the API rather than a session that has not
 *    arrived yet.
 * 2. **The Refresh button is DISABLED while `awaitingToken`.** `refresh()` is deliberately a
 *    no-op in that state: it will not clear `exhausted` for a request that cannot happen. An
 *    enabled button that silently does nothing is worse than no button at all.
 * 3. **`forbidden` is its own rendered state, not an error card.** `tenants`, `qdrant` and
 *    `metrics` are Operator-gated and NOBODY SATISFIES THE OPERATOR POLICY TODAY, so 403 is
 *    the outcome every real human currently gets there. "You are not authorized to view this"
 *    is the honest answer; a red "request failed" card is not.
 *
 * Retained data always renders, in every state. When a poll fails, the last good value stays
 * on screen under an "as of {asOf}" line rather than being replaced by a spinner.
 */

export type WidgetState =
  /** No access token yet, or it went away. Nothing is being fetched and nothing can be. */
  | "awaitingToken"
  /** The first attempt has not settled and there is genuinely nothing to show. */
  | "loading"
  /** 403. A real authorization answer that a fresh token cannot change. */
  | "forbidden"
  /** 401. Silent renewal has been requested; there is nothing for the user to do. */
  | "unauthorized"
  /** A non-2xx that carried a `reason` — `disabled`, `notReady`, `notDeployed`, … */
  | "unavailable"
  /** Transport failure, or a non-2xx with no readable body. */
  | "error"
  /** A payload is present. It may be stale; `stale` says so separately. */
  | "ready"
  /** Settled, no failure, and still no payload. Should not happen; rendered honestly if it does. */
  | "empty";

/** The slice of a `PolledResource` the state ladder actually reads. */
export type WidgetResourceState = Pick<
  PolledResource<unknown>,
  "data" | "loading" | "failure" | "awaitingToken"
>;

/**
 * The one ordering that matters: `awaitingToken` outranks `loading`, because at a tokenless
 * mount both flags are true and only one of them is the truth worth telling.
 */
export function resolveWidgetState(resource: WidgetResourceState): WidgetState {
  if (resource.awaitingToken) return "awaitingToken";
  if (resource.loading) return "loading";
  if (resource.failure !== null) {
    switch (resource.failure.kind) {
      case "forbidden":
        return "forbidden";
      case "unauthorized":
        return "unauthorized";
      case "problem":
        return "unavailable";
      default:
        return "error";
    }
  }
  return resource.data !== null ? "ready" : "empty";
}

export interface WidgetCardProps<T> {
  /** Heading text, and the basis of the Refresh button's accessible name. */
  readonly title: string;
  /** A one-line scope note under the heading — e.g. "Tenant-scoped, not a deployment total." */
  readonly subtitle?: string;
  /** `data-testid` on the card root, which also carries `data-state`. */
  readonly testId: string;
  readonly resource: PolledResource<T>;
  /**
   * What a 403 means for THIS widget. Defaulted rather than required so no widget can
   * accidentally fall through to a generic error card.
   */
  readonly forbiddenMessage?: string;
  /** Renders a `kind: "problem"` body's `reason` as prose. */
  readonly reasonText?: (reason: string | null) => string;
  /** Renders the payload. Called only when `resource.data` is non-null. */
  readonly children: (data: T) => ReactNode;
}

const DEFAULT_FORBIDDEN_MESSAGE = "You are not authorized to view this.";

function defaultReasonText(reason: string | null): string {
  return reason === null ? "Currently unavailable." : `Currently unavailable: ${reason}.`;
}

interface Notice {
  readonly severity: "info" | "warning" | "error";
  readonly text: string;
}

function noticeFor<T>(
  state: WidgetState,
  props: WidgetCardProps<T>
): Notice | null {
  const failure = props.resource.failure;
  switch (state) {
    case "awaitingToken":
      return { severity: "info", text: "Waiting for session…" };
    case "forbidden":
      return {
        severity: "warning",
        text: props.forbiddenMessage ?? DEFAULT_FORBIDDEN_MESSAGE,
      };
    case "unauthorized":
      return { severity: "info", text: "Session expired — renewing…" };
    case "unavailable":
      return {
        severity: "warning",
        text: (props.reasonText ?? defaultReasonText)(
          failure !== null && failure.kind === "problem" ? failure.reason : null
        ),
      };
    case "error":
      return {
        severity: "error",
        text:
          failure !== null && failure.kind === "failed"
            ? `Could not load: ${failure.message}`
            : "Could not load.",
      };
    case "empty":
      return { severity: "info", text: "No data." };
    default:
      return null;
  }
}

export function WidgetCard<T>(props: WidgetCardProps<T>) {
  const { title, subtitle, testId, resource, children } = props;
  const state = resolveWidgetState(resource);
  const notice = noticeFor(state, props);
  const headingId = `${testId}-heading`;

  return (
    <Paper
      component="section"
      aria-labelledby={headingId}
      data-testid={testId}
      data-state={state}
      sx={{ p: 2, height: "100%" }}
    >
      <Box sx={{ display: "flex", alignItems: "flex-start", gap: 2 }}>
        <Box sx={{ flexGrow: 1 }}>
          <Typography id={headingId} variant="h6" component="h2">
            {title}
          </Typography>
          {subtitle !== undefined && (
            <Typography variant="body2" color="text.secondary">
              {subtitle}
            </Typography>
          )}
        </Box>
        <Button
          size="small"
          onClick={resource.refresh}
          // `refresh()` is inert without a token. Disabling says so instead of pretending.
          disabled={resource.awaitingToken}
          aria-label={`Refresh ${title}`}
        >
          Refresh
        </Button>
      </Box>

      {notice !== null && (
        <Alert severity={notice.severity} sx={{ mt: 1 }} data-testid={`${testId}-notice`}>
          {notice.text}
        </Alert>
      )}

      {state === "loading" && (
        <Box sx={{ mt: 2, display: "flex", alignItems: "center", gap: 1 }}>
          <CircularProgress size={16} aria-hidden="true" />
          <Typography variant="body2">Loading…</Typography>
        </Box>
      )}

      {resource.stale && resource.asOf !== null && (
        <Typography
          variant="caption"
          color="text.secondary"
          sx={{ mt: 1, display: "block" }}
          data-testid={`${testId}-stale`}
        >
          Showing data as of {resource.asOf}
        </Typography>
      )}

      {resource.exhausted && (
        <Typography
          variant="caption"
          color="warning.main"
          sx={{ mt: 1, display: "block" }}
          data-testid={`${testId}-exhausted`}
        >
          Automatic retries have stopped. Use Refresh to try again.
        </Typography>
      )}

      {resource.data !== null && <Box sx={{ mt: 2 }}>{children(resource.data)}</Box>}
    </Paper>
  );
}
