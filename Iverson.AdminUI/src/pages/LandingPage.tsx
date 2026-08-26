import { useAuth } from "react-oidc-context";
import { Box, Typography } from "@mui/material";
import { DataVolumeWidget } from "../widgets/DataVolumeWidget";
import { HealthStrip } from "../widgets/HealthStrip";
import { MetricsBand } from "../widgets/MetricsBand";
import { QdrantCollectionsWidget } from "../widgets/QdrantCollectionsWidget";
import { SchemaCatalogWidget } from "../widgets/SchemaCatalogWidget";
import { TenantRosterWidget } from "../widgets/TenantRosterWidget";

/**
 * The console's landing page, replacing the redirect to `/performance`.
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
 * Nine cards, seven sources: the four Band B metrics cards are all fed by ONE poll of
 * `/admin/console/metrics`, which `MetricsBand` owns — see the note there. Six of the nine
 * render "not authorized" on a real deployment today, because `tenants`, `metrics` and
 * `qdrant` are all `Operator`-gated and no human satisfies that policy yet (Design 4d). That
 * is a first-class rendered state, not an error, and it is deliberately not hidden: the page
 * degrades per Design 3 rather than showing a smaller page to a less privileged user.
 */
export function LandingPage() {
  const auth = useAuth();
  const accessToken = auth.user?.access_token;

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
        <TenantRosterWidget accessToken={accessToken} />
        <SchemaCatalogWidget accessToken={accessToken} />
        <DataVolumeWidget accessToken={accessToken} />
        <MetricsBand accessToken={accessToken} />
        <QdrantCollectionsWidget accessToken={accessToken} />
      </Box>
    </Box>
  );
}
