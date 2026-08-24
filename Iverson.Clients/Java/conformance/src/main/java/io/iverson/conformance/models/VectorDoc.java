package io.iverson.conformance.models;

import io.iverson.client.annotations.IversonChunk;
import io.iverson.client.annotations.IversonEmbedding;
import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;
import io.iverson.client.annotations.IversonMetadata;

import java.util.UUID;

/**
 * S7 {@code vector-search}'s subject type. Every one of the five drivers declares the same type
 * name and shape; only the .NET driver ever registers it (register-once rule), and every driver
 * writes one row into it and then searches it.
 *
 * <p>Deliberately relation-free, and deliberately without any enrichment annotation (summary,
 * keywords, contextual chunking): the scenario's exact set comparisons must not depend on
 * generative output that differs run to run.
 *
 * <p>{@code marker} carries the run's {@code --id-prefix} and is the property both queries filter
 * on. It is {@code @IversonMetadata} so that one value scopes BOTH stores: the object collection
 * filters it as an ordinary scalar payload clause, and the chunks collection can filter it only
 * because metadata columns are denormalized onto every chunk point. {@code title} is the embedding
 * source {@code SearchSimilar} searches; {@code body} is the chunk source {@code SearchChunks}
 * searches, short enough to produce a single window per row. {@code label} is the row's
 * per-language identity — {@code SearchSimilar} streams the Qdrant payload, whose row key lives
 * under a reserved {@code key} entry no typed projection binds to {@code id} — and its spelling
 * must match {@code VectorSearchScenario.LabelFor}.
 */
@IversonEntity
public class VectorDoc {

    @IversonKey
    private UUID id;

    private String tenantId;

    private String ownerId;

    @IversonMetadata
    private String marker;

    @IversonEmbedding
    private String title;

    @IversonChunk(maxTokens = 256, overlap = 32)
    private String body;

    private String label;

    public UUID getId() { return id; }
    public void setId(UUID id) { this.id = id; }

    public String getTenantId() { return tenantId; }
    public void setTenantId(String tenantId) { this.tenantId = tenantId; }

    public String getOwnerId() { return ownerId; }
    public void setOwnerId(String ownerId) { this.ownerId = ownerId; }

    public String getMarker() { return marker; }
    public void setMarker(String marker) { this.marker = marker; }

    public String getTitle() { return title; }
    public void setTitle(String title) { this.title = title; }

    public String getBody() { return body; }
    public void setBody(String body) { this.body = body; }

    public String getLabel() { return label; }
    public void setLabel(String label) { this.label = label; }
}
