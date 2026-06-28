package io.iverson.client.annotations;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Designates a field as a sort key column. Multiple fields may carry this annotation;
 * {@link #order()} determines their position in the sort key.
 *
 * <p>The server uses sort key columns to build an efficient secondary index (e.g.
 * a StarRocks DUPLICATE KEY or similar) for ORDER BY queries on these columns.</p>
 */
@Target(ElementType.FIELD)
@Retention(RetentionPolicy.RUNTIME)
public @interface IversonSearchKey {
    /**
     * Zero-based position of this field in the compound sort key.
     * Fields with lower {@code order} values appear first.
     */
    int order();
}
