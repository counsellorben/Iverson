import { renderHook } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";

const useAuthMock = vi.fn();
vi.mock("react-oidc-context", () => ({ useAuth: () => useAuthMock() }));

import { useSessionExpiry } from "./useSessionExpiry";

describe("useSessionExpiry", () => {
  beforeEach(() => useAuthMock.mockReset());

  it("unsubscribes from the expiry event on unmount", () => {
    const unsubscribe = vi.fn();
    useAuthMock.mockReturnValue({
      removeUser: vi.fn(),
      events: { addAccessTokenExpired: () => unsubscribe },
    });

    const { unmount } = renderHook(() => useSessionExpiry());
    unmount();

    expect(unsubscribe).toHaveBeenCalledTimes(1);
  });

  it("does nothing when the auth context carries no events", () => {
    useAuthMock.mockReturnValue({ removeUser: vi.fn() });

    expect(() => renderHook(() => useSessionExpiry())).not.toThrow();
  });
});
