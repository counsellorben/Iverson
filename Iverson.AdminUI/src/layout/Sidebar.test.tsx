import { render, screen } from "@testing-library/react";
import { BrowserRouter } from "react-router";
import { describe, it, expect, vi } from "vitest";

const useAuthMock = vi.fn();

vi.mock("react-oidc-context", () => ({
  useAuth: () => useAuthMock(),
}));

import { Sidebar } from "./Sidebar";

function renderSidebar(groups: string[]) {
  renderSidebarWithClaim(groups);
}

/**
 * Widened to `unknown` because the `groups` claim is not typed as an array anywhere — an OIDC
 * profile claim is `unknown`, and the tests below need to feed the sidebar shapes other than a
 * string array to pin how it reads membership.
 */
function renderSidebarWithClaim(groups: unknown) {
  useAuthMock.mockReturnValue({
    user: {
      profile: {
        groups,
      },
    },
  });

  render(
    <BrowserRouter>
      <Sidebar />
    </BrowserRouter>
  );
}

describe("Sidebar", () => {
  it("renders Performance and Storage links unconditionally (operator only)", () => {
    renderSidebar(["operators"]);

    expect(screen.getByRole("link", { name: "Performance" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Storage" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Tenants" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Tenant Admin" })).not.toBeInTheDocument();
  });

  it("renders Performance and Storage links unconditionally (tenant-admin only)", () => {
    renderSidebar(["tenant-admins"]);

    expect(screen.getByRole("link", { name: "Performance" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Storage" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Tenants" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Tenant Admin" })).toBeInTheDocument();
  });

  it("renders all links when user has both roles", () => {
    renderSidebar(["operators", "tenant-admins"]);

    expect(screen.getByRole("link", { name: "Performance" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Storage" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Tenants" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Tenant Admin" })).toBeInTheDocument();
  });

  it("renders only Performance and Storage links when user has neither role", () => {
    renderSidebar([]);

    expect(screen.getByRole("link", { name: "Performance" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Storage" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Tenants" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Tenant Admin" })).not.toBeInTheDocument();
  });

  it("does not offer Tenants to a member of a group that merely CONTAINS 'operators'", () => {
    // The sidebar shares its membership check with the RequireGroup route guard, so this is
    // the same escalation pinned in both places: substring matching would show a nav item to a
    // member of `non-operators`. A hidden link and a blocked route must agree.
    renderSidebar(["non-operators", "non-tenant-admins"]);

    expect(screen.queryByRole("link", { name: "Tenants" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Tenant Admin" })).not.toBeInTheDocument();
  });

  it("reads whole names out of a space-joined claim string, not substrings of it", () => {
    renderSidebarWithClaim("non-operators tenant-admins");

    expect(screen.queryByRole("link", { name: "Tenants" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Tenant Admin" })).toBeInTheDocument();
  });

  it("offers no privileged link when the groups claim is missing entirely", () => {
    renderSidebarWithClaim(undefined);

    expect(screen.getByRole("link", { name: "Performance" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Tenants" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Tenant Admin" })).not.toBeInTheDocument();
  });
});
