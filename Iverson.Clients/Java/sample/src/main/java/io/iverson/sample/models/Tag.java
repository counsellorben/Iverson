package io.iverson.sample.models;

import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;

import java.util.UUID;

/**
 * A content tag. Root entity with no upward relations.
 */
@IversonEntity
public class Tag {

    @IversonKey
    private UUID id;

    private String label;

    public Tag() {}

    public Tag(UUID id, String label) {
        this.id    = id;
        this.label = label;
    }

    public UUID getId()          { return id; }
    public void setId(UUID id)   { this.id = id; }

    public String getLabel()              { return label; }
    public void   setLabel(String label)  { this.label = label; }

    @Override
    public String toString() {
        return "Tag{id=" + id + ", label='" + label + "'}";
    }
}
