package io.iverson.conformance.models;

import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;

import java.util.UUID;

/** S1's many-to-many peer. */
@IversonEntity
public class JavaTag {

    @IversonKey
    private UUID id;

    private String tenantId;

    private String ownerId;
    private String label;

    public UUID getId() { return id; }
    public void setId(UUID id) { this.id = id; }

    public String getTenantId() { return tenantId; }
    public void setTenantId(String tenantId) { this.tenantId = tenantId; }

    public String getOwnerId() { return ownerId; }
    public void setOwnerId(String ownerId) { this.ownerId = ownerId; }

    public String getLabel() { return label; }
    public void setLabel(String label) { this.label = label; }
}
