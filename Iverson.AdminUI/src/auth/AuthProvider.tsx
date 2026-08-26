import { useEffect } from "react";
import { AuthProvider as OidcAuthProvider, useAuth } from "react-oidc-context";
import { config } from "../config";
import { useTokenRenewal } from "../api/useTokenRenewal";

/**
 * Removes the OIDC authorization code and state from the address bar once the code has been
 * exchanged for tokens.
 *
 * The code is single-use and already spent by the time this runs, but the URL that carries it
 * is not: it stays in `window.location`, in the browser's history entry, and — the reason this
 * matters here — in the `http.url` attribute of every span the OpenTelemetry web SDK records
 * for this document, which the console exports to Jaeger. A `?code=` in a trace backend is a
 * credential in a log store.
 *
 * `replaceState` rather than `pushState`: the callback URL must not become a history entry the
 * back button can return to.
 *
 * This covers the SUCCESS path only. The error path — where `CallbackPage` renders the failure
 * and never navigates, so `react-oidc-context` never invokes this — is covered by the span
 * attribute scrub in `telemetry.ts`. Both are needed; neither subsumes the other.
 */
export function onSigninCallback(): void {
  window.history.replaceState({}, document.title, window.location.pathname);
}

export const oidcConfig = {
  authority: config.oidcAuthority,
  client_id: config.oidcClientId,
  redirect_uri: `${window.location.origin}${import.meta.env.DEV ? "" : "/admin"}/callback`,
  post_logout_redirect_uri: `${window.location.origin}${import.meta.env.DEV ? "" : "/admin"}/`,
  // `offline_access` is load-bearing and must stay. The landing page polls for hours, so the
  // access token has to renew; without a refresh token `automaticSilentRenew` falls back to
  // IFRAME silent renew, for which this console registers no callback handler at all.
  scope: "openid groups tenant_id offline_access",
  automaticSilentRenew: true,
  // Ask the IdP to revoke the access and refresh tokens at signout instead of only dropping
  // them from sessionStorage. Without this, a "logged out" session leaves a refresh token that
  // is still valid at Authentik for its full lifetime. `react-oidc-context` spreads settings it
  // does not recognise straight through into the `UserManager`, so this reaches oidc-client-ts.
  revokeTokensOnSignout: true,
  onSigninCallback,
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
