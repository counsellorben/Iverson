package io.iverson.conformance.models;

import io.iverson.client.annotations.IversonEmbedding;
import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;
import io.iverson.client.annotations.IversonMetadata;

import java.util.UUID;

/**
 * S13 {@code match-pattern}'s subject type. Every one of the five drivers declares the same type
 * name and shape; only the .NET driver ever registers it (register-once rule, as for
 * {@code VectorDoc}), and every driver writes three rows into it and then matches over them.
 *
 * <p>Deliberately relation-free and chunk-free: both requests read TYPE_ROWS. {@code marker}
 * carries the run's {@code --id-prefix} and is {@code @IversonMetadata} exactly as on
 * {@code VectorDoc}; {@code label} is the row's per-language identity and the PARTITION BY column,
 * and its spelling must match {@code MatchPatternScenario.LabelFor}; {@code seq} is the ORDER BY
 * column that {@code DEFINE B AS Seq > PREV(Seq)} compares; {@code title} is the embedding source
 * the similarity request's {@code SIMILARITY(Title, ...)} scores.
 */
@IversonEntity
public class PatternDoc {

    @IversonKey
    private UUID id;

    private String tenantId;

    private String ownerId;

    @IversonMetadata
    private String marker;

    private String label;

    private int seq;

    @IversonEmbedding
    private String title;

    public UUID getId() { return id; }
    public void setId(UUID id) { this.id = id; }

    public String getTenantId() { return tenantId; }
    public void setTenantId(String tenantId) { this.tenantId = tenantId; }

    public String getOwnerId() { return ownerId; }
    public void setOwnerId(String ownerId) { this.ownerId = ownerId; }

    public String getMarker() { return marker; }
    public void setMarker(String marker) { this.marker = marker; }

    public String getLabel() { return label; }
    public void setLabel(String label) { this.label = label; }

    public int getSeq() { return seq; }
    public void setSeq(int seq) { this.seq = seq; }

    public String getTitle() { return title; }
    public void setTitle(String title) { this.title = title; }
}
