import { renderHook } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

// This tsconfig carries no Node types; vitest runs on Node, where this global exists.
declare const process: {
  on(event: "unhandledRejection", listener: (reason: unknown) => void): void;
  off(event: "unhandledRejection", listener: (reason: unknown) => void): void;
};

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

  describe("when removeUser fails", () => {
    afterEach(() => {
      vi.restoreAllMocks();
    });

    it("logs the failure and does not leave an unhandled rejection", async () => {
      // AppLayout.test.tsx's shape, with two changes that keep it from passing vacuously: under
      // vitest + jsdom an unhandled rejection reaches `process`, never `window`, and a vi.fn
      // attaches its own settle handlers to a promise it returns, which marks it handled.
      const error = vi.spyOn(console, "error").mockImplementation(() => {});
      const unhandled = vi.fn();
      process.on("unhandledRejection", unhandled);

      const failure = new Error("storage unavailable");
      let removeUserCalls = 0;
      const removeUser = () => {
        removeUserCalls += 1;
        return Promise.reject(failure);
      };
      let onExpired: (() => unknown) | undefined;
      useAuthMock.mockReturnValue({
        removeUser,
        events: {
          addAccessTokenExpired: (callback: () => unknown) => {
            onExpired = callback;
            return () => {};
          },
        },
      });

      try {
        renderHook(() => useSessionExpiry());
        onExpired?.();
        // One macrotask: Node reports an unhandled rejection once the microtask queue drains.
        await new Promise((resolve) => setTimeout(resolve, 0));

        expect(removeUserCalls).toBe(1);
        expect(unhandled).not.toHaveBeenCalled();
        expect(error).toHaveBeenCalledWith(expect.any(String), failure);
      } finally {
        process.off("unhandledRejection", unhandled);
      }
    });
  });
});
