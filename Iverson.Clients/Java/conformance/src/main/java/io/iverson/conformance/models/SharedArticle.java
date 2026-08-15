package io.iverson.conformance.models;

import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;
import io.iverson.client.annotations.IversonTenant;
import io.iverson.client.annotations.ManyToOne;

import java.util.UUID;

/**
 * S4 {@code interop}'s root type. Java (like .NET) declares the foreign key as its own field
 * alongside an annotated navigation property; only {@code sharedAuthorId} is ever sent, per the
 * FK-only write contract.
 */
@IversonEntity
public class SharedArticle {

    @IversonKey
    private UUID id;

    @IversonTenant
    private String tenantId;

    private String ownerId;
    private String title;

    /** FK column — convention: {RelatedTypeName}Id. */
    private UUID sharedAuthorId;

    @ManyToOne(type = SharedAuthor.class)
    private SharedAuthor sharedAuthor;

    public UUID getId() { return id; }
    public void setId(UUID id) { this.id = id; }

    public String getTenantId() { return tenantId; }
    public void setTenantId(String tenantId) { this.tenantId = tenantId; }

    public String getOwnerId() { return ownerId; }
    public void setOwnerId(String ownerId) { this.ownerId = ownerId; }

    public String getTitle() { return title; }
    public void setTitle(String title) { this.title = title; }

    public UUID getSharedAuthorId() { return sharedAuthorId; }
    public void setSharedAuthorId(UUID sharedAuthorId) { this.sharedAuthorId = sharedAuthorId; }

    public SharedAuthor getSharedAuthor() { return sharedAuthor; }
    public void setSharedAuthor(SharedAuthor sharedAuthor) { this.sharedAuthor = sharedAuthor; }
}
