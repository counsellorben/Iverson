import type { ReactNode } from "react";
import type { PolledResource } from "../hooks/usePolledResource";
import type { MetricsResponse } from "../api/types";
import { WidgetCard } from "./WidgetCard";

/**
 * The four metrics widgets' shared shell: a {@link WidgetCard} with this endpoint's 403 and
 * 503 wording already filled in.
 *
 * It is a THIN pre-fill, not a second state ladder. Every state decision — which state a
 * resource is in, and what severity its notice renders at — still happens inside `WidgetCard`,
 * where `noticeSeverityFor` is the single keyed-on-`WidgetState` decision point and is pinned
 * by test. A widget here that rendered its own `<Alert>` would sit outside all of that: the
 * severity contract would hold everywhere except the one card that broke it.
 *
 * What it does own is the wording of the two outcomes all four widgets share, so a fix to
 * either lands on all four at once rather than on three of them.
 */

/** Design 3's cadence for the Band B metrics. */
export const METRICS_POLL_INTERVAL_MS = 30_000;

/**
 * What a 403 means here, in words.
 *
 * This is the answer EVERY human gets from `/admin/console/metrics` today: the endpoint is
 * `Operator`-gated and no identity currently satisfies that policy (the console never requests
 * the `groups` scope, and no `operators` group exists — Design 4d). So four of the nine cards
 * on this page say this sentence on a real deployment, and it has to read as an authorization
 * answer rather than as a fault. `WidgetCard` renders it at warning severity for exactly that
 * reason.
 */
export const METRICS_FORBIDDEN_MESSAGE =
  "Not authorized: deployment metrics require the Operator role.";

/**
 * The `reason` values `/admin/console/metrics` sends with its 503, as prose.
 *
 * The two are NOT the same sentence, because they are not the same fact:
 *
 * - `notDeployed` — Prometheus is not installed. `values-laptop.yaml` sets
 *   `prometheus.enabled: false`, so this is a SUPPORTED deployment shape and there is nothing
 *   for anyone to fix. Telling an operator their metrics stack is broken here would send them
 *   hunting an outage that does not exist.
 * - `unreachable` — Prometheus is installed and is not answering. That one IS worth acting on.
 *
 * Collapsing them into one "metrics unavailable" line throws away the only bit that decides
 * whether anybody should do anything.
 */
export function describeMetricsReason(reason: string | null): string {
  switch (reason) {
    case "notDeployed":
      return "Prometheus is not installed in this deployment, so there are no metrics to show. This is a supported configuration, not an outage.";
    case "unreachable":
      return "Prometheus is installed but is not answering, so metrics are temporarily unavailable.";
    default:
      return reason === null ? "Currently unavailable." : `Currently unavailable: ${reason}.`;
  }
}

export interface MetricsWidgetCardProps {
  readonly title: string;
  readonly subtitle: string;
  readonly testId: string;
  readonly resource: PolledResource<MetricsResponse>;
  readonly children: (data: MetricsResponse) => ReactNode;
}

export function MetricsWidgetCard({
  title,
  subtitle,
  testId,
  resource,
  children,
}: MetricsWidgetCardProps) {
  return (
    <WidgetCard
      title={title}
      subtitle={subtitle}
      testId={testId}
      resource={resource}
      forbiddenMessage={METRICS_FORBIDDEN_MESSAGE}
      reasonText={describeMetricsReason}
      pollIntervalMs={METRICS_POLL_INTERVAL_MS}
    >
      {children}
    </WidgetCard>
  );
}
