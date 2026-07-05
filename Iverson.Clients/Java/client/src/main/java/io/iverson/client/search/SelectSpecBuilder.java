package io.iverson.client.search;

import iverson.ObjectSearch.SelectItem;

import java.util.ArrayList;
import java.util.List;

/** Builds a joined step's projection: which columns survive the join. */
public final class SelectSpecBuilder {

    private final List<SelectItem> items = new ArrayList<>();

    /** All columns from a source ("base", a step name, or a joined type name). */
    public SelectSpecBuilder allFrom(String source) {
        items.add(SelectItem.newBuilder().setSource(source).setAll(true).build());
        return this;
    }

    /** One column from a source. */
    public SelectSpecBuilder pick(String source, String column) {
        return pick(source, column, null);
    }

    /** One column from a source, renamed. */
    public SelectSpecBuilder pick(String source, String column, String alias) {
        items.add(SelectItem.newBuilder()
            .setSource(source)
            .setColumn(column)
            .setAlias(alias == null ? "" : alias)
            .build());
        return this;
    }

    List<SelectItem> items() {
        return items;
    }
}
