import type { ReactNode } from "react";
import { Box, Typography } from "@mui/material";
import { NO_SAMPLE_CAPTION, NO_SAMPLE_VALUE } from "./format";

/**
 * One named figure from `/admin/console/metrics`, and the single place the console decides
 * what a **missing** metric looks like.
 *
 * Every value in `MetricsResponse` is `number | null`, and `null` does not mean zero. It means
 * Prometheus answered but has no current sample for that series — a fresh deployment before
 * its first scrape, a counter that has never incremented, or (for the five worker-only
 * instruments) an environment whose Prometheus has no scrape job pointing at the worker at
 * all. Rendering any of those as `0` would tell an operator "the reconciliation queue is
 * empty" when the truth is "nobody is measuring the reconciliation queue" — the two most
 * different possible readings of the same card.
 *
 * So a `null` renders as words, never as a numeral: the figure line says "No data" and a
 * caption underneath says why. That is a VISUAL promise as much as a textual one — the
 * absence has to be legible at a glance, not inferred from a suspiciously round number — and
 * it is pinned by tests that assert no digit is rendered for a null metric.
 *
 * `data-metric-null` carries the same fact to those tests, but the assertions deliberately
 * check the rendered TEXT too: an attribute alone would keep passing if the visible value
 * silently became `0`.
 */

export interface MetricStatProps {
  /** The figure's name, e.g. "Reconciliation queue depth". */
  readonly label: string;
  /** The value off the wire. `null` is "no current sample", never zero. */
  readonly value: number | null;
  /** Renders a non-null value. Called only when `value !== null`. */
  readonly format: (value: number) => string;
  /** `data-testid` on the stat's root. */
  readonly testId: string;
}

export function MetricStat({ label, value, format, testId }: MetricStatProps) {
  const absent = value === null;

  return (
    <Box
      data-testid={testId}
      data-metric-null={absent}
      sx={{ minWidth: 160, flexGrow: 1, flexBasis: 0 }}
    >
      <Typography variant="caption" component="div" sx={{ color: "text.secondary" }}>
        {label}
      </Typography>
      <Typography
        variant="h6"
        component="div"
        // Greyed rather than emphasised: an absent figure must not read as a headline number.
        // Through `sx`, NOT through Typography's `color` prop — MUI v9 has dropped that prop
        // and renders it as nothing at all, silently and without a type error, so the same
        // line written the obvious way would be a styling promise that never applied. A test
        // asserts the two computed colours differ, which is what caught it.
        sx={{ color: absent ? "text.secondary" : "text.primary" }}
        data-testid={`${testId}-value`}
      >
        {absent ? NO_SAMPLE_VALUE : format(value)}
      </Typography>
      {absent && (
        <Typography
          variant="caption"
          component="div"
          sx={{ color: "text.secondary" }}
          data-testid={`${testId}-caption`}
        >
          {NO_SAMPLE_CAPTION}
        </Typography>
      )}
    </Box>
  );
}

/** The row a widget lays its stats out in. Wraps rather than overflowing on a narrow card. */
export function MetricStatRow({ children }: { readonly children: ReactNode }) {
  return <Box sx={{ display: "flex", flexWrap: "wrap", gap: 3 }}>{children}</Box>;
}
