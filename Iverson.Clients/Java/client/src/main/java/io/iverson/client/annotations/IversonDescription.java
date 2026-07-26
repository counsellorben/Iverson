package io.iverson.client.annotations;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Supplies a human-readable description for an entity type or one of its fields.
 *
 * <p>Applied to a class the description is registered on the type; applied to a
 * field — including the {@link IversonKey} field — it is registered on that
 * property.</p>
 */
@Target({ElementType.TYPE, ElementType.FIELD})
@Retention(RetentionPolicy.RUNTIME)
public @interface IversonDescription {
    /** The description text. */
    String value();
}
