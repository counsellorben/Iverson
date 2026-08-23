package io.iverson.conformance.models;

import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;

import java.util.UUID;

/**
 * S8 {@code identity}'s subject type. Every one of the five drivers declares the same type name and
 * shape; only the .NET driver ever registers it (register-once rule), and every driver writes one
 * row into it, reads that row back, and then attempts one update under a deliberately wrong acting
 * user.
 *
 * <p>Deliberately relation-free and search-free: the axis is about WHOSE identity the server
 * resolves a row's tenant and owner from, and a relation or a vector field would only add ways for
 * the scenario to go red for reasons that are not about identity.
 */
@IversonEntity
public class IdentityDoc {

    @IversonKey
    private UUID id;

    /**
     * This property is NOT the row's tenant and has not been since the server took ownership of the
     * boundary: the real tenant lives in the server-owned __TenantId column, which is injected
     * server-side, never declared by a client and stripped from every outbound path. It survives
     * here as a NEGATIVE CONTROL, and it is the write phase's IdentityWrongTenant value that makes
     * it one: IVC-IDN-003's orchestrator-side probe asserts that a user column literally named
     * TenantId, carrying a tenant-shaped value the client chose, is still holding that value in
     * Postgres and did NOT leak into __TenantId. Delete this property and that assertion goes red —
     * which is the point. (Before Task 5 the stated reason was 'IVC-IDN-003 grades the read-back';
     * that assertion no longer exists, because reading this column back only ever graded an echo.)
     */
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
