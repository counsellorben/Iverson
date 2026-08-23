package io.iverson.conformance.models;

import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;

import java.util.UUID;

/**
 * S9 {@code error-contract}'s unregistered fixture: declared by all five drivers and registered by
 * NOTHING — no driver, no scenario, no orchestrator, in this run or any other. A mapped write
 * against it must be refused with {@code FAILED_PRECONDITION}
 * ({@code ObjectMappingGrpcService.RequireSchema}), which is the whole observation.
 *
 * <p>Do not hand this type to any {@code SchemaRegistrar} call. Registering it would destroy the
 * fixture {@code IVC-ERR-005} depends on.
 */
@IversonEntity
public class ErrorUnregisteredDoc {

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
