import { useEffect } from "react";
import { AuthProvider as OidcAuthProvider, useAuth } from "react-oidc-context";
import { config } from "../config";

// Deliberately no `offline_access` scope: requesting it makes oidc-client-ts
// obtain a refresh token and store the whole token set (access + id +
// refresh) in sessionStorage, readable by any JS on the origin — an
// XSS-exfiltration surface for the single most valuable token in the
// session. Without a refresh token, `automaticSilentRenew` would fall back
// to a hidden-iframe silent renewal against the authority, but this app's
// CSP (nginx.conf) is `default-src 'self'` with no `frame-src`, so that
// iframe is already blocked — a futile renewal attempt would just log
// errors on a timer. We do NOT loosen the CSP to allow it (that would trade
// a smaller token-storage exposure for a larger framing/clickjacking one).
// So `automaticSilentRenew` is off, and the session simply ends at
// access-token expiry, requiring a fresh login. That's an accepted tradeoff
// for an admin console whose pages are currently stubs rendering no tenant
// data; revisit if silent renewal becomes worth reintroducing (e.g. via a
// backend-mediated refresh that never puts the refresh token in the
// browser).
const oidcConfig = {
  authority: config.oidcAuthority,
  client_id: config.oidcClientId,
  redirect_uri: `${window.location.origin}${import.meta.env.DEV ? "" : "/admin"}/callback`,
  post_logout_redirect_uri: `${window.location.origin}${import.meta.env.DEV ? "" : "/admin"}/`,
  scope: "openid profile email",
  automaticSilentRenew: false,
};

export function AuthProvider({ children }: { children: React.ReactNode }) {
  return <OidcAuthProvider {...oidcConfig}>{children}</OidcAuthProvider>;
}

/**
 * Gates its children behind an authenticated session, redirecting an
 * unauthenticated visitor into the Authentik login flow. `AppLayout`
 * (Task 4) is the intended child; this component only concerns itself
 * with the auth boundary.
 */
export function AuthGate({ children }: { children: React.ReactNode }) {
  const auth = useAuth();

  useEffect(() => {
    if (!auth.isLoading && !auth.isAuthenticated) {
      auth.signinRedirect();
    }
  }, [auth.isLoading, auth.isAuthenticated, auth.signinRedirect]);

  if (!auth.isAuthenticated) {
    return null;
  }

  return <>{children}</>;
}
