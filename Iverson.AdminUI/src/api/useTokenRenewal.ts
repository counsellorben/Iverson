import { useEffect } from "react";
import { useAuth } from "react-oidc-context";
import { setTokenRenewer } from "./client";

/**
 * Bridges the module-scope fetch layer to `oidc-client-ts`'s silent-renewal path.
 *
 * The fetch layer cannot call `useAuth()` — it is React context, and the fetch layer is not a
 * component. So this hook, mounted once inside the auth boundary (`AuthGate`), hands the
 * layer a renewer it can call when any request comes back 401. Mounting it once rather than
 * per widget is the point: the renewal is a single global action, not nine.
 *
 * `automaticSilentRenew` already renews ahead of expiry; this covers the case where the token
 * is rejected anyway — a clock skew, a revoked session, a renewal that quietly failed.
 */
export function useTokenRenewal(): void {
  const auth = useAuth();
  const signinSilent = auth?.signinSilent;

  useEffect(() => {
    if (typeof signinSilent !== "function") return;
    setTokenRenewer(() => signinSilent());
    return () => setTokenRenewer(null);
  }, [signinSilent]);
}
