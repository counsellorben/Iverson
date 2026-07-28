package io.iverson.client.annotations;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks the field that holds the entity's tenant boundary.
 *
 * <p>Exactly one field per entity must carry this marker. The server requires
 * every registered schema to declare a tenant field and will reject
 * registration without one.</p>
 */
@Target(ElementType.FIELD)
@Retention(RetentionPolicy.RUNTIME)
public @interface IversonTenant {
}
