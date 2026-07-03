package io.iverson.client.annotations;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a string field as a source for a whole-field vector embedding. The server
 * embeds the field value at ingestion time and stores it in the entity's Qdrant
 * collection, enabling {@code SearchSimilar}.
 */
@Target(ElementType.FIELD)
@Retention(RetentionPolicy.RUNTIME)
public @interface IversonEmbedding {
}
