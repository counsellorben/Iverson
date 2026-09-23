import { useEffect } from "react";
import { useAuth } from "react-oidc-context";

// No refresh token and no iframe fallback, so an expired token cannot renew: end the session instead.
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
