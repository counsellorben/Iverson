package io.iverson.client.annotations;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a class as an Iverson-managed entity. Classes annotated with
 * {@code @IversonEntity} will be discovered by {@link io.iverson.client.core.SchemaRegistrar}
 * and have their schema registered with the server.
 */
@Target(ElementType.TYPE)
@Retention(RetentionPolicy.RUNTIME)
public @interface IversonEntity {
}
