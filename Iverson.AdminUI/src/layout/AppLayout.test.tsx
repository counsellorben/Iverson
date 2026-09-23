import { render, screen, waitFor } from "@testing-library/react";
import { BrowserRouter } from "react-router";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

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

  it("still clears the local session when revocation at the IdP fails", async () => {
    // `AuthProvider` does not set `revokeTokensOnSignout`, so signout revokes nothing — but it
    // still makes a cross-origin call to Authentik (the end-session request needs the IdP's
    // metadata) and rethrows on failure, for reasons unrelated to this console. The rejection
    // below stands in for any such failure. Without the fallback, Logout would be a dead button
    // with an unhandled rejection; with it, the local session is cleared regardless.
    const signoutRedirect = vi.fn(async () => {
      throw new Error("revocation failed: CORS");
    });
    const removeUser = vi.fn(async () => undefined);
    renderLayout({ signoutRedirect, removeUser });

    screen.getByRole("button", { name: "Logout" }).click();

    await waitFor(() => expect(removeUser).toHaveBeenCalledTimes(1));
    expect(signoutRedirect).toHaveBeenCalledTimes(1);
  });

  it("does not leave an unhandled rejection when the local fallback fails too", async () => {
    const unhandled = vi.fn();
    window.addEventListener("unhandledrejection", unhandled);

    const signoutRedirect = vi.fn(async () => {
      throw new Error("revocation failed");
    });
    const removeUser = vi.fn(async () => {
      throw new Error("storage unavailable");
    });
    renderLayout({ signoutRedirect, removeUser });

    screen.getByRole("button", { name: "Logout" }).click();

    await waitFor(() => expect(removeUser).toHaveBeenCalledTimes(1));
    await Promise.resolve();
    expect(unhandled).not.toHaveBeenCalled();

    window.removeEventListener("unhandledrejection", unhandled);
  });
});
