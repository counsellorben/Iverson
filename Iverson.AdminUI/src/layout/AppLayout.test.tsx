import { render, screen, waitFor } from "@testing-library/react";
import { BrowserRouter } from "react-router";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

// This tsconfig carries no Node types; vitest runs on Node, where this global exists.
declare const process: {
  on(event: "unhandledRejection", listener: (reason: unknown) => void): void;
  off(event: "unhandledRejection", listener: (reason: unknown) => void): void;
};

const useAuthMock = vi.fn();

vi.mock("react-oidc-context", () => ({
  useAuth: () => useAuthMock(),
}));

import { AppLayout } from "./AppLayout";

function renderLayout(auth: Record<string, unknown>) {
  useAuthMock.mockReturnValue({
    user: { profile: { email: "operator@example.com", groups: [] } },
    ...auth,
  });

  render(
    <BrowserRouter>
      <AppLayout />
    </BrowserRouter>
  );
}

describe("AppLayout logout", () => {
  beforeEach(() => {
    useAuthMock.mockReset();
    vi.spyOn(console, "warn").mockImplementation(() => {});
    vi.spyOn(console, "error").mockImplementation(() => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("signs out through the IdP on the happy path, without clearing the session locally", async () => {
    const signoutRedirect = vi.fn(async () => undefined);
    const removeUser = vi.fn(async () => undefined);
    renderLayout({ signoutRedirect, removeUser });

    screen.getByRole("button", { name: "Logout" }).click();

    await waitFor(() => expect(signoutRedirect).toHaveBeenCalledTimes(1));
    // The IdP round-trip succeeded, so oidc-client-ts has already removed the user itself.
    expect(removeUser).not.toHaveBeenCalled();
  });

  it("still clears the local session when the IdP round-trip fails", async () => {
    // `AuthProvider` does not set `revokeTokensOnSignout`, so signout revokes nothing — but
    // oidc-client-ts's `_signoutStart` still removes the user locally FIRST, then makes a
    // cross-origin call to Authentik to build the end-session request, and rethrows if that
    // call fails, for reasons unrelated to this console. The rejection below stands in for any
    // such failure. By the time it fires, oidc-client-ts has already removed the user locally,
    // so the fallback below is a floor, not what clears the session in this scenario — but
    // without it, the rejection would go unhandled.
    const signoutRedirect = vi.fn(async () => {
      throw new Error("end-session request failed: CORS");
    });
    const removeUser = vi.fn(async () => undefined);
    renderLayout({ signoutRedirect, removeUser });

    screen.getByRole("button", { name: "Logout" }).click();

    await waitFor(() => expect(removeUser).toHaveBeenCalledTimes(1));
    expect(signoutRedirect).toHaveBeenCalledTimes(1);
  });

  it("does not leave an unhandled rejection when the local fallback fails too", async () => {
    // `window`'s "unhandledrejection" never fires under vitest + jsdom — an unhandled rejection
    // reaches `process` instead — and a vi.fn-wrapped async function attaches its own settle
    // handlers to the promise it returns, which marks the rejection handled regardless of
    // whether AppLayout catches it. Both would make this test pass vacuously; see
    // useSessionExpiry.test.tsx, which uses the same fix for the same reason.
    const unhandled = vi.fn();
    process.on("unhandledRejection", unhandled);

    const signoutRedirect = vi.fn(async () => {
      throw new Error("revocation failed");
    });
    const failure = new Error("storage unavailable");
    let removeUserCalls = 0;
    const removeUser = () => {
      removeUserCalls += 1;
      return Promise.reject(failure);
    };
    renderLayout({ signoutRedirect, removeUser });

    try {
      screen.getByRole("button", { name: "Logout" }).click();

      await waitFor(() => expect(removeUserCalls).toBe(1));
      // One macrotask: Node reports an unhandled rejection once the microtask queue drains.
      await new Promise((resolve) => setTimeout(resolve, 0));
      expect(unhandled).not.toHaveBeenCalled();
    } finally {
      process.off("unhandledRejection", unhandled);
    }
  });
});
