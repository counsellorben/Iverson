package io.iverson.sample.models;

import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;
import io.iverson.client.annotations.IversonTenant;

import java.util.UUID;

/**
 * A content tag. Root entity with no upward relations.
 */
@IversonEntity
public class Tag {

    @IversonKey
    private UUID id;

    @IversonTenant
    private String tenantId;

    private String label;

    public Tag() {}

    public Tag(UUID id, String label) {
        this.id    = id;
        this.label = label;
    }

    public UUID getId()          { return id; }
    public void setId(UUID id)   { this.id = id; }

    public String getTenantId()                 { return tenantId; }
    public void   setTenantId(String tenantId)  { this.tenantId = tenantId; }

    public String getLabel()              { return label; }
    public void   setLabel(String label)  { this.label = label; }

    @Override
    public String toString() {
        return "Tag{id=" + id + ", label='" + label + "'}";
    }
}
