import { createContext, useContext } from "react";
import { useAuth } from "react-oidc-context";
import { Alert, Box, CircularProgress } from "@mui/material";
import type { ApiFailure } from "../api/client";
import { fetchRoles } from "../api/console";
import type { ConsoleRolesResponse } from "../api/types";
import { usePolledResource } from "../hooks/usePolledResource";

/**
 * The console's two roles (CSR round 12 §2.2). The console is for admins only: an Operator (on this
 * install's operator list, or an admin-scoped automation token) or a tenant admin.
 *
 * **The server decides, the console renders.** The roles come from `GET /admin/console/roles`, which the
 * server answers with the same functions its `Operator` and `TenantAdmin` policies use. The console no
 * longer reads the token's `groups` claim: an Authentik group no longer makes an Operator, so the claim
 * cannot answer that question. Every route guard and the sidebar read the one answer `RolesProvider`
 * holds, so a hidden nav item and a blocked route can never disagree.
 *
 * **The guards are defence in depth, not the authorization decision.** The server enforces the same
 * roles on every endpoint these pages call.
 */
export type ConsoleRole = "operator" | "tenantAdmin";

export type ConsoleRoleState =
  | { status: "checking" }
  | { status: "ready"; roles: ConsoleRolesResponse }
  | { status: "unavailable"; failure: ApiFailure };

/** "checking" outside a provider, so a component rendered on its own offers nothing privileged. */
export const ConsoleRolesContext = createContext<ConsoleRoleState>({ status: "checking" });

export function useConsoleRoles(): ConsoleRoleState {
  return useContext(ConsoleRolesContext);
}

/**
 * Reads the caller's roles once per session token and publishes them. A failed read is retried with
 * `usePolledResource`'s backoff; the last good answer is kept while a retry runs.
 */
export function RolesProvider({ children }: { children: React.ReactNode }) {
  const auth = useAuth();
  const roles = usePolledResource(fetchRoles, null, auth.user?.access_token);

  const state: ConsoleRoleState =
    roles.data !== null
      ? { status: "ready", roles: roles.data }
      : roles.failure !== null
        ? { status: "unavailable", failure: roles.failure }
        : { status: "checking" };

  return <ConsoleRolesContext.Provider value={state}>{children}</ConsoleRolesContext.Provider>;
}

/** True only for an answer that grants `role` ("either": an Operator or a tenant admin). */
export function hasRole(state: ConsoleRoleState, role: ConsoleRole | "either"): boolean {
  if (state.status !== "ready") {
    return false;
  }

  return role === "either" ? state.roles.operator || state.roles.tenantAdmin : state.roles[role];
}

const DENIED: Record<ConsoleRole | "either", string> = {
  operator: "Not authorized: this page requires the Operator role.",
  tenantAdmin: "Not authorized: this page requires the tenant admin role.",
  either: "Not authorized: the console is for operators and tenant admins.",
};

/**
 * Route guard: renders its children only for a caller who holds `role`, progress while the roles are
 * being read, an error when they could not be read, and a refusal for everyone else. Placed around a
 * route's element in `router.tsx`, one layer inside `AuthGate`, which answers "is anyone signed in?".
 */
export function RequireRole({
  role,
  children,
}: {
  role: ConsoleRole | "either";
  children: React.ReactNode;
}) {
  const state = useConsoleRoles();

  if (state.status === "checking") {
    return (
      <Box sx={{ p: 3 }} data-testid="require-role-checking">
        <CircularProgress aria-label="Checking your console roles" />
      </Box>
    );
  }

  if (state.status === "unavailable") {
    return (
      <Box sx={{ p: 3 }}>
        <Alert severity="error" data-testid="require-role-unavailable">
          Could not read your console roles. The console retries on its own; reload the page to retry now.
        </Alert>
      </Box>
    );
  }

  if (!hasRole(state, role)) {
    return (
      <Box sx={{ p: 3 }}>
        <Alert severity="warning" data-testid="require-role-denied">
          {DENIED[role]}
        </Alert>
      </Box>
    );
  }

  return <>{children}</>;
}
