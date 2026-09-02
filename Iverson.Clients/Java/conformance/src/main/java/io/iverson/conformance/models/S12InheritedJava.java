package io.iverson.conformance.models;

import io.iverson.client.annotations.IversonChunk;
import io.iverson.client.annotations.IversonEmbedding;
import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;

import java.util.UUID;

/**
 * S12 {@code model-inherited}'s Java fixture ({@code register_inherited_doc} driver step).
 * Declares no {@code @IversonEmbeddingModel} of its own — it inherits
 * {@code @IversonEmbeddingModel("nomic-embed-text")} from its field-less parent
 * {@link S12DeclaredJava}, now that the annotation is {@code @Inherited}. Must be named exactly
 * {@code S12InheritedJava}: T8 derives and asserts this name with ordinal comparison.
 */
@IversonEntity
public class S12InheritedJava extends S12DeclaredJava {

    @IversonKey
    private UUID id;

    private String tenantId;

    private String ownerId;

    @IversonEmbedding
    private String title;

    @IversonChunk
    private String body;

    public UUID getId() { return id; }
    public void setId(UUID id) { this.id = id; }

    public String getTenantId() { return tenantId; }
    public void setTenantId(String tenantId) { this.tenantId = tenantId; }

    public String getOwnerId() { return ownerId; }
    public void setOwnerId(String ownerId) { this.ownerId = ownerId; }

    public String getTitle() { return title; }
    public void setTitle(String title) { this.title = title; }

    public String getBody() { return body; }
    public void setBody(String body) { this.body = body; }
}
