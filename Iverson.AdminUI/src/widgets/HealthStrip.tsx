import { Box, Chip, Typography } from "@mui/material";
import { fetchHealth } from "../api/console";
import type { HealthChecks, HealthResponse } from "../api/types";
import { usePolledResource } from "../hooks/usePolledResource";
import { WidgetCard } from "./WidgetCard";

/**
 * Store-by-store health, one tile per backing store.
 *
 * Three things about this widget are contract, not preference:
 *
 * - **`checks.starrocks` has FOUR states.** `true`, `false`, `"disabled"` when the engagement
 *   store is switched off, and `"authPending"` while a fresh install is waiting for its
 *   create-user post-install hook. A boolean read would render EITHER of the two strings as
 *   DOWN and put a red tile on a deployment that is fine. `"authPending"` is the sharper case:
 *   `ReadinessPolicy` deliberately counts it as ready — the hook cannot run until the readiness
 *   probe passes, so failing on it would deadlock every first install — which means `/health`
 *   answers 200 while this check is not "up", and the operator's very first look at a
 *   correctly-progressing deployment would have been a red "StarRocks: Down".
 * - **There is no Ollama tile.** `/health` does not check it; its state is inferred from the
 *   embedding-latency figure in the Band B widgets. A tile here would be a guess.
 * - **60 seconds, not 30.** `/health` is write-bearing — it drives readiness — so its cadence
 *   comes from Design 3's table rather than from whatever felt responsive.
 *
 * `/health` answers 503 with exactly the body it answers 200 with, and `fetchHealth` declares
 * that status body-bearing, so a degraded deployment arrives as `kind: "ok"` and the strip
 * renders the degraded checks instead of blanking at the moment it matters most.
 */

/** Design 3's cadence for `/health`. Slower than the others because `/health` writes. */
export const HEALTH_POLL_INTERVAL_MS = 60_000;

/** The stores `/health` reports. Ollama is deliberately absent — see the note above. */
const STORE_TILES: readonly { readonly key: keyof HealthChecks; readonly label: string }[] = [
  { key: "postgres", label: "Postgres" },
  { key: "starrocks", label: "StarRocks" },
  { key: "qdrant", label: "Qdrant" },
  { key: "kafka", label: "Kafka" },
];

export type CheckState = "up" | "down" | "disabled" | "authPending" | "unknown";

/**
 * Maps one check value to its rendered state. Both string values are first-class answers, not
 * falsy booleans: `"disabled"` means the operator turned the store off and `"authPending"`
 * means a fresh install has not finished bootstrapping its database user — neither is up, and
 * neither is down.
 *
 * The `undefined` fallthrough is `"unknown"`, not `"down"`: a field missing from the body is
 * something we failed to read, which is not a claim about the store.
 */
export function describeCheck(
  value: boolean | "disabled" | "authPending" | undefined
): CheckState {
  if (value === "disabled") return "disabled";
  if (value === "authPending") return "authPending";
  if (value === true) return "up";
  if (value === false) return "down";
  return "unknown";
}

const CHECK_LABELS: Record<CheckState, string> = {
  up: "Up",
  down: "Down",
  disabled: "Disabled",
  authPending: "Auth pending",
  unknown: "Unknown",
};

export interface CheckAppearance {
  readonly color: "success" | "error" | "default" | "warning";
  readonly variant: "filled" | "outlined";
}

/**
 * How each check state LOOKS. This is contract, not styling.
 *
 * The word "Disabled" in a red filled chip still reads as a broken store — colour wins over
 * text at a glance, which is the entire job of a health strip. So `disabled` is a neutral
 * OUTLINED chip: visibly not a fault, visibly not "up" either. `error` red is reserved for
 * `down`, the one state that is actually a failure.
 *
 * `authPending` follows the same reasoning and lands on `warning` outlined rather than
 * `default`: unlike `disabled` it is a state the deployment is expected to LEAVE, so it is
 * worth noticing — a cluster still auth-pending an hour in really is stuck — but it is not a
 * fault while a fresh install is progressing, and it must never be red. It shares an
 * appearance with `unknown` deliberately: both mean "not confirmed up, not confirmed down",
 * and the chip's label is what separates them.
 */
export const CHECK_APPEARANCE: Record<CheckState, CheckAppearance> = {
  up: { color: "success", variant: "filled" },
  down: { color: "error", variant: "filled" },
  disabled: { color: "default", variant: "outlined" },
  authPending: { color: "warning", variant: "outlined" },
  unknown: { color: "warning", variant: "outlined" },
};

/** The cadence sentence, derived from the constant so the two cannot drift apart. */
export function describeCadence(intervalMs: number): string {
  return `Polled every ${Math.round(intervalMs / 1000)} seconds.`;
}

export function HealthStrip({ accessToken }: { accessToken: string | undefined }) {
  const resource = usePolledResource(fetchHealth, HEALTH_POLL_INTERVAL_MS, accessToken);

  return (
    <WidgetCard
      title="Store health"
      subtitle={describeCadence(HEALTH_POLL_INTERVAL_MS)}
      testId="widget-health"
      resource={resource}
      pollIntervalMs={HEALTH_POLL_INTERVAL_MS}
    >
      {(data: HealthResponse) => (
        <Box>
          <Typography
            variant="body2"
            data-testid="health-status"
            data-status={data.status}
            gutterBottom
          >
            Overall: {data.status === "healthy" ? "Healthy" : "Degraded"}
          </Typography>
          <Box sx={{ display: "flex", flexWrap: "wrap", gap: 1 }}>
            {STORE_TILES.map(({ key, label }) => {
              const state = describeCheck(data.checks?.[key]);
              const appearance = CHECK_APPEARANCE[state];
              return (
                <Chip
                  key={key}
                  data-testid={`health-tile-${key}`}
                  data-check-state={state}
                  color={appearance.color}
                  variant={appearance.variant}
                  label={`${label}: ${CHECK_LABELS[state]}`}
                />
              );
            })}
          </Box>
        </Box>
      )}
    </WidgetCard>
  );
}
