package io.iverson.client.annotations;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks the primary key field of an {@link IversonEntity}. Exactly one field
 * per entity class must carry this annotation. The field name is used (converted
 * to PascalCase) as the key column name in the server schema.
 */
@Target(ElementType.FIELD)
@Retention(RetentionPolicy.RUNTIME)
public @interface IversonKey {
}
