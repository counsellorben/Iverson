package io.iverson.client.search;

import iverson.ObjectSearch.SearchValue;

import java.time.LocalDateTime;
import java.time.OffsetDateTime;
import java.time.format.DateTimeFormatter;

/**
 * Shared value-encoding logic for the search DSL. Converts a plain Java object
 * (String, Number, Boolean, date/time types) into the {@link SearchValue} oneof
 * expected by the proto. Used by both {@link QueryBuilder.FieldCondition} and
 * {@link GroupByBuilder} so the encoding rules live in exactly one place.
 */
final class SearchValues {

    private SearchValues() {}

    static SearchValue toSearchValue(Object value) {
        if (value == null)
            return SearchValue.newBuilder().build();
        if (value instanceof String s)
            return SearchValue.newBuilder().setStringVal(s).build();
        if (value instanceof Boolean b)
            return SearchValue.newBuilder().setBoolVal(b).build();
        if (value instanceof Number n)
            return SearchValue.newBuilder().setNumberVal(n.doubleValue()).build();
        if (value instanceof OffsetDateTime dt)
            return SearchValue.newBuilder()
                .setStringVal(dt.format(DateTimeFormatter.ISO_OFFSET_DATE_TIME))
                .build();
        if (value instanceof LocalDateTime ldt)
            return SearchValue.newBuilder()
                .setStringVal(ldt.format(DateTimeFormatter.ISO_LOCAL_DATE_TIME))
                .build();
        // Fallback
        return SearchValue.newBuilder().setStringVal(value.toString()).build();
    }
}
