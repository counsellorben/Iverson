package io.iverson.client.annotations;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a field that holds large text content (e.g. article body, HTML, JSON).
 * Large fields are excluded from materialized view projections on the server to
 * keep MV size manageable. They are still stored in the primary table.
 */
@Target(ElementType.FIELD)
@Retention(RetentionPolicy.RUNTIME)
public @interface IversonLargeField {
}
