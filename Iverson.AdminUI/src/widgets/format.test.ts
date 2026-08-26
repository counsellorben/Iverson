import { describe, it, expect } from "vitest";
import { formatInstant, formatInteger, pluralise } from "./format";

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
