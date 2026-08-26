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
 * - **`checks.starrocks` has THREE states.** `true`, `false`, and the literal string
 *   `"disabled"` when the engagement store is switched off. A boolean read would render a
 *   deliberately disabled store as DOWN and put a red tile on a perfectly healthy deployment.
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

export type CheckState = "up" | "down" | "disabled" | "unknown";

/**
 * Maps one check value to its rendered state. `"disabled"` is a first-class answer, not a
 * falsy boolean: it means the operator turned the store off, which is neither up nor down.
 */
export function describeCheck(value: boolean | "disabled" | undefined): CheckState {
  if (value === "disabled") return "disabled";
  if (value === true) return "up";
  if (value === false) return "down";
  return "unknown";
}

const CHECK_LABELS: Record<CheckState, string> = {
  up: "Up",
  down: "Down",
  disabled: "Disabled",
  unknown: "Unknown",
};

const CHECK_COLORS: Record<CheckState, "success" | "error" | "default" | "warning"> = {
  up: "success",
  down: "error",
  disabled: "default",
  unknown: "warning",
};

export function HealthStrip({ accessToken }: { accessToken: string | undefined }) {
  const resource = usePolledResource(fetchHealth, HEALTH_POLL_INTERVAL_MS, accessToken);

  return (
    <WidgetCard
      title="Store health"
      subtitle="Polled every 60 seconds."
      testId="widget-health"
      resource={resource}
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
              return (
                <Chip
                  key={key}
                  data-testid={`health-tile-${key}`}
                  data-check-state={state}
                  color={CHECK_COLORS[state]}
                  variant={state === "disabled" ? "outlined" : "filled"}
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
