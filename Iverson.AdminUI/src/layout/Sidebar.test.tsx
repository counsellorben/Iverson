import { render, screen } from "@testing-library/react";
import { BrowserRouter } from "react-router";
import { describe, it, expect } from "vitest";
import { ConsoleRolesContext } from "../auth/roles";
import type { ConsoleRoleState } from "../auth/roles";
import { Sidebar } from "./Sidebar";

function renderSidebar(state: ConsoleRoleState) {
  return render(
    <ConsoleRolesContext.Provider value={state}>
      <BrowserRouter>
        <Sidebar />
      </BrowserRouter>
    </ConsoleRolesContext.Provider>
  );
}

function ready(operator: boolean, tenantAdmin: boolean): ConsoleRoleState {
  return { status: "ready", roles: { operator, tenantAdmin } };
}

const CHECKING: ConsoleRoleState = { status: "checking" };
const UNAVAILABLE: ConsoleRoleState = {
  status: "unavailable",
  failure: { kind: "failed", status: null, message: "network down" },
};

describe("Sidebar", () => {
  it("offers Tenants, and not Tenant Admin, to an operator", () => {
    renderSidebar(ready(true, false));

    expect(screen.getByRole("link", { name: "Tenants" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Tenant Admin" })).not.toBeInTheDocument();
  });

  it("offers Tenant Admin, and not Tenants, to a tenant admin", () => {
    renderSidebar(ready(false, true));

    expect(screen.queryByRole("link", { name: "Tenants" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Tenant Admin" })).toBeInTheDocument();
  });

  it("offers both to a caller with both roles", () => {
    renderSidebar(ready(true, true));

    expect(screen.getByRole("link", { name: "Tenants" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Tenant Admin" })).toBeInTheDocument();
  });

  it("offers neither to a signed-in user with neither role", () => {
    renderSidebar(ready(false, false));

    expect(screen.queryByRole("link", { name: "Tenants" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Tenant Admin" })).not.toBeInTheDocument();
  });

  it("offers no privileged link while the roles are being read, or when they could not be read", () => {
    for (const state of [CHECKING, UNAVAILABLE]) {
      const { unmount } = renderSidebar(state);
      expect(screen.queryByRole("link", { name: "Tenants" })).not.toBeInTheDocument();
      expect(screen.queryByRole("link", { name: "Tenant Admin" })).not.toBeInTheDocument();
      unmount();
    }
  });

  it("no longer links the removed Performance and Storage pages", () => {
    renderSidebar(ready(true, true));

    expect(screen.queryByRole("link", { name: "Performance" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Storage" })).not.toBeInTheDocument();
  });
});
