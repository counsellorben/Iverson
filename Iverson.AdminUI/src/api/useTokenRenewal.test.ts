import { renderHook } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

const useAuthMock = vi.fn();
vi.mock("react-oidc-context", () => ({
  useAuth: () => useAuthMock(),
}));

import { useTokenRenewal } from "./useTokenRenewal";
import { requestTokenRenewal, setTokenRenewer } from "./client";

describe("useTokenRenewal", () => {
  beforeEach(() => {
    useAuthMock.mockReset();
    setTokenRenewer(null);
  });

  afterEach(() => {
    setTokenRenewer(null);
  });

  it("registers signinSilent so that a 401 anywhere in the console renews the session", async () => {
    const signinSilent = vi.fn(async () => undefined);
    useAuthMock.mockReturnValue({ signinSilent });

    renderHook(() => useTokenRenewal());

    // Asserted through the fetch layer's own entry point rather than by spying on the
    // registration: what matters is that a 401 reaches oidc-client-ts, not that a setter ran.
    requestTokenRenewal();
    await Promise.resolve();

    expect(signinSilent).toHaveBeenCalledTimes(1);
  });

  it("unregisters on unmount, so a torn-down bridge cannot be called", async () => {
    const signinSilent = vi.fn(async () => undefined);
    useAuthMock.mockReturnValue({ signinSilent });

    const { unmount } = renderHook(() => useTokenRenewal());
    unmount();

    requestTokenRenewal();
    await Promise.resolve();

    expect(signinSilent).not.toHaveBeenCalled();
  });

  it("tolerates an auth context that has no signinSilent yet", async () => {
    useAuthMock.mockReturnValue({});

    expect(() => renderHook(() => useTokenRenewal())).not.toThrow();

    // No renewer registered, and asking for one is a no-op rather than a crash.
    expect(() => requestTokenRenewal()).not.toThrow();
  });
});
