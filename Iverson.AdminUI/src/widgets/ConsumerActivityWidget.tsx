import type { PolledResource } from "../hooks/usePolledResource";
import type { MetricsResponse } from "../api/types";
import { formatRate } from "./format";
import { MetricStat, MetricStatRow } from "./MetricStat";
import { MetricsWidgetCard } from "./MetricsWidgetCard";

/**
 * Consumer retry and DLQ-routing RATES, from `consumer_retries_total` and
 * `consumer_dlq_routed_total`.
 *
 * These are counters, and the endpoint sends them already rated over its query window — so
 * these are messages per second, not totals since start-up. The unit is rendered on the figure
 * (`0.02 /s`) rather than left to the label alone, because a bare `0.02` beside "Consumer
 * retries" reads as a count and there is no count in this deployment that could be 0.02.
 *
 * Both are WORKER-ONLY instruments, so an environment with no worker scrape job returns `null`
 * for both — "not measured", not "no retries happening". See `MetricStat`.
 */

export const CONSUMER_ACTIVITY_SUBTITLE =
  "Per-second rates over the metrics window, scraped from the worker. A figure is absent — not zero — where the worker is not scraped. Polled every 30 seconds.";

export function ConsumerActivityWidget({
  resource,
}: {
  readonly resource: PolledResource<MetricsResponse>;
}) {
  return (
    <MetricsWidgetCard
      title="DLQ and retry rate"
      subtitle={CONSUMER_ACTIVITY_SUBTITLE}
      testId="widget-consumer-activity"
      resource={resource}
    >
      {(data: MetricsResponse) => (
        <MetricStatRow>
          <MetricStat
            label="Consumer retries"
            value={data.consumerActivity?.consumerRetriesPerSecond ?? null}
            format={formatRate}
            testId="consumer-retries-rate"
          />
          <MetricStat
            label="Messages routed to the DLQ"
            value={data.consumerActivity?.consumerDlqRoutedPerSecond ?? null}
            format={formatRate}
            testId="consumer-dlq-routed-rate"
          />
        </MetricStatRow>
      )}
    </MetricsWidgetCard>
  );
}
