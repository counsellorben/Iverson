import { List, ListItem, ListItemText, Typography } from "@mui/material";
import { fetchSchema } from "../api/console";
import type { SchemaCatalogResponse } from "../api/types";
import { usePolledResource } from "../hooks/usePolledResource";
import { HiddenTypesNote } from "./HiddenTypesNote";
import { formatInteger, pluralise } from "./format";
import { WidgetCard } from "./WidgetCard";

/**
 * The registered object types this session may see, from `/admin/console/schema`.
 *
 * `withheldTypeCount` is rendered, never dropped. A zero-length `types` array with a non-zero
 * `withheldTypeCount` does not mean "no types are registered" — it means "you may see none of
 * the N that are", which is a completely different thing to tell an operator.
 *
 * On mount and manual refresh only; the type registry does not move at poll timescales.
 */
export function SchemaCatalogWidget({ accessToken }: { accessToken: string | undefined }) {
  const resource = usePolledResource(fetchSchema, null, accessToken);

  return (
    <WidgetCard
      title="Schema"
      subtitle="Fetched on load and on refresh; never polled."
      testId="widget-schema"
      resource={resource}
    >
      {(data: SchemaCatalogResponse) => (
        <>
          <Typography variant="body2" data-testid="schema-type-count" data-count={data.typeCount}>
            {formatInteger(data.typeCount)} {pluralise(data.typeCount, "type")} visible
          </Typography>
          <HiddenTypesNote
            count={data.withheldTypeCount}
            testId="schema-hidden-types"
            explanation="withheld by your permissions"
          />
          {data.types.length > 0 && (
            <List dense aria-label="Object types">
              {data.types.map((type) => (
                <ListItem key={type.name} disableGutters data-testid={`schema-type-${type.name}`}>
                  <ListItemText
                    primary={type.name}
                    secondary={`${formatInteger(type.fieldCount)} ${pluralise(
                      type.fieldCount,
                      "field"
                    )}, ${formatInteger(type.relations.length)} ${pluralise(
                      type.relations.length,
                      "relation"
                    )}`}
                  />
                </ListItem>
              ))}
            </List>
          )}
        </>
      )}
    </WidgetCard>
  );
}
