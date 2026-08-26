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
   * `revokeTokensOnSignout: true` (see `auth/AuthProvider.tsx`) makes token revocation a hard
   * PRECONDITION of signout, not a bonus step: oidc-client-ts's `_signoutStart` calls
   * `_revokeInternal(user)` BEFORE `removeUser()` and rethrows on failure. Revocation is a
   * cross-origin POST to Authentik, so it can fail for reasons that have nothing to do with
   * this console — CORS, a network blip, an IdP restart.
   *
   * Without this catch, that failure would leave Logout as a dead button with an unhandled
   * rejection, the user still signed in, and the local session still in sessionStorage — i.e.
   * the security fix would make a session STICKIER than it was before. `removeUser()` drops
   * the local session unconditionally, which restores the pre-fix behaviour as the floor:
   * `AuthGate` then observes the lost session and sends the browser back into the login flow.
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
