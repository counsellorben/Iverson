package io.iverson.client.annotations;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Declares a many-to-many relationship. The annotated field holds a collection
 * of related entities. A corresponding FK array field (named {@code {RelatedType}Ids})
 * should exist on this class.
 */
@Target(ElementType.FIELD)
@Retention(RetentionPolicy.RUNTIME)
public @interface ManyToMany {
    /** The related entity type. */
    Class<?> type();
}
