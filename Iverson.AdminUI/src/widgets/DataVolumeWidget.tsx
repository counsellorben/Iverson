import { Table, TableBody, TableCell, TableHead, TableRow, Typography } from "@mui/material";
import { fetchDataVolume } from "../api/console";
import type { DataVolumeResponse } from "../api/types";
import { usePolledResource } from "../hooks/usePolledResource";
import { HiddenTypesNote } from "./HiddenTypesNote";
import { formatInteger, pluralise } from "./format";
import { WidgetCard } from "./WidgetCard";

/**
 * Row counts per object type, from `/admin/console/data-volume`.
 *
 * **Never polled.** One request costs a sequential StarRocks `COUNT(*)` per registered type,
 * uncached, with row authorization re-evaluated per type — so a 30-second timer would turn
 * one idle browser tab into sustained aggregate load for a number that moves slowly, and six
 * tabs into six times that. Mount plus manual Refresh, and `intervalMs` is `null`.
 *
 * **Tenant-scoped, not a deployment total.** Row authorization runs per type, so these are the
 * rows THIS session can see. The subtitle says so, because a number that looks like a
 * deployment-wide total and is not would be read wrong every single time.
 *
 * **The zeros are real.** A denied type produces no entry at all — only the aggregate
 * `deniedTypeCount` — so there is no path by which authorization can manufacture a `0`. The
 * hidden types are surfaced as their own "N types not shown" line instead of as a hedge
 * smeared across trustworthy numbers.
 */

/** `reason` values `/admin/console/data-volume` sends with its 503, as prose. */
export function describeDataVolumeReason(reason: string | null): string {
  switch (reason) {
    case "disabled":
      return "The engagement store is disabled in this deployment, so there are no row counts to report.";
    case "notReady":
      return "The engagement store is not ready yet. Try refreshing shortly.";
    default:
      return reason === null ? "Currently unavailable." : `Currently unavailable: ${reason}.`;
  }
}

export function DataVolumeWidget({ accessToken }: { accessToken: string | undefined }) {
  const resource = usePolledResource(fetchDataVolume, null, accessToken);

  return (
    <WidgetCard
      title="Data volume"
      subtitle="Tenant-scoped row counts, not a deployment total. Fetched on load and on refresh; never polled."
      testId="widget-data-volume"
      resource={resource}
      reasonText={describeDataVolumeReason}
    >
      {(data: DataVolumeResponse) => {
        // Denied and unknown are separate server-side causes with one user-facing meaning:
        // this list is shorter than the registry. They are summed for the affordance and
        // never folded into the visible rows.
        const hiddenTypeCount = data.deniedTypeCount + data.unknownTypeCount;
        return (
          <>
            <Typography
              variant="body2"
              data-testid="data-volume-type-count"
              data-count={data.types.length}
            >
              {formatInteger(data.types.length)} {pluralise(data.types.length, "type")} counted
            </Typography>
            <HiddenTypesNote
              count={hiddenTypeCount}
              testId="data-volume-hidden-types"
              explanation="not visible to you"
            />
            {data.types.length > 0 && (
              <Table size="small" aria-label="Row counts by type">
                <TableHead>
                  <TableRow>
                    <TableCell>Type</TableCell>
                    <TableCell align="right">Rows</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {data.types.map((entry) => (
                    <TableRow
                      key={entry.typeName}
                      data-testid={`data-volume-row-${entry.typeName}`}
                      data-row-count={entry.rowCount}
                    >
                      <TableCell>{entry.typeName}</TableCell>
                      <TableCell align="right">{formatInteger(entry.rowCount)}</TableCell>
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
