package io.iverson.conformance.models;

import io.iverson.client.annotations.IversonChunk;
import io.iverson.client.annotations.IversonEmbedding;
import io.iverson.client.annotations.IversonEmbeddingModel;
import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;

import java.util.UUID;

/**
 * S11 {@code model-rejected}'s Java fixture ({@code Scenarios/ModelRejectedScenario.cs}). Unlike
 * S1's shared fixtures, each requested language registers its OWN instance of this scenario's
 * type rather than one type shared across all five — the subject is what happens to a type
 * ALREADY registered by THIS client, so five languages sharing one type would leave four of the
 * five columns grading a row a different client registered. Must be named exactly
 * {@code S11ModelJava}: {@code ModelRejectedScenario.TypeNameFor("java")} derives and asserts
 * this name with ordinal comparison.
 *
 * <p>Declares the deployment's default model explicitly ({@code @IversonEmbeddingModel
 * ("nomic-embed-text")}) rather than a second one, on purpose: this exercises the whole
 * declaration path while keeping the conformance environment single-model, so no second model
 * ever needs to be pulled. It also means the harness alone cannot distinguish "the client
 * stamped the declared model" from "the client sent {@code ""} and the server fell back to the
 * same value" — that distinction is pinned by a client-side unit test instead
 * ({@code SchemaRegistrarTest}'s
 * {@code registerAll_stampsDeclaredEmbeddingModel_onEmbeddingAndChunkProperties}).
 */
@IversonEntity
@IversonEmbeddingModel("nomic-embed-text")
public class S11ModelJava {

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
