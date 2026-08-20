package io.iverson.conformance.models;

import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;
import io.iverson.client.annotations.IversonTenant;

import java.util.UUID;

/**
 * S6 {@code query}'s subject type. Every one of the five drivers declares the same type name and
 * shape; only the .NET driver ever registers it (register-once rule), and every driver writes one
 * row into it and then queries it.
 *
 * <p>Deliberately relation-free: the scenario's exact result-set comparison is over row keys, and a
 * relation would drag hydration into what a search returns without adding anything the QRY axis
 * asserts. {@code marker} carries the run's {@code --id-prefix} and is the property every driver
 * filters on — unique per run, so the expected result set is exactly this run's rows.
 */
@IversonEntity
public class QueryDoc {

    @IversonKey
    private UUID id;

    @IversonTenant
    private String tenantId;

    private String ownerId;
    private String marker;
    private String label;

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
}
