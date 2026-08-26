import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";

const signinRedirect = vi.fn();
const useAuthMock = vi.fn();

let capturedProviderProps: Record<string, unknown> | null = null;

vi.mock("react-oidc-context", () => ({
  useAuth: () => useAuthMock(),
  // Captures the settings `AuthProvider` hands to react-oidc-context. These settings ARE the
  // security behaviour — there is no other code path that revokes a token or scrubs the
  // authorization code out of the URL — so the tests below assert on what gets passed, not on
  // a helper that something might have forgotten to wire up.
  AuthProvider: (props: { children: React.ReactNode }) => {
    capturedProviderProps = props as unknown as Record<string, unknown>;
    return <>{props.children}</>;
  },
}));

import { AuthGate, AuthProvider, onSigninCallback } from "./AuthProvider";
import { requestTokenRenewal, setTokenRenewer } from "../api/client";

function renderProviderAndCaptureSettings(): Record<string, unknown> {
  capturedProviderProps = null;
  render(
    <AuthProvider>
      <div>App</div>
    </AuthProvider>
  );
  if (capturedProviderProps === null) {
    throw new Error("AuthProvider did not render the OIDC provider");
  }
  return capturedProviderProps;
}

describe("AuthGate", () => {
  beforeEach(() => {
    signinRedirect.mockClear();
    useAuthMock.mockReset();
  });

  it("redirects an unauthenticated visitor into the login flow instead of rendering children", () => {
    useAuthMock.mockReturnValue({
      isLoading: false,
      isAuthenticated: false,
      signinRedirect,
    });

    render(
      <AuthGate>
        <div>Protected content</div>
      </AuthGate>
    );

    expect(screen.queryByText("Protected content")).not.toBeInTheDocument();
    expect(signinRedirect).toHaveBeenCalledTimes(1);
  });

  it("renders children once authenticated", () => {
    useAuthMock.mockReturnValue({
      isLoading: false,
      isAuthenticated: true,
      signinRedirect,
    });

    render(
      <AuthGate>
        <div>Protected content</div>
      </AuthGate>
    );

    expect(screen.getByText("Protected content")).toBeInTheDocument();
    expect(signinRedirect).not.toHaveBeenCalled();
  });

  it("does not redirect while the auth state is still loading", () => {
    useAuthMock.mockReturnValue({
      isLoading: true,
      isAuthenticated: false,
      signinRedirect,
    });

    render(
      <AuthGate>
        <div>Protected content</div>
      </AuthGate>
    );

    expect(screen.queryByText("Protected content")).not.toBeInTheDocument();
    expect(signinRedirect).not.toHaveBeenCalled();
  });

  it("bridges the console's fetch layer to silent renewal, so one 401 renews once", async () => {
    const signinSilent = vi.fn(async () => undefined);
    useAuthMock.mockReturnValue({
      isLoading: false,
      isAuthenticated: true,
      signinRedirect,
      signinSilent,
    });
    setTokenRenewer(null);

    render(
      <AuthGate>
        <div>Protected content</div>
      </AuthGate>
    );

    // AuthGate is the single place this wiring lives. Without it the fetch layer's renewal
    // seam is registered by nobody, and every 401 on the landing page becomes nine dead
    // widgets instead of one silent renewal.
    requestTokenRenewal();
    await Promise.resolve();

    expect(signinSilent).toHaveBeenCalledTimes(1);
  });
});

describe("AuthProvider OIDC settings", () => {
  it("asks the IdP to revoke the tokens at signout, not merely to forget them locally", () => {
    // Without this, signing out clears sessionStorage but leaves a refresh token that stays
    // valid at Authentik for its full lifetime — a logged-out session that can still mint
    // access tokens if the storage were ever recovered.
    expect(renderProviderAndCaptureSettings().revokeTokensOnSignout).toBe(true);
  });

  it("keeps offline_access in the requested scope", () => {
    // The landing page polls for hours. Drop the refresh token and automaticSilentRenew falls
    // back to iframe silent renew, which this console has no handler for: the session simply
    // dies mid-session.
    const scope = renderProviderAndCaptureSettings().scope as string;
    expect(scope.split(" ")).toContain("offline_access");
    expect(scope.split(" ")).toContain("groups");
  });

  it("wires a signin callback that strips the authorization code out of the URL", () => {
    const settings = renderProviderAndCaptureSettings();
    window.history.replaceState({}, "", "/callback?code=secret-auth-code&state=abc123");

    (settings.onSigninCallback as () => void)();

    expect(window.location.search).toBe("");
    expect(window.location.href).not.toContain("secret-auth-code");
    expect(window.location.pathname).toBe("/callback");
  });

  it("leaves no history entry that can restore the code with the back button", () => {
    window.history.replaceState({}, "", "/callback?code=secret-auth-code&state=abc123");
    const depthBefore = window.history.length;

    onSigninCallback();

    // replaceState, not pushState: the callback URL must not be revisitable.
    expect(window.history.length).toBe(depthBefore);
    expect(window.location.search).toBe("");
  });
});
