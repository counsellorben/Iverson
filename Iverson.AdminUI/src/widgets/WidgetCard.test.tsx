import { StrictMode } from "react";
import { fireEvent, render, screen } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import type { PolledResource } from "../hooks/usePolledResource";
import { WidgetCard, resolveWidgetState } from "./WidgetCard";

interface Payload {
  value: string;
}

function resource(over: Partial<PolledResource<Payload>> = {}): PolledResource<Payload> {
  return {
    data: null,
    lastUpdatedAt: null,
    asOf: null,
    stale: false,
    loading: false,
    failure: null,
    exhausted: false,
    paused: false,
    awaitingToken: false,
    refresh: vi.fn(),
    ...over,
  };
}

function renderCard(over: Partial<PolledResource<Payload>>, strict = false) {
  const r = resource(over);
  render(
    <WidgetCard title="Example" testId="widget-example" resource={r}>
      {(data) => <span data-testid="payload">{data.value}</span>}
    </WidgetCard>,
    strict ? { wrapper: StrictMode } : undefined
  );
  return r;
}

function state(): string | null {
  return screen.getByTestId("widget-example").getAttribute("data-state");
}

describe("resolveWidgetState", () => {
  it("reports awaitingToken, NOT loading, when a tokenless mount sets both flags", () => {
    // This is the real shape of a first mount before the session resolves: the hook sets
    // loading AND awaitingToken. Branching on loading first would show a spinner forever.
    expect(resolveWidgetState({ data: null, loading: true, failure: null, awaitingToken: true }))
      .toBe("awaitingToken");
  });

  it("reports loading only once a token is present", () => {
    expect(resolveWidgetState({ data: null, loading: true, failure: null, awaitingToken: false }))
      .toBe("loading");
  });

  it.each([
    [{ kind: "forbidden", status: 403, error: null } as const, "forbidden"],
    [{ kind: "unauthorized", status: 401 } as const, "unauthorized"],
    [
      { kind: "problem", status: 503, reason: "disabled", error: null, body: {} } as const,
      "unavailable",
    ],
    [{ kind: "failed", status: null, message: "network down" } as const, "error"],
  ])("maps failure kind %o to its own state", (failure, expected) => {
    expect(resolveWidgetState({ data: null, loading: false, failure, awaitingToken: false }))
      .toBe(expected);
  });

  it("reports ready with data and empty without", () => {
    expect(
      resolveWidgetState({
        data: { value: "x" },
        loading: false,
        failure: null,
        awaitingToken: false,
      })
    ).toBe("ready");
    expect(resolveWidgetState({ data: null, loading: false, failure: null, awaitingToken: false }))
      .toBe("empty");
  });
});

describe("WidgetCard", () => {
  it("says it is waiting for the session instead of spinning, at a tokenless mount", () => {
    renderCard({ loading: true, awaitingToken: true });

    expect(state()).toBe("awaitingToken");
    expect(screen.getByText("Waiting for session…")).toBeInTheDocument();
    expect(screen.queryByText("Loading…")).not.toBeInTheDocument();
  });

  it("disables Refresh while awaiting a token, because refresh() is inert there", () => {
    renderCard({ loading: true, awaitingToken: true, exhausted: true });

    expect(screen.getByRole("button", { name: "Refresh Example" })).toBeDisabled();
  });

  it("enables Refresh and calls it once a token is present", () => {
    const r = renderCard({ failure: { kind: "failed", status: null, message: "boom" }, exhausted: true });
    const button = screen.getByRole("button", { name: "Refresh Example" });

    expect(button).toBeEnabled();
    fireEvent.click(button);

    expect(r.refresh).toHaveBeenCalledTimes(1);
  });

  it("renders a 403 as an authorization answer, not as a failure", () => {
    render(
      <WidgetCard
        title="Example"
        testId="widget-example"
        resource={resource({ failure: { kind: "forbidden", status: 403, error: null } })}
        forbiddenMessage="Not authorized: needs the Operator role."
      >
        {(data) => <span>{data.value}</span>}
      </WidgetCard>
    );

    expect(state()).toBe("forbidden");
    expect(screen.getByText("Not authorized: needs the Operator role.")).toBeInTheDocument();
    expect(screen.queryByText(/Could not load/)).not.toBeInTheDocument();
  });

  it("renders a problem body's reason through the widget's own wording", () => {
    render(
      <WidgetCard
        title="Example"
        testId="widget-example"
        resource={resource({
          failure: { kind: "problem", status: 503, reason: "disabled", error: null, body: {} },
        })}
        reasonText={(reason) => `reason was ${reason}`}
      >
        {(data) => <span>{data.value}</span>}
      </WidgetCard>
    );

    expect(state()).toBe("unavailable");
    expect(screen.getByText("reason was disabled")).toBeInTheDocument();
  });

  it("keeps the last good payload on screen when a poll fails, with an as-of line", () => {
    renderCard({
      data: { value: "retained" },
      stale: true,
      asOf: "09:41",
      failure: { kind: "failed", status: null, message: "network down" },
    });

    expect(state()).toBe("error");
    expect(screen.getByTestId("payload")).toHaveTextContent("retained");
    expect(screen.getByTestId("widget-example-stale")).toHaveTextContent("Showing data as of 09:41");
  });

  it("says so when backoff has given up", () => {
    renderCard({ failure: { kind: "failed", status: null, message: "boom" }, exhausted: true });

    expect(screen.getByTestId("widget-example-exhausted")).toHaveTextContent(
      "Automatic retries have stopped."
    );
  });

  it("renders under StrictMode", () => {
    renderCard({ data: { value: "strict" } }, true);

    expect(state()).toBe("ready");
    expect(screen.getByTestId("payload")).toHaveTextContent("strict");
  });
});
