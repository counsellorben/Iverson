/**
 * Locale-independent formatting helpers.
 *
 * `toLocaleString` is deliberately avoided: it would make both the rendered string and every
 * test asserting on it depend on the test runner's locale — the same reasoning that keeps
 * `formatAsOf` hand-rolled in `usePolledResource`.
 */

/** Group a run of digits in threes with commas. `"1234567"` → `"1,234,567"`. */
function groupDigits(digits: string): string {
  return digits.replace(/\B(?=(\d{3})+(?!\d))/g, ",");
}

/** Group an integer in threes with commas. `1234567` → `"1,234,567"`. */
export function formatInteger(value: number): string {
  const negative = value < 0;
  const digits = groupDigits(String(Math.abs(Math.trunc(value))));
  return negative ? `-${digits}` : digits;
}

/**
 * A fixed number of decimal places, with the whole part grouped. `1234.5` at 2 → `"1,234.50"`.
 *
 * `toFixed` is used only for the ROUNDING; the grouping is still hand-rolled, because
 * `toLocaleString` would make the rendered string depend on the runner's locale — and a
 * comma-vs-period swap in a latency figure is not a cosmetic difference.
 */
export function formatDecimal(value: number, places: number): string {
  const negative = value < 0;
  const fixed = Math.abs(value).toFixed(places);
  const dot = fixed.indexOf(".");
  const whole = dot === -1 ? fixed : fixed.slice(0, dot);
  const fraction = dot === -1 ? "" : fixed.slice(dot);
  return `${negative ? "-" : ""}${groupDigits(whole)}${fraction}`;
}

/**
 * A duration given in SECONDS, rendered in the unit an operator reads it in.
 *
 * The metrics endpoint reports both p95 figures in seconds because that is the unit
 * OpenTelemetry declares the histograms in, but a `0.043` on a card is read as "43
 * milliseconds" by nobody. Sub-second values are shown in ms; anything from a second up
 * stays in seconds, where "1.20 s" is clearer than "1,200 ms".
 */
export function formatDuration(seconds: number): string {
  const ms = seconds * 1000;
  if (Math.abs(ms) < 1000) return `${formatDecimal(ms, Math.abs(ms) < 10 ? 2 : 0)} ms`;
  return `${formatDecimal(seconds, 2)} s`;
}

/** A per-second rate. `0.0166…` → `"0.02 /s"`. */
export function formatRate(perSecond: number): string {
  return `${formatDecimal(perSecond, 2)} /s`;
}

/** A percentage that is already expressed as a percentage, not a fraction. `1.5` → `"1.5%"`. */
export function formatPercentage(percent: number): string {
  return `${formatDecimal(percent, 1)}%`;
}

/** `"types"` / `"type"`, for an "N types not shown" line that reads correctly at N = 1. */
export function pluralise(count: number, singular: string, plural = `${singular}s`): string {
  return count === 1 ? singular : plural;
}

/**
 * An ISO-8601 instant as `YYYY-MM-DD HH:MM`, WITH ITS ZONE NAMED.
 *
 * The zone marker is the point. These instants come off the wire in UTC (`TenantSummary`'s
 * `createdAt` is a `timestamptz`), while the card's "Updated {asOf} local time" line is the
 * viewer's own clock. Two clocks on one card with only one of them labelled is how an
 * operator several hours off UTC reads a normal timestamp as a fault.
 *
 * The instant is NOT converted to local time: the rendered string would then depend on the
 * viewer's — and every test runner's — timezone, which is the same reason `formatAsOf` is
 * hand-rolled rather than delegated to `toLocaleTimeString`. Naming the zone is honest and
 * deterministic; converting is neither.
 *
 * A value with no offset at all is left unlabelled, because there is nothing to label it
 * with, and anything not recognisably ISO-8601 is returned untouched rather than mangled.
 */
export function formatInstant(iso: string): string {
  const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})(?::\d{2}(?:\.\d+)?)?(Z|[+-]\d{2}:?\d{2})?$/.exec(
    iso
  );
  if (match === null) return iso;
  const [, date, time, offset] = match;
  if (offset === undefined) return `${date} ${time}`;
  const utc = offset === "Z" || /^[+-]00:?00$/.test(offset);
  return `${date} ${time} ${utc ? "UTC" : offset}`;
}
