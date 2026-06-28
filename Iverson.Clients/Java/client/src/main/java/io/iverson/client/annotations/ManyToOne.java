package io.iverson.client.annotations;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Declares a many-to-one relationship. The annotated field holds a reference
 * to the related entity. A corresponding FK field (named {@code {RelatedType}Id})
 * must exist on this class.
 */
@Target(ElementType.FIELD)
@Retention(RetentionPolicy.RUNTIME)
public @interface ManyToOne {
    /** The related entity type. */
    Class<?> type();
}
