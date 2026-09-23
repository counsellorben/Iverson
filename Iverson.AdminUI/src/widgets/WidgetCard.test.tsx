import { StrictMode } from "react";
import { fireEvent, render, screen } from "@testing-library/react";
import { Typography } from "@mui/material";
import { describe, it, expect, vi } from "vitest";
import type { PolledResource } from "../hooks/usePolledResource";
import { WidgetCard, noticeSeverityFor, resolveWidgetState } from "./WidgetCard";

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

function renderCard(
  over: Partial<PolledResource<Payload>>,
  options: { strict?: boolean; pollIntervalMs?: number | null } = {}
) {
  const r = resource(over);
  render(
    <WidgetCard
      title="Example"
      testId="widget-example"
      resource={r}
      pollIntervalMs={options.pollIntervalMs ?? null}
    >
      {(data) => <span data-testid="payload">{data.value}</span>}
    </WidgetCard>,
    options.strict === true ? { wrapper: StrictMode } : undefined
  );
  return r;
}

function colourOf(testId: string): string {
  return window.getComputedStyle(screen.getByTestId(testId)).color;
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

describe("noticeSeverityFor", () => {
  it("reserves error red for actual faults", () => {
    expect(noticeSeverityFor("error")).toBe("error");
  });

  it("renders an authorization answer and a configuration statement as warnings, not errors", () => {
    // A 403 is not a break, and neither is a store that was deliberately switched off.
    expect(noticeSeverityFor("forbidden")).toBe("warning");
    expect(noticeSeverityFor("unavailable")).toBe("warning");
  });

  it("renders the informational states as info", () => {
    expect(noticeSeverityFor("awaitingToken")).toBe("info");
    expect(noticeSeverityFor("unauthorized")).toBe("info");
    expect(noticeSeverityFor("empty")).toBe("info");
  });

  it("has no notice for the states that render content", () => {
    expect(noticeSeverityFor("ready")).toBeNull();
    expect(noticeSeverityFor("loading")).toBeNull();
  });
});

describe("WidgetCard", () => {
  it("says it is waiting for the session instead of spinning, at a tokenless mount", () => {
    renderCard({ loading: true, awaitingToken: true });

    expect(state()).toBe("awaitingToken");
    expect(screen.getByText("Waiting for session…")).toBeInTheDocument();
    expect(screen.queryByText("Loading…")).not.toBeInTheDocument();
  });

  it("tells the user to sign in again on a 401, and does not promise a renewal that never comes", () => {
    renderCard({ failure: { kind: "unauthorized", status: 401 } });

    expect(state()).toBe("unauthorized");
    const notice = screen.getByTestId("widget-example-notice");
    expect(notice).toHaveTextContent("Session no longer accepted — sign in again.");
    expect(notice).not.toHaveTextContent(/renew/i);
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

    // The promise is VISUAL, so the assertion has to be visual. Identical wording rendered in
    // error red is still "a generic error card" to anyone reading the page at a glance, and a
    // text-only assertion cannot tell the two apart.
    const notice = screen.getByTestId("widget-example-notice");
    expect(notice).toHaveClass("MuiAlert-colorWarning");
    expect(notice).not.toHaveClass("MuiAlert-colorError");
  });

  it("renders a real fault in error red, so warning still means something", () => {
    renderCard({ failure: { kind: "failed", status: null, message: "network down" } });

    const notice = screen.getByTestId("widget-example-notice");
    expect(notice).toHaveClass("MuiAlert-colorError");
    expect(notice).not.toHaveClass("MuiAlert-colorWarning");
  });

  it("renders a 503 configuration statement as a warning, not as a fault", () => {
    render(
      <WidgetCard
        title="Example"
        testId="widget-example"
        resource={resource({
          failure: { kind: "problem", status: 503, reason: "disabled", error: null, body: {} },
        })}
      >
        {(data) => <span>{data.value}</span>}
      </WidgetCard>
    );

    const notice = screen.getByTestId("widget-example-notice");
    expect(notice).toHaveClass("MuiAlert-colorWarning");
    expect(notice).not.toHaveClass("MuiAlert-colorError");
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
    const asOf = screen.getByTestId("widget-example-as-of");
    expect(asOf).toHaveAttribute("data-stale", "true");
    expect(asOf).toHaveTextContent("Showing data as of 09:41 local time");
  });

  it("shows the as-of line on a HEALTHY resource too, so freshness is never invisible", () => {
    renderCard({ data: { value: "fresh" }, asOf: "09:41" });

    expect(state()).toBe("ready");
    const asOf = screen.getByTestId("widget-example-as-of");
    expect(asOf).toHaveAttribute("data-stale", "false");
    expect(asOf).toHaveTextContent("Updated 09:41 local time");
  });

  it("labels the as-of clock as local, because the instants in the payload are UTC", () => {
    renderCard({ data: { value: "fresh" }, asOf: "09:41" });

    expect(screen.getByTestId("widget-example-as-of")).toHaveTextContent(/local time/);
  });

  it("says polling is paused only for a widget that actually polls", () => {
    renderCard({ data: { value: "x" }, paused: true }, { pollIntervalMs: 60_000 });
    expect(screen.getByTestId("widget-example-paused")).toHaveTextContent(
      "Polling is paused while this tab is hidden."
    );
  });

  it("does not claim a paused poll on a widget that never polls", () => {
    renderCard({ data: { value: "x" }, paused: true }, { pollIntervalMs: null });
    expect(screen.queryByTestId("widget-example-paused")).not.toBeInTheDocument();
  });

  it("says so when backoff has given up, and points at the button it left enabled", () => {
    renderCard({ failure: { kind: "failed", status: null, message: "boom" }, exhausted: true });

    expect(screen.getByTestId("widget-example-exhausted")).toHaveTextContent(
      "Automatic retries have stopped. Use Refresh to try again."
    );
    expect(screen.getByRole("button", { name: "Refresh Example" })).toBeEnabled();
  });

  it("does NOT tell the user to press Refresh in the state where Refresh is disabled", () => {
    // `exhausted && awaitingToken`: the button is disabled and refresh() would no-op anyway.
    renderCard({ exhausted: true, awaitingToken: true, loading: true });

    const caption = screen.getByTestId("widget-example-exhausted");
    expect(caption).toHaveTextContent(
      "Automatic retries have stopped. They resume on their own once the session is back."
    );
    expect(caption).not.toHaveTextContent("Use Refresh");
    expect(screen.getByRole("button", { name: "Refresh Example" })).toBeDisabled();
  });

  it("renders the retries-have-stopped caption as a caution, not as a third grey footnote", () => {
    // This caption says the card has stopped updating itself and only a human pressing
    // Refresh will change that. It renders directly beneath the as-of caption and, when
    // polling, the paused one — both secondary text — so in ordinary body colour it reads as
    // one more footnote instead of as the one line on the card asking for an action.
    //
    // This is also the site that proved MUI v9's `color` prop is inert: `color="warning.main"`
    // here rendered no colour whatsoever, with no type error, from the upgrade until now.
    render(
      <Typography sx={{ color: "warning.main" }} data-testid="reference-warning">
        reference
      </Typography>
    );
    renderCard({
      failure: { kind: "failed", status: null, message: "boom" },
      exhausted: true,
      data: { value: "x" },
      lastUpdatedAt: 1_700_000_000_000,
      asOf: "12:34",
      stale: true,
    });

    const exhausted = colourOf("widget-example-exhausted");
    expect(exhausted).not.toBe("");
    // Positive, and theme-independent: the same colour a `warning.main` reference resolves to,
    // rather than a hard-coded rgb that a palette change would falsify for the wrong reason.
    expect(exhausted).toBe(colourOf("reference-warning"));
    // Negative: distinct from the secondary caption it sits directly beneath.
    expect(exhausted).not.toBe(colourOf("widget-example-as-of"));
  });

  it("renders under StrictMode", () => {
    renderCard({ data: { value: "strict" } }, { strict: true });

    expect(state()).toBe("ready");
    expect(screen.getByTestId("payload")).toHaveTextContent("strict");
  });
});
