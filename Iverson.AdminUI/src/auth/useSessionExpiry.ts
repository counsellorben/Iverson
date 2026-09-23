import { useEffect } from "react";
import { useAuth } from "react-oidc-context";

/**
 * Ends the session when the access token expires, because nothing here can renew it.
 *
 * `AuthProvider` requests no `offline_access` scope, so there is no refresh token, and
 * `automaticSilentRenew` is off because main's CSP blocks the hidden iframe that silent renewal
 * needs. An expiring token therefore has no path back to a valid one.
 *
 * Subscribes to `addAccessTokenExpired` and, on expiry, calls `removeUser()`. That drops the
 * local session, `AuthGate` observes the lost session, and the browser is redirected to login.
 *
 * A failed `removeUser()` is caught and logged rather than left to become an unhandled
 * rejection, the same discipline `AppLayout`'s `signOut` applies to its own fallback.
 */
export function useSessionExpiry(): void {
  const auth = useAuth();
  const events = auth?.events;
  const removeUser = auth?.removeUser;

  useEffect(() => {
    if (!events || typeof removeUser !== "function") return;
    return events.addAccessTokenExpired(async () => {
      // Caught and logged, as AppLayout's signOut does: a failed removeUser must not surface as an
      // unhandled rejection.
      try {
        await removeUser();
      } catch (error) {
        console.error("Could not end the expired session.", error);
      }
    });
  }, [events, removeUser]);
}
