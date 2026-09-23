import { fetchMetrics } from "../api/console";
import { usePolledResource } from "../hooks/usePolledResource";
import { ConsumerActivityWidget } from "./ConsumerActivityWidget";
import { EmbeddingLatencyWidget } from "./EmbeddingLatencyWidget";
import { FanOutBacklogWidget } from "./FanOutBacklogWidget";
import { METRICS_POLL_INTERVAL_MS } from "./MetricsWidgetCard";
import { TransportHealthWidget } from "./TransportHealthWidget";

/**
 * The four Band B cards, and the ONE poll of `/admin/console/metrics` that feeds all of them.
 *
 * Band B is four widgets over a single response: `MetricsResponse` carries `fanOutBacklog`,
 * `consumerActivity`, `rpcHealth` and `embeddingLatency` in one body, and the endpoint answers
 * it by issuing nine Prometheus queries. Giving each card its own `usePolledResource` would
 * therefore have put four requests — thirty-six Prometheus queries — on the wire every thirty
 * seconds per open browser tab, for four cards that would then be free to disagree with each
 * other about whether Prometheus was reachable. One poll, four presentations.
 *
 * This is not the page-level load gate Design 3 rules out. Nothing here waits on anything
 * else: the other five widgets on the landing page still fetch independently and render as
 * their own data arrives, and Design 3's own count — "nine widgets across seven sources" —
 * already says a source may feed more than one card.
 *
 * The four widgets are presentational: they take the resource rather than fetching it, which
 * is also what lets each one be rendered over a fixture without a timer. The hook lives here,
 * so this is the component their StrictMode tests exercise — `main.tsx` wraps the app in
 * `React.StrictMode`, and a hook that never leaves `loading` under it is invisible to `tsc`,
 * to the production build and to any test that does not mount under the wrapper.
 *
 * Every one of these four cards renders "not authorized" on a real deployment today: the
 * endpoint is `Operator`-gated and no human satisfies that policy yet (Design 4d).
 */
export function MetricsBand({ accessToken }: { readonly accessToken: string | undefined }) {
  const resource = usePolledResource(fetchMetrics, METRICS_POLL_INTERVAL_MS, accessToken);

  return (
    <>
      <FanOutBacklogWidget resource={resource} />
      <ConsumerActivityWidget resource={resource} />
      <TransportHealthWidget resource={resource} />
      <EmbeddingLatencyWidget resource={resource} />
    </>
  );
}
