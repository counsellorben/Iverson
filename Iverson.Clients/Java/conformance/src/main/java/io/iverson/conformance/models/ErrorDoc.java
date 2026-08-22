package io.iverson.conformance.models;

import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;
import io.iverson.client.annotations.IversonTenant;

import java.util.UUID;

/**
 * S9 {@code error-contract}'s subject type. Every one of the five drivers declares the same type
 * name and shape; only the .NET driver ever registers it (register-once rule), and every driver
 * seeds one row into it, reads that row back as a positive control, and then reads a key no row
 * exists under.
 *
 * <p>Deliberately relation-free and search-free: the axis is about what the server's two error
 * shapes look like when they reach a caller, and a relation or a vector field would only add ways
 * for the scenario to go red for reasons that are not about the error contract.
 */
@IversonEntity
public class ErrorDoc {

    @IversonKey
    private UUID id;

    @IversonTenant
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
