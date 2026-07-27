package io.iverson.client.annotations;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a string field as the target for Ollama-generated keywords during
 * ingest enrichment. The server writes the generated keywords into this field.
 */
@Target(ElementType.FIELD)
@Retention(RetentionPolicy.RUNTIME)
public @interface IversonKeywords {
}
