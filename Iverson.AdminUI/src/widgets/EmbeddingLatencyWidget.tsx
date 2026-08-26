import { Typography } from "@mui/material";
import type { PolledResource } from "../hooks/usePolledResource";
import type { MetricsResponse } from "../api/types";
import { formatDuration } from "./format";
import { MetricStat, MetricStatRow } from "./MetricStat";
import { MetricsWidgetCard } from "./MetricsWidgetCard";

/**
 * p95 of the API's outbound HTTP calls to Ollama, from `http.client.request.duration`.
 *
 * **The conflation caveat is RENDERED, not merely commented.** `AddEmbeddings` and
 * `AddEnrichment` each register their own named `HttpClient`, but HTTP client metrics are
 * labelled by `server.address` — the host actually dialled — and carry nothing identifying the
 * logical client. So when both base URLs resolve to the same host, which is the default in
 * both docker-compose and the Helm chart, this p95 is the p95 of embeddings AND enrichment
 * together, and enrichment calls are the slower of the two.
 *
 * The person who needs that caveat is the one reading the number, and they are not reading
 * this file. It goes on the card.
 *
 * This is also the only signal the console has for Ollama at all: `/health` does not check it,
 * so the store-health strip deliberately has no Ollama tile and this figure stands in for it.
 */

export const EMBEDDING_LATENCY_SUBTITLE =
  "p95 of the API's outbound HTTP calls to Ollama. Polled every 30 seconds.";

/** The conflation caveat, rendered under the figure. Exported so its test cannot drift off it. */
export const EMBEDDING_LATENCY_CAVEAT =
  "Client metrics are labelled by server address, not by client name: if the embeddings and enrichment base URLs resolve to the same host, this figure covers both.";

export function EmbeddingLatencyWidget({
  resource,
}: {
  readonly resource: PolledResource<MetricsResponse>;
}) {
  return (
    <MetricsWidgetCard
      title="Embedding latency"
      subtitle={EMBEDDING_LATENCY_SUBTITLE}
      testId="widget-embedding-latency"
      resource={resource}
    >
      {(data: MetricsResponse) => (
        <>
          <MetricStatRow>
            <MetricStat
              label="p95 call duration"
              value={data.embeddingLatency?.p95Seconds ?? null}
              format={formatDuration}
              testId="embedding-p95"
            />
          </MetricStatRow>
          <Typography
            variant="caption"
            component="p"
            sx={{ mt: 1, color: "text.secondary" }}
            data-testid="embedding-latency-caveat"
          >
            {EMBEDDING_LATENCY_CAVEAT}
          </Typography>
        </>
      )}
    </MetricsWidgetCard>
  );
}
