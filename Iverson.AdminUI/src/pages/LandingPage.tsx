import { useAuth } from "react-oidc-context";
import { Box, Typography } from "@mui/material";
import { hasRole, useConsoleRoles } from "../auth/roles";
import { DataVolumeWidget } from "../widgets/DataVolumeWidget";
import { HealthStrip } from "../widgets/HealthStrip";
import { MetricsBand } from "../widgets/MetricsBand";
import { QdrantCollectionsWidget } from "../widgets/QdrantCollectionsWidget";
import { SchemaCatalogWidget } from "../widgets/SchemaCatalogWidget";
import { TenantRosterWidget } from "../widgets/TenantRosterWidget";

/**
 * The console's landing page.
 *
 * The access token is read ONCE here and passed down as a prop. Widgets take it as an
 * argument rather than each calling `useAuth()` themselves: `usePolledResource` already
 * treats the token as an argument for the same reason, and a widget that is a pure function
 * of (token, fetcher) can be render-tested without standing up an OIDC context.
 *
 * `user?.access_token` is `undefined` before the session resolves, which every widget renders
 * as "waiting for session" — the state that must NOT be confused with a spinner, since both
 * `awaitingToken` and `loading` are true at that first mount.
 *
 * **Admins only, cards by role** (CSR round 12 §2.2). The route guard in `router.tsx` admits an
 * Operator or a tenant admin and nobody else. Both see the health strip and the schema and
 * data-volume cards, whose endpoints require `ConsoleAdmin`. Only an Operator sees the tenant
 * roster, the four Band B metrics cards and the Qdrant card, whose endpoints are `Operator`-gated:
 * a tenant admin is not shown cards that could only answer 403.
 *
 * Nine cards for an Operator over six sources: the four Band B metrics cards are all fed by ONE
 * poll of `/admin/console/metrics`, which `MetricsBand` owns — see the note there.
 */
export function LandingPage() {
  const auth = useAuth();
  const accessToken = auth.user?.access_token;
  const operator = hasRole(useConsoleRoles(), "operator");

  return (
    <Box sx={{ p: 3 }}>
      <Typography variant="h5" component="h1" gutterBottom>
        Overview
      </Typography>
      <Box sx={{ mb: 2 }}>
        <HealthStrip accessToken={accessToken} />
      </Box>
      <Box
        sx={{
          display: "grid",
          gap: 2,
          gridTemplateColumns: { xs: "1fr", md: "repeat(2, minmax(0, 1fr))" },
        }}
      >
        {operator && <TenantRosterWidget accessToken={accessToken} />}
        <SchemaCatalogWidget accessToken={accessToken} />
        <DataVolumeWidget accessToken={accessToken} />
        {operator && <MetricsBand accessToken={accessToken} />}
        {operator && <QdrantCollectionsWidget accessToken={accessToken} />}
      </Box>
    </Box>
  );
}
