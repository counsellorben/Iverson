import { describe, it, expect } from "vitest";
import {
  formatDecimal,
  formatDuration,
  formatInstant,
  formatInteger,
  formatPercentage,
  formatRate,
  pluralise,
} from "./format";

describe("formatInteger", () => {
  it("groups in threes without touching the runner's locale", () => {
    expect(formatInteger(0)).toBe("0");
    expect(formatInteger(999)).toBe("999");
    expect(formatInteger(1000)).toBe("1,000");
    expect(formatInteger(1234567)).toBe("1,234,567");
    expect(formatInteger(-1234)).toBe("-1,234");
  });
});

describe("pluralise", () => {
  it("reads correctly at one", () => {
    expect(pluralise(1, "type")).toBe("type");
    expect(pluralise(0, "type")).toBe("types");
    expect(pluralise(2, "type")).toBe("types");
  });
});

describe("formatInstant", () => {
  it("names the zone, so it cannot be mistaken for the card's local as-of clock", () => {
    expect(formatInstant("2026-01-02T03:04:05.678Z")).toBe("2026-01-02 03:04 UTC");
    expect(formatInstant("2026-02-03T04:05:06Z")).toBe("2026-02-03 04:05 UTC");
    expect(formatInstant("2026-02-03T04:05:06+00:00")).toBe("2026-02-03 04:05 UTC");
  });

  it("carries a non-UTC offset through rather than silently claiming UTC", () => {
    expect(formatInstant("2026-02-03T04:05:06+02:00")).toBe("2026-02-03 04:05 +02:00");
  });

  it("labels nothing when the value states no offset, rather than inventing one", () => {
    expect(formatInstant("2026-02-03T04:05:06")).toBe("2026-02-03 04:05");
  });

  it("returns anything unrecognisable untouched rather than mangling it", () => {
    expect(formatInstant("not a date")).toBe("not a date");
    expect(formatInstant("")).toBe("");
  });
});

describe("formatDecimal", () => {
  it("rounds to a fixed number of places and groups the whole part", () => {
    expect(formatDecimal(0, 2)).toBe("0.00");
    expect(formatDecimal(1.005, 1)).toBe("1.0");
    expect(formatDecimal(1234.5, 2)).toBe("1,234.50");
    expect(formatDecimal(-1234.567, 1)).toBe("-1,234.6");
    expect(formatDecimal(1234.5, 0)).toBe("1,235");
  });
});

describe("formatDuration", () => {
  it("shows sub-second latencies in milliseconds and the rest in seconds", () => {
    // 0.043s is read as "43 ms" by every operator and as nothing by anyone at all.
    expect(formatDuration(0.043)).toBe("43 ms");
    expect(formatDuration(0.0005)).toBe("0.50 ms");
    expect(formatDuration(0.9994)).toBe("999 ms");
    expect(formatDuration(1)).toBe("1.00 s");
    expect(formatDuration(2.5)).toBe("2.50 s");
  });

  it("renders a genuine zero as a zero, since a zero duration is a real measurement", () => {
    // Distinct from a null metric, which never reaches a formatter — see MetricStat.
    expect(formatDuration(0)).toBe("0.00 ms");
  });
});

describe("formatRate and formatPercentage", () => {
  it("carry their unit on the figure, not only in the label", () => {
    expect(formatRate(0.016666)).toBe("0.02 /s");
    expect(formatRate(0)).toBe("0.00 /s");
    expect(formatRate(1234.5)).toBe("1,234.50 /s");
    expect(formatPercentage(0)).toBe("0.0%");
    expect(formatPercentage(1.46)).toBe("1.5%");
    expect(formatPercentage(100)).toBe("100.0%");
  });
});
