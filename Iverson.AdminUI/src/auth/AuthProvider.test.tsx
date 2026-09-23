import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";

const signinRedirect = vi.fn();
const useAuthMock = vi.fn();
const capturedOidcProps: Record<string, unknown>[] = [];

vi.mock("react-oidc-context", () => ({
  useAuth: () => useAuthMock(),
  AuthProvider: (props: { children?: React.ReactNode }) => {
    capturedOidcProps.push(props);
    return props.children;
  },
}));

import { AuthGate, AuthProvider, onSigninCallback } from "./AuthProvider";

function renderProviderAndCaptureSettings(): Record<string, unknown> {
  capturedOidcProps.length = 0;
  render(
    <AuthProvider>
      <div>App</div>
    </AuthProvider>
  );
  if (capturedOidcProps.length === 0) {
    throw new Error("AuthProvider did not render the OIDC provider");
  }
  return capturedOidcProps[0];
}

describe("AuthProvider", () => {
  beforeEach(() => {
    capturedOidcProps.length = 0;
  });

  it("does not request the offline_access scope, so no refresh token is ever issued or stored", () => {
    render(
      <AuthProvider>
        <div>child</div>
      </AuthProvider>
    );

    expect(capturedOidcProps).toHaveLength(1);
    const scope = capturedOidcProps[0].scope as string;
    expect(scope.split(" ")).not.toContain("offline_access");
  });

  it("disables automaticSilentRenew, since a hidden-iframe renewal would be blocked by the CSP anyway", () => {
    render(
      <AuthProvider>
        <div>child</div>
      </AuthProvider>
    );

    expect(capturedOidcProps[0].automaticSilentRenew).toBe(false);
  });
});

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
});

describe("AuthProvider OIDC settings", () => {
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
