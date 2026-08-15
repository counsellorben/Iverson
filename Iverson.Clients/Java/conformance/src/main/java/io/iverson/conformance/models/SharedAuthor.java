package io.iverson.conformance.models;

import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;
import io.iverson.client.annotations.IversonTenant;

import java.util.UUID;

/**
 * S4 {@code interop}'s "one" side. Every one of the five drivers declares the same type name and
 * shape; only the .NET driver ever registers it (register-once rule), so this driver's own
 * {@code SchemaRegistrar} is never invoked for it.
 */
@IversonEntity
public class SharedAuthor {

    @IversonKey
    private UUID id;

    @IversonTenant
    private String tenantId;

    private String ownerId;
    private String name;

    public UUID getId() { return id; }
    public void setId(UUID id) { this.id = id; }

    public String getTenantId() { return tenantId; }
    public void setTenantId(String tenantId) { this.tenantId = tenantId; }

    public String getOwnerId() { return ownerId; }
    public void setOwnerId(String ownerId) { this.ownerId = ownerId; }

    public String getName() { return name; }
    public void setName(String name) { this.name = name; }
}
