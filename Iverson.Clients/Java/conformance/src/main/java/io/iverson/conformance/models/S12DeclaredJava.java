package io.iverson.conformance.models;

import io.iverson.client.annotations.IversonEmbeddingModel;

/**
 * S12 {@code model-inherited}'s Java declaring parent. Field-less and never registered (no
 * {@code @IversonEntity}) — it exists only to carry {@code @IversonEmbeddingModel
 * ("nomic-embed-text")} for {@link S12InheritedJava} to inherit, now that the annotation is
 * {@code @Inherited}.
 */
@IversonEmbeddingModel("nomic-embed-text")
public class S12DeclaredJava {
}
