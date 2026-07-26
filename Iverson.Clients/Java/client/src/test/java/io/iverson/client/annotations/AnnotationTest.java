package io.iverson.client.annotations;

import org.junit.jupiter.api.Test;

import java.lang.reflect.Field;
import java.util.List;
import java.util.UUID;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Verifies that all Iverson annotations are retained at runtime and can be
 * read via reflection.
 */
class AnnotationTest {

    // ── Fixture entity ────────────────────────────────────────────────────────

    @IversonEntity
    @IversonDescription("A blog post")
    static class SamplePost {
        @IversonKey
        @IversonDescription("Primary identifier")
        private UUID id;

        @IversonMetadata
        private String source;

        @IversonDescription("Author display name")
        private String authorName;

        @IversonSearchKey(order = 0)
        private String category;

        @IversonSearchKey(order = 1)
        private String slug;

        @IversonLargeField
        private String body;

        @ManyToOne(type = SampleAuthor.class)
        private SampleAuthor author;

        @ManyToMany(type = SampleTag.class)
        private List<SampleTag> tags;

        @OneToMany(type = SampleComment.class)
        private List<SampleComment> comments;

        @OneToOne(type = SampleProfile.class)
        private SampleProfile profile;
    }

    @IversonEntity
    static class SampleAuthor  { @IversonKey private UUID id; }
    @IversonEntity
    static class SampleTag     { @IversonKey private UUID id; }
    @IversonEntity
    static class SampleComment { @IversonKey private UUID id; }
    @IversonEntity
    static class SampleProfile { @IversonKey private UUID id; }

    // ── @IversonEntity ────────────────────────────────────────────────────────

    @Test
    void iversonEntity_isPresentAtRuntime() {
        assertNotNull(SamplePost.class.getAnnotation(IversonEntity.class),
            "@IversonEntity must be retained at runtime");
    }

    // ── @IversonKey ───────────────────────────────────────────────────────────

    @Test
    void iversonKey_isPresentAtRuntime() throws NoSuchFieldException {
        Field id = SamplePost.class.getDeclaredField("id");
        assertNotNull(id.getAnnotation(IversonKey.class),
            "@IversonKey must be retained at runtime");
    }

    // ── @IversonSearchKey ─────────────────────────────────────────────────────

    @Test
    void iversonSearchKey_isPresentAtRuntime_withCorrectOrder() throws NoSuchFieldException {
        Field category = SamplePost.class.getDeclaredField("category");
        IversonSearchKey sk = category.getAnnotation(IversonSearchKey.class);
        assertNotNull(sk, "@IversonSearchKey must be retained at runtime");
        assertEquals(0, sk.order(), "order() must be 0 for category");

        Field slug = SamplePost.class.getDeclaredField("slug");
        IversonSearchKey sk2 = slug.getAnnotation(IversonSearchKey.class);
        assertNotNull(sk2);
        assertEquals(1, sk2.order(), "order() must be 1 for slug");
    }

    // ── @IversonLargeField ────────────────────────────────────────────────────

    @Test
    void iversonLargeField_isPresentAtRuntime() throws NoSuchFieldException {
        Field body = SamplePost.class.getDeclaredField("body");
        assertNotNull(body.getAnnotation(IversonLargeField.class),
            "@IversonLargeField must be retained at runtime");
    }

    // ── @IversonMetadata ──────────────────────────────────────────────────────

    @Test
    void iversonMetadata_isPresentAtRuntime() throws NoSuchFieldException {
        Field source = SamplePost.class.getDeclaredField("source");
        assertNotNull(source.getAnnotation(IversonMetadata.class),
            "@IversonMetadata must be retained at runtime");
    }

    // ── @IversonDescription ───────────────────────────────────────────────────

    @Test
    void iversonDescription_isPresentAtRuntime_onType() {
        IversonDescription d = SamplePost.class.getAnnotation(IversonDescription.class);
        assertNotNull(d, "@IversonDescription must be retained at runtime on a type");
        assertEquals("A blog post", d.value());
    }

    @Test
    void iversonDescription_isPresentAtRuntime_onField() throws NoSuchFieldException {
        Field authorName = SamplePost.class.getDeclaredField("authorName");
        IversonDescription d = authorName.getAnnotation(IversonDescription.class);
        assertNotNull(d, "@IversonDescription must be retained at runtime on a field");
        assertEquals("Author display name", d.value());
    }

    @Test
    void iversonDescription_isPresentAtRuntime_onKeyField() throws NoSuchFieldException {
        Field id = SamplePost.class.getDeclaredField("id");
        IversonDescription d = id.getAnnotation(IversonDescription.class);
        assertNotNull(d, "@IversonDescription must be readable on the key field");
        assertEquals("Primary identifier", d.value());
    }

    // ── Relation annotations ──────────────────────────────────────────────────

    @Test
    void manyToOne_isPresentAtRuntime_withCorrectType() throws NoSuchFieldException {
        Field author = SamplePost.class.getDeclaredField("author");
        ManyToOne mto = author.getAnnotation(ManyToOne.class);
        assertNotNull(mto, "@ManyToOne must be retained at runtime");
        assertEquals(SampleAuthor.class, mto.type());
    }

    @Test
    void manyToMany_isPresentAtRuntime_withCorrectType() throws NoSuchFieldException {
        Field tags = SamplePost.class.getDeclaredField("tags");
        ManyToMany mtm = tags.getAnnotation(ManyToMany.class);
        assertNotNull(mtm, "@ManyToMany must be retained at runtime");
        assertEquals(SampleTag.class, mtm.type());
    }

    @Test
    void oneToMany_isPresentAtRuntime_withCorrectType() throws NoSuchFieldException {
        Field comments = SamplePost.class.getDeclaredField("comments");
        OneToMany otm = comments.getAnnotation(OneToMany.class);
        assertNotNull(otm, "@OneToMany must be retained at runtime");
        assertEquals(SampleComment.class, otm.type());
    }

    @Test
    void oneToOne_isPresentAtRuntime_withCorrectType() throws NoSuchFieldException {
        Field profile = SamplePost.class.getDeclaredField("profile");
        OneToOne oto = profile.getAnnotation(OneToOne.class);
        assertNotNull(oto, "@OneToOne must be retained at runtime");
        assertEquals(SampleProfile.class, oto.type());
    }

    // ── Missing annotation ────────────────────────────────────────────────────

    @Test
    void annotationsAreAbsent_onUnmarkedField() throws NoSuchFieldException {
        Field body = SamplePost.class.getDeclaredField("body");
        // body has @IversonLargeField but NOT @IversonKey
        assertNull(body.getAnnotation(IversonKey.class));
        assertNull(body.getAnnotation(IversonSearchKey.class));
    }
}
