import { render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ApiResult } from "../api/client";
import type { ConsoleRolesResponse } from "../api/types";

const useAuthMock = vi.fn();
const fetchRolesMock =
  vi.fn<(token: string, signal: AbortSignal) => Promise<ApiResult<ConsoleRolesResponse>>>();

vi.mock("react-oidc-context", () => ({
  useAuth: () => useAuthMock(),
}));

vi.mock("../api/console", () => ({
  fetchRoles: (token: string, signal: AbortSignal) => fetchRolesMock(token, signal),
}));

import { ConsoleRolesContext, RequireRole, RolesProvider, hasRole, useConsoleRoles } from "./roles";
import type { ConsoleRole, ConsoleRoleState } from "./roles";

const TOKEN = "roles-access-token";

const CHECKING: ConsoleRoleState = { status: "checking" };
const UNAVAILABLE: ConsoleRoleState = {
  status: "unavailable",
  failure: { kind: "failed", status: null, message: "network down" },
};

function ready(operator: boolean, tenantAdmin: boolean): ConsoleRoleState {
  return { status: "ready", roles: { operator, tenantAdmin } };
}

function renderGuard(state: ConsoleRoleState, role: ConsoleRole | "either") {
  return render(
    <ConsoleRolesContext.Provider value={state}>
      <RequireRole role={role}>
        <div>Privileged content</div>
      </RequireRole>
    </ConsoleRolesContext.Provider>
  );
}

/** Renders what the provider publishes, so a test can read it. */
function RoleProbe() {
  const state = useConsoleRoles();
  return (
    <div
      data-testid="role-probe"
      data-status={state.status}
      data-operator={String(hasRole(state, "operator"))}
      data-tenant-admin={String(hasRole(state, "tenantAdmin"))}
    />
  );
}

beforeEach(() => {
  useAuthMock.mockReset();
  fetchRolesMock.mockReset();
});

describe("RequireRole", () => {
  it("renders an Operator page for an operator", () => {
    renderGuard(ready(true, false), "operator");

    expect(screen.getByText("Privileged content")).toBeInTheDocument();
    expect(screen.queryByTestId("require-role-denied")).not.toBeInTheDocument();
  });

  it("refuses a tenant admin an Operator page", () => {
    renderGuard(ready(false, true), "operator");

    expect(screen.queryByText("Privileged content")).not.toBeInTheDocument();
    expect(screen.getByTestId("require-role-denied")).toHaveTextContent(
      "Not authorized: this page requires the Operator role."
    );
  });

  it("refuses an operator a tenant-admin page: the two roles answer different questions", () => {
    renderGuard(ready(true, false), "tenantAdmin");

    expect(screen.queryByText("Privileged content")).not.toBeInTheDocument();
    expect(screen.getByTestId("require-role-denied")).toHaveTextContent(
      "Not authorized: this page requires the tenant admin role."
    );
  });

  it("admits an operator and a tenant admin alike to a page for either role", () => {
    for (const state of [ready(true, false), ready(false, true)]) {
      const { unmount } = renderGuard(state, "either");
      expect(screen.getByText("Privileged content")).toBeInTheDocument();
      unmount();
    }
  });

  it("refuses a signed-in user with neither role a page for either role", () => {
    renderGuard(ready(false, false), "either");

    expect(screen.queryByText("Privileged content")).not.toBeInTheDocument();
    expect(screen.getByTestId("require-role-denied")).toHaveTextContent(
      "Not authorized: the console is for operators and tenant admins."
    );
  });

  it("shows progress, not the page and not a refusal, while the roles are being read", () => {
    renderGuard(CHECKING, "either");

    expect(screen.getByTestId("require-role-checking")).toBeInTheDocument();
    expect(screen.queryByText("Privileged content")).not.toBeInTheDocument();
    expect(screen.queryByTestId("require-role-denied")).not.toBeInTheDocument();
  });

  it("says the roles could not be read when the request failed, rather than refusing", () => {
    renderGuard(UNAVAILABLE, "either");

    expect(screen.getByTestId("require-role-unavailable")).toBeInTheDocument();
    expect(screen.queryByText("Privileged content")).not.toBeInTheDocument();
    expect(screen.queryByTestId("require-role-denied")).not.toBeInTheDocument();
  });
});

describe("RolesProvider", () => {
  it("reads the roles once with the session's token and publishes them", async () => {
    useAuthMock.mockReturnValue({ user: { access_token: TOKEN } });
    fetchRolesMock.mockResolvedValue({ kind: "ok", status: 200, data: { operator: true, tenantAdmin: false } });

    render(
      <RolesProvider>
        <RoleProbe />
      </RolesProvider>
    );

    await waitFor(() => expect(screen.getByTestId("role-probe")).toHaveAttribute("data-status", "ready"));
    expect(screen.getByTestId("role-probe")).toHaveAttribute("data-operator", "true");
    expect(screen.getByTestId("role-probe")).toHaveAttribute("data-tenant-admin", "false");
    expect(fetchRolesMock).toHaveBeenCalledTimes(1);
    expect(fetchRolesMock).toHaveBeenCalledWith(TOKEN, expect.anything());
  });

  it("publishes 'checking' and reads nothing before the session has a token", () => {
    useAuthMock.mockReturnValue({ user: undefined });

    render(
      <RolesProvider>
        <RoleProbe />
      </RolesProvider>
    );

    expect(screen.getByTestId("role-probe")).toHaveAttribute("data-status", "checking");
    expect(fetchRolesMock).not.toHaveBeenCalled();
  });

  it("publishes 'unavailable' when the roles request fails", async () => {
    useAuthMock.mockReturnValue({ user: { access_token: TOKEN } });
    fetchRolesMock.mockResolvedValue({ kind: "failed", status: null, message: "network down" });

    render(
      <RolesProvider>
        <RoleProbe />
      </RolesProvider>
    );

    await waitFor(() => expect(screen.getByTestId("role-probe")).toHaveAttribute("data-status", "unavailable"));
    expect(screen.getByTestId("role-probe")).toHaveAttribute("data-operator", "false");
  });
});
