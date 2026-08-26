import { useEffect } from "react";
import { AuthProvider as OidcAuthProvider, useAuth } from "react-oidc-context";
import { config } from "../config";
import { useTokenRenewal } from "../api/useTokenRenewal";

const oidcConfig = {
  authority: config.oidcAuthority,
  client_id: config.oidcClientId,
  redirect_uri: `${window.location.origin}${import.meta.env.DEV ? "" : "/admin"}/callback`,
  post_logout_redirect_uri: `${window.location.origin}${import.meta.env.DEV ? "" : "/admin"}/`,
  scope: "openid groups tenant_id offline_access",
  automaticSilentRenew: true,
};

export function AuthProvider({ children }: { children: React.ReactNode }) {
  return <OidcAuthProvider {...oidcConfig}>{children}</OidcAuthProvider>;
}

/**
 * Gates its children behind an authenticated session, redirecting an
 * unauthenticated visitor into the Authentik login flow. `AppLayout`
 * (Task 4) is the intended child; this component only concerns itself
 * with the auth boundary.
 *
 * It is also where the console's fetch layer is bridged to silent renewal. A 401 on any of
 * the landing page's nine widgets is one expired session, not nine broken cards, so the
 * renewal callback is registered ONCE here — inside the auth boundary, where `useAuth()` is
 * reachable — rather than per widget. See `api/useTokenRenewal`.
 */
export function AuthGate({ children }: { children: React.ReactNode }) {
  const auth = useAuth();
  useTokenRenewal();

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
