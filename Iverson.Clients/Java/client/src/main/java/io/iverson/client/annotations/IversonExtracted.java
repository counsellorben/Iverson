package io.iverson.client.annotations;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a field as the target for an Ollama-driven extraction during ingest
 * enrichment, guided by {@link #value()}. The hint is mandatory: the server
 * only treats a property as an extraction target when a non-empty hint is
 * present, so this annotation must always specify one.
 */
@Target(ElementType.FIELD)
@Retention(RetentionPolicy.RUNTIME)
public @interface IversonExtracted {
    /** The extraction hint guiding the Ollama prompt. Required; must not be blank. */
    String value();
}
