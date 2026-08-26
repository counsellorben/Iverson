import { useAuth } from "react-oidc-context";
import { Box, Typography } from "@mui/material";
import { DataVolumeWidget } from "../widgets/DataVolumeWidget";
import { HealthStrip } from "../widgets/HealthStrip";
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
 * Band B's widgets (metrics, Qdrant collections) are added to this page by a later task.
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
      </Box>
    </Box>
  );
}
