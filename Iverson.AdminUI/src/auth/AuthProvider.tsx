import { useEffect } from "react";
import { AuthProvider as OidcAuthProvider, useAuth } from "react-oidc-context";
import { config } from "../config";
import { useSessionExpiry } from "./useSessionExpiry";

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

const oidcConfig = {
  authority: config.oidcAuthority,
  client_id: config.oidcClientId,
  redirect_uri: `${window.location.origin}${import.meta.env.DEV ? "" : "/admin"}/callback`,
  post_logout_redirect_uri: `${window.location.origin}${import.meta.env.DEV ? "" : "/admin"}/`,
  // CSR round-2 finding #16 follow-up: request groups/tenant_id so the id_token actually
  // carries the claims a tenant-aware admin console needs (verified live 2026-09-11 — without
  // these scopes the claims come back null even though the provider supports them; the
  // .well-known/openid-configuration scopes_supported list already included both). Still
  // deliberately no `offline_access` — see the comment above; that decision is unrelated to
  // and unaffected by this addition.
  scope: "openid profile email groups tenant_id",
  automaticSilentRenew: false,
  onSigninCallback,
};

export function AuthProvider({ children }: { children: React.ReactNode }) {
  return <OidcAuthProvider {...oidcConfig}>{children}</OidcAuthProvider>;
}

/**
 * Gates its children behind an authenticated session, redirecting an
 * unauthenticated visitor into the Authentik login flow. `AppLayout`
 * (Task 4) is the intended child; this component only concerns itself
 * with the auth boundary. It also ends the session when the access token
 * expires — see `useSessionExpiry`.
 */
export function AuthGate({ children }: { children: React.ReactNode }) {
  const auth = useAuth();
  useSessionExpiry();

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
