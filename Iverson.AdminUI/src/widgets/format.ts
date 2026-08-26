/**
 * Locale-independent formatting helpers.
 *
 * `toLocaleString` is deliberately avoided: it would make both the rendered string and every
 * test asserting on it depend on the test runner's locale — the same reasoning that keeps
 * `formatAsOf` hand-rolled in `usePolledResource`.
 */

/** Group an integer in threes with commas. `1234567` → `"1,234,567"`. */
export function formatInteger(value: number): string {
  const negative = value < 0;
  const digits = String(Math.abs(Math.trunc(value))).replace(
    /\B(?=(\d{3})+(?!\d))/g,
    ","
  );
  return negative ? `-${digits}` : digits;
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
