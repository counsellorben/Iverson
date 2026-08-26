import { Table, TableBody, TableCell, TableHead, TableRow, Typography } from "@mui/material";
import { fetchTenants } from "../api/console";
import type { TenantsResponse } from "../api/types";
import { usePolledResource } from "../hooks/usePolledResource";
import { formatInstant, formatInteger, pluralise } from "./format";
import { WidgetCard } from "./WidgetCard";

/**
 * The tenant roster from `/admin/console/tenants`.
 *
 * **"Not authorized" is this widget's primary state, not its edge case.** The endpoint is
 * Operator-gated, and no human satisfies the `Operator` policy in the deployment today
 * (Design 4d — a known live blocker, not a bug to work around here), so `kind: "forbidden"`
 * is what every real operator sees. It is therefore rendered as an explicit authorization
 * answer, with wording that names the policy, rather than as a red "request failed" card that
 * would send someone hunting a fault that does not exist.
 *
 * Fetched on mount and on manual Refresh only — a cross-tenant enumeration changes on human
 * timescales, so `intervalMs` is `null`. Note that a null interval still retries a FAILED
 * fetch at 60s, 120s and 240s before it gives up; it means "never polled on success".
 */
export function TenantRosterWidget({ accessToken }: { accessToken: string | undefined }) {
  const resource = usePolledResource(fetchTenants, null, accessToken);

  return (
    <WidgetCard
      title="Tenants"
      subtitle="Fetched on load and on refresh; never polled."
      testId="widget-tenants"
      resource={resource}
      forbiddenMessage="Not authorized: listing tenants requires the Operator role."
    >
      {(data: TenantsResponse) => (
        <>
          <Typography variant="body2" data-testid="tenant-count" data-count={data.count}>
            {formatInteger(data.count)} {pluralise(data.count, "tenant")}
          </Typography>
          {data.tenants.length > 0 && (
            <Table size="small" aria-label="Tenants">
              <TableHead>
                <TableRow>
                  <TableCell>Name</TableCell>
                  <TableCell>Status</TableCell>
                  <TableCell>Created</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {data.tenants.map((tenant) => (
                  <TableRow key={tenant.id} data-testid={`tenant-row-${tenant.id}`}>
                    <TableCell>{tenant.displayName}</TableCell>
                    <TableCell>{tenant.status}</TableCell>
                    <TableCell>{formatInstant(tenant.createdAt)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </>
      )}
    </WidgetCard>
  );
}
