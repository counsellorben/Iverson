package io.iverson.conformance.models;

import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;
import io.iverson.client.annotations.ManyToMany;
import io.iverson.client.annotations.ManyToOne;
import io.iverson.client.annotations.OneToOne;

import java.util.List;
import java.util.UUID;

/**
 * S1's root type. Java (like .NET) declares each foreign key as its own field alongside an
 * annotated navigation property; the write contract is foreign-key only, so only the
 * {@code javaAuthorId}/{@code javaTagIds} fields are ever sent.
 */
@IversonEntity
public class JavaArticle {

    @IversonKey
    private UUID id;

    private String tenantId;

    private String ownerId;
    private String title;

    /** FK column — convention: {RelatedTypeName}Id. */
    private UUID javaAuthorId;

    @ManyToOne(type = JavaAuthor.class)
    private JavaAuthor javaAuthor;

    /** FK column — convention: {RelatedTypeName}Ids. */
    private List<UUID> javaTagIds;

    @ManyToMany(type = JavaTag.class)
    private List<JavaTag> javaTags;

    /**
     * IVC-REL-001/002/003's one_to_one fixture: a second relation to JavaTag (the many_to_many
     * relation's own related type), through the SINGULAR "javaTagId" foreign key so it does not
     * collide with the many_to_many's plural "javaTagIds" — exercising one_to_one end to end
     * without a whole new entity type.
     */
    private UUID javaTagId;

    @OneToOne(type = JavaTag.class)
    private JavaTag javaTag;

    public UUID getId() { return id; }
    public void setId(UUID id) { this.id = id; }

    public String getTenantId() { return tenantId; }
    public void setTenantId(String tenantId) { this.tenantId = tenantId; }

    public String getOwnerId() { return ownerId; }
    public void setOwnerId(String ownerId) { this.ownerId = ownerId; }

    public String getTitle() { return title; }
    public void setTitle(String title) { this.title = title; }

    public UUID getJavaAuthorId() { return javaAuthorId; }
    public void setJavaAuthorId(UUID javaAuthorId) { this.javaAuthorId = javaAuthorId; }

    public JavaAuthor getJavaAuthor() { return javaAuthor; }
    public void setJavaAuthor(JavaAuthor javaAuthor) { this.javaAuthor = javaAuthor; }

    public List<UUID> getJavaTagIds() { return javaTagIds; }
    public void setJavaTagIds(List<UUID> javaTagIds) { this.javaTagIds = javaTagIds; }

    public List<JavaTag> getJavaTags() { return javaTags; }
    public void setJavaTags(List<JavaTag> javaTags) { this.javaTags = javaTags; }

    public UUID getJavaTagId() { return javaTagId; }
    public void setJavaTagId(UUID javaTagId) { this.javaTagId = javaTagId; }

    public JavaTag getJavaTag() { return javaTag; }
    public void setJavaTag(JavaTag javaTag) { this.javaTag = javaTag; }
}
