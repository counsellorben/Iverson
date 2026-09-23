import { useAuth } from "react-oidc-context";
import { Alert, Box } from "@mui/material";
import { hasGroup } from "./groups";

/**
 * Route guard: renders its children only for a user who is a member of `group`, and a refusal
 * notice for everyone else. Mirrors `AuthGate`'s shape — a wrapper element placed around a
 * route's element in `router.tsx` — one layer further in: `AuthGate` answers "is anyone signed
 * in?", this answers "is the signed-in person allowed on this page?".
 *
 * **This is defence in depth, not the authorization decision.** The server enforces the same
 * rule on every endpoint these pages call (`OperatorAuthorizationPolicy`,
 * `TenantAdminAuthorizationPolicy`); a browser-side guard cannot be trusted and is not relied
 * on. What it buys is that a privileged page is *unreachable* rather than merely unlinked —
 * the sidebar already hides the nav item, but before this guard, typing `/tenants` into the
 * address bar still mounted the page.
 *
 * **Deliberately NOT used on the landing page at `/`.** That page shows Operator-gated widgets
 * to every authenticated user on purpose and degrades card by card, so guarding it away would
 * replace nine informative cards with one refusal.
 */
export function RequireGroup({
  group,
  children,
}: {
  group: string;
  children: React.ReactNode;
}) {
  const auth = useAuth();

  if (!hasGroup(auth.user?.profile, group)) {
    return (
      <Box sx={{ p: 3 }}>
        <Alert severity="warning" data-testid="require-group-denied">
          Not authorized: this page requires membership in the "{group}" group.
        </Alert>
      </Box>
    );
  }

  return <>{children}</>;
}
