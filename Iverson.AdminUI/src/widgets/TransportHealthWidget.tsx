import type { PolledResource } from "../hooks/usePolledResource";
import type { MetricsResponse } from "../api/types";
import { formatDuration, formatPercentage, formatRate } from "./format";
import { MetricStat, MetricStatRow } from "./MetricStat";
import { MetricsWidgetCard } from "./MetricsWidgetCard";

/**
 * Request rate, error percentage and p95 for the API's own HTTP pipeline.
 *
 * **This widget is called "transport health", and the word choice is the point.** The figures
 * come from `http.server.request.duration`, collected by `AddAspNetCoreInstrumentation`, which
 * observes gRPC-over-HTTP/2 as ordinary HTTP requests. gRPC carries its own status in a
 * trailer INSIDE a successful HTTP response, so a call that fails with `PermissionDenied` or
 * `InvalidArgument` is an HTTP 200 here and does not move the error percentage at all.
 *
 * Labelling this card "RPC errors" or "RPC health" would therefore state the one thing it
 * cannot know: it would show 0% while every gRPC call in the deployment was failing, and an
 * operator would read that as proof the system was fine. The card says HTTP, on the title, on
 * the subtitle and on the error figure's own label — three places, because the number is going
 * to be quoted out of context and one of those three will travel with it.
 *
 * A test asserts the card renders no "RPC" anywhere. That is deliberately blunt: the failure
 * mode is a well-meaning rename, and a blunt assertion is what a rename trips over.
 */

/** Title and subtitle both name the protocol; neither says "RPC". */
export const TRANSPORT_HEALTH_TITLE = "Transport health (HTTP)";

export const TRANSPORT_HEALTH_SUBTITLE =
  "HTTP status of the API's request pipeline. A gRPC call that fails inside an HTTP 200 response is not counted as an error here, so this is transport health, not call-level health. Polled every 30 seconds.";

export function TransportHealthWidget({
  resource,
}: {
  readonly resource: PolledResource<MetricsResponse>;
}) {
  return (
    <MetricsWidgetCard
      title={TRANSPORT_HEALTH_TITLE}
      subtitle={TRANSPORT_HEALTH_SUBTITLE}
      testId="widget-transport-health"
      resource={resource}
    >
      {(data: MetricsResponse) => (
        <MetricStatRow>
          <MetricStat
            label="HTTP requests"
            value={data.rpcHealth?.requestsPerSecond ?? null}
            format={formatRate}
            testId="transport-request-rate"
          />
          <MetricStat
            label="HTTP error responses"
            value={data.rpcHealth?.errorPercentage ?? null}
            format={formatPercentage}
            testId="transport-error-percentage"
          />
          <MetricStat
            label="p95 request duration"
            value={data.rpcHealth?.p95Seconds ?? null}
            format={formatDuration}
            testId="transport-p95"
          />
        </MetricStatRow>
      )}
    </MetricsWidgetCard>
  );
}
