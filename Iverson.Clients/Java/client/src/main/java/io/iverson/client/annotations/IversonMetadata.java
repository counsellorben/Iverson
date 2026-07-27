package io.iverson.client.annotations;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a field as a metadata signal — a property whose value describes or
 * qualifies the entity rather than carrying its primary content.
 *
 * <p>The server records this on the registered schema so downstream consumers
 * can distinguish metadata columns from content columns.</p>
 */
@Target(ElementType.FIELD)
@Retention(RetentionPolicy.RUNTIME)
public @interface IversonMetadata {
}
