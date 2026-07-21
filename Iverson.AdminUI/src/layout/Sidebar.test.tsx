import { render, screen } from "@testing-library/react";
import { BrowserRouter } from "react-router-dom";
import { describe, it, expect, vi } from "vitest";

const useAuthMock = vi.fn();

vi.mock("react-oidc-context", () => ({
  useAuth: () => useAuthMock(),
}));

import { Sidebar } from "./Sidebar";

function renderSidebar(groups: string[]) {
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
});
