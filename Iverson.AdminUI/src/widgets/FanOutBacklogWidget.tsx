import type { PolledResource } from "../hooks/usePolledResource";
import type { MetricsResponse } from "../api/types";
import { formatInteger } from "./format";
import { MetricStat, MetricStatRow } from "./MetricStat";
import { MetricsWidgetCard } from "./MetricsWidgetCard";

/**
 * The three fan-out queue depths: reconciliation outbox, unreplayed DLQ, document re-render.
 *
 * All three are WORKER-ONLY instruments (`reconciliation_queue_depth`,
 * `dlq_unreplayed_count`, `document_rerender_queue_depth`). They are scraped from the worker,
 * not the API, so an environment whose Prometheus has no worker scrape job returns `null` for
 * every one of them — and `null` here is "nobody is measuring this queue", which is the exact
 * opposite reading of "this queue is empty". `MetricStat` is what keeps the two apart; see the
 * note there.
 *
 * The subtitle says where the numbers come from for the same reason: an operator staring at
 * three "No data" figures needs to know the first thing to check is the scrape config, not the
 * worker.
 */

export const FAN_OUT_BACKLOG_SUBTITLE =
  "Current queue depths, scraped from the worker. A figure is absent — not zero — where the worker is not scraped. Polled every 30 seconds.";

export function FanOutBacklogWidget({
  resource,
}: {
  readonly resource: PolledResource<MetricsResponse>;
}) {
  return (
    <MetricsWidgetCard
      title="Fan-out backlog"
      subtitle={FAN_OUT_BACKLOG_SUBTITLE}
      testId="widget-fan-out-backlog"
      resource={resource}
    >
      {(data: MetricsResponse) => (
        <MetricStatRow>
          <MetricStat
            label="Reconciliation queue depth"
            value={data.fanOutBacklog?.reconciliationQueueDepth ?? null}
            format={formatInteger}
            testId="fan-out-reconciliation-queue-depth"
          />
          <MetricStat
            label="Unreplayed DLQ messages"
            value={data.fanOutBacklog?.dlqUnreplayedCount ?? null}
            format={formatInteger}
            testId="fan-out-dlq-unreplayed"
          />
          <MetricStat
            label="Document re-render queue depth"
            value={data.fanOutBacklog?.documentRerenderQueueDepth ?? null}
            format={formatInteger}
            testId="fan-out-document-rerender-queue-depth"
          />
        </MetricStatRow>
      )}
    </MetricsWidgetCard>
  );
}
