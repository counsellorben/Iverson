import { Typography } from "@mui/material";
import { formatInteger, pluralise } from "./format";

/**
 * The "N types not shown" affordance shared by the schema catalog and the data-volume widget.
 *
 * Both endpoints answer with a list plus an aggregate count of the types they REFUSED to name
 * — `withheldTypeCount` for schema, `deniedTypeCount` + `unknownTypeCount` for data volume.
 * That count is the whole reason a short list is not the same claim as a complete one, so it
 * gets a rendered line of its own rather than a caveat buried on the list.
 *
 * Note what this is NOT: it is not a warning about the numbers that ARE shown. A denied type
 * has no entry at all in either response — a false `rowCount: 0` is unconstructible — so
 * every value in the list is real and needs no hedge.
 *
 * Renders nothing at all when the count is zero.
 */
export function HiddenTypesNote({
  count,
  testId,
  explanation,
}: {
  readonly count: number;
  readonly testId: string;
  readonly explanation: string;
}) {
  // `count <= 0` alone is FALSE for `undefined` and for `NaN`, either of which would reach the
  // formatter and render the literal string "NaN types not shown". Every other field these
  // widgets read is handled defensively; a missing `deniedTypeCount` or `withheldTypeCount`
  // gets the same treatment — say nothing rather than say nonsense.
  if (!Number.isFinite(count) || count <= 0) return null;

  return (
    <Typography
      variant="body2"
      sx={{ color: "text.secondary" }}
      data-testid={testId}
      data-hidden-count={count}
    >
      {formatInteger(count)} {pluralise(count, "type")} not shown ({explanation}).
    </Typography>
  );
}
