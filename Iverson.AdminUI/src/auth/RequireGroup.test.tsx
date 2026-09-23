import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";

const useAuthMock = vi.fn();

vi.mock("react-oidc-context", () => ({
  useAuth: () => useAuthMock(),
}));

import { RequireGroup } from "./RequireGroup";
import { normalizeGroups, hasGroup } from "./groups";

function renderGuard(groupsClaim: unknown, required: string) {
  useAuthMock.mockReturnValue({ user: { profile: { groups: groupsClaim } } });

  render(
    <RequireGroup group={required}>
      <div>Privileged content</div>
    </RequireGroup>
  );
}

describe("RequireGroup", () => {
  beforeEach(() => {
    useAuthMock.mockReset();
  });

  it("renders its children for a member of the required group", () => {
    renderGuard(["operators"], "operators");

    expect(screen.getByText("Privileged content")).toBeInTheDocument();
    expect(screen.queryByTestId("require-group-denied")).not.toBeInTheDocument();
  });

  it("refuses a signed-in user who is in no groups at all", () => {
    renderGuard([], "operators");

    expect(screen.queryByText("Privileged content")).not.toBeInTheDocument();
    expect(screen.getByTestId("require-group-denied")).toHaveTextContent(
      'Not authorized: this page requires membership in the "operators" group.'
    );
  });

  it("refuses a user whose only group merely CONTAINS the required name", () => {
    // The escalation this guard must not permit. `.includes("operators")` on the string
    // "non-operators" is true; on the LIST ["non-operators"] it is false. The guard must
    // match whole group names, so a member of a differently named group gets nothing.
    renderGuard(["non-operators"], "operators");

    expect(screen.queryByText("Privileged content")).not.toBeInTheDocument();
    expect(screen.getByTestId("require-group-denied")).toBeInTheDocument();
  });

  it("refuses a member of a group whose name merely STARTS with the required name", () => {
    renderGuard(["operators-readonly"], "operators");

    expect(screen.queryByText("Privileged content")).not.toBeInTheDocument();
  });

  it("still matches whole names if the claim arrives space-joined rather than as an array", () => {
    // Authentik's `groups` scope mapping emits a JSON array today, so this is the defensive
    // arm. It matters because if the claim ever became a bare string, a naive `.includes`
    // would silently turn into substring matching inside a security control.
    renderGuard("tenant-admins operators", "operators");

    expect(screen.getByText("Privileged content")).toBeInTheDocument();
  });

  it("refuses a substring match inside a space-joined claim string", () => {
    renderGuard("non-operators tenant-admins", "operators");

    expect(screen.queryByText("Privileged content")).not.toBeInTheDocument();
  });

  it("refuses when the groups claim is absent or is not a recognised shape", () => {
    for (const claim of [undefined, null, 42, { operators: true }]) {
      useAuthMock.mockReturnValue({ user: { profile: { groups: claim } } });
      const { unmount } = render(
        <RequireGroup group="operators">
          <div>Privileged content</div>
        </RequireGroup>
      );
      expect(screen.queryByText("Privileged content")).not.toBeInTheDocument();
      unmount();
    }
  });

  it("refuses when there is no session at all rather than throwing", () => {
    useAuthMock.mockReturnValue({});

    render(
      <RequireGroup group="operators">
        <div>Privileged content</div>
      </RequireGroup>
    );

    expect(screen.queryByText("Privileged content")).not.toBeInTheDocument();
    expect(screen.getByTestId("require-group-denied")).toBeInTheDocument();
  });
});

describe("normalizeGroups", () => {
  it("passes an array of names through, dropping non-strings and blanks", () => {
    expect(normalizeGroups(["operators", "", "  ", 7, null, " tenant-admins "])).toEqual([
      "operators",
      "tenant-admins",
    ]);
  });

  it("splits a space- or comma-delimited string into whole names", () => {
    expect(normalizeGroups("operators tenant-admins")).toEqual(["operators", "tenant-admins"]);
    expect(normalizeGroups("operators,tenant-admins")).toEqual(["operators", "tenant-admins"]);
  });

  it("yields nothing for an unrecognised claim shape, so membership fails closed", () => {
    expect(normalizeGroups(undefined)).toEqual([]);
    expect(normalizeGroups(null)).toEqual([]);
    expect(normalizeGroups(42)).toEqual([]);
    expect(normalizeGroups({ groups: ["operators"] })).toEqual([]);
  });
});

describe("hasGroup", () => {
  it("matches whole group names and never substrings", () => {
    expect(hasGroup({ groups: ["operators"] }, "operators")).toBe(true);
    expect(hasGroup({ groups: ["non-operators"] }, "operators")).toBe(false);
    expect(hasGroup({ groups: "non-operators" }, "operators")).toBe(false);
    expect(hasGroup({ groups: "operators" }, "operator")).toBe(false);
  });

  it("answers false for a missing or non-object profile instead of throwing", () => {
    expect(hasGroup(undefined, "operators")).toBe(false);
    expect(hasGroup(null, "operators")).toBe(false);
    expect(hasGroup("operators", "operators")).toBe(false);
  });
});
