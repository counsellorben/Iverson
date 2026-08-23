package io.iverson.conformance.models;

import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;
import io.iverson.client.annotations.OneToMany;

import java.util.List;
import java.util.UUID;

/**
 * S1's "one" side. Carries the reverse {@link OneToMany} navigation the foreign-key-only write
 * contract work broke, so the harness observes it end to end.
 */
@IversonEntity
public class JavaAuthor {

    @IversonKey
    private UUID id;

    private String tenantId;

    private String ownerId;
    private String name;

    @OneToMany(type = JavaArticle.class)
    private List<JavaArticle> javaArticles;

    public UUID getId() { return id; }
    public void setId(UUID id) { this.id = id; }

    public String getTenantId() { return tenantId; }
    public void setTenantId(String tenantId) { this.tenantId = tenantId; }

    public String getOwnerId() { return ownerId; }
    public void setOwnerId(String ownerId) { this.ownerId = ownerId; }

    public String getName() { return name; }
    public void setName(String name) { this.name = name; }

    public List<JavaArticle> getJavaArticles() { return javaArticles; }
    public void setJavaArticles(List<JavaArticle> javaArticles) { this.javaArticles = javaArticles; }
}
