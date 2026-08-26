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
 * An ISO-8601 instant as `YYYY-MM-DD HH:MM`. Anything that is not recognisably ISO-8601 is
 * returned untouched rather than mangled — the server serializes `DateTime`, but a widget
 * should not blank a row because a field arrived in a shape it did not expect.
 */
export function formatInstant(iso: string): string {
  const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})/.exec(iso);
  return match === null ? iso : `${match[1]} ${match[2]}`;
}
