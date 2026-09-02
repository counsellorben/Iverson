package io.iverson.client.annotations;

import java.lang.annotation.ElementType;
import java.lang.annotation.Inherited;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Declares the embedding model for every embedded and chunked property of this type. Optional;
 * omission means the deployment's default model, unless a superclass declares one — in which
 * case the nearest declaring ancestor's model is inherited; a subclass's own declaration
 * overrides. Class-level, not per-property: one model per type is what keeps a query from fusing
 * across two incompatible vector spaces.
 */
@Target(ElementType.TYPE)
@Retention(RetentionPolicy.RUNTIME)
@Inherited
public @interface IversonEmbeddingModel {
    /** The embedding model id, e.g. {@code "nomic-embed-text"}. */
    String value();
}
