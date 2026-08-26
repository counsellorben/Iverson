/**
 * Wire shapes of the admin console's read-only JSON endpoints.
 *
 * These mirror the C# response records one-for-one:
 * `Iverson.Server/Iverson.Api/Console/AdminConsoleEndpoints.cs` (tenants, schema,
 * data-volume, qdrant), `AdminConsoleMetricsEndpoint.cs` (metrics), and the anonymous
 * `/health` endpoint in `Program.cs`. The minimal-API pipeline serializes with the web
 * defaults, so every property below is the camelCase spelling of its PascalCase record
 * member.
 *
 * The distinctions the server went out of its way to keep on the wire are preserved here
 * rather than smoothed over — see the per-type notes. A widget that renders a `0` where the
 * truth is "withheld from you" is the specific defect these shapes exist to prevent.
 */

// ── /admin/console/tenants (Operator) ─────────────────────────────────────────

export interface TenantSummary {
  id: string;
  displayName: string;
  status: string;
  /** ISO-8601 instant, as serialized from `DateTime`. */
  createdAt: string;
}

export interface TenantsResponse {
  count: number;
  tenants: TenantSummary[];
}

// ── /admin/console/schema (authenticated) ─────────────────────────────────────

export type RelationKind = "OneToOne" | "OneToMany" | "ManyToOne" | "ManyToMany";

export interface SchemaRelationEdge {
  propertyName: string;
  kind: RelationKind;
  relatedType: string;
  foreignKey: string;
}

export interface SchemaTypeSummary {
  name: string;
  description: string;
  fieldCount: number;
  relations: SchemaRelationEdge[];
}

export interface SchemaCatalogResponse {
  typeCount: number;
  types: SchemaTypeSummary[];
  /**
   * Registered types this caller may not see, counted but never named. NON-ZERO WITH AN
   * EMPTY `types` IS NOT "no types are registered" — it is "you may see none of the N that
   * are". Render it; do not drop it.
   */
  withheldTypeCount: number;
}

// ── /admin/console/data-volume (authenticated) ────────────────────────────────

/**
 * One counted type. `status` is always `"counted"` as the endpoint is written — a denied
 * type has NO ENTRY AT ALL, so a false `rowCount: 0` is unconstructible rather than merely
 * discouraged.
 */
export interface TypeRowCountEntry {
  typeName: string;
  status: "counted";
  rowCount: number;
}

export interface DataVolumeResponse {
  types: TypeRowCountEntry[];
  /** Types denied to this caller by row/field authorization. Counted, never named. */
  deniedTypeCount: number;
  /** Types that vanished from the registry mid-request. Effectively always 0. */
  unknownTypeCount: number;
}

// ── /admin/console/metrics (Operator) ─────────────────────────────────────────
//
// Every value below is `number | null`. `null` means "Prometheus answered, but this series
// has no current sample" — a fresh deployment before its first scrape, or a counter that has
// never incremented. It is NOT zero, and must not be rendered as zero.

export interface FanOutBacklogMetrics {
  reconciliationQueueDepth: number | null;
  dlqUnreplayedCount: number | null;
  documentRerenderQueueDepth: number | null;
}

export interface ConsumerActivityMetrics {
  consumerRetriesPerSecond: number | null;
  consumerDlqRoutedPerSecond: number | null;
}

/**
 * HTTP-transport health for the API's own request pipeline. `errorPercentage` is HTTP
 * status, not gRPC status: a gRPC call failing inside an HTTP 200 does not count. Label it
 * transport health (Task 11, Step 2).
 */
export interface RpcHealthMetrics {
  requestsPerSecond: number | null;
  errorPercentage: number | null;
  p95Seconds: number | null;
}

/**
 * p95 of the API's outbound HTTP calls to Ollama. HTTP client metrics label by
 * `server.address`, so if the embeddings and enrichment base URLs resolve to the same host
 * this figure covers both (Task 11, Step 3).
 */
export interface EmbeddingLatencyMetrics {
  p95Seconds: number | null;
}

export interface MetricsResponse {
  fanOutBacklog: FanOutBacklogMetrics;
  consumerActivity: ConsumerActivityMetrics;
  rpcHealth: RpcHealthMetrics;
  embeddingLatency: EmbeddingLatencyMetrics;
}

// ── /admin/console/qdrant (Operator) ──────────────────────────────────────────

export interface QdrantCollectionSummary {
  name: string;
  /** `null` when Qdrant did not report the figure — not zero points. */
  pointsCount: number | null;
  indexedVectorsCount: number | null;
}

export interface QdrantResponse {
  collectionCount: number;
  collections: QdrantCollectionSummary[];
}

// ── /health (anonymous, also served on the admin-api host) ────────────────────

/**
 * `starrocks` has THREE states, not two: `true`, `false`, or the literal string
 * `"disabled"` when the engagement store is switched off. The other three are booleans.
 */
export interface HealthChecks {
  postgres: boolean;
  starrocks: boolean | "disabled";
  qdrant: boolean;
  kafka: boolean;
}

export interface HealthResponse {
  status: "healthy" | "degraded";
  checks: HealthChecks;
}

// ── Shared 503 body ───────────────────────────────────────────────────────────

/**
 * The body both `/admin/console/data-volume` and `/admin/console/metrics` return with a 503.
 * `reason` is machine-readable and is the whole point of the status code carrying a body:
 *
 *  - data-volume: `"notReady"` (StarRocks is not ready yet) | `"disabled"` (engagement store off)
 *  - metrics:     `"notDeployed"` (Prometheus is not installed — a supported profile, not a
 *                 failure) | `"unreachable"` (installed but not answering)
 *
 * `"disabled"` and `"notDeployed"` are configuration statements to render calmly. `"notReady"`
 * and `"unreachable"` are faults.
 */
export interface UnavailableResponse {
  reason: string;
  error: string;
}
