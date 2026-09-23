import { Typography } from "@mui/material";
import { formatInteger, pluralise } from "./format";

/**
 * The "N not shown" affordance shared by the schema catalog, the data-volume widget and the
 * Qdrant collections widget.
 *
 * Each of those endpoints answers with a list plus an aggregate count of the entries missing
 * from it — `withheldTypeCount` for schema, `deniedTypeCount` + `unknownTypeCount` for data
 * volume, `unreadableCollectionCount` for Qdrant. That count is the whole reason a short list
 * is not the same claim as a complete one, so it gets a rendered line of its own rather than a
 * caveat buried on the list.
 *
 * Note what this is NOT: it is not a warning about the numbers that ARE shown. A missing entry
 * has no row at all in any of the three responses — a false `rowCount: 0` or `pointsCount: 0`
 * is unconstructible — so every value in the list is real and needs no hedge.
 *
 * `noun` defaults to `"type"` because two of the three callers count types; the Qdrant widget
 * passes `"collection"`. It is a parameter rather than a second near-identical component
 * precisely so all three lines keep one wording and one test id convention.
 *
 * Renders nothing at all when the count is zero.
 */
export function HiddenTypesNote({
  count,
  testId,
  explanation,
  noun = "type",
}: {
  readonly count: number;
  readonly testId: string;
  readonly explanation: string;
  readonly noun?: string;
}) {
  // `count <= 0` alone is FALSE for `undefined` and for `NaN`, either of which would reach the
  // formatter and render the literal string "NaN types not shown". Every other field these
  // widgets read is handled defensively; a missing `deniedTypeCount`, `withheldTypeCount` or
  // `unreadableCollectionCount` gets the same treatment — say nothing rather than say nonsense.
  if (!Number.isFinite(count) || count <= 0) return null;

  return (
    <Typography
      variant="body2"
      sx={{ color: "text.secondary" }}
      data-testid={testId}
      data-hidden-count={count}
    >
      {formatInteger(count)} {pluralise(count, noun)} not shown ({explanation}).
    </Typography>
  );
}
