import { render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import { MetricStat } from "./MetricStat";
import { NO_SAMPLE_CAPTION, NO_SAMPLE_VALUE, formatRate } from "./format";

/**
 * `MetricStat` is the single place the console decides what an ABSENT figure looks like, so
 * its two promises are pinned here directly rather than only through the widgets that use it.
 */

function colourOf(testId: string): string {
  return window.getComputedStyle(screen.getByTestId(testId)).color;
}

describe("MetricStat", () => {
  it("renders an absent figure as words and a measured one as a number", () => {
    render(
      <>
        <MetricStat label="Absent" value={null} format={formatRate} testId="absent" />
        <MetricStat label="Zero" value={0} format={formatRate} testId="zero" />
      </>
    );

    expect(screen.getByTestId("absent-value")).toHaveTextContent(NO_SAMPLE_VALUE);
    expect(screen.getByTestId("absent-value").textContent ?? "").not.toMatch(/\d/);
    expect(screen.getByTestId("absent")).toHaveTextContent(NO_SAMPLE_CAPTION);
    expect(screen.getByTestId("absent")).toHaveAttribute("data-metric-null", "true");

    expect(screen.getByTestId("zero-value")).toHaveTextContent("0.00 /s");
    expect(screen.getByTestId("zero")).not.toHaveTextContent(NO_SAMPLE_CAPTION);
    expect(screen.getByTestId("zero")).toHaveAttribute("data-metric-null", "false");
  });

  it("does not give an absent figure the emphasis of a real one", () => {
    // The claim in the source is that an absent figure is greyed rather than emphasised, so
    // it does not read as a headline number. Asserting the COMPUTED colour is what makes that
    // a promise rather than a comment — and it is what proved the obvious spelling of it,
    // Typography's `color` prop, is inert under MUI v9: `color="text.secondary"` and
    // `color="text.primary"` produce the identical emotion class and no colour at all, with
    // no type error to notice. Only `sx` applies.
    render(
      <>
        <MetricStat label="Absent" value={null} format={formatRate} testId="absent" />
        <MetricStat label="Present" value={42} format={formatRate} testId="present" />
      </>
    );

    const absent = colourOf("absent-value");
    const present = colourOf("present-value");
    expect(absent).not.toBe("");
    expect(present).not.toBe("");
    expect(absent).not.toBe(present);
    // The absent figure is rendered in the same de-emphasised colour as its own caption,
    // rather than in some third colour that would read as a status of its own.
    expect(absent).toBe(window.getComputedStyle(screen.getByTestId("absent-caption")).color);
  });
});
