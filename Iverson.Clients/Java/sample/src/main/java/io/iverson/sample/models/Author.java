package io.iverson.sample.models;

import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;

import java.util.UUID;

/**
 * A content author. Root entity with no relations pointing upward.
 */
@IversonEntity
public class Author {

    @IversonKey
    private UUID id;

    private String name;
    private String email;

    public Author() {}

    public Author(UUID id, String name, String email) {
        this.id    = id;
        this.name  = name;
        this.email = email;
    }

    public UUID getId()          { return id; }
    public void setId(UUID id)   { this.id = id; }

    public String getName()             { return name; }
    public void   setName(String name)  { this.name = name; }

    public String getEmail()              { return email; }
    public void   setEmail(String email)  { this.email = email; }

    @Override
    public String toString() {
        return "Author{id=" + id + ", name='" + name + "'}";
    }
}
