import { useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "react-oidc-context";

/**
 * OIDC redirect_uri target. `react-oidc-context`'s `AuthProvider` handles the
 * authorization-code exchange itself; this page just observes the resulting
 * isLoading/error states and, once the exchange has settled successfully,
 * navigates back into the app.
 */
export function CallbackPage() {
  const auth = useAuth();
  const navigate = useNavigate();

  useEffect(() => {
    if (!auth.isLoading && !auth.error) {
      navigate("/", { replace: true });
    }
  }, [auth.isLoading, auth.error, navigate]);

  if (auth.error) {
    return <div>Authentication error: {auth.error.message}</div>;
  }

  return <div>Signing in…</div>;
}
