/**
 * Locale-independent formatting helpers.
 *
 * `toLocaleString` is deliberately avoided: it would make both the rendered string and every
 * test asserting on it depend on the test runner's locale — the same reasoning that keeps
 * `formatAsOf` hand-rolled in `usePolledResource`.
 */

/**
 * The word the console uses for a figure that was NOT measured, everywhere on the page.
 *
 * It lives here, beside the formatters, rather than in any one widget's module: it is the
 * counterpart to a formatted number — what gets rendered when there is no number — and both
 * a Band B metric with no current Prometheus sample and a Qdrant collection whose count the
 * server did not report mean exactly the same thing to a reader. Two words for one fact would
 * invite the reader to look for a difference that is not there.
 *
 * It is emphatically not a zero. See `MetricStat`, which is where that promise is enforced.
 */
export const NO_SAMPLE_VALUE = "No data";

/** The caption under an absent figure. Says "not zero" in as many words. */
export const NO_SAMPLE_CAPTION = "No current sample — this is not a zero.";

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
 * Whether a POSITIVE value is too small for `places` decimals to show as anything but zero.
 *
 * This is the formatter-level half of the rule `MetricStat` enforces one layer up: a value
 * that is not zero must never render as the string a zero renders as. `toFixed` alone breaks
 * it — it maps a whole RANGE of nonzero values onto `"0.0"` — and the collapse is invisible
 * precisely where it matters most, because a small nonzero rate is a healthy-looking number
 * and nothing else on the card contradicts it. `data-metric-null` is legitimately `false`
 * here: the value really was sampled, so no absence flag fires.
 *
 * Both live cases are ordinary, not pathological. `errorPercentage` is
 * `(5xx rate / total rate) * 100`, so at 42.5 req/s a single 5xx every hundred seconds is
 * 0.024% — "0.0%" while 5xx responses are actively being served. A DLQ taking fourteen
 * messages an hour is 0.004/s — "0.00 /s" while messages are actively being dropped.
 */
function belowResolution(value: number, places: number): boolean {
  return value > 0 && value < 0.5 * 10 ** -places;
}

/** The smallest magnitude `places` decimals can render. `1` → `"0.1"`, `2` → `"0.01"`. */
function resolution(places: number): string {
  return formatDecimal(10 ** -places, places);
}

/**
 * A duration given in SECONDS, rendered in the unit an operator reads it in.
 *
 * The metrics endpoint reports both p95 figures in seconds because that is the unit
 * OpenTelemetry declares the histograms in, but a `0.043` on a card is read as "43
 * milliseconds" by nobody. Sub-second values are shown in ms; anything from a second up
 * stays in seconds, where "1.20 s" is clearer than "1,200 ms".
 *
 * The rollover is tested against the ROUNDED millisecond figure, not the raw one: at
 * `Math.abs(ms) < 1000` a p95 of 0.9996s rounds to "1,000 ms", the only comma-grouped
 * millisecond reading anywhere in the console, and it means the same thing as "1.00 s".
 */
export function formatDuration(seconds: number): string {
  const ms = seconds * 1000;
  if (Math.abs(ms) < 999.5) {
    if (belowResolution(ms, 2)) return `<${resolution(2)} ms`;
    return `${formatDecimal(ms, Math.abs(ms) < 10 ? 2 : 0)} ms`;
  }
  return `${formatDecimal(seconds, 2)} s`;
}

/**
 * A per-second rate. `0.0166…` → `"0.02 /s"`; a positive rate under 0.005 → `"<0.01 /s"`,
 * which is emphatically not `"0.00 /s"`. See {@link belowResolution}.
 */
export function formatRate(perSecond: number): string {
  if (belowResolution(perSecond, 2)) return `<${resolution(2)} /s`;
  return `${formatDecimal(perSecond, 2)} /s`;
}

/**
 * A percentage that is already expressed as a percentage, not a fraction. `1.5` → `"1.5%"`;
 * a positive percentage under 0.05 → `"<0.1%"`, not `"0.0%"`. See {@link belowResolution}.
 */
export function formatPercentage(percent: number): string {
  if (belowResolution(percent, 1)) return `<${resolution(1)}%`;
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
