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

import { AuthGate, AuthProvider } from "./AuthProvider";

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
