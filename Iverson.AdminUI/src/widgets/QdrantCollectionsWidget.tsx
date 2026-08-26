import { Table, TableBody, TableCell, TableHead, TableRow, Typography } from "@mui/material";
import { fetchQdrant } from "../api/console";
import type { QdrantResponse } from "../api/types";
import { usePolledResource } from "../hooks/usePolledResource";
import { HiddenTypesNote } from "./HiddenTypesNote";
import { NO_SAMPLE_VALUE, formatInteger, pluralise } from "./format";
import { WidgetCard } from "./WidgetCard";

/**
 * Points and indexed vectors per Qdrant collection, from `/admin/console/qdrant`.
 *
 * Two things this widget must not do:
 *
 * - **Render a missing count as zero.** `pointsCount` and `indexedVectorsCount` are
 *   `number | null`: Qdrant does not always report them, and a collection whose figure is
 *   absent is not an empty collection. "0 points" on a collection that is actually serving
 *   traffic is the kind of number someone deletes a collection over. Absent renders as words —
 *   `NO_SAMPLE_VALUE`, the same word a metric with no current sample gets, so one fact has
 *   one spelling across the whole page.
 * - **Present a partial list as a complete one.** `IntelligenceCollectionReader` contains a
 *   per-collection stats failure so one bad collection cannot blank the card, which means
 *   `collections` can be SHORTER than what Qdrant listed. `unreadableCollectionCount` is that
 *   shortfall and gets its own `HiddenTypesNote` line — the same affordance the schema and
 *   data-volume cards use — because "7 collections" read as complete while Qdrant holds 10 is
 *   the same class of lie as a denied type rendered as "0 rows".
 * - **Present its 403 as an error.** The endpoint is `Operator`-gated — collection names are
 *   tenant-scoped, so enumerating them is a cross-tenant read — and no human satisfies the
 *   `Operator` policy today (Design 4d), so 403 is what this card shows on a real deployment.
 *   `WidgetCard`'s `forbidden` path renders that as an authorization answer at warning
 *   severity; a widget rendering its own `<Alert>` would be outside that guarantee.
 *
 * `indexedVectorsCount` legitimately differs from `pointsCount` — a point is indexed
 * asynchronously, and a collection below Qdrant's indexing threshold reports zero indexed
 * vectors while serving every query by brute force. The two are shown side by side rather than
 * as a ratio for that reason: a "50% indexed" figure would read as a fault.
 */

/** Design 3's cadence for the Qdrant card. */
export const QDRANT_POLL_INTERVAL_MS = 30_000;

export const QDRANT_FORBIDDEN_MESSAGE =
  "Not authorized: listing Qdrant collections requires the Operator role.";

export function QdrantCollectionsWidget({
  accessToken,
}: {
  readonly accessToken: string | undefined;
}) {
  const resource = usePolledResource(fetchQdrant, QDRANT_POLL_INTERVAL_MS, accessToken);

  return (
    <WidgetCard
      title="Qdrant collections"
      subtitle="Points and indexed vectors per collection. Polled every 30 seconds."
      testId="widget-qdrant"
      resource={resource}
      forbiddenMessage={QDRANT_FORBIDDEN_MESSAGE}
      pollIntervalMs={QDRANT_POLL_INTERVAL_MS}
    >
      {(data: QdrantResponse) => {
        const collections = data.collections ?? [];
        return (
          <>
            <Typography
              variant="body2"
              data-testid="qdrant-collection-count"
              data-count={collections.length}
            >
              {formatInteger(collections.length)} {pluralise(collections.length, "collection")}
            </Typography>
            <HiddenTypesNote
              count={data.unreadableCollectionCount}
              testId="qdrant-unreadable-collections"
              noun="collection"
              explanation="listed by Qdrant, but their stats could not be read"
            />
            {collections.length > 0 && (
              <Table size="small" aria-label="Qdrant collections">
                <TableHead>
                  <TableRow>
                    <TableCell>Collection</TableCell>
                    <TableCell align="right">Points</TableCell>
                    <TableCell align="right">Indexed vectors</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {collections.map((collection) => (
                    <TableRow
                      key={collection.name}
                      data-testid={`qdrant-row-${collection.name}`}
                      data-points-null={(collection.pointsCount ?? null) === null}
                      data-indexed-null={(collection.indexedVectorsCount ?? null) === null}
                    >
                      <TableCell>{collection.name}</TableCell>
                      <TableCell align="right">{countCell(collection.pointsCount)}</TableCell>
                      <TableCell align="right">
                        {countCell(collection.indexedVectorsCount)}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </>
        );
      }}
    </WidgetCard>
  );
}

/** A count Qdrant may not have reported. `null` becomes words, never a numeral. */
function countCell(value: number | null | undefined): string {
  return value === null || value === undefined ? NO_SAMPLE_VALUE : formatInteger(value);
}
