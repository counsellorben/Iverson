import { useEffect } from "react";
import { AuthProvider as OidcAuthProvider, useAuth } from "react-oidc-context";
import { config } from "../config";

const oidcConfig = {
  authority: config.oidcAuthority,
  client_id: config.oidcClientId,
  redirect_uri: `${window.location.origin}${import.meta.env.DEV ? "" : "/admin"}/callback`,
  post_logout_redirect_uri: `${window.location.origin}${import.meta.env.DEV ? "" : "/admin"}/`,
  scope: "openid profile email offline_access",
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
