package io.iverson.sample.models;

import io.iverson.client.annotations.*;

import java.time.OffsetDateTime;
import java.util.List;
import java.util.UUID;

/**
 * A published article. Demonstrates all annotation types supported by the
 * Iverson Java client.
 */
@IversonEntity
public class Article {

    @IversonKey
    private UUID id;

    private String title;

    @IversonLargeField
    private String body;

    @IversonSearchKey(order = 0)
    private String category;

    private int wordCount;

    @IversonSearchKey(order = 1)
    private OffsetDateTime publishedAt;

    /** FK column — convention: {RelatedTypeName}Id. */
    private UUID authorId;

    @ManyToOne(type = Author.class)
    private Author author;

    @ManyToMany(type = Tag.class)
    private List<Tag> tags;

    // ── Constructors ───────────────────────────────────────────────────────────

    public Article() {}

    public Article(UUID id, String title, String body, String category,
                   int wordCount, OffsetDateTime publishedAt, UUID authorId) {
        this.id          = id;
        this.title       = title;
        this.body        = body;
        this.category    = category;
        this.wordCount   = wordCount;
        this.publishedAt = publishedAt;
        this.authorId    = authorId;
    }

    // ── Getters / setters ──────────────────────────────────────────────────────

    public UUID getId()                         { return id; }
    public void setId(UUID id)                  { this.id = id; }

    public String getTitle()                    { return title; }
    public void   setTitle(String title)        { this.title = title; }

    public String getBody()                     { return body; }
    public void   setBody(String body)          { this.body = body; }

    public String getCategory()                 { return category; }
    public void   setCategory(String category)  { this.category = category; }

    public int    getWordCount()                { return wordCount; }
    public void   setWordCount(int wordCount)   { this.wordCount = wordCount; }

    public OffsetDateTime getPublishedAt()                        { return publishedAt; }
    public void           setPublishedAt(OffsetDateTime publishedAt) { this.publishedAt = publishedAt; }

    public UUID   getAuthorId()                    { return authorId; }
    public void   setAuthorId(UUID authorId)       { this.authorId = authorId; }

    public Author getAuthor()                   { return author; }
    public void   setAuthor(Author author)      { this.author = author; }

    public List<Tag> getTags()                  { return tags; }
    public void      setTags(List<Tag> tags)    { this.tags = tags; }

    @Override
    public String toString() {
        return "Article{id=" + id + ", title='" + title + "', category='" + category + "'}";
    }
}
