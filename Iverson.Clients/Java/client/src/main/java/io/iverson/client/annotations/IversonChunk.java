package io.iverson.client.annotations;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a string field as a source for chunk-level vector embeddings. The server
 * splits the field value into overlapping windows and stores each chunk as a
 * separate Qdrant point in a {collection}_chunks collection, enabling
 * passage-level RAG retrieval via {@code SearchChunks}.
 */
@Target(ElementType.FIELD)
@Retention(RetentionPolicy.RUNTIME)
public @interface IversonChunk {
    /** Approximate window size in tokens (1 token ~ 4 chars). Default 512. */
    int maxTokens() default 512;

    /** Tokens shared between adjacent windows. Default 64. */
    int overlap() default 64;
}
