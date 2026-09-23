import { useCallback } from "react";
import { useAuth } from "react-oidc-context";
import { Outlet } from "react-router";
import { AppBar, Toolbar, Typography, Button, Box } from "@mui/material";
import { Sidebar } from "./Sidebar";
import { tokens } from "../theme/tokens";

export function AppLayout() {
  const auth = useAuth();
  const userEmail = auth.user?.profile?.email || "User";

  /**
   * Signs out, and clears the local session even if the IdP round-trip fails.
   *
   * `auth/AuthProvider.tsx` does not set `revokeTokensOnSignout`, so oidc-client-ts's
   * `_signoutStart` revokes nothing: it reads the stored user, removes it locally, then builds
   * the end-session request from the IdP's metadata and rethrows any failure. That metadata is a
   * cross-origin fetch to Authentik, so it can fail for reasons that have nothing to do with
   * this console — CORS, a network blip, an IdP restart — and a failure before the local
   * removal (reading storage) leaves the session in place.
   *
   * Without this catch, such a failure would leave Logout as a dead button with an unhandled
   * rejection. `removeUser()` drops the local session unconditionally as the floor: `AuthGate`
   * then observes the lost session and sends the browser back into the login flow. Were
   * revocation ever switched on, it would run BEFORE the local removal and rethrow, which makes
   * this floor matter more, not less.
   */
  const signOut = useCallback(async () => {
    try {
      await auth.signoutRedirect();
    } catch (error) {
      console.warn("Signout via the IdP failed; clearing the local session instead.", error);
      try {
        await auth.removeUser();
      } catch (removeError) {
        console.error("Could not clear the local session.", removeError);
      }
    }
  }, [auth]);

  return (
    <Box>
      <AppBar position="static">
        <Toolbar>
          <Typography variant="h6" sx={{ fontFamily: tokens.fontHeading, flexGrow: 1 }}>
            Iverson
          </Typography>
          <span>{userEmail}</span>
          <Button color="inherit" onClick={signOut}>
            Logout
          </Button>
        </Toolbar>
      </AppBar>
      <Box sx={{ display: "flex" }}>
        <Sidebar />
        <Box component="main" sx={{ flexGrow: 1 }}>
          <Outlet />
        </Box>
      </Box>
    </Box>
  );
}
