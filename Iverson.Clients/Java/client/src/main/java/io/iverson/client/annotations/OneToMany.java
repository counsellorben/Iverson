package io.iverson.client.annotations;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Declares a one-to-many relationship. The annotated field holds a collection
 * of related entities, each of which carries a FK back to this entity
 * (named {@code {ThisType}Id}).
 */
@Target(ElementType.FIELD)
@Retention(RetentionPolicy.RUNTIME)
public @interface OneToMany {
    /** The related entity type. */
    Class<?> type();
}
